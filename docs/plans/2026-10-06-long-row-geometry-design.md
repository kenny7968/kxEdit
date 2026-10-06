# 長い行の座標と可視範囲の描画 設計書

策定日: 2026-10-06
策定ブランチ: `feature/long-line-paint-cost`
前提資料
- `docs/plans/2026-09-27-perf-followups-design.md`(以下「傘」)§13(フェーズ 9)と §1(F-1 の案 B を範囲外とし、「フェーズ 9 の計測で必要と分かった場合は、その時点で提案する」)
- `docs/plans/2026-09-14-l5-followup-fixes-design.md`(以下「F-1 設計書」)§2(案 A = 長い行を 16,384 字ごとの op に割る。案 B = 可視範囲だけを出す)
- `docs/plans/2026-08-02-large-line-resilience-design.md`(長大行の計測コスト)

傘 §3.1 の規約(新しい抽象や seam を伴う修正は新しい日付の設計書に書く)に従い、本書に書く。フェーズ 9 の 17・18・19 の結論は、傘の実施記録(§13 の末尾)に書く。

## 1. 背景(フェーズ 9 の調査で分かったこと)

計測は 2026-10-06。NVDA 起動中、Release ビルド。scratchpad の計測ビルドで、`FrameDiff.Describe` などに Stopwatch を入れて測った。製品コードは変えていない。

### 1.1 長大行の打鍵が重い

Smoke `--perf` に長大行の打鍵シナリオ S11(1M 字の 1 行・折り返し OFF・行頭付近で 1 文字挿入と BackSpace を交互に行う)を足して測った。

| 文書 | 1 打鍵 | うち描画(`PaintAndRecord`) | うち行の記述子の本文(項目 17) |
|---|---|---|---|
| 英字 1M 字 | 約 135 ms | 126 ms | 0.19 ms(0.14%) |
| 日本語 1M 字 | 約 2.7 秒 | 2.7 秒 | 約 1 ms(0.04%) |

- 項目 17 の判断基準(打鍵 1 回の描画全体の 1 割)を大きく下回るので、17 と 19 は閉じる(傘の実施記録に書く)。
- 重いのは描画の本体だった。dotnet-trace によると、`TextRenderer.DrawText` が約 6 割、`GdiCharMetrics.MeasureRun`(`FrameBuilder.EmitBodyRun` の区切りごとの計測)が 5 割強を占める。F-1 の案 A は、行を 16,384 字ごとに区切って**全部の区切り**を測り、全部を描く。画面の外の区切りも含む。
- 行全体の `GetText` は 1 打鍵に 6〜7 回あるが、合計で 1〜6 ms にとどまる。

### 1.2 非 ASCII の長い行では横スクロールが効かない(新しく見つけた不具合)

`TextRenderer.MeasureText` は、**43,679 字を超える文字列を測ると、エラーを出さずに幅 0 を返す**(2026-10-06 実測。F-1 の描画の上限と同じ Uniscribe の制約)。

| 文字列 | 幅 |
|---|---|
| `a` × 43,000 | 344,000 |
| `a` × 44,000 | **0** |
| `吾` × 43,000 | 688,000 |
| `吾` × 44,000 | **0** |

`GdiCharMetrics.MeasureRun` は、ASCII だけの run を文字幅の表の足し算で測る。非 ASCII を含む run は GDI で一括して測る。そのため、非 ASCII を含み 43,679 字を超える run の幅は 0 になる。この run を行頭から測っている箇所が壊れる。

- **横スクロールバー**(`EditorControl.UpdateHorizontalScrollbar` が可視行の全体を測る): 行の幅が 0 になり、横スクロールバーが出ない。実測では、日本語 1M 字の行でキャレットを 30,000 字目・行末に置いても `ScrollX` は 0 のままだった。英字では正しく追従した。
- **キャレットの X**(`PixelMapper.OffsetToPx` が行頭からの prefix を一括で測る): 43,679 字目より右で 0 になる。
- **マウスのクリック**(`PixelMapper.PxToOffset` が最初に行全体を測る): 行全体の幅が 0 になり、どこをクリックしても行末になる。
- **選択矩形・上下移動の桁の維持・UIA の矩形**: どれも `OffsetToPx` を通るので、同じく壊れる。

