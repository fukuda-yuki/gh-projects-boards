# UI, UX and Information Architecture

This document owns the criteria for judging interface design. [AGENTS.md](AGENTS.md) defines when and how to apply them. The [specification](docs/spec.md) owns concrete product behavior, [decisions](docs/decisions.md) owns accepted choices and their rationale, and Issues/PRs own individual acceptance conditions, exceptions and execution evidence. These principles do not certify the current UI or authorize changing a behavioral contract. Resolve a conflict through the owning Issue and current user direction rather than silently weakening that contract.

## Product design goals

### Useful density and continuous work

- **Principle:** Give users enough information to compare and edit GitHub work across rows without repeatedly leaving the table. Judge density by the decisions it supports, not by how little content is visible.
- **Applies when:** Allocating workspace space, choosing summaries, or deciding whether content belongs beside the table or in details.
- **Exceptions / do not apply:** Do not compress text, targets or spacing until values become unreadable or operations inaccessible. An exceptional state may need more space to explain its consequence and recovery.
- **Good / bad:** A dense table with aligned Issue identities and before/after values supports comparison. Repeated explanations, idle process reports and decorative panels that displace those values add noise. An almost empty screen that hides every difference also fails.
- **Check:** Can the user identify the work, compare relevant values and continue editing without reconstructing context? Do visible elements contribute to the current task?

The product goal is a native Windows editing workspace, not a marketing page, a status dashboard or a reproduction of a web component library. The [product outline](docs/requirements.md#product-goal) defines the work it serves; this document does not select a new grid or invent a visual theme.

## Information architecture and surface roles

### Organize work before arranging navigation

- **Principle:** Define information, actions, ownership and names around the user's work before choosing buttons, panels or routes. IA defines those relationships; navigation gives access to them.
- **Applies when:** Introducing an entry point, moving an operation, naming a surface, or separating normal work from configuration.
- **Exceptions / do not apply:** A task may visit settings to recover a missing prerequisite, but settings should return the user to that task. This does not make all task commands settings. A contextual editing preference may also have a nearby shortcut.
- **Good / bad:** Connection settings configure connection; Project registration adds an existing Project to the local workspace; editing prepares work; review supports a send decision; results explain its outcome. A connection screen that also owns registration and Apply obscures these roles.
- **Check:** Can users predict where an action belongs and what it changes from its name and context? Can they distinguish local preparation, GitHub actions and settings without explanatory narration?

Use the [workspace](docs/spec.md#workspace-presentation), [connection](docs/spec.md#connection-and-api-access), [registration](docs/spec.md#project-registration-and-cache), [editing](docs/spec.md#editing-and-drafts), [Apply](docs/spec.md#existing-field-apply) and [creation](docs/spec.md#new-issue-creation) contracts for actual routes and behavior. These links define responsibilities, not a requirement for one screen per role or a forced wizard.

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
- **Check:** Review several rows, open full text, resolve a problem, repeat a check and return from settings. Can users locate the same work and understand any necessary context change? Selection, focus and pending input must follow the [input contract](docs/spec.md#selection-input-and-rectangular-operations).

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
- **Check:** Can the user tell whether work was sent, which result is verified, and what to do next? Can a screen-reader user receive relevant state changes without relying on a visual toast? Check behavior against the [Apply contract](docs/spec.md#existing-field-apply), not a simplified success/failure model.

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

Use the semantic resources and styles in [App.xaml](src/GhProjectsBoards.App/App.xaml) as the implementation reference for current visual values. Do not copy their numeric values into this document or treat their existence as proof of visual approval. Concrete input behavior remains in the [specification](docs/spec.md#selection-input-and-rectangular-operations).

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

### Reference scope

References consulted on **2026-09-19**. The adopted criteria above are maintained here; upstream changes do not automatically change this product's contract. External examples inform decisions and do not mandate web components, a screen layout or new dependencies.

| Source | Adopted idea and boundary |
| --- | --- |
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
