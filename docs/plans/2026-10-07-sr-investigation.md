# フェーズ 10: SR の実機調査 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 項目 6(折り返し ON で say all が論理行 2 行ぶんで止まる)と項目 7(タブ切替でフォーカス移動が発声より先に起きている疑い)を、NVDA の実機と自動テストで切り分け、それぞれ「直す / 新しい設計書 / 閉じる」の結論まで進める。

**Architecture:** 項目 7 は App.Tests で `KeyBasedSwitch` と新しいエディタの `GotFocus` の発火順を記録して判定し、実機の発声順(スピーチビューアー)で裏を取る。項目 6 は、scratchpad の git worktree に UIA プロバイダの呼び出しを全部ファイルに書く計測ビルドを作り、NVDA の say all を折り返し ON / OFF とメモ帳で比べて、止まる直前の呼び出し列から原因の側(kxEdit / NVDA)を特定する。NVDA の実機セッションは 1 回にまとめる(項目 6・7 の両方)。

**Tech Stack:** .NET 9 WinForms、xUnit(App.Tests の STA 実行)、UIA プロバイダ(`kxEdit.Accessibility`)、NVDA 2026.2jp(スピーチビューアー)、windows-mcp(キー送出)、PowerShell(Win32 の `WM_GETTEXT`・`SetForegroundWindow`)。

**Spec:** `docs/plans/2026-09-27-perf-followups-design.md` §3(共通規約)・§14.1(フェーズ 10)。項目の出典は元の設計書 `docs/plans/2026-09-24-general-perf-improvements-design.md` §11.1.1・§11.3・§11.6(項目 7)と §15.3(項目 6)、L5 の記録は `docs/plans/2026-09-26-perf-uia-wrap-cache.md` の実施記録 (5)。

## Global Constraints

- 項目 6 の結論: 原因が kxEdit 側で修正が小さければ直す。Move や Expand の意味を変えるなど大きければ、新しい日付の設計書 `docs/plans/YYYY-MM-DD-<topic>-design.md` に書き、ユーザーの承認を得てから実装する(本書・傘の設計書に新しい設計を追記しない。§3.1)。NVDA 側なら閉じる。
- Move や Expand(`TextRangeProviderV2`)を直す場合は、CLAUDE.md §4-A の変異検証の対象になる(カーソル移動・選択範囲の算出)。それ以外では変異検証を行わない。
- 項目 7 の結論: 発火順が崩れていれば、移動先の文書を先に求め、`KeyBasedSwitch` を `SelectedIndex` の変更より前に発火させる(10〜20 行とテスト。§14.1 に方針まで書いてあるので、精密化として本書に書く)。
- L5 は必須(調査そのものが実機。§3.3)。NVDA の実機操作はキーボードとマウスを占有するので、**始める前にユーザーの了承を取る**。
- NVDA は通常権限(Medium)で起動している状態で行う。`nvda.exe -r` で再起動しない。NVDA のメニュー操作(ログレベルの変更など)は自動化せず、必要ならユーザーに依頼する。
- 計測ビルドと実機用のスクリプト・試験文書は scratchpad に置き、コミットしない。手順・条件・結果は実施記録に残す。
- ユーザーの `settings.json`(`%APPDATA%\kxEdit\settings.json`)を書き換えるときは、先に退避し、終わったら戻す(RecentFiles・セッション復元のバックアップも戻す)。
- ログは UTF-8(BOM なし)で書く。
- 0 warning を維持する(`-warnaserror`)。計測ビルドは scratchpad の worktree なので `-p:TreatWarningsAsErrors=false` でビルドしてよい。

## Review Focus

- **前提の崩れた順序テストが緑になる**: 切替前に旧エディタがフォーカスを持っていなければ、TabControl はフォーカスを動かさず、どの順でも「発声 → フォーカス」に見える。テストは切替前の `Focused` を assert してから操作する(CLAUDE.md §4-B の Guard 系の教訓)。
- **計測ビルドが製品と違う動きをする**: トレースの I/O で RPC スレッドの応答が遅れ、NVDA の say all の挙動が変わりうる。計測ビルドでも製品の exe と同じ症状(折り返し ON で論理行 2 行で止まる)が出ることを先に確かめてから、トレースを読む。
- **NVDA 側の原因を kxEdit の不具合と取り違える**: メモ帳の折り返しでも同じ止まり方をするかを必ず見る。kxEdit の呼び出し列だけで結論を出さない。
- **スピーチビューアーの差分の取り違え**: 本文は先頭から切り詰められる。差分は操作前の末尾 300 字をアンカーにして `LastIndexOf` の後ろを読む(オフセットで取らない)。
- **項目 7 の修正で、取り消された切替を発声する**: `KeyBasedSwitch` を `SelectedIndex` の変更より前に出すと、切替が取り消された場合(`Deselecting` の Cancel など)にタブ名だけ読まれる。修正前に、取り消しの経路がないことを grep で確かめる。

---

## Task 1: 項目 7 の発火順を App.Tests で記録する

**Files:**
- Modify: `tests/kxEdit.App.Tests/DocumentManagerTests.cs`(`KeyBasedSwitch_SingleTab_SelectNext_DoesNotFire` の直後、`// ===== BeforeActiveChange` の前)

**Interfaces:**
- Consumes: `DocumentManager.SelectAt(int)`・`SelectNext(int)`・`KeyBasedSwitch`(`EventHandler<Document>`)・`Document.Editor`(`EditorControl`)。
- Produces: 判定「発火順が崩れている / 崩れていない」(Task 3・4 が使う)。

