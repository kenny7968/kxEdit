# バックアップ復元時に初期無題タブを閉じる 設計書

- **作成日**: 2026-07-24
- **対象**: `src/yEdit.App`(BackupCoordinator・MainForm)
- **区分**: バグ修正(既定 OFF 経路の復元後タブ整理を ON 経路と揃える)
- **前提**: PR #24(hot exit 統合)マージ後の main。ON 経路(`RestoreOpenFilesOnStartup=true`)には既に `_startupEmptyDoc` を閉じるロジックが存在するが、OFF 経路(バックアップ復元のみ)には無い

## 0. スコープと決定事項サマリ

**採用方針**: 案 A(API 対称化)派生 ―― `BackupCoordinator.OfferRestoreOnStartup` の返り値を全 4 分岐(確認 ON/OFF × Restore/その他)で実復元件数に統一し、`MainForm.OnShown` OFF 経路で「`restored > 0` なら初期無題タブ `_startupEmptyDoc` を TryClose」する。ON 経路(`FileController.RestoreSession`)の `openedCount > 0 && initialEmpty is not null` パターンと対称にする。

**Announcer は挙動不変**: 「バックアップを N 件復元しました」は従来どおり **確認 OFF(silent 自動復元)のときのみ**発話する。返り値対称化に引きずられて確認 ON+Restore で新規発話しないよう `!_settings.ConfirmRestoreOnStartup` でゲートする(ユーザーがダイアログで既知の情報を再アナウンスしない)。

**変更後の姿(ユーザー視点)**:
- 現状: OFF 設定+バックアップ有効+異常終了 → 起動時に「起動時無題1 + 復元無題1 + 復元無題2 + 復元既存」の 4 タブが並び、無題1 が同名 2 個で保存確認ダイアログの識別性が失われる。
- 修正後: バックアップから 1 件以上復元された場合、起動時の空無題1 タブは自動的に閉じ、**復元されたタブだけが残る**。

**非対象(scope out)**:
- 復元件数 0 のとき(確認ダイアログで Later / DiscardAll / チェック全解除)は初期無題タブを残す(現行不変)。
- ON 経路(`RestoreUnifiedSession`)の挙動(既に対応済み・不変)。
- 確認 ON+Restore の Announcer 発話有無(現行の「無音」を維持=`!confirm` ゲートで明示)。

## 1. アーキテクチャ

変更範囲は 2 ファイル・数行:

- `BackupCoordinator.cs`:確認 ON+Restore 分岐(`OfferRestoreOnStartup` :233-254)にカウンタを入れ、末尾 `return 0` を `return restored` へ。他 3 分岐は既に実件数を返している(:227 の確認 OFF+成功、:257 の DiscardAll、:261 の Later はいずれも 0 で仕様どおり)。
- `MainForm.cs`:OFF 経路(`OnShown` :243-249)に `_startupEmptyDoc` の TryClose を追加。Announcer 呼び出しは `!_settings.ConfirmRestoreOnStartup` でゲート化。

### 変更前後の差分(擬似コード)

```csharp
// BackupCoordinator.OfferRestoreOnStartup :230-254 修正後
var outcome = _restorePrompt.Prompt(owner, ordered);
switch (outcome.Action)
{
    case RestoreAction.Restore:
        int restored = 0;
        foreach (var rec in outcome.Checked)
        {
            try { var doc = restore(rec); AdoptRestored(doc, rec); restored++; }
            catch (Exception ex) { _trace.Warn("restore-item", ..., ex); }
        }
        return restored;   // 従来 return 0(:263)を分岐内へ移動+実件数化

    case RestoreAction.DiscardAll:
        _writer?.DeleteAll();
        return 0;

    case RestoreAction.Later:
    default:
        return 0;
}
// 末尾の return 0(:263)は削除
```

