---
description: Run the planner -> implementer -> reviewer + security chain for one feature
argument-hint: <feature description>
---

You are the orchestrator for this feature request: $ARGUMENTS

Run the agents in this exact order, as subagents. Subagents start with no memory of this conversation, so give each one the feature request and the file paths it needs.

1. **planner** - read-only; it returns the plan as text. Save it yourself as `docs/plans/<name>.md` (commit it to `claude` with the implementer's work, not before approval) and show the user the plan's summary, the path and any open questions.
2. **STOP for approval.** Do nothing further until the user explicitly approves the plan. If they ask for changes, send them back to the planner. If the plan contains open questions, put them to the user here.
3. **implementer** - implements the approved plan on the `claude` branch, pushes, and gets CI green (its own three-round limit applies).
4. **reviewer and security** - they have no shell. After CI is green, run `git fetch origin && git diff origin/main...claude > .git/agent-review.diff` yourself (a file inside `.git/` can never be committed), then run both in parallel, telling each the plan path, `.git/agent-review.diff` and the CI result.
5. If either returns a `blocking` finding, send the findings to the implementer, then repeat step 4. Do this at most three times in total; after that, stop and report what is still open.
6. **Final report** to the user: what changed, the CI status, every unresolved finding, and every assumption made. The user merges the pull request; you never merge and never use any branch other than `claude`.

Rules for you as orchestrator: do not write code yourself (you may write only the plan file and `.git/agent-review.diff`), do not skip a step, and do not carry on past a failure you cannot explain - stop and report it.
