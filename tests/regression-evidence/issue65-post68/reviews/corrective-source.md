# レビュー結果

これは、添付された corrective patch と修正後ファイルを、統合済み main `ab292d50f02d4e373871c48f408ce1151bf7a169` の未変更協調コードまで追って行った静的レビューです。テストやアプリは実行しておらず、性能・IME・人手受入は合格扱いしていません。

**残存する具体的な指摘は、High 4件、Medium 2件、Low 1件です。**
加えて、性能についてソース上確認できる無駄な処理が1点ありますが、観測された約125ms停止の原因だとはまだ断定できません。

---

## 1. High — 初回保存失敗後の再試行は、依然として `.tmp` の rename 成功に依存している

**場所:** `src/GhProjectsBoards.Core/Projects/DraftStore.cs`
**メソッド:** `SaveAsync`, `LoadAsync`, `HasInterruptedSave`, `CheckpointsAsync`

修正は、失敗した保存呼出しが所有する `.tmp` を `.tmp.rejected` へ rename しています。しかし、その rename が共有違反、ウイルス対策ソフト、アクセス権、ファイルシステム障害などで失敗すると例外を握り潰し、元の `.tmp` が残ります。次回の明示的な保存は引き続き recovery-sensitive な `LoadAsync` を呼ぶため、canonical `.json` がない状態ではその `.tmp` を検出して、保存候補を書き始める前に再び失敗します。つまり、元の「初回保存を一度拒否すると以後の再試行もできない」という欠陥は、rename 失敗時には残っています。 

**最小再現:**

1. canonical `.json` がない状態で保存する。
2. `canCommit` 内で、生成済み `.tmp` を `FileShare.Delete` なしで開いたまま `false` を返す。
3. catch 内の `File.Move(temp, temp + ".rejected")` が共有違反になり、元の `.tmp` が残る。
4. ハンドルを閉じて同じワークスペースを再保存する。
5. `LoadAsync` が残った `.tmp` を理由に再試行を拒否する。

追加テストは、rename が成功する通常経路と同一プロセス内の即時再試行だけを確認しており、この境界を通っていません。

もう一つの問題は、rename が成功した場合です。`.tmp.rejected` は `HasInterruptedSave`、`LoadAsync`、`CheckpointsAsync` のいずれからも見えません。プロセスが新しい canonical を確立する前に終了すると、候補 bytes は残っていますが、次回起動では「中断候補なし、保存済みチェックポイントなし」と扱われます。自動採用すべきではありませんが、少なくとも recovery evidence として表示される必要があります。現在の inventory は `.bak` と末尾 `.tmp` だけを扱います。 

**データ保持型の修正:**

* `SaveAsync` の CAS 比較専用に、canonical `.json` だけを読む private メソッドを設ける。
* writer lock 保持中の再保存では、既存 `.tmp` を無視して canonical revision だけを比較する。
* 失敗候補は元の `.tmp` のまま bytes を保存し、rename、削除、自動採用をしない。
* 公開 `LoadAsync` と起動時 inventory は引き続き `.tmp` を recovery evidence として報告する。
* 再試行は新しい GUID の `.tmp` から canonical を確立し、古い候補はそのまま残す。

`DraftSession.FlushCoreAsync` の「失敗中に到着した新しい retry request を次の反復へ持ち越す」部分自体は妥当です。また、candidate workspace は durable save 後にしか live workspace と交換されません。問題は、その下の `DraftStore` が再試行前に停止することです。 

---

## 2. High — 作成 identity 昇格で、既存の dependency field と衝突すると先行関係を黙って失う

**場所:** `src/GhProjectsBoards.Core/Projects/CreationPlanning.cs`
**メソッド:** `PlanningIdentityOccupied`, `PromotePlanning`
**呼出し:** `CreationJournal.cs` の `PromoteCreatedRows`

現在の preflight は次を検査しています。

* target Issue と同じ `PlanningTask.Id`
* protected baseline 内の同じ `TaskId`
* local ID を remote ID に置換した後の `LocalLinks` 重複
* target Issue の Project item に属する active field
* `Dependency` の **`NodeId == issueId`**