- [ ] **Step 1: 順序を記録するテストを書く**

`DocumentManagerTests` の `KeyBasedSwitch_SingleTab_SelectNext_DoesNotFire` の後に追加する。

```csharp
    // ===== 発声 → フォーカスの順(フェーズ 10 項目 7・I-5) =====
    // AnnounceThenFocus は「KeyBasedSwitch(タブ名の発声)→ エディタへフォーカス」の順を意図している。
    // TabControl.SelectedIndex のセッター自体が新しいタブのエディタへフォーカスを移すと、
    // この順が崩れる(2026-09-27-perf-followups-design.md §14.1)。
    // 前提: 切替前に旧タブのエディタがフォーカスを持つこと(フォーカスが TabControl の外にあれば、
    // セッターはフォーカスを動かさず、どの実装でもこの順に見える)。

    [Fact]
    public void SelectAt_FromFocusedEditor_AnnouncesBeforeNewEditorGetsFocus() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var docs = new[]
            {
                host.Docs.CreateNew(),
                host.Docs.CreateNew(),
                host.Docs.CreateNew(),
            }; // アクティブ=docs[2]
            var order = RecordSwitchAndFocusOrder(host, docs);

            host.Docs.SelectAt(0);

            Assert.Equal(new[] { "switch:0", "focus:0" }, order);
            Assert.True(docs[0].Editor.Focused);
        });

    [Fact]
    public void SelectNext_FromFocusedEditor_AnnouncesBeforeNewEditorGetsFocus() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var docs = new[]
            {
                host.Docs.CreateNew(),
                host.Docs.CreateNew(),
                host.Docs.CreateNew(),
            }; // アクティブ=docs[2]
            var order = RecordSwitchAndFocusOrder(host, docs);

            host.Docs.SelectNext(-1);

            Assert.Equal(new[] { "switch:1", "focus:1" }, order);
            Assert.True(docs[1].Editor.Focused);
        });

    /// <summary>アクティブ(末尾)のエディタにフォーカスを置いたうえで、KeyBasedSwitch と
    /// 全エディタの GotFocus を発火順に記録するリストを返す(要素は "switch:i" / "focus:i")。</summary>
    private static List<string> RecordSwitchAndFocusOrder(Host host, Document[] docs)
    {
        var active = docs[^1];
        active.Editor.Focus();
        Assert.True(active.Editor.Focused, "旧タブのエディタにフォーカスが無いと、セッターがフォーカスを動かす経路を通らない");

        var order = new List<string>();
        for (int i = 0; i < docs.Length; i++)
        {
            int index = i;
            docs[i].Editor.GotFocus += (_, _) => order.Add($"focus:{index}");
        }
        host.Docs.KeyBasedSwitch += (_, d) => order.Add($"switch:{Array.IndexOf(docs, d)}");
        return order;
    }
```

- [ ] **Step 2: テストを走らせて結果を記録する**

Run:
```powershell
dotnet test tests/kxEdit.App.Tests --filter "FullyQualifiedName~AnnouncesBeforeNewEditorGetsFocus" 2>&1 | Out-File -Encoding utf8 <scratchpad>/task1-order.log
```

結果の読み方:
- **前提の assert で落ちる**(フォーカスが乗らない): 判定不能。`host.Form.Activate()` を `active.Editor.Focus()` の前に足して再実行する。それでも乗らなければ、ユーザーに報告して止まる。
- **両方 PASS**: 発火順は崩れていない。疑いは WinForms の段では否定される。
- **`["focus:0", "switch:0"]` のように focus が先で FAIL**: 疑いどおり。セッターがフォーカスを先に移している。
- **それ以外の列**(例: 中間のエディタにも focus が来る): 列をそのまま実施記録に写し、Task 3 で扱う。

失敗メッセージ(実際の列)を `<scratchpad>/task1-order.log` から実施記録用に控える。

- [ ] **Step 3: 判定に応じて commit する**

- 両方 PASS の場合: I-5 の意図を固定する回帰テストとして commit する。
  ```bash
  git add tests/kxEdit.App.Tests/DocumentManagerTests.cs
  git commit -m "test(tabs): タブ切替で発声がフォーカスより先に来ることを固定する(フェーズ 10 項目 7)"
  ```
- FAIL の場合: commit しない(赤のテストを main 系に積まない)。テストは作業ツリーに残したまま Task 2 へ進み、Task 3 で修正と一緒に commit する。

---

## Task 2: NVDA の実機セッション(項目 6・7)

**前提**: ユーザーの了承を取ってから始める(キーボードとマウスを 30〜60 分占有する。NVDA が通常権限で起動していること、スピーチビューアーを表示していることを確かめてもらう)。

**Files(すべて scratchpad・コミットしない):**
- Create: `<scratchpad>/wt-trace/`(計測ビルド用の git worktree)
- Create: `<scratchpad>/wt-trace/src/kxEdit.Accessibility/UiaTrace.cs`(worktree 内だけ)
- Create: `<scratchpad>/l5/sv.ps1`(スピーチビューアーの読み取り)
- Create: `<scratchpad>/l5/fg.ps1`(kxEdit の前面化と配置)
- Create: `<scratchpad>/l5/sayall-wrap.txt`(試験文書)
- Output: `%TEMP%\kx-uia-trace.log`(計測ビルドのトレース)・`<scratchpad>/l5/out/*.txt`(発声)

**Interfaces:**
- Consumes: Task 1 の判定。
- Produces: 項目 6 の切り分け表(条件ごとに「どこまで読んだか」)と、止まる直前のトレース。項目 7 の実機の発声順。

