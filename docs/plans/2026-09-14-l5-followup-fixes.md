# L5 で検出した F-1 / F-4 の修正 実装計画

設計書: [2026-09-14-l5-followup-fixes-design.md](./2026-09-14-l5-followup-fixes-design.md)
対象ブランチ: `feature/v0.2-l5-consolidated`(設計書 commit `f967716` の上に積む)

---

## Task 1 — F-4: 復元された空の無題タブに本文バッファを与える

### 変更

`src/kxEdit.App/FileController.cs` の `RestoreUntitledFrame`。
`NewFile()` と同じ 4 行を足し、末尾コメントを実態に合わせる。

```csharp
    /// <summary>空の無題タブの枠を復元する(BackupId=null=終了時に空だったタブ。設計 §2.1)。</summary>
    private Document RestoreUntitledFrame(kxEdit.Core.Session.SessionLayoutRecord rec)
    {
        var s = _settings();
        var doc = _docs.CreateNew();
        doc.State.Path = null;
        doc.State.UntitledNumber = rec.UntitledNumber > 0 ? rec.UntitledNumber : ++_untitledSeq;
        if (rec.UntitledNumber > _untitledSeq)
            _untitledSeq = rec.UntitledNumber;
        doc.State.Encoding = EncodingCatalog.Get(s.DefaultCodePage);
        doc.State.HasBom = false;
        doc.State.LineEnding = SafeLineEndingOrFallback(rec.LineEnding);
        // F-4(2026-09-14 L5): ここでソースを与えないと EditorControl の _buffer が null のままになり、
        // 書込系がすべて `if (_buffer is null || ReadOnly) return;` で黙って落ちる
        // = 復元された空の無題タブに 1 文字も入力できない。NVDA は打鍵を読み上げるので
        // 「入力できた」と誤認する。他の復元経路(RestoreDirtyFromBackup /
        // RestoreUntitledFromBackup)と NewFile() は全てソースを与えており、ここだけが抜けていた。
        doc.Editor.Text = string.Empty;
        ApplyEol(doc);
        doc.Editor.EmptyUndoBuffer();
        // 空だったタブの枠を戻すだけなので clean(ClearSavePoint ではない)。
        // `*` を付けると「保存すべき中身がある」と嘘をつく。
        doc.Editor.SetSavePoint();
        DocumentManager.UpdateLabel(doc);
        return doc;
    }
```

### 網(`tests/kxEdit.App.Tests/FileControllerTests.cs` へ追加)

1. `RestoreUntitledFrame` 経由で復元したタブに**実際に文字を入れると本文が変わる**
   (`Editor.Text` の往復で確認。**「_buffer が null でない」では固定しない**)。
2. 復元直後は `Modified == false`。
3. `rec.LineEnding` が `Editor.EolMode` に反映されている。

### 検証

```powershell
dotnet test tests/kxEdit.App.Tests -c Release --filter "FullyQualifiedName~FileControllerTests"
```

---

## Task 2 — F-1: 本文 DrawText を長さで分割する(通常経路)

### 変更

`src/kxEdit.Core/Layout/FrameBuilder.cs`。

1. 定数を足す:

```csharp
    /// <summary>
    /// 1 つの DrawText op に載せる最大文字数。GDI / Uniscribe は 1 回の描画で扱える
    /// グリフ数が 16 bit に収まる必要があり、見積もりが <c>1.5 × 文字数 + 16 ≤ 65535</c>
    /// (= 43,679 文字)を超えると <b>エラーを返さずに何も描かない</b>
    /// (2026-09-14 の L5 で実測: 43,671 文字は描かれ、43,681 文字は描かれない)。
    /// 実装詳細に依存する値なので 1/2.6 の余裕を取る。結合文字が多い文字列では
    /// グリフ数が文字数を上回りうることも織り込む。
    /// 通常の行(数百文字)では絶対に分割されない大きさにしてある。
    /// </summary>
    private const int MaxCharsPerTextOp = 16384;
```