しかし dependency field の key は、`NodeId` が後続タスク、`FieldId` が先行 Issue です。したがって、たとえば `I2 → local-A` を `I2 → I1` に昇格させる際、既存の `Dependency(NodeId=I2, FieldId=I1)` は preflight に捕まりません。

その後 `PromotePlanning` は同じ key が既に存在すると `ContainsKey` により何もしません。続いて、native dependency が complete なタスクから promoted `LocalLinks` を削除します。このため、既存 field が「I1を外す」という draft、競合、未確認状態を持っていても、`local-A` 由来の「I1を追加する」という intent が黙って消えます。

**最小再現:**

* `I2.LocalLinks = [FS(local-A)]`
* 既存 field `Dependency(I2, P1, I1)` の effective value が `null`、または clear/conflict/pending
* `local-A` が `I1` と検証済みになる
* task/baseline には別の `I1` identity がなく、LocalLinks の二重辺もない

preflight は通過します。`ContainsKey` により `present` が作られず、最後に LocalLink が消えます。checkpoint validation も通るため、これは検出可能な失敗ではなく、durable な intent loss です。

caller 側で preflight 自体は title/select transfer より先に実行されています。そのため、preflight を完全にすれば、拒否時に local row、fields、history、journal、baseline を保ったまま停止できます。

**データ保持型の修正:**

* identity 置換によって作られる全 dependency key を、`PromoteCreatedRows` が何かを転送する前に列挙する。
* 既存 key がある場合、effective value だけでなく `Baseline`、`Change/Clear`、`Buffer`、`Conflict`、`Observation` と edge payload を比較する。
* 完全に同値なら明示的に coalesce する。
* それ以外は昇格全体を拒否し、local row と verified lineage evidence を保持する。
* `PromotePlanning` 内の無条件 `if (!fields.ContainsKey(key))` は廃止し、preflight 済みの merge または throw にする。

追加された dependency collision テストは `LocalLinks` 同士の重複を扱っていますが、既に存在する `DraftField` key との衝突は扱っていません。

---

## 3. High — canonical appearance の同値判定が pending buffer と draft semantics を比較していない

**場所:** `src/GhProjectsBoards.Core/Projects/PlanningWorkspace.cs`
**メソッド:** `PlanFor`, `ProjectPlan`

`DistinctBy` を `GroupBy` に変更し、異なる mapped values を `SourceProblem` にする方向は正しいです。実際、異なる committed value、reason、非通常 availability、conflict は検出され、generated projection はスキップされます。 

しかし、現在の comparator は主に次だけを見ています。

* `Reason`
* 一部の `Availability`
* `Value(cell)`
* `Observation.Reason`
* `Conflict`

`Value(cell)` は committed `Change` または `Baseline` であり、`Buffer` を含みません。また、同じ effective value でも、一方が explicit clear/draft、もう一方が remote baseline という差も失われます。したがって、「同じIssueの一方の appearance では16を入力中、もう一方はcommitted 8」という状態が canonical conflict になりません。

さらに `SetBuffer` と `SetPlanningBuffer` は plan cache を invalidate しません。typed planning buffer も field の `Buffer` と `Revision` だけを更新します。

**具体的な結果:**

1. 二つの appearance の committed Estimate は8で一致している。
2. 一方だけ pending buffer が16になる。
3. `PlanFor` は8を canonical input として採用する。
4. unrelated planning edit により、8を基にした Start/Finish が両 appearance へ生成される。
5. `SourceProblem` がないため、その generated DATE は publication guard も通る。
6. Apply review は pending text を件数として表示しますが、独立して committed された payload 自体は除外しません。したがって、もう一方の appearance への stale projection は明示的Applyの対象になり得ます。

追加テストは Estimate/Remaining/Actual/Start/Finish の**異なる scalar value**を扱いますが、同じ committed value＋片側 pending buffer、片側 explicit clear、異なる B/L/R 状態を扱っていません。

**データ保持型の修正:**

canonical equivalence descriptor に、少なくとも次を含めます。

