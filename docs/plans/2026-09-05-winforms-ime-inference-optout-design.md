# WinForms の IME モード推測を無効化する 設計書

- 日付: 2026-09-05
- 対象 Issue: なし(Issue 起票はせず docs/plans に設計書と実装計画を残す運用。ユーザー承認 2026-09-05)
- ブランチ: `feature/winforms-ime-optout`

## 1. 背景と症状

操作のたびにスクリーンリーダー(NVDA)が「文字変換」「変換停止」と読み上げることがある。
ユーザー報告の再現操作は 3 つ:

- タブを 2 つ以上開いた状態で `Ctrl+W` でタブを閉じ、別タブへフォーカスしたとき
- 設定ダイアログを開いたとき
- 設定ダイアログを閉じたとき

NVDA 日本語版は **IME の open status(日本語入力の ON/OFF)が変化したこと**を
「日本語変換」/「変換停止」として報告する(NVDA 日本語版の説明に、半角全角キー押下時の
これらの報告を音声からビープへ切り替える設定がある)。つまりこの発声は、
**アプリのどこかが実際に IME を ON/OFF している**ことを意味する。

## 2. 根本原因(実測)

### 2.1 kxEdit は IME 状態を触っていない(監査結果)

`src` 配下の IMM32 呼び出しは以下がすべてで、**IME の ON/OFF・変換モードを変える API は
呼び出しゼロ・P/Invoke 宣言もゼロ**である(`ImmSetOpenStatus` / `ImmSetConversionStatus` /
`ImmAssociateContext` / `ImmSimulateHotKey` のいずれも無い)。

| API | 役割 | IME の ON/OFF を変えるか |
|---|---|---|
| `ImmGetContext` / `ImmReleaseContext` | コンテキストの取得・解放(対で使用) | いいえ |
| `ImmGetCompositionStringW` | 未確定文字列・属性・文節・カーソル位置の読み取り | いいえ |
| `ImmNotifyIME(NI_COMPOSITIONSTR, CPS_CANCEL / CPS_COMPLETE)` | 未確定文字列の取消・確定 | いいえ |
| `ImmSetCompositionFontW` | 変換中文字列のフォント伝達(表示) | いいえ |
| `ImmSetCandidateWindow` | 候補ウィンドウの位置(表示) | いいえ |

`WM_IME_SETCONTEXT` の lParam マスク(`ISC_SHOWUICOMPOSITIONWINDOW` の除去)も、
既定の変換ウィンドウ UI を抑止して自前描画に切り替えるだけで IME 状態には触れない。

**エディタは「IME の ON/OFF はユーザー専権」という原則を既に守っている。**

### 2.2 発声源は WinForms の `ImeMode` 機構

スクラッチのプローブ(WinForms の素の `TabControl` + `Control`。kxEdit のコードを 1 行も
使わない構成でも再現する)で、`IMN_SETOPENSTATUS` 受信時の managed スタックを採取した:

```
ImmSetOpenStatus  ← open=True   … NVDA「文字変換」
  ImeContext.SetOpenStatus
  ImeContext.SetImeStatus
  Control.WmImeKillFocus
  Control.WmKillFocus            ← kxEdit.Editor.EditorControl.WndProc 経由
  ...
  ContainerControl.AfterControlRemoved
  TabControl+TabPageCollection.Remove   ← DocumentManager.TryClose

ImmSetOpenStatus  ← open=False  … NVDA「変換停止」(同じスタック)
```

WinForms の該当コード(`Control.Ime.cs`)がそのまま説明になる。

```csharp
private void WmImeKillFocus()
{
    Control topMostWinformsParent = TopMostParent;
    Form? appForm = topMostWinformsParent as Form;

    if ((appForm is null || appForm.Modal) && !topMostWinformsParent.ContainsFocus)
    {
        if (s_propagatingImeMode != ImeMode.Inherit)
        {
            ImeContext.SetImeStatus(PropagatingImeMode, topMostWinformsParent.Handle);
            PropagatingImeMode = ImeMode.Inherit;
        }
    }
}
```

