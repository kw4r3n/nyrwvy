#!/usr/bin/env node
// Stands in for the F# session host in the Zig host parity tests (docs/decisions/zig-session-host.md,
// "The switch", step 4). `record` runs the real F# host behind a proxy and writes what it did to a run
// file; `replay` answers the same requests from that file, so the tests need no F# host.
//
//   node host-golden.mjs record|replay <run file> <env JSON> -- <host arguments>
//
// The env JSON is the test's per-process overrides (they are already in this process's environment);
// it is part of the run's identity. Record also needs NYRWVY_FSHARP_HOST_DLL.
//
// A run file keeps the normalized arguments and overrides, a hash per request line, every stdout line
// with the number of request lines received before the host wrote it, and the files the host changed
// under the temporary directories named in its arguments (contents in blobs/, by SHA-256). Exit code
// and stderr come last. Temporary directories and the repository root are stored as <tmpN> and <repo>.
import { spawn } from "node:child_process";
import { createHash } from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import zlib from "node:zlib";

const [mode, runFile, envJson, separator, ...hostArgs] = process.argv.slice(2);
if (!["record", "replay"].includes(mode) || separator !== "--") {
  process.stderr.write("usage: host-golden.mjs record|replay <run file> <env JSON> -- <host arguments>\n");
  process.exit(64);
}
const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const blobs = path.join(path.dirname(path.dirname(runFile)), "blobs");
const env = JSON.parse(envJson);
const tmp = os.tmpdir().replace(/\/+$/, "");
const tokenPattern = new RegExp(`${tmp.replace(/[.*+?^${}()|[\]\\/]/g, "\\$&")}/[^/"\\\\\\s\\u0000]+`, "g");

// Temporary directories in order of first appearance, so record and replay number them alike.
const tokens = [];
const discover = (text) => {
  for (const match of text.matchAll(tokenPattern)) if (!tokens.includes(match[0])) tokens.push(match[0]);
};
// Only arguments, overrides and requests name new directories, so output never shifts the numbering.
const normalize = (text, learn = false) => {
  if (learn) discover(text);
  let result = text.split(repo).join("<repo>");
  tokens.forEach((token, index) => { result = result.split(token).join(`<tmp${index}>`); });
  return result;
};
const denormalize = (text) => {
  let result = text;
  tokens.forEach((token, index) => { result = result.split(`<tmp${index}>`).join(token); });
  return result.split("<repo>").join(repo);
};
// Brotli: the replies repeat themselves heavily (about 20 times smaller); `zlib.brotliDecompressSync` reads one.
const pack = (data) => zlib.brotliCompressSync(data, { params: { [zlib.constants.BROTLI_PARAM_QUALITY]: 9, [zlib.constants.BROTLI_PARAM_LGWIN]: 24 } });
const unpack = (file) => zlib.brotliDecompressSync(fs.readFileSync(file));
const sha = (data) => createHash("sha256").update(data).digest("hex");
const identity = () => ({ args: hostArgs.map((arg) => normalize(arg, true)), env: Object.keys(env).sort().map((name) => [name, normalize(env[name], true)]) });
// The directory of each temporary path argument (itself if it is a directory): the host writes only
// beside its snapshot, and a whole test directory can hold hundreds of other hosts' copies.
const watched = () => [...new Set(hostArgs.filter((arg) => tokens.some((token) => arg.startsWith(token)))
  .map((arg) => (fs.existsSync(arg) && fs.statSync(arg).isDirectory() ? arg : path.dirname(arg))))]
  .filter((dir) => fs.existsSync(dir) && dir.startsWith(tmp + "/"));

// Every file under the watched directories: relative path (with its token) -> [size, mtime].
const scan = () => {
  const files = new Map();
  // Temporaries (`*.tmp`) live only while the host writes, so whether a scan sees one is timing; skip them.
  const walk = (dir) => {
    let entries;
    try { entries = fs.readdirSync(dir, { withFileTypes: true }); } catch { return; /* removed meanwhile */ }
    for (const entry of entries) {
      const full = path.join(dir, entry.name);
      if (entry.isDirectory()) { files.set(full + "/", null); walk(full); }
      else if (entry.isFile() && !entry.name.endsWith(".tmp")) {
        const stat = fs.statSync(full, { bigint: true, throwIfNoEntry: false });
        if (stat) files.set(full, `${stat.size}:${stat.mtimeNs}`);
      }
    }
  };
  for (const dir of watched()) walk(dir);
  return files;
};

// Text survives as text with its paths normalized; other bytes are stored as they are.
const storeBlob = (bytes) => {
  const text = bytes.toString("utf8");
  const stored = Buffer.from(text, "utf8").equals(bytes) ? Buffer.from(normalize(text), "utf8") : bytes;
  const name = sha(stored);
  fs.mkdirSync(blobs, { recursive: true });
  const file = path.join(blobs, `${name}.br`);
  if (!fs.existsSync(file)) fs.writeFileSync(file, pack(stored));
  return name;
};
const loadBlob = (name) => {
  const bytes = unpack(path.join(blobs, `${name}.br`));
  const text = bytes.toString("utf8");
  return Buffer.from(text, "utf8").equals(bytes) ? Buffer.from(denormalize(text), "utf8") : bytes;
};

const splitLines = (onLine) => {
  let pending = Buffer.alloc(0);
  return {
    push(chunk) {
      pending = Buffer.concat([pending, chunk]);
      let at;
      while ((at = pending.indexOf(10)) >= 0) { onLine(pending.subarray(0, at + 1)); pending = pending.subarray(at + 1); }
    },
    end() { if (pending.length > 0) onLine(pending); pending = Buffer.alloc(0); },
  };
};
// Raw bytes as latin1, so any request bytes hash and normalize the same way in both modes.
const requestHash = (line) => sha(normalize(line.toString("latin1"), true)).slice(0, 24);