* effective committed value
* `Buffer` の有無と値
* `Change` の有無、値、`Clear`
* `Baseline`
* cell availability/reason
* field conflict
* observation availability/value/reason

一つでも異なれば、その canonical task を unresolved にし、generated projection、baseline capture、planning-field publication を止めます。代表 appearance を使えるのは equivalence 確認後だけとし、`ItemId` など安定 identity で決定してください。

buffer の各キー入力で全再計算する必要はありません。`null ↔ non-null` の遷移時だけ該当 Project の plan cache を invalidate すれば、文字ごとの再計算を避けながら pending-state difference を認識できます。

---

## 4. High — duplicate canonical Issue があるだけで Gantt の link 描画が例外になる

**場所:** `src/GhProjectsBoards.App/GanttView.cs`
**メソッド:** `DrawLinks`, `Relations`

`GanttProjection.Create` は Project item appearance ごとに `GanttRow` を作るため、同じ `TaskId` の行を複数保持します。これは両 appearance を消さないという点では正しいです。

一方、`DrawLinks` は選択行が存在すると直ちに次を実行します。

```csharp
var byTask = projection.Rows.ToDictionary(r => r.TaskId);
```

duplicate canonical Issue が一つでもあれば `ArgumentException` になります。link が一本もなくても、任意のGantt行を選択した時点で到達します。

`Relations` は例外にはなりませんが、`DistinctBy(TaskId)` の first-wins で relation target を決めるため、snapshot row order により別 appearance へ移動します。

**データ保持型の修正:**

* `TaskId` で grouping する。
* ambiguous group は link 描画と relation navigation を無効にし、duplicate reconciliation が必要であることを表示する。
* equivalence が確認できた group のみ、安定した representative row を描画用に選ぶ。
* `selectedEdges` も canonical edge key で重複除去する。
* `projection.Rows` 自体から appearance を削除しない。

UI integration では、identical duplicate と conflicting duplicate の両方について、行選択、縦スクロール、relation一覧、link描画を通す必要があります。

---

## 5. Medium — Summary は duplicate appearance と再帰属後の selection を別タスクへフォールバックする

**場所:**
`src/GhProjectsBoards.Core/Projects/SummaryProjection.cs` — `Create`
`src/GhProjectsBoards.App/SummaryView.cs` — `Present`, `FilterTasks`

Summary projection は Gantt rows を `DistinctBy(TaskId)` し、canonical task ごとに最初の `RowId` だけを contribution に残します。したがって Boards で同じIssueの二番目の Project item appearance を選んでSummaryへ入ると、その `RowId` を持つ contribution は存在しません。 

`Present` は explicit `selectedRow` が渡された場合だけ person を再解決し、候補がなければ person を null にします。その後 `FilterTasks` は要求された row が見つからない場合、無条件にその person の最初の task を選びます。これにより「Boardsで開く」「Ganttで開く」「工数を編集」が、入場元とは別の visible row を対象にします。

別の再現は再帰属です。

1. Summaryで task X / person A を選ぶ。
2. task X の contribution/actual attribution を person B に変更する。
3. `UpdateSummary` が explicit row なしで再表示する。
4. `Present` は古い person A を維持し、row に対応する person を再解決しない。
5. Aのtask一覧内でXが見つからず、最初の別taskを選ぶ。

**データ保持型の修正:**

* explicit parameter か保持済み selection かにかかわらず、requested row がある限り毎回 person を再解決する。
* canonical contribution に全 appearance RowId を保持するか、`RowId → canonical TaskId` map を projection に持たせる。
* requested row が解決できない場合は first-task fallback を行わず、selection を空にして navigation/edit commands を無効にする。
* 表示 selection だけを変更し、`Open`、field initialization、cell commit、checkpoint flush は呼ばない。

現在の追加テストは unique な二番目の task の初回入場と、そのままの往復を確認しています。duplicate appearance と再帰属は未確認です。

---

## 6. Medium — lazy editor の同期 focus が失敗すると logical selection と native focus が分離する

**場所:** `src/GhProjectsBoards.App/EditingGrid.cs`
**メソッド:** `Select`, `RestoreWorkspaceFocus`, `EnsureCell`

