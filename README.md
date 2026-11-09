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

The reviewer never invokes an LLM and does not change repository files. Only an
explicit caller-provided `--out` destination is written on submission.

## Prerequisites

- Existing **.NET 10 SDK** to build; **ASP.NET Core 10 runtime** to run the
  framework-dependent published application.
- Git on PATH.
- An existing Edge or Chrome browser.

There are **no third-party NuGet packages, npm dependencies or runtime downloads**.
`NuGet.Config` clears package feeds; framework references use the installed SDK.
Nothing installs or upgrades the work container or browser. Approval and .NET 10
availability in the actual work environment still need verification.

## Build and check

From this repository:

```sh
dotnet build AgentDiffReview.csproj -c Release
dotnet run --project checks/Checks.csproj -c Release
dotnet publish AgentDiffReview.csproj -c Release -o artifacts/publish
```

The checks are a small .NET console harness, without a test framework or new CI
setup. They use disposable repositories and run real HTTP/process-level checks.
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
commits are supported.

For a **committed branch comparison**, use the merge base of a local/origin ref:

```sh
dotnet ./artifacts/publish/diff-review-slim.dll open --repo E:/repos/your-project --base main
```

The positional `open main` form is also supported. Both sides are pinned commits;
dirty working-tree contents and untracked files are excluded. No fetch is run.

### Feedback contract

- Finish with non-empty feedback: Markdown to stdout, exit `0`.
- Cancel, close/disconnect, empty submission, timeout or error: no feedback, exit
  non-zero.
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

## Browser review

- Collapsible file tree with a subsequence/fuzzy path filter; Escape clears it.
- Split old/new diff, stacked on narrow windows. Click a line number to comment.
- Inline/file comments with immediate editing and deletion, and an overall note.
- Reviewed markers, comment counts, wrapping and expandable unchanged context.
- Browser Copy feedback action; no cross-OS clipboard helper.
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
seconds; `--timeout-seconds` configures 1–3600 seconds. Closing before the UI ever
connects cannot be detected this way; terminal cancellation/deadline remains the
fallback. Exact native-window lifecycle parity is not promised.

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
- Git child output/time is bounded; external diff/textconv and fsmonitor execution
  are disabled. Cancellation terminates active Git process trees.
- Files are capped at 1,000,000 bytes per side and diffs at 20,000 rows. Binary,
  non-UTF-8, symlink/submodule, unsupported and oversized entries get omission
  messages, not misleading blank diffs. Unmerged files are currently omitted.
- At most 10,000 indexed files; an estimated 32 MB retained-row budget. This is
  not a guarantee of total .NET/browser process memory. Loaded data is not evicted
  beneath comments; hitting the budget requires a smaller/new review.
- Captured bytes are briefly written to owned temporary files for Git's diff
  calculation, then removed. Hard termination cannot guarantee cleanup.

## Verification and next work

Verified locally on Windows with the installed .NET 10 SDK:

- Build/publish, dependency-free regression harness and JavaScript syntax check.
- Real HTTP submit/cancel/empty/deadline/disconnect/reconnect behaviour and clean
  stdout, plus token/Host/Origin/body-limit rejection.
- Installed Edge headless smoke: diff rendering, inline comment, overall note,
  reviewed marker, draft restoration after refresh and successful submission.

Native Edge app launch/close interaction, Chrome, Linux/WSL and actual work-container
forwarding are not yet manually verified. This Windows account cannot create
filesystem symlinks, so that regression scenario is explicitly skipped here.

Next gates:

1. Verify the installed Claude hook contract; implement supported context injection
   and blocking in `hook` mode. Do not copy the original hook settings blindly.
2. Prove private forwarding, best-effort host launch and prompt close cancellation
   in the actual work dev container.
3. Decide first-release scope priority and add last-commit/history/all-files views.

pi can exercise the published CLI locally. A dedicated pi adapter is optional;
pi testing does not prove Claude context injection or cancellation blocking.
opencode migration is out of scope.

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
