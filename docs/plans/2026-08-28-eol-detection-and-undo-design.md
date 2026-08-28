# 改行コード判定窓と EOL 変換の Undo 消失(A-9 / A-11)設計書

- 日付: 2026-08-28
- 対象: [v0.2 リリース前バグ監査](./2026-08-22-v0.2-release-bug-audit.md) §4 の **A-9** / **A-11**
- ブランチ: `feature/eol-detection-and-undo`

---

## 1. 目的

上書き保存で本文が無警告に書き換わり、しかも Undo で戻せない導線を塞ぐ。

- **A-9** — 改行コード判定が読み込み後バッファの先頭 4,096 文字だけを見るため、
  1 行目が 4,096 文字を超える LF ファイル(ミニファイ JSON・長いヘッダ行の CSV)が
  CRLF と誤判定される。Ctrl+S で全行が CRLF 化されるが、Modified は立たず警告も出ない。
- **A-11** — 保存経路の `ConvertEols` が非 fast-path で `ReplaceSource(builder.Build())` を呼び、
  新しい `TextBuffer` に差し替わるため **Undo/Redo 履歴が全消去**される。
  CRLF 文書に LF 混じりを貼って Ctrl+S すると、直後の Ctrl+Z が無反応になる。

2 件を 1 ブランチにまとめる根拠は、症状が 1 本の線でつながることにある
(A-9 が誤った EOL 変換を起動し、A-11 がその結果からの復旧路を奪う)。
A-9 が引き金を減らし、A-11 が踏んだ後の復旧路を作る、という多層防御の関係にある。
一方で修正面は Core の検出側と Editor / App の変換側に分かれるため、タスクは分離できる(§6)。

---

## 2. 現行 main での実在確認(2026-08-28)

### 2.1 A-9

`src/kxEdit.Core/Text/TextFileService.cs:187-192`:

```csharp
// 4) LineEnding 検出。バッファ先頭 4KB を GetText して LineEndingDetector に流す
//    (空バッファなら 0 バイト=CRLF 既定)。
var snap = buffer.Current;
int probeChars = Math.Min(4096, snap.CharLength);
string lineProbe = probeChars > 0 ? snap.GetText(0, probeChars) : string.Empty;
LineEnding eol = LineEndingDetector.Detect(lineProbe);
```

`LineEndingDetector.Detect`(`src/kxEdit.Core/Text/LineEnding.cs`)は
`crlf == 0 && lf == 0 && cr == 0` のとき `LineEnding.Crlf` を返す。
窓内に改行が 1 つも無い文書は、実際の改行が何であれ CRLF と判定される。

この窓は P6 Task 10 の Stream 化で入った退行である(旧 `DecodeBytes` は全文判定だった)。
監査は「CSV-L-2『EOL 検出 4KB 窓』として受容済みだが、無警告の全行書換は受容範囲を超える」と
評価している。

### 2.2 A-11

`src/kxEdit.Editor/EditorControl.cs:447-565` の `ConvertEols` は、
fast-path(`IsEolAlreadyUniform` が true)でなければ `TextBufferBuilder` で
新しい `TextBuffer` を組み、`ReplaceSource(builder.Build())`(`:545`)で差し替える。

`ReplaceSource`(`:274-309`)は `_buffer = buffer;` でバッファ参照ごと置き換えるため、
新バッファが持つ `UndoHistory` は空のインスタンスになる。旧履歴は到達不能になって消える。

保存経路は `App/FileController.cs:843` の `doc.Editor.ConvertEols(doc.Editor.EolMode)`。
その直後に `SetSavePoint()` が走るので Modified も立たない。

### 2.3 A-11 を安く直せる根拠(実装調査で判明した事実)

`UndoHistory`(`src/kxEdit.Core/Buffer/UndoHistory.cs`)のエントリは
**永続木の root 参照 + 位置情報だけ**を持つ:

```csharp
internal readonly record struct Entry(
    PieceTree.Node? RootBefore,
    PieceTree.Node? RootAfter,
    int Pos,
    int RemovedLen,
    int InsertedLen
);
```

`TextBuffer.Undo()` は `_current = new TextSnapshot(e.Value.RootBefore);` するだけである。
したがって **全文 EOL 変換を 1 エントリとして記録するコストはほぼゼロ**(テキストの実体化も
差分計算も不要)。`ConvertEols` はすでに変換後の木を `TextBufferBuilder.Build()` で
手に入れているので、その root を「1 Undo 単位の編集」として現在のバッファへ記録すればよい。

---

## 3. 決定した方針(2026-08-28・ユーザー承認済み)

| 項目 | 決定 | 却下した対案と理由 |
|------|------|------------------|
| A-9 の検出範囲 | **全文バイトスキャン**。4,096 文字窓を撤廃し、PieceTree を byte 走査して CRLF / LF / CR を数える | 「改行 0 件のときだけ窓を延長する」= 報告症状しか塞がず、混在ファイルの誤判定が残る。「デコード中に数える」= 追加パスは無くせるが `LoadAsBuffer` の戻り値と責務が膨らむ |
| A-11 の直し方 | **`ConvertEols` を 1 Undo 単位の編集にする**。`TextBuffer` に構築済み root を記録付きで差し替える API を足し、`ReplaceSource` をやめる | 「書き出し側で EOL 変換してバッファを触らない」= M-31 系の副作用まで一掃できるが、保存後もメモリ上は EOL 混在のまま残る**挙動変更**になる(§7 で申し送り)。「ReplaceSource 時に旧履歴を移植」= 変換で char オフセットが変わるため Undo が壊れる |

監査 A-11 が引く「Scintilla の `SCI_CONVERTEOLS` は undo 可だった」という基準は、
採用案(バッファは変換され、その変換を Undo で戻せる)と一致する。

---

## 4. 設計 — A-9(検出範囲)

### 4.1 変更点

`LineEndingDetector` に `TextSnapshot` を直接受ける判定を足し、`TextFileService` はそれを呼ぶ。
`string` を受ける既存 `Detect(string)` は他所からも使われうるので**残す**(削らない)。

- **新**: `LineEndingDetector.Detect(TextSnapshot snap)` — PieceTree を byte 走査して
  CRLF / LF / CR を数え、既存 `Detect(string)` と**同じ多数決規則**で `LineEnding` を返す。
- **変更**: `TextFileService.LoadAsBufferAuto` の 4)節を `LineEndingDetector.Detect(snap)` に置換。

判定規則は現行と厳密に同一に保つ(移すのは走査範囲だけで、多数決の意味論は変えない):

- 3 種すべて 0 件 → `Crlf`(空ファイルが CRLF 既定になる現行挙動は維持する。
  `LoadAuto_EmptyFile_UsesUtf8Default_AndReturnsEmptyBuffer` が固定している)
- `crlf >= lf && crlf >= cr` → `Crlf`
- それ以外は `lf >= cr` なら `Lf`、さもなくば `Cr`

### 4.2 走査の形

`EditorControl.IsEolAlreadyUniform`(`:601-662`)と同型の byte 走査にする。
`PieceTree.Enumerate(snap.Root)` で piece を辿り、`piece.Chunk.Span.Slice(...)` を 1 byte ずつ見る。
`string` の実体化をしないので、512MB 上限の文書でもピーク メモリは増えない。

**ピース境界の CR は `pendingCr` で持ち越す**。piece の末尾が `0x0D` のとき、
次 piece の先頭が `0x0A` なら CRLF 1 件、そうでなければ CR 単独 1 件として数える。
全 piece 走査後に `pendingCr` が残っていれば文書末尾の単独 CR = CR 1 件。
この持ち越しを落とすと、4MB チャンク境界に CRLF が落ちた文書で CRLF が CR + LF に化けて
多数決が反転しうる(`ConvertEols` が同じ罠を `pendingCr` で回避している)。

UTF-8 では `0x0D` / `0x0A` はマルチバイト文字の継続バイトとして出現しない(継続バイトは
すべて `0x80` 以上)ので、byte 走査と char 走査は同じ結果を返す。

### 4.3 コスト

読み込み済みバッファに対する 1 パスの byte 走査が増える。
`LoadAsBufferAuto` はすでにファイル全体をストリームで読み・デコードし・
`TextBufferBuilder` が全 byte を `Utf8Sanitizer.Sanitize` に通している。
そこへメモリ上の 1 パスが増えるだけなので、相対コストは小さい。
P6 Task 10 以前(`DecodeBytes` 全文判定)へ戻す変更でもある。

