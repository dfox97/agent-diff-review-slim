# TODO

The CLI/UI slice and safety cleanup are merged to `main` (PRs #1 and #2).
Working-tree and committed base comparisons, plain-text browser review, and the
optional pi adapter are in place. It is usable locally, but is not yet a
drop-in replacement for the work Claude hook.

Nothing here is agreed priority beyond section 1; read `REFACTOR-PLAN.md` before
changing scope. Two invariants are easy to break:

- **Every browser asset must be allowlisted in `Program.cs`.** The current assets
  are `index.html`, `app.js` and `style.css`, served by an explicit map rather
  than `UseStaticFiles`. The existing harness checks published asset reachability.
- **Source rows use `textContent`.** Keep repository content as text rather than
  injecting it into `innerHTML`. The custom syntax highlighter was removed in
  PR #2 to simplify rendering and maintenance.

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

## 4. Platform acceptance

- [ ] Manually verify native Edge app launch, close and completed-window behaviour.
- [ ] Verify Chrome, including the review and clipboard workflows.
- [ ] Verify Linux/WSL execution and Windows host-browser access.
- [ ] Run ancestor/final-component symlink rejection checks on a machine with
  link-creation permission; the current Windows account skips this scenario.

## Previously recorded verification

These results were recorded before the safety/rendering cleanup in PR #2. They
are historical evidence, not verification of the current revision. Rerun the
relevant checks when validating a release.

- User confirmed that local launch via the documented `dotnet` command works
  and the UI looks good. This does not yet establish manual close/completion
  behaviour or Claude/dev-container compatibility.
- The regression harness previously reported 15 passed, 0 failed and 1 symlink
  scenario skipped. Its HTTP check covers published browser-asset reachability.
- Release build/publish and JavaScript syntax checks passed.
- Installed Edge headless smoke passed: rendering, inline comments, overall note,
  reviewed state, refresh recovery and successful submission with clean stdout.
- Real pi 1.4.0 RPC smoke passed with the optional adapter: draft insertion,
  cancellation/empty/error outcomes and cleanup, with no conversation messages
  or agent/model turn. Interactive terminal acceptance is still pending.
- pi testing does not establish Claude hook compatibility. Claude was unavailable
  on the recorded Windows machine's PATH; verify the actual installed work
  version before configuring hooks. The pi adapter remains optional; opencode
  is out of scope.
- The original `agent-diff-review` repository is untouched and still usable; this
  repository is a separate implementation with its own history.

## Handoff: how to verify the current state

Requirements: .NET 10 SDK/runtime, Git, an installed Edge or Chrome. No packages,
no `npm install`, nothing global is modified by the app or these checks.

```sh
dotnet build AgentDiffReview.csproj -c Release      # TreatWarningsAsErrors is on
dotnet run --project checks/Checks.csproj -c Release
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
- Screenshot regeneration is manual. Verify that `docs/review-window.png` matches
  the current plain-text UI and refresh it if needed.
