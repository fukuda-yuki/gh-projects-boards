# Claude Code ⇄ DeepSeek Harness ブリッジ（Windows）

Claude Code を司令塔のまま維持し、実装作業を DeepSeek Harness のエージェント
（OpenCode Go 経由の DeepSeek V4.1 Flash）へ委譲するローカル MCP サーバーです。
English version: [README.md](README.md)

| 役割 | 担当 | 責務 |
|---|---|---|
| 親 | Claude Code | 要求整理、設計、タスク分割、結果のレビュー、修正依頼、完了判断 |
| 子 | DSH エージェント実行（`headless` プロファイル） | コード調査、編集、テスト実行、失敗原因の調査と修正 |
| 連携 | この MCP サーバー | 依頼を 1 件ずつ子へ渡し、状態・報告・終了理由・セッション情報を返す |

委譲は一方向です。Claude Code がブリッジを呼び、ブリッジが DSH のエージェント
実行を起動します。DSH から Claude Code を呼び戻すことはなく、子は DSH 自身の
ツールとサンドボックスを持つ DSH エージェントです（Claude Code を子エージェント
として使う構成ではありません）。

## この環境で確認した事実（2026-10-11 実測）

公開 master の機能は前提にせず、すべてインストール済みアプリから読み取りました。

| 項目 | 値 |
|---|---|
| インストール先 | `C:\Users\mwam0\AppData\Local\Programs\DeepSeek Harness` |
| バージョン | アプリ `44.0.0`（インストール直下の `version`）、CLI `dsh --version` → **`0.2.0-rc.2`** |
| ランチャー | `resources\runtime\cli\bin\dsh.cmd`（`ELECTRON_RUN_AS_NODE=1 --expose-internals` で asar 内の CLI を起動） |
| CLI 実体 | `resources\app.asar\dsh\node_modules\@deepseek-ai\dsh-desktop-host\lib\cli.js`（asar 内） |
| DSH home | `%DSH_HOME%` = `C:\Users\mwam0\.dsh`。`desktop` プロファイルは Electron アプリ専用で、CLI からの起動は拒否される |
| CLI 起動可能なプロファイル | `headless` / `web` / `acp` / `sdk` / `sdk-minimal`（初回使用時に同梱テンプレートから自動生成） |
| モデル接続 | pi-ai プロバイダー `opencode-go`（OpenCode Go）、モデル `deepseek-v4.1-flash`、api `openai-completions`、`https://opencode.ai/zen/go/v1`、コンテキスト 1,000,000 / 最大出力 384,000 |
| 認証情報 | `OPENCODE_API_KEY`（pi-ai の env 認証）。**ユーザー環境変数**として存在するため、通常起動したプロセスは継承する |
| セッション記録 | `%DSH_HOME%\sessions\<workspace-slug>\<session-id>\session.v4.jsonl.zstd`（マルチフレーム zstd JSONL） |
| Node.js | `C:\Program Files\nodejs\node.exe` v24.15.0（zstd 利用のため 22.15 以上が必要） |

調査したが**採用しなかった**接続経路: `dsh-sdk-protocol` / `dsh-sdk-jsonrpc-server`
/ `dsh-sdk-app`（SDK）、`dsh-acp` / `dsh-acp-app`（ACP）、`dsh-mcp-client` /
`dsh-mcp-resources`（MCP クライアント方向）、`dsh-host-webserver` + `dsh-api-*`
（GUI の HTTP API）、`dsh-hooks-claude-code`。

## なぜ「独立した MCP サーバー」なのか

