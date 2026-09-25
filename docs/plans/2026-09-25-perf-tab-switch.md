# フェーズ 6: タブ切替とバックアップ(perf-tab-switch) 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development(または executing-plans)で、タスク単位に実装する。

**Goal:** タブ切替の固定費(約 97 ms)の原因を ETW で特定し、コードから明らかな冗長処理(P-6 のバックアップ照合の全文化・パスのないタブへの外部変更チェックの投函)をなくす。

**設計書:** `docs/plans/2026-09-24-general-perf-improvements-design.md` §3・§11(以下「設計書」)
**調査記録:** `docs/plans/2026-09-24-general-perf-audit.md` P-6・P-25・M-6(以下「調査記録」)
**前フェーズの計画:** `docs/plans/2026-09-25-perf-search.md`(実施記録は設計書 §10.5 に同梱済み。本フェーズで転記するものはない)

**Architecture:**
- **調査(§11.1)**: WPR の CPU プロファイル(サンプリング+コンテキストスイッチ+スタック)を、harness M-6 の実行中に採る。ETL は scratchpad の使い捨て解析器(TraceEvent)で、スレッド別・モジュール別・関数別(包含)に集計する。結論は設計書 §11 の末尾に「調査結果」として追記する。
- **P-6(§11.2)**: `BackupCoordinator.DocBackup` に、最後に署名を計算したスナップショットへの弱参照 `LastSnapshot` を持たせる。modified かつ参照が同じ かつ `!ForceWrite` なら、全文化もハッシュも省く(従来も必ず `BackupAction.None` になる条件)。
- **外部変更チェック(§11.2)**: `MainForm` の `ActiveDocumentChanged` ハンドラで、パスのないタブでは `BeginInvoke` を投げない。
- **§11.3(調査結果しだい)**: 調査の後にユーザーと採否を決め、本計画の Task 5 を精密化してから実装する。

**Tech Stack:** C#(.NET 9)、xUnit、WinForms、tools/perf-harness.ps1、PowerShell 7、WPR(`C:\WINDOWS\system32\wpr.exe`・要管理者)、Microsoft.Diagnostics.Tracing.TraceEvent(scratchpad の解析器のみ。リポジトリには入れない)。

---

## 0. 前提と決定事項

### 0.1 計測の条件(設計書 §3.2・§5.5・§11.5)

- **体感値の正は perf-harness M-6**(publish・各 3 回)。Smoke `--perf` にタブ切替のシナリオはないので使わない。
  - M-6 の 3 条件(空の新規タブ 4 枚 / 3 ファイル+空 / 3 ファイル未保存+空)を、セッション復元 OFF(既定)で測る。
  - Task 1 で harness に `-SessionRestore` を足し、ON でも同じ 3 条件を測る(設計書 §11.1 の「セッション復元の設定 ON / OFF」)。
- **NVDA は起動したまま測る**(ユーザー判断 2026-09-25。設計書 §5.5 の「NVDA なしで採り直す」はフェーズ 6 でも行わない)。harness の CSV の `env` 行で NVDA の有無を確かめ、変更前後で揃える。
- **harness の実行の直前にユーザーへ声をかける**(1 回あたり数分、キーボードとマウスを占有し、`%APPDATA%\kxEdit` を退避・復元する)。
- 画面のロック中・他のエージェントの重い処理と並走しているときは測らない。
- 判断基準は設計書 §3.2: 変更後の中央値が、変更前の 3 回の最小〜最大を超えて改善していること。揺れの範囲なら PR に書き、採否をユーザーに判断してもらう。

### 0.2 設計書からの精密化・逸脱

- **§11.1 の 4 条件のうち 2 つは、切り替えずに ETW の帰属で代える**(逸脱)。
  - **NVDA あり / なし**: 上の 0.1 のとおり NVDA は止めない。代わりに、ETW で NVDA のプロセスの CPU と、kxEdit の UI スレッドのうち UIA のサーバー側(`UIAutomationCore` / `WM_GETOBJECT` 由来のスタック)に入った時間を数え、NVDA が起こす費用の上限を見積もる。
  - **windows-mcp 常駐あり / なし**: windows-mcp は Claude Code のセッションが起動した MCP サーバーで、止めるにはセッションを終える必要がある。harness は windows-mcp を使わない(pwsh から SendInput)。ETW でそのプロセス(python / uv 等)の CPU を数えて寄与を見積もる。
  - どちらも、帰属で「kxEdit の外の費用」と言い切れない量が残った場合に限り、ユーザーに切り替えての採り直しを相談する。
- **空タブ / 実ファイル**は harness M-6 の既存 3 条件で代える(空の新規タブ 4 枚 と 3 ファイル+空)。
- **ETW の採取は管理者権限が要る**(セッションのシェルは非昇格)。scratchpad の `etw-capture.ps1` を `Start-Process -Verb RunAs` で起動し、ユーザーが UAC を 1 回承認する。harness 自体は非昇格のまま動かす(昇格すると kxEdit も昇格で起動し、UIPI の条件が変わるため)。
- **P-6 の弱参照の更新点は設計書どおり 3 箇所**(RegisterNew・AdoptRestored・Write 分岐)。None 分岐(参照は変わったが内容が同じ)では更新しない。更新しても不変条件は保てるが、起きるのは「最後に書いた内容へ戻した」場合だけで、利得がない(YAGNI)。
- **P-6 の全文化は `doc.Editor.SnapshotText` ではなく、取得したスナップショットから直接行う**(設計書「全文化とハッシュの対象と、覚える参照は、同じ snapshot から取る」)。`SnapshotText` の実装(`_buffer?.Current.GetText(0, CharLength) ?? ""`)と、`CurrentBuffer`(未設定時は空の静的バッファ)の `Current.GetText(0, CharLength)` は同じ文字列を返す。
- **外部変更チェックの条件は `doc.State.Path is null` だけにする**(設計書どおり)。`LastKnownWriteTimeUtc is null` でも `CheckExternalChange` は Skipped を返すが、投函から実行までの間に保存で値が入ることがあるので、条件を増やさない。
  - パスのないタブで投函しない場合との差: 投函から実行までの間に「名前を付けて保存」でパスが入ると、従来は実行時にチェックが走っていた。ただしその時点の `LastKnownWriteTimeUtc` は保存直後の値なので `NoChange` で終わる。結果は同じ。

