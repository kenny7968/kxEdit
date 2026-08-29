# 未処理例外と入力の取りこぼし(A-13 / M-1 / A-20)設計書

対象ブランチ: `feature/unhandled-exception-safety`
起点: [v0.2 リリース前バグ監査](2026-08-22-v0.2-release-bug-audit.md) §4 の A-13 / A-20、§6 の M-1。

本書は**策定時スナップショット**(CLAUDE.md §8)。実装時の精密化・実施記録は §10 に追記する。

## 1. 目的

監査書 §4 の未対応項目のうち、**未保存データの喪失に直結する系統**を 1 ブランチで塞ぐ。

| ID | 症状 | 監査時の検証状態 | 本書での検証状態 |
|----|------|------------------|------------------|
| A-13 | クリップボード `ExternalException` が Copy/Cut/Paste で未捕捉 → WinForms 既定の未処理例外ダイアログ | コード | **実機** |
| M-1 | グローバル例外ハンドラ不在 | コード | **実機**(A-13 の帰結として) |
| A-20 | WM_CHAR で分割到着するサロゲートペアが高・低それぞれ U+FFFD 化 | 推定 | **実機**(ただし発現条件は監査書と異なる) |

## 2. 現行 main での実在確認(2026-08-29)

Release ビルドの `kxEdit.exe`(`cf569d8`)を実際に起動して確認した。

### 2.1 A-13 — 再現。かつ監査書より深刻

再現手順: 別プロセスが `OpenClipboard` でクリップボードを保持している間に、選択のある kxEdit で Ctrl+C。

結果:

1. WinForms 既定の未処理例外ダイアログが出る。本文は
   「アプリケーションのコンポーネントで、ハンドルされていない例外が発生しました。…
   **要求されたクリップボード操作に成功しませんでした。**」、ボタンは `詳細(D)` / `続行(C)` / `終了(Q)`。
2. `終了(Q)` を押すと「無題 1 の変更を保存しますか?」(はい/いいえ/キャンセル)が出る。
3. **`キャンセル` を押してもアプリは終了する**(3 回中 3 回)。
4. しかも hot exit バックアップが書かれないことがある(3 回中 2 回は
   `%APPDATA%\kxEdit\backups` に何も残らず、未保存の無題文書が完全に消えた。
   残った 1 回との差は本書の範囲では特定していない=**書かれるかどうかは当てにできない**)。

つまり A-13 は「うるさいダイアログ」ではなく、**通常操作(Ctrl+C)から到達する未保存データの喪失経路**である。
監査書 §4 は「WinForms 既定の例外ダイアログ(続行/終了)」までしか書いていない。
キャンセルが効かないこと・バックアップが書かれないことは本書で新たに確認した。

`Clipboard.SetText` は STA 必須という契約(`EditorControl.cs:1566` の remarks)は満たされているので、
これはスレッド契約の問題ではなく、単に**他プロセスがクリップボードを保持している間の失敗が未捕捉**である。

該当箇所:

- `EditorControl.cs:1579` — `Copy` の `Clipboard.SetText`
- `EditorControl.cs:1626, 1628` — `Paste` の `Clipboard.ContainsText` / `Clipboard.GetText`
- `Program.cs` — `ThreadException` / `UnhandledException` ハンドラなし(M-1)

### 2.2 A-20 — 再現。ただし監査書が挙げた再現手順は成立しない

監査書は発現源として「絵文字パネル Win+.」と「SendInput 系ツール」を挙げていた。
**前者は反証、後者に相当する素の WM_CHAR 経路は再現**した。

**(a) 絵文字パネル(Win+.)は正常**。kxEdit で 😂(U+1F602)を挿入すると
ステータスバーは「行 1, 桁 3」= 2 UTF-16 単位が 1 コードポイントとして正しく入る。

理由は最小プローブ(WinForms `Control` 直接派生・全メッセージを `base.WndProc` に流す)で確認した。
絵文字パネルの確定は次の順で届く:

```
msg=0x010F (WM_IME_COMPOSITION) wParam=0xD83D lParam=0x00000800 (GCS_RESULTSTR)
msg=0x0286 (WM_IME_CHAR)        wParam=0xD83D   → OnKeyPress U+D83D (high surrogate)
msg=0x0286 (WM_IME_CHAR)        wParam=0xDE02   → OnKeyPress U+DE02 (low surrogate)
msg=0x0102 (WM_CHAR)            wParam=0xD83D   → OnKeyPress U+D83D (high surrogate)
msg=0x0102 (WM_CHAR)            wParam=0xDE02   → OnKeyPress U+DE02 (low surrogate)
```

