---
description: Re-learn the project state (rulings, tasks, designs, commits) and report a RELEARNED brief
---

Re-learn the project state, exactly as after a context compaction, then report.

1. Run `bash scripts/session-brief.sh` and read all of its output.
2. Read `docs/RULINGS.md` in full and `docs/TASKS.md` in full.
3. Read the design doc(s) in `docs/design/` that own the open tasks (at least their
   status, proposals and as-built sections; the full doc if you will change that area).
4. Run `git log --oneline -15` and `git status --short` to confirm where work stopped.
5. Reply with the `RELEARNED` block the script prints, filled in, then answer whatever the
   user asked in the same reply. If a line cannot be filled, say so instead of guessing.
