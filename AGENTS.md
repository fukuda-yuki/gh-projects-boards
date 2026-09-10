# AGENTS.md

## Language

Write agent-facing and shared development documents in English. Respond to the user in Japanese.

## Authority

- Develop from GitHub Issues in this repository. Read the relevant Issue and its comments before starting.
- Issues own requirements, acceptance criteria, open questions, future work, and development plans. The user's current request defines the authorized scope.
- Use [requirements](docs/requirements.md) for the product outline, [specification](docs/spec.md) for agreed behavior, [architecture](docs/architecture.md) for structure, and [decisions](docs/decisions.md) for technical choices. Keep these concise.
- Do not turn a candidate technology or unresolved requirement into a decision. Clarify choices that affect the result, while continuing independent authorized work. Record resolved choices in the owning document.

## Execution

- Inspect the current branch, worktree, and existing changes before editing. Preserve the user's work.
- Make the smallest coherent change for the current outcome. Do not prebuild later Issues, introduce speculative layers, or treat a skeleton as a completed feature.
- Add dependencies, test projects, and abstractions only when the current outcome needs them. Dependency or lockfile changes require explicit authority.
- Continue through relevant build checks and non-destructive validation. For UI work, verify the ordinary executable and user interaction path; a successful build alone does not establish usability.
- Pause only the dependent work for unresolved product decisions, wider scope, unauthorized remote writes, or destructive actions. Do not repeat an approval already given.
- Use explicitly designated test data for live GitHub mutation tests. Never store or print authentication tokens.

## Closeout

- Report changes, validation, failures, and unverified scope separately. Distinguish local completion from GitHub integration and product acceptance.
- Link PRs to the relevant Issues. Use `Closes #...` only when the Issue's full acceptance criteria are satisfied; use `Refs #...` for partial work.
- Keep task status and findings in Issues, PRs, and the final response; keep implementation history in Git. Do not copy task histories into product documents.