### 0.3 レビューとミューテーション検証

- **前倒しの脆弱性レビュー**: Task 1(harness がプロフィールに `settings.json` を書く=利用者データの退避・復元の経路に触れる。CLAUDE.md §3「パス操作」)。
- 前倒しのコード品質レビュー: 該当なし(新しい共通 seam を入れない。`DocBackup` の内部状態とテスト観測用のカウンタだけ)。
- **ミューテーション検証: 行わない**。P-6 は設計書 §3.4 の列挙にもその拡張の提案にも入っていない(バックアップは CLAUDE.md §4-A の列挙の外)。等価性は Task 3 のテスト(省略する条件・省略しない 3 条件・回収後)で押さえる。

### 0.4 L5

- Task 3・4 は SR の経路に触れない(バックアップの内部状態と、`BeginInvoke` を投げるかどうかだけ)。**不要**。
- Task 5(§11.3)でフォーカス移動や発声の順序に触れる場合は、そのタスクの精密化で L5(NVDA でのタブ切替時の読み上げ)を加える(設計書 §11.5)。

### 0.5 意図的な挙動差

- Task 3・4: なし(挙動不変)。
- Task 5 で「レイアウト書込のまとめ」を採る場合のみ、設計書 §3.5 のフェーズ 6 の行(セッション復元 ON のとき、タブ切替だけによるレイアウトの即時書込をまとめる)。

---

## Task 0: 本計画を commit する(docs のみ)

```powershell
git add docs/plans/2026-09-25-perf-tab-switch.md
git commit -m "docs(perf): フェーズ 6(タブ切替とバックアップ)の実装計画"
```

---

## Task 1: harness に `-SessionRestore` を足す(tools のみ・前倒しの脆弱性レビュー)

**Files:**
- Modify: `tools/perf-harness.ps1`(コメントヘルプ・`param`・`Start-KxEdit`・`env` 行・`-SelfTest`)
- Modify: `tools/README.md`(§3 の harness の引数の説明)

**Interfaces:**
- Produces: `pwsh -File tools/perf-harness.ps1 -PublishDir <dir> -Scenario M-6 -SessionRestore` で、各起動の直前に空にしたプロフィールへ `settings.json` = `{"RestoreOpenFilesOnStartup":true}` を置く。CSV に `env` 行 `session_restore`(0/1)を出す。

**Step 1: コメントヘルプに引数を足す**(`.PARAMETER WorkDir` の後)

```powershell
.PARAMETER SessionRestore
    計測用の空のプロフィールに settings.json({"RestoreOpenFilesOnStartup":true})を置いてから起動する
    (「起動時に前回開いていたファイルを開く」= ON。性能改善フェーズ 6 の条件)。既定は OFF(既定設定のまま)。
    書くのは計測中の空のプロフィールだけで、利用者の退避には触れない。
```

**Step 2: `param` に足す**(`$WorkDir` の後)

```powershell
    [Parameter(ParameterSetName = 'Run')]
    [switch]$SessionRestore,
```

**Step 3: 設定を書く関数を足し、`Start-KxEdit` から呼ぶ**

`Clear-ProfileForRun` の定義の直後に置く。パスを引数に取る(-SelfTest が一時フォルダーで同じコードを通すため。既存の関数と同じ規約)。

```powershell
# 空にした直後のプロフィールに、計測条件の settings.json を置く(-SessionRestore)。
# Clear-ProfileForRun の後にだけ呼ぶ(目印が cleared であること=利用者のデータは退避済みで、今の中身は計測の産物だけ)。
function Write-RunSettings([string]$ProfileDir, [string]$Root, [bool]$SessionRestore) {
    if (-not $SessionRestore) { return }
    $marker = Read-Marker $Root
    if ($null -eq $marker -or $marker.State -ne 'cleared') {
        throw '計測用の設定を書けません(プロフィールを空にしていない)。利用者のプロフィールに触れずに中止します。'
    }
    if (-not (Test-Path -LiteralPath $ProfileDir)) { [void](New-Item -ItemType Directory -Path $ProfileDir) }
    Assert-NoReparsePoints $ProfileDir
    Set-Content -LiteralPath (Join-Path $ProfileDir 'settings.json') -Value '{"RestoreOpenFilesOnStartup":true}' -Encoding utf8
}
```

`Start-KxEdit` の `Clear-ProfileForRun $script:ProfileDir $script:HarnessRoot` の次の行に:

```powershell
    Write-RunSettings $script:ProfileDir $script:HarnessRoot $SessionRestore.IsPresent
```

**Step 4: `env` 行を足す**(`Add-Result 'env' 'NVDA起動中' ...` の次の行)

```powershell
Add-Result 'env' 'session_restore' '-' 1 ([int]$SessionRestore.IsPresent) 'bool'
```

**Step 5: -SelfTest に検査を足す**

既存の -SelfTest のブロック(`Clear-ProfileForRun` を一時フォルダーで検査している付近)を読み、同じ `Check` / `Throws` の書き方で次の 3 件を足す。

```powershell
        # Write-RunSettings: cleared の後だけ書く。stashed(まだ空にしていない)では投げて、何も書かない。
        # (前準備: 偽プロフィール $prof と作業ルート $root で Save-ProfileStash を済ませた状態から)
        Check ((Throws { Write-RunSettings $prof $root $true }) -and -not ((Get-Content -LiteralPath (Join-Path $prof 'settings.json') -Raw -ErrorAction SilentlyContinue) -match 'RestoreOpenFilesOnStartup')) '空にする前は計測用の設定を書かない'
        Clear-ProfileForRun $prof $root
        Write-RunSettings $prof $root $true
        Check ((Get-Content -LiteralPath (Join-Path $prof 'settings.json') -Raw).Trim() -eq '{"RestoreOpenFilesOnStartup":true}') '空にした後は計測用の設定を書く'
        Write-RunSettings $prof $root $false
        Check ((Get-Content -LiteralPath (Join-Path $prof 'settings.json') -Raw).Trim() -eq '{"RestoreOpenFilesOnStartup":true}') 'OFF のときは何もしない'
```

