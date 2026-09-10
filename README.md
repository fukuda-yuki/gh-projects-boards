# gh-projects-boards

GitHub Projects を表形式で扱い、新規 Issue と既存 Issue を同じ画面で一括編集する C# 製 Windows デスクトップアプリ。

現時点では **WPF + .NET 10 の空ウィンドウを起動する骨組みのみ**です。表編集、GitHub 接続、下書き保存は未実装です。

## 開発

Issue 駆動で進めます。要件と受け入れ条件の正本は [Epic #1](https://github.com/fukuda-yuki/gh-projects-boards/issues/1) と関連 Issue です。WPF + .NET 10 の選択と未決事項は [技術判断](docs/decisions.md) を参照してください。

### ビルド・起動

Windows と .NET 10 SDK が必要です。リポジトリのルートで実行します。

```powershell
dotnet build GhProjectsBoards.sln --configuration Release
.\src\GhProjectsBoards.App\bin\Release\net10.0-windows\GhProjectsBoards.App.exe
```

`GitHub Projects Boards` というタイトルの空ウィンドウが開き、閉じると終了します。この骨組みの起動に gh の導入やログインは不要です。配布形式・同梱ランタイム等は [Issue #13](https://github.com/fukuda-yuki/gh-projects-boards/issues/13) で別途決定します。

## 構成

- `GhProjectsBoards.sln`：アプリ1プロジェクトを含むソリューション
- `src/GhProjectsBoards.App/`：WPF の起動処理と空ウィンドウ
- `docs/`：要件・仕様・構成・技術判断の骨組み
- `tests/`：試験の入口。テストプロジェクトは機能実装時に追加

## 文書

| 文書 | 内容 |
| --- | --- |
| [要件](docs/requirements.md) | 製品の目的、対象範囲、関連 Issue |
| [仕様](docs/spec.md) | 合意済みの動作原則と未定義の契約 |
| [構成](docs/architecture.md) | 現在の構成と今後分離する責務 |
| [技術判断](docs/decisions.md) | 決定事項・理由・未決事項 |
| [試験](tests/README.md) | 検証方針と受け入れ条件の参照先 |
| [AGENTS.md](AGENTS.md) | このリポジトリでの作業ルール |