- [ ] **Step 1: 計測ビルドを作る**

```bash
cd <repo>
git worktree add "<scratchpad>/wt-trace" HEAD --detach
```

worktree に `src/kxEdit.Accessibility/UiaTrace.cs` を作る。

```csharp
using System.Diagnostics;
using System.Text;

namespace kxEdit.Accessibility;

/// <summary>フェーズ 10 の調査用(コミットしない)。UIA プロバイダの呼び出しを %TEMP%\kx-uia-trace.log に書く。</summary>
public static class UiaTrace
{
    private static readonly object Gate = new();
    private static readonly string LogPath = Path.Combine(Path.GetTempPath(), "kx-uia-trace.log");
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static readonly UTF8Encoding Utf8 = new(false);

    public static void Log(string what)
    {
        string line = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{Clock.Elapsed.TotalMilliseconds:F1}\tT{Environment.CurrentManagedThreadId}\t{what}{Environment.NewLine}"
        );
        lock (Gate)
            File.AppendAllText(LogPath, line, Utf8);
    }
}
```

`TextRangeProviderV2.cs`(worktree)の各メソッドに 1 行ずつ足す。範囲は `[s,e)`、移動前の値は先に控える。

```csharp
// ExpandToEnclosingUnit: 先頭で控え、末尾(反転ガードの後)で書く
int s0 = _start, e0 = _end;
// ...既存の本体...
UiaTrace.Log($"Expand {unit} [{s0},{e0})->[{_start},{_end})");

// Move: 先頭で控え、return の直前で書く
int s0 = _start, e0 = _end;
// ...既存の本体(return を int r = ...; に変える)...
int r = count < 0 ? -moved : moved;
UiaTrace.Log($"Move {unit} {count} [{s0},{e0})->[{_start},{_end}) ret={r}");
return r;

// MoveEndpointByUnit: 同じ形
UiaTrace.Log($"MoveEndpointByUnit {endpoint} {unit} {count} [{s0},{e0})->[{_start},{_end}) ret={r}");

// MoveEndpointByRange: 末尾で
UiaTrace.Log($"MoveEndpointByRange {endpoint} {targetEndpoint} target={target} ->[{_start},{_end})");

// GetText: return の直前(本文は先頭 20 字だけ)
string t = host.GetTextRange(s, count);
UiaTrace.Log($"GetText max={maxLength} [{_start},{_end}) len={t.Length} \"{(t.Length > 20 ? t[..20] : t).ReplaceLineEndings("\\n")}\"");
return t;

// Select
public void Select()
{
    UiaTrace.Log($"Select [{_start},{_end})");
    _owner.Host.SetSelection(_start, _end);
}

// Compare / CompareEndpoints: 結果を書く
UiaTrace.Log($"Compare [{_start},{_end}) vs [{o._start},{o._end}) -> {result}");
UiaTrace.Log($"CompareEndpoints {endpoint}/{targetEndpoint} {a} vs {b} -> {a - b}");

// ScrollIntoView / GetBoundingRectangles / Clone
UiaTrace.Log($"ScrollIntoView [{_start},{_end}) top={alignToTop}");
UiaTrace.Log($"GetBoundingRectangles [{_start},{_end})");
UiaTrace.Log($"Clone [{_start},{_end})");
```

`TextProviderImplV2.cs`(worktree): `GetSelection` に `UiaTrace.Log($"GetSelection [{s},{e})");`、`GetVisibleRanges` に `UiaTrace.Log($"GetVisibleRanges [{s},{e})");`、`DocumentRange` を本体付きの getter にして `UiaTrace.Log("DocumentRange");`、`RangeFromPoint` に `UiaTrace.Log($"RangeFromPoint {pos}");`。

`src/kxEdit.Editor/UiaTextHostAdapter.cs`(worktree): `RaiseUia` の `_provider is null` の判定の直後に `kxEdit.Accessibility.UiaTrace.Log($"Raise {ev.ProgrammaticName} listening={AutomationInteropProvider.ClientsAreListening}");`。`IUiaTextHost.SetSelection` を本体付きにして、投函する UI スレッド側の処理の先頭でも書く。

```csharp
void IUiaTextHost.SetSelection(int start, int end)
{
    kxEdit.Accessibility.UiaTrace.Log($"SetSelection(post) [{start},{end})");
    TryPostToUi(() =>
    {
        kxEdit.Accessibility.UiaTrace.Log($"SetSelection(ui) [{start},{end})");
        _host.SetSelectionCharRange(start, end);
    });
}
```

`src/kxEdit.App/Speech/UiaAnnouncer.cs`(worktree)の `RaiseCore` の先頭に `kxEdit.Accessibility.UiaTrace.Log($"Announce \"{message}\"");`、`DocumentManager.cs`(worktree)の `FocusActiveEditor` を本体付きにして `kxEdit.Accessibility.UiaTrace.Log("FocusActiveEditor");`、`EditorControl.OnGotFocus` の先頭に `kxEdit.Accessibility.UiaTrace.Log("Editor.OnGotFocus");`(項目 7 用)。

ビルドする。

```powershell
dotnet build "<scratchpad>/wt-trace/src/kxEdit.App/kxEdit.App.csproj" -c Release -p:TreatWarningsAsErrors=false 2>&1 | Out-File -Encoding utf8 <scratchpad>/l5/out/build-trace.log
```

Expected: `0 エラー`(警告は数えない)。exe の更新時刻が今であることを確かめる(古い exe を走らせない)。

製品と同じ比較用に、main(本ブランチの HEAD)も Release でビルドしておく。

