// Focused host-boundary regression against an emitted example bundle.
import { execFile } from "node:child_process"
import { copyFile, mkdtemp, rm, writeFile } from "node:fs/promises"
import { tmpdir } from "node:os"
import { join, resolve } from "node:path"
import { promisify } from "node:util"

if (!process.argv[2]) throw new Error("Usage: test-acquisition.mjs <bundle-directory>")
const bundle = resolve(process.argv[2])
const directory = await mkdtemp(join(tmpdir(), "nyrwvy-acquisition-test-"))
const execute = promisify(execFile)
try {
  await copyFile(join(bundle, "Generated.cs"), join(directory, "Generated.cs"))
  await copyFile(join(bundle, "Host.cs"), join(directory, "Host.cs"))
  await copyFile(new URL("./test-acquisition.cs", import.meta.url), join(directory, "Program.cs"))
  await writeFile(join(directory, "Test.csproj"), '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>')
  const environment = { ...process.env, DOTNET_CLI_TELEMETRY_OPTOUT: "1", DOTNET_NOLOGO: "1" }
  await execute("dotnet", ["build", "Test.csproj", "--configuration", "Release", "--source", join(process.env.HOME, ".nuget", "packages"), "-p:NuGetAudit=false"], { cwd: directory, env: environment, timeout: 60000 })
  const { stdout } = await execute("dotnet", ["bin/Release/net10.0/Test.dll", join(directory, "fixture")], { cwd: directory, env: environment, timeout: 10000 })
  process.stdout.write(stdout)
} catch (error) {
  process.stderr.write(error.stdout ?? "")
  process.stderr.write(error.stderr ?? "")
  throw error
} finally { await rm(directory, { recursive: true, force: true }) }
