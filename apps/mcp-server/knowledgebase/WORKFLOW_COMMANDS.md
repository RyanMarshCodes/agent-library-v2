# ADLC-Compatible Workflow Commands

This is the canonical workflow contract for spec-based development across GitHub Copilot, Claude Code, Cursor, OpenCode, Antigravity, and MCP-enabled agents.

## Core Workflow

Use this sequence for new features:

/context -> /spec -> /validate -> /architect -> /validate -> /implement -> /test -> /reflect -> /review -> /commit -> /wrapup

Natural language trigger:

Let's build a new feature: <description>

## Speckit Templates

Canonical templates are in:

	mcp-server/knowledgebase/templates/

Use by phase:
- /spec -> PRODUCT_SPEC_TEMPLATE.md
- /architect -> ENGINEERING_DESIGN_TEMPLATE.md
- /test -> QA_TEST_SPEC_TEMPLATE.md
- /commit (and optional /wrapup) -> RELEASE_SPEC_TEMPLATE.md

Speckit index:

	mcp-server/knowledgebase/templates/SPECKIT_TEMPLATE.md

## Command Reference

### /context (or /init)
Goal: load project context, standards, architecture, conventions, and relevant prior decisions.

Required checks:
- Confirm project context exists (or bootstrap via /init).
- Identify relevant standards and conventions before /spec.

### /spec
Goal: produce a requirement spec with explicit acceptance criteria and non-goals.

Required checks:
- No implementation leakage.
- Acceptance criteria are specific and testable.
- Assumptions, dependencies, out-of-scope, and open questions are explicit.

### /validate
Goal: validate current phase artifact before advancing.

Supported targets:
- spec
- architecture
- tasks
- implementation

Output must include:
- Pass/fail checklist
- Severity labels (Blocker, Warning, Info)
- Recommended next step

### /architect
Goal: produce architecture decisions and a task breakdown.

Required checks:
- Tasks are small and actionable.
- Task dependencies form a DAG (no cycles).
- Tests are included in task acceptance criteria.

### /implement
Goal: execute tasks in order with small, verifiable diffs.

Required checks:
- Follow conventions and architecture constraints.
- Run relevant checks after each significant task.

### /test
Goal: add meaningful tests and coverage for changed behavior.

Required checks:
- Happy path, edge cases, and failure paths.
- Deterministic tests.

### /reflect
Goal: self-review implementation before formal review.

Required checks:
- Correctness
- Convention compliance
- Architecture fit
- Testing completeness
- Remaining questions for the user

### /review
Goal: multi-agent review across correctness, quality, architecture, testing, and security.

Required checks:
- Findings are prioritized by severity.
- Include concrete file-level remediation guidance.

### /commit
Goal: generate conventional commit message and PR description.

Required checks:
- Clear change summary
- Risk and rollback notes
- Reviewer focus areas

### /wrapup
Goal: close the feature, finalize artifacts, and capture durable knowledge.

Required checks:
- Record lessons and assumptions validated/invalidated.
- Update relevant architecture or conventions notes when needed.

## Orchestration Commands

### /proceed
Run the end-to-end feature pipeline with validation gates between phases.

### /status
Show workflow status for current feature or active pipelines.

### /bugfix
Use streamlined bug flow: report -> analyze -> fix -> verify.

## MCP Tool Mapping

These MCP tools provide machine-callable access:
- list_workflows
- get_workflow
- start_feature_workflow
- run_workflow_step

## Cross-Tool Enablement Contract

Every tool integration should support at least one of:
- Slash commands (/spec, /validate, etc.)
- Prompt recipes (new-feature prompts)
- MCP tool calls (list_workflows/get_workflow/start_feature_workflow/run_workflow_step)

If slash commands are not natively supported, route natural prompts to the same workflow sequence.