```csharp
// MainForm.OnShown OFF 経路(:243-249)修正後
int restored = _backup.OfferRestoreOnStartup(
    this, _file.RestoreFromBackup, _settings.ConfirmRestoreOnStartup);
if (restored > 0)
{
    if (_startupEmptyDoc is not null)
        _docs.TryClose(_startupEmptyDoc, _ => true);   // ON 経路と同じ「空無題は無条件破棄」
    if (!_settings.ConfirmRestoreOnStartup)
        _announcer.Say($"バックアップを {restored} 件復元しました");
}
```

### なぜ ON 経路と実装を揃えるか

ON 経路の `FileController.RestoreSession` は `openedCount > 0 && initialEmpty is not null` で `_docs.TryClose(initialEmpty, _ => true)`(FileController.cs:660-661)を行う。同じ「復元件数 > 0 なら空無題は捨てる」不変条件を OFF 経路にも移植する。層越境(BackupCoordinator が DocumentManager を直接触る)は避け、呼び出し側(MainForm)で対称化する。

## 2. データフロー

```
[起動時 OFF 経路(RestoreOpenFilesOnStartup=false)]
  ctor: _file.NewFile() → 「無題1」作成 → _startupEmptyDoc に保存
  OnShown: _backup.OfferRestoreOnStartup(...) 実行
    ├─ 確認 OFF+Restore 成功 → restored=実件数(現状既に対称)
    ├─ 確認 ON+Restore 成功 → restored=実件数(★本修正で 0→実件数)
    ├─ Later / DiscardAll / 未選択 Restore → 0(現状不変)
    └─ 全経路 return を経て MainForm.OnShown へ
  if (restored > 0):
    _startupEmptyDoc?.TryClose(_ => true)     ← ★新規
    if (!confirm) _announcer.Say(...)         ← 現行の Say を confirm ゲート下に移動
```

`_docs.TryClose(_, _ => true)` の第 2 引数(破棄確認 predicate)を無条件 true にするのは ON 経路(FileController.cs:661)と同じ。空無題は Modified=false のため、predicate 自体が呼ばれない見込みだが、契約として無条件破棄を明示する。

## 3. エラー処理

- `_startupEmptyDoc` が既に閉じられている(タブ操作でユーザーが手動で閉じた等)場合: `_docs.TryClose` は冪等(`DocumentManager.TryClose` の契約=存在しない doc は no-op)。null チェック済みなら安全。
- ON 経路と同型のため既存の失敗パターン(TryClose が false を返す=doc は残る)もそのまま踏襲する(ユーザーが編集を書き込んで dirty 化していたら閉じない挙動は残す=データ保護優先。ただし本フローでは復元処理中にユーザー入力は入らないため実質発生しない)。
- 復元自体の例外は現行の per-record catch(BackupCoordinator.cs:236-251 / 218-225)がそのまま担当。本修正は復元件数を数える契約を追加するだけで例外経路は不変。

## 4. UI / 設定 / 説明書

- 設定項目の追加・変更なし。
- 「起動時に復元するか確認する」設定の意味は不変(確認 ON=ダイアログ表示・確認 OFF=silent 自動復元)。
- 説明書: 現状の記述は復元後のタブ数に触れていないため更新不要。もし将来「バックアップから復元」節に「復元時に起動時の空タブは自動で閉じます」の一文を足す価値があれば、この修正リリース時にユーザー校閲前提のたたき台を提示する(申し送り)。

## 5. テスト計画(CLAUDE.md §5)

### L3(App.Tests)

以下の新規テストを追加する。既存の `OfferRestoreOnStartup_*` テストは `restored` を assert していない(戻り値の破棄利用のみ)ため、返り値意味論の変更で回帰は起きない見込み。

**BackupCoordinatorTests(既存ファイル)**:
1. `OfferRestoreOnStartup_ConfirmTrue_Restore_ReturnsCountOfRestored`:
   確認 ON+Restore で 2 件チェック時 → 復元 lambda 2 回呼び出し+戻り値 2。
2. `OfferRestoreOnStartup_ConfirmTrue_Restore_OneThrows_ReturnsSuccessCountOnly`:
   2 件中 1 件が例外を投げる → 戻り値 1(既存の per-record catch との整合)。
3. `OfferRestoreOnStartup_ConfirmTrue_RestoreEmpty_ReturnsZero`:
   Restore アクションだが outcome.Checked が空 → 戻り値 0。

