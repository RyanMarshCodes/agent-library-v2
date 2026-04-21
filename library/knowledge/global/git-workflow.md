# Git Workflow Standards

Branching model, commit conventions, PR process, and merge strategy for any project.

## Branching model

Use **trunk-based development** for teams with CI/CD and feature flags, or **GitHub Flow** for simpler projects. Avoid long-lived feature branches.

### Branch naming

```
<type>/<short-description>
<type>/<issue-id>-<short-description>
```

Types match commit types (see Commit Conventions below):
- `feat/user-auth`
- `fix/GH-142-order-total-rounding`
- `chore/upgrade-dotnet-9`
- `refactor/extract-payment-service`

Rules:
- Lowercase, kebab-case
- Short enough to be readable in a list (≤50 chars)
- Include the issue/ticket ID when one exists
- Delete branches after merging — don't let stale branches accumulate

### Long-lived branches

| Branch | Purpose | Who merges to it |
|---|---|---|
| `main` | Production-ready code at all times | PRs only, after CI passes |
| `develop` *(if used)* | Integration branch | Feature branches; merge to main via release |

`main` is always deployable. Never commit directly to `main`.

## Commit conventions

Use **Conventional Commits** (https://www.conventionalcommits.org):

```
<type>(<scope>): <subject>

[optional body]

[optional footer(s)]
```

### Types

| Type | Use |
|---|---|
| `feat` | New feature (triggers minor version bump in semver) |
| `fix` | Bug fix (triggers patch bump) |
| `docs` | Documentation only |
| `style` | Formatting, whitespace — no logic change |
| `refactor` | Code change that neither fixes a bug nor adds a feature |
| `perf` | Performance improvement |
| `test` | Adding or correcting tests |
| `chore` | Build process, dependency updates, tooling |
| `ci` | CI/CD configuration changes |
| `revert` | Reverts a previous commit |

### Subject line rules

- Imperative mood: "add user auth" not "added user auth" or "adds user auth"
- No capital letter at the start
- No period at the end
- ≤72 characters
- Describes the *why* when it's not obvious from the type+scope alone

### Breaking changes

```
feat(api)!: remove deprecated /v1/users endpoint

BREAKING CHANGE: /v1/users has been removed. Use /v2/users instead.
Closes #234
```

The `!` and `BREAKING CHANGE:` footer trigger a major version bump.

### Body and footer

- Use the body to explain *why*, not *what* (the diff shows what)
- Reference issues: `Closes #123`, `Fixes #456`, `Refs #789`
- Co-authors: `Co-authored-by: Name <email>`

## Pull requests

### PR size

- One PR = one concern. A PR that does a feature + a refactor + a dependency bump is three PRs.
- Target: reviewable in under 30 minutes. If a PR takes longer, split it.
- Large, unavoidable PRs: add a PR description that guides the reviewer through the diff in the right order.

### PR description template

```markdown
## What
[What change was made and why — one paragraph]

## How
[Key implementation decisions and non-obvious choices]

## Testing
- [ ] Unit tests added/updated
- [ ] Integration tests added/updated
- [ ] Tested manually in [environment]

## Checklist
- [ ] No secrets or credentials in code
- [ ] Breaking changes documented
- [ ] Related issues linked
```

### Review process

- At least one approval required before merging
- CI must pass (build, lint, tests) before merging
- Author resolves all comments before merging; use "Resolve conversation" to track
- Reviewer: comment on the behavior, not the person; suggest, don't demand style nits
- Reviewer: use prefixes for clarity:
  - `nit:` — minor style, take it or leave it
  - `question:` — asking for understanding, not requesting a change
  - `blocker:` — must be addressed before merge
  - `suggestion:` — worth considering, not required

### Stale PRs

PRs open for more than 7 days without activity should be commented on or closed. Stale PRs create merge conflict debt.

## Merge strategy

**Squash merge** for feature branches into `main`:
- Produces one clean commit per PR on `main`
- The squash commit message = the PR title (which must follow conventional commits)
- Squash commit body = the PR description summary
- Individual commits on the feature branch don't need to be clean

**Merge commit** (no squash) when:
- The branch history is itself meaningful (e.g., a long-lived release branch)
- You need to preserve individual commits for audit/compliance

**Never use force push to `main`**. Rebase and force-push are acceptable on feature branches before review, but not after others have checked out the branch.

## Rebase vs. merge for keeping branches current

- **Rebase** your feature branch onto `main` before opening a PR (clean history, no merge commits)
- `git fetch origin && git rebase origin/main`
- Resolve conflicts as they appear per-commit — easier to reason about than a single merge conflict
- After rebasing a branch that's already in review, notify reviewers (history changed)

## Tags and releases

- Tag releases on `main`: `v1.2.3` (semver)
- Annotated tags: `git tag -a v1.2.3 -m "Release v1.2.3"`
- Generate a CHANGELOG from conventional commits: use `git-cliff`, `standard-version`, or the ChangelogAgent

## .gitignore

Every project must have a `.gitignore`. Never commit:
- Build output (`bin/`, `obj/`, `dist/`, `build/`, `*.pyc`)
- Dependencies (`node_modules/`, `.venv/`, `packages/`)
- IDE files (`.vs/`, `.idea/`, `*.user`) — use `~/.gitignore_global` for personal tooling
- Environment files (`.env`, `*.env.local`, `appsettings.Development.json`)
- Generated files that can be reproduced from source
- Secrets of any kind — ever

## Protecting main

Configure branch protection rules on `main`:
- Require PR before merging
- Require status checks to pass (CI build + tests)
- Require at least 1 approval
- Dismiss stale approvals when new commits are pushed
- Restrict force-push and deletion
