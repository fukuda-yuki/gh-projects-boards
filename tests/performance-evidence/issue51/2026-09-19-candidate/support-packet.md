# Project-add diagnostic packet — not submitted

Question for a maintainer or GitHub Support: why did `addProjectV2ItemById` return `UNPROCESSABLE` with HTTP 200 / gh exit 1 while a subsequent independent query found the requested membership? Can the retained request ID identify a service-side reason or an applicable documented containment? Original message text was not retained, so local evidence cannot classify that historical response.

## Scope and implementation checked

Only `fukuda-yuki/codex-sandbox` and user `fukuda-yuki` Project 3 were used. The command is `gh api graphql --hostname github.com --method POST --input - --include`; JSON on stdin contains the mutation below and explicitly verified Project/Issue IDs. No token is extracted. No workflow or Project configuration was changed.

```graphql
mutation PerformanceAdd($project: ID!, $issue: ID!) {
  addProjectV2ItemById(input: {projectId: $project, contentId: $issue}) {
    item { id }
  }
}
```

The target mutation/ID construction matches the [GitHub CLI v2.100.0 item-add implementation](https://github.com/cli/cli/blob/v2.100.0/pkg/cmd/project/item-add/item_add.go). Product field updates already use GraphQL; this is a fixture-add problem, not evidence that Title updates need parallelism. [GitHub's API guidance](https://docs.github.com/en/rest/using-the-rest-api/best-practices-for-using-the-rest-api) recommends serial requests and pauses between writes; the diagnostic retained serial one-second-minimum pacing, observed quota guards and cleanup reserves.

## Historical evidence, preserved

- Retained old source: `adf59a85264dbbe99f3c1af98b543dd770fdb3cb`; run began 2026-09-19 06:06:01 +09:00. The eighth add (`add-7`) failed with HTTP 200, exit 1 and `UNPROCESSABLE`. Request ID: `8CAE:12E2D4:55E93:77761:6AADA7E8`. Observed GraphQL remaining quota: 4986. Recorded request/response byte counts: 228/1401. The message/detail and error path were not retained.
- An earlier independent setup stopped at its 39th add; its error code/detail was not preserved. Neither failure is reconstructed or relabelled.
- Independent readbacks found membership after both failed responses. Enabled workflows and a non-automated add event were inspected in the old investigation, but they do not prove or exclude a race. Do not label this a workflow defect, quota exhaustion, transport corruption or duplicate-add error without new evidence.
- All 58 old run-owned Issues were deleted and old manifests record verified cleanup; the old total was 276 logical mutation attempts, including failure/cleanup. The current run includes those ledger roots in budget admission. Old deleted identities were not reused.

## One new prospective single-item attempt

Hypothesis: an isolated freshly created Issue may already be a member before explicit add, or the independently absent-to-added path may reproduce the error. The [plan](plan.md) was committed before dispatch. The maximum for this increment is two single-item attempts; only one was used, with no retry or second hypothesis.

At 2026-09-19 04:47:34 UTC, one run-owned Issue was created. Complete independent membership read: **0**. One Project-add at 04:47:38 UTC returned **HTTP 200 / exit 0**, request ID `D998:BDEE2:2E1941:3DDAF7:6AAE13E9`, GraphQL remaining quota **4975**. Complete post-read membership: **1**. A separate Verify stage passed. Separate Cleanup removed the membership and deleted the Issue; independent HTTP 410 Gone and the exact complete original sandbox snapshot were verified. Four logical writes total: create/add/remove/delete. No throttle or GraphQL error signal occurred; the deletion readback's nonzero CLI exit for HTTP 410 is expected absence evidence.

See [sanitized per-stage protocol metadata](diagnostic-stages.json) and [readback summary](diagnostic-readback.json). Process windows were approximately 14.005 s preparation, 6.032 s verification and 12.980 s cleanup; these are first-process-start through last-process-end intervals, not timed Apply samples or complete lifecycle elapsed. Private originals retain exact run-owned IDs and snapshot/journal provenance, linked by hashes. They are not reproduced here.

## Disposition and exact next gate

The isolated add did **not** reproduce the historical error. It disproves only that every isolated absent-to-added operation fails under the observed conditions. It does not explain intermittent failure in a larger setup or validate a supported containment. No second diagnostic or bulk preparation was dispatched. The required paired live 50-Title comparison is **Not run**.

To proceed: obtain a supported cause/correction or validate a containment that addresses the retained uncertain-add scenario without replay, ownership ambiguity or increased error risk. A maintainer can review this packet and choose to submit its retained request ID to support. Another evidence-backed hypothesis could use the remaining single-item diagnostic allowance; current evidence does not justify that attempt. A support response is not guaranteed because historical detail is missing. After the setup gate is resolved, execute the already fixed counterbalanced plan across budget windows using a new run-owned fixture. A longer delay, increased allowance or one successful add alone does not clear the gate.