**MainFormSmokeTests(既存ファイル・追加できる範囲で)**:
4. **初期無題タブが復元後に閉じる(バグ再現テスト=ミューテーション対象)**:
   バックアップ 1 件+確認 OFF+起動 → `_docs.Documents.Count()==1` かつ復元 doc が生きている。
   ミューテーション検証: 修正コードの `_startupEmptyDoc?.TryClose` を除去 → 本テスト赤化を確認。
5. **復元 0 件のとき初期無題タブは残る**:
   バックアップ 0 件 → `_docs.Documents.Count()==1` かつそれが `_startupEmptyDoc`。
6. **Announcer は confirm=false のみ発話(挙動不変の pin)**:
   確認 ON+Restore 成功 2 件 → Announcer 呼び出しなし。
   確認 OFF+成功 1 件 → Announcer 呼び出しあり(件数=1)。

MainForm 経路のテスト難度により (4)-(6) が丸ごと現行基盤で書けない場合は、`FileController` 相当の seam を経由するか、MainForm を隔離した薄い経路テストを追加する。既存の tests/README.md「MainForm 隔離規律」の範囲内に収める。

### L1 / L2

- L1(Core.Tests):対象外(Core への変更なし)。
- L2(Editor.Tests):対象外。

### L4 / L5

- L4:対象外(1 起動あたり関数呼び出し数変化なし)。
- L5:SR 経路変更なし(UIA プロバイダ・Speech 系に触れない。Announcer は既存の `_announcer.Say` を条件下で呼ぶだけで新規経路ではない)。CLAUDE.md §5 基準では省略可。判定=省略。

### ミューテーション検証(最終品質パスのスポットチェック)

以下を高価値テストとしてミューテーション検証対象にする:
- `OfferRestoreOnStartup` :234 の `restored++` 除去変異 → テスト (1) が赤化。
- `MainForm.OnShown` の `_startupEmptyDoc?.TryClose` 除去変異 → テスト (4) が赤化。
- Announcer の `!confirm` ゲート反転変異 → テスト (6) の確認 ON 部が赤化。

## 6. セキュリティ考慮

- 該当なし。外部入力のパース・パス操作・プロセス起動・WebView / プレビュー・ネットワークのいずれにも触れない。
- CLAUDE.md §3 の前倒し脆弱性レビュー判定=不要。

## 7. 実装計画

- **1 タスク・単一 commit**(CLAUDE.md §3 簡略化基準=数十行規模・実質 2 ファイルの小変更):
  - BackupCoordinator.cs `OfferRestoreOnStartup` の Restore 分岐にカウンタ追加+末尾 return を分岐内へ移動
  - MainForm.cs OnShown OFF 経路に TryClose 追加+Announcer の confirm ゲート化
  - L3 テスト 6 件追加(BackupCoordinatorTests 3 件+MainFormSmoke 相当 3 件)
- **仕様レビュー**: 実装完了後、実装+テストが本設計どおりか確認(1 パス)。
- **最終ブランチレビュー**: 変更が小規模のため 2 パス統合(コード品質+脆弱性)を別エージェントで 1 回実施。ミューテーション検証は上記 3 点をスポットチェック。
- **品質ゲート**: `tools/pre-merge-check.ps1` EXIT 0 確認。
- **PR**: 本設計書リンク+バグ再現手順+修正内容+レビュー経緯を description に記載。

## 8. 申し送り(follow-up 候補)

- 説明書「バックアップからの復元」節に「復元時に起動時の空タブは自動的に閉じます」の一文追加(ユーザー校閲前提のたたき台をリリース時に提示)。
- ON 経路(`RestoreUnifiedSession`)の Announcer 発話有無(現状 silent)は本修正の対象外。将来「復元件数 N を非侵襲に読み上げる」要望があれば別 Issue で。
- `OfferRestoreOnStartup` の返り値意味論変更に伴い、既存テストで `restored` の値を assert している箇所が新たに発生した際は本設計書に紐付けて追跡(現状該当なし)。