結果として、非 ASCII の長大行では、最初の 1 画面より右を**晴眼・弱視ユーザーが見られない**。SR の読み上げは正常なので、これまで気付かれていなかった(F-1 と同じ形)。

### 1.3 空白の可視化が長い行で 2 乗のコストになる

`FrameBuilder.EmitWhitespaceGlyphs` は、空白 1 つごとに `PixelMapper.OffsetToPx(span, i)`(行頭からの prefix の計測)を呼ぶ。空白の多い長い行で空白の表示を ON にすると、1 フレームの費用が行の長さの 2 乗に比例する。

## 2. 決定事項(ブレインストーミングでの合意・2026-10-06)

| 論点 | 決定 |
|---|---|
| F-1 の案 B の扱い | フェーズ 9 に含めて直す(本ブランチ・本 PR) |
| 範囲 | 描画のコストだけでなく、§1.2 の座標の不具合も一緒に直す |
| 方式 | 長い行は、コードポイントごとの幅の**足し算**で座標を決める(下の §3)。区切りの幅をメモ化して一括計測を続ける案は採らない(行頭付近の打鍵で後ろの区切りの中身がすべてずれ、メモが効かない。横スクロールバーには行全体の幅が要るので、日本語 1M 字で 1 打鍵あたり約 60 区切り × 約 1 ms の GDI 計測が残る) |

## 3. 設計

### 3.1 用語

- **長い行**: 長さ(UTF-16 の単位数)が `PixelMapper.LongRowThreshold` を超える視覚行。閾値は `FrameBuilder.MaxCharsPerTextOp`(16,384)と同じ値にする(定数を参照して二重に持たない)。
- **短い行**: それ以外。通常の文書の行はすべてこちら。**短い行の座標・op 列は一切変えない**。

折り返し ON の視覚行は折り返し幅で切られるので、通常は短い行になる。長さだけで判定するので、折り返し ON で長い行ができた場合も同じ規則に従う。

### 3.2 幅の足し算(`ICharMetrics` の新しいメンバー)

```csharp
/// <summary>
/// <paramref name="text"/> の幅を、コードポイントごとの幅の和で返す(長い行の座標用。設計書 2026-10-06 §3.2)。
/// <see cref="MeasureRun"/> の一括計測とは一致しないことがある(カーニング・合成)。
/// </summary>
int MeasureAdditive(ReadOnlySpan<char> text)
{
    // 既定実装: 1 コードポイントずつ MeasureRun で測って足す。
}
```

- 既定実装を持たせるので、`MonoCharMetrics` とテストの偽のメトリクスは変えなくてよい。
- `GdiCharMetrics` は高速版で上書きする。
  - ASCII は既存の `_asciiWidths` で引く。
  - BMP の非 ASCII は、`char` で引く配列(要素 65,536・未計測は -1)で引く。未計測なら既存の `CachedCodePointWidth` で測って配列に入れる。配列は初めて要るときに作る(1 インスタンス 256KB。フォントを差し替えると、インスタンスごと捨てられる)。
  - サロゲートペアは既存の `CachedCodePointWidth`(Dictionary)で引く。単独サロゲートは長さ 1 のコードポイントとして扱う(`TextBoundary.CodePointLengthAt` の既存の規約)。
- 返す値は、既存の 1 コードポイントの計測(`MeasureRun` の単一コードポイントの経路)の和と一致する。新しい計測値は作らない。
- UI スレッド専用(`GdiCharMetrics` の既存の契約のまま)。

### 3.3 座標の芯(`PixelMapper`)

短い行は今の実装のまま。長い行だけ、次のようにする。

| メソッド | 長い行での挙動 |
|---|---|
| `OffsetToPx(seg, off)` | 既存どおり前方スナップした `off` について、`MeasureAdditive(seg[..off])` |
| `PxToOffset(seg, px)` | 先頭の `MeasureRun(segment)`(一括計測。長大行では 0 になる)を使わない。既存の「1 コードポイントずつ累積して歩く」処理だけで求める。歩き切ったら `segment.Length` |
| `RowWidthPx(seg)`(新設) | `OffsetToPx(seg, seg.Length)`。短い行では `MeasureRun(seg)` と同じ値 |
| `SliceForWindow(seg, leftPx, rightPx)`(新設) | 窓 `[leftPx, rightPx)`(行頭が x = 0)にかかる文字範囲 `[Start, End)` と、`Start` の X を返す。下の規則 |