| 選択肢 | この環境に存在するもの | 判断 |
|---|---|---|
| **独立 MCP サーバー（採用）** — 文書化された `dsh --profile headless` を駆動 | `headless` は同梱テンプレート。`--json`（NDJSON イベント）、`--session-id`（セッション継承）、終了コード規約が同梱パッケージに記載 | DSH のランチャー・エージェントループ・サンドボックスツール・セッション永続化・セッション継承をそのまま再利用。1 実行 = 1 プロセスなのでライフサイクルを検証できる。依存パッケージ・ビルド工程なし |
| DSH 内部の MCP プラグイン | MCP **サーバー**プラグインは同梱されていない（`dsh-mcp-client` / `dsh-mcp-resources` は逆方向）。Cordis プラグインを書いてビルドし、pnpm でプロファイルへ導入する必要がある | `0.2.0-rc.2` の内部構造に密結合し、DSH 更新のたびに追従が必要。不採用 |
| DSH SDK JSON-RPC サーバー（プロファイル `sdk`） | `initialize` / `session/prompt` / `shutdown` と `session.event` 等の通知。ただし **cancel メソッドが無く**「クライアントはランタイムを閉じてターンを放棄する」と明記 | MCP への変換層は結局必要で、要求 4（キャンセルと停止確認）が共有ランタイムの kill に劣化する。不採用 |
| ACP（`dsh-acp`、プロファイル `acp`） | DSH が ACP *エージェント* になる（クライアントは Zed 等のエディター） | Claude Code は ACP クライアントではない。方向が違う。不採用 |
| GUI の HTTP API（`127.0.0.1:19387`） | 起動中の GUI セッションがセッション制御 API を公開 | GUI 常駐とブラウザーセッション秘密に依存。`desktop` は CLI 起動不可。不採用 |
| DSH 内の Claude Code フック（`dsh-hooks-claude-code`） | DSH が Claude Code の `hooks.json` を**取り込む**機能 | 依頼された方向の逆。無関係。不採用 |

結果として、ブリッジが依存するのは「ランチャー」＋「`--profile headless --json
--session-id` とセッション保管庫のレイアウト」だけです。この契約が維持される限り、
アプリの更新でブリッジを書き換える必要はありません。

## 要求 5 項目との対応

| 要求 | 実装 |
|---|---|
| 1. 作業フォルダーとタスク仕様を指定して実装エージェントを起動 | `dsh_delegate`（`task` と任意の `workspace`）。ワークスペースを子プロセスの cwd にして `dsh --profile headless --json` を起動し、タスクは **stdin** で渡す（コマンドライン長・引用符の問題を回避） |
| 2. 実行状態・最終報告・終了理由・セッション情報の取得 | `dsh_status`（状態、経過時間、pid 生存、終了コード、イベント件数、記録パス）と `dsh_result`（報告＋プロセス結果＋セッション ID とログ位置） |
| 3. レビュー指摘の再投入と、継続／新規の明示 | `dsh_rework`。`mode: "continue"` は `--session-id` を採用して会話を継続、`mode: "new_run"` は新規セッション。応答は必ず `same_session` / `session_mode` と「どちらになったか」を文章で返す |
| 4. キャンセルと実際の停止確認 | `dsh_cancel`。プロセスツリーを `taskkill /T /F` → 実行自身の exit イベントを待機 → pid 生存確認（`process.kill(pid, 0)`）と `tasklist /FO CSV /NH` の再確認。すべて一致したときだけ `verified_stopped: true` |
| 5. 実際に使った provider / model / profile と記録の場所 | `dsh_result` の `model_route` は DSH 自身のセッションログ（`request/header` の `config.provider/model/reasoningEffort`、`request/context` の `contextWindow`）を読み戻して返す。プロファイル名・patch パス・全記録パスも併せて返す |

完了判断は意図的に分離しています。モデルの報告は「主張」、正常終了は「正しさの
証明」ではないためです。

* `model_report.text` — 子の最後の非空アシスタントメッセージ（`final` イベント）
* `process_outcome` — 終了コード、シグナル、`turn/end` の理由（`completed` など）
* `judgement.process_finished_normally` — 終了コード 0 **かつ** `turn/end` が `completed`
* `judgement.model_report_present` — 子が何か報告したかどうか

ワークスペースの検証（差分・テスト）は親である Claude Code が行う前提です。

## Windows でのセットアップ

### 前提

* DeepSeek Harness がインストール済み（既定の場所で可）
* Node.js 22.15 以上が PATH にある（zstd 圧縮されたセッションログを読むため）
* `OPENCODE_API_KEY` がユーザー環境変数として設定済み
* Claude Code（`claude` CLI）が登録に必要

### 一括セットアップ

```powershell
cd <repo>\tools\claude-dsh-bridge
pwsh -File install.ps1 -Register
```

スクリプトの動作:

1. Node.js を特定し、zstd 対応を検証
2. ブリッジ自身のリゾルバーで DSH インストールを特定（検出元と、CLI 実体が実ファイルか
   `app.asar` 内かを表示）