実装者への注意: 既存の -SelfTest のシナリオの並び(`Save-ProfileStash` → `Clear-ProfileForRun` → `Restore-ProfileStash`)を読み、上の 3 件を「退避の後・空にする前」から「空にした後」へまたがる位置に挿入する。挿入後、そのシナリオの最後の復元の照合(元のプロフィールに戻ること)が PASS し続けることを確かめる(計測用の settings.json は復元で消えるべきもの)。

**Step 6: README に書く**

`tools/README.md` §3 の harness の引数の一覧に、次の 1 項目を足す(既存の書式に合わせる)。

```markdown
- `-SessionRestore`: 「起動時に前回開いていたファイルを開く」を ON にした空のプロフィールで測る(既定は OFF)。CSV の `env` 行 `session_restore` に記録する。
```

**Step 7: 検証**

```powershell
pwsh -NoProfile -File tools/perf-harness.ps1 -SelfTest
```
Expected: 全件 PASS・EXIT 0。

`-SessionRestore` を付けた実行は Task 2 で行う(実行にはユーザーの了承が要る)。

**Step 8: commit**(ps1 は BOM 付き UTF-8 のまま保存されていることを確かめる)

```powershell
git add tools/perf-harness.ps1 tools/README.md
git commit -m "feat(tools): perf-harness に -SessionRestore を追加(フェーズ 6 の計測条件)"
```

**Step 9: 前倒しの脆弱性レビュー**(別エージェント)。観点: 利用者のプロフィールへ書かないこと(cleared の検査の抜け・レース)、再解析ポイント、復元の照合が計測用の設定で崩れないこと、BOM。

---

## Task 2: 変更前を測る(src は未変更)

**Step 1: 変更前の publish を作る**(scratchpad。ブランチの HEAD は Task 1 まで=src は main と同一)

```powershell
$pub = "$env:TEMP\claude\...\scratchpad\pub-before"   # 実際の scratchpad のパスを使う
dotnet publish src/kxEdit.App -c Release -r win-x64 --self-contained false -o $pub
```

**Step 2: ユーザーに声をかけてから、M-6 を 6 回走らせる**(OFF × 3・ON × 3)

```powershell
foreach ($i in 1..3) {
  pwsh -NoProfile -File tools/perf-harness.ps1 -PublishDir $pub -Scenario M-6 -OutCsv "$scratch\m6-before-off-$i.csv"
  pwsh -NoProfile -File tools/perf-harness.ps1 -PublishDir $pub -Scenario M-6 -SessionRestore -OutCsv "$scratch\m6-before-on-$i.csv"
}
```
Expected: 各 EXIT 0。`env` 行で NVDA=1、`session_restore` が 0 / 1 であること。

**Step 3: 集計して、実施記録の下書きに残す**

条件(3 条件 × OFF/ON)ごとに、プロセス全体・UI スレッド(合計/ユーザー)・その他で最大のスレッドの、3 回の最小・中央値・最大を表にする。本計画の末尾に「実施記録」節を作り、そこに書く(commit は Task 6 でまとめる)。

---

## Task 3: ETW で固定費の内訳を採り、調査結果を設計書に書く(§11.1)

**Files:**
- Create(scratchpad・commit しない): `etw-capture.ps1`、`EtwTabSwitch/`(解析器)
- Modify: `docs/plans/2026-09-24-general-perf-improvements-design.md`(§11.1 の末尾に「調査結果」を追記。§11.1 の「成果物」が求める追記で、CLAUDE.md §8 の許す範囲)

**Step 1: 採取スクリプト**(scratchpad の `etw-capture.ps1`。BOM 付き UTF-8。昇格して実行される)

```powershell
param([Parameter(Mandatory)][string]$Etl, [Parameter(Mandatory)][string]$StopFlag)
$ErrorActionPreference = 'Stop'
wpr -cancel 2>$null | Out-Null   # 前回の取り残しを捨てる(無ければ失敗するので無視)
wpr -start CPU -filemode
try {
    while (-not (Test-Path -LiteralPath $StopFlag)) { Start-Sleep -Milliseconds 200 }
}
finally {
    wpr -stop $Etl
    Remove-Item -LiteralPath $StopFlag -ErrorAction SilentlyContinue
}
```

**Step 2: 採取**(条件ごとに 1 回。ユーザーに UAC の承認と、harness の占有を伝えてから)

```powershell
$etl = "$scratch\m6-off.etl"; $flag = "$scratch\etw-stop.flag"
Start-Process pwsh -Verb RunAs -ArgumentList '-NoProfile','-File',"$scratch\etw-capture.ps1",'-Etl',$etl,'-StopFlag',$flag
# wpr が開始したことを確かめる(ユーザーが UAC を承認するまで待つ)
pwsh -NoProfile -File tools/perf-harness.ps1 -PublishDir $pub -Scenario M-6 -OutCsv "$scratch\m6-etw-off.csv"
New-Item -ItemType File $flag | Out-Null
# $etl ができるまで待つ
```
同じ手順で `-SessionRestore` 付き(`m6-on.etl`)も採る。

**Step 3: 解析器を作る**(scratchpad。リポジトリには入れない)

```powershell
dotnet new console -n EtwTabSwitch -o "$scratch\EtwTabSwitch" --framework net9.0
dotnet add "$scratch\EtwTabSwitch" package Microsoft.Diagnostics.Tracing.TraceEvent
```

`Program.cs`:

```csharp
using Microsoft.Diagnostics.Symbols;
using Microsoft.Diagnostics.Tracing.Etlx;
using Microsoft.Diagnostics.Tracing.Parsers.Kernel;

// 使い方: EtwTabSwitch <etl> [windowSec=10] [tailSec=0.5]
// kxEdit のプロセスごとに、終了の tailSec 秒前から windowSec 秒間(harness M-6 の Ctrl+Tab × 40 の区間)を集計する。
// 窓が正しいかは、先に出す 0.5 秒刻みの CPU の推移(4 Hz の周期が見えること)で確かめる。
string etl = args[0];
double windowSec = args.Length > 1 ? double.Parse(args[1]) : 10.0;
double tailSec = args.Length > 2 ? double.Parse(args[2]) : 0.5;
using var log = TraceLog.OpenOrConvert(etl);
string symPath = Environment.GetEnvironmentVariable("_NT_SYMBOL_PATH")
    ?? $@"SRV*{Path.Combine(Path.GetTempPath(), "sym")}*https://msdl.microsoft.com/download/symbols";
using var reader = new SymbolReader(TextWriter.Null, symPath);

foreach (var p in log.Processes.Where(p => p.Name.Equals("kxEdit", StringComparison.OrdinalIgnoreCase)))
{
    double end = p.EndTimeRelativeMsec - tailSec * 1000, start = end - windowSec * 1000;
    Console.WriteLine($"=== kxEdit pid={p.ProcessID} 窓 {start:F0}〜{end:F0} ms ===");
    foreach (var m in p.LoadedModules)
        try { log.CodeAddresses.LookupSymbolsForModule(reader, m.ModuleFile); } catch { }

    var hist = new SortedDictionary<int, int>();
    var perProc = new Dictionary<string, int>();
    var perThread = new Dictionary<int, int>();
    var incl = new Dictionary<(int tid, string frame), int>();
    var leafMod = new Dictionary<(int tid, string mod), int>();
    foreach (var ev in log.Events)
    {
        if (ev is not SampledProfileTraceData s) continue;
        double t = s.TimeStampRelativeMSec;
        if (s.ProcessID == p.ProcessID) hist[(int)(t / 500)] = hist.GetValueOrDefault((int)(t / 500)) + 1;
        if (t < start || t > end) continue;
        string pn = s.Process()?.Name ?? "?";
        perProc[pn] = perProc.GetValueOrDefault(pn) + 1;
        if (s.ProcessID != p.ProcessID) continue;
        perThread[s.ThreadID] = perThread.GetValueOrDefault(s.ThreadID) + 1;
        var seen = new HashSet<string>();
        bool leaf = true;
        for (var cs = s.CallStack(); cs is not null; cs = cs.Caller)
        {
            var ca = cs.CodeAddress;
            string mod = ca.ModuleName ?? "?";
            string frame = $"{mod}!{ca.FullMethodName}";
            if (leaf) { leafMod[(s.ThreadID, mod)] = leafMod.GetValueOrDefault((s.ThreadID, mod)) + 1; leaf = false; }
            if (seen.Add(frame)) incl[(s.ThreadID, frame)] = incl.GetValueOrDefault((s.ThreadID, frame)) + 1;
        }
    }
    Console.WriteLine("-- 0.5 秒刻みの kxEdit のサンプル数(末尾 30 区間) --");
    foreach (var kv in hist.TakeLast(30)) Console.WriteLine($"{kv.Key * 0.5,8:F1}s {kv.Value}");
    Console.WriteLine("-- 窓の中のプロセス別サンプル数(1 サンプル ≒ 1 ms。上位 15) --");
    foreach (var kv in perProc.OrderByDescending(k => k.Value).Take(15)) Console.WriteLine($"{kv.Value,7} {kv.Key}");
    foreach (var th in perThread.OrderByDescending(k => k.Value).Take(4))
    {
        Console.WriteLine($"-- スレッド {th.Key}: {th.Value} サンプル(÷40 で ms/回) --");
        Console.WriteLine("   葉のモジュール(上位 10):");
        foreach (var kv in leafMod.Where(k => k.Key.tid == th.Key).OrderByDescending(k => k.Value).Take(10))
            Console.WriteLine($"   {kv.Value,7} {kv.Key.mod}");
        Console.WriteLine("   包含の関数(上位 40):");
        foreach (var kv in incl.Where(k => k.Key.tid == th.Key).OrderByDescending(k => k.Value).Take(40))
            Console.WriteLine($"   {kv.Value,7} {kv.Key.frame}");
    }
}
```

実装者への注意:
- `CPU` プロファイルの既定のサンプリング間隔は 1 ms。サンプル数 ÷ 40(Ctrl+Tab の回数)≒ ms/回。harness の CPU 時間と桁が合うことを確かめる。
- 管理コードのフレームは、TraceEvent が JIT の rundown から名前を引く。名前が `?` ばかりなら、`wpr -start CPU` に .NET の rundown が含まれていない。その場合は `wpr -start CPU -start DotNET` で採り直す。
- M-6 は 1 条件ごとに kxEdit を起動し直すので、1 本の ETL に kxEdit のプロセスが 3 つ入る(空 4 枚 → 3 ファイル → 3 ファイル未保存の順)。

**Step 4: 解析して、仮説を順に判定する**(設計書 §11.1 の仮説 1〜4)

```powershell
dotnet run --project "$scratch\EtwTabSwitch" -c Release -- "$scratch\m6-off.etl" | Out-File -Encoding utf8 "$scratch\m6-off.txt"
dotnet run --project "$scratch\EtwTabSwitch" -c Release -- "$scratch\m6-on.etl"  | Out-File -Encoding utf8 "$scratch\m6-on.txt"
```

判定の観点:
1. 窓管理: UI スレッドの包含に `win32kfull!xxxSetWindowPos` / `xxxShowWindow` / `xxxRedrawWindow` / `NtUserSetWindowPos` 等が占める割合。
2. TSF / IME: `msctf.dll` / `TextInputFramework.dll` / `imm32` / IME の DLL の包含。
3. UIA: kxEdit 側の `UIAutomationCore.dll` と `WM_GETOBJECT` の処理(`kxEdit.Accessibility` の管理コード)。窓の中の NVDA(`nvda`)・windows-mcp のプロセスのサンプル数。
4. レイアウト書込: ON の ETL で、背景ライター(`SerialBackupWriter` のスレッド)と `FlushFileBuffers` / `ntfs` のサンプル。OFF との差。
- 「正体不明の別スレッド」(調査記録 M-6 の 35 ms): 2 番目に多いスレッドの包含の上位から、スレッドの役割(スタックの根)を特定する。

**Step 5: 設計書 §11.1 の末尾に「調査結果」を追記する**

書く内容: 採取の条件(NVDA 起動中・windows-mcp 常駐・OFF/ON)、スレッド別の内訳の表(ms/回)、仮説 1〜4 の判定と根拠(関数名)、別スレッドの正体、kxEdit で減らせる部分と減らせない部分の区分、§11.3 の 2 項目それぞれの採否の提案。

