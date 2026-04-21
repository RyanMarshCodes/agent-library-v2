---
mode: ask
model: GPT-5.3-Codex
description: Create a requirement spec from a feature request
---

/spec for this feature request:

${input:feature_request:Describe the feature request}

Output must include:
- Description and why
- Testable acceptance criteria
- Dependencies
- Assumptions
- Questions
- Out-of-scope

Then recommend /validate.
