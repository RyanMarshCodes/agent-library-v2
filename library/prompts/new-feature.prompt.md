---
mode: ask
model: GPT-5.3-Codex
description: Start spec-based feature workflow from a feature description
---

Start the spec-based feature workflow for this request:

${input:feature_request:Describe the new feature}

Sequence to run:
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

For each step:
- Return concise output.
- Keep assumptions explicit.
- Stop and request missing mandatory inputs only when blocked.
