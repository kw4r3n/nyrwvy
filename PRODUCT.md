# Product

<!-- impeccable:product-schema 1 -->

Scope: the human-facing surface of Nyrwvy is the viewer page (`src/Nyrwvy.Viewer/viewer.html`). The rest of the repository (F# core, Zig host, MCP and OpenCode adapters) has no visual UI.

## Platform

web

## Users

Two audiences carry equal weight:

- **People supervising an AI agent.** The agent edits a Nyrwvy semantic graph through tools. The person opens the page `nyrwvy_view` wrote to judge a pending proposal (Change Review) before the agent commits or rejects it, or to inspect a committed function (Function Focus).
- **Nyrwvy developers.** They open the same pages to check the engine and its projections while building Nyrwvy.

## Product Purpose

Nyrwvy is an AI-native semantic programming system. Programs are persistent typed semantic graphs, and every change is a validated, meaning-level operation that is simulated and reviewed before it is published. The viewer makes that review readable for a human, as a static HTML page opened in a browser. A page is a success when the reader can decide to approve or reject a change from its meaning, without reading generated code.

## Positioning

The page shows the change at the level of meaning, not as a text diff of generated C# or Zig. It lists renames, visibility changes, effects, capabilities, obligations, abort boundaries, and removed or added parameters and variants, all keyed to stable IDs. The engine reports the requested intent and the resulting diff; the page renders the engine's review and never computes its own.

## Operating Context

- An agent calls `nyrwvy_view` over MCP or OpenCode. By default the page is written to `nyrwvy-views/` beside the snapshot, and the person opens the file locally in a browser.
- `tools/Nyrwvy.ViewerExport` renders the same viewer from the F# side.
- A Change Review is read before `nyrwvy_commit` or `nyrwvy_reject`. Writing the page publishes nothing.
- The page is a projection. It is never read back, and editing it changes nothing.

## Capabilities and Constraints

- Two views: **Function Focus** (signature, abort boundaries, blocking obligations, unresolved declarations, structured graph, referenced types, capabilities and effects) and **Change Review** (semantic change summary, committed and proposed focus, engine review).
- Current implementation: one fixed HTML asset with the view data embedded as JSON (`@DATA@`). The page's script renders it, so the page needs JavaScript. Every data string is HTML-encoded. A strict CSP allows only inline styles and one script, pinned by its hash.
- **Confirmed constraint:** light and dark follow the OS color scheme (today through `color-scheme: light dark` and the system colors `Canvas` and `CanvasText`).
- Not confirmed as binding: keeping the page a single self-contained file, and whether the F# viewer-export tests pin the rendered output. Check both before changing the page's structure.
- Terminology: snapshot, proposal, commit/reject, stable ID, obligation, effect, capability, Function Focus, Change Review.

## Brand Commitments

The name is "Nyrwvy", and the page title is "Nyrwvy viewer". No logo, palette or voice guide exists.

## Evidence on Hand

- The viewer asset: `src/Nyrwvy.Viewer/viewer.html`.
- The specs: `docs/specs/agent-graph-tools.md` (human-readable view) and `docs/specs/mcp-graph-tools.md`.
- The README states the project is under active development and not for use. There are no users, testimonials or adoption figures, and none may be invented.

## Product Principles

1. Meaning before text: show what a change does to the program, not how its generated code differs.
2. The engine is the authority: the page renders the engine's review and never infers its own conclusions.
3. Decision-ready: blocking obligations, unresolved declarations and removals must be easy to find before someone approves a commit.
4. A projection, never a source: nothing on the page is read back or edited.

## Accessibility & Inclusion

No standard has been set yet (open decision). The only confirmed requirement is that light and dark follow the OS color scheme.