```powershell
dotnet build src/kxEdit.App/kxEdit.App.csproj -c Release 2>&1 | Out-File -Encoding utf8 <scratchpad>/l5/out/build-main.log
```

- [ ] **Step 2: 試験文書と実機用スクリプトを作る**

`<scratchpad>/l5/sayall-wrap.txt`(UTF-8・CRLF)。2026-09-26 の L5 と同じ形にする: 1 行が 40 桁で 2〜3 視覚行に折り返される日本語の行、空行、英語の行を混ぜた 20 行。各行の先頭に行番号の目印(`L01` 〜 `L20`)を付け、どこまで読んだかを発声から判定できるようにする。

```text
L01 吾輩は猫である。名前はまだ無い。どこで生れたかとんと見当がつかぬ。何でも薄暗いじめじめした所でニャーニャー泣いていた事だけは記憶している。
L02 The quick brown fox jumps over the lazy dog.

L04 吾輩はここで始めて人間というものを見た。しかもあとで聞くとそれは書生という人間中で一番獰悪な種族であったそうだ。
L05 Short line.
L06 この書生というのは時々我々を捕えて煮て食うという話である。しかしその当時は何という考もなかったから別段恐しいとも思わなかった。

L08 ただ彼の掌に載せられてスーと持ち上げられた時何だかフワフワした感じがあったばかりである。
L09 Pack my box with five dozen liquor jugs.
L10 掌の上で少し落ちついて書生の顔を見たのがいわゆる人間というものの見始であろう。
L11 この時妙なものだと思った感じが今でも残っている。第一毛をもって装飾されべきはずの顔がつるつるしてまるで薬缶だ。

L13 その後猫にもだいぶ逢ったがこんな片輪には一度も出会わした事がない。
L14 How vexingly quick daft zebras jump.
L15 のみならず顔の真中があまりに突起している。そうしてその穴の中から時々ぷうぷうと煙を吹く。
L16 どうも咽せぽくて実に弱った。これが人間の飲む煙草というものである事はようやくこの頃知った。

L18 この書生の掌の裏でしばらくはよい心持に坐っておったが、しばらくすると非常な速力で運転し始めた。
L19 The five boxing wizards jump quickly.
L20 書生が動くのか自分だけが動くのか分らないが無暗に眼が廻る。胸が悪くなる。
```

`<scratchpad>/l5/sv.ps1`: スピーチビューアーの本文を読む。`EnumWindows` で題名が「NVDAスピーチビューアー」の窓を探し、`EnumChildWindows` でクラス名が `RICHEDIT` で始まる子を取り、`WM_GETTEXTLENGTH` と `WM_GETTEXT`(`SendMessageTimeout`)で全文を返す。引数 `-Anchor <文字列>` を渡すと、全文の中で `LastIndexOf(Anchor)` の後ろだけを返す。`-SaveTail <パス>` で末尾 300 字をファイルに書く(次の操作の Anchor にする)。整合性レベルが High の NVDA では無言で空になるので、空が返ったら NVDA の整合性レベルを `OpenProcessToken` + `GetTokenInformation(TokenIntegrityLevel)` で確かめて止まる。

`<scratchpad>/l5/fg.ps1`: 題名に `kxEdit` を含む窓を `EnumWindows` で探し、`SetWindowPos` で x=530・y=0・幅 494・高さ 740 に置き、`SetForegroundWindow` を最大 10 回リトライする(戻り値と `GetForegroundWindow` の一致を確かめる。失敗したときだけ Alt の空打ちを 1 回挟む)。

- [ ] **Step 3: 設定を退避して折り返しを設定する**

```powershell
Copy-Item "$env:APPDATA\kxEdit\settings.json" "<scratchpad>/l5/settings.json.orig"
```

`settings.json` の `WrapColumnEnabled` を `true`、`WrapColumn` を `40` にする(JSON として読み書きする。他のキーは変えない)。折り返し OFF の条件では `WrapColumnEnabled` を `false` にする。

- [ ] **Step 4: 項目 6 の切り分け(say all)**

各条件で、次の操作列を **2 回ずつ**行う。kxEdit は Win+R(windows-mcp)から exe のフルパスで起動し(スクリプト起動は前面を取れない)、Ctrl+O で `sayall-wrap.txt` を開く。

1. `fg.ps1` で前面化 → Ctrl+Home → `sv.ps1 -SaveTail` でアンカーを控える。
2. say all(NVDA+↓。Insert+↓ を windows-mcp の Shortcut で送る)。
3. 発声が止まるまで待つ(`sv.ps1` を 2 秒おきに読み、10 秒変化がなければ止まったとみなす。上限 120 秒)。
4. `sv.ps1 -Anchor` で差分を `<scratchpad>/l5/out/<条件>-<回>.txt` に保存する。最後に読まれた行の目印(`Lnn`)と、止まった時点のキャレット位置(kxEdit のステータスバー。PrintWindow で撮る)を控える。
5. 計測ビルドの条件では、操作の前に `%TEMP%\kx-uia-trace.log` を消し、操作の後に `<scratchpad>/l5/out/<条件>-<回>-trace.log` へ移す。

条件:

| # | ビルド | 折り返し | 目的 |
|---|---|---|---|
| A | main(Release) | ON(40 桁) | 症状の再現(2026-09-26 の記録と同じか) |
| B | main(Release) | OFF | 最も効く切り分け(OFF なら最後まで読むか) |
| C | 計測ビルド | ON(40 桁) | A と同じ症状が出ることの確認と、止まる直前の呼び出し列 |
| D | 計測ビルド | OFF | B と比べる対照の呼び出し列 |
| E | メモ帳(折り返し ON・窓幅を kxEdit と同じにする) | — | NVDA 側の継続条件で止まるかの対照 |