if (mode === "record") await record();
else await replay();

async function record() {
  const dll = process.env.NYRWVY_FSHARP_HOST_DLL;
  const head = identity();
  let before = scan();
  const changes = () => {
    const after = scan();
    const changed = {};
    for (const [file, stamp] of after) {
      if (before.get(file) === stamp && before.has(file)) continue;
      if (stamp === null) { changed[normalize(file)] = "dir"; continue; }
      // A temporary the host renamed or removed since the scan did not survive this step.
      let bytes;
      try { bytes = fs.readFileSync(file); } catch (error) { if (error.code !== "ENOENT") throw error; after.delete(file); continue; }
      changed[normalize(file)] = storeBlob(bytes);
    }
    for (const file of before.keys()) if (!after.has(file)) changed[normalize(file)] = null;
    before = after;
    return Object.keys(changed).length > 0 ? Object.fromEntries(Object.entries(changed).sort()) : undefined;
  };
  const requests = [];
  const steps = [];
  const stderr = [];
  const child = spawn("dotnet", [dll, ...hostArgs], { stdio: ["pipe", "pipe", "pipe"] });
  child.stdin.on("error", () => {});
  const input = splitLines((line) => { requests.push(requestHash(line)); child.stdin.write(line); });
  process.stdin.on("data", (chunk) => input.push(chunk));
  process.stdin.on("end", () => { input.end(); child.stdin.end(); });
  // While the caller is ahead (more requests than answers), the host may already have written later
  // requests' files, so the scan waits for a step where it has caught up; the files land in the same
  // step in every recording, and a batch caller sees them all before it reads the last answer.
  const output = splitLines((line) => {
    const caughtUp = requests.length <= steps.length + 1;
    // A reply needs at most its own request; a batch caller's lead is timing, so it is not stored.
    steps.push({ in: Math.min(requests.length, steps.length + 1), out: normalize(line.toString("utf8")), files: caughtUp ? changes() : undefined });
    process.stdout.write(line);
  });
  child.stdout.on("data", (chunk) => output.push(chunk));
  child.stdout.on("end", () => output.end());
  child.stderr.on("data", (chunk) => { stderr.push(chunk); process.stderr.write(chunk); });
  const code = await new Promise((resolve) => child.on("close", (status, signal) => resolve(status ?? (signal ? 128 : 1))));
  const exit = { in: Math.min(requests.length, steps.length), code, stderr: normalize(Buffer.concat(stderr).toString("utf8")), files: changes() };
  fs.mkdirSync(path.dirname(runFile), { recursive: true });
  const lines = [
    JSON.stringify(head),
    JSON.stringify(requests.slice(0, Math.max(exit.in, ...steps.map((step) => step.in)))),
    ...steps.map((step) => JSON.stringify(step)),
    JSON.stringify(exit),
  ];
  fs.writeFileSync(runFile, pack(Buffer.from(lines.join("\n") + "\n", "utf8")));
  process.stdout.end(() => process.exit(code));
}

async function replay() {
  const fail = (message) => {
    const text = `GoldenMismatch: ${message} (${path.relative(repo, runFile)}). Re-record with NYRWVY_HOST_REFERENCE=record if the F# host's answer is still the reference.\n`;
    process.stdout.write(text);
    process.stderr.write(text, () => process.exit(97));
  };
  if (!fs.existsSync(runFile)) return fail("no recorded F# run");
  const [head, requestLine, ...rest] = unpack(runFile).toString("utf8").trimEnd().split("\n").map((line) => JSON.parse(line));
  const exit = rest.pop();
  const steps = rest;
  const actual = JSON.stringify(identity());
  if (actual !== JSON.stringify(head)) return fail(`the host was started as ${actual}, recorded as ${JSON.stringify(head)}`);
  const apply = (files) => {
    for (const [file, blob] of Object.entries(files ?? {})) {
      const target = denormalize(file);
      if (blob === null) fs.rmSync(target, { recursive: true, force: true });
      else if (blob === "dir") fs.mkdirSync(target, { recursive: true });
      else { fs.mkdirSync(path.dirname(target), { recursive: true }); fs.writeFileSync(target, loadBlob(blob)); }
    }
  };
  let received = 0;
  let ended = false;
  let next = 0;
  let done = false;
  const pump = () => {
    if (done) return;
    while (next < steps.length && (steps[next].in <= received || ended)) {
      apply(steps[next].files);
      process.stdout.write(denormalize(steps[next].out));
      next++;
    }
    if (next === steps.length && (exit.in <= received || ended)) {
      done = true;
      apply(exit.files);
      process.stdin.destroy();
      process.stdout.end(() => process.stderr.write(denormalize(exit.stderr), () => process.exit(exit.code)));
    }
  };
  const input = splitLines((line) => {
    if (done) return;
    const hash = requestHash(line);
    if (received >= requestLine.length || requestLine[received] !== hash) {
      done = true;
      return fail(`request ${received + 1} differs from the recorded one: ${line.toString("utf8").slice(0, 300)}`);
    }
    received++;
    pump();
  });
  process.stdin.on("data", (chunk) => input.push(chunk));
  process.stdin.on("end", () => {
    input.end();
    if (done) return;
    if (received < exit.in) { done = true; return fail(`the input ended after ${received} of ${exit.in} recorded requests`); }
    ended = true;
    pump();
  });
  process.stdin.on("error", () => {});
  pump();
}