### 4.4 A-9 修正後も残るもの(意図的)

EOL が**混在**しているファイルは、修正後も多数決で決まった EOL に保存時へ黙って統一される。
これは A-9 の対象外(長年の挙動)であり、本ブランチでは変えない。
ただし A-11 の修正により、その変換は **Ctrl+Z で戻せる**ようになる。
A-9 が「誤った引き金」を消し、A-11 が「踏んだ後の復旧路」を作る、という関係になる。

---

## 5. 設計 — A-11(EOL 変換を Undo 可能にする)

### 5.1 Core: 記録付きの全文差し替え API

`TextBuffer` に、構築済みの root を 1 Undo エントリとして記録しつつ現在スナップショットへ
差し替える public API を足す(名称は実装時に確定。本書では仮に `ReplaceAllRecordingUndo` と呼ぶ)。

契約:

- 引数は `TextBufferBuilder.Build()` が返した `TextBuffer`(= 変換後の木の持ち主)。
  root だけを取り込む。`PieceTree.Node` は Core 内部型なので、public シグネチャに露出させない。
- `_history.Record(rootBefore, newRoot, pos: 0, removedLen: 旧 CharLength, insertedLen: 新 CharLength,
  insertHasBreak: true)` を呼ぶ。`insertHasBreak: true` にするのは coalescing を必ず切るため
  (EOL 変換は「≤2 文字の連続タイピング」ではないので、直前のタイプ操作へ融合させてはならない)。
- `_current = new TextSnapshot(newRoot)` で差し替える。
- `_savedRoot` は**触らない**。これにより「保存点に Undo で戻ると Modified が false に戻る」
  という既存の参照比較セマンティクスがそのまま効く。
- `MaxTotalBytes` 上限: EOL 変換で総 byte 数は増えうる(LF → CRLF)。
  ただし木は `TextBufferBuilder` 側ですでに構築済みで、`TextBufferBuilder` 自身が
  `MaxTotalBytes` 判定と `DocumentTooLargeException` を持つ。二重判定にせず、
  ビルダー側のガードに委ねる(実装時に上限の重複判定が無いことを確認する)。

**別チャンク由来の root を取り込んでよい根拠**: `TextBuffer._append`(`AppendBuffer`)は
新規挿入テキストの置き場でしかなく、`Piece` は自分の `Chunk` 参照を持つ。
`PieceTree` の操作は `Piece.Stats` のモノイド結合だけで完結し、
「全 piece が `_append` 由来である」ことをどこも仮定していない。
`TextBufferBuilder` が作る `TextChunk` は不変で、root から到達可能な限り生存する。
**この不変条件は実装タスクで検証してから依存する**(§6 Task 2 の受け入れ条件)。

### 5.2 Editor: `ConvertEols` の切替

`EditorControl.ConvertEols` の `ReplaceSource(builder.Build());`(`:545`)を
新 API 経由の in-place 差し替えに置き換える。**保たなければならない現行契約**:

| 契約 | 現状の担い手 | 切替後 |
|------|------------|-------|
| caret / anchor の論理位置復元 | `(m, k)` 分解 → `SetSelection`(`:549-554`) | そのまま維持(バッファ参照は変わらないが `_caretCtrl` の再設定は必要 — 変換で char 位置がずれるため) |
| `_topLine` / `_topSegment` / `_scrollX` 復元 | `SetTopPosition` + `ScrollX`(`:556-557`) | そのまま維持 |
| system caret 再配置 | `PositionCaret()`(`:562-563`) | そのまま維持 |
| UIA スナップショット更新 | `ReplaceSource` 内の `_uia.OnSnapshotChanged` | **明示的に呼ぶ**(`ReplaceSource` を通らなくなるため) |
| UIA `TextChanged` 発火 | `ReplaceSource` 内の `_uia.RaiseTextChanged()` | **明示的に呼ぶ** |
| `UpdateUI` 発火 | `ReplaceSource` 内 | **明示的に呼ぶ**(ステータスバー更新契機) |
| スクロールバー同期 / `Invalidate` | `ReplaceSource` 内 | **明示的に呼ぶ** |
| `_cellHighlight` 無効化 | `ReplaceSource` 内 | **維持する**(EOL 変換でセルのオフセットは動く) |
| IME 未確定の確定キャンセル | `ReplaceSource` 冒頭の `if (IsComposing) CancelCompositionAndDefault()` | **維持する** |

**意図的に変える点が 1 つある** — `ReplaceSource` は `_caretCtrl.SetTo(0, ...)` で
キャレットを一度 0 に潰し、`RaiseUiaSelectionEvents` が有効なら
`_uia.RaiseSelectionChanged()` を **caret=0 の状態で**発火する。
監査 A-11 が「副作用として UIA `SelectionChanged` が caret=0 で先に飛ぶ」と書いている挙動である。
in-place 化するとこの中間状態が消えるので、**caret 復元後に 1 回だけ**
`SelectionChanged` を発火する形になる。これは A-11 が指摘した副作用の解消であり、
挙動変更として PR description に明記する。SR の実発声への影響は L5 でのみ判定できる。

fast-path(`IsEolAlreadyUniform` が true)は現状のまま変更しない。

### 5.3 App: 保存失敗ロールバックの置き換え

**これが本ブランチで最も慎重な扱いを要する箇所である。**

`FileController.WriteToPath`(`src/kxEdit.App/FileController.cs:821-892`)は、
保存失敗時に本文と保存点を戻すため

```csharp
var snapshotBefore = doc.Editor.CurrentBuffer;   // ConvertEols 前のバッファ参照を握る
...
if (!ReferenceEquals(doc.Editor.CurrentBuffer, snapshotBefore))
    doc.Editor.SetOrReplaceSource(snapshotBefore);
```

という機構を持つ。これは「`ConvertEols` が**バッファ参照ごと差し替える**ので、
旧参照の `_savedRoot` / `_current` は無傷のまま残っている」という前提に**全面的に依存**している。
in-place 化するとこの前提が崩れ、**ロールバックが黙って no-op になる**
(`ReferenceEquals` が常に true になるため)。放置すると
「保存に失敗したのに本文の EOL は書き換わったまま」という silent な状態が残る。

置き換え方針: `ConvertEols` が積んだ Undo エントリを 1 つ戻す。
`EditorControl` 側に「直前の EOL 変換を取り消す」導線を用意し、
`WriteToPath` の catch 節はそれを呼ぶ。要件:

- **fast-path で何も積んでいない場合は no-op でなければならない**。
  `ConvertEols` が変換を行ったかどうかを呼び出し元が知る必要がある
  (`ConvertEols` の戻り値化、または `WriteToPath` 側で fast-path 判定を持つ)。
  誤って 1 つ余分に Undo すると、**保存失敗時にユーザーの直前の編集が消える**。
- 保存点(`Modified`)も一緒に戻ること。`_savedRoot` を触らない設計(§5.1)なので、
  root が変換前に戻れば参照比較で `Modified` も自動的に元へ戻る。
- Redo スタックを汚さない扱いを決める(`PopUndo` は `_redo` へ積む)。
  保存失敗のロールバックが Ctrl+Y で「やり直せる」のは不自然なので、
  実装時に Redo を捨てるか否かを決めて設計書へ追記する。

`WriteToPath` の XML doc コメント(`:812-819`)は旧機構を詳細に説明しているので、
新機構の説明へ**書き換える**(古い説明を残すと次の読者を誤導する)。

---

## 6. タスク分割

| # | 内容 | 層 | 追加レビュー |
|---|------|-----|------------|
| 1 | A-9: `LineEndingDetector.Detect(TextSnapshot)` を追加し、`TextFileService` の 4,096 文字窓を撤廃 | L1 | — |
| 2 | A-11: `TextBuffer` に記録付き全文差し替え API を追加 | L1 | **コード品質レビュー**(後続 2 タスクが依存する新 seam)+ **ミューテーション検証** |
| 3 | A-11: `ConvertEols` を新 API へ切替。§5.2 の契約表を 1 行ずつ満たす | L2 | — |
| 4 | A-11: `WriteToPath` のロールバックを新機構へ。XML doc も更新 | L3 | **仕様レビュー**(§5.3 の no-op 要件) |

各タスクは実装 → 仕様レビュー → 指摘反映の順で進める(CLAUDE.md §3)。
Task 2 は「後続タスクが依存する新しい抽象・seam」に該当するため前倒しでコード品質レビューを行う。
セキュリティ敏感面(外部入力のパース・パス操作・プロセス起動・WebView・ネットワーク)には
触れないため、脆弱性レビューの前倒しは行わない(最終ブランチレビューの脆弱性パスは実施する)。