これは **source上の条件付き欠陥**です。WinUIで実際に何回発生するかはruntime evidenceが必要ですが、失敗時の処理は明確に不整合です。

`Select` は先に `currentRow/currentColumn/active` を新しいセルへ変更し、`ScrollIntoView` 後に `Focus` を呼びますが、戻り値を無視します。lazy path では、target editor が作成済みでも、まだ virtualized row の detached visual tree 内にあり `IsLoaded == false` である可能性があります。

`Focus` が false だと、次の状態になります。

* selection frame と詳細は新セルを示す
* native keyboard focus は旧editorに残る
* key event は旧editorの `(row, column)` handlerへ届く
* 次の矢印/Tab/F2が旧セル基準で処理される
* 水平スクロール時には、logical current でない旧focused control がcollapse対象になり得る

同じクラスの `RestoreWorkspaceFocus` は `Focus` がfalseになる可能性を認識していますが、そこで exact cell focus を遅延完了せず、command buttonへ移します。

**データ保持型の修正:**

* target `(generation, row, column, editor reference)` を pending-focus として保持する。
* target row/editor の `Loading` または `Loaded` で、同じgeneration・同じreferenceであることを確認してfocusする。
* focus成功まではlogical currentを旧セルに維持するか、pending selectionを別状態として扱う。
* focus失敗中に旧editorのkey handlerが次の操作を処理しないようにする。
* focused editorを破棄、置換、別rowへrebindしない。
* `RestoreWorkspaceFocus` も同じ exact-reference completion pathを使う。

境界テストは、viewport末尾からの `Down`、行末からの `Tab`、`Shift+Tab`、水平offscreen列への移動で、各キー後に `SelectionIdentity` と `FocusManager.GetFocusedElement` が同じセルを示すことを確認すべきです。

---

## 7. Low — duplicate planning conflict が無関係な NUMBER/DATE field のApplyまで止める

**場所:** `src/GhProjectsBoards.Core/Projects/PlanningWorkspace.cs`
**メソッド:** `PlanningPublicationProblem`

`SourceProblem` があると、現在は field が `NUMBER` または `DATE` であるだけでpublication problemを返します。fieldが planning mapping に含まれるかは確認していません。

そのため、たとえば mapped Estimate の duplicate conflict があると、同じrowの無関係な「Budget」「Review date」などの変更もApply reviewで拒否されます。これは安全側の停止ですが、correction scopeより広い操作阻害です。Apply review は全changed cellについてこのメソッドを呼びます。

**修正:** `SourceProblem` によるpublication blockを、`ProjectPlanning.Fields` に含まれる field ID、および Start/Finish/Actual のgenerated projection fieldに限定します。無関係なdraftは保持したまま通常のApply reviewへ進めます。

---

# 以前の8件と cold-projection concern の処分

| 以前の指摘                                                       | 今回の処分                                                                                                                                                                            |
| ----------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 1. 初回保存失敗後に再試行不能                                            | **未解決。** `.tmp → .rejected` renameが成功する通常経路だけ改善。rename失敗時のdeadlockと、成功時のrecovery inventory消失が残る。                                                                                 |
| 2. verified promotion が protected baseline / dependency と衝突 | **部分修正。** target task/baseline identity と LocalLinks重複は事前拒否される。しかし既存dependency `DraftField` keyとの衝突でintent lossが残る。                                                              |
| 3. duplicate canonical rows がfirst-wins・fan-out             | **部分修正。** 異なるeffective scalarは unresolved、generated writes skip、baseline拒否、publication blockになる。pending buffer/draft semanticsが比較されず、Ganttはduplicateで例外になる。                      |
| 4. held Summary が通常navigationから利用可能                         | **修正済み。** 通常selectorは「準備中」かつdisabledで、programmatic Summary要求もBoardsへcoerceされる。isolated evaluation routeは保持されている。                                                                 |
| 5. rollup detail がraw effortを0に置換                           | **修正済み。** contributionはraw値を保持し、Project/person totalsのみ `IncludedInTotals` で除外する。                                                                                                |
| 6. Boards/Ganttの非先頭selectionがSummary入場時に失われる                | **unique rowの初回入場は修正済み。** duplicate appearance と再帰属refreshには上記 finding 5 が残る。                                                                                                    |
| 7. wholly unknown effort に「0人時」を併記                          | **修正済み。** wholly unknownは「不明 人時」、mixedは「小計」と表示する。                                                                                                                                |
| 8. past cutoffでもActual headerが「本日時点」                        | **修正済み。** headerはprojection cutoffの実日付を表示する。                                                                                                                                     |
| historical cold projection field initialization             | **core pathでは修正済み。** `PlanFor` と `GanttProjection.Create` は `ReadRows` を使い、field/revision/history/checkpointを初期化しない。`calculatedPlans` cacheはtransient mutationだが、登録更新時にclearされる。 |