素通しのコントロールでは **OnKeyPress が 4 回**呼ばれる。kxEdit がこれを踏まないのは、
`EditorControl.WndProc` の `WM_IME_COMPOSITION` 分岐が `m.Result = IntPtr.Zero; return;` で
**`base.WndProc` に流さない**ため。DefWindowProc に届かない = WM_IME_CHAR も WM_CHAR も生成されない。
確定文字列は `ImmGetCompositionStringW(GCS_RESULTSTR)` が完全なペアで返し、
`InsertConfirmedText` に 2 文字の string として渡る。監査書の「IME 確定経路は無事」は正しい。

**(b) 素の WM_CHAR ペアは U+FFFD 化する**。`PostMessageW(hwndFocus, WM_CHAR, 0xD83D, 1)` と
`0xDE02` を続けて投げると、バッファの内容は `U+FFFD, U+FFFD`(Ctrl+A → Ctrl+C でクリップボードを
ダンプして確認)。監査書の機構(`AppendBuffer.Append` の
「孤立サロゲートは既定で U+FFFD 置換」)が予想どおり発現する。

該当箇所:

- `EditorControl.Input.cs:70` — `InsertConfirmedText(ch.ToString())`(WM_CHAR 1 通ごとに 1 char)
- `AppendBuffer.cs:26` — 孤立サロゲートの U+FFFD 置換(最後の砦・**変更しない**)

したがって A-20 の現実の発現源は「**KEYEVENTF_UNICODE の SendInput / PostMessage で
サロゲートペアを 2 通の WM_CHAR として送るツール**」に限られる(AutoHotkey 等の
テキスト展開ツール・自動化ツール・一部のリモートデスクトップ/オンスクリーンキーボード)。
IME・絵文字パネル・貼り付けは無事。**監査書が想定したより発現頻度は低い**が、
発現したときは無言で本文が壊れる。

### 2.3 検証の再現材料

- WM_CHAR プローブ: 本ブランチには含めない(使い捨て)。§2.2 のログが結果。
- クリップボード占有: 別プロセスで `OpenClipboard(NULL)` して 12 秒保持するだけ。

## 3. 決定した方針(2026-08-29・ユーザー承認済み)

1. **A-13 は発生源で捕捉する**。M-1 のハンドラに落とさない。
   クリップボードの失敗は回復可能な事象で、アプリを終了させる理由がない。
2. **M-1 のハンドラは「バックアップして終了」の一択**。`続行` は出さない。
   壊れた状態で走り続けるより、退避して落ちるほうが結果が読める。
   §2.1 のとおり WinForms 既定ダイアログの `続行` / `終了` は結果が当てにならないので、
   **既定ダイアログに到達させないこと**自体が目的である。
3. **A-20 の孤立サロゲートは破棄する**(Scintilla 準拠)。U+FFFD を入れない。
   不完全な入力を本文に残さない。

## 4. 設計 — A-13(クリップボード失敗の捕捉と通知)

### 4.1 捕捉する例外

`System.Runtime.InteropServices.ExternalException` のみ。
`COMException` はその派生なので追加で列挙しない。`catch (Exception)` にはしない
(`ArgumentNullException` 等の呼び出し側バグを握り潰さない)。

### 4.2 `Cut` の既存契約を壊さないこと

現行 `Cut` は次の契約で書かれている(`EditorControl.cs:1588-1591` の remarks):

> `Copy` → `TextBuffer.Replace` で「クリップボード書き込み → 本文削除」の順に実行する
> (Copy 失敗時に本文だけ消える事故を防ぐ= `Clipboard.SetText` が例外を投げると本メソッドも
> 上に throw して `AfterEdit` へ到達しない)。

**この契約は load-bearing**。`Copy` の中で例外を握って握り潰すと、`Cut` は
「クリップボードに入っていないのに本文が消える」= A-13 より重いデータ喪失に化ける。

そこで:

- `Copy` の戻り値を `void` → **`bool`** に変える(true = クリップボードへ書けた)。
- `Cut` は `if (!Copy()) return;` で、false のとき `_buffer.Replace` に進まない。
- `Paste` も同様に `bool` を返す(`ContainsText` / `GetText` のどちらで落ちても false)。

`Copy` / `Paste` の戻り値は既存の呼び出し側(`InputRouter.cs:302,312,322,363,369,392`・
`MainForm.cs:768,774,780`)では捨ててよい。戻り値の追加は破壊的変更ではない。

### 4.3 ユーザーへの通知経路