---

## 7. テスト

### 7.1 L1 — `kxEdit.Core.Tests`

`Text/LineEndingDetectorTests.cs` / `Text/TextFileServiceLoadAsBufferAutoTests.cs` を拡張:

- **A-9 の回帰網**: 1 行目が 4,096 文字を超える LF ファイルを読み、`LineEnding.Lf` が返ること。
  fixture は先頭 4,096 文字に改行を 1 つも含まないこと(= 旧実装が確実に落ちる形)を
  テスト内で明示する。**旧実装で赤になることを確認してから**新実装を入れる。
- 同形の CR 版・CRLF 版。
- ピース / チャンク境界に CRLF が跨る文書で CRLF が 2 件に割れないこと(§4.2)。
  `EditorControlConvertEolsTests.ConvertEols_Utf8_LargeContent_ChunkBoundary_CrlfSpansChunks` が
  使う fixture の作り方を流用する。
- 混在文書の多数決が `Detect(string)` と一致すること(意味論不変の確認)。
- 空ファイル → `Crlf` 既定が維持されること(既存テストで担保済み。削らない)。

`Buffer/` に Task 2 の新 API のテスト:

- 差し替え後に `Undo()` が変換前の本文へ戻ること / `Redo()` が変換後へ進むこと。
- **既存履歴が消えないこと** — 編集を 2 回積んでから全文差し替えし、Undo 3 回で最初の状態まで
  戻れること。これが A-11 の本質的な回帰網である。
- 差し替え前後で `Modified` が期待どおり遷移し、Undo で保存点に戻ると `false` へ復すること。
- coalescing が切れること(差し替え直後の 1 文字入力が差し替えエントリへ融合しない)。

### 7.2 L2 — `kxEdit.Editor.Tests`

`EditorControlConvertEolsTests.cs` を拡張。既存テスト(caret / anchor / チャンク境界 / 末尾単独 CR / fast-path)は
**すべて維持**する(切替が挙動不変であることの対照群になる)。

- 非 fast-path の `ConvertEols` 後に `CanUndo` が true で、Undo で変換前の本文へ戻ること。
- `ConvertEols` 前に積んだ編集履歴が、変換後も Undo で辿れること。
- fast-path では履歴に何も積まれないこと(no-change テストなので、
  **非既定の状態から始める** — 履歴を 1 つ積んでおき、その数が変わらないことを見る。
  CLAUDE.md §4-B の教訓)。

### 7.3 L3 — `kxEdit.App.Tests`

`FileControllerTests.cs` を拡張:

- 保存失敗時に本文の EOL が変換前へ戻り、`Modified` が true のままであること
  (既存のロールバック網があれば、それが新機構でも緑であることを確認する)。
- **fast-path で保存失敗したとき、直前の編集が消えないこと**(§5.3 の no-op 要件)。
  partial なロールバックを検出するため、fixture は「変換不要な EOL」かつ
  「直前に Undo 可能な編集が 1 つ積まれている」状態から始める。

### 7.4 ミューテーション検証

**Task 2 の Undo 履歴管理部のみ**実施する。CLAUDE.md §4-A が
「UNDO/REDO の履歴管理アルゴリズム」を有効な適用先として明示しており、
本タスクはまさにそこへ新しいエントリ生成経路を足すため。

検証対象は新 API の `Record` 引数(`pos` / `removedLen` / `insertedLen` / `insertHasBreak`)と
`_savedRoot` を触らない判断。A-9 の検出ロジックはファイル入出力寄りだが、
多数決の比較演算子(`>=`)は境界が効くため、**変異が生存したら網を足す**方針で
スポットチェックに留める(全面適用はしない)。

`ConvertEols` の GUI 側(caret / スクロール復元)と `WriteToPath` の
ダイアログ経路には**適用しない**(CLAUDE.md §4-A の禁止領域)。

---

## 8. L5(実機 SR 検証)

**必要と判定する**。§5.2 のとおり UIA の発火経路が変わる
(`ReplaceSource` 経由の caret=0 中間状態が消え、`SelectionChanged` が
caret 復元後の 1 回になる)。CLAUDE.md §5 の「SR 経路に触れる変更は必須」に該当する。

チェックリストは実装完了時に `docs/plans/2026-08-28-eol-detection-and-undo-l5-checklist.md` へ作る。
最低限の項目:

1. LF 文書を CRLF 設定で Ctrl+S → 保存直後のキャレット位置が発声上ずれないこと。
2. 同上の直後に Ctrl+Z → 変換前へ戻り、戻った旨が読まれること。
3. 1 行目が 4,096 文字超の LF ファイルを開いて Ctrl+S → ステータスバーの EOL 表示が LF のままで、
   保存後もファイルが LF であること(晴眼確認 + SR での EOL 読み上げ)。

---

## 9. 非目標(YAGNI)

- **EOL 混在の警告ダイアログを出さない**。A-10 で入れた符号化劣化警告と同型の確認を
  EOL にも足す案はあるが、A-9 の修正で誤検出が消え、A-11 の修正で Undo できるようになれば
  「無警告のデータ書換」という監査の指摘は解消する。保存のたびにダイアログが増えるコストに見合わない。
- **保存時の EOL 統一をやめない**(= 書き出し側変換への移行)。§3 のとおり挙動変更になるため
  本ブランチでは扱わず、§10 の申し送りへ回す。
- **文字コード判定窓(64KB・M-16)には触れない**。受容済みトレードオフであり、別テーマ。
- **`Detect(string)` を削除しない**。呼び出し元の棚卸しは本テーマの範囲外。

---

## 10. 申し送り(実装時・レビュー時に追記する)

- **書き出し側 EOL 変換案**: `TextFileService.Save` が EOL を変換しながら書けば、
  バッファを一切触らずに済む。A-11 のロールバック機構(§5.3)そのものが不要になり、
  監査 M-25(CSV F2 編集中に `ConvertEols` 差替え後の古い `start/length` で別位置を書換)も
  同時に消える。代償は「保存後もメモリ上は EOL 混在のまま」という挙動変更。
  次リリース以降のテーマ候補として記録する。
- **A-9 の残余**: EOL が混在する文書は修正後も多数決で黙って統一される(§4.4)。
  説明書へ明記するか否かは未決。

### 10.1 Task 1(A-9)実装時の逸脱記録(2026-08-28 追記)

実装計画 `2026-08-28-eol-detection-and-undo.md` Task 1 の記載からの逸脱。src のロジックは
計画どおりで、逸脱はすべて fixture と網の側。

- **`LoadAuto_MajorityLfOutsideOldProbeWindow_DetectsLf` の filler を 4,000 → 4,094 文字**。
  計画の 4,000 だと後続の `"x\n"` が旧窓(4,096 文字)の内側に 47 組入り、窓内が
  crlf=1 / lf=47 になる。旧実装でも多数決で `Lf` を返すため、Step 2 の赤の確認で
  この 1 件だけが緑になった。4,094 にすると CRLF がちょうど窓の末尾 2 文字に収まり、
  窓内が crlf=1 / lf=0 =旧実装が `Crlf` を返す形になる。
- **チャンク境界 fact の fixture から末尾の `"tail\r\n"` を削除**。計画のままでは網にならない。
  境界の CRLF が CR + LF に割れた場合の内訳は crlf=1 / lf=1 / cr=1 で、判定は `Crlf` のまま
  =正解と区別できない。改行を境界の CRLF 1 つだけにすると、正=`Crlf` / 割れた場合=`Lf` で弁別できる。
- **チャンク境界の前提をテスト内で自己検証**(`AssertTwoPiecesSplitBetween`)。
  `PieceTree.Enumerate` でピース数と境界前後のバイトを固定し、`TargetChunkBytes` も定数参照にした。
  分割規則が変わって境界を跨がなくなったとき、fixture が黙って通り続けるのを防ぐ。
- **`Snapshot_overload_counts_trailing_lone_cr` の filler を `TargetChunkBytes - 1` →
  `TargetChunkBytes`**。ピース数の主張(計画の値だと 1 ピースにしかならない)は正しい
  (`off + len < bodyLen` が `4MB < 4MB` で偽になり分割ループが 1 回で終わる)。
  **ただし「drain 経路を通らない」という当初の説明は誤り**で、1 ピースでも末尾バイト 0x0D は
  `i + 1 < span.Length` が偽になり `pendingCr = true` → foreach 後の drain を通る。
  実測でも drain 削除変異は計画の fixture で撃墜できていた。
  変更は「複数ピース文書の最終ピースが CR 単独」という追加ケースを得る点で有益なので維持する。
