---
description: Open the slim browser diff reviewer and send submitted feedback to Claude. Optional: /diff-review <base-branch>
disable-model-invocation: true
argument-hint: [base-branch]
---

Address the code review feedback injected into context by the `/diff-review` hook. For each comment:

- Apply the requested change to the codebase, or
- Explain why you disagree with the point.

Work through every comment in the feedback. Do not edit files outside the scope of the review.
If no review feedback was injected, stop without editing files and report that the review hook did not supply feedback.
