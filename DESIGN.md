# UI, UX and Information Architecture

This document owns the criteria for judging interface design. [AGENTS.md](AGENTS.md) defines when and how to apply them. The [specification](docs/spec.md) owns concrete product behavior, [decisions](docs/decisions.md) owns accepted choices and their rationale, and Issues/PRs own individual acceptance conditions, exceptions and execution evidence. These principles do not certify the current UI or authorize changing a behavioral contract. Resolve a conflict through the owning Issue and current user direction rather than silently weakening that contract.

## Product design goals

### Useful density and continuous work

- **Principle:** Give users enough information to compare and edit GitHub work across rows without repeatedly leaving the table. Judge density by the decisions it supports, not by how little content is visible.
- **Applies when:** Allocating workspace space, choosing summaries, or deciding whether content belongs beside the table or in details.
- **Exceptions / do not apply:** Do not compress text, targets or spacing until values become unreadable or operations inaccessible. An exceptional state may need more space to explain its consequence and recovery.
- **Good / bad:** A dense table with aligned Issue identities and before/after values supports comparison. Repeated explanations, idle process reports and decorative panels that displace those values add noise. An almost empty screen that hides every difference also fails.
- **Check:** Can the user identify the work, compare relevant values and continue editing without reconstructing context? Do visible elements contribute to the current task?

The product is a PMO planning editor for GitHub Projects: take in the version's existing Issues and their weekly updates, maintain effort, assignments and predecessors while seeing the whole schedule, then publish adopted changes. Creating tasks is occasional. The plan sheet with its row-aligned Gantt is the central screen; the people view answers who exceeds their time, on which days and why, and supports correcting the plan. Comparison against an agreed plan is an optional reference before publishing. Ordinary-state instructions must not displace the work; use concise labels and disclose supplementary explanations on request. The [product outline](docs/requirements.md#product-goal) defines the work it serves.

Follow the conventions PMO users already know from MS Project and the TFS Office integration before inventing new ones: a task sheet with predecessor row numbers, calculated dates that look different from typed dates, and two Project commands, **最新の情報に更新** and **発行**. Show unpublished work as one count and conflicts at publish time. Do not surface internal save, buffer, cache or journal states during ordinary work; expose them only when they explain a failure and its recovery.

The empty workspace offers one next action appropriate to its state. Put setup procedures, keyboard reference and extended explanations in the [user manual](docs/user-manual.md), reached from the app. A manual complements recognizable controls; it must not compensate for an unclear ordinary task. Settings live on one page and never add rows of commands to the planning surface.

## Visual language

### A light Windows 11 planning tool the PMO wants to open