Editor 層(`kxEdit.Editor`)は `IAnnouncer` を持たない。プロジェクト参照は
`kxEdit.Accessibility` と `kxEdit.Core` だけで、`IAnnouncer` は `kxEdit.App/Speech/IAnnouncer.cs` にある。
参照を足す向きの変更はしない(層の向きが逆になる)。

既存パターンに乗せる:

1. `EditorControl` に `public event EventHandler<ClipboardFailureKind>? ClipboardFailed;` を足す
   (`SavePointReached` / `SavePointLeft` / `UpdateUI` と同じ列)。
   `ClipboardFailureKind` は `Copy` / `Paste` の 2 値 enum(文言を分けるため)。
2. `DocumentManager.CreateNew`(`DocumentManager.cs:82-83` で `SavePointLeft` /
   `SavePointReached` を購読している箇所)で購読し、`DocumentManager` から
   `public event EventHandler<ClipboardFailureKind>? ClipboardFailed;` として再送する
   (`KeyBasedSwitch` / `EditorGotFocus` と同じ形)。
3. `MainForm` が購読して `_announcer.Say(...)` する。

文言(日本語・SR で読める短文):

- Copy / Cut 失敗: 「クリップボードを使用できません。他のアプリが使用中の可能性があります」
- Paste 失敗: 同上(貼り付け側も同じ原因なので文言を分けない案もある。実装時に 1 文言へ寄せてよい)

晴眼・弱視ユーザー(CLAUDE.md §2)向けにはステータスバー表示も考えられるが、
本ブランチでは**発声のみ**とする。ステータスバーは行・桁・符号化・EOL の常設表示で、
一時メッセージを載せる仕組みが今は無く、作ると本題より大きくなる。

### 4.4 `Cut` の `IsComposing` 経路

`Cut` / `Paste` は先頭で `CancelCompositionAndDefault()` を呼ぶ。
クリップボード失敗で早期 return しても、IME 取消は既に済んでいる=巻き戻さない。
「Ctrl+X が失敗したのに未確定文字列だけ消える」ことになるが、
IME 取消は Ctrl+X を押した時点でユーザーの意図として確定しているので許容する。

## 5. 設計 — M-1(未処理例外を握って退避してから終了)

### 5.1 配線

`Program.Main` で、`Application.Run` の**前に**:

```
Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
Application.ThreadException += ...;
AppDomain.CurrentDomain.UnhandledException += ...;
```

`SetUnhandledExceptionMode` は `Application.Run` より前・かつ
ウィンドウ生成前に呼ぶ必要がある(`ApplicationConfiguration.Initialize()` の直後が適切)。

### 5.2 ハンドラの動作

ロジックは `Program` に直書きせず、テスト可能な `CrashHandler`(App 層・新規)へ切り出す。
`Program` は「seam を注入して購読する」だけにする。

1. **再入ガード**。`Interlocked.Exchange` で 1 回だけ通す。
   ハンドラの中で再び例外が出ても無限ループしない。
