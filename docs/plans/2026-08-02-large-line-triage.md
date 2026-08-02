# 巨大 1 行 / 長大トークン耐性 — 真因切り分け調査 実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 長大 1 行ファイルを開くと 240 秒以上ハングする既存問題の真因を確定し、申し送り
F-1〜F-6 の対応要否・順序・単位を判断できる材料を揃える。

**Architecture:** `src/` は一切変更しない。計測を 2 段に分ける — ① `yEdit.Core.Bench`
(`MonoCharMetrics` 固定幅・GDI 抜き)で**行長に対する線形性**を判定し、②
`yEdit.Editor.Smoke`(実 `EditorControl` を offscreen Form に載せる)で**GDI 込みの実経路**を
測る。GDI を後から足すことで「非線形性は構造由来か GDI 由来か」が切り分けられる。

**Tech Stack:** .NET 9 / C# / WinForms(Smoke のみ)/ 既存ベンチ基盤
(`tests/yEdit.Core.Bench/Program.cs` の `--characcess`・`tests/yEdit.Editor.Smoke/GdiBench.cs`)

**設計書:** `docs/plans/2026-08-02-large-line-triage-design.md`

**実装者への注意(PR #33 の教訓):** 本計画に書いたコードは
SonarAnalyzer / CSharpier / 実際の API シグネチャと食い違う可能性がある。
**そのまま貼って通らなければ、誤りを報告したうえで修正して進めること。** 計画を絶対視しない。
PR #33 では計画側の誤りが 6 件見つかっている。

**本ブランチの不変条件:** `src/` 配下を 1 行も変更しない。修正候補が見つかっても
実装せず §4.4 の設計書に記録するに留める(設計書 §8 R3)。

---

### Task 1: ファイルオープン経路の呼び出し回数を確定する

**Files:**
- Read: `src/yEdit.Editor/EditorControl.cs:209`(`SetSource`)/ `:1072-1083`(水平スクロールバー算出)/ `:1528-1533`
- Read: `src/yEdit.Editor/EditorControl.Paint.cs:31-34`
- Read: `src/yEdit.Core/Layout/ViewportLayout.cs:48-72`
- Read: `src/yEdit.Core/Editing/VerticalNavigation.cs:210-216`
- Read: `src/yEdit.App/` のファイルオープン経路(`TextFileService.LoadAsBufferAuto` の呼び出し元から `SetSource` までを辿る)
- Create: `docs/plans/2026-08-02-large-line-resilience-design.md`

**Step 1: 論理行全体を舐める箇所を洗い出す**

設計書 §4.1 の候補表を出発点に、`snapshot.GetText(lineStart, lineLen)` および
`LineLayout.Wrap` の呼び出し元を全て列挙する。

Run: `rg -n "LineLayout\.Wrap|ViewportLayout\.Build" src/`
Expected: 設計書 §4.1 の 5 箇所 + `FrameBuilder` 内の参照

**Step 2: 1 回のファイルオープンで各箇所が何回呼ばれるかを確定する**

コードを読んで呼び出し回数を determine する。特に次を明らかにする。

- `SetSource` は水平スクロールバー算出(`EditorControl.cs:1072-1083`)を呼ぶか
- `EditorControl.Paint.cs:34` の `ViewportLayout.Build` に**キャッシュはあるか**
  (なければフレームごとに論理行全体を再 Wrap する)
- 水平スクロールバー算出は `wrapColumns: 0` で `Build` するため、巨大 1 行では
  `SegmentLength` が行長そのものになる。`snap.GetText` が 500K 文字の string を作り
  `MeasureRun` へ一括投入される経路が実際に踏まれるか

**Step 3: 新設計書に §2.1 として書く**

`docs/plans/2026-08-02-large-line-resilience-design.md` を新規作成し、次の骨格で書く。
§1 は Task 5 で最後に埋めるため見出しだけ置く。

```markdown
# 巨大 1 行 / 長大トークン耐性 設計書

策定日: 2026-08-02 / 調査ブランチ: `feature/large-line-triage`

## 1. 背景・目的

(Task 5 で記述)

## 2. 現状調査結果(2026-08-02)

### 2.1 「ファイルを開く」経路の呼び出し回数

| 箇所 | 何をするか | 1 回のオープンでの呼び出し回数 | 巨大 1 行でのコスト |
|---|---|---|---|
| ... | ... | ... | ... |

(キャッシュの有無・`wrapColumns: 0` 経路が踏まれるかを明記する)
```

**Step 4: commit**

```bash
git add docs/plans/2026-08-02-large-line-resilience-design.md
git commit -m "docs(plans): 調査 Task 1 — ファイルオープン経路の呼び出し回数を確定"
```

---

### Task 2: Core.Bench に `--largeline` を追加して構造コストを測る

GDI を含まない `MonoCharMetrics`(固定幅)で測り、**行長に対して線形か O(n²) か**だけを
先に確定させる。あわせて F-6 の空白(`AppendBuffer` 現ブロック経路)を埋める。

**Files:**
- Modify: `tests/yEdit.Core.Bench/Program.cs`(フラグ追加 + `--characcess` ブロックの直後に新ブロック)

**Step 1: フラグを追加する**

`Program.cs:18` 付近の宣言に 1 行、`:34-37` の分岐に 1 ブロックを足す。

```csharp
bool largeLineMode = false;
```

```csharp
    else if (args[i] == "--largeline")
    {
        largeLineMode = true;
    }
```

冒頭のコメントブロック(`:12-13` の下)にも 1 行足す。

```csharp
// 巨大 1 行調査 Task 2: --largeline 追加。空白・改行なしの単一長大行に対する
//             ViewportLayout / GetChar の構造コストを測る(GDI 抜き)。単独で早期 return する。
```

**Step 2: `--largeline` ブロックを書く**

`--characcess` ブロックの直後(`caResults` の出力と `return` の後)に置く。

```csharp
// ---- 2026-08-02 巨大 1 行調査: --largeline ----
// 空白・改行を一切含まない単一長大行の構造コスト。MonoCharMetrics(固定幅)を使い
// GDI を経路から外すことで、非線形性が「構造由来」か「GDI 由来」かを切り分ける
// (GDI 込みは tests/yEdit.Editor.Smoke --largeline が担当)。
// 判定ゲートは持たない=調査用のため常に EXIT 0。
if (largeLineMode)
{
    Console.WriteLine("--largeline: 巨大 1 行の構造コスト(MonoCharMetrics・GDI 抜き)");

    // 空白も改行も含まない 1 行を作る。文字種で MeasureRun の経路が変わるため 3 種振る
    // (GdiCharMetrics は全 ASCII なら配列加算・非 ASCII を 1 文字でも含むと GDI へ落ちる)。
    static string MakeSingleLine(int chars, string kind)
    {
        var sb = new StringBuilder(chars);
        var r = new Random(20260802); // 決定的(既存ベンチの流儀)
        while (sb.Length < chars)
        {
            sb.Append(
                kind switch
                {
                    "ascii" => (char)('a' + r.Next(26)),
                    "cjk" => (char)('あ' + r.Next(40)),
                    _ => r.Next(2) == 0 ? (char)('a' + r.Next(26)) : (char)('あ' + r.Next(40)),
                }
            );
        }
        return sb.ToString(0, chars);
    }

    var llMetrics = new MonoCharMetrics(halfWidthPx: 8, lineHeightPx: 20);
    int llHeightPx = 40 * llMetrics.LineHeightPx; // 可視 40 行相当
    int[] llLengths = [100_000, 500_000, 2_000_000];

    Console.WriteLine();
    Console.WriteLine("kind,chars,wrapColumns,buildMs,rows");
    foreach (string kind in new[] { "ascii", "cjk", "mixed" })
    {
        foreach (int len in llLengths)
        {
            var llSnap = TextBuffer.FromString(MakeSingleLine(len, kind)).Current;
            foreach (int wrap in new[] { 0, 80 })
            {
                // ウォームアップ(JIT)。巨大入力なので 1 回で足りる
                var _warm = ViewportLayout.Build(llSnap, 0, llHeightPx, wrap, llMetrics);
                var llSw = Stopwatch.StartNew();
                var llRows = ViewportLayout.Build(llSnap, 0, llHeightPx, wrap, llMetrics);
                llSw.Stop();
                Console.WriteLine(
                    $"{kind},{len},{wrap},{llSw.Elapsed.TotalMilliseconds:F1},{llRows.Count}"
                );
            }
        }
    }

    // --- F-6 の空白埋め: AppendBuffer 現ブロック経路 ---
    // 既存 --characcess は TextBuffer.FromString(builder チャンク)だけを測っており
    // 「実際にタイプして育てた文書」= AppendBuffer の現ブロックに一度も触れていない。
    // 同クラスの共有チャンクは格子表を先頭 1 エントリに固定しているため(設計書 §2.4)、
    // CharToByte がブロック先頭からの線形走査になる=最大 64KB 走査が残る領域。
    Console.WriteLine();
    Console.WriteLine("F-6: AppendBuffer 現ブロック経路(タイプして育てた文書)");
    var typedBuf = new TextBufferBuilder().Build();
    var typedRnd = new Random(20260802);
    // 64KB ブロックが埋まりかけの状態を作る。単語区切りを入れて PrevWordStart が動くようにする
    for (int i = 0; i < 60_000; i++)
    {
        typedBuf.Insert(
            typedBuf.Current.CharLength,
            typedRnd.Next(8) == 0 ? " " : ((char)('a' + typedRnd.Next(26))).ToString()
        );
    }
    var typedSnap = typedBuf.Current;
    Console.WriteLine($"typed 文書: {typedSnap.CharLength:N0} 文字 / ピース数 {typedSnap.PieceCount}");
    foreach (int frac in new[] { 1, 2, 4 })
    {
        int pos = typedSnap.CharLength * (frac - 1) / 4 + typedSnap.CharLength / 8;
        var f6Sw = Stopwatch.StartNew();
        long f6Sink = 0;
        const int F6Iters = 2_000;
        for (int i = 0; i < F6Iters; i++)
            f6Sink += typedSnap.GetChar(pos);
        f6Sw.Stop();
        Console.WriteLine(
            $"  GetChar(pos={pos:N0}): {f6Sw.Elapsed.TotalNanoseconds / F6Iters:N0} ns/回 (sink {f6Sink})"
        );
    }
    var f6NavSw = Stopwatch.StartNew();
    long f6NavSink = 0;
    for (int i = 0; i < 200; i++)
        f6NavSink += WordBoundary.PrevWordStart(typedSnap, typedSnap.CharLength - 1 - i * 10);
    f6NavSw.Stop();
    Console.WriteLine(
        $"  PrevWordStart × 200: {f6NavSw.Elapsed.TotalMilliseconds / 200:F4} ms/回 (sink {f6NavSink})"
    );

    Console.WriteLine();
    Console.WriteLine("(調査用ベンチのため判定ゲートなし) EXIT 0");
    return 0;
}
```

**Step 3: ビルドして実行する**

Run:
```bash
dotnet run -c Release --project tests/yEdit.Core.Bench -- --largeline
```

Expected: `kind,chars,wrapColumns,buildMs,rows` の CSV が 18 行(3 文字種 × 3 長さ × 2 wrap)、
続いて F-6 の 4 行。EXIT 0。

**判定の読み方:** `buildMs` を行長 100K → 500K → 2M で比較する。
**5 倍・20 倍で伸びれば線形**(構造は素直)、**それ以上に跳ねれば O(n²)**。
線形かつミリ秒級なら、240 秒の主因は構造ではなく GDI 側 = Task 3 で確定する。

**Step 4: 結果を設計書 §2.2 へ記録する**

`docs/plans/2026-08-02-large-line-resilience-design.md` に `### 2.2 構造コスト(GDI 抜き)`
として CSV を表で貼り、**線形性の判定**と **F-6 の実測値**を文章で書く。

**Step 5: commit**

```bash
git add tests/yEdit.Core.Bench/Program.cs docs/plans/2026-08-02-large-line-resilience-design.md
git commit -m "bench(core): --largeline で巨大 1 行の構造コストと F-6 の空白を測る"
```

---

### Task 3: Editor.Smoke に `--largeline` を追加して GDI 込みの実経路を測る

**Files:**
- Create: `tests/yEdit.Editor.Smoke/LargeLineBench.cs`
- Modify: `tests/yEdit.Editor.Smoke/Program.cs`(`--bench` 分岐の直後に 1 ブロック)

**Step 1: `Program.cs` に分岐を足す**

`Program.cs:13-16` の `--bench` ブロックの直後に置く。

```csharp
// 2026-08-02 巨大 1 行調査 Task 3: --largeline。空白・改行なしの単一長大行を
// 実 EditorControl に載せ、SetSource〜初回描画完了までを GDI 込みで測る。
if (args.Length > 0 && args[0] == "--largeline")
{
    return LargeLineBench.Run(args);
}
```

**Step 2: `LargeLineBench.cs` を書く**

```csharp
using System.Diagnostics;
using System.Text;
using yEdit.Core.Buffers;
using yEdit.Editor;

namespace yEdit.Editor.Smoke;

/// <summary>
/// 2026-08-02 巨大 1 行調査(docs/plans/2026-08-02-large-line-triage-design.md §4.2)。
/// 空白・改行を一切含まない単一長大行を実 <see cref="EditorControl"/> へ載せ、
/// SetSource と初回描画の実時間を GDI 込みで測る。文字種(ascii / cjk / mixed)と
/// 行長と折り返し ON/OFF を振り、240 秒ハングの主因を切り分ける。
/// GDI 抜きの構造コストは Core.Bench --largeline が対。
/// 調査用のため判定ゲートは持たない(常に EXIT 0)。
/// </summary>
internal static class LargeLineBench
{
    /// <summary>1 条件がこの時間を超えたら、その文字種の以降の行長をスキップする。</summary>
    private const double SkipThresholdSec = 60.0;

    public static int Run(string[] args)
    {
        ApplicationConfiguration.Initialize();
        using var form = new Form
        {
            Text = "yEdit.Editor.Smoke --largeline",
            Width = 900,
            Height = 700,
            StartPosition = FormStartPosition.Manual,
            Location = new Point(-32000, -32000),
            ShowInTaskbar = false,
        };
        using var editor = new EditorControl { Dock = DockStyle.Fill };
        form.Controls.Add(editor);
        form.Show(); // ハンドル生成(offscreen だと Invalidate/Update が no-op)

        int[] lengths = [100_000, 500_000, 2_000_000];

        Console.WriteLine("kind,chars,wrapColumns,setSourceMs,firstPaintMs,totalMs");
        foreach (string kind in new[] { "ascii", "cjk", "mixed" })
        {
            foreach (int wrap in new[] { 0, 80 })
            {
                foreach (int len in lengths) // 短い順=閾値超えで残りを捨てられる
                {
                    string line = MakeSingleLine(len, kind);
                    var buf = TextBuffer.FromString(line);

                    editor.WrapColumns = wrap;
                    Application.DoEvents();

                    var swSet = Stopwatch.StartNew();
                    editor.SetSource(buf);
                    Application.DoEvents();
                    swSet.Stop();

                    var swPaint = Stopwatch.StartNew();
                    editor.Invalidate();
                    editor.Update(); // 同期 paint
                    swPaint.Stop();

                    double totalSec = (swSet.Elapsed + swPaint.Elapsed).TotalSeconds;
                    Console.WriteLine(
                        $"{kind},{len},{wrap},{swSet.Elapsed.TotalMilliseconds:F1},"
                            + $"{swPaint.Elapsed.TotalMilliseconds:F1},{totalSec * 1000:F1}"
                    );

                    if (totalSec > SkipThresholdSec)
                    {
                        Console.WriteLine(
                            $"  → {totalSec:F1}s > {SkipThresholdSec}s のため {kind}/wrap={wrap} の以降の行長をスキップ"
                        );
                        break;
                    }
                }
            }
        }

        form.Close();
        Console.WriteLine("(調査用ベンチのため判定ゲートなし) EXIT 0");
        return 0;
    }

    /// <summary>空白も改行も含まない 1 行を作る(Core.Bench --largeline と同一の生成規則)。</summary>
    private static string MakeSingleLine(int chars, string kind)
    {
        var sb = new StringBuilder(chars);
        var r = new Random(20260802);
        while (sb.Length < chars)
        {
            sb.Append(
                kind switch
                {
                    "ascii" => (char)('a' + r.Next(26)),
                    "cjk" => (char)('あ' + r.Next(40)),
                    _ => r.Next(2) == 0 ? (char)('a' + r.Next(26)) : (char)('あ' + r.Next(40)),
                }
            );
        }
        return sb.ToString(0, chars);
    }
}
```

**Step 3: 実行する**

Run:
```bash
dotnet run -c Release --project tests/yEdit.Editor.Smoke -- --largeline
```

Expected: CSV が最大 18 行(スキップが起きればそれ以下)。EXIT 0。
**60 秒を超える条件が出たら、それが 240 秒ハングの再現である。**

**注意:** offscreen Form とはいえウィンドウを 1 枚出す。実行中は他の GUI 操作をしない
(マシン負荷で絶対値が 10 倍ずれた実例がある — 設計書 §5)。

**Step 4: Core.Bench の結果と突き合わせる**

Task 2 の `buildMs`(GDI 抜き)と Task 3 の `firstPaintMs`(GDI 込み)を同条件で比較し、
差分が GDI 由来のコストである。文字種による差(ascii ↔ cjk)が支配的なら
設計書 §3 の仮説(`GdiCharMetrics.MeasureRun` の非 ASCII 分岐)が裏づけられる。

**Step 5: 結果を設計書 §2.3 へ記録する**

`### 2.3 GDI 込みの実経路` として CSV を表で貼り、**Task 2 との差分**を文章で書く。
真因が特定できた場合はその機序を明記する。**特定できなかった場合は §8 R1 に従い、
Task 4 で `dotnet-dump` によるスタック採取へ切り替える旨を書く。**

**Step 6: commit**

```bash
git add tests/yEdit.Editor.Smoke/LargeLineBench.cs tests/yEdit.Editor.Smoke/Program.cs docs/plans/2026-08-02-large-line-resilience-design.md
git commit -m "bench(smoke): --largeline で巨大 1 行の GDI 込み実経路を測る"
```

---

### Task 4: 実機セッション(ユーザー作業)

**このタスクはユーザーが実施する。** 自動では取れない 2 つを 1 セッションに束ねる。
Task 2・3 の結果を先に提示し、**実機で確認すべき項目を絞ってから**依頼すること。

**Files:**
- Modify: `docs/plans/2026-08-02-large-line-resilience-design.md`(§2.4 を追記)

**Step 1: 副産物の裏取り**

- 長大 1 行ファイルを開き、**`Process.Responding` をポーリング**して復帰までの実時間を測る
- **`SendKeys` の所要時間は使わない**(ハングしたウィンドウのメッセージキューに投げて即座に
  返るため。§9.8 で実際に 10.5 ms という無効値を得た)
- **折り返し ON / OFF の両方**で測る
- **§9.8 で使った fixture の文字種を記録する**(設計書 §3 の分岐に直結)

**Step 2: F-3 の実害採取**

NVDA スピーチビューアーの `RICHEDIT50W` ペインの `Name` プロパティを UIA で読む手法
(PR #33 §9.8 で確立)により、「**同じ位置なのに SR の単語読みと Ctrl+←→ の移動先が違う**」
実例を採取する。文字種は **CJK / 記号 / 英単語 / 混在**の 4 通り。

**Step 3(任意): L5 未検証項目の前倒し消化**

余力があれば NVDA レビューカーソル(テンキー)での文字単位・単語単位読みと、
上書きモードでの発話も同セッションで確認する。

**Step 4: 結果を設計書 §2.4 へ記録して commit**

```bash
git add docs/plans/2026-08-02-large-line-resilience-design.md
git commit -m "docs(plans): 調査 Task 3 — 実機での裏取りと F-3 実害採取の結果"
```

---

### Task 5: 結果を統合し、レビュー・ゲート・PR

**Files:**
- Modify: `docs/plans/2026-08-02-large-line-resilience-design.md`(§1 を記述)

**Step 1: §1 背景・目的を書く**

Task 1〜4 の結果を踏まえ、**真因を 1〜2 文で断定する**(特定できなかった場合はその旨と
次に取るべき手段を書く)。推定と実測を混ぜない。

**Step 2: 申し送りの再評価を書く**

設計書 §2 の棚卸し表を、実測を踏まえて更新した版を `## 3. 対応方針(案)` として書く。
**ただしスコープの確定はユーザー判断**であり、ここでは選択肢と見積りを提示するに留める。

**Step 3: 別エージェントによるレビュー(CLAUDE.md §4・省略しない)**

調査ブランチのためコード量は小さいが、**測定手法の妥当性**をレビューさせる。
特に次を見させる。

- 空真の合格(`All(r => r.Pass is not false)` 型)を作っていないか
- ウォームアップ・繰り返し回数が結論を支えるに足りるか
- 文字種 / 行長の振り方に取りこぼしがないか
- 「測っていないもの」を測ったかのように書いていないか

**Step 4: 品質ゲート**

Run: `pwsh -File tools/pre-merge-check.ps1`
Expected: **EXIT 0**。`tests/` に変更が入るため CLAUDE.md §6 のドキュメントのみ例外には
当たらない。

**Step 5: push して PR を作る**

```bash
git push -u origin feature/large-line-triage
gh pr create --title "調査: 巨大 1 行 / 長大トークン耐性の真因切り分け" --body "..."
```

PR description は日本語で、**目的・実測結果の要点・レビュー経緯・申し送り**を書く
(CLAUDE.md §7)。`src/` 変更ゼロであることを明記する。

---

## 検証手法上の注意(全タスク共通)

PR #33 で実際に踏んだもの。再発させない(設計書 §5)。

- `SendKeys` の所要時間は処理完了までの時間ではない → `Process.Responding` をポーリング
- 単一位置での測定は格子セル内オフセットで最大 20 倍変わる → セル全域を掃く
- `results.All(r => r.Pass is not false)` は判定行ゼロで**空真 = 測っていないのに合格**
- 前後比較のため旧版をビルドして同じ操作を流す
- ミューテーション検証をするなら `--no-build` を使わない / 復元は書き込みで行う
  (`Copy-Item` は LastWriteTime ごと複製され再ビルドが省略される)
- 並行でエージェントを走らせるならワークツリーを分離する
