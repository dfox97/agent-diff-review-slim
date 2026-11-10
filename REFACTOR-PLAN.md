# Agent Diff Review Slim implementation plan

Status: first CLI/UI implementation slice is working locally on Windows with
.NET 10. This remains a target design, not a claim that every feature below is
implemented or release-verified. Claude hook mode, history scopes and actual
work-container verification are still pending.

### Implementation progress

Implemented: `diff-review-slim open`, working-tree and committed base comparisons,
root/unborn working-tree handling, safe lazy captures, Git-generated full-context
patches, a split/narrow diff UI, inline/file/overall comments, reviewed markers,
filter/tree/wrap/context controls, plain-text source rendering through
`textContent`, Markdown/JSON/atomic file output, private HTTP session
protection, browser-disconnect cancellation and tab-scoped reload recovery.
Windows Edge/Chrome launch is best-effort through shell registration; no bridge
or environment installation was added.

A separate dependency-free console harness covers parsing, Git comparisons,
submission/output and real HTTP/process lifecycle/security paths. Previously
recorded build/publish, JavaScript and installed-Edge checks predate the
safety/rendering cleanup in PR #2; see `todo.md` for that historical baseline and
commands to rerun. The recorded Windows account could not create filesystem
symlinks, so that check was skipped. Native app-window interactions, Chrome,
Linux/WSL and work-container forwarding still need manual acceptance.

Optional local pi testing now has a thin `integrations/pi.ts` command adapter.
It invokes the published CLI, inserts successful feedback only as an editor draft,
and preserves no-auto-send semantics. `checks/pi-smoke.py` exercised the actual
adapter in real pi 1.4.0 RPC mode with submit/cancel/empty/disconnect/error/shutdown
checks and no agent/model work. This optional check uses existing Python/pi;
neither is required by the .NET core or its regression harness. Interactive pi
terminal/Escape acceptance remains pending. This is not Claude hook verification.

Browser assets are served by an explicit three-file map in `Program.cs`.
Missing required assets fail startup; unrelated files and generated `.br`/`.gz`
siblings are ignored and not exposed. New UI assets must be added to the map.
The regression harness requests every required asset and checks additional
published source assets for missing mappings. It also checks that the Copy
preview matches final stdout byte for byte.

The current slice starts with working-tree/base comparisons as an implementation
order, not an agreed decision to omit other scopes from the first release.

This is a separate .NET/browser implementation with fresh Git history, not an
in-place migration of the original application. The original repository remains
independently usable and must not be deleted or modified by this implementation.

