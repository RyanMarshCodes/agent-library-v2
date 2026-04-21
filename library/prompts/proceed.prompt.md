---
mode: ask
model: GPT-5.3-Codex
description: Run end-to-end ADLC-compatible pipeline with validation gates
---

Run /proceed for this target:

${input:target:Feature request or REQ id}

Execution gates:
1. /context
2. /spec
3. /validate (spec)
4. /architect
5. /validate (architecture/tasks)
6. /implement
7. /test
8. /reflect
9. /review
10. /commit
11. /wrapup

Rules:
- Do not advance while blockers exist.
- Re-run /validate after blocker fixes.
- Stop after 3 failed validation loops and report unresolved blockers.
