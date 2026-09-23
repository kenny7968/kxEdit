# CSVモード: Ctrl+矢印で行/列の端へ移動（設計書）

策定日: 2026-09-23

## 背景・目的

CSV モードの端ジャンプは現在 `Home`/`End`（行の左端/右端）と `PageUp`/`PageDown`
（列の上端/下端）に割り当てられている。これに **Ctrl+矢印** の割り当てを追加し、
表計算ソフトで広く使われている操作（Ctrl+矢印でデータ範囲の端へ）に近い動線を提供する。

あわせて、CSV モード中に `Ctrl+矢印` が `CsvCommands.ByKey` に無いため素通りして
`InputRouter` の単語移動・行移動へ流れていた状態（CSV モードは読み取り専用なので
キャレットだけが動き、SR が生の行を読む恐れがある）を塞ぐ。

## 決定事項

### キー割り当て（追加）

| キー | 呼び出すコマンド | 機能 |
|---|---|---|
| `Ctrl+Up` | `CsvController.MoveColumnTop()` | 現在列の上端セルへ |
| `Ctrl+Down` | `CsvController.MoveColumnBottom()` | 現在列の下端セルへ |
| `Ctrl+Left` | `CsvController.MoveRowStart()` | 現在行の左端セルへ |
| `Ctrl+Right` | `CsvController.MoveRowEnd()` | 現在行の右端セルへ |

### 既存キーは別名として併存（ブレストで合意）

`Home`/`End`/`PageUp`/`PageDown` は**変更しない**。学習済みの操作を壊さないため、
また `Home`/`End` の横取りをやめると CSV モード中にキャレットだけが動く挙動
（2026-09-04 の Home キー設計で明示的に横取りしていた経緯）に戻ってしまうため。

既存の `Ctrl+Home`/`Ctrl+End`（左上/右下）とも修飾キーの体系が揃う。

### 新規ロジックなし

`CsvController` の 4 メソッドは既存のものをそのまま再利用する。座標計算
（`CsvDocument` のグリッド移動ヘルパ）にも手を入れない。変更は
`CsvCommands.ByKey` の表エントリのみ（17 → 21 エントリ）。

## 実装

1. `src/kxEdit.App/CsvCommands.cs` — `ByKey` に上表の 4 エントリを追加。
   `Add` 形式の初期化子なのでキー重複は静的初期化で例外になる（重複の黙殺防止）。
2. `tests/kxEdit.App.Tests/CsvControllerTests.cs`
   - `ByKey_HasExactly17Entries` を 21 エントリに更新（内訳コメントも更新）。
   - `ByKey_MapsAllEntriesToExpectedCommands` の switch に 4 case を追加。
     `(2,2)` 起点で `Ctrl+Up`→(0,2) / `Ctrl+Down`→(4,2) / `Ctrl+Left`→(2,0) /
     `Ctrl+Right`→(2,4)。Theory は `ByKey.Keys` 列挙 + default throw なので
     エントリ追加漏れは機械的に落ちる。

CLAUDE.md §3 の簡略化基準（数十行・単一ファイル＋テスト）に乗せ、実装 1 タスク・
単一 commit・最終レビューは別エージェント 1 回に統合する。

## テスト戦略

- L1〜L3 の自動テストで足りる（新規ロジックが無く、検証対象はキー→コマンドの対応）。
- ミューテーション検証は**行わない**。キーバインドのイベントマッピングは
  CLAUDE.md §4-A の禁止領域。
- **L5（実機 SR 検証）は必要**と判断する。読み上げ経路自体は不変だが、CSV モードは
  SR ユーザーの主要動線であり、新しいキーが実際に `ProcessCmdKey` で横取りされ
  発声まで届くかは実機でしか確認できない（判定に迷ったら必要に倒す方針）。
  確認項目: CSV モードで `Ctrl+上/下/左/右` を押し、移動先セルの内容＋行列番号が
  読み上げられること。`Home`/`End`/`PageUp`/`PageDown` が従来どおり動くこと。

## 申し送り

- `説明書/kxEdit説明書.md` 8.1 のキー表への追記が必要。**ユーザー編集版が正**
  （CLAUDE.md §8）なので本ブランチでは書き換えず、差分案の提示のみ行う。案:

  ```
  | Ctrl+上 / Ctrl+下 | 現在の列の上端 / 下端のセルへ(PageUp / PageDown と同じ) |
  | Ctrl+左 / Ctrl+右 | 現在の行の左端 / 右端のセルへ(Home / End と同じ) |
  ```

- CSV モードのキー一覧を持つアプリ内ヘルプは未実装（`MainForm` のコメントに
  「キー一覧は将来のヘルプに記載する」とある）。実装時に本件の 4 キーも載せる。