Source project: [dfox97/agent-diff-review](https://github.com/dfox97/agent-diff-review),
upstream HEAD at planning handoff: `7d9295b63840e8310ef6d2e829320ed6577ff537`.
The original architecture review used `71e62f2309b303d3a2f918d0f2842e77d6c2578c`.
This plan incorporates the subsequent design-session decisions; those changes
were not part of the upstream reference commit. Preserve the source project's
MIT licence and required attribution for reused code.

## 1. Original project architecture (reference only)

The original application is TypeScript/Node with an already-vanilla-JavaScript frontend.
There is no React, Angular, Vite, Electron or Tauri to remove.

- `bin/diff-review` runs the built CLI, falling back to TypeScript through tsx.
- `src/bindings/` contains standalone CLI, pi and opencode integration.
- `src/core/git/` resolves the repository/base, builds a file/commit index and
  lazily loads original/modified contents. Git runs as child processes.
- `src/core/window/orchestrator.ts` opens Glimpse, gathers metadata, exchanges
  ready/init/files/request-file messages and resolves submit/cancel/close.
- `src/platform/wsl-glimpse.ts` handles native windows and WSL bridging. It can
  install Glimpse on Windows, patch/build its C# host, or download Chromium on
  Linux. These behaviours must disappear from the replacement.
- `web/` uses Monaco's read-only diff editor and a locally vendored Tailwind
  browser runtime. Numerous small global modules coordinate comments, scopes,
  file trees, modals and rendering.
- `src/core/prompt.ts` turns review data into Markdown. The CLI's `open` mode
  emits that Markdown to stdout or `--out`; `clip` uses OS clipboard tools.
- `.claude/hooks/diff-review-hook.mjs` reads hook JSON, invokes `open`, and
  maps its result to additionalContext or a blocking decision. The configured
  timeout is 600 seconds. The reviewer itself never invokes an LLM.

### How the original diff works

The original Git pipeline supplies names/statuses and full file contents; Monaco calculates
the visual diff. Without a base, tracked changes are indexed against HEAD and
untracked files are added. Last-commit and up-to-50-commit history scopes are
also indexed, alongside current tracked/untracked files.

There is a consistency bug to avoid copying: with `--base`, the file list uses
merge-base..HEAD, but the modified content is read from the working tree.
Local changes can therefore appear inside a supposedly committed branch diff.

Contents are loaded on demand, capped at 1,000,000 bytes per file, with a
40-entry backend cache and next-file prefetch. The frontend retains its own
unbounded contents cache. Binary/minified extensions are filtered. Working-file
reads reject final-component symlinks, but ancestor-directory symlinks also need
protection in the replacement.

### Feature inventory and disposition

These are intended target behaviours, not features already implemented in slim.
Whether last-commit/history/all-files scopes are required in the first usable
release or follow later is still an open prioritisation decision. Do not silently
drop them or claim their first-release deferral has been agreed.

| Existing behaviour | Proposed treatment |
| --- | --- |
| Working-tree diff, including staged/unstaged net changes and untracked files | Preserve; compare HEAD to working tree, not a separate staging UI |
| Base branch argument, local/origin fallback, merge-base comparison | Preserve; pin merge base and HEAD, compare committed contents consistently |
| Last commit and selectable history, currently limited to 50 commits | Preserve; load a selected commit's file list on demand |
| All-files scope | Preserve as a read-only current-file view |
| Added/modified/deleted/renamed file labels and old/new sides | Preserve, including rename paths and correct line numbers |
| Collapsible directory tree and fuzzy file filter | Preserve with small DOM functions; reuse the existing search logic where useful |
| Inline comments on either side; immediate body editing and deletion | Preserve with ordinary textareas beneath the relevant line |
| File comments, overall review note, scope/commit-specific comments | Preserve |
| Reviewed markers, comment counts and summary | Preserve in memory |
| Sidebar toggle, wrapping, full file/changed-area toggle, per-file scroll | Preserve with CSS, native scrolling and expandable context |
| Automatic initial scope: diff, then last commit, history, all files | Preserve |
| Loading/error/empty states and lazy file reads | Preserve with fetch and visible retry where needed |
| Finish/cancel; empty feedback produces no agent prompt | Preserve |
| Markdown stdout, optional output file, positional base alias | Preserve; add optional JSON output |
| Clipboard mode | Use a user-clicked browser Copy action before submission; document this CLI behaviour change |
| pi/opencode draft insertion and pi Escape cancellation | Claude is the only required work integration. pi may exercise the new CLI locally; a dedicated pi adapter is optional and test-only. opencode migration is out of scope. No host SDKs in the .NET app |
| Claude slash command/hook and cancellation blocking | Replace Node hook implementation with the same executable's hook mode; verify installed Claude contract |
| Open in nvim, line jump, terminal selection and window minimisation | Defer from the work version; offer copyable path:line instead |
| Monaco syntax colouring | Omitted; the custom tokenizer was removed in PR #2. Render source with `textContent` and retain added/removed line backgrounds |
| Intraline diff, minimap, folding and editor commands | Deliberately simplify; do not recreate an IDE |

There are no explicit application-wide review shortcuts in the current browser
code. Escape clears the file filter; pi separately supports Escape to cancel.
Monaco supplies its own editor shortcuts. Preserve native selection/copy/find,
keyboard-operable file/comment controls and Escape clearing the filter.
Do not invent a large shortcut system or intercept typing in textareas.

Branch switching, discard and restore helpers exist in `git/actions.ts` but
are not wired into the current UI/protocol. They are not user-facing features
to reproduce. Keep the replacement read-only with respect to repository files.

## 2. Original stack not carried into this repository

Do not copy the old backend, vendored assets, npm tooling or Git history into
this repository. The table describes alternatives to the original stack, not
instructions to delete files from the source project.

| Original item | Purpose | Slim approach |
| --- | --- | --- |
| `glimpseui` | Native window and message transport | Existing Windows Edge plus localhost HTTP |
| Vendored Monaco 0.52.2 | Diff calculation/rendering, syntax/editor features | Git patch output plus DOM/CSS |
| Vendored Tailwind browser 4.3.1 | Runtime utility CSS generation | One ordinary stylesheet |
| TypeScript, native-preview/tsgo, tsx | Type checking/build/source execution | .NET SDK build; browser JavaScript has no build |
| Vitest and existing test infrastructure | Automated tests | Small dependency-free regression harness plus manual workflow verification; no new test framework or CI setup |
| Node/Bun type packages | TypeScript tooling | Not included |
| pi peer dependencies and opencode SDK/plugin development dependencies | In-process agent adapters | No core dependencies; optional local pi testing through CLI, no opencode migration |
| npm manifests/lockfile, vendor downloader and TS configs | Node build/distribution | Not included; build/publish with .NET |
| Windows/WSL Glimpse bridge and native host patch/build scripts | Platform orchestration | Not included |
| Editor/terminal probing and shell command templates | External Neovim launch | Copy path:line |
| Webview protocol, placeholder HTML, asset path/UNC resolution | Native host plumbing | Local static files and a few HTTP endpoints |

The core target has zero third-party NuGet packages, zero npm packages and no
runtime downloads. Git, the approved .NET SDK/ASP.NET Core runtime and an existing
browser (Edge preferred, Chrome also available) remain prerequisites. Optional
host integrations must not become core build dependencies.

## 3. Smallest viable target

One process per review, one repository fixed at launch, one session in memory.
Use `Microsoft.NET.Sdk.Web`, targeting .NET 10 (`net10.0`). The first implementation
now uses that target. Work-environment approval and availability remain gates.
This repository has no dev-container configuration, so actual work-environment
support cannot be confirmed here. Check the actual container's .NET 10 SDK and ASP.NET Core runtime before
work-environment verification; if unavailable, flag the blocker rather than
silently choosing another framework or installing/upgrading the environment.
Local implementation can use this Windows machine's existing .NET 10 SDK, but
that does not establish work-container compatibility.

### Work environment decisions

- The primary work workflow is Claude sessions running inside dev containers.
  Claude is the only required agent integration for this work version, alongside
  the standalone CLI contract. pi on the local machine may be used to exercise
  the CLI; a dedicated pi extension is not a release requirement. opencode
  integration is out of scope.
- Support Windows and WSL/Docker development environments without tying the core
  reviewer to one container-to-host bridge. Run the reviewer alongside the checkout
  and Git, serving the UI to the host browser through private local access or
  approved port forwarding as appropriate.
- Edge and Chrome are already installed on the Windows host. Prefer exploring
  Edge app mode as the review window, while keeping the UI usable in an ordinary
  Edge/Chrome tab; app mode must not be a core runtime dependency.
- Prefer best-effort automatic opening in host Edge app mode. Use an existing
  approved host-launch mechanism where available; do not rebuild a custom
  Windows/WSL bridge or add runtime dependencies just for automatic launch.
  If automatic launch is unavailable or fails, leave the reviewer available and
  print the private forwarded URL with manual-opening instructions to stderr.
  Confirm the actual forwarding and host-launch mechanisms in the work container
  before promising automatic launch there.

Current implementation files (hook mode is still pending):

- `AgentDiffReview.csproj` and `NuGet.Config`: .NET 10 build with no package feeds.
- `Program.cs`: HTTP host, security boundaries, endpoints, lifecycle and browser launch.
- `CliOptions.cs`: launch-time CLI options and forwarding validation.
- `ReviewSession.cs`: completion, comment validation, feedback and atomic output.
- `GitDiff.cs`: bounded Git invocation, indexing, safe captures and patch parsing.
- `checks/Program.cs`: dependency-free checks as a .NET 10 file-based app that
  references the main project; no second project file or duplicated build output.
- `wwwroot/index.html`
- `wwwroot/app.js`
- `wwwroot/style.css`
- `README.md`

Keep the licence. Add Claude command/settings files only after verifying the
installed Claude contract; do not blindly copy the original hook configuration.
Keep the implementation small; add source files when readability requires them.
No controllers, service interfaces,
repository abstractions, database, DI layers, message bus or hosted workers.
Do not use the generated OpenAPI/Swagger package scaffolding.

### Git and rendering

Use `ProcessStartInfo.ArgumentList`, `UseShellExecute = false`, fixed Git
operations and the startup repository directory. Drain stdout/stderr together,
bound output, enforce command deadlines and kill an active Git process tree on
cancellation. Disable external diff/textconv execution for review operations.

Resolve requested refs to validated commit IDs at startup. Use NUL-delimited
name/status output so spaces, tabs, Unicode and newlines in filenames survive.
Handle initial/root commits without assuming HEAD or a parent exists. For merge
commits, use an explicitly labelled first-parent comparison.

Have Git calculate per-file unified patches; do not write a diff algorithm in
C# or JavaScript. Parse hunk headers and context/add/delete lines into rows with
old/new line numbers. Pair adjacent deletion/addition blocks for a simple split
view; use a unified layout on narrow windows. Git metadata is authoritative for
paths: do not recover filenames by splitting patch headers on spaces.

Retain full-file viewing through full-context patches with bounded output and
expandable unchanged blocks. Render untracked files as added rows and all-files
content as plain numbered lines. Preserve CRLF, missing-final-newline and empty
file semantics. Binary, symlink, unsupported and oversized entries get an
explicit omission message, never misleading blank diffs.

Fetch metadata once; load only the selected scope/file. Pin commit revisions.
Working-tree reads represent the review session's captured content for each
loaded file; retain it while comments reference it, with a total memory cap.
If work changes during a review, require restarting to review the newer state
rather than silently rebasing line comments. Do not claim the tool locks Git.

Render one file at a time into a shared scrolling container using DOM textContent,
a document fragment and delegated click/input handlers. Keep comments keyed by
scope, commit, file, side and line. Start without prefetch or an LRU framework.
Use expandable unchanged regions and, only if needed, bounded row batches for
large files. Cap retained data rather than duplicate full contents in both
backend and frontend caches.

### HTTP and session protection

A small API is enough: session metadata, selected-file diff, submit and cancel.
Static files come only from `wwwroot`; never expose the repository directory.

Generate an unguessable per-review token using built-in cryptography. Include it
in the initial URL fragment, retain it in tab-scoped sessionStorage for reload,
remove it from the visible URL after reading it, and send it in a request header.
Require it for every data/mutation request. Clear session storage on acknowledged
completion or confirmed expiry; a saved token never revives a terminated session.
Reject unexpected Host/Origin values, do not enable CORS, reject non-JSON
mutations and apply small request/comment/output limits. Use no-store for review
responses, a restrictive local-only CSP and no-referrer policy. Avoid token or
file-content request logging.

Prefer the simplest refresh support: save a bounded draft (comments, overall
note, reviewed markers and selected scope/file) in sessionStorage, associated
with the server's session ID. Restore it only after authenticating with the same
live session; fetch diff data again from the server's captured content. Do not
store repository file contents in browser storage. No database, server-side draft
autosave endpoint, localStorage or recovery across reviewer-process restarts is
required. Browser sessionStorage may be disk-backed; do not describe this as
memory-only storage. If browser storage is unavailable, keep review usable but
show that refresh recovery is unavailable. Validate restored input as untrusted
and clear stale/completed-session data when detected.

The browser submits file IDs and allowed scope/commit IDs; it never supplies a
repository path, shell command or arbitrary revision. Resolve every ID through
the server's review index. Reject absolute/traversal paths and symlinks in every
working-tree path component; keep resolved paths inside the repository. Do not
serve `.git` or permit arbitrary file reads. Review an ordinary trusted developer
checkout; do not claim that path checks sandbox a malicious concurrent local
process swapping files.

Bind to container loopback through the approved forwarding mechanism. If the
actual forwarding setup requires another container bind address, make that an
explicit documented configuration while keeping the Windows-facing forwarded
port private/loopback. Never silently publish to the LAN or a public tunnel.
Use one available configured port per process; fail clearly on a collision.

### CLI and Claude feedback

Preserve:

`diff-review-slim open [base] [--base branch] [--out path]`

The executable deliberately differs from the original `diff-review` name for
safe coexistence. Hook/adaptor configuration must invoke slim explicitly.

- Default output stays Markdown so existing callers keep working.
- `--format json` returns a versioned object with session ID, submitted status,
  repo/base/HEAD identity, overall note and comments. Include resolved old/new
  paths, scope, commit, side and line range; do not return opaque file IDs alone.
- Keep stdout exclusively for final feedback. URLs, startup/errors and hosting
  logs go to stderr. Publish/build separately, not on every review launch.
- Empty/cancel/close/error produces no feedback and a non-zero exit status.
  Emit success once, only after validating the submission and writing output.
- `--out` is a launch-time, caller-controlled destination, never a browser input.
  Prefer stdout; use unique private temporary files and atomic completion only
  when a caller needs a file.
- `hook` mode reads Claude's JSON stdin and emits Claude's hook JSON. It uses
  the same review flow directly rather than spawning another .NET reviewer.
  Only a successful non-empty review adds context; other results block expansion.
  Align the review deadline with the configured hook timeout.

Required Claude outcome parity with `.claude/hooks/diff-review-hook.mjs`:
submitted non-empty feedback is injected into context so Claude can act on it;
Cancel, window close, empty submission and errors block the slash-command
expansion and must not start a Claude turn. This is not draft-only insertion.
The replacement must preserve these outcomes, even if the installed Claude
version requires a different supported hook event or integration mechanism.

Installed Claude versions and hook field support must be checked manually. Do not
assume that any non-zero exit status automatically blocks every agent host.
Local pi sessions may exercise the published CLI's review UI, feedback, exit
status and cancellation behaviour. They do not establish Claude hook compatibility:
Claude context injection and blocking still require verification in Claude itself.
If a thin pi adapter is useful for local testing, keep it outside the .NET core
and preserve pi's no-auto-send/draft semantics. No opencode adapter is required.
Document the narrowed integration support as a deliberate difference from the
original project. Use an explicit executable path during side-by-side testing;
CLI naming/installation must not silently replace the original `diff-review`.

### Edge and lifecycle: explicit limits

Print the forwarded URL and document an optional Windows-side command:

`msedge.exe --app="http://localhost:<forwarded-port>/#<session-token>"`

The host-side port can differ from the container port. Prefer automatic Edge app
launch through an existing approved host mechanism, with manual opening as the
fallback. Automatic host launch from a dev container is an environment capability,
not a core guarantee. No Windows executable probing, custom cross-OS process
bridge, extra runtime, browser installation, PWA manifest or service worker is
needed.

Submit and Cancel await the server acknowledgement, show a terminal state and
then attempt window.close(). Shut down the reviewer after the response completes,
and invalidate the session. Edge may leave a completed window open: do not kill
a shared Edge process to close it.

Handle Ctrl+C/SIGTERM through one cancellation/completion path, stop Git children,
and clean up owned temporary files. Hard termination cannot guarantee cleanup;
stdout remains empty until successful completion.

An Edge app does not expose a dependable native close event to the container,
regardless of whether it was launched automatically or manually. Closing the
window with X must nevertheless cancel promptly after a short reconnect grace
period, returning no review feedback and blocking the Claude command expansion.
Browser crashes and forwarding loss are also cancellation outcomes, not success.
Refresh within the reconnect grace period must not accidentally cancel a review;
restore the unfinished review using tab-scoped sessionStorage where available.
This is best-effort reload recovery, not durable draft persistence. A configurable
overall deadline must also bound sessions that never connect or whose disconnect
cannot be detected promptly.

**Required early implementation gate:** manually check close, reload and
forwarding in the actual work environment. Start with one token-protected,
held-open HTTP lifetime request, disconnect handling and a short reconnect grace
period. This is the smallest justified extra communication path: no polling,
heartbeats, SignalR or background worker initially. Verify that forwarding does
not hide or indefinitely delay disconnects; if it does, revisit the transport
before proceeding. A browser crash cannot be perfectly distinguished from a
forwarding failure; either must fail as cancellation. Do not promise exact
native-window lifecycle parity or an unverified cancellation time bound.

## 4. Practical implementation sequence

1. **Confirm environment and contract.** Check approved container .NET/runtime,
   Git, private forwarding and host Edge. Exercise launch/submit/cancel/close/
   reload early. Confirm installed Claude hook contract and timeout. Choose the
   simplest lifecycle option that meets the actual close requirement.
2. **Build the minimal host and Git path.** Add the few .NET/static files in this
   repository; keep the original application independently usable. Implement
   pinned scopes, lazy per-file
   patches, IDs/token validation and a single completion path. Add focused,
   dependency-free regression checks alongside these paths; no generic API
   infrastructure or third-party test framework.
3. **Build the slim UI.** Dark CSS, file tree/filter, split diff, old/new
   comments, edit/delete, file/overall notes, reviewed state, wrap/context toggles
   and responsive layout. Keep browser selection and scrolling usable.
4. **Connect agent output.** Preserve Markdown stdout and add JSON/file output.
   Implement the executable's hook mode for Claude. Use pi locally to exercise the
   CLI where useful, without treating that as Claude hook acceptance. Document
   browser Copy, Claude-only work integration, and the deferred Neovim/Monaco
   features; opencode migration and a dedicated pi extension are not required.
5. **Verify the standalone slim implementation.** Run the regression checks and
   walk a real repository through the manual checklist below. Port relevant
   regression scenarios from the source project into the dependency-free harness.
   Update README with actual build/run instructions and verified environment
   support. Do not modify or delete the original stack or its history. No
   unrelated container changes, package installs or new CI setup.

### Automated regression checks for implementation

Use a small .NET 10 console harness with local assertions and a non-zero exit
status on failure, referencing the relevant application code. No third-party
packages, test framework, browser automation or new CI setup is required. Keep
this separate from the reviewer launch path and document how to run it.

Cover the correctness-critical paths:

- Unified patch parsing and old/new line anchors, including additions, deletions,
  empty files, CRLF and missing final newline.
- Git comparison semantics using disposable temporary repositories where needed:
  renames, root/merge commits and committed base diffs with a dirty working tree.
- Markdown/JSON formatting, resolved paths and scope-specific comment locations.
- Submission validation, including invalid IDs, side/line ranges, size limits,
  empty feedback and duplicate terminal requests.

Retain manual acceptance for browser rendering, Edge launch, container forwarding,
close/reload behaviour and the installed Claude hook contract. Local pi workflow
checks supplement these; they do not replace the focused regression harness or
Claude verification.

### Manual acceptance checklist for implementation

- Build with the approved SDK; run the published app inside the actual container
  and Windows Edge through approved private forwarding.
- Review staged/unstaged/untracked changes, base-branch changes, rename/delete,
  root and merge commits, history and all-files scopes. Verify branch diff
  contents match its committed file list even when the working tree is dirty.
- Check old/new line anchors, empty files, CRLF, no final newline, Unicode and
  awkward filenames; binary/large/symlink omissions remain clear.
- Add/edit/delete inline and file comments; change scopes and return; preserve
  notes, reviewed markers, scroll, filtering, wrapping and context expansion.
- Submit via CLI/Claude; verify correct Markdown and JSON locations, exit status,
  clean stdout, once-only submission, output failures and session isolation.
- Cancel, submit empty, close with X, reload, lose forwarding, Ctrl+C and terminate
  the process. No feedback may be mistaken for a successful submission.
- Reject wrong tokens, foreign origins/hosts, unknown IDs, traversal/symlink reads,
  oversized bodies and duplicate terminal requests. Verify no external requests.
- Record startup latency, container process memory, Edge process memory and idle
  CPU on representative small/large reviews. No guaranteed RAM figure: Edge is
  still a multiprocess browser, and app mode mainly changes its window chrome.

The first implementation now uses normal build checks and the dependency-free
regression harness described above. The installed Edge was exercised locally;
remaining manual acceptance is explicitly listed in README. Do not treat these
local checks as work-container or Claude-hook acceptance.

## 5. Recommendation and difficult parity

ASP.NET Core plus existing Edge is the simplest viable .NET solution that
supports interactive submission. A static HTML export could display a diff but
would complicate returning feedback and managing the review lifecycle.

The main work is replacing Monaco's rendering while keeping old/new comment
anchors correct. Core line-level review is practical without dependencies.
Rich syntax/intraline highlighting, minimap/folding, native window control,
automatic container-to-Windows opening and terminal/editor orchestration are
the expensive parity items. Keep them simplified or explicitly deferred.

Zero application packages reduces the approval surface; it does not establish
corporate approval. Validate the executable, existing prerequisites and forwarding
against the organisation's actual process.

## Open decisions and environment gates

- First usable release: all intended review scopes versus working-tree/base first.
- Actual dev-container port forwarding and approved Windows host-launch mechanism.
- Installed Claude version, supported hook event/fields and blocking semantics.
- Working-tree capture/change-detection details and explicit behaviour at the
  session memory cap; do not silently change contents beneath comment anchors.
- Installation/publishing workflow for work use. The executable name is now
  `diff-review-slim`; do not overwrite the original `diff-review` installation.

## References

The paths below refer to the original repository, not files included here:

Repository evidence: `package.json`, `src/bindings/cli.ts`,
`src/core/git/review-commands.ts`, `contents.ts`, `parse.ts`,
`src/core/window/orchestrator.ts`, `src/core/prompt.ts`,
`src/platform/wsl-glimpse.ts`, `web/app.js`, `web/index.html`,
`web/monaco-review-editor.js`, `web/sidebar-renderer.js`,
`web/data-access.js`, `web/modal.js`, `src/core/editor.ts`,
and `.claude/` at the inspected commit. No repository AGENTS.md or dev-container
configuration was present in that tree.

- [Claude Code hook reference](https://code.claude.com/docs/en/hooks)
- [Browser window.close restrictions](https://developer.mozilla.org/en-US/docs/Web/API/Window/close)
- [Browser pagehide reliability](https://developer.mozilla.org/en-US/docs/Web/API/Window/pagehide_event)