- **多数決の同数(tie)fixture を 3 件追加**。§7.4 が「比較演算子の変異が生存したら網を足す」
  としており、計画の theory には crlf==lf / crlf==cr / lf==cr のケースが 1 件も無く、
  3 つの `>=` すべてで変異が生存した。`"a\r\nb\nc"` / `"a\r\nb\rc"` / `"a\nb\rc"` を追加。
- **`using System.Linq;` の追加は不要だった**(`Directory.Build.props` で `ImplicitUsings` 有効)。
- **`snap` ローカル変数は削除した**。同ファイル別メソッドの同名変数はそのまま。

### 10.2 Task 1 仕様適合レビューの指摘と対応(2026-08-28 追記)

全 5 件を fixup commit で修正した(元 commit は書き換えない= CLAUDE.md §4)。

- **§4.2 の `pendingCr` fall-through 分岐に網が無かった**(最重要)。「ピース末尾 CR +
  次ピース先頭が LF 以外 → CR 単独 1 件として数え、現バイトは落とさず通常処理へ進める」経路が、
  初回コミットの fixture では一度も実行されていなかった。境界 CRLF の fact は `b == 0x0A` 側へ、
  trailing CR の fact は drain へ抜けるため。等価変異ではなく、撃墜できる fixture が実在した。
  `Snapshot_overload_counts_carried_cr_before_non_lf_byte`
  (`'a'*(TargetChunkBytes-1) + "\r" + "x"` → `Cr`)と
  `Snapshot_overload_counts_carried_cr_before_crlf`
  (`'a'*(TargetChunkBytes-1) + "\r" + "\r\nx"` → `Crlf`)を追加。
  実ファイルでの再現条件は「4MB チャンク境界がちょうど CR に落ちる CR 単独(旧 Mac)文書」。
  変異実測: `cr++` 削除は前者のみ撃墜(`Cr` → `Crlf`)、`cr++` 後に `continue` を足す
  (現バイトを捨てる)変異は後者のみ撃墜(`Crlf` → `Lf`)。2 fixture が 1 行ずつ受け持つ。
- **削除した 4KB 窓を「意図的な設計」と説明する XML doc が 2 箇所残っていた**
  (`LoadedBuffer.LineEnding` / `LoadAsBufferAuto` の `<remarks>`)。A-9 のバグそのものを
  「1GB 級の全文カウントを避けるため意図的に prefix 限定」と記述しており、次の読者を
  「窓を戻す」方向へ誘導する状態だった。§5.3 が A-11 側に課している基準を A-9 にも当て、
  全文 byte 走査であること・窓が A-9 の原因だったこと・窓を復活させないことを明記した。
- **§7.1 の「同形の CRLF 版」が未実装だった**。
  `LoadAuto_CrlfFile_FirstLineLongerThanOldProbeWindow_DetectsCrlf` を追加。
  旧実装でも偶然 `Crlf` を返すのでバグの弁別力は無く、
  「窓の撤廃が過剰修正になって CRLF ファイルまで LF 側へ倒れる」変化を捕まえる対照群としての価値。
- **`LoadAuto_MajorityLfOutsideOldProbeWindow_DetectsLf` の前提がコメントでしか守られていなかった**。
  旧窓のコードはもう src に無いため、filler を縮めても `"x\n"` を減らしてもテストは黙って
  緑のまま弁別力だけを失う(チャンク境界 fact には自己検証を入れたのに、こちらには入れていなかった)。
  `OldProbeWindowChars` 定数を置き、CRLF の位置と「LF 多数派 50 件が窓の外にある」ことを
  assertion で固定した。filler を 4,000 に戻すと `Expected: 4094 / Actual: 4000` で落ちることを実測済み。
  LF 版 / CR 版にも同形の「旧窓に改行が無い」前提 assertion を入れて扱いを揃えた。
- **逸脱が文書に記録されていなかった**(CLAUDE.md §2)。本節 §10.1 として追記した。

### 10.3 §4.3 の性能記述は事実と食い違っていた(コード品質レビュー I-1)

§4.3 は「読み込み済みバッファに対する 1 パスが増えるだけなので、相対コストは小さい」と書いた。
**事実と違う。** レビュアーの実測(255MB / LF / 40 byte 行・Release)では、
`LoadAsBuffer`(検出を含まない)が cold 1,397 ms / warm 473〜515 ms に対し、
`LineEndingDetector.Detect` 単体が cold 289 ms / warm 298〜303 ms
= **cold で +21%・warm で +59〜63%** の増分だった。読込 CPU 時間の約 4 割を検出が占めている。

§4.3 は策定時スナップショットなので書き換えず(CLAUDE.md §8)、ここに実測を記録する。
先例は `2026-08-28-save-encoding-loss-warning-design.md` §8.6。

**対応**: 改行の探索を 1 バイトずつの比較ループから
`ReadOnlySpan<byte>.IndexOfAny(0x0D, 0x0A)`(SIMD 化済み)へ移した。
多数決の規則・`pendingCr` の持ち越し・末尾 drain の意味論はすべて不変。

本セッションでの実測(255MB・Release・ServerGC・同一マシン。cold は 1 回目、warm は 3 回の範囲):

| ケース | before(スカラー) | after(IndexOfAny) | 倍率 |
|---|---|---|---|
| 255MB / LF / 40 byte 行 | cold 282.4 / warm 276.1〜280.1 ms | cold 45.2 / warm 42.2〜42.5 ms | **約 6.5 倍** |
| 255MB / CRLF / 40 byte 行 | cold 233.6 / warm 231.9〜235.4 ms | cold 48.8 / warm 47.8〜48.8 ms | **約 4.8 倍** |
| 255MB / 改行なし巨大 1 行 | cold 236.9 / warm 237.0〜238.8 ms | cold 20.7 / warm 18.1〜19.9 ms | **約 12.5 倍** |

これで検出の増分は `LoadAsBuffer` warm 473〜515 ms に対し約 8〜10% になる。

**等価性の確認**: 改行を高密度に含むランダム文字列 30 万件で `Detect(string)` と
`Detect(TextSnapshot)` の全一致を確認した(各ケースについて、先頭を削って
`ByteStart != 0` / 複数ピースにした版でも一致することを併せて確認)。不一致 0 件。

**副次の設計変更(M-1)**: 持ち越し `pendingCr` が立つのは「ピース末尾の CR」だけなので、
その処理を内側ループの外(ピース先頭の 1 回)へ出した。「carry はピース先頭バイトにしか
効かない」という不変条件がコードから読める形になる。
これに伴い**空ピースガードが必須になった**(持ち越し処理が `span[0]` を見るため)。
`PieceTree.Split` は空ピースを作らず `TextBufferBuilder.AddChunk` も空を積まないので
現行の公開経路では到達不能=テストで覚えられない(変異を当てても生存する)。
ガードを消す変異が生存することは既知の等価変異として記録し、理由をコード側にも書いた。

### 10.4 Task 1 コード品質レビュー後の変異実測(2026-08-28 追記)

`IndexOfAny` 化で走査の形が変わったため、変異実測をやり直した
(`LineEndingDetectorTests` 全体に対して 1 変異ずつ適用し、`-warnaserror` ビルドの
成否を exit code で確認してから実行する)。

| 変異 | 結果 | 撃墜したテスト |
|---|---|---|
| 持ち越しの `cr++` を落とす | kill | `..._counts_carried_cr_before_non_lf_byte` |
| 持ち越し後に `continue`(現バイトを捨てる) | kill | `..._counts_carried_cr_before_crlf` |
| 末尾 drain を落とす | kill | `..._counts_trailing_lone_cr` / `..._matches_string_overload("a\r")` |
| `pendingCr = true` を `cr++` に(持ち越しを一切しない) | kill | `..._counts_crlf_spanning_chunk_boundary_as_one` |
| `Slice(ByteStart, ByteLen)` → `Slice(0, Chunk.ByteLength)` | kill | `..._scans_only_the_piece_range_after_edit` |
| 同上を `Slice(0, ByteStart + ByteLen)` と書いた版 | kill | 同上 |
| `crlf >= lf` → `crlf > lf` | kill | `..._matches_string_overload("a\r\nb\nc")` |
| `crlf >= cr` → `crlf > cr` | kill | `..._matches_string_overload("a\r\nb\rc")` ほか |
| `lf >= cr` → `lf > cr` | kill | `..._matches_string_overload("a\nb\rc")` |
| 空ピースガードを落とす | **生存(等価変異)** | — §10.3 のとおり到達不能 |