`SliceForWindow` の規則(長い行でだけ呼ぶ)
- `Start`: X の範囲 `[x(c), x(c) + w(c))` が `leftPx` を含むコードポイント `c` の先頭。`leftPx <= 0` なら 0。
- `End`: X の開始が `rightPx` 以上になる最初のコードポイントの、**次の**コードポイントの先頭(右端からはみ出すグリフのための 1 つの余裕)。行末を超えない。
- `Start >= End` になりうる(窓が行末より右)。そのときは本文を出さない。
- 戻り値の `StartPx` は `OffsetToPx(seg, Start)` と一致する。
- 境界はコードポイントの境界になる(サロゲートペアを割らない)。
- 費用は O(`End`)。窓より右は歩かない。

`EmitBodyTextWithSelection` の「`OffsetToPx` と同じ前方スナップに依存する」注意は、そのまま成り立つ(スナップ方針は変えない)。

### 3.4 描画(`FrameBuilder.Build`)

- 引数に横の窓を足す: `int viewLeftPx, int viewWidthPx`。`viewLeftPx` は Frame の X 座標での窓の左端(= 水平スクロール量 `scrollX`)、`viewWidthPx` は描画幅。
- **短い行では窓を使わない**。op 列は今と同じになる。
- **長い行**(工程 5 と 6)
  - 窓の範囲を行頭基準に直す: `[viewLeftPx - bodyX, viewLeftPx + viewWidthPx - bodyX)`。
  - `SliceForWindow` で `[s, e)` と `x(s)` を求め、本文は `[s, e)` だけを出す。X は `bodyX + x(s)`。`[s, e)` が `MaxCharsPerTextOp` を超える場合は、既存の `EmitBodyRun` の分割をそのまま使う。
  - 選択の文字色で分割するテーマでは、選択の行内範囲を `[s, e)` と交差させる。その上で既存の 3 run の分割を `[s, e)` の中で行う。run の X と幅は `OffsetToPx` の差分(長い行では足し算)で出す。
  - 空白の可視化は `[s, e)` の中だけ出す。X は `x(s)` から 1 コードポイントずつ累積する(prefix を測り直さない)。
- 工程 3・4・8(選択矩形・セル強調)は `OffsetToPx` を通るので、長い行でも足し算の座標で正しくなる。矩形は今と同じく行頭基準の座標で出す(窓で切らない。§6 の申し送りを参照)。
- `RowsTouching`・行番号・現在行の強調は変えない。

### 3.5 Editor 側

- `PaintBody` は `inputs.ScrollX` と `inputs.PaintWidth` を `FrameBuilder.Build` に渡す。
- `UpdateHorizontalScrollbar` の `_metrics.MeasureRun(lineText)` を `PixelMapper.RowWidthPx(lineText, _metrics)` に替える。
- 差分の無効化(`InvalidateChangedRows`)は変えない。窓は `ScrollX` と `PaintWidth` で決まり、どちらもすでに描画の入力(`FrameInputs`)に入っている。`ScrollX` が変われば、全面か画素の移動 + 露出した帯の描画になる。`PaintWidth` は `SameLayoutAs` に入っている。したがって、記述子が同じ行は同じ絵になるという不変条件は保たれる。
- キャレット・マウス・上下移動・UIA の矩形は、`PixelMapper` を通るので変えない。

### 3.6 意図的な挙動差(PR に記載する)

- 長い行(16,384 字を超える視覚行)の座標は、コードポイントの幅の足し算になる。等幅の既定フォント(ＭＳ ゴシック)では、一括計測と同じ値になる(全角 1,000 字 = 16,000 px、半角 1,000 字 = 8,000 px で確認)。比例フォントや結合文字では、16,385〜43,679 字の非 ASCII の行で、今より数 px ずれうる。F-1 設計書 §2.4 で受け入れた区切りの継ぎ目のずれと同じ種類のものである。
- 43,679 字を超える非 ASCII の行で、横スクロールバー・キャレットの X・クリック・選択矩形・上下移動・UIA の矩形が**正しく動くようになる**(§1.2 の不具合の修正)。
- 長い行で、画面の外の文字の `DrawText` を出さない(`TestHook_GetLastFrame` で見える op 列が変わる。本番コードで読む箇所はない)。