---

# Detached snapshot、Undo、v12/v10/v11 lineage

この範囲では追加のsource defectを確認できませんでした。

`DraftSnapshot.Copy` は、Fields/History、observation options、registrations、Issue native arrays、creation journalのnested arrays、row preferences、planning task arrays、assignment、calendar、Summary baselineまで別配列化しています。`LocalRow` 内のselectsは `ImmutableArray` なので、`LocalRowChange` のshallow record copyもmutable array aliasにはなりません。

`CommitPlanning` は restored staged workspace 上で projection acceptance とcommitを完了してからlive fields/planning/history/revisionを交換します。`DraftSession.CommitAsync` も detached candidateを作り、durable save成功後にだけ `Workspace` を交換します。失敗時にlive Undo/historyを部分変更する経路は、この範囲では見つかりませんでした。 

checkpoint lineageも整合しています。

* record versions 1–12を受け付ける
* plan v2はrecord v10/v12
* plan v3はrecord v11以降
* plan v4はrecord v12
* current planとhistoryのBefore/Afterの両方をvalidate
* v10/v12ではJSON上の明示的 `Summary` と `LaborKind` を確認
* v11/v12のassignment系は`PlanningContract.Validate`で検証
* 新規snapshotはv12として書かれる

 

ここでの残存問題はschema migrationではなく、finding 1のorphan classificationです。

---

# Lazy offscreen editor を残すために必要なruntime evidence

ソース上、次の安全策は確認できます。

* `EnsureCell` はplaceholderをそのrow/column専用editorへ一度だけ置換し、別rowへrebindしない。
* active、editing、composing rowはevictionから保護される。
* workspace bufferが存在するcellは、row realization時にeditorを即時構築する。
* stale native eventはexact referenceを使う `CurrentEditor` で拒否する。
* inactive rowだけが64行cacheを超えた後に解放される。

ただし、これらは次を証明しません。

* virtualized containerがdetachされた間もTSF/IME composition、candidate window、caret、selectionが保たれること
* `TextCompositionEnded` が失われず、`CanRefresh` が永久falseにならないこと
* keyboard traversal境界でfocusが必ず成功すること
* session refresh/save acknowledgementがoffscreen pending editorを置換しないこと
* reloadされたcached rowが現在のhorizontal visibility/frozen transformを即時取得すること

最低限、次のactual WinUI runtime matrixが必要です。

1. **Focus traversal:** viewport末尾の行・列から、Tab、Shift+Tab、上下左右、Enterを連続入力する。各キー後にlogical identity、focused AutomationId、exact editor instance IDを一致させる。
2. **Physical Japanese IME:** TitleとNUMBERでcompositionを開始し、縦・横wheelでcellをoffscreenにして戻す。途中でcomposition end、commit、buffer消失、別cellへのfocus移動がないことを確認する。
3. **Pending buffer:** F2で未確定文字を作り、64行cache境界を越えて往復する。focused rowでは同一editor instanceとcaretを保持し、focusを別cellへ移した後に再生成される場合でも同じrow/key/bufferを復元する。
4. **Refresh:** composition中、pending中、offscreen中にsave acknowledgementとregistration refreshを発生させる。deferred refresh後もbuffer、history、revision semantics、selectionが不変であることを確認する。
5. **Same-row identity:** horizontal reveal/collapse/revealでeditorが別fieldへrebindされていないことをinstance IDで確認する。