**Step 6: ユーザーと §11.3 の採否を決める**(ここで止まる)

- 「レイアウト書込のまとめ」: 仮説 4 が有力な場合のみ提案する。テスト `Reconcile_ActiveSwitched_RewritesLayout` の期待値が変わるので、採否をユーザーに確認する(設計書 §11.3)。
- 「フォーカス移動の二重化」: 確認できたら a11y の不具合として別途起票を提案する(本フェーズでは性能の対処だけ・発声の順序は変えない)。
- 原因が OS / 環境にある場合は、そう記録して対処を行わない(設計書 §11.1)。

決定を本計画の Task 5 に精密化として書き込んでから、Task 5 に進む。

**Step 7: commit**

```powershell
git add docs/plans/2026-09-24-general-perf-improvements-design.md docs/plans/2026-09-25-perf-tab-switch.md
git commit -m "docs(perf): フェーズ 6 のタブ切替の固定費の調査結果(設計書 §11.1)"
```

---

## Task 4: P-6 バックアップ照合の全文化を省く(§11.2)

**Files:**
- Modify: `src/kxEdit.App/BackupCoordinator.cs`(`DocBackup`・`AdoptRestored`・`ReconcileContent`・`RegisterNew`・新しい private ヘルパーとテスト観測用のカウンタ)
- Test: `tests/kxEdit.App.Tests/BackupCoordinatorTests.cs`(新しい節「P-6: 同じスナップショットの照合を省く」)

**Interfaces:**
- Produces(テスト観測用・internal):
  - `internal int MaterializeCountForTest { get; }` — 署名のための全文化の累計回数(RegisterNew・AdoptRestored・ReconcileContent のすべて)。
  - `internal void ForgetRememberedSnapshotsForTest()` — 全 `DocBackup` の弱参照の対象を消す(GC による回収の再現)。

**Step 1: 失敗するテストを書く**(`Reconcile_ActiveSwitched_RewritesLayout` などレイアウトの節の前に、新しい節として置く)

```csharp
    // ===== P-6(性能改善フェーズ 6): 同じスナップショットの照合を省く =====
    // 省く条件は「modified かつ 覚えている参照と同じ かつ !ForceWrite」。この条件では従来も必ず None だった。
    // 省かない側(ForceWrite・参照の変化・回収後)は、全文化したうえで従来どおりに判定すること。

    [Fact]
    public void Reconcile_SameSnapshot_DoesNotMaterialize() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            _ = host.NewDoc("hello");
            host.Backup.Reconcile(); // 登録+書込(ここで参照を覚える)
            int before = host.Backup.MaterializeCountForTest;
            Assert.True(before >= 1); // sanity: 登録で全文化している

            host.Backup.Reconcile();
            host.Backup.Reconcile();

            Assert.Equal(before, host.Backup.MaterializeCountForTest);
            Assert.Single(host.Writer.Writes); // 判定は従来どおり None
        });

    [Fact]
    public void Reconcile_SameSnapshot_AfterWrite_DoesNotMaterialize() =>
        Sta.Run(() =>
        {
            // Write 分岐でも参照を覚えること(RegisterNew 以外の更新点)。
            using var host = new Host();
            var doc = host.NewDoc("hello");
            host.Backup.Reconcile();
            doc.Editor.Text = "world";
            doc.Editor.ClearSavePoint();
            host.Backup.Reconcile(); // 内容が変わった → Write(ここで新しい参照を覚える)
            Assert.Equal(2, host.Writer.Writes.Count);
            int before = host.Backup.MaterializeCountForTest;

            host.Backup.Reconcile();

            Assert.Equal(before, host.Backup.MaterializeCountForTest);
            Assert.Equal(2, host.Writer.Writes.Count);
        });

    [Fact]
    public void Reconcile_ForceWrite_MaterializesAndRewrites() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            _ = host.NewDoc("hello");
            host.Backup.Reconcile();
            var id = host.Writer.Writes[0].Id;
            host.Writer.OnWriteFailed?.Invoke(id); // 背景失敗 → ForceWrite
            int before = host.Backup.MaterializeCountForTest;

            host.Backup.Reconcile(); // 参照は同じでも ForceWrite なので省かない

            Assert.Equal(before + 1, host.Backup.MaterializeCountForTest);
            Assert.Equal(2, host.Writer.Writes.Count);
            Assert.Equal("hello", host.Writer.Writes[^1].Content);
        });

    [Fact]
    public void Reconcile_NewSnapshotSameContent_HashesAndDoesNotWrite() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("hello");
            host.Backup.Reconcile();
            doc.Editor.Text = "hello"; // 新しいバッファ=新しいスナップショット・内容は同じ
            doc.Editor.ClearSavePoint();
            int before = host.Backup.MaterializeCountForTest;

            host.Backup.Reconcile();

            Assert.Equal(before + 1, host.Backup.MaterializeCountForTest); // 参照が変わったのでハッシュまで進む
            Assert.Single(host.Writer.Writes); // 署名が同じなので None
        });

    [Fact]
    public void Reconcile_NewSnapshotChangedContent_Writes() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("hello");
            host.Backup.Reconcile();
            doc.Editor.Text = "world";
            doc.Editor.ClearSavePoint();

            host.Backup.Reconcile();

            Assert.Equal(2, host.Writer.Writes.Count);
            Assert.Equal("world", host.Writer.Writes[^1].Content);
        });

    [Fact]
    public void Reconcile_AfterSnapshotCollected_MaterializesAndStaysCorrect() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            _ = host.NewDoc("hello");
            host.Backup.Reconcile();
            host.Backup.ForgetRememberedSnapshotsForTest(); // 弱参照が回収された状態
            int before = host.Backup.MaterializeCountForTest;

            host.Backup.Reconcile();

            Assert.Equal(before + 1, host.Backup.MaterializeCountForTest); // 省けないので全文化する
            Assert.Single(host.Writer.Writes); // 内容は同じ → None(従来どおり)
        });

    [Fact]
    public void Reconcile_AfterSnapshotCollected_ChangedContent_Writes() =>
        Sta.Run(() =>
        {
            // 回収後に「参照が取れない」ことを「同じ」と取り違えないこと(null 同士の一致で省かない)。
            using var host = new Host();
            var doc = host.NewDoc("hello");
            host.Backup.Reconcile();
            host.Backup.ForgetRememberedSnapshotsForTest();
            doc.Editor.Text = "world";
            doc.Editor.ClearSavePoint();

            host.Backup.Reconcile();

            Assert.Equal(2, host.Writer.Writes.Count);
            Assert.Equal("world", host.Writer.Writes[^1].Content);
        });

    [Fact]
    public void Reconcile_AdoptRestored_RemembersSnapshot() =>
        Sta.Run(() =>
        {
            // AdoptRestored でも参照を覚えること(起動時の復元がタブ数の 2 乗にならない)。
            using var host = new Host();
            var doc = host.NewDoc("restored");
            var rec = new BackupRecord(
                Id: "adopt-p6",
                OriginalPath: null,
                UntitledNumber: 1,
                CodePage: 65001,
                HasBom: false,
                LineEndingId: 0,
                Content: "restored",
                TimestampUtc: FixedNow.UtcDateTime
            );
            host.Backup.AdoptRestored(doc, rec);
            int before = host.Backup.MaterializeCountForTest;

            host.Backup.Reconcile();

            Assert.Equal(before, host.Backup.MaterializeCountForTest);
            Assert.Empty(host.Writer.Writes); // 署名は採用時に計算済み → None
        });

    [Fact]
    public void Reconcile_CleanDocument_DoesNotMaterialize() =>
        Sta.Run(() =>
        {
            // クリーンな文書は従来から全文化しない(省略の条件に modified が要る理由の裏側)。
            using var host = new Host();
            _ = host.NewDoc("hello", dirty: false);
            host.Backup.Reconcile(); // 登録(RegisterNew は clean でも全文化する)
            int before = host.Backup.MaterializeCountForTest;

            host.Backup.Reconcile();

            Assert.Equal(before, host.Backup.MaterializeCountForTest);
            Assert.Empty(host.Writer.Writes);
        });
```

