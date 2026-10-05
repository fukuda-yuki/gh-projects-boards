# AGENTS.md

## Language

Write agent-facing and shared development documents in English. Respond to the user in Japanese.

## Authority

- Develop from the relevant GitHub Issue and its comments. The user's current request defines the authorized scope and supersedes an outdated plan. Reconcile the owning Issue when the direction changes.
- The product is being redesigned as a planning editor for GitHub Projects under [Epic #76](https://github.com/fukuda-yuki/gh-projects-boards/issues/76). [Requirements](docs/requirements.md) and [decisions](docs/decisions.md) describe the target. [Specification](docs/spec.md), [planning](docs/planning.md) and [architecture](docs/architecture.md) still describe the implementation being replaced; where they conflict with requirements or decisions, the latter prevail. Rewrite an affected section in the child Issue that changes that behavior.
- Issues own acceptance criteria, unresolved decisions, task status and execution evidence. Use [requirements](docs/requirements.md) for the product outline, [specification](docs/spec.md) for agreed behavior, [design](DESIGN.md) for UI/UX/IA judgment criteria, [architecture](docs/architecture.md) for structure, and [decisions](docs/decisions.md) for accepted choices.
- Write shared documents as the current product contract. Keep implementation chronology, rejected experiments and progress reports in Git, Issues and PRs, not in product or agent documentation. Do not falsify execution results or rewrite Git history to simplify documentation.
- Keep Issues short enough for the user to read: state the goal, behavior, acceptance and outcome. Put evidence in a concise result comment with links to artifacts instead of long narrative logs, and close Issues that no longer guide work.

## Product and implementation

- Build a Windows desktop application with C# and .NET 10, using the WinUI 3 / Windows App SDK window as the shell. It is not a hosted web service. The rendering of the plan sheet and Gantt (native WinUI or web components hosted in WebView2) is decided by [#77](https://github.com/fukuda-yuki/gh-projects-boards/issues/77); until then, limit work on the current Boards/Gantt/Summary screens to that prototype.
- Treat this as an internal-use tool under active development. Do not preserve backwards compatibility or maintain data migration shims; refactor directly toward the ideal, simplest design adhering to KISS principles.
- Keep GitHub access, identity, validation, scheduling and application orchestration in the UI-independent Core. Preserve their behavior and regression tests; change them only for an authorized requirement or demonstrated defect.
- Implement UI from the agreed behavior using WinUI controls and public APIs, or the web components selected in #77 for the plan sheet and Gantt. Keep window lifetime, binding/presentation, dialogs, clipboard and UI Automation in the UI boundary. Do not introduce compatibility shells or speculative framework layers.
- Use one real core library and one app. Add another project, abstraction or dependency only for a concrete current need. A grid or Gantt candidate is not an accepted component merely because it compiles.
- Develop the shell, agent instructions, build and test infrastructure independently of the rendering choice or release-packaging work. A blocker stops only the work that actually depends on it.
- Preserve Project-scoped work, local editing with explicit publish, account/host isolation, selection versus editing, IME confirmation versus cell commit, and operation-level Undo. Keep internal save, buffer and journal states out of ordinary screens. Do not replace required editable behavior with a read-only demonstration.

## UI design and review

- Read [DESIGN.md](DESIGN.md) before designing, implementing or reviewing changes to appearance, wording, information presentation, navigation or interaction flows.
- Define the user's task and decision for the affected surface, then apply the relevant design principles alongside the owning Issue and specification. Review the resulting presentation and behavior against those principles; record justified exceptions and unverified conditions in the Issue/PR.
- Judge a UI change by the PMO planning job in [#76](https://github.com/fukuda-yuki/gh-projects-boards/issues/76): run it in the ordinary app with realistic data and look at the rendered screens before reporting it complete. Passing tests do not establish usability.
- Select validation through the [test policy](tests/README.md); use DESIGN.md's checks for design judgment without duplicating either document's rules here.

## Execution

- Inspect the branch, worktree and existing changes before editing; preserve the user's work. Work on an Issue-linked branch unless explicitly instructed otherwise.
- When creating a feature branch from `origin/main`, use `git switch --no-track -c <branch> origin/main`. Its upstream must never be `origin/main`. Before handing off publication, check `branch.<branch>.remote` and `branch.<branch>.merge`: an existing upstream must name the intended same-name remote branch. For an unpublished branch, use repository-local `branch.autoSetupMerge=simple`, `push.default=simple` and `push.autoSetupRemote=true` so an ordinary first push establishes the same-name upstream. Do not change global Git configuration or push to main to work around an upstream mismatch.
- Drive development through a disciplined docs-first, test-driven cycle:
  1. **Documentation first:** Update shared documentation and specifications first; verify that no contradictions or outdated statements remain before writing code.
  2. **Write tests (TDD):** Define expected behavior through automated tests before modifying production code.
  3. **Minimal implementation:** Write the simplest coherent change satisfying the tests; avoid speculative abstractions.
  4. **Final audit:** Verify that obsolete code and documentation discrepancies are eliminated before completing the task.
- Make the smallest coherent change. Do not prebuild later Issues or treat a skeleton as a completed feature. Do not merge unrelated experimental branches to obtain reusable code.
- Define concrete UI operations, visible states and failure conditions in the owning Issue; do not claim completion without corresponding execution evidence.
- Pin required dependencies and review their exact artifacts and terms. Dependency changes require authority for the current outcome. Required commercial use must not depend on paid or company-size/revenue eligibility.
- For WinUI work, load installed skills relevant to the actual change and selected validation boundary: development workflow, code review, design when authoring XAML, and UI testing when exercising the running UI. Repository test policy takes precedence over a skill's generic batch-UI workflow; loading a skill does not require E2E or authorize machine configuration changes. Use the setup skill only for an explicit setup request.
- Continue through relevant build checks and validation selected under the test policy. Validate UI collaboration primarily through scoped UI integration tests. Use representative ordinary-executable journeys for whole-application acceptance and real native/process checks where required; running an executable does not by itself classify a test as E2E. This is not a blanket all-suite gate for each change. Compilation or test discovery alone is insufficient behavioral evidence.
- Pause only dependent work for unresolved decisions, scope expansion, unauthorized remote writes or destructive actions. Do not repeat an approval already given and do not bypass enforced execution restrictions.
- Follow [README.md](README.md) for build/run and [tests/README.md](tests/README.md) for validation. Never store or print authentication tokens.

## Code, tests, comments and commits

- **Code — How:** use clear names and structure.
- **Tests — What:** verify agreed observable behavior, including prohibited side effects, rather than incidental UI-tree structure or private calls.
- **Comments — Why / Why not:** explain a non-obvious constraint near the relevant code; do not narrate development history or invent rationale.
- **Commit messages — Why:** explain the purpose and relevant trade-offs.

## Testing

Read [test policy](tests/README.md) before changing production behavior.

- **Primary testing order: logic-layer unit tests > UI-layer integration tests > E2E tests.** Put most behavioral coverage and implementation feedback in logic tests, then UI integration tests. E2E supplements them with representative whole-application journeys; it must not become the primary proof of rules or UI states that lower layers can verify. This is a priority for where behavior is verified, not a numeric quota or a ban on E2E or live execution.
- Derive expectations from the Issue and specification, not merely the implementation. Map changed behavior to the lowest reliable test boundary before coding. Use short Red-Green-Refactor cycles and reproduce a bug before fixing it. If reproduction requires E2E/live execution, use it and add a lower-layer regression for the underlying defect wherever that layer can detect it. Report a Red or Green result only when it was observed.
- Prefer real collaborators and state/output assertions. A unit of behavior may span collaborating classes. Substitute only external or nondeterministic boundaries as needed; test doubles must not replace the behavior being verified. Retain real adapter/process and isolated-storage integration tests alongside logic unit tests.
- **Behavior over interaction.** Assert observable results, state changes, errors and persisted output. Do not prove behavior through call counts, call order, internal method calls or private state, and do not restate a caller's behavior in its collaborator's test: a collaborator's own cases cover its own contract (invariants, boundaries, ordering, normalization, error mapping). Thin CRUD is covered once at the integration boundary; add logic-unit cases only for real branching, validation, identity, conflict or failure rules.
- **No coverage target.** Do not add cases to raise a percentage or a suite size. Name each case for the behavior it establishes (condition and outcome), use arrange/act/assert, and use table-driven cases for matrices rather than near-identical copies. Propose a short behavior list and let the user review necessity, duplication and layer assignment; treat a behavior-preserving refactor that fails a test as a test defect, not as a reason to mock the behavior under test.
- Classify tests by the system boundary and collaboration actually exercised, including fixture setup, real/replaced dependencies and assertions. UI integration checks a bounded collaboration of UI components, events/commands, presentation state and rendered results; E2E checks a representative user workflow through the application's principal layers to its declared endpoint. FlaUI/UI Automation, process separation, an ordinary executable or a test host does not determine the classification. Fake gh does not by itself make a test UI integration, and live GitHub is an environment dimension, not a synonym for E2E.
- Keep the UI collaboration under test real. Include actual views/controls and their event/binding paths when claiming rendered UI behavior; ViewModel-only checks or direct handler calls cannot prove that wiring. Reuse a suitable existing runtime and test setup, with either direct or external-driver interaction. A dedicated UI host is an option, not a requirement. Inspect existing cases before declaring UI integration coverage absent or adding task-relevant infrastructure; project names alone establish neither coverage nor a gap.
- Preserve lower-layer assertions during UI work. Whole-application desktop E2E uses the ordinary WinUI executable and supported public UI Automation path, with isolated fake gh for deterministic journeys. State the substituted endpoint; this is not real-GitHub evidence. Do not duplicate every logic branch or UI-state combination through the whole app.
- Select executions by changed behavior, boundary risk and explicit acceptance needs. Targeted runs are valid evidence for their stated scope; full regression remains available when warranted. Historical all-suite execution records and suite-preservation rules are not instructions to rerun every suite on each iteration. Explain the boundary-specific reason for E2E, physical IME or live checks and report relevant omitted or unavailable coverage without calling it passed.
- Report test scope, execution mechanism and environment separately. Preserve native focus, physical-key IME, clipboard/picker, process-lifetime, performance and human acceptance evidence where required; these concerns do not automatically make a test E2E. Unicode insertion is not IME evidence, and sandbox permission is not an execution schedule.
- Never weaken, delete or skip contractual tests merely to obtain a pass. Reclassifying existing cases requires a case-level rationale based on their actual scope, not a tool or project rename. Moving coverage down requires an explicit behavior mapping and observed replacement coverage before retiring redundant higher-layer checks; retain native/end-to-end checks that the replacement cannot establish. Follow the Issue and test policy for such changes.
- A test whose asserted behavior is removed by an accepted decision in [decisions](docs/decisions.md) is no longer contractual. Delete it in the PR that removes that behavior and name the decision there. Behavior that survives the redesign keeps or regains coverage at the lowest reliable layer.
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