これらが揃うまでは、lazy editor correctionについてfocus、IME、human acceptanceを合格とは扱えません。

---

# 性能についてのソース上の判断

提示された lazy run の title p95 34.70ms、NUMBER p95 61.73ms、最大callback gap 259.81ms、22 gaps は、合格値でも因果的speedupの証明でもありません。初期realize/layoutと、各groupの二回目のlarge vertical wheelに現れる約125ms間隔は未説明です。

## Source-proven な無駄: vertical scrollでも全rowをhorizontal同期している

`ScrollChanged` はvertical-only callbackでも必ず `SyncHeader` を呼びます。`SyncHeader` は毎回、

* header widthを再設定
* header `ChangeView`
* vertical scrollbar propertiesを同期
* header frozen transformを更新
* **全 `rowLines` を走査**
* loaded rowごとに `FreezeIdentity` と `UpdateColumnVisibility`

を行います。1000 rowsであれば、表示row数にかかわらず各scroll callbackで1000個の`IsLoaded`判定が発生します。これはソース上確認できる不要処理ですが、観測された125ms停止の原因だとはまだ証明されていません。

**狭いデータ保持型の修正候補:**

* vertical scrollbar同期をhorizontal header同期から分離する。
* 前回の `HorizontalOffset`、`ViewportWidth` を保持し、変化したときだけheader/column visibilityを更新する。
* 新規またはcached rowの`Loading`で、そのrowだけ現在のfrozen transformとvisibilityを適用する。
* focused/editor identity、save path、cache sizeは変更しない。

`ResizeSheetColumns` も全1000 itemを走査してwidthを設定するため、初期layout intervalの候補です。呼出し回数とinclusive timeを分けて計測し、必要ならheader＋realized/nonempty rowsだけを更新し、後からrealizeするrowには`EnsureRow`でcurrent widthを適用する実験ができます。

## 約125ms停止に対する、境界を限定した実験

| 仮説                                                      | 実験                                                                                                                                                       |
| ------------------------------------------------------- | -------------------------------------------------------------------------------------------------------------------------------------------------------- |
| 64 dormant rowsを初めて超えた時のeditor解放が二回目wheelと一致する          | cache sizeは64のまま、各callbackの`dormantRows.Count`、eviction数、cleared editor数、realized editor数を記録する。64未満で往復する系列と、64を跨ぐ系列を同じ入力で比較する。                           |
| `EnsureRow` 1.39秒はnative editorだけでなく全cell scaffold生成による | `EnsureRow` を container/marker/frame/fill-handle作成、`CreateCellEditor`、`UpdateCell` に分け、各wheel callback内の件数と時間を記録する。                                      |
| vertical callbackのheader/full-row scanが停止を作る            | 上記split-sync branchと現状を、同一checkpoint・同一wheel trace・同一cacheで比較する。                                                                                         |
| finalizer activityがUI pauseを発生させる                       | 自然に発生したGC/finalizerのETW CPU stacks、GC pause、UI-thread runnable/blocked期間をcallback gapと相関させる。**17.08秒のfinalizer-thread inclusive wall timeだけでは因果とみなさない。** |
| observer自体が間隔を伸ばす                                       | full/light/offを同一入力で比較する。lightはvisual walkを止めるだけで、`CompositionTarget.Rendering` hookとrecords/spansは残るため、最終性能判断はdiagnostics off＋独立observerで行う。            |

full traceの2.54秒 `CaptureDiagnosticState` visual-tree workは明らかに侵襲的ですが、light observerでもpauseが残る以上、それだけを原因とはできません。また、17.08秒の `IObjectReference.Finalize` はinclusive wall timeであり、UI停止時間やpure CPUとして2.54秒・1.39秒へ加算できません。

すべての性能比較で、保存は有効のまま、同じ64行cache、同じcheckpoint clone、同じ入力系列を使うべきです。GC強制、focused editorの破棄/rebind、保存抑止、benchmarkに合わせたcache拡大、追加並行処理は、この原因分離には不要です。
