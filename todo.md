# Outstanding work

Current usage and verification commands are in [README.md](README.md).
Only unfinished work belongs here; Git history preserves the implementation plan.

## Manual browser and pi acceptance

- [ ] Verify Copy feedback in Edge and Chrome after the server-preview change.
- [ ] Verify native app launch, Cancel, close, reload and completed-window behaviour.
- [ ] Verify pi draft insertion without auto-send, Escape cancellation and
  preservation of an existing editor draft. Browser Cancel/close must add nothing.
- [ ] Run symlink/reparse rejection checks on a machine allowed to create links.
- [ ] Check that `docs/review-window.png` matches the current plain-text UI.

## Claude integration — after local pi acceptance

- [ ] Verify the installed Claude version and hook contract; do not copy old settings.
- [ ] Implement and document `hook` mode and supported context injection.
- [ ] Verify non-empty feedback reaches Claude, while Cancel, close, empty feedback
  and errors block command expansion without starting a model turn.
- [ ] Align the reviewer deadline with the configured hook timeout.

## Work-container and platform acceptance

- [ ] Confirm .NET 10 SDK/runtime availability and workplace approval.
- [ ] Verify Linux/WSL execution and private container-to-Windows forwarding,
  including different host/container ports.
- [ ] Verify prompt close/crash/forwarding-loss cancellation, reload grace and
  draft recovery through the actual forwarding setup.
- [ ] Verify an approved best-effort host-browser launch mechanism, retaining
  the manual URL fallback.

## Undecided scope — not a first-release commitment

- [ ] Decide whether last-commit, history and all-files views are required.
  If approved, include root commits, labelled first-parent merge comparisons,
  lazy history loading and scope/commit-specific comment anchors and review state.
