## Findings

### 1. High — An absent retained task can still be deleted by omission

`SameRetainedTask` now performs the necessary content comparison for arrays and assignment metadata. The remaining problem is that `CommitPlanning` invokes that comparison only for absent tasks that are still present in `candidate.Tasks`. It never verifies that every absent task from the existing plan remains in the candidate. The staged commit then replaces the complete planning collection with the candidate, so omission silently drops the retained record.  

**Minimal Core reproduction**

```csharp
var retained = new PlanningTask(
    "I-absent",
    PlanningMode.Manual,
    "U-old",
    Actuals: [new("U-old", 5, new DateOnly(2026, 10, 5))],
    Contributions: [new("U-old", null, null)],
    LocalLinks: [],
    Assignment: new([], false, Legacy: true));

var w = Work(p, PlanningPathTests.Plan() with
{
    Version = 3,
    Tasks = [retained]
});

var candidate = w.Planning("P1")! with { Tasks = [] };

w.CommitPlanning(p, candidate, w.Revision);

// Current source flow: the commit succeeds and I-absent is gone.
Assert.That(w.Planning("P1")!.Tasks, Is.Empty);
```

The added regression test proves that a deep-copied retained task is accepted and a modified retained task is rejected, but it does not exercise omission. 

The narrow correction is to validate the retained set in both directions:

* Every candidate task absent from the current registration must have an identical existing task.
* Every existing task absent from the current registration must have an identical candidate task.

A regression should attempt `Tasks = []`, expect `InvalidOperationException`, and verify the serialized snapshot is unchanged.

---

### 2. High — Generic `CommitPlanning` can still clear an unknown-time opposite DATE

`SchedulingCandidate` correctly rejects a Manual pair when an endpoint has an observed GitHub day, no adopted exact minute, and no explicit clearing signal. 

That protection is not enforced for candidates submitted directly to `CommitPlanning`. In `ProjectPlan`, a null Manual endpoint is preserved only when the **previous task was already Manual with that endpoint null**, or when no previous plan was supplied. If the previous task was Unplanned or Auto, the condition falls through and the non-empty DATE baseline is converted into `LocalValue(null, Clear: true)`, even though the endpoint was not included in `consumeBuffers` or projection decisions.  

**Minimal Core reproduction**

1. Give `F-Finish` the observed value `2026-10-06`.
2. Start with an existing task in `Unplanned` or `Auto`, with no exact Finish minute.
3. Construct a direct candidate changing the task to Manual with `ManualStart = 2026-10-05 10:17` and `ManualFinish = null`.
4. Call `CommitPlanning` without the Finish key in `consumeBuffers` or decisions.

By the current source flow, the Finish field receives an explicit clear. The guarded `CommitDateInput` tests do not cover this generic candidate path. Their Unplanned/Auto cases go through `SchedulingCandidate`, which rejects earlier. 

The core projection boundary should require an explicit endpoint signal whenever a non-empty observed DATE would become null. At minimum, the preservation/throw condition must not depend on the previous task already being Manual. A suitable regression should submit the direct candidate, expect rejection or preservation, and assert that the Finish field has neither `Change` nor `Clear`.

---

### 3. High — Selecting a time first can convert a known day into an explicit deletion

A day-only value such as `2026-10-06` is intentionally presented when the GitHub DATE is known but no exact minute has been adopted. The scheduling editor passes that raw day to `MinuteEditor`. 

`MinuteEditor.ReadText` only recognizes the full `yyyy-MM-dd HH:mm` format. For a day-only value, parsing fails and both native pickers remain unset. If the user then chooses a **time before choosing a date**, `ReadPickers` sees `date.Date == null` and replaces the text with an empty string. The `Edited` callback durably stores that empty buffer.  

The scheduling candidate interprets exactly that empty buffer as an explicitly cleared endpoint, bypassing the unknown-time protection. 

**Minimal actual-control reproduction**

```text
Initial GitHub Finish: 2026-10-06
Adopted exact Finish: null

Open SchedulingEditor.
Confirm ScheduleFinish.Text == "2026-10-06".
Set ScheduleFinish-Time.SelectedTime to 16:43 before selecting a date.

Current result:
ScheduleFinish.Text becomes "".
The workspace buffer becomes "".
Apply treats Finish as explicitly cleared.
```

The same state handling has an inverse defect for an existing exact minute: clearing only the native date writes empty text but leaves the old `TimePicker.SelectedTime` in memory. Selecting another day in the same editor therefore reconstructs an exact timestamp using the stale time, even though only a day was selected.

The added UI case does not cover either sequence. It applies the clear, closes the editor, and opens a fresh editor before testing day-only selection, so the stale `TimePicker` state has already been discarded. It also always selects the date before the time. 

The narrow correction is for `MinuteEditor` to maintain the two components explicitly:

* Parse a valid `yyyy-MM-dd` day into `CalendarDatePicker.Date` while leaving `SelectedTime` null.
* A time change with no date must not replace a non-empty day/incomplete text with `""`.
* Clearing the native date must also clear the native time when it represents an explicit endpoint clear.
* Only a known date plus a selected time may produce an exact-minute string.

Two actual-control regressions are needed: known-day → time-first, and exact-minute → clear date → select a new date in the same editor.

## Review limits

This was static source review of the supplied frozen bundle. I did not execute the supplied Core or WinUI tests, build the application, or perform a human/native-input run.

I found no additional concrete defect in the shown empty-Actual protection, sparse-contributor retention, legacy-owner preservation, modern Gantt assignment label, or normal-priority row-release logic. That is not runtime validation: the final editor/caret roundtrip, dispatcher behavior under sustained activity, final input timings, physical presentation, scroll-stall acceptance, and the two gated live cases remain unverified. The explicit GitHub Apply boundary is still present; finding 3 first creates an incorrect durable local clear intent, which would require the separate Apply operation before changing GitHub.