## 4. テスト

### 4.1 Core(`kxEdit.Core.Tests`)

偽のメトリクス `GdiLikeMetrics` を足す。1 文字の幅は、ASCII が 8・それ以外が 16 とする。`MeasureRun` は、非 ASCII を含み 43,679 字を超える run に 0 を返す(GDI の挙動を写す)。`MeasureAdditive` は既定実装を使う。

- `PixelMapper`
  - 短い行(閾値ちょうど)で、`OffsetToPx`・`PxToOffset`・`RowWidthPx` が `MeasureRun` を使った今の値と一致する(閾値の境界は定数から導く)。
  - 長い行(閾値 + 1)で、`OffsetToPx` が足し算の値になる。
  - 長い行(5 万字の日本語)で、行末の `OffsetToPx` と `RowWidthPx` が 0 にならず、足し算の値になる(今は 0 = 赤)。
  - 長い行の `PxToOffset` が、`OffsetToPx` の逆になる(コードポイントの途中の px は、そのコードポイントの直後)。
  - `SliceForWindow`: 窓の左端がコードポイントの途中にあるとき、`Start` はそのコードポイントの先頭になる。`End` は 1 つの余裕を含む。窓が行末より右なら空。サロゲートペアを割らない。
- `FrameBuilder`
  - 短い行の op 列が、窓の値によらず今と同じになる(非既定の窓から確かめる)。
  - 長い行で、本文の op の Text を連結すると `row[s..e)` になり、最初の op の X が `bodyX + x(s)` になる。窓が行頭にあるときは行の先頭から出る。
  - 長い行の選択の文字色の分割が、窓の中だけで 3 run になる(prefix と suffix が窓の外に出る fixture にする)。
  - 長い行の空白の可視化が、窓の中の空白だけを、足し算の X で出す。

### 4.2 Editor(`kxEdit.Editor.Tests`)

実物の `EditorControl` と `GdiCharMetrics`(ＭＳ ゴシック)を使う。

- 日本語 5 万字の 1 行で、横スクロールバーが出る。キャレットを行末に置くと `ScrollX` が 0 より大きくなり、キャレットが可視になる(今は赤)。
- 同じ行の可視範囲を描いた `TestHook_GetLastFrame` の本文の op が、窓の中の文字だけになる。
- `GdiCharMetrics.MeasureAdditive` が、ASCII・BMP の非 ASCII・サロゲートペア・単独サロゲートで、1 コードポイントずつの `MeasureRun` の和と一致する。

### 4.3 計測(完了条件)

- S11(英字・日本語の 1M 字の行)を、変更前と変更後で 3 回ずつ測り、中央値を比べる。両方とも、変更前の揺れ(3 回の最小〜最大)を超えて下がること。下がらなければ原因を調べる。
- S3・S7(ja10k・en10k)が悪化しないこと(短い行の経路は変えないので、揺れの範囲に収まる見込み)。

### 4.4 L5・目視

`kxEdit.Accessibility` は変えないが、UIA の矩形(`ComputeCaretPointForUia`)が `PixelMapper` を通るので、L5 を行う。

- PrintWindow による実解像度の目視: 日本語・英字の長大行を、行頭・中ほど・行末で見る。選択・空白の表示・ハイコントラストテーマ(選択の文字色の分割)も見る。キャレットが文字の境目にあること、選択矩形と文字の色が合っていること。
- Smoke `--paint-transition` が EXIT 0。
- `tools/sr-regression.ps1` が EXIT 0。
- NVDA の簡易確認: 長大行で文字単位の移動の読み上げが変わらないこと。行の途中で、NVDA のハイライト矩形がキャレットの位置に出ること。

### 4.5 変異検証

`PixelMapper` はキャレットと選択範囲の算出なので、CLAUDE.md §4-A の有効側に当たる。ユーザーのグローバル規約(原則実施しない)の例外として、スポットで 2 個行う。