**ハーネスの罠**: 最初の実測は `dotnet build` の失敗を `grep -c "error CS"` で判定しており、
Sonar の `error S108` / `error S3267` を見落として**古い DLL に対してテストを走らせていた**。
撃墜したテスト名が理屈と合わないことで気付いた。変異ハーネスはビルドの exit code で
判定すること(変異がアナライザに引っかかってビルドできないケースは実在する)。

### 10.5 却下: `PieceStats.Breaks` による早期終了(コード品質レビュー Q2)

`snapshot.LineCount - 1` で総改行数 `T` を O(1) で得て `2 * crlf >= T` 等で打ち切る案。
**数学的には正しい**(レビュアーが反例なしを確認)が却下する。

1. 効果が最良でも 2 倍止まり。§10.3 の `IndexOfAny` 化は同じ実装量で 4.8〜12.5 倍で、
   しかも早期終了と両立する。まず取るべきはそちら。
2. `LineEndingDetector` が `PieceStats.Breaks` のセマンティクス(CRLF を 1 と数える monoid 規約)に
   結合する。将来 `Breaks` の定義が変われば検出器が**黙って**誤った早期終了をする。
   現設計の「byte 列だけを見る自己完結」が読みやすさの核なので壊さない。

### 10.6 申し送り: `EolSegments` seam(EOL トークナイザの共通化・コード品質レビュー Q1)

`LineEndingDetector.Detect(TextSnapshot)` / `EditorControl.IsEolAlreadyUniform` / `ConvertEols` の
3 者は、目的は違う(数える / 判定する / 変換する)が
**`PieceTree.Enumerate` → `Slice(ByteStart, ByteLen)` → `pendingCr` 持ち越し → 末尾 drain**
という EOL トークナイザを共有している。lexer / consumer の分離であり抽象は歪まない。

**重複はすでにコストを払っている**。§10.2 の「最重要」指摘は、この状態機械の 3 つ目のコピーで
`pendingCr` fall-through に網が無かったという指摘だった。同じ罠を 3 回踏み直す構造になっている。

`kxEdit.Core.csproj` に `<InternalsVisibleTo Include="kxEdit.Editor" />` があるので、
Core 側に `internal` で置けば `EditorControl` から使える(公開 API を増やさずに済む)。
`ref struct` に `IEnumerator<Piece>` と `ReadOnlySpan<byte>` を抱えた duck-typed enumerator の案:

```csharp
namespace kxEdit.Core.Buffers;
internal enum EolKind { Text, Crlf, Lf, Cr }
internal readonly ref struct EolSegment { public EolKind Kind { get; } public ReadOnlySpan<byte> Bytes { get; } }
internal ref struct EolSegments { public EolSegments(PieceTree.Node? root) { ... } }
```

副次効果: `IsEolAlreadyUniform` の
`targetBytes.Length == 2 && targetBytes[0] == 0x0D && targetBytes[1] == 0x0A` という 4 回反復する
比較が `kind != EolKind.Crlf` の 1 行になり、`ConvertEols` の 1 バイトずつの
`outBuf[outLen++] = b` が span 単位コピーになる(§10.3 と同種の性能改善も同時に取れる)。

**本ブランチでは行わない。** 理由: (1) A-11(Task 2 以降)が `ConvertEols` 本体を書き換えるので、
同じホットメソッドで 2 系統の変更を混ぜると挙動不変の証明が両方とも弱くなる。
(2) CLAUDE.md §2「リファクタは挙動不変が原則」— A-9 の scope 外のリファクタを混載すると
PR の焦点が散る。**A-11 マージ後の独立テーマとして回収する。**

### 10.7 申し送り: XML doc の `cref` は CI で守られていない

`GenerateDocumentationFile` がどのプロジェクトでも有効でないため、通常ビルドで XML doc が
コンパイルされず CS1574(解決できない `cref`)が原理的に出ない。

**本ブランチの差分については問題なし**。品質レビュアーが `-p:GenerateDocumentationFile=true` で
実ビルドし、今回の差分が追加した `cref` はすべて解決することを確認している(警告ゼロ)。

一方で既存債務は実測 70 件(CS1574 ×13 / CS0419 ×9 / CS1570 ×14 / CS1573 ×33 / CS1734 ×1)。
`kxEdit.Core` 分は `TextFileService.cs:26, 128, 361` の CS1574 ×3 と
`TextSnapshot.cs:57` / `TextFileService.cs:358` の CS0419 ×2。
`SafeLinkExtension.cs:125-129` は URL を含む summary が XML として壊れている。

有効化は **70 件の債務返済とセットの独立テーマ**とする(§3 の工程を踏まない大域変更になるため、
A-9 の PR には載せない)。再現コマンド(非破壊):

```
dotnet build kxEdit.sln -c Release -p:GenerateDocumentationFile=true -p:TreatWarningsAsErrors=false -p:NoWarn="CS1591" --no-incremental
```

### 10.8 訂正: §10.3 / §10.4 の「生存する変異は空ピースガードだけ」は過大申告だった(再レビュー M3)

§10.3 末尾と §10.4 の表は「生存するのは空ピースガードを落とす等価変異のみ」と書いた。
**不正確だった。** 持ち越し処理の `i = 1;`(境界を跨いだ CRLF の LF を消費する行)を落とす変異も
**生存していた**。実測で確認済み — 1,272 件すべて緑のまま通った。

`i = 1;` を落とすと `i` が 0 のままなので、続く `IndexOfAny` が**同じ LF をもう一度見つけて
`lf++` する**。境界 CRLF が `crlf=1` かつ `lf=1` に二重計上される。等価変異ではなく、
`crlf == lf` の同数文書では `crlf >= lf` が `1 >= 2` で偽になり多数決が **`Crlf` → `Lf` に反転**する。

**生存していた理由**: `..._counts_crlf_spanning_chunk_boundary_as_one` の fixture は改行が
境界 CRLF 1 つだけなので、二重計上しても `crlf=1 / lf=1` の同数で `Crlf` のまま区別できない。
**その fact のコメント自身が警戒している「割れても Crlf のままで区別できない」構図の裏返し**である。

**これは `IndexOfAny` 化(§10.3)で入った退行ではない。** `706dcab` を使い捨て worktree に
チェックアウトして等価な変異を当て、同じく 1,271 件すべて緑=生存することを実測した。
Task 1 の最初から存在した抜けで、`IndexOfAny` 化はそれを持ち込んでも消してもいない。

スカラー版で当てる変異には注意が要る。`continue;` を単に削除すると `cr++` も走る
**複合変異**になり(こちらも生存する)、`i = 1;` を落とすのと同じ意味にならない。
正確な等価物は `if (b == 0x0A) crlf++; else cr++;` へ組み替えて **LF の消費だけを落とす**形:
これで b == 0x0A が下の `else if (b == 0x0A) lf++` へ落ちて二重計上になる。

**対応**: `Snapshot_overload_does_not_double_count_boundary_crlf` を追加した。
fixture は境界 CRLF の後ろに `"x\n"` を置いて `crlf=1 / lf=1` の同数を作る
(改行を 1 つだけにすると二重計上しても同数のままで弁別できないため)。
狙いが違うので既存 fact に混ぜず**別 fact として**足した
(既存 fact のコメントは「改行はこの CRLF 1 つだけにすること」と警告しており、
そこへ足すと将来の読者を混乱させる)。追加後に同じ変異を当て、この fact だけが撃墜することを確認した。

**教訓**: 「生存した変異は等価変異だけ」と書くときは、当てた変異の一覧が網羅的かを疑うこと。
このブランチの価値は「網が実際に何を守っているかを正確に書いてある」ことにあり、
過大申告が 1 つ混じると資料全体の信頼が落ちる。

### 10.9 訂正: §10.4 の変異名が `IndexOfAny` 化後の構造と合っていない(再レビュー N-3)

§10.4 の表は 2 行目を「持ち越し後に `continue`(現バイトを捨てる)」と書いたが、
`IndexOfAny` 化後のコードに `continue` はもう存在しない。実際に当てたのは
**`else` 節に `i = 1;` を足して先頭バイトを捨てる**変異である
(撃墜の事実 — `..._counts_carried_cr_before_crlf` が落ちること — は正しい)。
表を再実行する人が迷わないよう、読み替えはここに記録する。