A で症状が出ない場合は、2026-09-26 の条件(同じ試験文書の形・40 桁)を見直して 1 回だけやり直す。それでも出なければ「再現しない」として記録し、項目 6 は Task 4 で閉じる判断に回す。

C の読み方: トレースの末尾から遡り、最後の `GetText` の後に NVDA が何を呼んだか(`Move Line 1` の `ret`、`Expand Line` の範囲、`Select`・`SetSelection(ui)`・`Raise TextSelectionChanged` の順と時刻、`GetSelection` の答え)を、D の同じ位置(2 行目から 3 行目へ移るところ)の列と並べる。特に次を見る。
- `Move Line 1` が `ret=0` を返した、または範囲が進まなかった(kxEdit の応答で NVDA が文書末と判断した)。
- `Select` の後の `GetSelection` が、Select した位置と違う位置を返した(`SetSelection` は UI スレッドへの投函なので、反映前に問い合わせが来うる。折り返しの境界ではキャレットの位置の正規化で位置が変わりうる)。
- `Raise TextSelectionChanged` が say all の途中で出て、その直後に発声が止まった(NVDA がキャレットの移動をユーザー操作と見なした)。

C・D でも判断がつかない場合に限り、ユーザーに NVDA のログレベルを「デバッグ」にしてもらい(NVDA のメニュー操作。自動化しない)、C を 1 回やり直して `%TEMP%\nvda.log` の sayAll 周りを読む。終わったらログレベルを戻してもらう。

- [ ] **Step 5: 項目 7 の実機確認(タブ切替の発声順)**

main(Release)・折り返し OFF で、無題タブを 3 つ開き、それぞれに 1 行ずつ違う本文(`タブ1の本文` など)を入れる(未保存のまま。タブ名は「* 無題」になるので、Ctrl+S で `<scratchpad>/l5/tab1.txt` 〜 `tab3.txt` に保存してタブ名を区別できるようにする)。

1. Ctrl+Tab を 1 回 → 発声の差分を保存。
2. Ctrl+Shift+Tab を 1 回 → 同じ。
3. Ctrl+1 → 同じ。

各操作を 3 回ずつ行う。判定: 発声の列で、タブ名(`tab2.txt` など)がエディタの読み上げ(本文の行・「本文 文書」など)より先に読まれているか。タブ名が読まれない回があるか。計測ビルドでも 1 回ずつ行い、トレースで `Announce` と `Editor.OnGotFocus`・`Raise AutomationFocusChanged` の順を控える。

- [ ] **Step 6: 後片付け**

```powershell
Copy-Item "<scratchpad>/l5/settings.json.orig" "$env:APPDATA\kxEdit\settings.json" -Force
```

kxEdit とメモ帳を閉じる(保存しない)。試験で増えたセッション復元のバックアップ(`%APPDATA%\kxEdit\backups` の、試験中に作られたもの)を消す前に、ユーザーに一覧を見せて確認する。`git worktree remove "<scratchpad>/wt-trace" --force`。

---

## Task 3: 項目 7 の結論(直す場合はここで直す)

**Files:**
- Modify: `src/kxEdit.App/DocumentManager.cs:167-200`(`SelectNext`・`SelectAt`・`AnnounceThenFocus`)
- Test: `tests/kxEdit.App.Tests/DocumentManagerTests.cs`(Task 1 のテスト)

**Interfaces:**
- Consumes: Task 1 の判定と Task 2 Step 5 の発声順。
- Produces: 項目 7 の結論(Task 5 の実施記録)。

判定:

| Task 1 | 実機の発声 | 結論 |
|---|---|---|
| PASS | タブ名が先 | 閉じる(疑いは否定。Task 1 のテストは回帰テストとして残す) |
| PASS | タブ名が後・欠ける | 閉じない。発火順は意図どおりなので、NVDA の処理(フォーカスイベントが通知を打ち消す等)の問題。結果をユーザーに見せ、扱いを決めてもらう(本タスクでは直さない) |
| FAIL | タブ名が先 | 発火順は崩れているが、実害はない。Step 1〜4 で直す(コメントの意図とコードを一致させる)。実機で悪化しないことを Step 5 で確かめる |
| FAIL | タブ名が後・欠ける | Step 1〜4 で直す |

- [ ] **Step 1: 取り消しの経路がないことを確かめる**

```bash
grep -rn "Deselecting\|Selecting\b\|e.Cancel" src/kxEdit.App --include=*.cs
```

Expected: TabControl の `Selecting`/`Deselecting` で `Cancel = true` にする箇所がない(`Deselecting` は `BeforeActiveChange` への配線だけ)。ある場合は、発声を先に出すと取り消された切替を読むことになるので、ここで止めてユーザーに報告する。

- [ ] **Step 2: 修正する**

`DocumentManager.cs` の `SelectNext`・`SelectAt`・`AnnounceThenFocus` を次に置き換える。

