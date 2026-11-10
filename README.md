# Agent Diff Review Slim

![The review window: file tree with a reviewed marker and comment counts, a split
old/new C# diff with a new-side inline comment, and the overall review note](docs/review-window.png)

*Working-tree review of a sample repository in Edge. The sidebar shows reviewed
files, status letters and per-file comment counts; clicking a line number opens a
comment on that old/new line. Feedback is submitted from the toolbar and never
returns to the browser.*

A lightweight, human-operated Git review tool built with .NET 10 and an existing
Edge or Chrome browser. Intended for Claude Code sessions in dev containers, with
Windows and WSL/Docker development environments.

**Status: early working CLI/UI slice, not yet a verified Claude integration.**
Working-tree and committed base comparisons are implemented. Claude hook mode,
history/last-commit/all-files scopes and production dev-container verification
are still pending. The original application remains independently usable.

The reviewer never invokes an LLM or edits source files. It writes short-lived
temporary files to calculate diffs and, on submission, an explicit caller-provided
`--out` destination if requested. Git automatic fetching is disabled.

## Prerequisites

- Existing **.NET 10 SDK** to build; **ASP.NET Core 10 runtime** to run the
  framework-dependent published application.
- Approved Git on PATH, with support for `--no-lazy-fetch` (older versions fail closed).
- An existing Edge or Chrome browser.

There are **no third-party NuGet packages, npm dependencies or runtime downloads**.
`NuGet.Config` clears package feeds; framework references use the installed SDK.
Nothing installs or upgrades the work container or browser. Approval and .NET 10
availability in the actual work environment still need verification.

## Build and check

From this repository:

```sh
dotnet build AgentDiffReview.csproj -c Release
dotnet run --file checks/Program.cs -c Release
dotnet publish AgentDiffReview.csproj -c Release -o artifacts/publish
```

Publish into a fresh output directory for deployment; publishing over an older
release does not remove obsolete files, and a leftover asset with an unknown
extension now fails startup instead of being ignored.

The checks are a small .NET console harness — a file-based app that references the
main project, so there is no second project file — without a test framework or new
CI setup. They use disposable repositories and run real HTTP/process-level checks.
Symlink checks are reported as skipped if the OS denies link creation.

Build/publish separately from review launches: build output is not agent feedback.

## Start a review

Run the published application, pointing it at the checkout to review:

```sh
dotnet ./artifacts/publish/diff-review-slim.dll open --repo E:/repos/your-project
```

On Windows, launch tries Edge app mode through the installed `msedge.exe` shell
registration. `--browser chrome` tries Chrome instead; `--no-open` leaves launch
to you. Launch failure prints a manual URL rather than aborting the review. There
is no browser probing, installation or custom Windows/WSL process bridge.

Working-tree review compares pinned HEAD contents to the working tree, combining
staged/unstaged net changes and including untracked files. Repositories without
commits are supported. A staged deletion with a recreated working file includes
the replacement content. Unresolved index conflicts are explicitly omitted.

For safety, working-tree review refuses tracked files assigned to a configured
Git `filter.*.clean` or `filter.*.process` helper, even if those files are unchanged.
Attributes are inspected without running the helpers. Unused global registrations
(such as Git for Windows' default LFS configuration) are allowed. For a checkout
that uses these helpers, use a committed `--base` comparison or an approved
checkout without them; do not remove required company Git configuration just to
bypass this check.

For a **committed branch comparison**, use the merge base of a local/origin ref:

```sh
dotnet ./artifacts/publish/diff-review-slim.dll open --repo E:/repos/your-project --base main
```

The positional `open main` form is also supported. Both sides are pinned commits;
dirty working-tree contents and untracked files are excluded. No fetch is run,
including Git's implicit lazy fetches in partial clones. Missing local content is
reported as unavailable; obtain it separately through your approved Git workflow.

### Feedback contract

- Finish with non-empty feedback: Markdown to stdout, exit `0`.
- Cancel, close/disconnect, empty submission, timeout or error: exit non-zero.
  No feedback is intentionally emitted before submission. An interrupted/failed
  output write can leave partial stdout; discard it unless the command exits `0`.
- URLs and diagnostics go to stderr; hosting/request logs are disabled.
- The browser waits for acknowledgement and shows a terminal state. Browsers may
  refuse `window.close()`; a completed window can be closed manually.
- Failed/lost acknowledgements are not grounds to assume success or resend:
  inspect the command outcome. A session accepts only one terminal action.

Optional versioned JSON and atomic file output:

```sh
dotnet ./artifacts/publish/diff-review-slim.dll open --repo E:/repos/your-project --format json --out E:/temp/review.json
```

`--out` writes **instead of stdout**; its parent directory must exist. The output
includes repository/base/HEAD identity, resolved old/new paths, side and line
ranges, and captured-content hashes where available. Unique temporary files in
the destination directory are renamed into place after a complete write.

See `--help` for all options. This executable is named **`diff-review-slim`**, so
it does not replace an existing `diff-review` installation.

## Test locally through pi

`integrations/pi.ts` is an optional host-owned adapter, not a dependency of the
.NET app. It uses the existing pi installation and its TypeScript loader; no npm
manifest, packages, global pi configuration or SDK dependencies are added.

Publish slim first, then start a **new pi session** in the checkout you want to
review. For this repository:

```sh
cd E:/repos/agent-diff-review-slim
pi --extension ./integrations/pi.ts
```

In pi, run:

```text
/diff-review-slim
/diff-review-slim main
```

Use one command at a time; the optional argument is a base ref. The distinct
command name avoids collision with the original `/diff-review`. From another
checkout, pass the absolute path to this extension; it reviews pi's working
folder, not the extension's source directory.

- Finish with feedback: insert it into pi's editor as a **draft**, appending to
  existing editor text. Nothing is automatically sent or added to model context.
- Cancel, empty feedback, window disconnect or CLI error: leave the editor alone.
- In the terminal waiting screen, **Escape** cancels the owned reviewer. Browser
  Cancel also works. `/diff-review-slim-cancel` is available to RPC clients.
- Session shutdown/reload cancels the reviewer. Cancellation never kills a shared
  Edge/Chrome process; an abandoned browser window may need closing manually.
- The private fallback URL appears in pi's UI, not model messages. RPC clients
  receive it as a UI record and must not log/share it.

The default DLL is `artifacts/publish/diff-review-slim.dll` relative to this repo.
Set `DIFF_REVIEW_SLIM_DLL` to use another compatible published DLL. Set
`DIFF_REVIEW_SLIM_NO_OPEN=1` for automated/manual-URL testing without launching a
browser. Do not use the adapter while pi is running model work; it requires idle.

Optional reproducible integration checks, using existing Python 3 and pi:

```sh
python checks/pi-smoke.py
```

This loads the actual extension in real pi RPC mode, uses an isolated offline
agent directory and disposable checkout, and verifies submit/cancel/empty/close,
pi-side cancellation, duplicate-review rejection, CLI failure and shutdown
cleanup. It requires no model turn, observes an empty/non-streaming conversation,
and keeps private URLs in memory. `--pi` and `--dll` override the executable/DLL.
The normal .NET regression harness does not require Python or pi.

Verified with pi 1.4.0 on this Windows machine. The interactive terminal waiting
screen, Escape key and preservation of an existing TUI draft still need hands-on
acceptance. **pi testing does not establish Claude hook compatibility.**

## Browser review

- Collapsible file tree with a subsequence/fuzzy path filter; Escape clears it.
- Plain-text split old/new diff, stacked on narrow windows. Click a line number
  to comment. Source is rendered with `textContent`, not HTML or a syntax parser.
- Inline/file comments with immediate editing and deletion, and an overall note.
- Reviewed markers, comment counts, wrapping and expandable unchanged context.
- Browser Copy feedback action, formatted by the server so it matches final
  stdout exactly; no cross-OS clipboard helper.
- Tab-scoped refresh recovery for comments, notes, selection and reviewed markers.

Refresh recovery uses bounded `sessionStorage` for the token and draft, not
repository contents. Browser storage may be disk-backed. It is not durable draft
storage: process exit or session expiry cannot be recovered, and unavailable
browser storage disables reload recovery with a visible warning.

Files freeze on first load, including across browser reloads. Restart the reviewer
to review newer changes; the tool does not lock Git. Changes in a working file's
size/mtime between indexing and capture produce an explicit omission rather than
silently moving comment anchors. This is not an atomic snapshot of the checkout.

## Dev containers and WSL

Run the .NET process **beside the checkout and Git**, then open its UI on the
Windows host through approved private port forwarding. For example, after
configuring private forwarding for container port 43127:

```sh
dotnet /path/to/publish/diff-review-slim.dll open \
  --repo /workspace/your-project --port 43127 \
  --public-url http://localhost:43127/ --no-open
```

The host-forwarded port may differ from the container port; use that host port in
`--public-url`. The URL must be an HTTP loopback URL, with no path/query/fragment.
Default binding is `127.0.0.1` with an OS-selected free port. If the approved
forwarding setup requires binding to all container interfaces, explicitly add
`--listen 0.0.0.0` and ensure the **Windows-facing port remains private/loopback**.
Never publish the reviewer to the LAN or a public tunnel.

For a dedicated host window, use the complete URL printed on stderr:

```text
msedge.exe --app="http://localhost:<host-port>/#<session-token>"
```

Automatic container-to-host opening is not configured yet. No environment
capability is inferred from the successful local Windows checks.

### Window lifecycle

A token-protected held-open HTTP request tracks the browser. Disconnect cancels
after a reconnect grace period (default 5 seconds); reload within that period
preserves the session. Browser crashes and forwarding loss are cancellation
outcomes, not successful submissions. There is no heartbeat or polling loop.

`--grace-seconds` configures 1–30 seconds. An overall deadline defaults to 570
seconds; `--timeout-seconds` configures 1–3600 seconds. The deadline also bounds
submission and its acknowledgement, including a blocked stdout consumer. A
submission is not successful until output writing completes. OS writes cannot
always be interrupted immediately; forced exit can leave partial stdout or a
temporary file. Treat feedback as valid only after exit `0`.

Closing before the UI ever connects cannot be detected this way; terminal
cancellation/deadline remains the fallback. Exact native-window lifecycle parity
is not promised.

**Verify close/reload/forwarding in the actual work container.** A proxy can hide
or delay disconnects; local checks do not prove prompt cancellation through it.

## Security and limits

- Random per-review token; fragment removed after reading, token required in an
  API header. Keep the printed launch URL private.
- Host/Origin validation, no CORS, JSON-only mutations, restrictive CSP,
  no-referrer policy, no-store responses and bounded bodies/comments/output.
- Only three fixed browser assets are served; never the checkout or `.git`.
- Browser file IDs resolve through a server-owned index. The browser cannot pass
  repository paths, commands or arbitrary revisions.
- Working reads reject traversal, symlinks and ancestor reparse points. This is
  intended for an ordinary trusted developer checkout, not a sandbox against a
  malicious local process concurrently swapping filesystem entries.
- Git child output/time is bounded; external diff/textconv, fsmonitor and lazy
  fetching are disabled. Working-tree review refuses clean/process filters
  assigned to tracked files; dirty submodule inspection is skipped. Cancellation
  terminates active Git process trees. Git/configuration/PATH must remain trusted and stable
  during review; this is not a sandbox for hostile checkouts or local processes.
- Files are capped at 1,000,000 bytes per side and diffs at 20,000 rows. Binary,
  non-UTF-8, symlink/submodule, unsupported and oversized entries get omission
  messages, not misleading blank diffs. Unmerged files are currently omitted.
- At most 10,000 indexed files; an estimated 32 MB retained-row budget. This is
  not a guarantee of total .NET/browser process memory. Loaded data is not evicted
  beneath comments; hitting the budget requires a smaller/new review.
- Captured bytes are briefly written to owned temporary files for Git's diff
  calculation, then removed. This byte comparison isolates repository/global Git
  configuration and attributes so it cannot invoke their conversion helpers.
  Hard termination cannot guarantee cleanup.

### Workplace approval checklist

No third-party packages does not mean no dependencies or automatic compliance.
Use approved, patched versions of .NET/ASP.NET Core, Git and the browser. Before
using proprietary code, confirm that your workplace permits:

- Temporary source copies (hard termination cannot guarantee cleanup).
- Browser-memory source viewing and potentially disk-backed `sessionStorage`
  containing notes and the session token.
- Clipboard use and the extensions/profile of the browser you open.
- Private local port forwarding, if needed; never a public tunnel or LAN binding.
- Any feedback ultimately sent to Claude through its approved work configuration.

Launch URLs are credentials. Do not put them in shared logs, tickets or model
transcripts. The optional pi adapter is not required for a work deployment.
Claude hook integration remains unimplemented; standalone review does not prove
compatibility or approval.

## Verification and next work

Verified locally on Windows with the installed .NET 10 SDK:

- Build/publish, dependency-free regression harness and JavaScript syntax check.
- Real HTTP submit/cancel/empty/deadline/disconnect/reconnect behaviour and clean
  stdout, plus token/Host/Origin/body-limit rejection.
- Installed Edge headless smoke: diff rendering, inline comment, overall note,
  reviewed marker, draft restoration after refresh and successful submission.
- Real pi 1.4.0 RPC integration: draft-only insertion, cancellation/empty/error
  outcomes, disconnect and shutdown cleanup without agent/model work.

Native Edge app launch/close interaction, Chrome, Linux/WSL and actual work-container
forwarding are not yet manually verified. This Windows account cannot create
filesystem symlinks, so that regression scenario is explicitly skipped here.

Next gates:

1. Verify the installed Claude hook contract; implement supported context injection
   and blocking in `hook` mode. Do not copy the original hook settings blindly.
2. Prove private forwarding, best-effort host launch and prompt close cancellation
   in the actual work dev container.
3. Decide first-release scope priority and add last-commit/history/all-files views.

The optional pi adapter above exercises the published CLI locally; it is not a
required work integration and does not prove Claude context injection or
cancellation blocking. opencode migration is out of scope.

See [REFACTOR-PLAN.md](REFACTOR-PLAN.md) for the broader target and design decisions.

## Relationship and licence

This is a separate work-focused implementation of
[Agent Diff Review](https://github.com/dfox97/agent-diff-review), with fresh Git
history. It does not replace, delete or import that project's history.

Reference upstream commit:
[`7d9295b63840e8310ef6d2e829320ed6577ff537`](https://github.com/dfox97/agent-diff-review/tree/7d9295b63840e8310ef6d2e829320ed6577ff537).
The plan also records decisions made during the subsequent design session.

[MIT](LICENSE). Original copyright and attribution are retained. Preserve
applicable licence notices for any code reused during implementation.
