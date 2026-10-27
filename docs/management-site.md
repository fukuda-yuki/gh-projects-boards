# Management progress site

The management progress site is a static GitHub Pages view built from the raw Project records that [#272](https://github.com/fukuda-yuki/gh-projects-boards/issues/272) collects. It belongs to the [#271](https://github.com/fukuda-yuki/gh-projects-boards/issues/271) product track, separate from the WinUI planning editor: different audience, delivered and released on its own.

The public deployment in this repository uses deterministic fictional demo data. Real plan data never enters this public repository.

## Purpose and audience

- Readers are management that oversees all projects (経営層), not the PMO. Everyone, including management, can access GitHub.
- At a phase-gate review, the site answers two questions without opening the app:
  1. How far has each phase progressed, and what remains by phase?
  2. How did work move between GitHub Project states week by week: steady completion, one batch, or work piling up?
- The site shows values only. It has no lateness, forecast, current phase, verdicts, annotations or explanatory cards (no 遅れ, 遅れ見込, 進め方 要確認 or 現在の工程).
- It works for waterfall (工程) and agile (Iteration) Projects and for any Status options a Project defines.

## Commands

The tool is `ghpb-report` in `src/GhProjectsBoards.Report`; run the commands from the repository root with the .NET 10 SDK. The same commands run in the Pages workflow.

~~~powershell
dotnet run --project src/GhProjectsBoards.Report -c Release -- demo --out <folder>
dotnet run --project src/GhProjectsBoards.Report -c Release -- site --data <raw folder> --config <site.json> --out <folder>
~~~

| Command | Input | Output |
| --- | --- | --- |
| `demo --out <folder>` | none; the five demo scenarios are generated from the tracked `evaluation/version-plan*.json` plans embedded in the tool | `<folder>/raw/<owner>/<number>/` raw records in the #272 format, and `<folder>/site.json` |
| `site --data <raw folder> --config <site.json> --out <folder>` | raw records in the #272 format and a site configuration | the static site files under `<folder>` |

- `demo` is deterministic: it uses fixed historical dates, no clock and no network, so two runs produce the same bytes. Its output is generated at build time and is never committed.
- `site` is independent of `demo`. It reads only the raw records and the configuration, so the same command can later build the site from real private data.
- `site` validates every configured Project and computes every page before it writes anything. A configuration or data error exits non-zero and leaves no partial site.
- Usage errors exit with code 2; build and input errors exit with code 1.

## Site configuration

One JSON file names the site and the Projects to show. The configuration supplies only what the record cannot know: the display name, the two fields the pages present, and the period management uses.

~~~json
{
  "title": "プロジェクト進捗",
  "demo": true,
  "projects": [
    {
      "url": "https://github.com/users/demo/projects/1",
      "name": "第2027.04版",
      "stateField": "Status",
      "groupField": "工程",
      "periodStart": "2026-10-13",
      "periodEnd": "2027-04-30"
    }
  ]
}
~~~

| Key | Required | Meaning | Default / rule |
| --- | --- | --- | --- |
| `title` | yes | Site title shown in the page header and the browser title | — |
| `demo` | no | Shows the デモデータ label on every page | `false` |
| `projects` | yes | Non-empty array of Projects; the list page sorts its rows by period end | — |
| `projects[].url` | yes | Project URL, `https://github.com/users/<owner>/projects/<number>` or `https://github.com/orgs/<owner>/projects/<number>` | — |
| `projects[].name` | yes | Display name used in the list, page titles and CSV download names | — |
| `projects[].stateField` | no | Project field whose values the 状態の推移 chart presents | `Status`; must be `SINGLE_SELECT` in the record |
| `projects[].groupField` | yes | Project field whose options form the group rows and the table title | Must be `SINGLE_SELECT` or `ITERATION` in the record |
| `projects[].periodStart`, `periodEnd` | no | Period management assigns to the Project, `yyyy-MM-dd` | Both or neither; without a period the list shows `—` and sorts the Project last |
| `projects[].notStartedStatuses` | no | Status option names that mean not started, used to read 着手日 for the CSV (#308) | Default no Status, `Backlog`, `Todo`, `Ready`; matched by option name |

- The raw records for a Project live at `<data>/<owner>/<number>/`, using the owner and number in the Project URL. The host and any path prefix before `users/` or `orgs/` are not part of the folder layout, so the same configuration works with GitHub Enterprise Cloud.
- Configuration and data errors fail the command with a message that names the Project (its `name`, or `projects[<i>]` when the name itself is missing) and the field. Examples:
  - `プロジェクト 第2027.04版 の url がありません。`
  - `プロジェクト 第2027.04版 の期間は periodStart と periodEnd の両方を指定してください。`
  - `プロジェクト 第2027.04版 の列 工程 が見つかりません。`
  - `プロジェクト 第2027.04版 の記録がありません: <path>`
- Duplicate Projects, a URL without `users`/`orgs` and `projects/<number>`, a date that is not `yyyy-MM-dd`, a state field that is not a `SINGLE_SELECT` and a group field that is neither `SINGLE_SELECT` nor `ITERATION` are rejected the same way.

## Definitions

| Term | Definition |
| --- | --- |
| Task (タスク) | A leaf Issue: an Issue item that is not the parent of another item in the same Project on that day. Archived items count like any other item. Pull requests, drafts, REDACTED items and parent Issues (requirements) are not tasks. |
| 完了 | A task whose Issue state is `CLOSED`, whatever the close reason (completed or not planned). A reopened Issue is not complete. The close date is `closedAt` as a date in JST. The header, the list and the group table use this definition. |
| 着手日 / 完了日 | The records the app shows on its plan sheet (#308): the latest Status change from a not-started option to a started option in this Project, and the latest close as completed while the Issue is closed (a close with no state reason counts as completed; not-planned and duplicate closes give none). Both are dates in JST taken from the recorded Issue timeline; the CSV prints them with who made the change. |
| 状態 | The recorded value of the Project's configured state field. Tasks are counted in the field definition's own option order; a task with no value is 未設定 and comes after the options. Every task, open or closed, is stacked by its recorded Status, so 完了 and Status may disagree; the page does not explain the difference. |
| Group (グループ) | One configured `SINGLE_SELECT` or `ITERATION` field per Project: 工程 for a waterfall Project, an Iteration or Status field for an agile one. Rows follow the field's option order, iterations by start date; tasks with no value form a final 未設定 row. The table title uses the field's name. |
| Day / week | Days and weeks are JST; a week runs Monday–Sunday. The chart covers the first recorded week through the latest record. A week's bar is the last recorded day in that week, and the current week is the latest record. Weeks before the first record are not drawn. A week without a record is an empty slot: it has no bar and values are not carried forward. |
| Latest record | The newest raw record for the Project. It supplies the counts, the state composition bar, the group table and the CSV. The chart uses every record. |

## Recorded Issue history (#308)

The raw record keeps each Issue's Status-change, close and reopen events from the Issue timeline — time, actor, previous and new Status, Project and close reason — on that Issue's item line, so a copy of the history survives a transfer or deletion of the Issue. The site's pages do not chart this history; the CSV reads it to print 着手日, 完了日 and the two actor columns with the same not-started options the planning editor uses. The recorder reads the events with the day's Project read; because the record still stores one line per item, an Issue whose events did not change is stored by the monthly full copy and the daily change files exactly as before.

## Pages

Both pages are laid out for the management display at 1920×1080 and fit without page scrolling. They use Japanese labels and one colour palette in the state option order, with neutral grey for 未設定. Colours follow each Project's own option order, so the same colour can mean different states in different Projects. The detail page has one legend (凡例) naming every state of its Project; the list page has no page legend and instead names each row's states and counts under its bar, so colour is never the only carrier.

### Project list

One row per Project:

| Column | Content |
| --- | --- |
| プロジェクト | Display name, with the group field name below it |
| 期間 | `periodStart – periodEnd`, or `—` when the Project has no period |
| 完了 | Closed tasks / tasks, and 完了率 as a percentage |
| 状態 | Composition bar of the latest record's tasks in state option order, with a swatch, state name and count for every non-zero state under it |
| 最終記録日 | Date of the latest record, `yyyy-MM-dd` |
| CSV | Link to the Project's `items.csv` |

- Rows are sorted by period end, with Projects without a period last.
- The whole row opens the Project detail page; the CSV link downloads the file without leaving the list.
- No explanatory cards and no judgement wording.

### Project detail

- **Header:** `← プロジェクト一覧` back link, the デモデータ label while demo is on, the Project name, and the 期間, 完了 (closed / tasks and 完了率), 最終記録日 and CSV entries.
- **状態の推移:** weekly stacked bars of task counts by recorded Status, in the Status option order, from the first recorded week to the latest record. The top of a bar is the Project's task count that week; a week without a record is an empty slot.
- **<group field name>ごとの完了:** a table with the group field, タスク, 完了 and 完了率 columns, rows in the configured group order (then 未設定), and a 合計 total row. A 工程 group titles the table 工程ごとの完了; an iteration field uses its own name.

The detail page has no cumulative line chart, daily completion chart, plan line, scope-over-time line, lateness, forecast or current phase.

## CSV

- One file per Project, from the latest record only. No history and no aggregation.
- Location: `p/<owner>-<number>/items.csv`. The download name is `<display name>_<record date>.csv`.
- One row per item, not only tasks: parent Issues, pull requests, drafts and REDACTED items are included. Rows are ordered by repository, then number; drafts and REDACTED items come last.
- Fixed columns, in this order: 種類, タスク, リポジトリ, 番号, タイトル, 状態, 閉じた理由, 閉じた日, 着手日, 着手した人, 完了日, 完了した人, 親, 担当者, ラベル, マイルストーン, Issue の種類, ブロック元, アーカイブ, 追加日, URL.
- After the fixed columns, one column per Project value field in the record's `fields[]` order, for `TEXT`, `NUMBER`, `DATE`, `SINGLE_SELECT` and `ITERATION` fields. Built-in columns are not repeated.
- Values are printed as recorded: dates as `yyyy-MM-dd` (JST where the record carries a time), numbers in invariant notation, a single-select option by its name and an iteration by its title; a missing value is blank. 着手日 is computed from the latest recorded Status change from a not-started option to a started option, 完了日 from the latest completed close while the Issue is closed (a close with no state reason is completed; not-planned and duplicate closes are not), by the same rules as the app (#308); the person columns name the actor, or `自動（Project のワークフロー）` for GitHub's automation. タスク and アーカイブ print はい or いいえ, 種類 prints Issue, PR, 下書き or 非公開, a merged pull request prints `MERGED` as its state, and assignees, labels and blocked-by Issues join with `; `.
- Format: UTF-8 with BOM, CRLF line endings and RFC 4180 quoting, so Excel opens the file without an import step.
- A cell whose recorded text starts with `=`, `+`, `-` or `@` is printed unchanged, so Excel may read it as a formula. The site does not escape data: the values stay as recorded.

## Content and page chrome

- The デモデータ label appears on every page while `demo` is `true`; the production site turns it off.
- Every page carries a `noindex` meta tag, and the site ships a `robots.txt` that disallows all crawlers.
- The pages and their chart are rendered at build time: inline SVG, one local stylesheet, no scripts and no external requests. The site works without JavaScript and from a local folder.
- All links are relative, so the same output serves the repository page path `/gh-projects-boards/`.
- Structure and labels carry the meaning; the pages do not add instructions or commentary.

## Demo scenarios

`demo` writes five fictional Projects in the #272 raw format plus a site configuration, generated at build time and not committed. Each recorded day goes through the recorder's own storage layout (monthly full copy plus daily changes), so the site reads demo data on the same path as real data. Each demo Issue line carries its recorded events, so the demo CSV shows 着手日 and 完了日. The waterfall Projects derive from the tracked version plans (40 requirements × 25 tasks, 9 phases); the other scenarios add synthetic Projects. Owners and repositories are fictional (`demo/order-system`, `demo/portal`, `demo/mobile-app`). Every Project's latest record is the same day, 2026-10-23 (Friday), and the waterfall plans are shifted by whole weeks from the tracked plans, so the list page compares all Projects at one point in time. The table lists the Projects in configuration order; the list page sorts them by period end.

| Project | Group field | Scenario and what the pages show |
| --- | --- | --- |
| 第2027.04版 | 工程 | Waterfall with steady completion: 1,000 tasks close in plan order; at the latest record the Project is about 86 % complete, the later Status values still hold work, and the header is below 100 % |
| 第2027.10版 | 工程 | Waterfall with a final-week batch: nothing closes until the last recorded week, when most tasks close at once; a few closed tasks stay in Status `In progress`, so 完了 and the chart differ |
| 改善バックログ | Iteration | Agile: 84 flat Issues without parents, Status options `Todo`, `Doing`, `Review` and `Done`, and a current iteration that is only one week old. Most of each iteration's Issues close on its last working day and appear in the batch recorded at the next iteration's first working day; one straggles into the next iteration; Doing and Review hold the work during the iteration |
| 第2027.01版 | 工程 | Zero closed: tasks and requirements stay open, and a whole week without a record leaves an empty slot in the chart (two isolated days are simply absent from the record) |
| 新規プロジェクト | 工程 | Just started: two weeks of records, four tasks closed and four tasks without a Status value (未設定); no period, so the list sorts it last |

The Pages workflow runs `demo` and `site` and deploys the result; the demo folders are build artifacts, never repository content.

## Deployment

- Workflow: [.github/workflows/pages.yml](../.github/workflows/pages.yml) runs on a push to `main` that changes `src/GhProjectsBoards.Core/Reports/**`, `src/GhProjectsBoards.Report/**`, `evaluation/version-plan.json`, `evaluation/version-plan-12m.json` or the workflow itself, and on manual dispatch.
- Jobs: `build` generates the demo records and the site and uploads the site output folder as the Pages artifact; `deploy` publishes it in the `github-pages` environment. The workflow writes its inputs to `demo/` and its output to `site/` at the checkout root. The site output folder is the artifact root, with `index.html` at the top.
- URL: <https://fukuda-yuki.github.io/gh-projects-boards/>
- Owner step: in the repository, Settings → Pages → Source = "GitHub Actions". The deploy job fails until Pages is enabled with that source.
- Tests are not part of this workflow; [ci.yml](../.github/workflows/ci.yml) owns them.

## Out of scope

- The WinUI planning editor.
- The previous snapshot pipeline (`ghpb-progress-snapshot/1`) and its [`progress-report.yml`](../.github/workflows/progress-report.yml) workflow.
- Production hosting: an organisation data repository, private Pages and a GitHub App.
- A backfill of Status history into the weekly chart; it still draws only the recorded weeks. The raw record keeps the events for the CSV (#308).
- Lateness, forecasts, current phase, verdicts and other judgements.

## Related

- [#271 Management progress site](https://github.com/fukuda-yuki/gh-projects-boards/issues/271) owns the site.
- [#272 Raw Project records](https://github.com/fukuda-yuki/gh-projects-boards/issues/272) owns the data and the [`progress-record.yml`](../.github/workflows/progress-record.yml) recorder.