§10.4 の表に加えて、本節時点で確認済みの変異は次のとおり:

| 変異 | 結果 | 撃墜したテスト |
|---|---|---|
| 持ち越しの `i = 1;` を落とす(LF を二重計上) | kill(本節で追加) | `..._does_not_double_count_boundary_crlf` |
| 空ピースガードを落とす | 生存(到達不能) | — §10.3 |

### 10.10 Task 2(A-11 Core)実装時の検証と逸脱記録(2026-08-28 追記)

**Step 0(§5.1 の受け入れ条件)の検証結果**: 「`TextBufferBuilder` が作った別チャンク由来の
root を既存 `TextBuffer` へ取り込んでよい」は成立する。

- `TextBuffer._append`(`AppendBuffer`)の参照は宣言と `Splice` 内の `_append.Append(insert)` の
  2 箇所だけで、新規挿入テキストの置き場にしか使われていない。差し替え後もそのまま使い続けてよい。
- `AppendBuffer.Append` 自身が 32KB(`LargeInsertBytes`)超の挿入で専用 `TextChunk` を作る。
  「木の中に追記バッファ由来でない chunk が混ざる」状態は既に日常的に起きており、
  外部で構築した root の取り込みは新しい事態ではない。
- 差し替え後も `_append` は書きかけの 64KB ブロックを保持し、そのブロックを包む `TextChunk` は
  Undo 履歴から到達可能な旧 root のピースに参照されたまま残る。ただし旧ピースが指すのは
  `[0, _pos)` で以後の書き込みは `_pos` 以降へ進むため、`AppendBuffer` の
  「公開済み範囲は以後不変」不変条件がそのまま効く。新しい危険は生じない。

**`MaxTotalBytes` を二重判定しない判断の裏取り**: `TextBuffer` の internal ctor の呼び出し元は
`TextBufferBuilder.Build()` の 1 箇所だけ(`grep -rn "new TextBuffer(" src/ tests/`)。
よって引数のバッファは必ずビルダーの上限判定(`AddChunk` の `DocumentTooLargeException`)を
通っており、既定値が同一(512 MB)な実運用では二重判定は不要。
**ただし** 両者の `MaxTotalBytes` は独立した internal のテスト注入点なので、注入値を食い違わせた
場合に限り本 API は自バッファの上限を超える木を取り込みうる。この残余は XML doc に明記した。

**実装計画 Task 2 の記載からの逸脱**:

- **`_history.Record` の前に `_history.BreakCoalescing()` を足した**。計画は
  「`insertHasBreak: true` で coalescing を必ず切る」としていたが、**これは事実と違う**。
  `UndoHistory.Record` の融合判定は `pureInsert` / `pureDelete` の形でしか通らないため、
  通常形(`removed > 0` かつ `inserted > 0`)では `insertHasBreak` はそもそも参照されない。
  効くのは退化形だけで、しかも 2 つの退化形で担い手が違う:
  - 空文書 → 1〜2 文字(純挿入形)= **直後**のタイプが差し替えエントリへ融合しうる。
    ここは `insertHasBreak: true` が止める。
  - 全文 → 空(純削除形)= **直前**の 1〜2 文字削除へ逆方向融合(Backspace 継続扱い)しうる。
    `pureDelete` 側の判定は `insertHasBreak` を見ないので、**前置の `BreakCoalescing()` が要る**。
  どちらも実測で撃墜を確認した(下表 M7 / M8)。両方あって初めて「全文差し替え= 1 Undo 単位」が
  形を問わず成立する。
- **早期 return は `ReferenceEquals` のまま維持**(内容が同じでも別 root なら記録する)。
  全文の内容比較は O(n) で、512 MB 文書では現実的でない。`Splice` の早期 return も同じく
  参照・長さベースで、契約が揃う。呼び出し側(`ConvertEols` の `IsEolAlreadyUniform` fast-path)が
  変換不要を先に判定するため、実運用で無変化の木を渡す経路は無い。
- **`BreakCoalescing()` は早期 return の後に置いた**。前に置くと無変化パスでも `_open` を倒し、
  「履歴を汚さない」契約が崩れる。
- **計画のテスト `ReplaceAllRecordingUndo_BreaksCoalescing` を書き直した**。計画はコメントで
  「履歴を 1 つ積んだ状態から始める」と書きながらコードは既定状態(履歴空)から始めており、
  直前方向の融合を検証できていなかった。差し替えをタイプ 2 回で挟み、Undo 3 回でちょうど
  3 段戻る形(`..._IsSingleUndoUnit_BetweenTypedEdits`)に変更した。
- **退化形 2 件・キャレット 1 件・null ガード 1 件のテストを追加**(下表参照)。
- **`using Xunit;` は追加不要**(テスト csproj が `<Using Include="Xunit" />` を持つ)。
  計画のコードをそのまま貼ると冗長 using になる。

**ミューテーション検証の実測**(CLAUDE.md §4-A「UNDO/REDO の履歴管理アルゴリズム」該当)。
使い捨て worktree で `ReplaceAllRecordingUndo` にのみ変異を当て、
`kxEdit.Core.Tests` 全件で判定した(ビルド可否は exit code で判定・
`-p:TreatWarningsAsErrors=false`)。

| 変異 | 結果 | 撃墜したテスト |
|---|---|---|
| M1 `_history.Record(...)` を削除 | kill | 8 件(早期 return と null ガードの fact 以外すべて) |
| M2 `pos: 0` → `1` | 初回 **生存** → 網追加後 kill | `..._UndoRedoCaretPos_IsEndOfDocument` |
| M3 `removedLen` と `insertedLen` を入替 | 初回 **生存** → 網追加後 kill | `..._UndoRedoCaretPos_IsEndOfDocument` |
| M4 早期 return を削除 | kill | `..._SameRoot_DoesNotRecord` |
| M5 `_savedRoot = newRoot` を足す | kill | `..._ModifiedTogglesWithSavePoint` |
| M6 `_current` 代入と `Record` の順序を入替 | **生存(等価変異)** | — |
| M7 `insertHasBreak: true` → `false` | kill | `..._PureInsertShape_DoesNotAbsorbFollowingTyping` |
| M8 前置の `BreakCoalescing()` を削除 | kill | `..._PureDeleteShape_DoesNotMergeIntoPrecedingDelete` |
| M9 `ArgumentNullException.ThrowIfNull` を削除 | 初回 **生存** → 網追加後 kill | `..._Null_Throws` |

- **M2 / M3 が初回生存した理由**: `pos` / `removedLen` / `insertedLen` は木の内容に一切効かず、
  `Undo()` / `Redo()` の戻り値 `UndoResult.CaretPos` にしか現れない。初回のテスト群は
  本文と `Modified` しか見ていなかった。キャレット位置を固定する fact を足して両方撃墜した。
- **ハーネスの罠(実測で踏んだ)**: `insertHasBreak: true` を `false` にする置換が、
  同じ文字列を含む**直上の説明コメント**にヒットして「生存」と出た(ファイルは変わるので
  適用失敗としても検出できない)。変異ごとに `diff` を出して**コード行が変わったこと**を
  目視してから判定し直したところ撃墜だった。上表は差分確認後の値である。
- **M6 を等価変異と判断した根拠**: `UndoHistory.Record` は引数と `_undo` / `_redo` / `_open` しか
  触らず `_current` を読まない。`_current` 代入側も `_history` を読まない。渡す 4 値
  (`rootBefore` / `newRoot` / `removed` / `inserted`)は代入より前に確定したローカルなので、
  2 文の順序は観測可能な差を生まない。**当てた変異はこの 9 件だけであり、
  「網が完全」という主張はしない**(§10.8 の教訓)。

### 10.11 Task 2 レビュー反映(fixup)と §10.10 の訂正(2026-08-28 追記)

仕様適合レビュー・前倒しコード品質レビュー(別エージェント 2 本)の指摘を反映した。
**§10.10 は履歴として残し、誤り・陳腐化は本節で訂正する**(CLAUDE.md §8 の追記原則)。

#### (1) 訂正: 設計書 §5.1 の `insertHasBreak` に関する主張は 2 通りに誤っていた

§5.1 は「`insertHasBreak: true` にするのは coalescing を必ず切るため
(EOL 変換は『≤2 文字の連続タイピング』ではないので、直前のタイプ操作へ融合させてはならない)」
と書いている。両レビュアーの独立検証により、次の 2 点で誤りと確定した(§5.1 自体は書き換えない)。

