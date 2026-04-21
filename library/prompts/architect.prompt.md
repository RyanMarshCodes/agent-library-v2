---
mode: ask
model: GPT-5.3-Codex
description: Create architecture decisions and implementation task graph
---

/architect from this validated spec:

${input:validated_spec:Paste validated spec or summary}

Return:
- Architecture decisions + rationale
- Impacted components/contracts
- Task breakdown and dependencies (DAG)
- Test expectations per task

Then recommend /validate.
