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
- For WinUI work, load installed skills relevant to the actual change and selected validation boundary: development workflow, code review, design when authoring XAML, and UI testing when exercising the running UI. Repository test policy takes precedence over a skill's generic batch-UI workflow; loading a skill does not require E2E or authorize machine configuration changes. Use the setup skill only for an explicit setup request.
- Continue through relevant build checks and validation selected under the test policy. Validate UI behavior primarily through UI integration tests. Use representative ordinary-executable user paths for native/process/cross-screen behavior that lower layers cannot establish; this is not a blanket all-suite gate for each change. Compilation or test discovery alone is insufficient behavioral evidence.
- Pause only dependent work for unresolved decisions, scope expansion, unauthorized remote writes or destructive actions. Do not repeat an approval already given and do not bypass enforced execution restrictions.
- Follow [README.md](README.md) for build/run and [tests/README.md](tests/README.md) for validation. Never store or print authentication tokens.

## Code, tests, comments and commits

- **Code — How:** use clear names and structure.
- **Tests — What:** verify agreed observable behavior, including prohibited side effects, rather than incidental UI-tree structure or private calls.
- **Comments — Why / Why not:** explain a non-obvious constraint near the relevant code; do not narrate development history or invent rationale.
- **Commit messages — Why:** explain the purpose and relevant trade-offs.

## Testing

Read [test policy](tests/README.md) before changing production behavior.

- **Primary testing order: logic-layer unit tests > UI-layer integration tests > E2E tests.** Put most behavioral coverage and implementation feedback in logic tests, then UI integration tests. E2E supplements them with representative end-to-end and native-boundary checks; it must not become the primary proof of rules or UI states that lower layers can verify. This is a coverage/design priority, not a numeric quota or a ban on E2E or live execution.
- Derive expectations from the Issue and specification, not merely the implementation. Map changed behavior to the lowest reliable test boundary before coding. Use short Red-Green-Refactor cycles and reproduce a bug before fixing it. If reproduction requires E2E/live execution, use it and add a lower-layer regression for the underlying defect wherever that layer can detect it. Report a Red or Green result only when it was observed.
- Prefer real collaborators and state/output assertions. A unit of behavior may span collaborating classes. Substitute only external or nondeterministic boundaries as needed; test doubles must not replace the behavior being verified. Retain real adapter/process and isolated-storage integration tests alongside logic unit tests.
- Exercise real presentation collaborators and, for binding/control behavior, actual WinUI views/controls in a suitable UI test host. ViewModel-only tests do not establish XAML/native behavior. A missing UI integration harness is a gap to address with the smallest task-relevant test infrastructure, not a reason to make all UI coverage E2E. Do not relabel existing external-driver tests as UI integration.
- Preserve lower-layer assertions during UI work. Desktop E2E drives the ordinary WinUI executable through public UI Automation with isolated fake gh for deterministic journeys. Add E2E for integration risks not established below, rather than duplicating every logic branch or UI-state combination through the whole app.
- Select executions by changed behavior, boundary risk and explicit acceptance needs. Targeted runs are valid evidence for their stated scope; full regression remains available when warranted. Historical all-suite execution records and suite-preservation rules are not instructions to rerun every suite on each iteration. Explain the boundary-specific reason for E2E, physical IME or live checks and report relevant omitted or unavailable coverage without calling it passed.
- Keep logic/adapter tests, UI integration, desktop E2E, physical-key IME automation, human input acceptance, performance and live GitHub checks distinct. Exercise real IME or live GitHub when the risk requires it; Unicode insertion is not IME evidence, and sandbox permission is not an execution schedule.
- Never weaken, delete or skip contractual tests merely to obtain a pass. Moving coverage to a lower layer requires an explicit behavior mapping and observed replacement coverage before retiring redundant higher-layer checks; retain native/end-to-end checks that the replacement cannot establish. Follow the Issue and test policy for such changes.
- Record source, environment, command, executed/passed/failed/skipped counts and artifacts for executed checks. Zero execution, discovery-only and skip-only outcomes are not passing acceptance. Keep failed attempts; do not repurpose another executable's results.

## Authorized GitHub sandbox

- Exact live-validation resources:
  - Repository: https://github.com/fukuda-yuki/codex-sandbox
  - Project: https://github.com/users/fukuda-yuki/projects/3
- The user has pre-authorized operations confined to these resources, including remote writes, configuration changes and deletions. Do not ask again for in-scope sandbox validation or cleanup. This permission does not require live execution for every task; select it under the test policy.
- Read the [sandbox scope and validation record](https://github.com/fukuda-yuki/codex-sandbox/issues/1) before sandbox work. Keep its task history in Issues.
- GitHub CLI: `C:\Program Files\GitHub CLI\gh.exe`. Explicitly select repository `fukuda-yuki/codex-sandbox` and Project `3` with owner `fukuda-yuki`, or verify equivalent API IDs. Never infer mutation targets from the checkout.
- Sandbox authorization does not cover other repositories, Projects, account/organization settings, or override enforced execution restrictions. Repository development writes need authority from the current task.

## Closeout

Report changes, validation, failures and unverified scope separately. Distinguish branch delivery, main integration and product acceptance. Link PRs with `Refs #...` for partial work; use `Closes #...` only when the full acceptance is satisfied. Keep task status in Issues/PRs and the response, and implementation history in Git.