```csharp
    /// <summary>タブを相対移動し、直接エディタへフォーカス。SR には KeyBasedSwitch でタブ名を読ませる(I-5)。</summary>
    public void SelectNext(int dir)
    {
        int n = _tabs.TabPages.Count;
        if (n == 0)
            return;
        int prev = _tabs.SelectedIndex;
        BeforeActiveChange?.Invoke(); // 切替前に F2 編集等を後始末（キーボード経路）
        SwitchTo(((prev + dir) % n + n) % n, prev); // 端は巡回
    }

    /// <summary>指定位置のタブを選択し、直接エディタへフォーカス。SR には KeyBasedSwitch でタブ名を読ませる(I-5)。</summary>
    public void SelectAt(int index)
    {
        if (index < 0 || index >= _tabs.TabPages.Count)
            return;
        int prev = _tabs.SelectedIndex;
        BeforeActiveChange?.Invoke(); // 切替前に F2 編集等を後始末（キーボード経路）
        SwitchTo(index, prev);
    }

    // I-5: 切替が実際に起きる時だけタブ名を能動発声し(単一タブや同一 index の no-op で冗長な発声を
    // 出さない)、それからタブを切り替えてエディタへフォーカスする。発声を SelectedIndex の変更より
    // 前に出すのは、旧タブのエディタがフォーカスを持っているとき、TabControl の SelectedIndex の
    // セッター自体が新しいタブのエディタへフォーカスを移すため(フェーズ 10 項目 7)。後に出すと
    // エディタの UIA FocusChanged がタブ名より先に SR へ届く。
    private void SwitchTo(int index, int prevIndex)
    {
        if (index != prevIndex && _tabs.TabPages[index].Tag is Document next)
            KeyBasedSwitch?.Invoke(this, next);
        _tabs.SelectedIndex = index;
        FocusActiveEditor();
    }
```

- [ ] **Step 3: テストを走らせる**

```powershell
dotnet test tests/kxEdit.App.Tests --filter "FullyQualifiedName~DocumentManagerTests" 2>&1 | Out-File -Encoding utf8 <scratchpad>/task3-test.log
```

Expected: 全件 PASS(Task 1 の 2 件と、既存の `KeyBasedSwitch_FiresWithNewDocument_OnlyWhenIndexActuallyChanges`・`KeyBasedSwitch_SingleTab_SelectNext_DoesNotFire`・`SelectAt_OutOfRange_IsNoOp`・`BeforeActiveChange` 系を含む)。

陰性対照: `SwitchTo` の発声の行を `_tabs.SelectedIndex = index;` の後ろへ一時的に移し(条件はそのまま)、Task 1 の 2 件だけが FAIL することを確かめる。ビルドの成功(`0 エラー`)を確かめてから判定する。確かめたら元に戻す(先に Step 4 の commit を済ませ、`git diff` が空であることを確かめる)。

- [ ] **Step 4: commit する**

```bash
git add src/kxEdit.App/DocumentManager.cs tests/kxEdit.App.Tests/DocumentManagerTests.cs
git commit -m "fix(tabs): タブ切替でタブ名の発声をフォーカス移動より先に出す(フェーズ 10 項目 7)"
```

- [ ] **Step 5: 実機で確かめる(L5)**

Task 2 Step 5 の操作を、修正後の Release ビルドで 3 回ずつ行い、タブ名がエディタの読み上げより先に読まれること、タブ名が欠ける回がないことを確かめる(手順・後片付けは Task 2 と同じ。設定は退避して戻す)。悪化した場合は commit を revert し、結果をユーザーに見せる。

---

## Task 4: 項目 6 の結論

**Interfaces:**
- Consumes: Task 2 Step 4 の切り分け表とトレース。
- Produces: 項目 6 の結論(Task 5 の実施記録)。

判定:

| 観測 | 結論 |
|---|---|
| A で再現しない | 閉じる(事前の挙動が現在は出ない。条件と結果を記録) |
| B(OFF)も E(メモ帳)も同じところで止まる | NVDA 側の say all の継続条件。閉じる |
| E は最後まで読み、B も最後まで読み、A・C だけ止まる。原因が `Move`/`Expand` の答え以外(例: `Select` の位置の正規化・イベントの発火)で、修正が 1 か所・数十行以内 | 直す(Step 1〜3) |
| 原因が `Move`/`Expand` の意味(視覚行と論理行の扱いなど)にある、または修正が複数の層にまたがる | 新しい日付の設計書を書いてユーザーの承認を取る(Step 4)。本書では実装しない |
| C・D と NVDA のデバッグログでも判断がつかない | 調査結果を記録して閉じる。申し送りに残す |

- [ ] **Step 1: (直す場合)原因と直し方をユーザーに示して承認を取る**

トレースの該当箇所(止まる直前の 20 行程度)と、直し方・影響範囲・テストの方針を示す。承認されたら、実施時の精密化として本書の末尾に「Task 4 の精密化」の節を足し、テスト・実装・検証のステップを本書の形(失敗するテスト → 実装 → 通るテスト → commit)で書いてから実装する。

- [ ] **Step 2: (直す場合)実装・テスト・変異検証**

Step 1 で足した節に従う。`TextRangeProviderV2` の `Move`/`Expand`、または `IUiaTextHost` の位置歩き(`LineStartOf`・`LineEnd`・`LineEndNoBreakOf`)を変える場合は、CLAUDE.md §4-A の変異検証をスポットで 1〜2 個行う(変異のたびにビルドの成功を確かめる。古い DLL で「生存」に見えることがある)。それ以外では行わない。

- [ ] **Step 3: (直す場合)実機で確かめる(L5)**

修正後の Release ビルドで、Task 2 Step 4 の A・B を 2 回ずつ行い、A が文書末(`L20`)まで読むこと、B が変わらないことを確かめる。加えて、2026-09-26 の L5 と同じ「先頭から ↓×10・↑×10」の発声が修正前と一致することを確かめる。

- [ ] **Step 4: (新しい設計書の場合)設計書を書く**

