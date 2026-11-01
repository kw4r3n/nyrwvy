#!/usr/bin/env node
// Stands in for the F# session host in the Zig host parity tests (docs/decisions/zig-session-host.md,
// "The switch", steps 4 and 5): `replay` answers the requests from the run file the F# host recorded
// (the F# host and the recorder are at tag fsharp-host-final).
//
//   node host-golden.mjs replay <run file> <env JSON> -- <host arguments>
//
// The env JSON is the test's per-process overrides (they are already in this process's environment);
// it is part of the run's identity.
//
// A run file keeps the normalized arguments and overrides, a hash per request line, every stdout line
// with the number of request lines received before the host wrote it, and the files the host changed
// under the temporary directories named in its arguments (contents in blobs/, by SHA-256). Exit code
// and stderr come last. Temporary directories and the repository root are stored as <tmpN> and <repo>.
import { createHash } from "node:crypto";
import fs from "node:fs";
import os from "node:os";
import path from "node:path";
import { fileURLToPath } from "node:url";
import zlib from "node:zlib";

const [mode, runFile, envJson, separator, ...hostArgs] = process.argv.slice(2);
if (mode !== "replay" || separator !== "--") {
  process.stderr.write("usage: host-golden.mjs replay <run file> <env JSON> -- <host arguments>\n");
  process.exit(64);
}
const repo = path.resolve(path.dirname(fileURLToPath(import.meta.url)), "..");
const blobs = path.join(path.dirname(path.dirname(runFile)), "blobs");
const env = JSON.parse(envJson);
const tmp = os.tmpdir().replace(/\/+$/, "");
const tokenPattern = new RegExp(`${tmp.replace(/[.*+?^${}()|[\]\\/]/g, "\\$&")}/[^/"\\\\\\s\\u0000]+`, "g");

// Temporary directories in order of first appearance, as the recorder numbered them.
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
// Brotli: the replies repeat themselves heavily (about 20 times smaller).
const unpack = (file) => zlib.brotliDecompressSync(fs.readFileSync(file));
const sha = (data) => createHash("sha256").update(data).digest("hex");
const identity = () => ({ args: hostArgs.map((arg) => normalize(arg, true)), env: Object.keys(env).sort().map((name) => [name, normalize(env[name], true)]) });
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

await replay();

async function replay() {
  const fail = (message) => {
    const text = `GoldenMismatch: ${message} (${path.relative(repo, runFile)}). The goldens are not re-recorded: the F# host was removed (tag fsharp-host-final).\n`;
    process.stdout.write(text);
    process.stderr.write(text, () => process.exit(97));
  };
  if (!fs.existsSync(runFile)) return fail("no recorded F# run");
  const [head, requestLine, ...rest] = unpack(runFile).toString("utf8").trimEnd().split("\n").map((line) => JSON.parse(line));
  const exit = rest.pop();
  const steps = rest;
  // A PATH override is the test's directory before the caller's PATH, which is the recording
  // machine's; only the test's own first entry is part of the identity.
  const comparable = (run) => JSON.stringify({ ...run, env: run.env.map(([name, value]) => [name, name === "PATH" ? value.split(path.delimiter)[0] : value]) });
  const actual = comparable(identity());
  if (actual !== comparable(head)) return fail(`the host was started as ${actual}, recorded as ${comparable(head)}`);
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
