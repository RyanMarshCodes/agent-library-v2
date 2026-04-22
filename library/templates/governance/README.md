# Governance Templates

Copy these files into project/user scope to bootstrap policy and workflow discipline.

## Included Files

- `project/AGENTS.md`
- `project/CLAUDE.md`
- `project/.cursorrules`
- `global/AGENTS.md`
- `global/CLAUDE.md`
- `global/.cursorrules`

## Precedence

Recommended precedence is project-level first, then global defaults. Project policy may tighten global rules but should not weaken security or approval boundaries.

## Required Policy Areas

Each governance file should cover:

- MCP tool policy (`read`, `mutate`, `execute`),
- cost and budget controls,
- SDLC workflow expectations,
- evidence and citation expectations,
- incident and rollback discipline.