- 閾値の比較の off-by-one(`>` と `>=` の入れ替え)
- 長い行の `PxToOffset` の境界(`accumulated + cpWidth >= px` の `>=` を `>` にする)

注意: `--no-build` やビルドの失敗で古い DLL が走ると、変異が当たらずに「生存」に見える。変異のたびにビルドの成功を確かめる。

## 5. レビュー

- `ICharMetrics.MeasureAdditive` と `PixelMapper` の長い行の経路は、後続(描画・横スクロールバー)が乗る新しい seam なので、タスク時に**前倒しのコード品質レビュー**を行う。
- 外部入力のパース・パス操作・プロセス起動・WebView・ネットワークには触れないので、前倒しの脆弱性レビューは不要。最終レビューの脆弱性パスは行う(CLAUDE.md §3 の 5)。

## 6. やらないこと・申し送り

- **行全体の `GetText`**(1 打鍵に 6〜7 回)は減らさない。1M 字で合計 1〜6 ms であり、本書の対象(GDI の計測と描画)より 2 桁小さい。
- **GDI の座標の範囲**: 選択矩形・セル強調は、行頭基準の X(`scrollX` を引く前)で出す。全角で約 800 万字を超える行では、GDI の座標の範囲(約 2^27 px)を超える。窓で切ると、セル強調の枠の左辺・右辺が窓の端に出てしまうので、本書では扱わない。
- **足し算の座標と描画のずれ**: 窓の中の本文は 1 回の `DrawText` で描くので、GDI の中ではカーニングや合成が効く。比例フォントでは、窓の中で足し算の X とグリフの位置が数 px ずれうる(長い行だけ)。
- 短い行の座標(一括計測)は変えない。

## 7. 実施記録(2026-10-07)

実装計画 `docs/plans/2026-10-06-long-line-paint-cost.md` の Task 1〜5 で行った。傘の実施記録は `2026-09-27-perf-followups-design.md` §13.3。

- **計測**(§4.3。NVDA 起動中・3 回の中央値)

  | シナリオ | 変更前 | 変更後 |
  |---|---|---|
  | S11a 英字 1M 字 | 126.8 ms | 2.45 ms |
  | S11b 英字 1M 字 | 127.4 ms | 2.61 ms |
  | S11a 日本語 1M 字 | 2,847 ms | 8.6 ms |
  | S11b 日本語 1M 字 | 2,831 ms | 8.5 ms |

  S3・S7 は、ja の S3 の中央値が変更前の範囲の上限を 0.01〜0.05 ms 上回った。それ以外は揺れの範囲に収まった。
- **L5**(§4.4): すべて PASS。`--paint-transition --expect-skip`・`sr-regression`・`pre-merge-check` は EXIT 0。目視は実アプリを PrintWindow で撮った。NVDA は文字単位の読み上げを確かめた。NVDA のハイライト矩形は、ユーザーの実機確認に回した。
- **変異検証**(§4.5): 2 個とも殺された。
- **本書からの精密化**
  - **§3.4 の本文**: 長い行の本文の run をタブで区切り、各区間を足し算の X に置く。GDI はタブを幅 0 で測って描くが、`MeasureAdditive` はスペース幅で数える。そのため、1 回の `DrawText` で描くと、タブのたびに文字が足し算の X より左へずれた。L5 で空白のグリフの位置のずれとして見つかった。タブはスペース幅の空きとして描く。
  - **§3.6 に 1 行足す**: 長い行のタブはスペース幅の空きとして描かれる。短い非 ASCII の行では、従来どおり幅 0 で描かれる。
  - **§3.4 の窓の引数**: 省略可能(既定は窓なし)にした。理由は実装計画 §0.5。
  - **§4.2 の Editor のテスト**: 表示した窓(`HostForm.CreateVisible`)で行う。表示しない Form では横スクロールバーが出ず、修正後も赤のままになるため。
  - **既存のテストの期待値を 1 件変えた**: `FrameBuilderSelectionForeTests.Run_at_exactly_the_limit_keeps_the_prefix_difference_width`。16,393 字の行が長い行になり、足し算の座標になるため(§3.6 の挙動差)。`MaxCharsPerTextOp` の比較を緩める変異は、引き続きこのテストで殺される。
