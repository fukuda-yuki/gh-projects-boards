# Contextual planning and sustained input correction

This evidence belongs to [#61](https://github.com/fukuda-yuki/gh-projects-boards/issues/61) and [#65's corrective assignment](https://github.com/fukuda-yuki/gh-projects-boards/issues/65#issuecomment-5747793548), under the [current roadmap](https://github.com/fukuda-yuki/gh-projects-boards/issues/1). The prior human evaluation was negative. A new human evaluation has not occurred.

## Preserved work and source

- Unit D remains intact at `671011ad04beb7b7cce8df8328808d7de1aaf13a`, branch `codex/issue-64-summary`, [draft PR #68](https://github.com/fukuda-yuki/gh-projects-boards/pull/68). Its continuation/evidence remain in that branch. [Preservation receipt](https://github.com/fukuda-yuki/gh-projects-boards/issues/64#issuecomment-5747926788). No corrective edits were made in that checkout.
- Corrective base: `3dcb3364d6c65f07f04b600e3b65f5ad53403557`, containing PRs #66/#67. A later fetch still resolved main to this commit. The separate worktree and Issue-linked `codex/issue-61-65-input-repair` branch had one writer; it never tracked `origin/main`.
- `de18efd60e43ccf921f934aa2711416948f8041f`: initial pending/save experiment. `58fae0ab07c390232d1069ab6618b456c6d1829e`: contextual input/assignment and independently reproduced save fixes. `bb9c69d9a62135860faa691f132fcc0c64c29b3f`: ordinary flyout anchors and isolated evaluation. `825389f`: modal-close input retention. `a642504bbcc772558a672151624858b6d7d4adc2`: bounded native offscreen realization. `ef8fa45745bf9754a0635ffd5f3e1b7f949769cb`: diagnostic preparation and test-boundary documentation.
- Final app/Core source: `ad56c06bfd8fbc314d6c92a5bc6692dcbb244dac`, after two rounds of independently reproduced review findings. `802004a2a38abfea141a365e0e502a075390306a` changes only the Gantt launcher/current launch instructions; `src/` is identical to `ad56c06`. Later evidence-only commits do not replace those execution receipts.
- Frozen measurements identify exact executable/App/Core hashes, fixture generator and driver hashes, source changes and source-file hashes. A source label denotes the committed source contents; where a binary was built just before that commit, its build receipt records the parent plus the exact working diff. Do not substitute a newly built binary for a frozen receipt.

## Behavior and lowest reliable boundary

| Acceptance | Implementation and verification boundary |
| --- | --- |
| FLOW-01 Project setup | Stable field ID/type mapping, including EndDate/rename/same-name other-ID cases; configure calendar/weights once, then native assignee plus Estimate creates Auto dates. Core assignment/contract cases and actual Project settings controls. |
| FLOW-02 Actual | Type/F2/paste retains pending numeric text. Confirm one visible reporting cutoff and reuse it across rows. Preserve historical worker/unattributed records; multiple workers require explicit breakdown. Actual 5→7 leaves Remaining 4; Remaining 4→3 and Undo leave Actual 7. Core + actual controls; ordinary journey checks the current native editor after modal closure. |
| FLOW-03 minute dates | Two methods, Auto or explicit datetime. Native calendar/time controls and typing preserve exact minutes and the other endpoint. Auto previews current/affected tasks before explicit Apply. Core transitions + actual date controls. |
| FLOW-04 assignment/history | Complete unique native assignee and explicit Project weight; missing/incomplete/multiple/missing-weight unresolved. Old independent owners remain labelled Legacy until deliberate comparison/adoption. Core rules, durable migration and contextual Auto controls. Sparse contributors and historical reports are retained; empty Update cannot erase unknown actuals. |
| FLOW-05 observation | EndDate is tested as an observed field name bound by stable ID. The original fixture used Finish; no unobserved production EndDate defect is claimed. |
| FLOW-06 workflow | Ordinary executable, isolated fake gh: setup, Manual minute endpoint, dependency, weekly Actual/Remaining, Gantt, restart and explicit reviewed projection publication. Separate 1,000-task journey checks Project isolation, far-row edit, narrow view, same-row Boards/Gantt and restart. Fake gh is the endpoint; this is not live GitHub evidence. |
| #65 input/lifetime | Core owns snapshot detachment, new requests during failed saving, reentrant completion, recovery and old checkpoints. UI owns pending/IME presentation, focus, scrolling, current controls and Undo. Opt-in native diagnostics own sustained physical input/scroll, independent pixels, writer-lock failure/retry and normal-close readback. |

Draft version 11 / Planning version 3 deliberately avoid collision with held Unit D's versions 10 / 2. Older supported drafts remain readable; version 10 is rejected without rewriting its bytes. Unit D must reconcile this contract when it resumes. Evaluation roots are isolated; no user's normal data was migrated for this task.

## Verified findings and reproduced failures

1. Pending text changed the general revision and re-entered broad aggregate/fingerprint/control presentation through save notifications. The existing scheduler already had a cache; there is no claim that every key recomputed it. Native-value baseline p95 exceeded 100 ms under the declared sustained fixture.
2. The initial save experiment lost newer requests after a failed shared flush, could report success before reentrant completion work was durable, retained mutable nested snapshot arrays, and could leave IME-deferred presentation undelivered. Independent review raised these defects; four Core regressions were observed failing before fixes and then passed. The original review is retained with dispositions.
3. Ordinary Boards/Gantt tests reproduced contextual flyouts disappearing with a dismissed overflow menu or being clipped by a full-height anchor. Stable command-bar anchors and actual viewport bounds fixed those failures; lower UI integration cases inspect input and final actions inside the popup viewport.
4. Ordinary actual entry after task details reproduced a later row rebuild replacing the receiving editor. An old UIA reference still said `5` while the current displayed cell was empty. A new current-control assertion failed; a bounded modal-close UI case also reproduced lost focus. Rebuilding before modal input is released fixed the current-cell/focus assertion and the ordinary journey. Earlier passing journeys are not proof of that formerly missing assertion.
5. Independent GDI frames remained unchanged during a 772 ms rendering interval in the earlier candidate while wheel input was being dispatched. Row construction overlapped the interval. Reducing the standard ListView offscreen buffer from four viewports to half a viewport reduced observed native construction and long gaps. This keeps the standard panel, original row/field identities and active/pending editors; it is not a new grid or row rebinding scheme.
6. An 80-hop Boards/Gantt sequence retained 78 sampled unloaded native editors. A Low-priority cleanup barrier failed to run within ten seconds while normal callbacks continued. Deferred Normal-priority cleanup reduced the sampled unloaded count to zero and retained the original pending native editor and caret. Earlier failed probes and temporary diagnostic counts remain separate; this does not attribute every rendering stall to that queue.
7. Independent source review found data-loss and misleading-state edges in empty Actual updates, partial dates, sparse contributors, legacy assignment adoption and unresolved capacity presentation. They were reproduced at Core/control boundaries and corrected. See [review dispositions](review-dispositions.md); a source review is not runtime or human acceptance.

## Measurements

Local Windows x64: Windows 11 build 26200, .NET SDK 10.0.401 / runtime 10.0.12, PowerShell 7, 125% display scaling. AMD Ryzen 7 9700X (8 cores / 16 logical processors); NVIDIA GeForce RTX 5070 Ti and AMD integrated graphics were reported by the host. GPU routing/physical scanout were not measured.

All comparison runs use the archived `de18efd` fixture generator: 1,000 tasks, 20 people, six fields, initialized mixed plans, dated actuals, one offscreen pending estimate and one Undo operation; the initial checkpoint is 16,000,045 bytes. Timestamps differ between freshly generated roots, so the fixture is workload-matched rather than byte-identical. Window size is 1080×760 physical pixels. Standard workload: 20 seconds title, 20 seconds NUMBER, 20 seconds alternating wheel/horizontal input; warm condition adds ten unmeasured seconds. Each condition starts a new process/root. No competing builds or performance/live experiments ran during these measurements.

| Frozen source / condition | Title p95 ms | NUMBER p95 ms | Longest rendering interval ms |
| --- | ---: | ---: | ---: |
| integrated main / cold | 106.29 | 118.12 | 1970.80* |
| integrated main / warm | 124.73 | 109.66 | 2241.74* |
| de18efd / cold | 81.53 | 79.52 | 1856.67* |
| de18efd / warm | 78.36 | 80.11 | 2003.72* |
| bb9c69d / cold | 93.31 | 80.94 | 771.59 |
| bb9c69d / warm | 81.22 | 79.25 | 858.91 |
| a642504 / cold | 47.54 | 47.44 | 304.97 |
| a642504 / warm | 48.82 | 52.75 | 316.67 |
| ad56c06 / cold | 52.88 | 52.47 | 288.21 |
| ad56c06 / warm | 48.21 | 51.39 | 287.33 |

These are one completed run per source/condition, not a repeatability claim. At final production source `ad56c06bfd8fbc314d6c92a5bc6692dcbb244dac`, cold has 98 title / 99 number samples and warm has 98 / 98, none >=100 ms. Native-value readback includes UIA observer cost and is not a composited-pixel latency. Rendering callbacks are pre-composition signals. Independent scroll captures had approximately 63–80 ms spacing; all intervals and file hashes are retained. The final cold/warm traces are drained and sequence-complete, with **37/40 rendering intervals >=100 ms**, **197/196 flush requests** and **138/143 durable commits** within the workload. Do not average those remaining freezes away or call scrolling/human acceptance complete.

The final cold run's frames 0036–0042 show identical rows 481–492 while wheel input is delivered; frame 0043 shows rows 721–732. A 75.42 ms row-realization span overlaps the largest callback gap; it is one contributor, not a complete attribution. Physical-IME confirmation/save-failure/retry completed 20 iterations (one with an intentional writer lock), normal close and readback, with no >=100 ms callback interval in that workload. The separate thumb/distant-row/horizontal/return diagnostic passed its state assertions, while retaining six >=100 ms callback gaps (max 272.95 ms). None of these observations establish natural-input or human acceptance.

`*` Earlier traces lacked a terminal drain receipt; their recorded operation counts and original rendering distributions are retained as incomplete-trace evidence, separately from current workload-filtered/drained analysis. Do not silently replace their original derived files. The portable stall analysis provides per-interval input/span/frame correlations; correlations do not prove a unique runtime cause.

The separate sampled runtime/GC profile uses `dotnet-trace` 10.0.745401 on `de18efd`, outside comparative latency runs. It points to allocation/serialization and WinRT finalization/control churn. Sampled inclusive wall time includes waiting; allocation samples are estimates, not exact heap totals. Original `.nettrace` remains local with a hash; portable stack/GC/alloc extracts are sanitized. The exact finalizer/GC initiation cause has not been proven.

## Execution and handoff

The machine-readable execution ledger and source/command/environment receipts preserve passing, failing, skipped and discovery-only attempts separately. Core's earlier full run executed 599 cases (569 passed / 30 failed); obsolete schema-version expectations and array-reference equality assertions were corrected, with 104/104 selected replacements and 15/15 assignment/save checks. They are not presented as a new all-green 599-case run. UI setup errors (unrealized controls, unfinished focus/overlay animation), the initial physical-IME pacing failure, the first scrollbar probe's insufficient wheel distance, and genuine product failures remain retained.

The Core regression at `6c932abb4ff21e28c28076a58bc3656ae2780a97` executed **618 / passed 618 / failed 0 / skipped 0** in 3m15s. Subsequent final-review fixes at `ad56c06bfd8fbc314d6c92a5bc6692dcbb244dac` passed **35/35** related Core cases and **37/37** actual-control planning/Gantt cases after observed **3 Core / 2 UI Reds**. Earlier runs and the final broader regression receipt are separate; see the execution ledger.

The final ordinary-app selection at `ad56c06` passed **2/2**, no skips, in 48 seconds (`TestResults/e2e/20260920-185207-95a07bdca4a14797853e9fac188ff090`). These representative lifetime journeys declare isolated fake gh as their endpoint. The Release solution build succeeded with two inherited Windows App SDK UI-host CS0436 warnings. Cold, warm, IME and scroll diagnostics each executed one passing state/recovery test; their long-gap observations remain separately reported above.

After the final three narrow review fixes, the complete non-live Core selection at `ad56c06` executed **621 / passed 621 / failed 0 / skipped 0**, in 3m14s. Exact command: `dotnet test tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj -c Release --filter 'TestCategory!=LiveGitHub' --logger 'trx;LogFileName=results.trx' --results-directory TestResults/issue61-65/core-final-15`. This final run is separate from the earlier 618/618, 605/605 and failed executions.

The user later identified the failed evaluation launch method as `dotnet build GhProjectsBoards.sln -c Release` followed by `./scripts/Start-GanttCheck.ps1`. Source inspection confirms that launcher seeds the ordinary production app with 1,000 synthetic tasks, 20 people and six fields, retained mixed planning, dated actuals and an offscreen pending estimate. It does not rename a connected Project's field. The exact earlier checkout, binary and generated root remain unknown; the reported experience is not retrospectively assigned to a measured run.

The identified Gantt launch path reproduced a separate startup warning: the old launcher placed `gantt-fixture.json` in RegistrationStore's root, where the app reported `InvalidJsonOrSchema`. The warning and original synthetic root are retained. `Start-GanttCheck.ps1` now delegates to the hardened planning launcher's Load scenario. A new ordinary launch displayed 1,000 Gantt tasks without the warning and closed normally. The old root remains untouched; Resume deliberately requires the current root-specific synthetic marker.

At launcher source `802004a`, Weekly was launched, physical F2/digits changed Actual 5→7 and Remaining 4→3 through the ordinary controls, then normal close/Resume/readback verified both **7 and 3** in current controls and pixels. Fresh also completed PrepareOnly with validated readback. Load, Weekly and Resume retained their rebuild/source/binary receipts. Four invalid-root rejection probes had already passed with original bytes unchanged. The older incomplete foreground/resume attempt remains retained and is not repurposed as this success.

Portable images include interior scroll frames and new client-interior captures. Full-window images that could contain neighboring pixels at rounded frame edges are excluded with original hashes, and remain local. No failure image was altered or regenerated. Text paths/private review URLs are sanitized with original/portable hashes. The archive includes the per-execution ledger, retained failures, reviewed source attachments, exact commands/environment and reproduction scripts; it is an evidence package, not an application data root to import.

```powershell
dotnet build GhProjectsBoards.sln -c Release
./scripts/Start-PlanningCheck.ps1 -Scenario Weekly
# Fresh: initial Project setup. Load: 1,000 tasks with labelled retained legacy plans.
./scripts/Start-PlanningCheck.ps1 -Scenario Fresh
# Reopen only an evaluation root printed by the launcher:
./scripts/Start-PlanningCheck.ps1 -Resume -DataRoot '<absolute evaluation root>'
```

Human reevaluation remains required after the demonstrated working loop. Other DPI/scaling, High Contrast/readout, broader Q1 selection/menu/bulk/planning timing combinations, and live GitHub were not newly accepted by these runs. #51/Q2, release/merge, and dependent Summary UI expansion remain separate. No main push, automatic merge/release, machine configuration change or real-user data overwrite occurred.
