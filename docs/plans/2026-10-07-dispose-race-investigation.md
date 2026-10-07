# フェーズ 11: 破棄時の競合の調査 実装計画

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 窓の破棄で出る「CreateHandle() の実行中は Dispose() を呼び出せません」が製品で起きるかを、NVDA 起動中のハーネスで確かめ、結論(閉じる / 新しい設計書)まで進める。

**Architecture:** scratchpad に調査用の WinForms ハーネスを置き(コミットしない)、汎用の Form と製品の窓(モーダルのダイアログ・タブ・モードレス窓・プレビュー)を数百回開閉する。検出は 3 本立て: (1) first-chance 例外、(2) UI スレッド以外が作った WinForms の窓(`SetWinEventHook` の `EVENT_OBJECT_CREATE`)、(3) 汎用 Form の `CreateHandle` を override して、UI スレッド以外で走ったときのスタック。結果で §14.2 の結論を選ぶ。

**Tech Stack:** .NET 9 WinForms、Win32 P/Invoke(`SetWinEventHook`・`GetWindowThreadProcessId`・`GetClassName`・`GetForegroundWindow`)、NVDA(起動中)。

**Spec:** `docs/plans/2026-09-27-perf-followups-design.md` §3(共通規約)・§14.2(フェーズ 11)。経緯は `docs/plans/2026-09-24-perf-paint-cost.md`「`--paint-snapshot` の破棄時の例外」と `docs/plans/2026-08-02-large-line-wrap-perf-design.md` §9.7。

## Global Constraints

- 調査の結論は「再現しない → `GdiBench` に `CloseQuietly` を適用して閉じる」か「再現する → 共通の基底で UI スレッド以外からの `CreateHandle` をガードする設計を、新しい日付の設計書に書き、ユーザーの承認を得てから実装する」のどちらか(設計書 §14.2)。
- 「再現する」場合、本書の実装は Task 2 で止める。新しい抽象を本書・元の設計書に追記しない(§3.1)。
- L5 は不要。ただし「直す」になった場合は L5 と前倒しのコード品質レビューを足す(§3.3)。
- 変異検証は行わない(§3.3)。
- ハーネスは scratchpad に置き、リポジトリにコミットしない。手順と結果は実施記録に残す。
- NVDA は通常権限で起動している状態で測る。`nvda.exe -r` で再起動しない(昇格して起動すると発声を読めなくなる。本調査では発声は使わないが、ユーザーの環境を崩さない)。
- ハーネスは前面に出ないと NVDA が問い合わせないので、**Win+R(windows-mcp)で起動する**。スクリプトからの起動は前面を取れない。
- ログは UTF-8(BOM なし)で書く。
- 製品コード(`src/`)は本計画では変えない。

## Review Focus

- **NVDA が窓に触れていない回を「再現しない」に数える**: 前面にならなかった回は NVDA が問い合わせない。各回で前面の窓が自プロセスかを数え、前面率が 9 割未満のシナリオは結論に使わない(Task 1 の判定)。
- **NVDA 自身の注入スレッドが作る窓を誤検出する**: nvdaHelperRemote や COM(`OleMainThreadWndClass`)・IME は UI スレッド以外に窓を作る。クラス名が `WindowsForms10.` で始まるものだけを数える。
- **ハーネスのビルドが失敗して古い exe が走る**: 走らせる前に毎回ビルドの成功(`0 エラー`)を確かめ、exe の更新時刻を見る。
- **再現の判定を汎用 Form の結果で下す**: 汎用 Form の「Close の後に Dispose」は Smoke・GdiBench の形で、製品の形ではない。製品の判定は製品の窓のシナリオ(dialog・tabs・grep・preview)だけで下す。
- **GdiBench の修正で `using` を残したまま CloseQuietly を足す**: 二重の Dispose が残ると直らない。form と editor の `using` を外し、finally で 1 度だけ閉じる。

---

## Task 1: 調査(ハーネスで数百回開閉し、結論を出す)

**Files:**
- Create(scratchpad・コミットしない): `<scratchpad>/DisposeRaceHarness/DisposeRaceHarness.csproj`
- Create(scratchpad・コミットしない): `<scratchpad>/DisposeRaceHarness/Program.cs`
- Output: `<scratchpad>/DisposeRaceHarness/out/race-<日時>.log`

`<scratchpad>` はセッションの scratchpad ディレクトリ。`<repo>` はリポジトリの絶対パス(csproj には実際のパスを書く)。

**Interfaces:**
- Produces: 結論(「再現する」/「再現しない」)と、シナリオ別の集計表(Task 2 の実施記録に載せる)。