- IME が OFF のとき、`PropagatingImeMode` には `ImeMode.Off` が入る。
- `ImeContext.SetImeStatus(Off)` は **`Enable()`(= `if (!IsOpen) SetOpenStatus(true)` で IME を開く)
  → その後 OFF に戻す**という順序で実装されている。
- したがって **「IME が OFF のとき」に限り必ず 2 回鳴る**。

条件 `(appForm is null || appForm.Modal)` が成立するのが、報告された 2 経路である。

| 操作 | 成立理由 |
|---|---|
| `Ctrl+W` | `TabPages.Remove` の途中で `TabPage` が Form から切り離された状態でエディタが `WM_KILLFOCUS` を受ける → `TopMostParent` が Form ではなく孤児 `TabPage` になり第 1 項が真 |
| 設定ダイアログを閉じる | ダイアログが `Modal` なので第 1 項が真 |

「毎回ではなく、することがある」のも説明がつく。`s_propagatingImeMode` は 1 回発火すると
`Inherit` に戻され、**次にユーザーが IME を切り替えるまで再武装しない**。

なお「設定ダイアログを**開いた**とき」はハーネスで再現しなかった(0/5)。同じ機構の一部である
可能性は高いが断定していない。L5 で切り分ける(§7)。

### 2.3 この機構は Microsoft 自身が「無視される」と文書化した遺物

`Control.ImeMode` は VB6 / Access / FoxPro の `IMEMode` プロパティの移植で、東アジア版の
業務入力フォーム向け機能(「氏名欄はひらがな、電話番号欄は半角英数」)である。
Windows 8 で IME モードがスレッド単位からユーザー単位(グローバル)に変わり、
Microsoft は公式ドキュメントに次を明記している。

> The `ImeMode` property is **ignored on Windows 8** when the global input mode is in effect.
> `InputScope` is recommended instead.

**「無視される」と書かれているのに実装は残っており、`ImmSetOpenStatus` を実際に叩き続けている。**
今はグローバルな IME 状態を叩くため、SR には確実に聞こえる。

## 3. 対策方針

WinForms が「この窓の今の IME モードは何か」を**推測する入口は 1 つだけ**である ——
`ImeContext.GetImeMode()` が読む `ImeModeConversion.InputLanguageTable[...]`。
この配列を読む箇所は `Control.Ime.cs` 全体で 6 行しかない。

そして `Control.PropagatingImeMode` の setter は仕様上こうなっている。

```csharp
switch (value)
{
    case ImeMode.NoControl:
    case ImeMode.Disable:
        return;          // ← NoControl と Disable は「記録しない」
    default:
        s_propagatingImeMode = value;
}
```

したがって **推測結果が常に `NoControl` になれば `s_propagatingImeMode` は永久に `Inherit` のままとなり、
`WmImeKillFocus` も `UpdateImeContextMode` も完全な no-op になる。**

起動時にテーブルの推測セル(index 2 以降)を `ImeMode.NoControl` で塗る。
index 0(`Inherit`)と 1(`Disable`)は構造的な値なので触らない。

### 3.1 実測(各シナリオ 5 回・数値 = `IMN_SETOPENSTATUS` の発生回数)

```
########## 現状 ##########
  タブを閉じる, IME OFF(通常状態)            10  [2,2,2,2,2]
  タブを閉じる, IME ON(入力中)                5  [1,1,1,1,1]
  設定ダイアログ, IME OFF                    10  [2,2,2,2,2]
  設定ダイアログ, IME ON                      0  [0,0,0,0,0]
  ImeMode.Disable 欄の往復(行へ移動)         10  [2,2,2,2,2]   ← 未報告の 4 つ目の経路
  s_propagatingImeMode                       Off

########## 対策後 ##########
  neutralised 3 table(s): s_japaneseTable, s_koreanTable, s_chineseTable
  タブを閉じる, IME OFF                       0  [0,0,0,0,0]
  タブを閉じる, IME ON                        0  [0,0,0,0,0]
  設定ダイアログ, IME OFF                     0  [0,0,0,0,0]
  設定ダイアログ, IME ON                      0  [0,0,0,0,0]
  ImeMode.Disable 欄の往復                    0  [0,0,0,0,0]
  s_propagatingImeMode                       Inherit
  → ImeMode.Disable は依然有効               ok(数値欄で IME が実際に切り離される)
  → エディタの IME                            ok(開ける・かな変換も生きている)
```

