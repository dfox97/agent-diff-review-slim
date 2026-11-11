# Outstanding work

Current usage and verification commands are in [README.md](README.md).
Only unfinished work belongs here; Git history preserves the implementation plan.

## Manual browser acceptance

- [ ] Verify Copy feedback in Edge and Chrome after the server-preview change.
- [ ] Verify native app launch, Cancel, close, reload and completed-window behaviour.
- [ ] Run symlink/reparse rejection checks on a machine allowed to create links.
- [ ] Check that `docs/review-window.png` matches the current plain-text UI.

## Live Claude acceptance

The adapter matches the original repo's `UserPromptExpansion` contract. Protocol
and reviewer checks are automated; Claude is not installed on the development PC.

- [ ] Confirm the workplace Claude version supports `UserPromptExpansion` and its
  context/block response contract before enabling the hook.
- [ ] Verify non-empty feedback reaches Claude, while Cancel, close, empty feedback
  and errors block command expansion without starting a model turn.
- [ ] Verify the configured 600-second hook timeout leaves room for the reviewer's
  at-most-570-second deadline in the actual Claude environment.

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