- [ ] **Step 1: csproj を作る**

scratchpad はリポジトリの外なので、`Directory.Build.props`(アナライザー・`-warnaserror`)は効かない。

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>WinExe</OutputType>
    <TargetFramework>net9.0-windows</TargetFramework>
    <UseWindowsForms>true</UseWindowsForms>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="<repo>\src\kxEdit.App\kxEdit.App.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: Program.cs を書く**

```csharp
// フェーズ 11(dispose-race-investigation)の調査用ハーネス。コミットしない。
// 使い方: DisposeRaceHarness.exe <scenario|all> <回数> <ログのパス>
//   scenario: generic-double / generic-quiet / dialog / tabs / grep / preview
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using kxEdit.App;
using kxEdit.Core.Search;
using kxEdit.Editor;

internal static class Program
{
    private const int DwellMs = 200; // 1 回ごとに表示しておく時間(NVDA が問い合わせる猶予)
    private static uint s_uiTid;
    private static StreamWriter s_log = null!;
    private static readonly ConcurrentQueue<string> s_pending = new();
    private static readonly WinEventProc s_proc = OnWinEvent; // GC で回収させない
    private static Stats s_cur = new("-");

    [STAThread]
    private static int Main(string[] args)
    {
        string which = args.Length > 0 ? args[0] : "all";
        int n = args.Length > 1 ? int.Parse(args[1]) : 300;
        string logPath = args.Length > 2 ? args[2] : "race.log";
        ApplicationConfiguration.Initialize();
        s_uiTid = GetCurrentThreadId();
        s_log = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true };
        Log($"start pid={Environment.ProcessId} uiTid={s_uiTid} which={which} n={n}");

        AppDomain.CurrentDomain.FirstChanceException += (_, e) =>
        {
            if (e.Exception is not (InvalidOperationException or Win32Exception))
                return; // ObjectDisposedException は InvalidOperationException の派生なので含まれる
            uint tid = GetCurrentThreadId();
            bool ui = tid == s_uiTid;
            s_cur.Count(ui ? $"FCE/ui/{e.Exception.GetType().Name}" : $"FCE/other/{e.Exception.GetType().Name}");
            s_pending.Enqueue(
                $"FCE tid={tid} ui={ui} {e.Exception.GetType().Name}: {e.Exception.Message}\n{Environment.StackTrace}"
            );
        };

        IntPtr hook = SetWinEventHook(
            EVENT_OBJECT_CREATE, EVENT_OBJECT_CREATE, IntPtr.Zero, s_proc,
            (uint)Environment.ProcessId, 0, WINEVENT_OUTOFCONTEXT
        );

        using var host = new Form { Text = "race host", Width = 800, Height = 600 };
        var docs = new DocumentManager(() => new EditorControl());
        host.Controls.Add(docs.TabHost);
        host.Show();
        docs.CreateNew(); // 常に 1 枚は開いておく
        Pump(500);

        string[] all = { "generic-double", "generic-quiet", "dialog", "tabs", "grep", "preview" };
        foreach (string s in which == "all" ? all : new[] { which })
        {
            int count = s == "preview" ? Math.Min(n, 50) : n; // WebView2 は重いので 50 回まで
            s_cur = new Stats(s);
            for (int i = 0; i < count; i++)
            {
                try
                {
                    RunOnce(s, host, docs);
                }
                catch (Exception e)
                {
                    s_cur.Count($"THROWN/{e.GetType().Name}");
                    Log($"[{s} #{i}] THROWN {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
                }
                Drain();
            }
            Log(s_cur.Summary());
        }

        UnhookWinEvent(hook);
        Log("DONE");
        return 0;
    }

    private static void RunOnce(string s, Form host, DocumentManager docs)
    {
        switch (s)
        {
            case "generic-double": // Smoke・GdiBench の旧形: Close の後に using で Dispose
            {
                var f = NewProbeForm();
                f.Show();
                Dwell(f);
                f.Close();
                try { f.Dispose(); }
                catch (InvalidOperationException e) { DisposeFailed(e); }
                break;
            }
            case "generic-quiet": // PaintSnapshot.CloseQuietly の形
            {
                var f = NewProbeForm();
                f.Show();
                Dwell(f);
                try
                {
                    f.Close();
                    if (!f.IsDisposed)
                        f.Dispose();
                }
                catch (InvalidOperationException e) { DisposeFailed(e); }
                break;
            }
            case "dialog": // 製品の using + ShowDialog(GoToLine と文書情報を交互に)
            {
                Form dlg = s_cur.Cycles % 2 == 0 ? new GoToLineDialog(1, 100) : new DocumentInfoDialog("probe");
                ShowModalAndDispose(dlg, host);
                break;
            }
            case "tabs": // 製品の DocumentManager.TryClose
            {
                var doc = docs.CreateNew();
                docs.Activate(doc);
                Dwell(host);
                try { docs.TryClose(doc, _ => true); }
                catch (InvalidOperationException e) { DisposeFailed(e); }
                break;
            }
            case "grep": // 製品のモードレス窓: ShowResults の後にユーザーが閉じる(Close のみ)
            {
                var w = new GrepResultsWindow(new GrepResultsCallbacks(_ => { }));
                w.Populate("probe", Path.GetTempPath(), new GrepOutcome(Array.Empty<GrepHit>(), 0, 0, Array.Empty<GrepError>(), false));
                w.ShowResults(host);
                Dwell(w);
                try { w.Close(); }
                catch (InvalidOperationException e) { DisposeFailed(e); }
                if (!w.IsDisposed)
                    s_cur.Count("grep/not-disposed-after-close");
                break;
            }
            case "preview": // 製品の using + ShowDialog(WebView2)
            {
                var f = new MarkdownPreviewForm("<p>probe</p>", Path.GetTempPath(), "probe.md", new FileReachabilityProbe());
                ShowModalAndDispose(f, host, dwellMs: 1500);
                break;
            }
            default:
                throw new ArgumentException(s);
        }
        s_cur.Cycles++;
    }

    private static void ShowModalAndDispose(Form dlg, Form owner, int dwellMs = DwellMs)
    {
        var t = new System.Windows.Forms.Timer { Interval = dwellMs };
        t.Tick += (_, _) =>
        {
            t.Stop();
            s_cur.Foreground(IsOurForeground());
            dlg.Close(); // モーダルでは非表示になるだけで、Handle は残る
        };
        dlg.Shown += (_, _) => t.Start();
        try
        {
            dlg.ShowDialog(owner);
            // 設計書 §14.2「ShowDialog が戻った時点での Handle の状態」
            s_cur.Count(dlg.IsHandleCreated ? "after-ShowDialog/handle-alive" : "after-ShowDialog/handle-gone");
            dlg.Dispose(); // using の末尾と同じ
        }
        catch (InvalidOperationException e) { DisposeFailed(e); }
        finally { t.Dispose(); }
    }

    private static ProbeForm NewProbeForm()
    {
        var f = new ProbeForm { Text = "race probe", Width = 600, Height = 400, ShowInTaskbar = false };
        f.Controls.Add(new EditorControl { Dock = DockStyle.Fill });
        return f;
    }

    private static void DisposeFailed(InvalidOperationException e)
    {
        s_cur.Count("DISPOSE-FAILED");
        Log($"[{s_cur.Name} #{s_cur.Cycles}] DISPOSE-FAILED: {e.Message}\n{e.StackTrace}");
    }

    private static void Dwell(Form f)
    {
        Pump(DwellMs);
        s_cur.Foreground(IsOurForeground());
    }

    private static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            Application.DoEvents();
            Thread.Sleep(5);
        }
    }

    private static bool IsOurForeground()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out uint pid);
        return pid == (uint)Environment.ProcessId;
    }

    // UI スレッド以外が作った WinForms の窓を数える(out-of-context なので UI スレッドに配送される)。
    private static void OnWinEvent(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint evThread, uint time)
    {
        if (idObject != 0 || hwnd == IntPtr.Zero) // OBJID_WINDOW だけ
            return;
        uint owner = GetWindowThreadProcessId(hwnd, out _);
        if (owner == s_uiTid)
            return;
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        string cls = sb.ToString();
        if (!cls.StartsWith("WindowsForms10.", StringComparison.Ordinal))
        {
            s_cur.Count("offthread-window/non-winforms");
            return;
        }
        s_cur.Count("OFFTHREAD-WINFORMS-WINDOW");
        s_pending.Enqueue($"OFFTHREAD-WINFORMS-WINDOW hwnd=0x{hwnd:X} class={cls} ownerTid={owner}");
    }

    private static void Drain()
    {
        while (s_pending.TryDequeue(out string? line))
            Log($"[{s_cur.Name} #{s_cur.Cycles}] {line}");
    }

    private static void Log(string line) => s_log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");

    /// <summary>UI スレッド以外で CreateHandle が走ったら、そのスタックを記録する(汎用 Form のみ)。</summary>
    private sealed class ProbeForm : Form
    {
        protected override void CreateHandle()
        {
            uint tid = GetCurrentThreadId();
            if (tid != s_uiTid)
            {
                s_cur.Count("OFFTHREAD-CREATEHANDLE");
                s_pending.Enqueue($"OFFTHREAD-CREATEHANDLE tid={tid}\n{Environment.StackTrace}");
            }
            base.CreateHandle();
        }
    }

    private sealed class Stats(string name)
    {
        private readonly ConcurrentDictionary<string, int> _counts = new();
        private int _fg;
        private int _fgSamples;
        public string Name { get; } = name;
        public int Cycles { get; set; }

        public void Count(string key) => _counts.AddOrUpdate(key, 1, (_, v) => v + 1);

        public void Foreground(bool ours)
        {
            Interlocked.Increment(ref _fgSamples);
            if (ours)
                Interlocked.Increment(ref _fg);
        }

        public string Summary()
        {
            var sb = new StringBuilder();
            sb.Append($"SUMMARY {Name}: cycles={Cycles} foreground={_fg}/{_fgSamples}");
            foreach (var kv in _counts.OrderBy(k => k.Key, StringComparer.Ordinal))
                sb.Append($"\n  {kv.Key} = {kv.Value}");
            return sb.ToString();
        }
    }

    private delegate void WinEventProc(IntPtr hook, uint ev, IntPtr hwnd, int idObject, int idChild, uint evThread, uint time);

    private const uint EVENT_OBJECT_CREATE = 0x8000;
    private const uint WINEVENT_OUTOFCONTEXT = 0;

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr hmod, WinEventProc proc, uint pid, uint tid, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder buf, int max);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
```

