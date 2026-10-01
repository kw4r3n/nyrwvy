# MCP graph-editing tools

**Status:** Maintained local stdio adapter; one qualified Codex CLI live probe completed.

The same ordinary [graph-editing tools](agent-graph-tools.md) can be used by MCP clients without OpenCode. The SDK handles MCP discovery, schemas and transport; the shared client calls the existing JSONL AuthoringSession. Semantic validation, graph identity, proposal review and publication remain in the existing implementation. No model, source language or planning service is added.

The official SDK handles both legacy MCP initialization and the 2026-07-28 protocol on the same stdio entry. Probing/tool discovery does not open a snapshot or start AuthoringSession.

## Setup

Requires Node 22.6+ (Node 22 recommended), npm and the .NET 10 SDK. From the repository root, run one setup command:

```sh
npm --prefix tools/nyrwvy-mcp run setup
```

This installs the pinned MCP SDK dependencies and builds the existing Release AuthoringSession. It does not install OpenCode, need model credentials, start a network listener, or modify client configuration. Rebuild AuthoringSession after changing its implementation. The MCP client will launch the server itself.

The ordinary server launch command is:

```sh
node --experimental-strip-types /absolute/path/nyrwvy/tools/nyrwvy-mcp/server.mjs
```

`--allow-host-sources` (or `NYRWVY_ALLOW_HOST_SOURCES=1` in the server's environment) is an optional server-level opt-in that lets `runPure`/`runPureByName` compile and run `hostSources` C# files; it is off by default. See [Host sources run arbitrary code](#host-sources-run-arbitrary-code) before enabling it.

Use an absolute server path. Relative snapshot paths default to the MCP server process's working directory. For local CLI clients that launch stdio servers in the active project, this makes `nyrwvy_open {snapshot:"planner.snapshot.json"}` naturally create or open `./planner.snapshot.json` in that project.

`--workspace /absolute/path/my-project` remains an optional server-level override for a deliberately fixed workspace. In addition, `nyrwvy_open` accepts an optional `workspace` argument so an agent can supply the host-known project directory when a GUI, plugin cache, desktop host, or other MCP launcher starts the server elsewhere. The user therefore does not need to bake a program directory into ordinary MCP registration.

Nyrwvy intentionally does not add a dependency on MCP Roots for this. Roots are deprecated in the 2026-07-28 MCP specification, which recommends passing directories/files through tool parameters, resource URIs, or server configuration for new implementations. The .NET assembly is always located relative to the server's repository, independently of the client project. Use an absolute `node` executable path when a graphical client does not inherit the expected PATH; `dotnet` must also be available to the launched server.

## Client configuration

Codex CLI has one bounded live result, linked below. The other client examples are configuration guidance, not live compatibility claims. Paths are placeholders to replace locally. All examples launch the same server.

### Codex CLI

Add to the applicable Codex `config.toml`:

```toml
[mcp_servers.nyrwvy]
command = "node"
args = ["--experimental-strip-types", "/absolute/path/nyrwvy/tools/nyrwvy-mcp/server.mjs"]
```

Launch Codex in the active project (or use `-C`) so project-relative snapshot paths resolve there. This configuration leaves `--workspace` and MCP `cwd` unset, as in the live probe.

Noninteractive `codex exec` may need a server-level approval setting: in Codex CLI 0.155.0, the first probe was blocked with `MCP tool call requires approval, but approval policy is never`. For a run in which Nyrwvy tools are authorized for automatic approval, add `default_tools_approval_mode = "approve"` under `[mcp_servers.nyrwvy]`, or pass:

```sh
-c 'mcp_servers.nyrwvy.default_tools_approval_mode="approve"'
```

That server-scoped correction allowed the follow-up to complete while retaining `workspace-write`; no global approval/sandbox bypass was used. This is an observed configuration requirement for that invocation, not a requirement to auto-approve every Codex session.

#### Host sources run arbitrary code

Server-wide auto-approval also approves every `nyrwvy_request`, which reaches the whole AuthoringSession protocol, including `runPure`. With `hostSources`, `runPure` compiles the named C# files beside the generated program and runs them with the user's permissions. An auto-approved model could therefore write a `.cs` file and execute it. For that reason the server rejects `hostSources` and `hostCapability` bindings with `RunHostSourcesDisabled` unless it was started with `--allow-host-sources` (or `NYRWVY_ALLOW_HOST_SOURCES=1`); the opt-in is server configuration, never a tool argument, and `nyrwvy_open` reports it as `hostSourcesAllowed`. Do not combine `--allow-host-sources` with auto-approval unless the host code and the model's file writes are otherwise sandboxed. Scripted `fakeCapability` bindings need no opt-in.

Reference: [Codex MCP configuration](https://developers.openai.com/codex/mcp/).

### Claude Code and other `mcpServers` clients

For Claude Code, add the entry to the project `.mcp.json`, following the client's normal trust/approval workflow:

```json
{
  "mcpServers": {
    "nyrwvy": {
      "command": "node",
      "args": ["--experimental-strip-types", "/absolute/path/nyrwvy/tools/nyrwvy-mcp/server.mjs"]
    }
  }
}
```

Clients with an `mcpServers` configuration use the same command/args; the settings file's location is client-specific. Reference: [Claude Code MCP](https://code.claude.com/docs/en/mcp).

### Zed

Add to Zed settings:

```json
{
  "context_servers": {
    "nyrwvy": {
      "command": "node",
      "args": ["--experimental-strip-types", "/absolute/path/nyrwvy/tools/nyrwvy-mcp/server.mjs"]
    }
  }
}
```

Reference: [Zed MCP](https://zed.dev/docs/ai/mcp). Tool availability and permission prompts depend on the selected agent/profile. An external agent's MCP forwarding is a client integration concern, not an alternative Nyrwvy semantic path.

## Operation and lifetime

`tools/list` advertises `nyrwvy_open`, `nyrwvy_inspect`, `nyrwvy_recipe_reference`, `nyrwvy_create_type`, `nyrwvy_create_function`, `nyrwvy_replace`, `nyrwvy_commit`, `nyrwvy_reject`, `nyrwvy_request`, and `nyrwvy_close`. Their arguments, descriptions and handlers are shared with the OpenCode adapter in `tools/nyrwvy-agent/graph-tools.mjs`.

`nyrwvy_recipe_reference` is read-only and sessionless, so it may be called before `nyrwvy_open` when a function-body or replacement recipe shape is unfamiliar. It takes no argument (the index), `category` (signatures), `kind` or `kinds` (shapes); see [agent graph tools](agent-graph-tools.md#named-recipe-reference-guide). Start an authoring session with `nyrwvy_open` arguments such as `{"mode":"create","snapshot":"planner.snapshot.json"}`. This normally resolves against the active project cwd. If the host launched MCP elsewhere but the agent knows the project directory, use `{"mode":"create","snapshot":"planner.snapshot.json","workspace":"/absolute/path/to/project"}` for that open only. Then inspect, propose, read the exact returned review, and explicitly commit/reject. Reviews are compact by default (signature, `nodeCount`, impact facts and fingerprints, no per-node graph); pass `detail: "full"` to `nyrwvy_create_function` or `nyrwvy_replace` to read the proposed graph before commit ([session contract](agent-authoring-session.md#simulate-review-detail)). Proposing never auto-commits. Exact stable-ID operations remain accessible via `nyrwvy_request`.

For a dependency-backed graph, `nyrwvy_open` also accepts `{"mode":"create","snapshot":"planner.json","dependency":"policy.json"}` (and optional `workspace`). Both relative paths resolve against that workspace, not the toolchain repository. The source must be a complete standalone snapshot; its exact content is retained in the sidecar, so source updates/removal and clone/move do not require the old absolute path. Use `mode: "open"` without `dependency` to resume the existing pin. `mode: "pin"` explicitly migrates a legacy source-only selection or repairs its storage using the exact selected content; optional `dependency` may point to a relocated original copy, not a newer version. No semantic upgrade is performed. Imported edits fail at simulation, while local functions can reference imported declarations. See the [pinned dependency and recovery contract](agent-authoring-session.md#pinned-snapshot-dependency-one-level).

One MCP server process owns at most one active authoring session. Concurrent tool calls, including open/close, are serialized. An explicit `nyrwvy_close` starts a **5-second graceful drain deadline when received**, even if an earlier open or mutation is still awaiting a response. Earlier calls may finish normally within that window. On expiry only that close's still-pending predecessors are cancelled: sent requests receive an unknown-outcome error, and unsent queued requests are discarded. The close still waits for the shared transport's bounded termination/reaping before later open/inspect calls execute; it does not abort the server lifetime or later requests. A timed-out drain remains a close error even if the earlier failed open already cleaned up its session. Thus normal FIFO order is preserved, and the same MCP connection can explicitly reopen after an abnormal close without replaying the mutation.

Different client processes do not share pending proposals. The existing snapshot lock still allows only one writer to a snapshot. Switching clients therefore means closing the previous authoring session and reopening the same committed snapshot, not simultaneous collaborative editing.

Structured JSONL results are returned as MCP `structuredContent` and text content; semantic `ok: false` becomes `isError: true` without dropping its diagnostics. Transport/lifecycle failures are also error tool results. stdout is reserved for MCP; diagnostics use stderr.

Client EOF, SIGINT or SIGTERM cancels outstanding tool work, settles queued calls, and closes the underlying AuthoringSession before closing the MCP handle. An already-running `nyrwvy_close` uses the same bounded shutdown as an ordinary close; shutdown cannot bypass child reaping by waiting forever behind that lifecycle call. Explicit close drains accepted authoring responses for up to 5 seconds, then allows 5 seconds after EOF, 5 seconds after SIGTERM and a final 5 seconds after SIGKILL. Transport failure/cancellation skips the graceful EOF window and starts termination immediately. See the [shared transport lifetime contract](agent-graph-tools.md#transport-lifetime) for deadlines and retained ownership if the OS cannot reap the child. Normal EOF shutdown releases the actual snapshot lock.

Cancellation of a sent request invalidates that session. Reopen and inspect committed state before retrying: a lost response has unknown outcome and does not prove a commit failed. Pending proposals and sent mutations are never restored or replayed automatically. Abnormal shutdown is reported as a transport/lifecycle error, not a successful close.

## Verification and scope

Run the focused deterministic MCP contract after setup:

```sh
npm --prefix tools/nyrwvy-mcp test
```

The contract uses the official MCP client over real subprocess stdio, then the real AuthoringSession. It covers tool discovery and schemas, sessionless recipe reference reads, automatic cwd-relative project opening, per-open workspace fallback from an unrelated cwd, explicit publication, error mapping, client isolation, and EOF/reopen. Additional raw-stdio tests use local children that acknowledge but ignore EOF/SIGTERM, covering MCP shutdown while idle, opening, closing or awaiting a sent mutation. Explicit-close regressions keep MCP stdin open, exercise a hung open or mutation followed by close/open/inspect, and verify settled predecessors, child reaping and no replay. A controlled successful response also verifies graceful drain and FIFO ordering. Shutdown tests await server exit without the client's own kill timeout hiding a stuck server. Existing ordinary-tool tests continue to exercise semantic workflows through the compatibility entry point.

On 2026-09-21, Codex CLI 0.155.0 with `gpt-6-astra` completed the known TimedOut maintenance task through this MCP surface on commit `ab74ecc4`: project-relative open, inspect, simulate with returned review, explicit commit, close/reopen, and committed-state inspection. Existing TypeId/FunctionId values were preserved, the new VariantId was system-owned, repaired match count was 1, and final execution readiness was `ready`. The follow-up had no semantic/transport errors, mutation retries, or semantic user intervention. See [Run 1: approval-blocked](../verification/codex-cli-mcp-maintenance-probe.md) and [Run 2: configuration-corrected success](../verification/codex-cli-mcp-maintenance-probe-run-2.md).

The model also consulted local repository documentation and `Protocol.fs` to discover the exact maintenance operation. The supported claim is **Codex CLI + local repository context + Nyrwvy MCP tools**; tool descriptions alone were not proven sufficient.

A later independent `nyrwvy-ai-task-router` consumer was authored without reading the Nyrwvy repository docs or implementation, but it first tried an unsupported `help` request and then incurred 10 function-recipe `ProtocolError` iterations while discovering names such as `conditional`, `whenTrue`/`whenFalse`, `fieldAccess.source`, and `matchUnion.input`. That second observed friction motivated `nyrwvy_recipe_reference`: an on-demand shared reference rather than a new semantic operation or an always-expanded recursive schema.

The bounded 2026-09-22 follow-up on `f95027b6` created a new policy program from an empty external project. The model selected `nyrwvy_recipe_reference` without being told its name, read the index plus `parameter`, `fieldAccess`, `unionConstruct`, `conditional`, and `matchUnion`, then submitted the nested function recipe successfully on the first attempt. The run completed four reviewed commits plus close/reopen/inspection with zero recipe `ProtocolError`, semantic/transport errors, approval errors, or repository-source lookups. See [the live verification record](../verification/expression-recipe-reference-live-probe.md). This is evidence for the measured discoverability repair, not a general model-success-rate claim.

Claude Code/Zed model use, interactive UI discovery, and external-agent forwarding remain unverified live boundaries. This adapter does not provide HTTP hosting or make a local stdio server directly available to browser-only/cloud-only clients. No additional live run or host matrix is scheduled without a relevant implementation change or a new decision-relevant observation.

## Maintainer rule

Keep tool definitions and authoring transport host-neutral. A host adapter must reuse them rather than copy semantic operations, invent an alternative snapshot authority, or silently replay a mutation. Add a focused transport contract for a new connection boundary; do not repeat the entire semantic or live-model suite for each host.