2. **退避**: `BackupCoordinator.FinalFlushForRestore()` → `WaitForFinalFlush()`。
   この 2 つは既存 API で、A-8(PR #50)が既に「hot exit の事後条件検査」として整えている。
   **必ず対で・この順で呼ぶ**(`BackupCoordinator.cs` の `WaitForFinalFlush` remarks が
   「先行 tick の失敗 Id を消すのは `ReconcileContent` 冒頭の drain だけ」と明示している)。
3. **通知**: `MessageBox` で日本語。`WaitForFinalFlush` の戻り値で文面を変える。
   - true: 「予期しないエラーが発生したため kxEdit を終了します。編集中の内容は退避したので、
     次回起動時に復元できます。」
   - false: 「予期しないエラーが発生したため kxEdit を終了します。
     **編集中の内容を退避できなかった可能性があります。**」
4. **終了**: `Environment.Exit(1)`。`Application.Exit()` は使わない
   (§2.1 のとおり、この経路の `FormClosing` は結果が読めない。既に退避は済んでいるので、
   終了確認をもう一度出す意味がない)。

### 5.3 スレッドの制約

- `Application.ThreadException` は **UI スレッドで発火する**。ここは制約なし。
- `AppDomain.UnhandledException` は**任意のスレッドで発火する**。
  `BackupCoordinator` は「UI スレッド専有・`_map` は非スレッドセーフな `Dictionary`」
  (`WaitForFinalFlush` の remarks)なので、UI スレッドへ marshal しなければならない。
  → `MainForm.Invoke` を使う。**ただし** UI スレッドが死んでいる/ブロックされている場合は
  戻ってこないので、`IsTerminating == true` のときだけ、タイムアウト付き
  (`BeginInvoke` + `WaitOne(FinalFlushWait)`)で試み、取れなければ通知だけして落ちる。
- 現状 Probe / Writer は内側で catch 済み(監査書 M-1 の但し書き)なので、
  `AppDomain.UnhandledException` 経路は「今は誰も投げていない」保険である。
  保険であることを設計書に書き残し、**タイムアウトで諦める側に倒す**。

### 5.4 `CrashHandler` の seam

App.Tests から「flush → 通知 → 終了」の順序と、
`WaitForFinalFlush` の戻り値で文面が変わることを検証できるようにする。

```
interface ICrashSink {
    bool FlushBackups();          // FinalFlushForRestore + WaitForFinalFlush
    void Notify(bool flushed);    // MessageBox
    void Exit();                  // Environment.Exit(1)
}
```

`CrashHandler.Handle(Exception, ICrashSink)` が上の順に呼ぶ。
`Program` が本物の実装を渡す。実装は計画側で詰める。

## 6. 設計 — A-20(サロゲートペアの結合)

### 6.1 保留

`EditorControl.Input.cs` に `private char _pendingHighSurrogate;`(既定 `'\0'` = 保留なし)。

`OnKeyPress` の分岐を次にする(制御文字の除外は現状どおり先に行う):

| 到着 char | 保留あり | 動作 |
|-----------|----------|------|
| 高サロゲート | – | 直前の保留があれば**破棄**し、この char を保留。挿入しない |
| 低サロゲート | あり | ペアを `InsertConfirmedText(new string([hi, lo]))`。保留クリア |
| 低サロゲート | なし | **破棄**(現状の U+FFFD 挿入をやめる) |
| それ以外 | あり | 保留を**破棄**してから通常どおり挿入 |
| それ以外 | なし | 現状どおり |

`e.Handled = true` は保留した場合も立てる(WM_CHAR は消費済み)。

### 6.2 保留の寿命

保留は「**直後の WM_CHAR にだけ効く**」という契約にする(Scintilla の
`lastHighSurrogateChar` と同じ)。次の契機で破棄する:

- `OnKeyDown`(`EditorControl.Input.cs:40`)— キー入力が挟まった
- `OnLostFocus` — フォーカスが移った
- `ImeController.OnStartComposition` 経由 — IME 変換が始まった
- マウス操作(クリックでキャレットが動く経路)

**この列挙は原理的に漏れる**(監査書 §9 V-7 の教訓)。列挙で守るのではなく、
「保留があるまま `InsertConfirmedText` 以外の経路で本文が変わったら破棄」という
**事後条件側**に置けないか実装時に検討する。ただし本件は「破棄しそこねても
次の非低サロゲート char で破棄される」ので、漏れの被害は
「保留が 1 文字ぶん長生きする」だけで本文は壊れない。列挙漏れの重大度は低い。

### 6.3 上書きモード(Overtype)

`InsertConfirmedText` の Overtype 分岐は既に `TextBoundary.CodePointLengthAt` で
コードポイント単位の上書き長を求めている(`EditorControl.Input.cs:305-310`)。
ペアを 1 つの string として渡せば、**監査書の「上書きモードでは既存 2 文字を潰す」は
追加の変更なしに解消する**。1 コードポイントが 1 コードポイントを置き換える。

### 6.4 `AppendBuffer` は変えない

`AppendBuffer.Append` の「孤立サロゲートは既定で U+FFFD 置換」は最後の砦として残す。
入口(`OnKeyPress`)で結合し、それでも孤立が来たら U+FFFD にする、の二段構え。
Core を触らないので L1 の変更はない。

## 7. タスク分割

| # | 内容 | 層 | 追加レビュー(CLAUDE.md §3) |
|---|------|-----|------------------------------|
| 1 | A-20: `OnKeyPress` のサロゲート結合 + 保留破棄 | Editor | — |
| 2 | A-13: `Copy` / `Paste` の `bool` 化 + `ClipboardFailed` イベント | Editor | **コード品質**(新しい seam を足す) |
| 3 | A-13: `DocumentManager` → `MainForm` の配線と発声 | App | — |
| 4 | M-1: `CrashHandler` + `Program` 配線 | App | **コード品質**(プロセス終了経路の新しい抽象) |

Task 2 と 4 は後続が依存する seam を作るのでタスク時にコード品質レビューを行う。
脆弱性レビューは §3 の該当面(外部入力のパース・パス操作・プロセス起動・WebView・ネットワーク)に
触れないので、最終ブランチレビューの脆弱性パスに任せる。

## 8. テスト

### 8.1 L1 — `kxEdit.Core.Tests`

変更なし(Core を触らない)。

### 8.2 L2 — `kxEdit.Editor.Tests`

A-20(`__TestProcessMessage` / `TestHook_WndProc` で WM_CHAR を投げる):

- 高 → 低 の 2 通で 1 コードポイントが入る(`CharLength == 2`・`GetText` がペアで一致)
- 高 → BMP 文字: 高が捨てられ BMP だけ入る(**`CharLength == 1`**)
- 低のみ: 何も入らない(`CharLength == 0`)
- 高 → 高 → 低: 最初の高が捨てられ、2 つ目のペアだけ入る
- 高 → `OnKeyDown`(矢印キー)→ 低: 何も入らない(保留破棄の確認)
- 高 → `LostFocus` → 低: 同上
- Overtype ON で既存 1 コードポイントの上に ペアを入れると、既存が 1 つだけ置き換わる
  (**非既定位置から**開始する = CLAUDE.md §4-B。文書は `あXい` のように prefix / suffix を持たせ、
  ペアで潰れるのが `X` 1 つだけであることを両端で固定する)

A-13(`IClipboard` seam を注入し `ExternalException` を投げさせる):

- `Copy` が false を返し、本文は不変
- `Cut` が本文を**変えない**(既存契約の回帰テスト。これが最重要)
- `Paste` が false を返し、本文は不変
- `ClipboardFailed` がちょうど 1 回・正しい `ClipboardFailureKind` で上がる
- 成功時は `ClipboardFailed` が上がらない

`Clipboard` は静的クラスなので、`EditorControl` に `IClipboard` を注入する seam が要る。
既存の `IImeContext`(`ImeController` の `_contextFactory`)と同じ形にする。

### 8.3 L3 — `kxEdit.App.Tests`

- `DocumentManager` が `EditorControl.ClipboardFailed` を再送する
- `MainForm` 相当の購読で `IAnnouncer.Say` が期待の文言で 1 回呼ばれる
- `CrashHandler.Handle` が `FlushBackups` → `Notify` → `Exit` の順に呼ぶ(Fake `ICrashSink`)
- `Notify` に渡る `flushed` が `FlushBackups` の戻り値と一致する(true / false 両方)
- 再入: `Handle` を 2 回呼んでも `Exit` は 1 回(再入ガード)

### 8.4 ミューテーション検証

**実施しない**。CLAUDE.md §4-A の「有効」はカーソル移動・選択範囲算出・Undo/Redo の履歴管理・
検索置換のパース・Lexer に限定されており、本件は

- A-20 = WM_CHAR のイベントマッピング(§4-A 禁止「キーバインドのイベントマッピング」に該当)
- A-13 / M-1 = 例外処理と終了経路(同「ファイルの入出力処理」に近い性質・GUI の操作性)

のいずれも「有効」側に当たらない。§8.2 の Overtype ケースだけは
`TextBoundary.CodePointLengthAt` の境界計算に触れるが、そこは既存コードで
CRLF atomic 化(PR #26)のときに検証済みであり、本ブランチでは形を変えない。

## 9. L5(実機 SR 検証)

**必須**。App の Speech 系(`IAnnouncer`)に新しい発声を足すため(CLAUDE.md §5)。

1. クリップボードを別プロセスで占有した状態で Ctrl+C
   → 例外ダイアログが**出ない**こと。NVDA が「クリップボードを使用できません…」を読むこと。
2. 同状態で Ctrl+X → 本文が消えていないこと(選択が残っていること)。
3. 同状態で Ctrl+V → 本文が変わらないこと。発声があること。
4. 絵文字パネル(Win+.)で 😂 を挿入 → 1 文字として入り、→ / ← が 1 回で跨ぐこと(**非退行**)。
5. IME で通常の日本語変換・確定 → 退行がないこと。
6. 上書きモードで既存文字の上に絵文字 → 1 文字だけが置き換わること。

M-1 のハンドラは意図的にクラッシュさせないと確認できない。
実機での確認は「A-13 の修正後に例外ダイアログが出ないこと」で間接的に済ませ、
ハンドラ本体の順序は §8.3 の自動テストで担保する。

### 9.1 実施済みの手動スモーク(2026-08-29・SR なし)

計画 Task 4 Step 5。Release ビルドを起動し、別プロセスで `OpenClipboard(NULL)` を保持した
状態で操作した。UIA / Win32 で観測(**発声そのものは未検証=L5 の代替にはならない**)。

| # | 操作 | 結果 |
|---|------|------|
| 1 | クリップボード保持中に Ctrl+C | **未処理例外ダイアログが出ない**(プロセスの可視トップレベルウィンドウは主フォーム 1 つのみ) |
| 2 | 同上 | 通知ラベルが「クリップボードにコピーできません。他のアプリが使用中の可能性があります」 |
| 3 | 同状態で Ctrl+X | 本文が消えない(UIA TextPattern で `SMOKE-A13-TEXT` を確認) |
| 4 | その後に通常終了 | 保存確認が正常に出て「いいえ」で終了(§2.1 の「キャンセルが効かない」状態から回復) |

§9 の 1〜3 に対応。4〜6(絵文字パネル・IME・上書きモード)と実発声の確認は L5 で行う。

追加で `PreviewUserDataSweeper`(M-V1)も実機確認した: 起動前に
`%LOCALAPPDATA%\kxEdit\WebView2` に 2026-08-22 由来の `preview-*` が 3 件残っていた
(本ブランチ以前から実際に漏れていた)。Release ビルドを起動すると 3 件とも回収され、
アプリは通常どおり起動・終了した。

## 10. 非目標(YAGNI)

- **M-3(512MB 上限の未捕捉)/ M-21(OOM)の個別捕捉**はしない。M-1 のハンドラが受け皿になる。
- **ステータスバーへの一時メッセージ表示**はしない(§4.3)。
- **`AppendBuffer` の U+FFFD 置換の変更**はしない(§6.4)。
- **クリップボードのリトライ**はしない。WinForms の `Clipboard.SetText` は既に
  内部で 10 回 × 100ms リトライしている(だから §2.1 の再現には 12 秒の占有が要った)。
  その上で失敗したなら、ユーザーに伝えるのが正しい。
- **`ThreadExceptionDialog` のカスタマイズ**はしない。到達させないのが方針(§3.2)。

## 10-A. 実装時の決定記録(2026-08-29 追記)

本書冒頭は「実施記録は §10 に追記する」と書いているが、§10 は「非目標」で追記先ではない。
記録はここ(§10-A)と §11 に置く。以下は**計画が「決めて記録せよ」と指定した項目**と、
設計・計画からの**意図的な逸脱**である(CLAUDE.md §2)。

### A-20(Task 1)

- **`VK_PACKET` は破棄契機から除外する**(最終レビューで発見・§6.2 の表への実質的な修正)。
  §6.2 は破棄契機に「`OnKeyDown`(キー入力が挟まった)」を挙げていたが、
  A-20 の**現実の発現源**である `KEYEVENTF_UNICODE` の `SendInput` は、WM_CHAR を運ぶために
  合成キー `VK_PACKET`(0xE7)を前置する。実際の到着順は
  `WM_KEYDOWN VK_PACKET → WM_CHAR(高) → WM_KEYUP → WM_KEYDOWN VK_PACKET → WM_CHAR(低)`。
  `OnKeyDown` で無条件に破棄すると、**直そうとしている経路でだけペアが結合しない**
  (絵文字が U+FFFD ではなく丸ごと消える=修正前より悪い)。
  `VK_PACKET` は本物のキー入力ではないので破棄しない。
  §2.2 の実機再現が `PostMessageW(WM_CHAR, ...)` を直接投げる形(= `VK_PACKET` を伴わない)
  だったため、設計時にはこの差が見えていなかった。
- **テストは `__TestProcessMessage` の実 WndProc 経路も通す**(§8.2 の指定どおり)。
  `OnKeyPress` をリフレクションで直接叩くだけだと、上の取りこぼしを検出できない。
  上書きモードの回帰(監査書の「既存 2 文字を潰す」に対する唯一の網)も実経路へ寄せた。
- **§6.2 が挙げた破棄契機「マウス操作」は実装しない**(意図的な逸脱)。
  実装は `OnKeyDown`(`VK_PACKET` を除く)/ `OnLostFocus` / `OnKeyPress` の各分岐 / `AfterEdit`。
  マウスでキャレットを動かしても保留は残るが、実際に踏むには 1 回の `SendInput` が生む
  2 通の WM_CHAR の**間に**マウス選択を挟む必要があり、現実性が極めて低い。
  踏んだ場合の被害は「選択範囲がペアで置換される」なので無害ではない点は認識しておく。
  脆弱性パスの判定は「攻撃者が合成できるのは自分が送った高位と低位の組だけで、
  WM_CHAR を送り込める主体は最初から任意の文字列をタイプさせられる=能力の増分ゼロ」。
- **IME 開始時(`DeleteSelectionForImeStart`)への追加はしない**: IME 確定は
  `InsertConfirmedText` → `AfterEdit` を通るので事後条件側で覆われる。
- **保留の破棄契機**: `OnKeyDown`(`VK_PACKET` を除く)と `EditorControl.OnLostFocus`
  (実在した override に 1 行追加)に
  加えて、**`AfterEdit` にも事後条件として置いた**。§6.2 が「列挙は原理的に漏れる。事後条件側に
  置けないか検討する」と求めていた点への回答で、`AfterEdit` は編集経路の唯一の後処理なので
  「本文が変わった=保留は対にならない」を 1 か所で担保できる。IME 開始時
  (`DeleteSelectionForImeStart`)への追加は**しない**: IME 確定は `InsertConfirmedText` →
  `AfterEdit` を通るため事後条件側で覆われる。
- `'\0'` の直書きをやめ `NoPendingHighSurrogate` 定数にした(番兵であることを型で示すため)。

### A-13(Task 2 / Task 3)

- **`ClipboardFailureKind` の値は `Copy` / `Paste` ではなく `Write` / `Read`**(§4.3 からの逸脱)。
  `Cut` の失敗も「書き込み」に含められるため。
- **`Copy` / `Paste` の戻り値の意味**は「意図した転送が行われたか」で統一した。
  空クリップボードの `Paste` は **`false`**(計画のコードは `true` を筋としていたが、
  「挿入したか」で揃えるほうが `Cut` の判定と一貫する)。false は no-op も失敗も含むので、
  **失敗の判定は必ず `ClipboardFailed` で行う**契約を XML doc に明記した。
- **App.Tests から Editor の internal を見る**(計画 Task 3 の選択肢 (a) を採用)。
  `kxEdit.Editor.csproj` に `InternalsVisibleTo kxEdit.App.Tests` を追加。テスト専用 seam を
  public へ昇格させるより副作用が小さく、Editor.Tests / Editor.Smoke に前例がある。
- 発声文言は読み書きで分けた(§4.3 は「1 文言へ寄せてよい」としていたが、
  SR で操作を聞き分けられるほうがよい)。実文言は `MainForm.ClipboardFailureMessage`。

### M-1(Task 4)

- **`Notify` は marshal しない**(計画 Task 4 Step 3 が「決めて記録せよ」と指定した項目)。
  この時点で退避は済んでおり直後に `Environment.Exit` する。UI スレッドが死んでいる/
  ブロックされている場合に marshal すると「通知も出ずに固まる」= WinForms 既定より悪くなるため、
  呼ばれたスレッドでそのまま `MessageBox` を出す。代償は、背景スレッド発火時に
  オーナー無しのダイアログが背後に出る可能性(`AppDomain` 経路は現状「誰も投げていない保険」)。
- **`FlushBackupsForCrash` の前提ゲートは hot exit の silent path と意図的に違う**。
  `BackupEnabled` と「32M 超 dirty なし」だけを見て、**`RestoreOpenFilesOnStartup` は見ない**。
  OFF でも `OfferBackupRestoreOnStartup` が次回起動で復元を提案できるため、OFF を理由に
  「退避できなかった可能性があります」と出すと、実際には復元できる構成に嘘の悲観を出すことになる
  (レビュー Major-3)。計画のコードにはゲート自体が無く、そのままだと BackupOFF 環境で
  `WaitForFinalFlush` の「書くものが無い=失敗も無い」の true を掴んで
  「復元できます」と嘘をつくところだった。
- **同期プリミティブは `TaskCompletionSource<bool>`**(計画は `ManualResetEventSlim` + `using`)。
  `SerialBackupWriter.WaitForPendingJobs` が同じ問題に対して同じパターンを避ける判断を
  明文で残しているため、先例に揃えた(レビュー Major-1)。
- **本番 sink は `Program` の入れ子ではなく `UiCrashSink` として切り出した**。
  marshal・タイムアウト・文面選択という「`CrashHandler` では検証できないロジック」が
  ここに残るため、`ICrashUiHost` 越しにテスト可能にした(レビュー Major-2)。
- 例外の中身は `Trace` へ落とすだけで `MessageBox` には出さない(パス等が画面に出る面を増やさない)。
- **`PreviewUserDataSweeper`(起動時 sweep)を本ブランチに含めた**(計画外の追加・最終レビュー M-V1)。
  `Environment.Exit` はフォームの `Dispose` を走らせないため、プレビューを開いたまま
  クラッシュすると `%LOCALAPPDATA%\kxEdit\WebView2\preview-{guid}` が**単調増加し回収されない**。
  そこには WebView2 のプロファイル(Code Cache / Local Storage 等)が入り、プレビューは
  文書のディレクトリを base URI に持つ(A-2)ので、相対参照で取得した外部リソースの
  キャッシュも入りうる=単なるディスク消費ではない。
  「終了直前に消す」は成立しない(WebView2 プロセスがまだプロファイルを掴んでいる)ため、
  `PreviewUserDataFolder` の doc が「v0.12 以降候補」としていた起動時 sweep を前倒しした。
  **並行インスタンス対策が要**: 素朴に消すと別プロセスのプロファイルを、ロックに当たる前に
  一部だけ消して壊しうる。「自分以外の kxEdit プロセスが居ないときだけ実行する」で回避した。
- **`CrashHandler` / `ICrashSink` は `internal`**。App は実行可能アセンブリで外部から
  使われないため、public にする理由がない(`ICrashUiHost` と揃う)。
  Editor 層の `IClipboard` が public なのは、コントロールライブラリとしての先例
  (`IImeContext`)に合わせたもので、こちらは意図的に非対称。

## 11. 申し送り(実装時・レビュー時に追記する)

- **`session-state.json` の `.tmp` 残骸は sweep 対象外**(最終レビュー L-V3)。
  `AtomicFile.Write` の temp は原本を壊さないが、`Environment.Exit` でワーカーが書込中に
  死ぬと `%APPDATA%\kxEdit\session-state.json.<rand>.tmp` が残る。
  `BackupStore.SweepTempFiles` は `backups` 配下しか見ないので親ディレクトリは回収されない。
  到達経路は「前提ゲート不通過 → `WaitForFinalFlush` を待たずに false を返す」ときだけ。実害は残骸のみ。
- **`ClipboardFailed` の購読側が投げるとプロセス終了に化ける**(最終レビュー L-V4)。
  catch 節の中から発火するので、ハンドラの例外は `Copy` / `Paste` を貫通し `CrashHandler` が受ける。
  本番の唯一の購読者 `UiaAnnouncer.Say` は内部で全例外を握るので現状は安全。
  `try/catch` で構造的に閉じる案は**採らない**: 発声経路のバグを黙って飲むほうが、
  SR ユーザーにとって危険(サイレントな発声失敗はこのプロジェクトが最も嫌う失敗)。
  XML doc に警告を置いて受容する。データ喪失には繋がらない(`Copy` が throw すると
  `Cut` は `_buffer.Replace` に到達しない=安全側)。
- **M-3 / M-21(OOM)の「受け皿」は楽観的**(最終レビュー L-V5)。`FlushBackupsForCrash` →
  `ReconcileContent` は dirty 文書ごとに `SnapshotText`(全文 string 化)を走らせるので、
  OOM 直後は二度目の OOM で失敗しうる。ただし失敗しても `CrashHandler` が拾って
  `flushed = false` の悲観文言になる=**嘘の安全宣言にはならず正しく degrade する**。
- **`Notify` のモーダル中に起きた例外は再入ガードで無音になる**(最終レビュー L-V6)。
  WinForms 既定ダイアログでも同種の問題は起きる(=退行ではない)ので受容。
- **`CrashHandler` の再入ガードは 2 本目の帰り先まで面倒を見ない**。2 本目が
  `AppDomain.UnhandledException(IsTerminating=true)` の場合、return すると CLR がそのまま
  プロセスを落とすため、先着が退避の途中(最大 5 秒)なら退避を殺して WER へ落ちる。
  `AppDomain` 経路は現状「誰も投げていない保険」(§5.3)なので受容。
- **真の同時発火を決定的に検証する網は書けない**(`Interlocked.Exchange` を素の read/write に
  変える変異はテストで殺せない)。網が無いことを認めたうえでの受容(CLAUDE.md §4-B)。


- §2.1 の「hot exit バックアップが書かれるかどうかが不定」は**原因未特定**。
  M-1 の修正で当該経路(WinForms 既定ダイアログ → `Application.ExitThread`)には
  到達しなくなるので本ブランチでは追わないが、`FormClosing` のキャンセルが
  無視される経路が他にもないかは別途の関心事。
- 監査書 §4 の A-20 の説明(「絵文字パネル Win+. …で高・低それぞれ U+FFFD 化」)は
  §2.2 のとおり**事実と違う**。監査書は策定時スナップショットなので書き換えず、
  本書 §2.2 が現在の正とする。
- 残る監査書 §4 の未対応項目: A-14 / A-15 / A-16 / A-17 / A-18 と T-3。
  実害の順は A-14(CRLF 文書の正規表現置換がサイレントに別位置を置換)
  → A-18(grep ジャンプが未保存タブでずれる)→ A-17 → A-15 / A-16。