型名・引数がコンパイルで合わない場合(`GrepHit`・`GrepError` の名前空間など)は、製品の定義に合わせて直す。調査の意図(どの窓をどの形で開閉するか)は変えない。

- [ ] **Step 3: ビルドする**

Run: `dotnet build <scratchpad>/DisposeRaceHarness/DisposeRaceHarness.csproj -c Release`
Expected: `0 エラー`。`bin/Release/net9.0-windows/DisposeRaceHarness.exe` の更新時刻が今であること。

- [ ] **Step 4: 前提を確かめる**

- `tasklist /FI "IMAGENAME eq nvda.exe"` で NVDA が動いていること。
- スピーチビューアーが前面にないこと(前面だと windows-mcp の入力が消える)。

- [ ] **Step 5: 短い試走(各 10 回)**

windows-mcp で Win+R を開き、次を入力して Enter:
`<exe のフルパス> all 10 <scratchpad>\DisposeRaceHarness\out\race-dry.log`

ログの末尾に `DONE` が出るまで待つ(Monitor で `DONE` を待つ。数分)。
Expected: 全シナリオの `SUMMARY` が出る。各シナリオの foreground が 9 割以上。preview で WebView2 の初期化に失敗していない(`THROWN` がない)。
前面率が低い・preview が起動しないなどの問題は、ここで直す(滞在時間・起動方法)。