報告 3 件に加え、監査で見つかった 4 つ目の経路(「行へ移動」/「CSV セルへ移動」)も同時に消える。

## 4. 採らなかった案(実測による否定)

| 案 | 結果 |
|---|---|
| サポートされた off スイッチを使う | **存在しない**。WinForms の `LocalAppContextSwitches` 全 12 個を確認、IME 関連はゼロ |
| `EditorControl` 側の opt-out(`WM_IME_NOTIFY` を `DefWndProc` で握る / `ImeModeBase => Inherit` / 両方) | **全滅**(10→10)。状態が process 全体の static で、`TabControl` / `TabPage` / `Form` / ダイアログ側が供給源になる |
| `s_propagatingImeMode` を `Inherit` に戻す | **無効**。getter が `GetFocus()` から即再導出する |
| テーブルの `ImeClosed` セルだけ書換 | **半減するが悪化**(10→5)。残り 1 は「IME が勝手に ON になったまま」 |
| `Ctrl+W` の順序入れ替え(残るタブへ先にフォーカス) | タブ経路だけ 10→0。ダイアログ経路は直らない。§3 案を採るなら不要 |
| 設定ダイアログをオーナー無効化のモードレスにする | ダイアログ経路だけ 10→0。SR のダイアログ読み上げ・Escape・再入経路への波及が大きい |
| Windows の「アプリウィンドウごとに異なる入力方式」 | per-thread に戻るだけで発声は消えない |
| NVDA 側で当該報告をビープ化 | 効くが**半角/全角の正当なフィードバックまで潰れる**。アプリ側の解ではない |

## 5. 実装方針

実装計画は別文書 [`2026-09-05-winforms-ime-inference-optout.md`](./2026-09-05-winforms-ime-inference-optout.md)
に置く(タスク分割・完全なコード・検証コマンド)。本節は方針だけを述べる。
別エージェントレビューと品質ゲートは省略しない。

### 5.1 配置

新規 `src/kxEdit.App/ImeStartup.cs`。

```csharp
internal static class ImeStartup
{
    /// <summary>WinForms の IME モード推測を無効化する。成否と対象を返す(テスト観測用)。</summary>
    internal static ImeSuppressionResult SuppressWinFormsImeModeInference();

    /// <summary>テスト用の seam。型解決を外から差し替えられるようにする。</summary>
    internal static ImeSuppressionResult SuppressWinFormsImeModeInference(Type? conversionType);
}

internal readonly record struct ImeSuppressionResult(
    bool Succeeded,
    IReadOnlyList<string> PatchedTables,
    string? FailureReason
);
```

### 5.2 呼び出し位置

`Program.Main` の**最初**。実測で、`Form.Shown` の時点で当てると
**起動時に既に武装済みの分が 1 回だけ鳴った**(`[2,0,0,0,0]`)ため、
「ウィンドウを 1 つも作る前」でなければならない。
既存の `Application.SetUnhandledExceptionMode`(「`Application.Run` より前・かつウィンドウ生成前」)と
同種の位置制約であり、同じ形でコメントに理由を残す。

### 5.3 堅牢性

- テーブルの発見は**フィールド名ではなく型**(`ImeMode[]` 型の static フィールド)で行い、
  `UnsupportedTable` は参照比較で除外する。内部名が変わっても壊れにくくする。
- 全体を `try` / `catch (Exception)` で囲み、失敗しても**起動を絶対に止めない**。
  失敗時は `Trace.TraceWarning` に理由を残し、現状動作(＝発声が出る)に落ちるだけ。
- 冪等。二重呼び出しで壊れない。
- 現在の入力言語に依存しない(全 CJK テーブルを塗るため、CI が en-US でも成立する)。

## 6. 挙動変更と受容事項

### 6.1 明示的な能動 `ImeMode` を持つコントロール