2. 本文 op の発行(工程 5 の `else` 側)を、直接 `ops.Add` する形から
   **`EmitBodyRun` ヘルパ経由**にする。ヘルパは長さで割って複数 op を出す。

```csharp
    /// <summary>
    /// 本文 1 run を DrawText op として発行する。<see cref="MaxCharsPerTextOp"/> を超える run は
    /// 複数 op へ割る(超えない run は今までどおり 1 op = 既存の PaintOp 列は変わらない)。
    /// </summary>
    /// <remarks>
    /// <b>X は「チャンクを 1 つずつ測った幅の累積」で出す。</b>
    /// <see cref="PixelMapper.OffsetToPx"/>(行頭からの prefix 計測)をチャンクごとに呼ぶと
    /// O(行長 × チャンク数)になり、長大行で毎フレーム数百万文字を測ることになる
    /// (2026-08-02 の large-line-resilience が潰した経路の再導入)。
    /// 累積なら合計は「行あたり 1 回ぶん」に収まる。
    /// <para>
    /// <see cref="ICharMetrics.MeasureRun"/> は非 ASCII を含む run の一括計測が加算的でないため、
    /// 継ぎ目で数 px ずれうる。<b>16,384 文字を超える行でしか起きない</b>ので受容する
    /// (設計 2026-09-14 §2.4)。「数 px ずれる」と「1 文字も描かれない」なら後者が重い。
    /// </para>
    /// 分割位置は <see cref="TextBoundary.SnapToCodePointStart"/> で前方スナップし、
    /// サロゲートペアを割らない(既存の選択分割と同じ規約)。
    /// </remarks>
    private static void EmitBodyRun(
        ReadOnlySpan<char> text,
        int xPx,
        int yPx,
        int lineHeight,
        PaintColor fore,
        ICharMetrics metrics,
        List<PaintOp> ops
    )
    {
        if (text.Length == 0)
            return;

        if (text.Length <= MaxCharsPerTextOp)
        {
            ops.Add(
                new PaintOp(
                    PaintOpKind.DrawText,
                    xPx,
                    yPx,
                    metrics.MeasureRun(text),
                    lineHeight,
                    Text: text.ToString(),
                    Fore: fore
                )
            );
            return;
        }

        int from = 0;
        int x = xPx;
        while (from < text.Length)
        {
            int want = Math.Min(from + MaxCharsPerTextOp, text.Length);
            // 末尾チャンク以外は、コードポイント境界へ前方スナップしてペアを割らない。
            int to = want >= text.Length ? text.Length : TextBoundary.SnapToCodePointStart(text, want);
            // スナップが from まで戻ることは無い(MaxCharsPerTextOp >= 2 かつ
            // コードポイント長は最大 2)。万一戻ったら前進を保証して無限ループを避ける。
            if (to <= from)
                to = Math.Min(from + 2, text.Length);

            var chunk = text[from..to];
            int w = metrics.MeasureRun(chunk);
            ops.Add(
                new PaintOp(PaintOpKind.DrawText, x, yPx, w, lineHeight, Text: chunk.ToString(), Fore: fore)
            );
            x += w;
            from = to;
        }
    }
```

3. 工程 5 の `else` 側を差し替える:

```csharp
            EmitBodyRun(text.AsSpan(), bodyX, row.YPx, lineHeight, style.Foreground, metrics, ops);
```

### 網(`tests/kxEdit.Core.Tests/Layout/FrameBuilderTests.cs` へ追加)

1. `MaxCharsPerTextOp` 以下の行は **DrawText op が 1 つ**(既存挙動の不変)。
2. 超える行は **op が複数になり、Text を順に連結すると元の行と一致する**(F-1 の本体)。
3. 各 op の X が単調増加し、先頭 op の X が `bodyX` と一致する。
4. **サロゲートペアが境界で割れない**(境界にペアが跨る fixture)。