1. **「必ず切る」は成立しない**。`UndoHistory.Record` の融合判定は `pureInsert` / `pureDelete` の
   形でしか通らず、EOL 変換が作る通常形(`removed > 0` かつ `inserted > 0`)では
   `insertHasBreak` は**参照すらされない**。
2. **括弧内の向きが逆**。`insertHasBreak` が左右するのは `_open = coalescable`、すなわち
   **後続**編集の融合可否である。「**直前**のタイプ操作への融合」は `pos = 0` では原理的に
   起こらない(タイプ継続枝が `pos == prev.Pos + prev.InsertedLen && prev.InsertedLen > 0` を
   要求するため、`pos = 0` と矛盾する)。

実際に必要だったのは次の 2 本立てで、担い手が別である。

| 止めたい融合 | 担い手 | 撃墜する fact |
|---|---|---|
| **後続**の小編集が差し替えエントリへ融合(空文書 → 1〜2 文字の純挿入形) | `insertHasBreak: true` | `..._PureInsertShape_DoesNotAbsorbFollowingTyping` |
| **直前**の 1〜2 文字削除へ逆方向融合(全文 → 空の純削除形。`pureDelete` 側は `insertHasBreak` を見ない) | 前置 `BreakCoalescing()` | `..._PureDeleteShape_DoesNotMergeIntoPrecedingDelete` |

**この 2 つの退化形は `ConvertEols` 経路からは発生しない**(EOL 変換は本文を空にも非空にもしない)。
公開 API の契約を形に依らず成立させるための防御であり、A-11 の実害シナリオではない。

#### (2) 訂正: §10.10 の変異表 M1 行が書いた撃墜「件数」は撤回する

テスト数は文書に書かない(CLAUDE.md §5)。fact 追加で必ず陳腐化する。
定性表現「早期 return と null ガードの fact 以外すべて」が正であり、数値は無効とする。

#### (3) API シグネチャの変更(§5.1 からの逸脱・レビュー I-1 / I-2)

```csharp
public bool ReplaceAllRecordingUndo(TextSnapshot rebuilt)
```

- **戻り値 `bool`(記録したら true)**。Task 4 のロールバックは「Undo 1 回で差し替え前へ戻せるか」
  の 1 bit に安全性が懸かる。戻り値が無いと `ConvertEols` 側が「非 fast-path を通った ⇒ 記録した」と
  **推論**することになり、その推論は Editor 側 fast-path 判定(`IsEolAlreadyUniform`)と
  Core 側早期 return という**独立した 2 コードの一致**に依存する。片方が変われば Task 4 が黙って
  1 つ余分に Undo する=設計書 §5.3 が名指しした最悪シナリオ。推論を事実に置き換えた。
- **引数を `TextBuffer` から `TextSnapshot` へ**。§5.1 は `TextBuffer` を受ける前提で書かれていたが、
  「`TextBuffer` を受け取って root だけ盗み、履歴・保存点・`_append` を捨てる」契約は
  **引数の型が嘘をついている**。`TextSnapshot` は public・不変で、既に読み取り経路の公開通貨。
  §5.1 の要件「`PieceTree.Node` を public シグネチャへ露出させない」は同じく満たす。
  自己渡しも `buf.ReplaceAllRecordingUndo(buf.Current)` と見るからに no-op になる。
  これに伴い上限の説明も「取り込む木の上限は渡し手が担保する」へ改めた。

#### (4) 受容: Redo スタックの破棄(レビュー I-4)

`_history.Record` は `_redo.Clear()` を呼ぶ(`UndoHistory.cs:44`)。Task 4 に次の差が出る。

| | 現行 main | 新設計(Task 4 後) |
|---|---|---|
| 入力 → Ctrl+Z(redo あり) | `_redo=[T]` | `_redo=[T]` |
| Ctrl+S → `ConvertEols` | 新バッファへ差替(旧バッファの `_redo=[T]` は無傷) | **`_redo` を破棄** |
| 保存失敗 → ロールバック | 旧バッファ参照へ戻す=`_redo=[T]` 復活 | `Undo()`=**ユーザーの redo は永久に失われる** |

保存成功時は main と同じ(main も新バッファなので redo は消える)。**失敗時だけが挙動変更**。
`_redo.Clear()` 済みから復元する手段が Core に無いため **Task 4 では原理的に直せない**。
保存失敗と pending redo の同時発生は稀で、`_redo` の退避・復元 API を足すコストに見合わないため
**受容**する。網は `..._DiscardsRedoStack`、XML doc にも 1 行明記した。

#### (5) 受容: Undo 後のキャレットが文書末尾へ飛ぶ(レビュー I-5)

`TextBuffer.Undo()` は `Pos + RemovedLen` を返し、全文差し替えでは `0 + 旧全長` = 文書末尾。
`EditorControl.Undo()` がそれをキャレットに設定し `AfterEdit()` で追従スクロールするため、
「文書途中で Ctrl+S(EOL 変換発生)→ Ctrl+Z」でキャレットと表示が末尾へ移動する。

**受容**する。これは「全文を 1 Undo 単位にする」ことの標準的な帰結で、`Replace(0, len, text)` でも
同じキャレットになる。Task 3 で `EditorControl` 側に EOL 変換専用の特例を作ると、汎用 Undo 経路に
分岐が入って他の Undo 意味論を壊すリスクの方が大きい。

**§8 の L5 チェックリスト項目 2 に追加する内容**(§8 は策定時記述なので本節に書く):
現行の「Ctrl+Z → 変換前へ戻り、戻った旨が読まれること」に加え、
**「文書の途中(例: 5,000 行目)で保存 → Ctrl+Z し、キャレットがどこへ移動するかを確認する」**を
明示項目として実施する。実機で不評なら、Task 3 で `(m, k)` 復元位置へ戻す案を申し送りとして回収する。

#### (6) 却下: `Record` の後にもう 1 回 `BreakCoalescing()` を置く案

構造的に「前後どちらの向きも閉じる」形にはなるが、そうすると `insertHasBreak: true` が冗長になり
M7 が等価変異に落ち、同時に「後置 `BreakCoalescing()` の削除」も等価変異になる。
**撃墜できていた変異 1 件を失って等価変異 2 件を得る**取引で、観測可能な挙動は何も変わらない。
現在の形は 2 つのガードがそれぞれ別の観測可能な仕事をしており、そちらが優れている。

なお純削除の退化形では `Record` 後に `_open = true` が残るが、差し替え後は空文書で後続の融合可能な
編集が来ない(削除は `Splice` の早期 return、挿入は `prev.RemovedLen == 0` の条件で弾かれる)ため
**観測できない**。コードコメントもこの事実に合わせた。

#### (7) 再実測: 変異 13 件(シグネチャ変更後の最終ソースに対して)

I-1 / I-2 でシグネチャが変わったため**全変異を当て直した**。判定は exit code、
適用差分を毎回 `diff` で目視してからテストを走らせている(§10.10 のハーネスの罠対策)。

| 変異 | 結果 | 撃墜した fact |
|---|---|---|
| M1 `_history.Record(...)` を削除 | kill | 早期 return と null ガードの fact 以外すべて |
| M2 `pos: 0` → `1` | kill | `..._UndoRedoCaretPos_IsEndOfDocument` |
| M3 `removedLen` と `insertedLen` を入替 | kill | 同上 |
| M4 早期 return を削除 | kill | `..._SameRoot_DoesNotRecord` ほか 2 件 |
| M5 `_savedRoot = newRoot` を足す | kill | `..._ModifiedTogglesWithSavePoint` |
| M6 `_current` 代入を `Record` の後へ移動 | **生存(等価変異)** | — |
| M7 `insertHasBreak: true` → `false` | kill | `..._PureInsertShape_DoesNotAbsorbFollowingTyping` |
| M8 前置 `BreakCoalescing()` を削除 | kill | `..._PureDeleteShape_DoesNotMergeIntoPrecedingDelete` |
| M9 `ThrowIfNull` を削除 | kill | `..._Null_Throws` |
| M10 `BreakCoalescing()` を早期 return の**前**へ移動 | 網追加前 **生存** → 追加後 kill | `..._SameRoot_DoesNotBreakCoalescing` |
| M11 判定を `ReferenceEquals(_current, rebuilt)` へ | 網追加前 **生存** → 追加後 kill | `..._DistinctSnapshotWithSameRoot_DoesNotRecord` |
| M12 末尾 `return true` → `false` | kill | `..._Undo_RestoresPreviousText` |
| M13 早期 `return false` → `true` | kill | `..._SameRoot_DoesNotRecord` ほか 2 件 |