- [ ] **Step 6: 本走(各 300 回・preview は 50 回)を 2 回**

Step 5 と同じ手順で、ログ名を `race-1.log`・`race-2.log` にして `all 300` を 2 回走らせる。

- [ ] **Step 7: 集計と判定**

各ログの `SUMMARY` を表にする。判定は次のとおり。

| 判定 | 条件(製品のシナリオ dialog・tabs・grep・preview のどれか) |
|---|---|
| 再現する | `DISPOSE-FAILED` が 1 件以上、または `OFFTHREAD-WINFORMS-WINDOW` が 1 件以上 |
| 再現しない | 上のどちらも 0 件で、各シナリオの foreground が 9 割以上 |
| 判定不能 | foreground が 9 割未満のシナリオがある → 起動方法を直して Step 6 をやり直す |

あわせて、汎用 Form(generic-double・generic-quiet)の結果と、`OFFTHREAD-CREATEHANDLE` のスタック(あれば)、`FCE/other/*` の内訳、`after-ShowDialog/*` の内訳を記録する。これらは仕組みの証拠で、製品の判定には使わない。

- [ ] **Step 8: ユーザーに結論を報告する**

表と判定をユーザーに示す。「再現する」なら Task 2 へは進まず、Task 3(記録)と新しい設計書の起草に移る(ユーザーの承認を得てから)。

---

## Task 2: GdiBench の後片付けを CloseQuietly にする(「再現しない」の場合のみ)

**Files:**
- Modify: `tests/kxEdit.Editor.Smoke/GdiBench.cs:38-87`

**Interfaces:**
- Consumes: `PaintSnapshot.CloseQuietly(Form)`(`tests/kxEdit.Editor.Smoke/PaintSnapshot.cs:357`。internal static・同じアセンブリ)

Smoke のベンチなので自動テストはない。挙動の確認は実行で行う。

