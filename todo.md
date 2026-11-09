# TODO

The first CLI/UI slice is merged to `main` (PR #1). Browser review, working-tree
and committed base comparisons, dependency-free syntax colouring, and the
optional pi adapter are in place. It is usable locally, but is not yet a
drop-in replacement for the work Claude hook.

Nothing here is agreed priority beyond section 1; read `REFACTOR-PLAN.md` before
changing scope. Two invariants are easy to break:

- **Every browser asset must be allowlisted in `Program.cs`.** Assets are served
  by an explicit map, not `UseStaticFiles`. `/highlight.js` once 404ed this way
  and the diff rendered blank rows. The regression harness now fails if a
  published `wwwroot` file is unreachable — keep it that way.
- **Highlighted rows are HTML.** The highlighter escapes its own output; any new
  branch that injects row text into `innerHTML` must escape first, and must fall
  back to plain text whenever the highlighter is missing.

## Local pi test before Claude

- [x] Add an optional `/diff-review-slim` adapter around the published CLI, without
  adding pi dependencies to the .NET app or changing global pi settings.
- [x] Test real pi 1.4.0 RPC draft insertion with no auto-send or model turn.
- [x] Verify Cancel, empty feedback, disconnect, pi-side cancellation, duplicate
  review rejection, CLI errors and shutdown cleanup through real pi RPC.
- [ ] Manually run `/diff-review-slim` in pi's terminal UI and verify feedback
  appears as an editable draft without sending it.
- [ ] Manually verify Escape cancellation and preservation of any existing TUI
  editor draft; browser Cancel/close must not insert feedback.

## 1. Claude integration — next priority after the local pi test

- [ ] Verify the installed Claude version and supported hook event/fields; do not
  blindly copy the original hook configuration.
- [ ] Implement `hook` mode and document its Claude configuration.
- [ ] Verify that successful, non-empty feedback is injected into context so
  Claude can act on it.
- [ ] Verify that Cancel, window close, empty submission and errors block the
  command expansion without starting a Claude turn.
- [ ] Align the review deadline with the configured Claude hook timeout.

## 2. Actual work dev-container verification

- [ ] Confirm .NET 10 SDK/runtime availability and approval in the work container.
- [ ] Verify private container-to-Windows port forwarding, including different
  host/container ports where applicable.
- [ ] Verify best-effort automatic Edge app launch through an approved host
  mechanism, with a manual URL fallback.
- [ ] Verify that closing the window cancels promptly through real forwarding.
- [ ] Verify refresh/reconnect grace and draft restoration without cancellation.
- [ ] Verify that browser crashes and forwarding loss cancel without feedback.

## 3. Remaining review scopes

- [ ] Decide whether last-commit/history/all-files scopes are required for the
  first usable release or can follow afterward; this priority is not yet agreed.
- [ ] Add last-commit review, including root and labelled first-parent merge
  comparisons.
- [ ] Add selectable commit history with lazy per-commit file loading.
- [ ] Add a read-only all-files view.
- [ ] Preserve scope/commit-specific comment anchors and review state when
  switching between these views.

## 4. Syntax colouring follow-ups (`wwwroot/highlight.js`)

Shipped and verified: escaping round-trip on adversarial rows, CRLF rows, 4000
C# rows in ~44 ms, and real-browser colours for C#, CSS and JSON. Remaining
gaps are known and deliberate, not accidental:

- [ ] No automated per-language assertions in the .NET harness. Coverage today is
  ad-hoc Node checks plus a browser probe; add a fixture check (row text +
  language -> expected classes) so changes are guarded. The escaping invariant is
  the priority, since `app.js` assigns these strings to `innerHTML`.
- [ ] Language map is approximate where it reuses another spec: `fs`/`vb` use the
  C# rules, `ps1` uses the shell rules, `toml`/`ini` use the YAML rules, `jsx`/
  `tsx` use the JS rules. Either widen the specs or document the approximation
  in the README.
- [ ] Not covered at all and rendered as plain text: Java, Kotlin, Go, Rust,
  Ruby, C/C++, PHP, Swift, Terraform/HCL.
- [ ] C# interpolated strings are coloured as one string; `{expression}` holes are
  not sub-highlighted. Acceptable for review reading; do not build a parser.
- [ ] Token colours target a dark background only (VS Code Dark Modern values in
  `style.css`). A light theme, or honouring `prefers-color-scheme`, is unbuilt.
- [ ] Files over 512 KB, and files whose extension is unknown, deliberately fall
  back to uncoloured text; confirm that reads acceptably on a large real diff.
- [ ] The window/UI screenshot was re-captured after this feature; re-capture it
  again whenever the diff rows or toolbar change, or the README drifts.
- [ ] Decide whether to commit the local capture helper: it currently lives in the
  gitignored `artifacts/screenshot-build/` (Node built-ins, drives headless Edge
  over CDP). `checks/screenshot.mjs` with a README note would fit the existing
  optional-script pattern.

## 5. Platform acceptance

- [ ] Manually verify native Edge app launch, close and completed-window behaviour.
- [ ] Verify Chrome, including the review and clipboard workflows.
- [ ] Verify Linux/WSL execution and Windows host-browser access.
- [ ] Run ancestor/final-component symlink rejection checks on a machine with
  link-creation permission; the current Windows account skips this scenario.

## Current verification baseline

- User confirmed that local launch via the documented `dotnet` command works
  and the UI looks good. This does not yet establish manual close/completion
  behaviour or Claude/dev-container compatibility.
- 15 regression checks passed, 0 failed, 1 symlink scenario skipped. The HTTP
  check now also asserts that every published `wwwroot` asset is served, and was
  confirmed to fail when `/highlight.js` was removed from the allowlist.
- Release build/publish and JavaScript syntax checks passed.
- Syntax colouring: escaping round-trip and CRLF checks passed in Node; a headless
  Edge probe confirmed served assets, 74 token spans and `#569cd6` keyword
  colour. Colours were not eyeballed beyond one C#/CSS/JSON sample.
- Installed Edge headless smoke passed: rendering, inline comments, overall note,
  reviewed state, refresh recovery and successful submission with clean stdout.
- Real pi 1.4.0 RPC smoke passed with the optional adapter: draft insertion,
  cancellation/empty/error outcomes and cleanup, with no conversation messages
  or agent/model turn. Interactive terminal acceptance is still pending.
- pi testing does not establish Claude hook compatibility. Claude is not on this
  machine's PATH; verify its actual installed work version before configuring
  hooks. The pi adapter remains optional; opencode is out of scope.
- The original `agent-diff-review` repository is untouched and still usable; this
  repository is a separate implementation with its own history.

## Handoff: how to verify the current state

Requirements: .NET 10 SDK/runtime, Git, an installed Edge or Chrome. No packages,
no `npm install`, nothing global is modified by the app or these checks.

```sh
dotnet build AgentDiffReview.csproj -c Release      # TreatWarningsAsErrors is on
dotnet run --project checks/Checks.csproj -c Release # 15 pass, 1 symlink skip
dotnet publish AgentDiffReview.csproj -c Release -o artifacts/publish
```

Then either drive it by hand:

```sh
dotnet ./artifacts/publish/diff-review-slim.dll open --repo <checkout> [--base main]
```

or through pi (publish first, new session, idle agent):

```sh
pi --extension ./integrations/pi.ts     # then /diff-review-slim [base]
python checks/pi-smoke.py               # optional, needs existing Python 3 + pi
```

Where the work actually stands:

- `main` is the integration point; the old `feat/first-review-slice` branch is
  merged. Keep the app buildable and the checks green before pushing.
- Start with section 1 (Claude). Sections 2 and 3 are blocked on the real work
  container and on a scope decision that is not yet agreed; do not silently drop
  history/all-files scopes.
- The `--out`/JSON contract, exit codes and the "no LLM, read-only, one terminal
  action" guarantees are load-bearing for callers; treat changes to them as
  breaking.
- Screenshot regeneration is manual today; if the diff rows or toolbar change,
  re-capture `docs/review-window.png` or the README lies.