`docs/plans/2026-10-07-<topic>-design.md`(topic は原因に合わせる。例: `uia-wrap-line-unit`)に、症状・トレースの証拠・原因・直し方の選択肢と推奨・影響範囲・テストと変異検証の方針・L5 の手順を書く。ユーザーの承認を得てから commit する。実装は別のフェーズとして扱う(本ブランチでは実装しない)。

---

## Task 5: 実施記録・品質ゲート・PR

**Files:**
- Modify: `docs/plans/2026-09-27-perf-followups-design.md`(§14.1 の後に「14.4 フェーズ 10 の実施記録」を足す。§15 に閉じた項目の行を足す)
- Modify: `docs/plans/2026-10-07-sr-investigation.md`(本書の末尾に「実施記録」を足す)

- [ ] **Step 1: 本書に実施記録を書く**

本書の末尾に「## 実施記録」を足し、次を書く。
- 実施日・NVDA のバージョン・画面の条件(解像度・kxEdit の配置)。
- Task 1 の結果(PASS / FAIL と実際の列)。
- Task 2 Step 4 の表(条件 A〜E × 2 回の、最後に読まれた目印と止まったときのキャレット位置)。止まる直前のトレースの抜粋(C と D の対応する箇所)。
- Task 2 Step 5 の発声の列(各操作 3 回)。
- 判定と結論、本書からの精密化・逸脱、限界(確かめていないこと)。

- [ ] **Step 2: 傘の設計書に実施記録を書く**

`2026-09-27-perf-followups-design.md` の §14.2 の前(§14.1 の直後)ではなく、§14.3 の後に「### 14.4 フェーズ 10 の実施記録(2026-10-07)」を足す(§14.3 と同じ形: 調査の結論・成果物・完了条件・本節からの精密化・意図的な挙動差・申し送り)。閉じた項目は §15 の表に理由付きで足す。項目 7 を直した場合、意図的な挙動差として「10: タブ切替(Ctrl+Tab・Ctrl+番号)で、タブ名の発声をタブの切替より前に出す」を足す。

- [ ] **Step 3: commit する**

```bash
git add docs/plans/2026-10-07-sr-investigation.md docs/plans/2026-09-27-perf-followups-design.md
git commit -m "docs(perf): フェーズ 10(SR の実機調査)の実施記録"
```

- [ ] **Step 4: 最終レビュー**

CLAUDE.md §3 の 5 に従う。コードの変更がある場合はコード品質パスと脆弱性パスを別々のエージェントで行う(数十行・単一ファイルの小変更なら 1 回に統合してよい)。docs だけの場合も別エージェントのレビューを 1 回行う。指摘は fixup commit で直すか、PR に記載して受容するか、理由付きで却下する。

- [ ] **Step 5: 品質ゲート**

コードの変更がある場合:

```powershell
pwsh tools/pre-merge-check.ps1 2>&1 | Out-File -Encoding utf8 <scratchpad>/pre-merge.log
```

Expected: EXIT 0。SR の経路(`kxEdit.Accessibility`・`EditorControl` の UIA 部・App の Speech 系・タブ切替の発声)に触れた場合は `pwsh tools/sr-regression.ps1` も EXIT 0 を確かめる。docs だけの場合は CLAUDE.md §6 の例外で省略してよい。

- [ ] **Step 6: PR を作る**

push して PR を作る。description(日本語)に、目的・調査の結論(項目 6・7)・意図的な挙動差・L5 の結果・レビューの経緯・申し送りを書く。

---

## Self-Review の結果

- §14.1 の 6 の 3 つの切り分け(折り返し OFF・メモ帳・debug ログと呼び出し列のトレース)は Task 2 Step 4 の B・E・C/D と、判断がつかない場合の NVDA ログ。結論の 3 分岐は Task 4 の表。Move/Expand を直す場合の変異検証は Task 4 Step 2。
- §14.1 の 7(スピーチビューアーで発声順・App.Tests で発火順・崩れていれば先に発火)は Task 2 Step 5・Task 1・Task 3。
- L5 必須(§3.3)は Task 2 と Task 3・4 の Step 5・3。

---

## 実施記録

### 条件

- 実施日: 2026-10-07(Task 2 の実機セッションと Task 3 の L5 は別セッション)。
- NVDA 2026.2jp(通常権限)。スピーチビューアーの本文を `WM_GETTEXT` で読んだ。
- 画面 1024×767。kxEdit は x=530・y=0・幅 494・高さ 740 に置いた。
- 起動は Win+R から exe のフルパス。キーは windows-mcp で送った(全文読みだけは下記のとおりユーザーの実打鍵)。

### Task 1: 項目 7 の発火順

両方 FAIL。疑いどおり、`SelectedIndex` のセッターが新しいエディタへフォーカスを先に移していた。

| テスト | 期待 | 実際 |
|---|---|---|
| `SelectAt_FromFocusedEditor_AnnouncesBeforeNewEditorGetsFocus` | `["switch:0", "focus:0"]` | `["focus:0", "switch:0"]` |
| `SelectNext_FromFocusedEditor_AnnouncesBeforeNewEditorGetsFocus` | `["switch:1", "focus:1"]` | `["focus:1", "switch:1"]` |

前提の assert(切替前に旧タブのエディタが `Focused`)は通った。計画どおり commit せずに Task 3 へ進んだ。

### Task 2 Step 4: 項目 6 の切り分け(say all)