### ミューテーション検証(スポット・ユーザー承認済み)

網 2 と 4 に対してのみ、次の 2 変異が殺せることを確認する:

| 変異 | 期待 |
|---|---|
| `text.Length <= MaxCharsPerTextOp` → `<` | 網 1(ちょうど上限の行)が落ちる |
| `SnapToCodePointStart` の呼び出しを落とす(`to = want`) | 網 4 が落ちる |

### 検証

```powershell
dotnet test tests/kxEdit.Core.Tests -c Release --filter "FullyQualifiedName~FrameBuilder"
```

---

## Task 3 — F-1: 選択色分け経路にも同じ分割を掛ける

### 変更

`EmitBodyTextWithSelection` の内側:

- 「ピクセル幅 0 の選択」で 1 op に倒す枝 → `EmitBodyRun` 経由にする。
- `EmitRun`(prefix / 選択内 / suffix)→ `EmitBodyRun` 経由にする。
  ただし **X は既存どおり `OffsetToPx` の差分で決める**(選択矩形との整合のため)。
  分割はその run の**内側**で行うので、run の開始 X は変わらない。

```csharp
        void EmitRun(int charFrom, int charTo, int pxFrom, int pxTo, PaintColor color)
        {
            if (charFrom >= charTo)
                return;
            // 幅は今までどおり OffsetToPx の差分(選択矩形と同じ出し方)。
            // 分割が起きるのは MaxCharsPerTextOp を超える run だけで、そのときは
            // EmitBodyRun 側の累積幅で内部の X が決まる(run の開始 X は変わらない)。
            EmitBodyRun(span[charFrom..charTo], bodyX + pxFrom, yPx, lineHeight, color, metrics, ops);
        }
```

> `pxTo - pxFrom` を捨てて `EmitBodyRun` の計測に任せると、**選択矩形と幅がずれる**。
> 分割しない run(= 大多数)では `EmitBodyRun` が `metrics.MeasureRun` で測るので、
> **既存の `pxTo - pxFrom` と一致しない可能性がある**。ここは要注意で、
> **分割しないときは従来どおり `pxTo - pxFrom` を使う**形にする(下の実装で分岐する)。

実装は `EmitBodyRun` に `widthOverride`(null なら自前計測)を足して解決する:

```csharp
    private static void EmitBodyRun(
        ReadOnlySpan<char> text,
        int xPx,
        int yPx,
        int lineHeight,
        PaintColor fore,
        ICharMetrics metrics,
        List<PaintOp> ops,
        int? widthOverride = null
    )
```

- 分割しない枝では `widthOverride ?? metrics.MeasureRun(text)` を使う
  = **選択経路の既存の幅(OffsetToPx の差分)がそのまま残る**。
- 分割する枝では累積幅で各チャンクの X と幅を出す(override は使えない)。

### 網(`tests/kxEdit.Core.Tests/Layout/FrameBuilderSelectionForeTests.cs` へ追加)

5. 選択色分けがある長大行で、**prefix / 選択内 / suffix それぞれの内側が分割**され、
   3 色の並びが保たれること(色の順序と連結一致)。
6. **既存テストが 1 本も落ちないこと**(分割しない run の幅が変わっていないことの証人)。

### 検証

```powershell
dotnet test tests/kxEdit.Core.Tests -c Release --filter "FullyQualifiedName~FrameBuilder"
dotnet test kxEdit.sln -c Release
```

---

## Task 4 — 最終ブランチレビュー 2 パス(別エージェント)

CLAUDE.md §3-5。コード品質パスと脆弱性パスを**別々のエージェント**で起動する。

## Task 5 — 品質ゲートと L5 再実施

```powershell
pwsh -File tools/pre-merge-check.ps1     # EXIT 0
```

そのあと設計書 §4 の再実施を行い、結果を
`docs/plans/2026-09-14-v0.2-l5-consolidated.md` へ**追記**する(判定の書き換えはしない)。