- **M10 / M11 は両レビュアーが独立に見つけた穴**で、実測でも網追加前は生存した。
  M10 は §10.10 が「早期 return の後に置く」と根拠まで書いた設計判断なのに網が無かった。
  M11 は既存の `..._SameRoot_DoesNotRecord` が同一インスタンスを渡すため
  「root 同一」と「インスタンス同一」を弁別できていなかった。空文書同士(別バッファ・別
  スナップショット・どちらも `null` root)で弁別する fact を追加して撃墜した。
- **M6 を等価変異と判断した根拠**: `UndoHistory.Record` は引数と `_undo` / `_redo` / `_open` しか
  触らず `_current` を読まない。`BreakCoalescing()` も `_current` を読まない。渡す 4 値は
  代入より前に確定したローカルなので、順序は観測可能な差を生まない。
  **当てた変異はこの 13 件だけであり、「網が完全」という主張はしない**(§10.8 の教訓)。

#### (8) Task 4 への申し送り: `DocumentTooLargeException` が catch フィルタを抜ける

`ConvertEols` は LF → CRLF で総バイト数を増やすため、512 MB 直下の文書で
`TextBufferBuilder.AddChunk` が `DocumentTooLargeException` を投げうる。しかし
`FileController.WriteToPath` の catch フィルタ
(`IOException or UnauthorizedAccessException or SecurityException or NotSupportedException or ArgumentException`)
に一致せず、未処理で抜ける。**main 既存の穴**であり Task 2 の commit が作ったものではないが、
Task 4 が catch 節を書き換えるので、そのタイミングで拾うか申し送るかを決める。

### 10.12 Task 3 / Task 4 への申し送り(Task 2 再レビューで判明・2026-08-28 追記)

Task 2 の前倒しコード品質レビュー(再レビュー)は Core API を承認したうえで、
**Core の外**に 2 件の課題を残した。いずれも §5.2 / §5.3 の記述漏れであり、
Task 3 / Task 4 の着手時に回収する。§5.2 / §5.3 本体は策定時スナップショットとして
書き換えず、本節を追加分として扱う(CLAUDE.md §8)。

#### (1) §5.2 の契約表に `_wasModified` が無い — 素直な実装は保存中に spurious `SavePointLeft` を焚く

§5.2 の契約表が「`ReplaceSource` 内でやっていて in-place 化後は明示的に呼ぶ必要があるもの」
として挙げた 5 項目(UIA スナップショット更新 / `TextChanged` / `UpdateUI` /
スクロールバー同期 / `Invalidate`)は、**`AfterEdit()` の本体そのもの**である。
したがって Task 3 の自然な実装は「`AfterEdit()` を呼ぶ」になる。

ところが `AfterEdit()`(`EditorControl.cs:1260-1263`)は表に無い副作用を持つ:

```csharp
bool nowModified = Modified;
bool shouldFireLeft = !_wasModified && nowModified;   // ← EOL 変換で false→true
_wasModified = nowModified;
if (shouldFireLeft) SavePointLeft?.Invoke(this, EventArgs.Empty);
```

`ReplaceAllRecordingUndo` は `_savedRoot` を触らないので **`Modified` が false→true へ遷移**する。
`AfterEdit()` を呼ぶと**保存処理の途中で `SavePointLeft` が発火**し、
`DocumentManager.cs:82` の `OnDirtyChanged(doc)` へ流れる。
現行 main は `ReplaceSource:301` が `_wasModified = buffer.Modified`(新バッファ=false)を
**直接代入**しイベントを一切焚かないため、これは**新規の挙動**になる。

**決定**: 挙動不変を優先し、`AfterEdit()` をそのまま呼ばない。UIA / `UpdateUI` /
スクロールバー同期 / `Invalidate` は個別に呼び、`_wasModified` は
`ReplaceSource` と同じく**代入で揃える**(保存中に `SavePointLeft` を焚かない)。

Task 3 の実装者は、**成功パスと失敗パスの両方**について
`SavePointLeft` / `SavePointReached` の発火列を列挙し、main と一致することを示すこと。

なお §5.2 の契約表には次の 2 行も欠けていた。Task 3 で決定として記録する:

| 契約 | 現状の担い手 | 切替後の決定 |
|------|------------|------------|
| `_wasModified` の同期 | `ReplaceSource:301` が直接代入(イベント無し) | **代入で揃える**(上記) |
| `MouseDragging` / `_wheelAccum` リセット | `ReplaceSource:287-289` | **意図的に落とす**(in-place 編集でドラッグ選択やホイール蓄積を破棄する理由が無い)。決定として記録する |

#### (2) Task 4 のロールバックは `UndoResult.CaretPos`(= 文書末尾)を使ってはならない

§10.11 (5) で受容したキャレット末尾ワープは、**ユーザーが Ctrl+Z を押したとき**の話である。
Task 4 のロールバックは**ユーザーが Undo を要求していない**のに同じ経路を通ると、
保存失敗ダイアログの裏でキャレットが黙って末尾へ飛ぶ。

現行 main も良くない: ロールバックの `SetOrReplaceSource` → `ReplaceSource` は
`_caretCtrl.SetTo(0, ...)` / `_topLine = 0` / `_scrollX = 0` で**キャレットもスクロールも 0 へ潰す**。
新設計は「0 へ潰す」が「末尾へ飛ぶ」に変わるだけで、どちらも劣悪である。

**新しい seam はこれを改善できる**。`TextBuffer.Undo()` は `UndoResult?` を返すだけで、
キャレット設定は Editor 側の責務である(`EditorControl.cs:1295-1300`)。
したがって `EditorControl` は戻り値を**無視して**保存前の位置へ復元できる。
Core 側の変更は不要。

**決定**: Task 4 のロールバックは、キャレットとスクロール位置を
**保存失敗前の位置へ明示復元する**。位置は `ConvertEols` を呼ぶ前に呼び出し元が捕捉する
(EOL 変換前のオフセットは、変換を取り消した本文に対してそのまま有効)。
結果として main(0 へ潰す)より良くなる。

#### (3) L5 チェックリスト作成時に §10.11 (5) を回収すること

§8 は「チェックリストは実装完了時に
`docs/plans/2026-08-28-eol-detection-and-undo-l5-checklist.md` へ作る」と書いているが、
項目 2 に足すべき内容は §10.11 (5) にある。チェックリストを作る人が §10.11 まで
読む保証がないため、ここに導線を置く。

§10.11 (5) の項目文は「キャレットがどこへ移動するかを確認する」という**観測指示**で、
期待値が無いためチェックを付けても何も記録されない。挙動は受容済みなので期待値を書ける:

> 5,000 行目付近にキャレットを置いて Ctrl+S(EOL 変換が起きる条件で)→ Ctrl+Z。
> **期待**: 本文が変換前へ戻り、キャレットと表示が**文書末尾**へ移動する(受容済み挙動)。
> **判定**: NVDA がその移動を読み上げるか / ユーザーが現在位置を見失わないか。
> 見失うなら Task 3 で `(m, k)` 復元位置へ戻す案を申し送りとして回収する。

#### (4) 変異ハーネスのアンカー注意(再レビューで レビュアー自身も踏んだ)

`insertHasBreak: true` を素朴な文字列置換で変異させると、fixup で追加された**説明コメント**

```
//  ・insertHasBreak: true = 空文書→1〜2文字(純挿入形)の直後のタイプがこのエントリへ
```

にヒットし、`Record` の引数が無変化のまま「生存」と出る。§10.4 / §10.8 が記録した罠と同型で、
コメントが増えたぶん発生確率が上がっている。**12 スペースインデントの引数行**
(`            insertHasBreak: true\n        );\n`)をアンカーに取ること。

#### (5) 純削除退化形の不可観測性は `UndoHistory.Record` の融合条件に依存する

§10.11 (6) で後置 `BreakCoalescing()` を却下した結果、「差し替えエントリは後方向へも閉じている」
はコードではなく**コメント**(`TextBuffer.cs:147-150`)が担保する形になった。
この不可観測性の論証は `UndoHistory.Record` の融合条件
(`UndoHistory.cs:46-50, 57` — 特に `pureInsert && prev.RemovedLen == 0` で弾かれること)に依存する。
融合条件を変更する将来のタスクは、`ReplaceAllRecordingUndo` のコメントを再検証すること。