- [ ] **Step 1: `using` を外し、finally で 1 度だけ閉じる**

`using var form = new Form { ... };` を `var form = new Form { ... };` に、`using var editor = new EditorControl { Dock = DockStyle.Fill };` を `var editor = new EditorControl { Dock = DockStyle.Fill };` に変える(editor は form の子なので form と一緒に破棄される)。`form.Controls.Add(editor);` の次の行から `return avgMs < 16 ? 0 : 1;` までを `try { ... } finally { PaintSnapshot.CloseQuietly(form); }` で包み、末尾の `form.Close();` を消す。結果は次の形になる。

```csharp
        var form = new Form
        {
            Text = "kxEdit.Editor.Smoke --bench",
            Width = 900,
            Height = 700,
        };
        // editor は form の子なので、form の破棄で一緒に破棄される(finally の CloseQuietly)。
        var editor = new EditorControl { Dock = DockStyle.Fill };
        form.Controls.Add(editor);
        try
        {
            // (既存のコメントと Show 〜 判定の出力までをそのまま)
            ...
            Console.WriteLine($"目標: <16ms  判定: {(avgMs < 16 ? "PASS" : "FAIL")}");
            return avgMs < 16 ? 0 : 1;
        }
        finally
        {
            // Close の後に using でもう一度 Dispose すると、破棄の競合で落ちることがあった
            // (2026-08-02-large-line-wrap-perf-design.md §9.7)。PaintSnapshot と同じ後片付けにする。
            PaintSnapshot.CloseQuietly(form);
        }
```

- [ ] **Step 2: ビルドと実行**

Run: `dotnet build tests/kxEdit.Editor.Smoke -c Release`
Expected: `0 警告 0 エラー`

Run: `dotnet run --project tests/kxEdit.Editor.Smoke -c Release --no-build -- --bench --mb 16`
Expected: 「GDI 平均フレーム時間」と「判定」が出て、例外なく終わる(EXIT 0 または 1 は性能の判定で、本変更とは関係ない)。

- [ ] **Step 3: Commit**

```bash
git add tests/kxEdit.Editor.Smoke/GdiBench.cs
git commit -m "fix(smoke): GdiBench の後片付けを CloseQuietly にする(フェーズ 11)"
```

---

## Task 3: 実施記録と閉じる判定の記録

**Files:**
- Modify: `docs/plans/2026-09-27-perf-followups-design.md`(§14.2 の末尾に「実施記録」を追記。「再現しない」なら §15 の表に項目 9 の行を足す)
- Modify: 本書の末尾に「実施記録」節(Task 1 の集計表・ログの要所・判定)

- [ ] **Step 1: 本書の末尾に実施記録を書く**

条件(NVDA のバージョン・起動方法・回数)、シナリオ別の集計表(2 回分)、`OFFTHREAD-CREATEHANDLE` と `FCE/other/*` の代表スタック、`after-ShowDialog/*` の内訳、判定。

- [ ] **Step 2: 設計書 §14.2 に実施記録を追記する**

既存のフェーズの実施記録(§13.3 など)と同じ形で書く: 調査の結論・成果物・完了条件(ゲート・レビュー)・意図的な挙動差(なし。Smoke のみ)・申し送り。「再現しない」なら §15 に次の形の行を足す:
`| 9 | NVDA 起動中に製品の窓(ダイアログ・タブ・grep の結果・プレビュー)を計 N 回開閉し、Dispose の失敗も UI スレッド以外での WinForms の窓の生成も 0 件だった(実施記録 §14.2)。Smoke 側は CloseQuietly で吸収する |`

- [ ] **Step 3: Commit**

```bash
git add docs/plans/2026-09-27-perf-followups-design.md docs/plans/2026-10-07-dispose-race-investigation.md
git commit -m "docs(perf): フェーズ 11(破棄時の競合)の調査結果を記録"
```

---

## Task 4: レビュー・品質ゲート・PR

- [ ] **Step 1: 最終レビュー(1 パスに統合してよい)**

変更は Smoke 1 ファイルと docs なので、CLAUDE.md §3 の簡略化の基準に当たる。別エージェントで、コード品質と脆弱性を 1 回のレビューで見る。指摘は fixup commit で直す。

- [ ] **Step 2: 品質ゲート**

Run: `pwsh tools/pre-merge-check.ps1`
Expected: EXIT 0(「再現する」で docs のみになった場合は省略してよい)

- [ ] **Step 3: push と PR**

PR description(日本語)に、目的・判定と集計表・レビュー経緯・意図的な挙動差(なし)・L5(不要)・申し送りを書く。
