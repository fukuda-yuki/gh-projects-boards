# Creation and recovery

Open a registered Project with a checked connection. Add local rows, commit titles, set each actual destination (`owner/repository`) and select supported Project values. Empty preparation rows are allowed. Edits and local saves perform no mutation.

1. Choose **GitHubに反映…** and select creation rows alongside existing updates in **反映内容の確認**. Selection starts empty. Leave incomplete unrelated rows unselected; their preparation reasons remain visible.
2. In this same screen, review the Project/host/account, destination Repository, committed title and field intent. The app checks the latest state automatically. Repository Issue enablement, archive state and creation capability are checked independently of Project permission. Pending text is shown separately as excluded input; internal IDs are available in details.
3. Choose **GitHubに反映（N件）** as final approval, with updated/new Issue counts shown separately. Each row proceeds through Issue creation, received identity, independent Issue verification, exact Project membership, initial-value observation, supported field setup and readback. Only verified completion is reported as completion.
4. Inspect **反映結果・履歴** directly from the Apply command group. Close and reopen the app: history restores without starting writes.

## Ambiguous Issue creation

An uncertain request stays on hold. An Issue ID from a partial response is retained as evidence and independently observed before dependent work. A missing verified identity never triggers an automatic create retry.

In **作成の不確定結果を解決**, choose **保留を続ける**, or enter a same-host Issue URL and choose **URLを独立確認**. Review the actual title, URL, Repository ID and Issue ID, then explicitly bind it. Binding is a reconciliation decision, not proof the original request succeeded, and does not mutate GitHub. Wrong host/repository/type and identities already claimed by another lineage are rejected. Existing shared pending title work must be resolved before binding.

Alternatively choose the separate new-attempt review. It displays the earlier attempt, frozen payload and resolved destination. Check the duplicate-risk acknowledgement before approving a new creation. Every earlier result remains in the checkpoint. A successful new attempt does not establish that the earlier attempt created nothing.

For known Issues, use the relevant batch's **この実行の未完了を確認して再開**. Membership is checked using the exact Issue and Project; incomplete reads never prove absence. **既知Issueの設定を再比較** explicitly reviews current local select choices against current server values when stale values or changed options require a new authorization. Removed fields are explicitly listed for withdrawal, with their earlier intent retained in history.

## Retained local editing

Approval freezes its payload. A later committed title B and pending native text C survive an approved title A becoming a verified Issue with title A: B remains an unapplied existing-title edit and C remains pending text. Native composition defers editor replacement. Complete Project snapshots, not mutation responses or title equality, drive row promotion. Shared drafts or unavailable remaining fields defer promotion rather than dropping data.

## Live verification

Run `scripts/Test-CreationLive.ps1` only on the authorized sandbox with the existing stored-keyring connection. It launches the ordinary app with isolated profile storage. An external test-only gh proxy forwards guarded requests to real gh, records fixture identities separately, and suppresses the third creation response. The product receives no hidden recovery ID: the public URL inspection/confirmation flow performs reconciliation. Reopening starts no new mutation.

The runner records its baseline, product dispatch intents, independent identity/readback evidence and cleanup under a unique `TestResults/creation-live` directory. It refuses another run if a previous directory lacks verified cleanup. Fixture cleanup deletes only independently identified run-owned sandbox Issues and checks the pre-existing snapshot afterward. Do not equate this automated journey with human IME/UX acceptance, enterprise support or main integration.