`ImeMode.Hiragana` 等を明示設定したコントロールは、フォーカス後に `ImeMode` プロパティの
**読み値が `NoControl` へ退化する**(実測)。ただし **IME の実挙動は正しい**
(実測 `open=True conv=0x19` = ひらがな)。
kxEdit に該当コントロールは**ゼロ**(`ImeMode.Disable` ×2 と `NoControl` ×1 のみ)。
将来足したときに気づけるよう §7 のテストで固定する。

### 6.2 `ImeMode.Disable` の 2 ダイアログを残す判断(受容)

`GoToLineDialog` / `CsvGoToCellDialog` の入力欄は `ImeMode = ImeMode.Disable` を設定している。
これは **kxEdit 側からの IME 制御**であり、「アプリは IME を勝手に制御しない」という原則に
照らせば撤去候補である(理由は「JIS 環境で全角数字が入る事故を防ぐ」)。

本ブランチでは**撤去しない**(ユーザー判断)。その結果、次が残る(いずれも実測):

| | 現状 | 本対策の適用後 |
|---|---|---|
| 日本語入力中に `ImeMode.Disable` の欄へ入る | IME が閉じる(発声あり) | IME が閉じる(発声あり) |
| そこから編集領域へ戻ったとき | WinForms が黙って開き直す(`open=True`) | **開き直されない(`open=False`)** |
| `ImeMode.NoControl` の欄で同じことをした場合 | 発声 0 / `open=True` のまま | 発声 0 / `open=True` のまま |

つまり **日本語入力中に `Ctrl+G`(行へ移動)や CSV のセル移動を使って戻ると、IME が切れたままになる。**
今は WinForms がその復元役を兼ねているため気づかないが、本対策はその復元役も止める。

**申し送り**: 撤去する場合は `ImeMode.Disable` → `NoControl` に変え、全角数字は
**入力値の正規化**(全角→半角に変換してからパース)で担保する。入力の検証はアプリの仕事であり、
IME を奪う理由にはならない、という整理。2 ファイル + 正規化 1 箇所で済む。

### 6.3 .NET の更新

内部構造が変わればテーブルを発見できず no-op に落ちる(症状が戻るだけで壊れない)。
§7 のテストが CI で検知する。

## 7. テスト方針

### L3 (`kxEdit.App.Tests`)

1. `SuppressWinFormsImeModeInference()` が `Succeeded=true` を返し、対象テーブルを 1 つ以上挙げる。
2. 実行後、日本語テーブルの **index 2 以降がすべて `NoControl`** かつ
   **index 0 = `Inherit` / index 1 = `Disable` が不変**であること
   (ループの開始位置や範囲を変える変異がここで死ぬ)。
3. 冪等: 2 回呼んでも `Succeeded=true`。
4. 型解決に失敗する seam(`null` を渡す)で、**例外を投げず** `Succeeded=false` と
   `FailureReason` を返すこと。
5. §6.1 の固定: `ImeMode.Disable` を設定したコントロールが**依然として IME を切り離す**こと。

**注意**: 本テストはプロセス全体の static 状態を変える。`kxEdit.App.Tests` は 1 プロセスで走るため、
以後のテストは「推測無効」の状態で動く —— これは製品の状態と同じなので受容する。
テスト間の順序依存を作らないよう、冪等性(3)を先に固定する。

**カバレッジの穴(受容)**: `Program.Main` 内の呼び出し配線そのものは自動テストで観測できない
(`Main` は `[STAThread]` + `Application.Run` で叩けない。既存の `SetUnhandledExceptionMode` /
`EncodingCatalog.EnsureRegistered` と同じ)。起動時に `Trace` へ 1 行残し、L5 で確認する。