3. `dsh headless --help` を 1 回実行して DSH に `%DSH_HOME%\profiles\headless` を
   同梱テンプレートから作成させ、`profile\cordis.patch.yml` を書き込む
   （独自の patch が既にある場合は `-Force` を付けない限り保持。`-Force` 時はバックアップを作成）
4. `--dump-config` で合成結果を検証し、`opencode-go` / `deepseek-v4.1-flash` が
   無ければエラーで停止
5. `OPENCODE_API_KEY` の有無を報告（**値は読み取りも表示もしない**）
6. `-Register` 指定時に Claude Code へ MCP サーバーを登録

スクリプトを使わない場合の手動手順:

```powershell
$dsh = "$env:LOCALAPPDATA\Programs\DeepSeek Harness\resources\runtime\cli\bin\dsh.cmd"
& $dsh headless --help                     # %DSH_HOME%\profiles\headless を作成
Copy-Item .\profile\cordis.patch.yml "$env:DSH_HOME\profiles\headless\cordis.patch.yml"
claude mcp add --scope user dsh-flash -- "C:\Program Files\nodejs\node.exe" "<repo>\tools\claude-dsh-bridge\server.js"
```

### PATH の扱い

* ブリッジは **`dsh` が PATH にある必要はありません**。インストール先を絶対パスで
  解決します（`DSH_BRIDGE_ROOT` / `DSH_BRIDGE_LAUNCHER` → PATH → 既定のインストール先の順）。
  ランチャーの存在は起動前に検証します。
* 登録には **Node.js の絶対パス**を保存するため、Claude Code 側にも `node` の
  PATH は不要です。手動で `claude mcp add ... -- node server.js` と登録した場合のみ、
  Claude Code 起動時に PATH 参照が発生します。
* DSH 自身の PATH 管理（`HKCU\Environment\Path` と
  `HKCU\Software\DeepSeekHarness\Command` による所有権管理）には一切触れていません。

### 設定（環境変数）