注意:
- `BackupRecord` の ctor の引数名・`AdoptRestored` を STA の Host から直に呼べることは、既存の復元テスト(`AdoptRestored` を呼んでいるテスト)を読んで合わせる。`TryMoveToSessionDir` が移動元なしで失敗しても trace だけで続行する(既存の `adopt-move-missed`)。
- `Reconcile_AdoptRestored_RemembersSnapshot` は、`host.NewDoc` の直後に `ActiveDocumentChanged` で `Reconcile` が走り、`RegisterNew` で既に `_map` に入っている可能性がある。その場合 `AdoptRestored` が `_map[doc]` を上書きする(本番の復元も同じ順序)。`Writes` が 1 件(RegisterNew の書込)になっていたら、`Assert.Empty` を「`AdoptRestored` の後に増えない」(`int writesBefore = host.Writer.Writes.Count;` を採って `Assert.Equal(writesBefore, ...)`)に直す。

**Step 2: 失敗を確かめる**

```powershell
dotnet test tests/kxEdit.App.Tests --filter "FullyQualifiedName~BackupCoordinatorTests" -c Debug
```
Expected: コンパイルエラー(`MaterializeCountForTest` / `ForgetRememberedSnapshotsForTest` が無い)。

**Step 3: 実装する**

`using kxEdit.Core.Buffers;` を足す(既存の `using` 群に、アルファベット順で)。

`DocBackup` に弱参照を足す:

```csharp
    private sealed class DocBackup
    {
        public string Id = "";
        public long LastSig;
        public bool HasBackup;
        public bool ForceWrite; // 前回の背景書込が失敗 → 次 tick で強制再書込(陳腐化・欠落を防ぐ)

        /// <summary>P-6(性能改善フェーズ 6): <see cref="LastSig"/> を計算したスナップショット。
        /// 不変条件: 対象が取れるなら、その署名は常に <see cref="LastSig"/> に等しい。そのため
        /// <see cref="LastSig"/> を新しい内容の署名で書き換える 3 箇所(RegisterNew・AdoptRestored・
        /// Write 分岐)で必ず同時に更新する。Delete 分岐は署名を変えないので残してよい。
        /// 弱参照にするのは、TextBuffer の差し替え後も旧文書全体の木を保持し続けないため。</summary>
        public WeakReference<TextSnapshot>? LastSnapshot;
    }
```

カウンタと 2 つのヘルパー(`RegisterNew` の直前に置く):

```csharp
    /// <summary>テスト観測用: 署名のための全文化の累計回数(P-6)。</summary>
    internal int MaterializeCountForTest { get; private set; }

    /// <summary>テスト観測用: 覚えているスナップショットをすべて忘れる(GC による回収の再現)。</summary>
    internal void ForgetRememberedSnapshotsForTest()
    {
        foreach (var info in _map.Values)
            info.LastSnapshot = null;
    }

    /// <summary>P-6: 署名の対象と、覚える参照を同じスナップショットから取る。
    /// <c>EditorControl.SnapshotText</c> と同じ文字列を返す(未設定のバッファは空の静的バッファ=空文字列)。</summary>
    private string Materialize(TextSnapshot snap)
    {
        MaterializeCountForTest++;
        return snap.GetText(0, snap.CharLength);
    }

    /// <summary>P-6: 覚えている参照と同じか。回収済み・未記憶は「同じでない」(null 同士を一致とみなさない)。</summary>
    private static bool IsRemembered(DocBackup info, TextSnapshot snap) =>
        info.LastSnapshot is { } w && w.TryGetTarget(out var s) && ReferenceEquals(s, snap);
```

`ForgetRememberedSnapshotsForTest` は `null` を代入する(回収後の `TryGetTarget` = false と、未記憶の `null` は `IsRemembered` で同じ扱いになるので、どちらで再現しても等価)。

`AdoptRestored`:

```csharp
    public void AdoptRestored(Document doc, BackupRecord rec)
    {
        var snap = doc.Editor.CurrentBuffer.Current;
        _map[doc] = new DocBackup
        {
            Id = rec.Id,
            LastSig = ContentSignature.Of(Materialize(snap)),
            LastSnapshot = new WeakReference<TextSnapshot>(snap),
            HasBackup = true,
        };
        // 以下(TryMoveToSessionDir)は変更しない
```