> **追記(Task 2 実装時に判明・2026-09-05)**: 上の受容は**前提が誤っていた**。
> `Main` を**実行**して観測できないのは事実だが、観測手段は実行だけではない。
> このリポジトリには `tests/kxEdit.App.Tests/IlCallees.cs`(IL から `call` / `callvirt` /
> `newobj` の対象を集める)という既存の道具と、`MainFormSmokeTests` の
> `ProgramMain_builds_the_form_through_the_tested_composition_point` という
> **まさに `Main` に対して同じ言い訳を反証している稼働中の前例**がある。
> **呼び出しの有無と順序は固定できる。**
>
> しかも本対策の核心は「呼ぶこと」ではなく **「最初のウィンドウを作る前に呼ぶこと」**である
> (§5.2 の実測 `[2,0,0,0,0]`)。そこで Task 2 で `MainFormSmokeTests` に
> `ProgramMain_suppresses_ime_mode_inference_before_creating_any_window` を 1 本追加し、
> `ImeStartup.SuppressWinFormsImeModeInference` の呼び出しが `Program.CreateMainForm`
> (= `MainForm` を組み立てる合成点 = 最初のウィンドウ)**より前**にあることを IL 上の
> 順序で固定した(`IlCallees.Scan` は IL を先頭から走査して追加するため、返却リストは
> オフセット昇順=実行順)。`Contains` / 位置比較は走査の偽陽性で緑になりうるので、
> 「呼び出しを合成点の後ろへ移す」「呼び出しを丸ごと削除する」の 2 変異を当てて
> 実際に赤くなることを確認してある。
>
> ただし IL 走査で**固定できないもの**は残る —— **渡した引数の値**と、
> **実際に推測抑止が効いたかどうか**。前者は呼出集合では原理的に観測できず、後者は
> `ImeStartupTests`(テーブルが塗られたことの観測)と **L5(実発声)** の担当である。
> `Trace` へ 1 行残す方針も変えない。
>
> これは CLAUDE.md §4「**『網が無い』という主張も検証対象**」の実例である。
> 「観測できない」と書く前に、既にあるツールと前例を探すこと。

### L5 (実機 SR 検証・**必須**)

SR 経路に触れる変更のため必須。チェックリストは
`docs/plans/2026-09-05-winforms-ime-inference-optout-l5-checklist.md` に作る。最低限:

1. 半角/全角で IME を切り替えた直後に `Ctrl+W` でタブを閉じる → 「文字変換」「変換停止」が**出ない**。
2. 同じ前提で設定ダイアログを開く → **開いたときに出るか**を記録(§2.2 の未確定点の切り分け)。
3. 設定ダイアログを閉じる → 出ない。
4. 日本語入力(未確定文字列あり)が従来どおり読み上げられる(変換候補・確定)。
5. `Ctrl+G` で行へ移動 → §6.2 の残存(発声あり・戻ると IME が切れている)を**確認して記録**する。
6. 半角/全角キー自体の「日本語変換」「変換停止」は**従来どおり鳴る**(潰していないこと)。

## 8. 上流への提起(任意・並行)

`System.Windows.Forms.DisableImeModeInference` のような AppContext スイッチを求める
feature request を dotnet/winforms へ出す余地がある。Microsoft 自身が
「Windows 8 以降このプロパティは無視される」と文書化している機構であること、
`ImeMode.Off` のときだけ「一度開いてから閉じる」実装になっており日本語 SR が毎回 2 回発声すること、
を根拠にできる。同種の `PropagatingImeMode` 不整合(中国語 IME で `ImeMode.Close` を誤記録する)は
既に修正歴があり前例もある。

## 9. 参照

- [dotnet/winforms `Control.Ime.cs`](https://github.com/dotnet/winforms/blob/main/src/System.Windows.Forms/System/Windows/Forms/Control.Ime.cs)
- [dotnet/winforms `LocalAppContextSwitches.cs`](https://github.com/dotnet/winforms/blob/main/src/System.Windows.Forms.Primitives/src/System/LocalAppContextSwitches/LocalAppContextSwitches.cs)
- [Control.ImeMode Property (Microsoft Learn)](https://learn.microsoft.com/en-us/dotnet/api/system.windows.forms.control.imemode?view=windowsdesktop-10.0)
- [Switch text input changed from per-thread to per-user (Microsoft Learn)](https://learn.microsoft.com/en-us/previous-versions/hh994466(v=vs.85))
- [NVDA 日本語版の説明](https://www.nvda.jp/nvda2024.1jp/ja/readmejp.html)