| 変数 | 既定値 | 意味 |
|---|---|---|
| `DSH_BRIDGE_HOME` | `%LOCALAPPDATA%\claude-dsh-bridge` | 実行記録の保存先（`runs\<run_id>\`） |
| `DSH_HOME` | `%USERPROFILE%\.dsh` | DSH home（プロファイルとセッション保管庫） |
| `DSH_BRIDGE_PROFILE` | `headless` | 委譲先の DSH プロファイル |
| `DSH_BRIDGE_PERMISSION_MODE` | `workspace-write` | 実行時の既定サンドボックスモード |
| `DSH_BRIDGE_MAX_RUNS` | `1` | 同時に走らせる子の数（合意どおり 1） |
| `DSH_BRIDGE_DEFAULT_TIMEOUT_SECONDS` | `0` | この秒数でキャンセル。0 は無制限 |
| `DSH_BRIDGE_ROOT` | – | 自動検出できない場合のインストール先 |

登録時に渡す場合は `claude mcp add -e KEY=value`、または Claude Code を起動する
環境変数として設定します。

### Claude Code への登録と承認

```powershell
claude mcp add --scope user dsh-flash -- "<node の絶対パス>" "<repo>\tools\claude-dsh-bridge\server.js"
claude mcp list          # dsh-flash が Connected と表示される
claude mcp get dsh-flash
```

ツールは `mcp__dsh-flash__dsh_delegate`、`..._dsh_status`、`..._dsh_result`、
`..._dsh_rework`、`..._dsh_cancel`、`..._dsh_info` として見えます。初回利用時は
Claude Code が承認を求めます。登録後は Claude Code を再起動してください。

## ツール一覧

### `dsh_delegate`
`task`（必須）、`workspace`、`session_id`、`permission_mode`、`timeout_seconds`、`wait_seconds`。
実行を開始し、`run_id`、`session_mode`（`new` / `continued`）、セッション ID（判明後）、
プロファイル、権限モード、記録パスを返します。

### `dsh_status`
`run_id`（必須）、`wait_seconds`。`status`（`running` / `completed` / `failed` /
`cancelled` / `timed_out`）、経過時間、pid 生存、終了コード、`turn_end_reason`、
イベント件数、stderr 末尾、キャンセル証跡、セッションログのパスを返します。

### `dsh_result`
`run_id`（必須）、`recent_events`。分離された判定、`model_report`、`process_outcome`、
`session`、`profile`、`model_route`（実際に使われた provider / model）、全記録パスを返します。
実行中は最終報告の代わりに途中経過のテキストを返します。

### `dsh_rework`
`feedback`（必須）、`mode`（既定 `continue` / `new_run`）、`run_id`、`session_id`、
`workspace`、`task`、`permission_mode`、`timeout_seconds`、`wait_seconds`。
`continue` は前回のセッションを採用します。DSH は「記録された作業ディレクトリ」でのみ
セッションを採用するため、`workspace` が一致しない場合は `new_run` を使うよう促す
エラーになります。応答には `same_session` と、`continue` の場合は採用したセッション ID
が入ります。

### `dsh_cancel`
`run_id`（必須）、`grace_seconds`。`cancelled`、`status`、`exit_code` と
`verification[]`（taskkill の結果、exit イベントの観測、pid 生存確認、
tasklist CSV の判定）を返します。

### `dsh_info`
`run_id`（任意）。DSH のバージョン、インストールパスと検出方法、プロファイルの
名前・ディレクトリ・patch と設定済み provider / model / reasoning effort、認証情報の
有無、実行一覧、セッション保管庫の位置、ある実行のセッションログから読み取った実使用ルート
を返します。

## 実行記録の場所

| 内容 | 場所 |
|---|---|
| ブリッジの実行記録 | `%DSH_BRIDGE_HOME%\runs\<run_id>\meta.json` |
| 実行の生イベント（`--json` 出力） | `%DSH_BRIDGE_HOME%\runs\<run_id>\events.ndjson` |
| 子の stderr（reasoning、診断） | `%DSH_BRIDGE_HOME%\runs\<run_id>\stderr.log` |
| DSH 自身の永続セッションログ | `%DSH_HOME%\sessions\<workspace-slug>\<session-id>\session.v4.jsonl.zstd` |
| セッションの投影キャッシュ | `%DSH_HOME%\storages\session_projcache\sessions\<session-id>.json` |

`dsh_status` と `dsh_result` はこれらのパスを返すので、親はそのまま引用できます。

## 実施した検証

実行コマンド（`tools\claude-dsh-bridge` で実行）:

```powershell
node test/unit.js   # プロトコル・ランチャー・セッションログ・キャンセル判定（モデル呼び出しなし）
node test/e2e.js    # 実 MCP クライアント → 実 DSH 実行 → 実ファイル変更
```

| 検証 | 結果 |
|---|---|
| `node test/unit.js` | **10/10 PASS**（MCP の initialize／通知／エラー、起動コマンド生成、asar 内 CLI 実体の検出、マルチフレーム zstd の解析、tasklist CSV 判定、設定と patch の読み取り） |
| `node test/e2e.js` | **33/33 PASS（88 秒）**（初回の完全実行は 60 秒）。Claude Code と同じ手順で MCP サーバーを駆動 |
| `claude mcp get dsh-flash` | **Status: √ Connected** — Claude Code 自身がサーバーを起動し MCP ハンドシェイクに成功 |
| ブリッジ作成前のスモーク実行 | `dsh --profile headless --json "implement multiply …"` → 26 秒で exit 0、`demo/calc.js` が実際に変更され `node demo/check.js` が green |

エンドツーエンドで確認できたこと（証跡 JSON は `test/artifacts/e2e-*.json`）:

1. **委譲 → 実ファイル変更 → 検証** — `dsh_delegate` がスクラッチ上の `demo/calc.js` に
   `multiply` を実装。ファイル内容が実際に変化し、**親自身が実行した**
   `node demo/check.js` が `PASSED 3/3`。
2. **結果返却** — `dsh_result` が子の最終報告、`process_finished_normally: true`
   （exit 0 かつ `turn/end` = `completed`）、空でない `model_report`、セッション ID、
   そしてセッションログから読み戻した `model_route` = `opencode-go` /
   `deepseek-v4.1-flash` とログのパスを返却。
3. **レビュー指摘による再修正（同一セッション）** — `dsh_rework`（`mode: "continue"`）が
   `same_session: true` と同じ `session-…` を採用。子が `TypeError` ガードと追加ケースを
   実装し、親の `multiply('x', 2)` 検査が `TypeError` を投げ、`node demo/check.js` が
   `PASSED 6/6`。
4. **子は常に 1 つ** — 実行中に 2 件目の `dsh_delegate` を投げると
   `a delegated run is already active (…)` で拒否。
5. **キャンセルと停止確認** — 意図的に長時間の実行をキャンセル。`taskkill /T /F` が exit 0、
   実行自身の exit イベントが発火、`process.kill(pid, 0)` が pid 消滅を報告し、
   `tasklist /FO CSV /NH` にも現れず → `verified_stopped: true`。状態は `cancelled` のまま
   維持され、`dsh_result` は `process_finished_normally: false` を返却。

## 未確認事項と既知の制限

* **Claude Code の対話中でのツール呼び出しは未実施**です。確認済みなのは、実 stdio での
  MCP ハンドシェイクと 6 ツール（`test/e2e.js`）、および Claude Code 自身のヘルスチェックが
  登録済みサーバーに `Connected` になること（`claude mcp get dsh-flash`）。残るのは
  Claude Code 内での実際のプロンプト 1 回です。
* `.claude/settings.json` での `mcp__dsh-flash__*` の**許可リスト**は Claude Code 側の
  仕様であり、この環境では未検証です（確認したのは対話的な承認経路）。
* **子は同時に 1 つ**（`DSH_BRIDGE_MAX_RUNS=1`）。増やすことは可能ですが未検証です。
* **headless では承認が fail closed**。対話的な回答者がいないため、ワークスペース外への
  書き込みなど `workspace-write` を超える操作は許可されません。タスク設計で考慮するか、
  `permission_mode` を明示してください。
* **キャンセルは Windows 専用**の経路です。他 OS では直接の子への `SIGKILL` に
  フォールバックし、孫プロセスの回収は行いません。
* **セッションログの読み取り**には Node.js 22.15 以上の zstd が必要です。無い場合、
  ルート情報は `available: false` になりますが他の機能は動作します。
* **実行記録は自動削除されません**（`%DSH_BRIDGE_HOME%\runs` は増え続けます）。
* ブリッジは差分の再構成やリポジトリのテスト実行は行いません。「検証済み」の定義は
  親である Claude Code が決めます。

## Claude Code からの最初の委譲手順

1. `dsh_info` — 実行前に DSH バージョン、設定済みルート、`OPENCODE_API_KEY` の有無を確認
2. `dsh_delegate` — タスクと作業フォルダーを指定（`wait_seconds: 240` を付けるとポーリングを 1 回省略）
3. `dsh_status` — `running` でなくなるまで待ち、`exit_code` と `turn_end_reason` を確認（子の文言だけで判断しない）
4. `dsh_result` — 報告と `model_route` を取得し、実際の差分とプロジェクトのテストを自分で確認
5. `dsh_rework` — 具体的な指摘を `mode: "continue"` で返して文脈を維持。方針が崩れた場合は `mode: "new_run"`。どちらになったかは応答に明示される
6. `dsh_cancel` — 逸脱した実行を停止。`verified_stopped` が実際に止まったことの証拠

## ファイル構成

| パス | 役割 |
|---|---|
| `server.js` | MCP の入口。設定、ツール定義、ハンドラー |
| `lib/mcp.js` | stdio JSON-RPC の MCP サーバー（initialize / tools/list / tools/call / ping / 通知） |
| `lib/launcher.js` | DSH インストールの特定（`app.asar` 内の CLI 実体を含む）と起動コマンド生成 |
| `lib/runner.js` | 実行ライフサイクル。起動、イベント記録、状態・結果ビュー、停止確認付きキャンセル |
| `lib/session-log.js` | マルチフレーム zstd のセッションログから実使用ルートを読み取る |
| `profile/cordis.patch.yml` | `%DSH_HOME%\profiles\headless` に導入するプロファイル patch |
| `install.ps1` | プロファイル準備、検証、Claude Code への登録 |
| `demo/calc.js`, `demo/check.js` | E2E がスクラッチへ複製する参照用フィクスチャ |
| `test/unit.js`, `test/e2e.js`, `test/mcp-client.js` | ロジック検証、実委譲サイクル、両者が使う MCP クライアント |