`ReconcileContent` のループ本体(`bool modified = ...` から switch の Write 分岐まで):

```csharp
            bool modified = doc.Editor.Modified;
            // P-6: 覚えている参照と同じなら、署名は LastSig に等しい(不変条件)。ForceWrite でなければ
            // Decide は必ず None を返すので、全文化もハッシュも省く。
            var snap = doc.Editor.CurrentBuffer.Current;
            if (modified && !info.ForceWrite && IsRemembered(info, snap))
                continue;
            string content = modified ? Materialize(snap) : ""; // クリーン時はスナップショット不要
            long sig = modified ? ContentSignature.Of(content) : info.LastSig;

            switch (
                BackupPlanner.Decide(modified, sig, info.LastSig, info.HasBackup, info.ForceWrite)
            )
            {
                case BackupAction.Write:
                    EnqueueWrite(info, doc, content);
                    info.LastSig = sig;
                    info.LastSnapshot = new WeakReference<TextSnapshot>(snap);
                    info.HasBackup = true;
                    info.ForceWrite = false;
                    break;
```
(Delete・None の分岐は変更しない)

`RegisterNew`:

```csharp
    private void RegisterNew(Document doc)
    {
        var snap = doc.Editor.CurrentBuffer.Current;
        string content = Materialize(snap);
        var info = new DocBackup
        {
            Id = Guid.NewGuid().ToString("N"),
            LastSig = ContentSignature.Of(content),
            LastSnapshot = new WeakReference<TextSnapshot>(snap),
            HasBackup = false,
        };
        // 以下は変更しない
```

`ReconcileContent` の `continue` は `foreach (var doc in _docs.Documents)` の次の文書へ進む(`switch` の外なので、ループの `continue` になる)。

**Step 4: 通るのを確かめる**

```powershell
dotnet test tests/kxEdit.App.Tests --filter "FullyQualifiedName~BackupCoordinatorTests|FullyQualifiedName~FileControllerTests.RestoreSession" -c Debug
```
Expected: 全件 PASS(既存の `BackupCoordinatorTests` と `FileControllerTests.RestoreSession_*` を含む。設計書 §11.4)。

**Step 5: 陰性対照**(テストが省略を本当に見ていることの確認・コードは残さない)

`ReconcileContent` の `if (modified && !info.ForceWrite && IsRemembered(info, snap)) continue;` を一時的にコメントアウトし、`Reconcile_SameSnapshot_DoesNotMaterialize` / `Reconcile_SameSnapshot_AfterWrite_DoesNotMaterialize` / `Reconcile_AdoptRestored_RemembersSnapshot` が FAIL することを確かめて戻す。Write 分岐の `info.LastSnapshot = ...` を一時的に消すと `Reconcile_SameSnapshot_AfterWrite_DoesNotMaterialize` が FAIL することも確かめて戻す。

**Step 6: commit**(commit 後の状態で build と test をやり直す。pre-commit の CSharpier が整形するため)

```powershell
git add src/kxEdit.App/BackupCoordinator.cs tests/kxEdit.App.Tests/BackupCoordinatorTests.cs
git commit -m "perf(backup): 同じスナップショットのバックアップ照合で全文化とハッシュを省く(P-6)"
dotnet build kxEdit.sln -c Debug
dotnet test tests/kxEdit.App.Tests --filter "FullyQualifiedName~BackupCoordinatorTests" -c Debug
```

---

## Task 5: パスのないタブで外部変更チェックを投函しない(§11.2)+ §11.3 の採用分

**Files:**
- Modify: `src/kxEdit.App/MainForm.cs:282-299`(`ActiveDocumentChanged` のハンドラ)と、テスト観測用のカウンタ
- Test: `tests/kxEdit.App.Tests/MainFormExternalChangeTests.cs`

**Interfaces:**
- Produces(テスト観測用・internal): `internal int ExternalChangeChecksQueuedForTest { get; }` — `ActiveDocumentChanged` から外部変更チェックを投函した累計回数。

**Step 1: 失敗するテストを書く**(`UntitledActiveDocument_Skipped` の後)

```csharp
    /// <summary>性能改善フェーズ 6(設計書 §11.2): パスのないタブへ切り替えたときは、外部変更チェックを
    /// 投函しない(実行しても Skipped で終わるため)。陽性対照として、ファイルのタブへ切り替えたときは投函する。
    /// 投函したデリゲートの実行はウィンドウの活性化が要るので見ない(クラスの注記のとおり L5 の担当)。</summary>
    [Fact]
    public void SwitchToUntitledTab_DoesNotQueueCheck_FileTab_Does() =>
        Sta.Run(() =>
        {
            using var tmp = new TempDir();
            using var form = ShowMainForm(NewSettings(), tmp, new FakePrompt());
            string path = tmp.File("a.txt");
            File2.WriteAllText(path, "v1");
            var untitled = form.DocsForTest.Active!; // 起動直後の無題タブ
            var fileDoc = form.FileForTest.TryOpenOrActivate(path)!;
            int before = form.ExternalChangeChecksQueuedForTest;

            form.DocsForTest.Activate(untitled);
            Assert.Equal(before, form.ExternalChangeChecksQueuedForTest);

            form.DocsForTest.Activate(fileDoc);
            Assert.Equal(before + 1, form.ExternalChangeChecksQueuedForTest);
        });
```

注意: `DocsForTest` という seam が `MainForm` に無ければ、既存の `FileForTest` / `CsvForTest` と同じ形(`internal DocumentManager DocsForTest => _docs;`)で足す。先に `MainForm.cs` を `ForTest` で検索し、同等のものがあればそれを使う。

**Step 2: 失敗を確かめる**

```powershell
dotnet test tests/kxEdit.App.Tests --filter "FullyQualifiedName~MainFormExternalChangeTests" -c Debug
```
Expected: コンパイルエラー(`ExternalChangeChecksQueuedForTest` が無い)。

**Step 3: 実装する**

ハンドラ(`MainForm.cs:290-298`)を次にする:

