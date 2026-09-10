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
- Follow [README.md](README.md) for build and run instructions, and [tests/README.md](tests/README.md) for validation guidance.

## Code, tests, comments, and commits

- **Code — How:** Make the implementation understandable through clear names and structure.
- **Tests — What:** Express and verify agreed observable behavior, rather than incidental implementation details. Add or update tests when behavior changes; include a regression test for bug fixes when feasible.
- **Comments — Why / Why not:** Explain non-obvious rationale, constraints, and deliberately rejected alternatives near the relevant code. Do not restate what the code already makes clear.
- **Commit messages — Why:** Summarize the change and explain why it is needed. Use the body for background and trade-offs when useful.

These are primary responsibilities, not exclusive categories. Add comments only when they help the reader, keep them accurate as code changes, and never invent rationale or rejected alternatives.

## Testing

Before changing production behavior, read [test policy](tests/README.md).

- Derive expected behavior from the relevant Issue and agreed specification,
  not merely from the current implementation.
- Use short Red-Green-Refactor cycles, one behavior at a time.
  Confirm the intended failure before implementing the change.
  Bug fixes start with a reproducing regression test.
- Prefer real in-process collaborators and observable results.
  Use test doubles at external or nondeterministic boundaries as needed.
  Verify interactions when they are part of the contract, not merely
  implementation details.
- Never weaken, delete, or skip tests, or bypass checks, merely to obtain
  a passing result. Justify legitimate test changes against an authorized
  behavior change or a demonstrated defect in the test.

  ## Authorized GitHub sandbox

- For live GitHub validation, the user has designated these exact sandbox resources:
  - Repository: https://github.com/fukuda-yuki/codex-sandbox
  - Project: https://github.com/users/fukuda-yuki/projects/3
- The user has pre-authorized all sandbox operations confined to these resources, including remote writes, configuration changes, and deletions. Do not ask for repeated approval for in-scope sandbox work or cleanup.
- Read the [sandbox scope and validation record](https://github.com/fukuda-yuki/codex-sandbox/issues/1) before sandbox work. Keep validation results and task history in GitHub Issues, not in this file.
- The installed GitHub CLI is available at `C:\Program Files\GitHub CLI\gh.exe`. Explicitly target `fukuda-yuki/codex-sandbox` with `--repo` and Project `3` with `--owner fukuda-yuki`, or verify equivalent API target identifiers. Do not infer mutation targets from the current checkout.
- This authorization does not extend to other repositories, Projects, or account and organization settings. It does not override enforced execution restrictions; report an actual block and its reason if one occurs.

## Closeout

- Report changes, validation, failures, and unverified scope separately. Distinguish local completion from GitHub integration and product acceptance.
- Link PRs to the relevant Issues. Use `Closes #...` only when the Issue's full acceptance criteria are satisfied; use `Refs #...` for partial work.
- Keep task status and findings in Issues, PRs, and the final response; keep implementation history in Git. Do not copy task histories into product documents.