| # | ビルド | 折り返し | キー | 結果 |
|---|---|---|---|---|
| A ×2 | main(Release) | ON(40 桁) | 送出 | 1 視覚行(「で生れたかとんと見当がつかぬ。何でも薄暗」)だけ |
| B ×2 | main(Release) | OFF | 送出 | `L02` の 1 行だけ |
| D ×1 | 計測ビルド | OFF | 送出 | `L02` の 1 行だけ |
| C ×1 | 計測ビルド | ON(40 桁) | **実打鍵** | `L01` から `L20`(文書末)まで読んだ |
| D ×1 | 計測ビルド | OFF | **実打鍵** | `L01` から `L20`(文書末)まで読んだ |

- 送出したキー(Insert+↓)では、NVDA は say all を始めていなかった。D(送出)のトレースは 142 行で、`GetText` 4 回・`SetSelection(ui)` 0 回。実打鍵の D は 1,273 行で `GetText` 37 回・`SetSelection(ui)` 40 回、C は 1,705 行で `GetText` 50 回・`SetSelection(ui)` 90 回。A・B の「止まった」は症状ではなく、say all が始まっていなかっただけと判断した。
- 実打鍵の C は、最後の `GetText` が `[783,803)`(文書末)まで進んだ。折り返し ON でも論理行 2 行で止まる症状は出なかった。
- E(メモ帳)と、main(Release)での実打鍵は行っていない。

### Task 2 Step 5: 項目 7 の実機の発声順(修正前・main の Release)

| 操作 | 発声の列 |
|---|---|
| Ctrl+Tab | 「本文 ドキュメント ブランク」→「無題 1」 |
| Ctrl+Shift+Tab | 「本文 ドキュメント ブランク」→「無題 1」 |
| Ctrl+3 | 「本文 ドキュメント タブ1の本文です。」→「tab1.txt」 |
| Ctrl+Tab | 「本文 ドキュメント L02 The quick brown fox …」→「sayall-wrap.txt」 |

4 回とも、タブ名がエディタの読み上げより後に読まれた。タブ名が欠けた回はない。

### Task 3: 項目 7 の修正と L5

- Step 1: タブまわりに切替を取り消す経路はない(`Deselecting` は `BeforeActiveChange` への配線だけ。`e.Cancel = true` はフォームとダイアログの `FormClosing` だけ)。
- Step 1 の追加確認: 発声(`UiaAnnouncer.Say`)は、前の発声から 50 ms 以上空いていれば UI スレッドで同期的に `RaiseAutomationNotification` を呼ぶ。発火順を入れ替えれば、SR に届く順も入れ替わる。購読側(`MainForm`)は渡された文書の `TabLabel` だけを使い、`Active` を見ない。
- Step 2〜4: 計画のコードどおりに `SwitchTo` を入れて commit した。`DocumentManagerTests` は 39 件すべて PASS。陰性対照(発声を `SelectedIndex` の代入の後ろへ移す)では、ビルド成功のうえで Task 1 の 2 件だけが FAIL した。元に戻して `git diff` が空であることを確かめた。
- Step 5(L5): 修正後の Release ビルドで、タブを 4 つ(無題 1・tab1〜tab3.txt)開いて行った。

| 操作 | 回数 | 発声の列 |
|---|---|---|
| Ctrl+Tab | 3 | 「無題 1 / tab1.txt / tab1.txt」→ 本文 |
| Ctrl+Shift+Tab | 3 | 「tab3.txt / 無題 1 / 無題 1」→ 本文 |
| Ctrl+1 | 3 | 「無題 1」→ 本文 |
| Ctrl+4・Ctrl+3(Ctrl+1 の前の移動) | 各 1 | 「tab3.txt」「tab2.txt」→ 本文 |
| Ctrl+1(すでに先頭のタブ。切替なし) | 1 | 発声なし |

11 回の切替すべてで、タブ名がエディタの読み上げ(「本文 ドキュメント …」)より先に読まれた。欠けた回はない。切替のない Ctrl+1 では、これまでどおりタブ名を読まない。

### 判定と結論

- **項目 7**: Task 1 が FAIL・実機でもタブ名が後(Task 3 の表の 4 行目)。**直した**。
- **項目 6**: 実打鍵では、折り返し ON・OFF とも文書末まで読んだ。**閉じる**(ユーザーの決定。Task 4 の表の 1 行目「再現しない」)。

### 本書からの精密化・逸脱

- Task 2 Step 4: 送出したキーでは NVDA が say all を始めないため、計測ビルドの C・D はユーザーに実打鍵してもらった。A・B を実打鍵でやり直すことと、E(メモ帳)は行っていない。ユーザーが「再現しない」として閉じることを決めた。
- Task 2 Step 5: 修正前の操作は計画の「各 3 回」ではなく合計 4 回だった。4 回とも同じ向き(タブ名が後)で、判定には足りると判断した。
- Task 3 Step 5: タブは 3 つではなく 4 つ(起動時の「無題 1」が残った)。Ctrl+1 を切替のある操作にするため、間に Ctrl+4・Ctrl+3 を挟んだ。これも番号切替として記録した。切替のない Ctrl+1 の確認を足した。

### 限界

- 項目 6: main(Release)の実打鍵と、メモ帳での対照は行っていない。計測ビルドはトレースの I/O で RPC スレッドの応答が遅れるので、製品の exe と全く同じ条件とは言えない。2026-09-26 の L5 で出た症状が、なぜ今回出なかったか(NVDA の版・試験文書・操作の違いなど)は調べていない。
- 項目 7: マウスでのタブ切替は対象外(`SelectNext`・`SelectAt` を通らず、`KeyBasedSwitch` も発火しない)。