```csharp
            var doc = _docs.Active;
            // 性能改善フェーズ 6(設計書 §11.2): パスのないタブは CheckExternalChange が即 Skipped を返すので
            // 投函しない(結果は同じ)。投函から実行までに「名前を付けて保存」でパスが入っても、
            // その時点の LastKnownWriteTimeUtc は保存直後の値なので NoChange で終わる。
            if (doc is null || doc.State.Path is null || !IsHandleCreated)
                return;
            ExternalChangeChecksQueuedForTest++;
            BeginInvoke(() =>
            {
                if (IsDisposed || ActiveForm != this || !ReferenceEquals(_docs.Active, doc))
                    return;
                CheckExternalChangeOnActive();
            });
```

カウンタ(`CheckExternalChangeOnActiveForTest` の近く):

```csharp
    /// <summary>テスト観測用: <c>ActiveDocumentChanged</c> から外部変更チェックを投函した累計回数(フェーズ 6)。</summary>
    internal int ExternalChangeChecksQueuedForTest { get; private set; }
```

M-18 のコメント(`// (起動直後の無題タブは Path=null で判定対象にならない)`)は、上の新しいコメントと重複するので「ctor 中(ハンドル未生成)の発火は BeginInvoke できないので見送る。」までに縮める。

**Step 4: 通るのを確かめる**

```powershell
dotnet test tests/kxEdit.App.Tests --filter "FullyQualifiedName~MainFormExternalChangeTests|FullyQualifiedName~MainFormSmokeTests" -c Debug
```
Expected: 全件 PASS。

**Step 5: 陰性対照**: 条件の `doc.State.Path is null ||` を一時的に外し、新テストの 1 つ目の assert が FAIL することを確かめて戻す。

**Step 6: commit**(commit 後の状態で build と test をやり直す)

```powershell
git add src/kxEdit.App/MainForm.cs tests/kxEdit.App.Tests/MainFormExternalChangeTests.cs
git commit -m "perf(app): パスのないタブへの切替で外部変更チェックを投函しない"
```

**Step 7: §11.3 の採用分**(Task 3 Step 6 の決定を、ここに精密化として書き込んでから実装する)

- 採用なし → このステップは「採用なし(理由: 調査結果 §11.1 のとおり)」と書いて終わる。
- 「レイアウト書込のまとめ」を採用 → 決定した内容・変更するテスト(`Reconcile_ActiveSwitched_RewritesLayout` の期待値)・新しいテスト(`IsActive` だけの変化では書かず、次の tick / FinalFlush で書く)・PR に書く挙動差(設計書 §3.5)を、TDD のステップとしてここに書き足す。
- フォーカス移動に触れる対処を採用 → L5(NVDA でのタブ切替時の読み上げ)を Task 6 に足す。

---

## Task 6: 変更後を測り、最終レビュー・品質ゲート・PR

**Step 1: 変更後の publish で M-6 を 6 回**(Task 2 と同じ条件。ユーザーに声をかけてから)

```powershell
dotnet publish src/kxEdit.App -c Release -r win-x64 --self-contained false -o "$scratch\pub-after"
foreach ($i in 1..3) {
  pwsh -NoProfile -File tools/perf-harness.ps1 -PublishDir "$scratch\pub-after" -Scenario M-6 -OutCsv "$scratch\m6-after-off-$i.csv"
  pwsh -NoProfile -File tools/perf-harness.ps1 -PublishDir "$scratch\pub-after" -Scenario M-6 -SessionRestore -OutCsv "$scratch\m6-after-on-$i.csv"
}
```

Task 2 の表の隣に並べ、設計書 §3.2 の判断基準で判定する。期待:
- P-6 は「3 ファイル未保存+空」で効く(調査記録: 未保存の合計 4.7MB で約 10 ms/回)。他の 2 条件では変わらない。
- 外部変更チェックの投函は「空の新規タブ 4 枚」でわずかに効く(投函 1 回ぶん)。揺れに埋もれてよい。
- 調査で「減らせない」と結論した分は、その旨を実施記録に書く(設計書 §11.5)。

**Step 2: 最終レビュー(2 パス・別エージェント)**(CLAUDE.md §3 の 5)

- コード品質パス(ミューテーション検証のスポットチェックは、本フェーズでは行わない=§0.3)。
- 脆弱性パス(harness のプロフィールへの書込・`BackupCoordinator` のデータ保全の不変条件)。
- 指摘は fixup commit で反映する(CLAUDE.md §4)。

**Step 3: 品質ゲート**

```powershell
pwsh -NoProfile -File tools/pre-merge-check.ps1
```
Expected: EXIT 0。

**Step 4: 実施記録を書く**

本計画の末尾「実施記録」と、設計書 §11 の末尾に「実施記録(2026-09-25・PR #NN)」を追記する(前フェーズと同じく PR に同梱する。マージ後の追記は不要にする)。書く内容: 成果物・計測(変更前後・NVDA 起動中)・調査結果の要約・精密化と逸脱(§0.2)・意図的な挙動差・申し送り。

**Step 5: PR**

push して PR を作る。description(日本語)に: 目的・変更点・変更前後の計測値(表)・調査結果の要約・意図的な挙動差(なし、または §3.5 の行)・L5(不要の理由、または結果)・レビューの経緯・申し送り。

---

## Review Focus

- **スナップショットの参照が同じなのに内容が違う**: 起きないこと(`TextSnapshot` は不変で、`TextBuffer` は編集・Undo・Redo・差し替えのたびに新しいスナップショットを作る)。レビューでは `TextBuffer` の `_current` の代入箇所がすべて新規生成であることを確かめる。
- **ForceWrite 中の省略**: 書込失敗の後の再書込が省かれないこと(`Reconcile_ForceWrite_MaterializesAndRewrites`)。
- **回収後の取り違え**: 弱参照が取れないことを「同じ」と扱わないこと(`Reconcile_AfterSnapshotCollected_ChangedContent_Writes`)。
- **clean → dirty の遷移**: 保存(clean)の後に編集して dirty に戻ると、スナップショットは必ず新しいので省略されない。保存時の Delete 分岐で参照を残すのは、不変条件(署名が等しい)を崩さないため安全。
- **harness が利用者のプロフィールに書く**: `Write-RunSettings` は目印が `cleared` のときだけ書く(Task 1 の -SelfTest と前倒しの脆弱性レビュー)。

---

## 実施記録

(Task 2 以降で追記する)