- **Principle:** The app looks like a finished Windows 11 business tool, not a stack of default controls. Use the Fluent structure of WinUI (title-bar content, view tabs, labelled command bars, cards and native pickers) with the density of a task sheet. The look is light. The agreed mockup is the 最終案 artboard of the [#109 design canvas](https://claude.ai/artifact/HnHTkkX8RPA4qtRaV347Ca), direction B's structure with A's density, together with its addendum artboards 追補1–4. The addendum covers lateness chains, the folded overview, no selection, narrow widths, parts and states, and the PMO's ordinary 1920 × 1080 display. Where the addendum and the specification differ from the 最終案, they take precedence. A dark look and an Office-style ribbon are not this product's direction.
- **Applies when:** Changing any screen's layout, typography, color, iconography, spacing or command presentation.
- **Exceptions / do not apply:** High Contrast replaces product colors with system colors. A screen not yet redesigned keeps its behavior until its owning Issue (#110) changes it; new work on it still follows this section.
- **Good / bad:** A Project picker in the title bar, view tabs with a visible current view, Project-wide GitHub commands on the right, and a status bar frame the work. A left pane spent on one Project, rows of identical buttons mixing navigation with actions, icon-only command rows, and raw field names as headers make the app feel unfinished.
- **Check:** Look at the rendered screen with realistic data beside the agreed mockup. Does it read as one deliberate design? Would the PMO choose to work in it for a weekly update? Passing tests or complete features do not answer this question.

Apply these rules across the app:

| Element | Rule |
| --- | --- |
| Theme | Light regardless of the Windows app mode; High Contrast honored. Semantic resources in App.xaml carry every product color. |
| Shell | Title bar: app name, Project picker (registered Projects, Project を開く…, 接続…), settings gear. Below it: view tabs (計画, 担当者) on the left; 状況日, Undo/Redo, 未発行 count, **最新の情報に更新** and **発行…** on the right; the work surface on a card; a status bar with requirement and task counts and nonzero lateness counts; the Gantt legend appears only on 計画. |
| Commands | Every primary command shows an icon and a label. Group by purpose with separators; infrequent commands and commands normally used through standard shortcuts (clipboard) go to the overflow with their shortcuts. One location per command. |
| Typography | Segoe UI Variable / Yu Gothic UI. One page title size, one body size for cells and labels, a smaller secondary size for metadata. Hierarchy comes from weight and color, not from many sizes. Tabular figures for numbers and dates. |
| Color | Accent marks selection, the current view, the status date and the one primary action. Amber marks unpublished work: tint and corner for values the PMO entered, corner only for recalculated dates. Critical red marks overdue work strongly (filled marker and lateness-segment tint, with a red end date only when that row's end moved later than published), later-than-published work quietly (outline only), and conflicts and failures by outline and icon. A critical tint is reserved for overdue work. Green marks completion. Every color state also has a marker, text or accessible name. |
| Sheet | Japanese column headers; a wide task name; quiet separators; requirement rows bold; calculated values in secondary text and typed values in primary text; an indicator column for row state; a selection line explaining the selected task. |
| Gantt | Two-tier timescale; shaded non-working days; one task hue with the completed share solid and the rest tinted; bracket bars for summaries; neutral dependency arrows; an accent status-date line; lateness against the published end drawn as a dashed critical segment. Only overdue work fills it; only at 日 and 週 does that work also label its working-day count. |

## Information architecture and surface roles

### Organize work before arranging navigation

- **Principle:** Define information, actions, ownership and names around the user's work before choosing buttons, panels or routes. IA defines those relationships; navigation gives access to them.
- **Applies when:** Introducing an entry point, moving an operation, naming a surface, or separating normal work from configuration.
- **Exceptions / do not apply:** A task may visit settings to recover a missing prerequisite, but settings should return the user to that task. This does not make all task commands settings. A contextual editing preference may also have a nearby shortcut.
- **Good / bad:** Connection settings configure connection; Project registration adds an existing Project to the local workspace; editing prepares work; review supports a send decision; results explain its outcome. A connection screen that also owns registration and Apply obscures these roles.
- **Check:** Can users predict where an action belongs and what it changes from its name and context? Can they distinguish local preparation, GitHub actions and settings without explanatory narration?

Use the [workspace and settings](docs/spec.md#current-workspace-and-project-settings-81--109), [connection](docs/spec.md#connection-and-api-access), [local document](docs/spec.md#current-local-plan-document-operations-and-storage-78--79--81) and [publishing](docs/spec.md#current-planning-editor-refresh-and-publishing-contract-79) contracts for routes and behavior. These responsibilities do not require one screen per role or a forced wizard.

## Information display policy

### Classify information by the decision it supports

- **Principle:** Choose visibility according to present relevance, frequency and the consequence of missing the information. The four classes below are this product's operating rule, not a standard taxonomy attributed to an external source.
- **Applies when:** Adding a count, identity, explanation, status, comparison value or diagnostic.
- **Exceptions / do not apply:** Visibility is relative to the active task. Information can change class when it becomes relevant; an optional diagnostic becomes prominent when it explains a blocker. Shared context need not be repeated in every row if its association remains clear.
- **Good / bad:** Show the target and comparison needed for a send decision together. Offer full technical identity on request when readable labels distinguish targets; expose a disambiguator immediately when names collide. Hiding necessary differences behind one expansion per row prevents comparison.
- **Check:** For each element, identify the decision it supports and why its class fits. Can users make the ordinary decision without opening every detail, and find exceptional information when it matters?

| Class | Decision rule | Product examples |
| --- | --- | --- |
| Always visible | Needed for the ordinary decision at this stage; no extra disclosure action required. Ordinary table scrolling remains available. | Shared destination context; row/field identity and readable before/after values in a review; the principal action. |
| Conditional | Needed when a meaningful condition changes the next action or confidence in the data. | Conflicts, failed saves, incomplete checks, hidden work relevant to the operation, or a reason an action cannot run. |
| On request | Supplementary information with a discoverable, keyboard-accessible route. | Unabridged long text, internal IDs, technical diagnostics and infrequent configuration. |
| Omitted | No contribution to the present decision and no loss of required information or access. | Duplicate explanations, irrelevant zero-count categories and redundant visual success messages. |

### Remove meaningless summaries without erasing meaning

- **Principle:** Omit redundant summaries, not data or the evidence needed to interpret it. Absence, emptiness, zero, unavailable and unverified are different states.
- **Applies when:** Simplifying counts, empty states, value formatting or normal-state messages.
- **Exceptions / do not apply:** Zero is useful when it explains an outcome or disabled action. A real numeric zero is data. Unretrieved information must never be inferred as an empty result or a zero count.
- **Good / bad:** Omit “New Issues: 0” when creation is irrelevant; keep “Selected: 0” when it explains why Apply is unavailable. Label an unavailable GitHub value rather than displaying an empty string or treating it as deleted.
- **Check:** Compare known zero, a blank value, no selection, not-yet-retrieved data and failed retrieval. Can the user distinguish them and understand the next action?

## Interaction and context

### Keep comparison, correction and return connected

- **Principle:** Place actions near their targets and keep related values together. Preserve the user's place while revealing detail, correcting work or updating a review.
- **Applies when:** Designing review tables, conflict resolution, details, settings roundtrips or asynchronous presentation updates.
- **Exceptions / do not apply:** Preserving context does not mean retaining an invalid target or approval after identity or data changes. If the prior item is no longer visible, provide an understandable return location and explain any required reselection. In-session continuity does not imply new persisted viewport settings.
- **Good / bad:** Show a conflict and its resolution beside the affected field; provide a discoverable route to an offscreen problem. Keep short differences readable and let long text open in full without losing the row. Requiring users to memorize one list and find matching entries in another, or resetting scroll after every check, breaks continuity.
- **Check:** Review several rows, open full text, resolve a problem, repeat a check and return from settings. Can users locate the same work and understand any necessary context change? Selection, focus and pending input must follow the [input contract](docs/spec.md#current-local-plan-document-operations-and-storage-78--79--81).

### Keep essential operations reachable

- **Principle:** Adapt presentation to available space without removing the path to required work. Use a coherent scroll and focus model for a comparison surface.
- **Applies when:** Handling narrow windows, scaling, command overflow, long lists or details within a collection.
- **Exceptions / do not apply:** Secondary commands can move into a labelled overflow or contextual surface; horizontal scrolling can be necessary for a table. Separate scrolling regions need distinct purposes and predictable keyboard access, not competing control of the same list.
- **Good / bad:** Constrain a native scrolling collection to available space and let it own its viewport. Keep a required command reachable through native overflow at narrow widths. Wrapping a scrolling collection in another same-direction ScrollViewer, or hiding the only command route, creates avoidable navigation problems.
- **Check:** At the supported sizes and scales, reach the final row, full differences, recovery commands and primary action using the keyboard. Verify that content and focus are not clipped or stranded. Concrete dimensions belong to the specification or owning Issue.

## State, errors and language

### Explain consequences and recovery with proportionate feedback

- **Principle:** Emphasize states according to how they affect the user's decision. State what happened, what remains uncertain and what the user can do, close to the affected operation.
- **Applies when:** Showing progress, connection state, save status, validation, execution outcomes or disabled actions.
- **Exceptions / do not apply:** Suppressing redundant visual success feedback must not remove status communication for assistive technology. A global problem can need a shared summary, with access to affected work. Required outcome history remains available even when an additional notification is unnecessary.
- **Good / bad:** Distinguish local saving from GitHub completion, and identify failed or uncertain fields with a recovery path. Keep necessary progress visible while checking. Repeating “ready” across the workspace, announcing every internal step, or calling a partly failed row successful misdirects attention.
- **Check:** Can the user tell whether work was sent, which result is verified, and what to do next? Can a screen-reader user receive relevant state changes without relying on a visual toast? Check behavior against the [publishing contract](docs/spec.md#current-planning-editor-refresh-and-publishing-contract-79), not a simplified success/failure model.

### Name the user's action

- **Principle:** Put the important information first; use concrete verbs, understandable nouns and explicit count units. Use structure and labels to communicate roles instead of compensating with paragraphs of instructions.
- **Applies when:** Writing commands, headings, errors, summaries and accessible names. Product examples below use the Japanese UI language.
- **Exceptions / do not apply:** Technical detail can be necessary for diagnosis or disambiguation; provide it where needed with readable context. A compact label must not obscure a consequential action or destination.
- **Good / bad:** “GitHubに反映…” names the destination and next workflow; an unexplained “Execute” or “Sync” does not. A count identifies Issues, fields or rows instead of mixing their totals. An error explains the affected work and available recovery before technical details.
- **Check:** Read the screen without its helper paragraphs. Can users predict the effect of each main command and understand every count? Do visible and accessible labels convey the same action?

## Visual hierarchy and accessibility

### Make dense content legible and operable

- **Principle:** Use alignment, hierarchy, semantic resources and native interaction to make important information distinguishable across input methods and display settings.
- **Applies when:** Choosing emphasis, spacing, truncation, markers, labels, focus cues, themes or control presentation.
- **Exceptions / do not apply:** Density does not justify tiny text, clipped targets or hover-only access. Detail can contain full long values only when the visible summary still supports the ordinary decision and signals truncation. Custom presentation must preserve native input and accessibility responsibilities.
- **Good / bad:** Align related fields, distinguish focus from selection and editing, and pair a problem's color with a readable label or marker. Provide full text through a keyboard-accessible surface. A color-only conflict, placeholder-only label, or tooltip-only route to necessary content excludes users.
- **Check:** Inspect real rendered content with keyboard navigation, text/display scaling, Light, Dark and High Contrast as relevant to the change. Check accessible names and status, visible focus, full-value access and return focus. Follow the [test policy](tests/README.md#test-policy) for execution scope; passing automation alone does not establish readability or human acceptance.

Use the semantic resources and styles in [App.xaml](src/GhProjectsBoards.App/App.xaml) as the implementation reference for current visual values. Do not copy their numeric values into this document or treat their existence as proof of visual approval. Concrete input behavior remains in the [specification](docs/spec.md#current-local-plan-document-operations-and-storage-78--79--81).

## Review criteria and references

### Review application

For each affected task, use the checks attached to the applicable principles. Record the user's decision, relevant principles, any justified exception, observed results and unverified conditions in the owning Issue/PR. The entry procedure lives in [AGENTS.md](AGENTS.md#ui-design-and-review); execution selection and evidence rules live in the [test policy](tests/README.md).

Use these scenarios to challenge a proposed design, not as a mandatory whole-application suite for every change:

| Scenario | Design question |
| --- | --- |
| No creation work, no selection, or a real zero value | Are irrelevant summaries omitted while meaningful zeroes and disabled-action reasons remain clear? |
| Not retrieved, failed retrieval, empty value or uncertain outcome | Are knowledge states distinguishable without inventing data or success? |
| Several short differences and one long difference | Can users compare ordinary values without expanding every row, then read the long value fully and return to the same work? |
| A conflict outside the current viewport | Is the problem discoverable and reachable, with the affected target and recovery in context? |
| Narrow or scaled layout | Are needed commands, identities and values reachable without competing scroll regions or clipping? |
| Keyboard and assistive-technology use | Can users select, inspect, act and return with meaningful labels, visible focus and perceivable status? |

Dataset sizes, visible-row thresholds, performance targets and screenshots belong to the individual Issue's acceptance conditions. A documentation review establishes the usefulness and consistency of these rules, not that the running product satisfies them.

### Baseline usability checklist

These are general expectations of any business application, drawn from the sources named in each row, not product decisions. A design that misses one needs no owner report to be called a defect. Audit the ordinary app with version-scale data at the PMO's 1920 × 1080 display, and record each row's result (met, partly met, not met, not verified) with its evidence in the owning Issue, never in this document. Product decisions that go beyond these rows belong to the requirements and specification.

| ID | Check | Source |
| --- | --- | --- |
| S1 | Direct manipulation (selection, typing, toggles, scrolling) responds within about 0.1 s; nothing the user did appears ignored. | NN/G response times |
| S2 | An operation that can exceed 1 s shows, where the user is looking, that work is under way. Beyond 10 s it names the current step, shows progress when known (n of N) and offers a safe way to stop. | NN/G response times; Nielsen 1 |
| S3 | While input is blocked, the blocked surface looks blocked, with a blocking indicator and a label, rather than only disabled buttons. A non-blocking indicator is used only while the user can keep working. | Microsoft progress controls; DADS progress indicator |
| S4 | A wait that affects part of the screen does not animate the rest. Skeleton placeholders are not used. | DADS progress indicator |
| S5 | The user can always tell which Project and view are open, how current the GitHub data is, and how much local work is unpublished. | Nielsen 1 |
| S6 | The outcome of a long operation stays visible until it is superseded; success needs no modal dialog. | DADS modal dialog; Primer notification messaging |
| W1 | Labels use the words Japanese PMOs already use for the concept (MS Project, TFS, SI practice), not literal translations or coined terms. One concept has one term across the UI, the manual and messages. | Nielsen 2, 4 |
| W2 | Units (h, 日, 件) are explicit and consistent; each context uses one date format. | Nielsen 4 |
| W3 | A message states what happened, its effect on the plan and what to do, in plain Japanese; technical detail is available on request. | Nielsen 9; Microsoft writing style |
| C1 | Local changes can be undone one by one, and there is a clearly marked way back to the last known good state. A destructive command states its scope and can itself be undone; a confirmation dialog is not the only safeguard. | Nielsen 3; DADS modal dialog |
| C2 | Every mode (editing, review, long operation) has a visible exit that says what it keeps or discards. | Nielsen 3 |
| K1 | Editable, calculated and non-editable values look different. A value that cannot be edited never looks or behaves like an input: no field chrome, caret, editor on click or F2, or fill handle. | DADS input text (readonly); Nielsen 4, 5 |
| K2 | A command or value that is unavailable says why, next to it or when focused, and stays reachable by keyboard when it carries information. | DADS button (disabled) |
| K3 | A command has one name, one place and one shortcut. Windows and Excel conventions (Ctrl+Z/Y, F2, Esc, Delete, Ctrl+C/V/D) behave as users expect. | Nielsen 4; Microsoft CommandBar |
| E1 | Invalid input is caught at its cell with a reason before it affects other work. An operation that would lose work asks first or can be undone. | Nielsen 5 |
| R1 | What a decision needs (identity, values, reason) is on one screen; users do not carry values between screens in memory. | Nielsen 6 |
| R2 | Column headers, abbreviations and markers are understandable without the manual; tooltips explain the non-obvious. | Nielsen 6, 10 |
| F1 | Frequent work has keyboard paths and range operations; experienced users can tailor frequently used views. | Nielsen 7 |
| M1 | No animation or transition that does not explain a state change. Scrolling and resizing do not flicker, blank out or shift content. | Nielsen 8; Microsoft motion |
| M2 | Each visible element supports the current decision. | Nielsen 8 |
| D1 | A failure names the affected work and offers recovery where it happened; retry keeps the user's place. | Nielsen 9 |
| H1 | The manual is reachable from the app and uses the current UI terms. | Nielsen 10 |
| A1 | Text contrast is at least 4.5:1 (3:1 for large text); controls, focus and state markers at least 3:1. | WCAG 2.2 AA / JIS X 8341-3:2016 |
| A2 | Every function works by keyboard with visible focus in visual order; focus returns after a flyout or dialog closes. | WCAG 2.2 AA; DADS accessibility |
| A3 | Accessible names match visible labels; progress, completion and errors are announced. | WCAG 2.2 AA |
| A4 | Color is never the only carrier of a state. | WCAG 2.2 AA |
| A5 | At 200% Windows text size, content and commands remain reachable. | WCAG 2.2 AA |
| P1 | Edits reach the screen within the specified target at version scale, and sheet rows and Gantt bars stay in step while scrolling. | NN/G response times; [specification](docs/spec.md#timeline-and-measurement) |
| I1 | Editing, monitoring, configuration and publication review are separate, clearly named surfaces; leaving settings returns to the task. | NN/G IA; Microsoft app settings |

### Reference scope

References consulted on **2026-09-19**, with later additions dated in their rows. The adopted criteria above are maintained here; upstream changes do not automatically change this product's contract. External examples inform decisions and do not mandate web components, a screen layout or new dependencies.

| Source | Adopted idea and boundary |
| --- | --- |
| NN/G: [10 Usability Heuristics](https://www.nngroup.com/articles/ten-usability-heuristics/) and [Response Times: The 3 Important Limits](https://www.nngroup.com/articles/response-times-3-important-limits/) (reviewed 2026-10-10) | Backbone of the baseline checklist ("Nielsen 1–10" refers to the heuristics in their published order). The 0.1 s / 1 s / 10 s limits set when feedback, a progress indication and a way to stop are expected. |
| デジタル庁デザインシステムβ版 Markdown, 2026-10-08 edition ([resources](https://design.digital.go.jp/dads/resources/); 出典：デジタル庁デザインシステムウェブサイト https://design.digital.go.jp/dads/) (reviewed 2026-10-10) | Japanese-language guidance adopted for progress indicators (labelled indicator for whole-screen waits, no skeleton UI), avoiding disabled and readonly inputs that look usable, modal dialog restraint, and destructive actions that can be undone. It is a web design system for public services; its components, colors and layout are not adopted. |
| Microsoft Learn: [Progress controls](https://learn.microsoft.com/en-us/windows/apps/design/controls/progress-controls) (reviewed 2026-10-10) | An indeterminate ProgressBar means the user can keep working; an indeterminate ProgressRing means interaction waits. Pair either with text naming the operation. |
| W3C [WCAG 2.2](https://www.w3.org/TR/WCAG22/) level AA, which JIS X 8341-3:2016 aligns with through WCAG 2.0 (reviewed 2026-10-10) | Contrast, keyboard, name, color-independence and text-resize checks A1–A5 for a desktop app. Web-only success criteria do not apply. |
| Microsoft Learn: [Motion in Windows apps](https://learn.microsoft.com/en-us/windows/apps/design/motion/) (reviewed 2026-10-10) | Motion should have a purpose, such as explaining a change; decorative motion is not adopted. |
| [impeccable](https://github.com/pbakaus/impeccable) (reviewed 2026-10-10) | Not adopted yet. Its deterministic detector targets web HTML/CSS, not a WinUI desktop app; its platform-independent critique and audit heuristics are trialled against the checklist in [#119](https://github.com/fukuda-yuki/gh-projects-boards/issues/119). The installed Microsoft WinUI design skill remains the platform reference. |
| GitHub [Projects table layout](https://docs.github.com/en/issues/planning-and-tracking-with-projects/customizing-views-in-your-project/customizing-the-table-layout), Primer [Empty states](https://primer.style/product/ui-patterns/empty-states/), and Microsoft [CommandBar](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/command-bar) (reviewed 2026-10-04) | Keep the table central, configuration grouped, the empty state's next action singular, and commands in native overflow. Preserve target identity, recovery and keyboard access. These references inform concrete behavior, not a claim of comparative usability or a requirement to copy web components. |
| Azure Boards [Bulk modify work items](https://learn.microsoft.com/en-us/azure/devops/boards/backlogs/bulk-modify-work-items?view=azure-devops) (reviewed 2026-10-04) | Bulk editing already exists in established products. This product must justify its value through the connected local planning, actual/remaining correction, load comparison and explicit publication job, not through the existence of a table or bulk command alone. |
| MS Project [views](https://support.microsoft.com/en-gb/office/overview-of-project-views-6cb1dbcd-5cd5-4cc2-a878-aa365564266d) and Azure Boards [capacity](https://learn.microsoft.com/en-us/azure/devops/boards/sprints/set-capacity?view=azure-devops) (reviewed 2026-10-05) | The Gantt Chart view (task sheet plus row-aligned chart) shapes the plan sheet; Resource Usage and per-person capacity bars shape the people view. Adopt their familiar structure and terms, not their full feature sets. |
| OpenAI: [Harness engineering](https://openai.com/index/harness-engineering/) and [AGENTS.md guidance](https://developers.openai.com/codex/guides/agents-md/) | A concise entry point routes agents to documents with distinct responsibilities. Explicitly require reading this document through AGENTS.md; its filename alone is not a default instruction-loading mechanism. Do not transplant the article's repository structure or automation system. |
| NN/G: [Aesthetic and Minimalist Design](https://www.nngroup.com/articles/aesthetic-minimalist-design/) | Prioritize useful information and reduce competing noise; this is not a requirement for sparse screens. |
| NN/G: [Progressive Disclosure](https://www.nngroup.com/articles/progressive-disclosure/) | Keep frequent needs accessible and reveal secondary detail when needed. Do not hide ordinary comparison requirements. |
| NN/G: [IA and Navigation](https://www.nngroup.com/articles/ia-vs-navigation/) | Separate organization, relationships and naming from the controls used to access them. |
| NN/G: [Recognition and Recall](https://www.nngroup.com/articles/recognition-and-recall/) | Keep the information needed for a decision recognizable together rather than requiring memory across surfaces. |
| GOV.UK: [Details](https://design-system.service.gov.uk/components/details/) and [Check answers](https://design-system.service.gov.uk/patterns/check-answers/) | Specify use and non-use conditions; connect review with correction and clear submission actions. Do not transplant a long application-form layout into a bulk editor. |
| Primer Product UI: [Notification messaging](https://primer.style/product/ui-patterns/notification-messaging/) and [Progressive disclosure](https://primer.style/product/ui-patterns/progressive-disclosure/) | Use proportionate, contextual feedback and retain context during disclosure. Avoid redundant visual success feedback while communicating state accessibly. No React components or Brand UI styling are adopted. |
| Microsoft Learn: [App settings](https://learn.microsoft.com/en-us/windows/apps/design/app-settings/guidelines-for-app-settings) and [Writing style](https://learn.microsoft.com/en-us/windows/apps/design/style/writing-style) | Separate configuration from normal work; use concise, action-oriented language. Product-specific routes remain in the specification. |
| Microsoft [win-dev-skills](https://github.com/microsoft/win-dev-skills), [winui-design/SKILL.md](https://github.com/microsoft/win-dev-skills/blob/main/plugins/winui/agent-plugin/skills/winui-design/SKILL.md) | Consulted installed plugin version **0.6.1**, path `agent-plugin/skills/winui-design/SKILL.md`; upstream path `plugins/winui/agent-plugin/skills/winui-design/SKILL.md`. Use concrete WinUI constraints and alternatives, including collection scrolling, command reachability and theme accessibility. The upstream main link is mutable; the installed version identifies the consulted skill. Generic control suggestions do not override the product's IA, editable-table contract or repository test policy. |
| Stitch [design-md/SKILL.md](https://github.com/google-labs-code/stitch-skills/blob/main/plugins/stitch-utilities/skills/design-md/SKILL.md) | Supplementary reference for describing approved visual direction only. It is not the template for this document; extracting an existing screen's appearance does not establish that the design is desirable or accepted. |
