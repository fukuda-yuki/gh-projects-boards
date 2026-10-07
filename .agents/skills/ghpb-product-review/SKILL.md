---
name: ghpb-product-review
description: Review gh-projects-boards product value from the owner's PMO planning job. Use for roadmap or Epic review, stage-completion assessment, and build handoff to the owner. Supports plan and ordinary-app product reviews; not routine code review or implementation.
---

# GHPB Product Review

Ask: if the owner used this for real weekly planning, would it be worth using, and is the next planned work the most valuable? Challenge whether the Issues and specifications still serve that job. Passing acceptance criteria is evidence of implementation, not proof of product value.

## Scope and inputs

Use the gh-projects-boards checkout selected by the user or the current workspace. Resolve repository paths from its Git root; inspect branch, worktree and existing changes. Do not silently switch to another checkout or a remembered short-path alias. If the repository cannot be identified, request its location.

Accept a review target (Epic, stage, roadmap or build), mode (`plan` or `product`), and any new owner statements verbatim. Infer the mode from an unambiguous request. Otherwise ask which target needs review while reading the available sources. These are review modes, not Codex execution or permission modes.

Perform the review without implementation changes. A skill invocation does not create a separate agent. If this Codex also implemented the target, disclose that this is a self-review. When the user requests an independent review and delegation is available, give the reviewer this skill, the target checkout, the scope and the owner's words; omit the implementer's verdict. If independence is unavailable, say so rather than claiming it.

## Establish the owner's job

Read the selected checkout's `AGENTS.md` and `docs/requirements.md`. Read `DESIGN.md` for presentation and interaction judgment. Follow their links to the owning Epic and relevant children and comments; #88 is an additional starting point for owner decisions, not a permanently current roadmap.

Use the owner's current words first, then attributable owner decisions in the Issues and the Product goal / Agreed boundaries in requirements. Treat model-written summaries, prior review verdicts and acceptance comments as derived evidence. Use `docs/spec.md`, `docs/decisions.md` and Issue criteria to understand commitments, and `DESIGN.md` to judge the experience; none alone proves that the owner's need is met. Report contradictions with the owner's words rather than silently changing a contract or favoring a derived plan.

For Issue reads on Windows, use the available GitHub connector or explicitly targeted CLI commands. For example, in PowerShell:

```powershell
& 'C:\Program Files\GitHub CLI\gh.exe' issue view 88 --repo fukuda-yuki/gh-projects-boards --comments
```

Reconstruct the job from current sources each time: who plans, the unit of planning, realistic scale, where existing Issues enter the flow, which edits dominate, how dates and workload inform decisions, and what the owner deliberately excludes. Establish whether task creation, comparison or another convenience is being promoted above ordinary plan maintenance. Do not propose features without an identified need. Keep changing product facts and task status in their sources rather than maintaining another product specification here. If an Issue is inaccessible, identify the missing evidence and keep dependent conclusions provisional.

## Plan review

Read the target Epic, its children and decision-bearing comments. Assess:

- Which part of the owner's job each item serves, and whether its priority matches that importance.
- Missing central work, contradictions, unnecessary features and dependencies that delay usable value.
- Whether each stage produces a useful increment and whether the next stage is the best use of effort.
- Whether the completed roadmap would support the whole weekly job, and what still requires ordinary-app or owner evaluation.

Make only corrections supported by the owner's job. Do not open the app merely to assess a roadmap, or turn this into code review. Mark UI claims that have not been observed.

## Product review

Do the owner's job through the ordinary executable, using the selected checkout's `README.md` for build/run and `tests/README.md` for evidence boundaries. Load installed WinUI development and UI-testing skills when building or exercising the app, as required by `AGENTS.md`; use an available desktop automation capability after reading its instructions. Locate supported helpers in the current environment instead of assuming a machine-specific helper exists.

Use realistic, version-shaped data and an isolated absolute `GHPB_DATA_ROOT`. Prefer the repository's offline evaluation launcher (`scripts/Start-Evaluation.ps1`) for a reproducible local journey unless live evidence is requested or needed. Inspect current launcher options and fixture size; do not assume fixture values or a prebuilt binary are current. Keep fake and live endpoints and data roots distinct.

Walk the relevant job in context: refresh existing Issues, find the work, update effort, assignees, predecessors and applicable date inputs, inspect the schedule and people's load, then review and explicitly publish adopted changes. Honor a narrower requested scope and report omitted steps. Record the build/source, environment, data scale, operations, meaningful timings and results. Capture and inspect rendered screenshots, linking evidence to findings. Do not substitute test-host output, source inspection or direct internal calls for an observed user operation.

Judge whether the PMO can complete each step, what work it costs, what breaks continuity, and what blocks or misleads a decision. Compare with the described MS Project / TFS workflow only where evidence supports the comparison; do not invent measured time savings. Small bugs matter here when they obstruct the job. An unavailable build or UI capability limits product evidence: continue source-based analysis, but do not report an unperformed product review as passed.

## Side effects and evidence

- Return findings to the user or requesting agent. Do not edit product code, shared documentation or Issues as part of the review. Build outputs, isolated evaluation data and review artifacts are allowed within the requested review scope.
- Apply the current `AGENTS.md` sandbox authorization before live work, including reading its linked sandbox validation record. Remote writes are confined to `fukuda-yuki/codex-sandbox` and user `fukuda-yuki` Project `3`, and only for required evaluation steps. Verify targets explicitly and list every write and its outcome. Authorization already granted there does not need to be requested again. Never use the development repository as a mutation target or print/store tokens.
- Label findings **observed**, **source-backed**, or **inferred**. Give each finding a screenshot, operation result, document location or Issue/comment link as appropriate. Identify missing evidence and blocked steps.
- Keep product judgment, automated validation, live GitHub evidence and human acceptance distinct. If tests are executed, record the command, mechanism, environment, executed/passed/failed/skipped counts and artifacts under the repository test policy. An agent's review cannot establish the owner's real-use acceptance.

## Report

Respond to the user in Japanese; write agent-facing or shared development reports in English. Quote owner statements and UI labels as given. Keep the report short, stating target, mode and whether the review was independent before these results:

1. **Verdict:** on track, needs correction, off track, or insufficient evidence. Limit the verdict to the scope actually reviewed.
2. **The owner's job now:** a few bullets, including newly learned owner decisions and their sources.
3. **Gaps:** rank by value to the owner. For each, give the impact, evidence label and reference, and owning Issue or "none".
4. **Roadmap corrections:** only necessary additions, removals or priority/order changes, each tied to the owner's job.
5. **Stop doing:** work that does not serve that job, or "none supported by evidence".
6. **Unverified:** blocked or unperformed steps and questions requiring the owner's real use. Include any remote-write record or link to it.

If no supported gaps are found, say so without manufacturing recommendations or implying unverified acceptance.
