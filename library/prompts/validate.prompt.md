---
mode: ask
model: GPT-5.3-Codex
description: Validate current artifact quality before advancing
---

/validate this target:

${input:target:spec|architecture|tasks|implementation and context}

Return:
- Pass/fail checklist
- Severity levels (Blocker/Warning/Info)
- Specific fixes
- Recommended next command
