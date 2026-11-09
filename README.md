# Agent Diff Review Slim

A planned lightweight, human-operated Git review tool for Claude Code, built with
.NET 10 and an existing Edge or Chrome browser.

**Status: planning only.** This initial repository contains the agreed design,
not a runnable application. Build/run commands and verified platform support will
be added with the implementation.

## Intended workflow

1. Start a review beside the Git checkout, normally from a Claude dev-container
   session, or directly on Windows/WSL.
2. Open the UI in host Edge app mode where an approved launcher is available.
   Otherwise open the printed private URL in Edge or Chrome.
3. Review changes and add inline, file-level or overall comments.
4. Submit non-empty feedback to Claude, or cancel without starting a Claude turn.

The reviewer itself never invokes an LLM and never changes repository files.
Submitting feedback through Claude's integration allows Claude to act on it.

## Design decisions

- **.NET 10 (`net10.0`) preferred**, using ASP.NET Core and built-in libraries.
  Work-container availability and approval still need verification.
- **No third-party NuGet or npm packages and no runtime downloads.** Use existing
  Git, .NET and browser installations.
- **Claude is the only required agent integration.** The standalone CLI can be
  exercised locally from pi; a dedicated pi extension is optional. opencode is
  out of scope.
- **Browser-based UI:** plain JavaScript/CSS and Git-generated patches; no
  Glimpse, Monaco, Tailwind runtime, Electron or browser installation.
- **Flexible execution:** run the backend beside the checkout on Windows,
  WSL or in a dev container; use private local access/approved forwarding.
- **Best-effort automatic Edge launch**, with a manual URL fallback. No custom
  container-to-Windows process bridge.
- **Close means cancel:** closing the window must cancel after a short reconnect
  grace period, without sending feedback. Verify disconnect behaviour through
  actual forwarding before claiming support.
- **Simple refresh recovery:** bounded, tab-scoped sessionStorage for draft input
  and authentication, not repository contents. No durable draft store or process
  restart recovery. Browser storage may be disk-backed.
- **Verification:** focused dependency-free regression checks plus manual browser,
  container and Claude integration checks. Local pi testing does not prove Claude
  hook compatibility.

The first-release scope order is not yet settled: the intended feature inventory
includes working-tree/base comparisons, last commit, history and all-files views.

See [REFACTOR-PLAN.md](REFACTOR-PLAN.md) for architecture, acceptance criteria,
explicit limitations and remaining decisions.

## Relationship to the original project

This is a separate work-focused implementation of
[Agent Diff Review](https://github.com/dfox97/agent-diff-review), with fresh Git
history. It does not replace, delete or import the history of that project.

Reference upstream commit: [`7d9295b63840e8310ef6d2e829320ed6577ff537`](https://github.com/dfox97/agent-diff-review/tree/7d9295b63840e8310ef6d2e829320ed6577ff537).
The plan also records decisions made during the subsequent design session.

The original project uses TypeScript/Node, Glimpse and Monaco, and supports
multiple agent hosts. Slim deliberately uses .NET plus an existing browser and
focuses on Claude. Rich editor features and external editor/terminal launching
are not goals.

During development, invoke slim explicitly rather than replacing an existing
`diff-review` installation. Final executable naming and installation remain to be
settled.

## Licence

[MIT](LICENSE). Original copyright and attribution are retained. Preserve
applicable licence notices for any code reused during implementation.
