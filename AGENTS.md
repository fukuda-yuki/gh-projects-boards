# AGENTS.md

## Language

Write agent-facing and shared development documents in English. Respond to the user in Japanese.

## Authority

- Develop from the relevant GitHub Issue and its comments. The user's current request defines the authorized scope and supersedes an outdated plan. Reconcile the owning Issue when the direction changes.
- Issues own acceptance criteria, unresolved decisions, task status and execution evidence. Use [requirements](docs/requirements.md) for the product outline, [specification](docs/spec.md) for agreed behavior, [architecture](docs/architecture.md) for structure, and [decisions](docs/decisions.md) for accepted choices.
- Write shared documents as the current product contract. Keep implementation chronology, rejected experiments and progress reports in Git, Issues and PRs, not in product or agent documentation. Do not falsify execution results or rewrite Git history to simplify documentation.

## Product and implementation

- Build a native Windows desktop application with C#, .NET 10 and WinUI 3 / Windows App SDK. This is the platform, not an open selection task. It is not a web application.
- Keep GitHub access, identity, validation and application orchestration independent of UI frameworks. Preserve their behavior and regression tests; change them only for an authorized requirement or demonstrated defect.
- Implement UI from the agreed behavior using native WinUI controls and public APIs. Keep window lifetime, binding/presentation, dialogs, clipboard and UI Automation in the UI boundary. Do not introduce compatibility shells or speculative framework layers.
- Use one real core library and one app. Add another project, abstraction or dependency only for a concrete current need. A grid candidate is not an accepted component merely because it compiles.
- Develop the shell, agent instructions, build and test infrastructure independently of unresolved grid-input or release-packaging work. A blocker stops only the work that actually depends on it.
- Preserve Project-scoped work, explicit GitHub apply, local drafts, account/host isolation, selection versus editing, IME confirmation versus cell commit, and operation-level Undo. Do not replace required editable behavior with a read-only demonstration.

## Execution

- Inspect the branch, worktree and existing changes before editing; preserve the user's work. Work on an Issue-linked branch unless explicitly instructed otherwise.
- Make the smallest coherent change. Do not prebuild later Issues or treat a skeleton as a completed feature. Do not merge unrelated experimental branches to obtain reusable code.
- Pin required dependencies and review their exact artifacts and terms. Dependency changes require authority for the current outcome. Required commercial use must not depend on paid or company-size/revenue eligibility.
- For WinUI work, load the relevant installed development-workflow, UI-testing and code-review skills; load design guidance when authoring XAML. Use the setup skill only for an explicit setup request. Repository requirements take precedence. Loading a skill does not authorize machine configuration changes.
- Continue through relevant build checks and non-destructive validation. UI completion requires the ordinary executable and the real user path; compilation or test discovery alone is insufficient.
- Pause only dependent work for unresolved decisions, scope expansion, unauthorized remote writes or destructive actions. Do not repeat an approval already given and do not bypass enforced execution restrictions.
- Follow [README.md](README.md) for build/run and [tests/README.md](tests/README.md) for validation. Never store or print authentication tokens.

## Code, tests, comments and commits

- **Code — How:** use clear names and structure.
- **Tests — What:** verify agreed observable behavior, including prohibited side effects, rather than incidental UI-tree structure or private calls.
- **Comments — Why / Why not:** explain a non-obvious constraint near the relevant code; do not narrate development history or invent rationale.
- **Commit messages — Why:** explain the purpose and relevant trade-offs.

## Testing

Read [test policy](tests/README.md) before changing production behavior.

- Derive expectations from the Issue and specification, not merely the implementation. Use short Red-Green-Refactor cycles and reproduce a bug before fixing it. Report a Red or Green result only when it was observed.
- Prefer real collaborators and state/output assertions. Substitute only external or nondeterministic boundaries as needed. Test doubles must not replace the behavior being verified.
- Preserve logic and integration assertions during UI work. Desktop E2E drives the ordinary WinUI executable through public UI Automation, with an isolated fake gh for deterministic connection journeys.
- Keep deterministic tests, desktop E2E, physical-key IME automation, human input acceptance, performance and live GitHub checks distinct. Unicode insertion is not IME evidence.
- Never weaken, delete or skip contractual tests merely to obtain a pass. Justify changes against an authorized behavior change or demonstrated test defect. Replacement of UI-specific mechanics must retain behavioral coverage.
- Record source, environment, command, executed/passed/failed/skipped counts and artifacts. Zero execution, discovery-only and skip-only outcomes are not passing acceptance. Keep failed attempts; do not repurpose another executable's results.

## Authorized GitHub sandbox

- Exact live-validation resources:
  - Repository: https://github.com/fukuda-yuki/codex-sandbox
  - Project: https://github.com/users/fukuda-yuki/projects/3
- The user has pre-authorized operations confined to these resources, including remote writes, configuration changes and deletions. Do not ask again for in-scope sandbox validation or cleanup.
- Read the [sandbox scope and validation record](https://github.com/fukuda-yuki/codex-sandbox/issues/1) before sandbox work. Keep its task history in Issues.
- GitHub CLI: `C:\Program Files\GitHub CLI\gh.exe`. Explicitly select repository `fukuda-yuki/codex-sandbox` and Project `3` with owner `fukuda-yuki`, or verify equivalent API IDs. Never infer mutation targets from the checkout.
- Sandbox authorization does not cover other repositories, Projects, account/organization settings, or override enforced execution restrictions. Repository development writes need authority from the current task.

## Closeout

Report changes, validation, failures and unverified scope separately. Distinguish branch delivery, main integration and product acceptance. Link PRs with `Refs #...` for partial work; use `Closes #...` only when the full acceptance is satisfied. Keep task status in Issues/PRs and the response, and implementation history in Git.
