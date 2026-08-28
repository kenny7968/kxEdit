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
### 10.13 Task 3(`ConvertEols` の in-place 化)実装時の検証と逸脱記録(2026-08-28 追記)

> 本節は Task 3 の仕様適合レビュー(指摘 I-1〜I-6)と**コード品質レビュー**
> (指摘 I-1〜I-4 / m-1〜m-4)を反映した版である。
> 初版で書いた「件数」「網が無い」「a11y 論拠」の 3 箇所は誤り/過小申告だったため
> **本節内で直接修正した**(§10.13 は本タスクが書いた節であり、訂正の追記ではなく
> 書いたばかりの記述の修正として扱う。§1〜§9 および §10.1〜§10.12 は無改変)。

#### (1) 赤の確認(Step 3)と main ベースラインの実測

新規テストを先に置き、**src を main のまま**(`git show c167fba:src/kxEdit.Editor/EditorControl.cs`)
走らせて赤/緑を実測した(戻り値テストは `void` シグネチャでコンパイルできないため
この実測からは除外)。

**FAIL(= in-place 化で初めて緑になるもの)**:

| テスト | main でのメッセージ |
|---|---|
| `ConvertEols_NonFastPath_IsUndoable` | `Assert.True() Failure`(`CanUndo` が false) |
| `ConvertEols_NonFastPath_PreservesEarlierUndoHistory` | `Expected: "a\nX\nY" / Actual: "a\r\nX\r\nY"`(Undo が no-op) |
| `ConvertEols_NonFastPath_RecordsExactlyOneUndoEntry` | `Expected: "a\nb\ncd" / Actual: "a\r\nb\r\ncd"` |
| `ConvertEols_NonFastPath_DoesNotCoalesceWithPrecedingTyping` | Undo が no-op で変換後本文のまま |
| `ConvertEols_NonFastPath_ThenUndo_RestoresSavePoint` | `Assert.True() Failure`(変換後 `Modified` が false) |
| `ConvertEols_NonFastPath_OnDirtyDocument_ThenUndo_FiresNoSavePointEvents` | `Expected: "a\nb\ncd" / Actual: "a\r\nb\r\ncd"` |
| `ConvertEols_NonFastPath_KeepsMouseDragging` | `Assert.True() Failure`(`ReplaceSource` が false へ潰す) |
| `ConvertEols_NonFastPath_KeepsWheelAccumulation` | `_wheelAccum が変換で破棄された疑い(TopLine=10, before=10)` |
| `ConvertEols_NonFastPath_RaisesUiaEventsAfterCaretRestore` | `Expected: [("TextChanged",8,8),("SelectionChanged",8,8)] / Actual: [("TextChanged",0,0),("SelectionChanged",0,0)]` |

**PASS(= main でも緑=「in-place 化しても挙動が変わらない」ことの対照群)**。
既存の `ConvertEols_*` に加え、本タスクで足した次の新規テストが main でも緑だった:

- `ConvertEols_FastPath_RecordsNothingInHistory`
- `ConvertEols_NonFastPath_OnSavedDocument_FiresNoSavePointEvents`
- `ConvertEols_NonFastPath_OnDirtyDocument_FiresNoSavePointEvents`
- `ConvertEols_NonFastPath_ThenSetSavePoint_FiresReachedOnce`
- `ConvertEols_NonFastPath_OnDirtyDocument_ThenSetSavePoint_FiresReachedOnce`
- `ConvertEols_NonFastPath_UpdatesUiaSnapshot`
- `ConvertEols_NonFastPath_FiresUpdateUiOnce` / `ConvertEols_FastPath_FiresNoUpdateUi`
- `ConvertEols_NonFastPath_RaisesTextChangedAndSelectionChangedOnce` / `ConvertEols_FastPath_RaisesNoUiaEvents`
- `ConvertEols_NonFastPath_DuringComposition_CancelsFirst` / `ConvertEols_FastPath_DuringComposition_DoesNotCancel`
- `ConvertEols_NonFastPath_ClearsCellHighlight` / `ConvertEols_FastPath_KeepsCellHighlight`
- `ConvertEols_NonFastPath_ResetsDesiredXpx` / `ConvertEols_FastPath_KeepsDesiredXpx`
- `ConvertEols_NonFastPath_KeepsHorizontalScroll`(下記 (7) の退行を捕まえた網。main では緑)

#### (2) §5.2 契約表(9 行)+ 「意図的に変える点」1 件 + §10.12 (1) の 2 行 の充足状況

見出しの数え方を訂正する(レビュー I-6)。**§5.2 の表は 9 行**で、そこに §5.2 本文の
「意図的に変える点」(UIA `SelectionChanged` の発火時点)1 件と §10.12 (1) が補った 2 行を
足して **12 項目**。さらに本タスクが自力で 1 項目(`DesiredXpx`)を回収したので **13 項目**を
1 行ずつ検証した。

| # | 契約 | 実装 | 網 |
|---|------|------|--------|
| 1 | caret / anchor の論理位置復元 | `(m, k)` 分解 → `_caretCtrl.SetSelection` を維持(位置計算は変更なし) | 既存 `..._PreservesCaretLogicalPosition_*` / `_PreservesAnchorForSelection` / `_CaretRequestedMidCrlf...` |
| 2 | `_topLine` / `_topSegment` / `_scrollX` 復元 | `SetTopPosition` + `ScrollX` を維持。in-place 化で値を潰さないため**常に no-op になった** | `ConvertEols_NonFastPath_KeepsHorizontalScroll`(topLine / scrollX 両方) |
| 3 | system caret 再配置 | `if (_hasFocus) PositionCaret()` を維持。#2 の no-op 化により**これが system caret 更新の唯一の経路になった** | **網なし**(Win32 `SetCaretPos` は自動テストで観測できない)。L5 で確認 |
| 4 | UIA スナップショット更新 | `_uia.OnSnapshotChanged` を明示。**caret 復元の後**に置いた(下記 (5)-2) | `ConvertEols_NonFastPath_UpdatesUiaSnapshot` |
| 5 | UIA `TextChanged` 発火 | `_uia.RaiseTextChanged()` を明示 | `..._RaisesTextChangedAndSelectionChangedOnce`(回数)+ `..._RaisesUiaEventsAfterCaretRestore`(発火時点の caret) |
| 6 | `UpdateUI` 発火 | `UpdateUI?.Invoke` を明示。**発火時点が「caret を 0 に潰した直後」から「caret 復元後」へ移った**(コード品質レビュー m-3・下記 (10)) | `..._FiresUpdateUiOnce` / `ConvertEols_FastPath_FiresNoUpdateUi`(回数のみ。時点の網は無し) |
| 7 | スクロールバー同期 / `Invalidate` | **垂直のみ**明示的に呼ぶ。**水平は意図的に呼ばない**(下記 (7)=レビュー I-1 のプローブが実測した退行)。`Invalidate()` は caret 復元後に 1 回 | `ConvertEols_NonFastPath_KeepsHorizontalScroll`(水平を呼ばない決定を固定)。`Invalidate` は**網なし**(下記 (6)) |
| 8 | `_cellHighlight` 無効化 | `_cellHighlight = null;` を維持(`ClearHighlight()` ではなく直接代入=`ReplaceSource` と同形) | `ConvertEols_NonFastPath_ClearsCellHighlight` / `ConvertEols_FastPath_KeepsCellHighlight` |
| 9 | IME 未確定の確定キャンセル | `if (IsComposing) CancelCompositionAndDefault();` を差し替え直前に維持 | `..._DuringComposition_CancelsFirst` / `ConvertEols_FastPath_DuringComposition_DoesNotCancel` |
| 10 | **意図的に変える点**: UIA `SelectionChanged` を caret 復元後に 1 回 | `RaiseUiaSelectionEvents` ガード付きで `RaiseTextChanged` の後に 1 回 | `..._RaisesUiaEventsAfterCaretRestore`(main = caret 0/0・切替後 = caret 8/8 を実測) |
| 11 | **`_wasModified` の同期**(§10.12 (1)) | `_wasModified = _buffer.Modified;` を代入(`ReplaceSource:301` と同じ位置)。`AfterEdit` は呼ばない | SavePoint 系 5 件(下記 (4)) |
| 12 | **`MouseDragging` / `_wheelAccum` リセット**(§10.12 (1)) | **書かない**(意図的に落とす) | `..._KeepsMouseDragging` / `..._KeepsWheelAccumulation`(いずれも main で赤) |
| 13 | **`_caretCtrl.DesiredXpx`**(設計書の漏れ・下記 (8)) | 挙動不変を優先し `-1` 代入を維持 | `..._ResetsDesiredXpx` / `ConvertEols_FastPath_KeepsDesiredXpx` |

#### (3) `SavePointLeft` / `SavePointReached` の発火列(§10.12 (1) の要求)

非 fast-path の `ConvertEols` を含む経路の発火列。`(clean)` は直前に `SetSavePoint` 済み、
`(dirty)` は保存点以後に編集済みの文書。○ の行は**同一のテストが main でも切替後でも緑**
であることで実測した((1) の対照群)。

| 経路 | main | 切替後 | 一致 |
|------|------|--------|------|
| `ConvertEols` 単体 (clean) | (なし) | (なし) | ○ |
| `ConvertEols` 単体 (dirty) | (なし) | (なし) | ○ |
| **成功パス**: `ConvertEols` → `SetSavePoint` (clean) | `Reached` | `Reached` | ○ |
| **成功パス**: `ConvertEols` → `SetSavePoint` (dirty) | `Reached` | `Reached` | ○ |
| **失敗パス(main の機構)**: `ConvertEols` → `SetOrReplaceSource(snapshotBefore)` | (なし) | — (Task 4 で撤去) | — |
| **失敗パス(新機構)**: `ConvertEols` → `Undo` (clean) | 該当なし(main は Undo 不能) | `Reached` | ✕(新規) |
| **失敗パス(新機構)**: `ConvertEols` → `Undo` (dirty) | 該当なし(main は Undo 不能) | (なし) | ✕(新規) |

- `AfterEdit()` を呼ばず `_wasModified` を代入で揃えたことにより、**保存処理の途中で
  `SavePointLeft` が焚かれる新規挙動は発生しない**(§10.12 (1) の要求を満たす)。
- 失敗パスの `Reached` 1 件は Task 4 が持ち込む**新規挙動**である。clean 文書の保存に失敗して
  `Undo` で保存点のルートへ戻ると `Modified` が true→false へ遷移し `SavePointReached` が
  1 回焚かれる。main は `ReplaceSource` の直接代入でイベントを焚かなかったが、
  **タブラベルの最終状態は両者とも「未変更」で一致する**(main は clean のまま動かない /
  新機構は一度も dirty 表示にせず `Reached` を打つ)。dirty 文書では `Undo` 後も
  `Modified` が true のままなので遷移が無く、発火もしない(main と同じ)。
- なお `ConvertEols` 直後の `Modified` の**値**は main と異なる(main=false / 切替後=true)。
  §5.1 の「`_savedRoot` を触らない」決定の直接の帰結である。**イベントは焚かない**ので
  App 層の表示はこの瞬間には変わらない。値の差が観測されるのは Task 4 のロールバック判定と、
  `WriteToPath` が例外で抜けた後に `doc.Editor.Modified` を読む経路だけである(下記 (9))。

#### (4) 発火列を固定しているテスト

| テスト | 固定する列 |
|---|---|
| `ConvertEols_NonFastPath_OnSavedDocument_FiresNoSavePointEvents` | clean で `ConvertEols` 単体 → 空 |
| `ConvertEols_NonFastPath_OnDirtyDocument_FiresNoSavePointEvents` | dirty で `ConvertEols` 単体 → 空 |
| `ConvertEols_NonFastPath_ThenSetSavePoint_FiresReachedOnce` | clean 成功パス → `["Reached"]` |
| `ConvertEols_NonFastPath_OnDirtyDocument_ThenSetSavePoint_FiresReachedOnce` | dirty 成功パス → `["Reached"]` |
| `ConvertEols_NonFastPath_ThenUndo_RestoresSavePoint` | clean 失敗パス → `["Reached"]` |
| `ConvertEols_NonFastPath_OnDirtyDocument_ThenUndo_FiresNoSavePointEvents` | dirty 失敗パス → 空 |

#### (5) 計画案から変えた点

1. **戻り値は `ReplaceAllRecordingUndo` の結果をそのまま返す**(計画は「末尾 `return true;`」も
   選択肢として提示していた)。理由: Task 2 が「呼び出し側は経路から推論せず戻り値で判定する」
   契約を明示しており、`IsEolAlreadyUniform` にバグがあって無変化の文書が非 fast-path へ
   落ちた場合でも `false` を返せば Task 4 が余分な `Undo` を打たない(=ユーザーの直前の編集を
   消さない)。危険な向きへ倒れない側を選んだ。なお現行コードで空文書・改行なし文書は
   いずれも `IsEolAlreadyUniform` が true を返すため、この分岐が実際に false になる経路は無い
   (`ConvertEols_NoLineBreaks_ReturnsFalse` が fast-path 側で固定している)。
2. **`_uia.OnSnapshotChanged` を caret 復元の後に置いた**(レビュー I-4 を受けて論拠を両側で書く)。
   - 前に置くと: 新スナップショット + **新本文では範囲外になりうる旧 caret オフセット**が
     同時に見える窓ができる。
   - 後に置くと: **対称の窓が開く**。LF→CRLF は本文が伸びるので、`SetSelection` から
     `OnSnapshotChanged` までの間、RPC スレッドの `IUiaTextHost.GetSelection()`
     (`UiaTextHostAdapter.cs` — **クランプしない**。クランプするのは `GetTextRange` だけ)は
     **旧スナップショット長を超えるオフセット**を返しうる。main は caret=0 なのでこの窓が無い。
   - **整合は一貫性の根拠であって安全性の根拠ではない**(コード品質レビュー I-4 (3))。
     順序を `AfterEdit` に揃えたのは一貫性のためだが、**安全なのは別の理由**である:
     `TextProviderImplV2.GetSelection()` は生オフセットを `TextRangeProviderV2` へ渡すが、
     **その ctor が `Math.Clamp(start, 0, owner.Host.TextLength)` を掛ける**
     (`src/kxEdit.Accessibility/TextRangeProviderV2.cs`)。したがって窓の中で RPC スレッドが
     観測しうる最悪値は「**旧文書末尾に縮退した選択範囲**」であり、例外も範囲外読みも起きない。
     **将来 `TextProviderImplV2.GetSelection` がクランプを通さない経路を足したら本判断は再検証が要る。**
   - **窓そのものを縮めた**(コード品質レビュー I-4 (2)): `_uia.OnSnapshotChanged(...)` を
     `SetSelection` の**直後**へ移し、窓から `PositionCaret`(`ComputeCaretPoint` の
     レイアウト計算を含む)と `Invalidate` を外した。「caret 先 → snapshot 後」の
     `AfterEdit` 整合は保たれるのでデメリットが無い。
3. **`builder.Build()` を IME 取消より前に評価する**。旧 `ReplaceSource(builder.Build())` は
   引数評価が先=`Build()` が例外(上限超過・carry の不正 UTF-8)で抜けるとき IME 未確定は
   取り消されないまま throw していた。順序を保つため `var rebuilt = builder.Build().Current;`
   を先に置いた。
4. **`KeepsMouseDragging` / `KeepsWheelAccumulation` を追加した**。§10.12 (1) の
   「意図的に落とす」という決定はコード上「何も書かない」なので、網が無いと将来の読者が
   「書き忘れ」と読んで復活させうる。決定をテストで固定した。
5. **UIA イベント系のテストを別クラス `EditorControlConvertEolsUiaEventTests` に置いた**。
   `TestHook_ForceUiaListen` が static のため、既存の `EditorControlUiaEventsTests` と同じ
   `[Collection("UiaEventHook")]` に入れる必要がある(ファイルは
   `EditorControlConvertEolsTests.cs` のまま)。
6. **`Modified` / SavePoint 系のテストを計画外に足した**。計画のテスト案は Undo 履歴だけを
   見ており、§10.12 (1) が要求する発火列の固定が無かった。
7. **「水平を再計算しない」判断が依存する不変条件をコードに書き出した**
   (コード品質レビュー I-2)。結論(「EOL 変換で水平 extent は不変」)は **Editor の外**にある
   2 つの実装詳細に全面的に依存している:
   - `src/kxEdit.Core/Buffer/Piece.cs` の `Breaks` 規約(LF / 単独 CR をそれぞれ 1 と数える)
     → 各改行が target 1 個へ 1:1 に写るので `LineCount` 不変。ここが変われば
     **垂直の「値は動かない」も水平の「extent 不変」も同時に崩れる**。
   - `src/kxEdit.Core/Layout/ViewportLayout.cs` の `VisualRow.SegmentLength` が
     「改行を含まない」こと → 幅測定の対象文字列が変換前後で同一。
   これは §10.5 が `PieceStats.Breaks` への結合を却下した構図と同型(「定義が変われば黙って
   誤動作する」)であり、今回は結合が暗黙だったぶん見つけにくい。該当行のコメント冒頭に
   「前提」として明記した。
8. **`ReplaceSource` の `<remarks>` に逆方向参照を入れた**(コード品質レビュー I-1)。
   `ConvertEols` → `ReplaceSource` の参照は元々あったが**一方向**で、
   「`ReplaceSource` に副作用を 1 行足したとき `ConvertEols` も直す必要がある」という
   手がかりがコードにもテストにも無かった(`ReplaceSource` に副作用を足しても
   `ConvertEols_*` は 1 件も赤くならない)。同種の列挙は `SetSource` / `AfterEdit` にもあり
   **現在 4 箇所に散っている**。防げないので「防げない」と書いた。
9. **可視ホストを `HostForm.CreateVisible()` に揃えた**(コード品質レビュー m-1)。
   `new Form() + Show()` はフォームをアクティブ化し、CI 非対話セッションで
   フォーカス奪取・チラつきを招く。`EditorControlBoundingRectsTests.GetBoundingRectangles_SubtractsScrollX`
   の手順(`CreateVisible()` → `ClientSize` → `PerformLayout()` → `DoEvents()`)に合わせた。

#### (6) 網の状況(レビュー I-1 を受けた訂正)

**初版の「6 項目はいずれも新しい test hook が要る」は過小申告だった。** レビュアーの指摘のとおり、
「嘘の安全宣言を作らない」の裏返しとして**書けるはずの網を「書けない」と宣言する**のも同種の事故である。
既存インフラだけで次の観測ができた:

| 項目 | 使った既存インフラ | 結果 |
|---|---|---|
| `_wheelAccum` | `MouseInputTests` の `OnMouseWheel` 直叩き(40x3=120 の蓄積を黒箱で観測) | 網を追加(main で赤) |
| IME 確定キャンセル | `EditorControl.Ime.cs` の `__TestIsComposing()` / `__TestApplyComposition` | 網を追加(main で緑=対照群) |
| `_cellHighlight` | セットは public `HighlightCharRange`、読みは private フィールドのリフレクション(`VisualRowScrollTests.VScroll` と同流儀) | 網を追加(main で緑) |
| `DesiredXpx` | `CaretController.DesiredXpx` は public。`_caretCtrl` は `UiaTextHostAdapterTests` に先例のあるフィールドリフレクションで借りる | 網を追加(main で緑) |
| スクロールバー同期 | 同上のリフレクションで `_vscroll` / `_hscroll` を観測するプローブ | **退行を発見**(下記 (7))。網を追加 |
| UIA 発火時点の caret | `UiaTextHostAdapter.PerformRaiseAutomationEvent`(`protected internal virtual` seam)を override したアダプタを `_uia` へリフレクション注入 | 網を追加(main で赤) |

**網を張っていない項目は 2 つだけ**である:

- **`Invalidate`**: `EditorControl` は sealed で `Invalidate` を差し替える seam が無く、
  実用的な観測手段が無い。**これは「観測手段が無い」で正しい。**
- **system caret 再配置(`PositionCaret`)**: Win32 `SetCaretPos` の結果は自動テストで
  観測できない。L5 の対象。

**`OnSnapshotChanged` の順序について、網が殺せる変異と殺せない変異**
(コード品質レビュー I-4 (1)。`ConvertEols` 内の呼び出しだけを一意にアンカーして実測。
素の `_uia.OnSnapshotChanged(_buffer.Current);` は `SetSource` / `ReplaceSource` /
`AfterEdit` にも現れるので、1 個目を置換すると別メソッドが変異する=§10.12 (4) の罠を踏んだ):

| 変異 | 結果 | 撃墜したテスト |
|---|---|---|
| `OnSnapshotChanged` を削除 | **撃墜** | `..._UpdatesUiaSnapshot` / `..._RaisesUiaEventsAfterCaretRestore` |
| UIA イベント発火の**後**へ移す | **撃墜** | `..._RaisesUiaEventsAfterCaretRestore`(発火時点の `TextLength` が旧値 14 になる) |
| `SetSelection` の**前**へ移す | **生存** | — |

最後の 1 つは**殺せない**。発火時点では caret も `TextLength` もどちらの順序でも新値であり、
差が出るのは `SetSelection` と `OnSnapshotChanged` の間の**別スレッドからの観測**だけだからである。
`OnSnapshotChanged` は非 virtual で `_uia` の静的型経由で呼ばれるため、サブクラスでは横取りできない。
**網のために production の可視性を広げるのは順序が逆**なので `virtual` 化はしない(レビュアーの指示)。
上記 (5)-2 の窓縮小によりこの 2 文は隣接しており、変異が動かす距離は 1 文である。

#### (7) レビュー I-1 のプローブが実測した退行 —— 水平スクロール位置が保存のたびに失われていた

> **先に結論**: これは初版実装が持ち込んだ退行であると同時に、**main も条件次第で同じ挙動を持つ
> 既存バグ**だった(本節末尾の「訂正」参照)。修正は挙動不変ではなく**挙動改善**である。

`_vscroll` / `_hscroll` の観測可能な状態を `ConvertEols` の前後で比べるプローブを書いたところ、
**初版実装(commit `e185ef9`)は main と異なる結果を出した**。

fixture A: 40 行・行 3 だけ 200 文字の長い行・`TopLine = 5`(長い行は先頭スクリーンフル内)・`ScrollX = 30`。

```
BEFORE           H(max=3265, lc=267, val=30, vis=True)  topLine=5, scrollX=30
main   の AFTER  H(max=3265, lc=267, val=30, vis=True)  topLine=5, scrollX=30
e185ef9 の AFTER H(max=0,    lc=1,   val=0,  vis=False) topLine=5, scrollX=0   ← 退行
```

原因: `UpdateHorizontalScrollbar` は「**可視行**のうち最長 pixel 幅」で extent を決めるので、
評価時点の `_topLine` に依存する。`ReplaceSource` は `_topLine=0` に潰した**後**に呼んでいたが、
in-place 化では `_topLine` を潰さないため、復元済みの起点(=長い行が見えない窓)で評価される。
その結果 `HideAndResetHScroll()` が走って `_scrollX` が 0 に落ち、直後の
`ScrollX = savedScrollX` は「HScroll 非表示」で早期 return する。
**Ctrl+S のたびに水平スクロール位置が消える**という、晴眼・弱視ユーザーに直接効く退行だった
(CLAUDE.md §2「晴眼・弱視ユーザーも第一級」)。main が fixture A で無事なのは
`_topLine=0` 評価がたまたま長い行を拾うからにすぎない(下記「訂正」)。

**修正**: 水平スクロールバーの再計算を**呼ばない**ことにした。EOL 変換は行本文(改行を除く)も
`LineCount` も変えないので**水平 extent は不変**であり、そもそも再計算する理由が無い
(`ReplaceSource` が呼ぶのは別文書への差し替えだから)。以後の編集/リサイズ/スクロールが
従来どおり更新する。垂直は `LineCount` 由来で値が動かないうえ `_vscroll.Value` の同期という
契約があるので**そのまま呼ぶ**(プローブでも main と同一)。

**網**: `ConvertEols_NonFastPath_KeepsHorizontalScroll`。commit `e185ef9` に対して
`Expected: 30 / Actual: 0` で赤、main と修正後で緑。

**訂正(コード品質レビュー I-3): 「main では緑」は上の fixture に限った事実だった。**
上の fixture は長い行(行 3)が**先頭スクリーンフル内**にあるため、main の `_topLine=0` 評価でも
長い行を拾って HScroll が残る。**長い行が先頭スクリーンフルの外にある文書では main も
同じ経路で `_scrollX` を失う**。実測(fixture B: 60 行・行 40 だけ `'W'x200`・
`ClientSize 300x120`・`TopLine=40`・`ShowLineNumbers=true`・`ScrollX=30`。
前提 assert は main でも両方 PASS):

```
c167fba (main)   ConvertEols 後  Expected: 30 / Actual: 0   ← 失う(main 既存バグ)
本タスク修正後   ConvertEols 後  PASS                        ← 保つ
```

したがって「水平を再計算しない」決定は、初版実装が持ち込んだ退行の修正であると同時に
**main 既存バグの解消**でもある=**挙動不変ではなく挙動改善**であり、CLAUDE.md §2 の
「意図的な挙動変更は文書化する」の対象になる。PR description にも記載すること。
網は `ConvertEols_NonFastPath_KeepsHorizontalScroll_WhenLongLineOffFirstScreen`(**main で赤**)。

**この訂正自体が §10.8 の教訓の再演である**(「過大申告が 1 つ混じると資料全体の信頼が落ちる」)。
初版の §10.13 (7) は fixture 1 本の観測から「main には無い退行」と一般化していた。

**教訓**: 「`ReplaceSource` の副作用のうち意味を失うものだけ再現する」という判断は、
**副作用の入力が何に依存しているか**まで見ないと安全でない。`UpdateHorizontalScrollbar` は
`_topLine` に依存しており、`ReplaceSource` はそれを 0 に潰してから呼んでいた。
「同じ呼び出しを同じ位置に置く」だけでは等価にならない。

#### (8) 設計書 §5.2 自身の記述の不足/誤り(レビューで確定・§5.2 本体は書き換えない)

1. **§5.2 の記述漏れは 2 件ではなく 3 件だった。** §10.12 (1) が `_wasModified` と
   `MouseDragging` / `_wheelAccum` を補ったが、**`_caretCtrl.DesiredXpx`(`ReplaceSource` が
   `-1` へ潰す)も抜けていた**。本タスクで自力で回収し、挙動不変を優先して `-1` 維持と決定した
   ((2) の #13)。
2. **§5.2 の「意図的に変える点」の締め「SR の実発声への影響は L5 でのみ判定できる」は
   範囲が広すぎた。** L5 固有なのは**実発声**だけで、**機構(発火時点の caret がどこか)は
   L2 で固定できる**。実際 `PerformRaiseAutomationEvent` seam で
   main = `caret 0/0` / 切替後 = `caret 8/8` を実測し、`..._RaisesUiaEventsAfterCaretRestore`
   で固定した。L5 に残るのは「NVDA がその差をどう読むか」だけである。

#### (9) Task 4 への申し送り

1. **`Save_ExistingPathIsDriveRoot_ReportsError_AndRollsBackModified` は今、何も守っていない**。
   本タスクの切替後もこのテストは**緑のまま**だが、それは
   「`ReferenceEquals` が常に true → ロールバックが no-op → しかし `_savedRoot` を触らないので
   `Modified` は true のまま」という理由で `Assert.True(doc.Editor.Modified)` が
   **空振りで通っている**からである。本文は EOL 変換後のまま残る(サイレント喪失)。
   Task 4 はこのテストに**本文の内容 assert を足す**か、
   `Save_WriteFailure_RollsBackContentEol_And_KeepsModifiedFlag` と同じ観測に寄せること。
2. Task 3 の時点で実際に赤くなった App テストは次の 2 件。いずれも**本タスクが追加したものではなく、
   A-10 ブランチ(`ffe35d6`)が残した既存の回帰網**である。**独立した既存網が今回の破壊を
   検出した**という良い事例なので、Task 4 の担当者は「ここは既存網が守っている」と読んでよい。
   Task 4 の完了条件はこの 2 件が緑に戻ることである。
   - `FileControllerTests.Save_WriteFailure_RollsBackContentEol_And_KeepsModifiedFlag`
     — `Expected: "x\ny" / Actual: "x\r\ny"`
   - `FileControllerTests.SaveAs_WriteFailure_RollsBackContentEol`
     — `Expected: "a\r\nb" / Actual: "a\nb"`
3. `WriteToPath` の XML doc(`FileController.cs:812-819` 付近)は「`ConvertEols`(非 fast-path)は
   `ReplaceSource(builder.Build())` で新規 TextBuffer に差し替える」と書いており、
   **本タスクで事実と食い違った**。Task 4 で書き換えること(§5.3 が既に指示しているが、
   旧説明が残ると次の読者を誤導するため再掲する)。
4. `ConvertEols` の戻り値を捨てている呼び出しが `FileController.cs:843` にある。Task 4 は
   `bool converted = doc.Editor.ConvertEols(doc.Editor.EolMode);` として catch 節へ渡すこと。
   **`converted` が false のときに `Undo()` を打ってはならない**(fast-path では変換エントリが
   積まれていないので、ユーザーの直前の編集が消える)。
5. (7) の教訓は Task 4 にもそのまま効く。`WriteToPath` のロールバックで caret / スクロールを
   明示復元する(§10.12 (2) の決定)とき、**復元値を捕捉する時点**と**復元を適用する時点**で
   依存する状態(`_hscroll.Visible` など)が変わっていないかを確認すること。

#### (10) コード品質レビューで却下したもの / 申し送りにしたもの

- **却下: byte 走査ブロックの抽出(m-2)**。レビュアー自身が「§10.6 が `EolSegments` seam として
  既に所有しており、本ブランチで先に取ると §10.6 の judgement(2 系統の変更を混ぜない)を崩す」
  として却下を推奨し、同意した。**§10.6 を回収するときは `ConvertEols` の byte 走査ブロックが
  最初の対象になる**(`EmitEol` / `FlushBuf` / `pendingCr` の carry を含む一式)。
- **`UpdateUI` の発火時点が変わった(m-3)**。旧経路は `ReplaceSource` 内=caret を 0 に潰した
  直後に発火していたが、in-place 化で caret 復元後になった。**実害なしを確認済み**:
  `MainForm.UpdateStatus()` の EOL 表示は `doc.State.LineEnding` を読んでおり `Editor.EolMode`
  ではない。caret 依存の表示も、成功パスでは `WriteToPath` 後段の `_metaChanged()` が再描画するため
  main の「保存後に『行 1, 桁 1』が残る」挙動は元々マスクされていた。
  **新実装のほうが素の状態では正しい**(ハンドラが読む caret が実際の位置になる)。
  網は回数のみで、発火「時点」の網は張っていない。
- **申し送り(m-4): テストヘルパの 4 コピー目**。本ファイルの `MakeHosted` は
  `MouseInputTests.MakeControl` / `VisualRowScrollTests.MakeControl` と、`SendMouseWheel` は
  `MouseInputTests.SendMouseWheel` と同型である。ただし既存はすべて `private static` で
  再利用不能な形なので、これは本ファイルが作った債務ではなく**既存慣行の踏襲**。
  `tests/kxEdit.Editor.Tests/TestHost.cs` に `internal static class EditorProbe`
  (`MakeHosted` / `SendMouseWheel` / `Field<T>(obj, name)`)を置いて集約する案を
  **独立テーマとして申し送る**(本ブランチでは実施しない。テスト資産全体に触る変更になり、
  A-9 / A-11 の差分に混ぜると挙動不変の証明が読めなくなるため)。

### 10.14 Task 4(保存失敗ロールバックの組み替え)実装時の決定と逸脱記録(2026-08-29 追記)

#### (1) ロールバック API の形(実装計画 Task 4 Step 4 からの逸脱)

```csharp
public bool UndoEolConversion(bool conversionRecorded, int anchorBefore, int caretBefore)
```

計画は引数 1 つ(`conversionRecorded`)で、キャレット復元の受け渡し方は「実装者が設計する」
としていた。**変換前の anchor / caret を呼び出し元から受け取る形**にした理由:

- §10.12 (2) が要求する「保存失敗前の位置へ明示復元」に必要な値は、`EditorControl` の中に
  **残っていない**。`ConvertEols` は変換後の等価な論理位置へ caret を移してしまうため、
  変換後の状態から変換前の char オフセットは復元できない(復元計算をもう一度やる案は、
  `ConvertEols` の `(m, k)` 分解を逆向きに再実装することになり、2 つ目の写しを作る)。
- 代替案「`ConvertEols` が捕捉して private フィールドに退避し、`UndoEolConversion` が黙って読む」は
  **採らなかった**。退避値の有効期間がコードから読めず(`ConvertEols` と catch 節の間に何が
  起きても構文上は通る)、将来「間に別の編集が入る」変更が入ったとき黙って古い位置を復元する。
  引数にすれば「呼び出し元が変換前に捕捉した値」という契約が型と呼び出し位置に出る。
- **anchor と caret の 2 値**にしたのは、選択範囲を保つため。`SetTo` 系(1 値)だと保存失敗の
  たびに選択が解除される。網は `UndoEolConversion_RestoresGivenSelection_NotUndoResultCaretPos`
  (`anchor != caret` の非既定状態から始める)。
- 捕捉に使うのは既存 public API(`SelectionAnchor` / `CaretCharOffset`)だけで、
  App 層向けに新しい観測点を増やしていない。

**捕捉は `try` の外**に置いた。`ConvertEols` 自身が throw する経路((4) の
`DocumentTooLargeException`)でも catch 節から参照するため。

#### (2) スクロール位置は「触らないことが復元」である

§10.12 (2) は「キャレットとスクロール位置を保存失敗前の位置へ明示復元する」と書いたが、
実装は**スクロールについては何も書かない**形になった。`ConvertEols` の in-place 化以降、
変換もルート差し戻しも `_topLine` / `_topSegment` / `_scrollX` を動かさないので、
**何もしないことが復元**である。`ScrollX = saved` 等の明示復元を足すと、値が同じで常に
早期 return する=網で殺せない等価な行が増えるだけになる。

逆に「編集後の定番処理」を素直に呼ぶと壊れる。網
`Save_WriteFailure_OnNonFastPathEol_RestoresCaretAndScroll` は次の 2 変異を実測で撃墜した((6) の表):

- `AfterEdit()` を呼ぶ → `BringCaretIntoView` が復元済みの `TopLine` を動かす。
- `UpdateHorizontalScrollbar()` を呼ぶ → 復元済みの `_topLine` で評価され、長い行が可視域に
  無いと `HideAndResetHScroll` が走って `_scrollX` が 0 に落ちる(§10.13 (7) と同じ罠)。

**fixture 設計の要点**: 後者は「長い行が可視域に残る」fixture では**生存する**。
行 0 だけを長くして `ScrollX` を非 0 に置いた後、`TopLine` を遠く(150 行目)へ動かして
長い行を可視域外にしてある(`TopLine` セッターは水平スクロールバーを再計算しないので、
この状態は作れる)。§10.13 (7) の訂正がまさに「fixture 1 本から一般化した」失敗だったので、
今回は最初から罠を踏める形に寄せた。

垂直スクロールバーの再計算も**呼ばない**。EOL 変換は `LineCount` を変えず、取り消しても
行数は同じなので、呼んでも no-op にしかならない(呼べば等価変異が 1 つ増える)。

#### (3) Redo は捨てる(§5.3 が実装時決定に委ねた点の結論)

`TextBuffer.DropRedo()`(→ `UndoHistory.ClearRedo()`)を足し、ロールバックの `Undo` が
`_redo` へ積み直したエントリをその場で捨てる。

- 捨てないと、保存に失敗しただけで「やり直し」メニューが有効になり、Ctrl+Y が
  **ユーザーが一度も要求していない全文 EOL 変換**を再適用する。
- 保存失敗**前**にユーザーが持っていた Redo は原理的に復元できない
  (§10.11 (4) で受容済み。`Record` が既に `_redo.Clear()` している)。捨てる側にすると
  ロールバック後の Redo スタックは「空」= 到達可能な状態のうち保存前に最も近い。
- 実装計画は「どちらを採るかは実装時にユーザーへ確認」としていたが、上記のとおり
  非対称(捨てない側に利点が無い)なので確認を待たずに決めた。**この判断自体を申し送る**:
  PR レビューで異論があれば `DropRedo` の呼び出し 1 行を外すだけで反転できる。

`ClearRedo` は `Clear()` と取り違えると Undo 履歴まで消える(=ユーザーの編集が全部消える)ので、
L1 に `DropRedo_ClearsRedoOnly_AndKeepsUndoStack` / `DropRedo_DoesNotTouchSavePoint` を置いた。
`Clear()` へ置換する変異は両方を撃墜する(実測)。

#### (4) `DocumentTooLargeException` は拾う(§10.11 (8) の申し送りの回収)

`WriteToPath` の catch フィルタへ追加した。**調査結果**:

- `ConvertEols` で上限超過が起きうるのは走査中の `builder.Add`(内部 `AddChunk`)と
  最後の `builder.Build()` の 2 箇所。どちらも **`_buffer` に触る前**である
  (`rebuilt = builder.Build().Current` → `IsComposing` 判定 → `ReplaceAllRecordingUndo` の順)。
- したがって例外が飛んだ時点で本文・キャレット・選択・スクロール・Undo 履歴はすべて未変更、
  `eolConverted` も `false` のままで、**ロールバックは no-op でよい**。
- **訂正(仕様適合レビュー 5-2)**: 初版はここに `EolMode` も挙げていたが**誤り**。
  同じ `try` の先頭にある `ApplyEol(doc)` が `EolMode = doc.State.LineEnding` を先に代入するので、
  `EolMode` は既に新値になっている。ただし (a) 代入元はユーザーが選んだ `State.LineEnding` であり、
  (b) main も同じ位置で同じ代入をして失敗時に戻さないので、**挙動は main と同じ**である
  (「本文の書き換えは起きていない」という結論は変わらない)。
- `try` 内の他の文(`ApplyEol` / `ReadOnly` セッター / `TextFileService.Save` / `SetSavePoint` /
  `DocumentManager.UpdateLabel` / `_metaChanged`)は `TextBufferBuilder` を使わないので、
  このフィルタが新たに飲み込む例外源は無い。

拾う前は**未処理例外でアプリが落ちる**(= 他タブの未保存分も道連れ)。拾えば
「保存できませんでした: 文書サイズ上限(512 MB)を超えました。」が出て編集を続けられる。

**網は張れない**: 発火には 512MB 級の文書が要る。`TextBufferBuilder.MaxTotalBytes` は
`internal init` で、`ConvertEols` が内部で `new TextBufferBuilder()` するため注入点が無い。
「木を組み終えてから `_buffer` に触る」という上の順序も、同じ理由でテストでは固定できていない
(**コードの読みでしか守られていない**)。順序を入れ替える変更をするときは本節を再検証すること。

#### (5) 訂正: §10.13 (3) が予告した「失敗パスの `SavePointReached` 1 件」は発生しない

§10.13 (3) の発火列の表は、Task 4 が `Undo` 相当(= `AfterEdit` 経由)を使う前提で
「clean 文書の保存失敗 → ロールバックで `Reached` が 1 回焚かれる(新規挙動)」と書いた。
**実装では焚かない。** `AfterEdit()` を使わず、`_wasModified` を `ConvertEols` と同じく
**代入で揃える**形にしたため。§10.13 (3) の表の該当 2 行は次に置き換わる:

| 経路 | main | Task 4 実装 | 一致 |
|------|------|------------|------|
| 失敗パス: `ConvertEols` → ロールバック (clean) | (なし) | (なし) | ○ |
| 失敗パス: `ConvertEols` → ロールバック (dirty) | (なし) | (なし) | ○ |

保存に失敗しただけで保存点到達イベントが飛ぶのは筋が悪く、§10.12 (1) が
`SavePointLeft` について下した決定(保存処理の途中でイベントを焚かない)と非対称になる。
**保存操作そのもの(失敗・成功の両パス)の発火列は main と完全一致**し、Task 3 が持ち込んだ
「新規挙動」は Task 4 で消えた。ただし**保存成功後の Ctrl+Z は新規に `SavePointLeft` を焚く**
(main は保存で履歴が消えるので `CanUndo=False` =無反応。現行は変換を巻き戻してタブに「*」が付く)
—— これは A-11 が直した対象バグ(§1 / §4.4 / §8 の L5 項目 2)の帰結であり、**意図した挙動変更**である。
初版はここに「= A-11 全体で `SavePoint` 系の発火列は挙動不変」と書いていたが**言い過ぎ**だった
(仕様適合レビュー 4 が main と現行に同一プローブを入れて 11 ケース実測し、
保存成功直後の Ctrl+Z だけが `[Reached]` → `[Reached, Left]` と食い違うことを確認)。網は
`UndoEolConversion_OnSavedDocument_FiresNoSavePointEvents`(clean 文書から始める= 変換で
false→true、取り消しで true→false の**両方向の遷移が実在する**非既定条件)。

**`_wasModified` の代入自体は落とすと実害が出る**: 落とすと `_wasModified` が true のまま残り、
**次のユーザー編集で `SavePointLeft` が発火せずタブの「*」が出ない**(遷移検出が「もう dirty
だった」と誤認する)。晴眼・弱視ユーザーに直接効く経路(CLAUDE.md §2)なので App 層に
`Save_WriteFailure_OnNonFastPathEol_KeepsDirtyIndicatorWorking` を置いて固定した(実測で撃墜)。

#### (6) 網が実際に何を守っているか(変異実測)

判定はビルドの exit code、適用差分は毎回目視してから実行した(§10.4 のハーネスの罠対策)。
M3 / M4 / M5 は (11) の fixture 変更(caret を行 0 の外へ)後に**当て直して**撃墜を再確認した。

| # | 変異 | 結果 | 撃墜したテスト |
|---|------|------|--------------|
| M1 | `conversionRecorded` の判定を落とす(常に取り消す) | kill | `Save_WriteFailure_OnFastPathEol_DoesNotUndoUserEdit` |
| M2 | `UndoEolConversion` に `ReadOnly` ガードを足す(= `Undo` 流用と同じ) | kill | `Save_WriteFailure_WhileEditorReadOnly_StillRollsBackContentEol` |
| M3 | `SetSelection(anchorBefore, caretBefore)` → `SetTo(UndoResult.CaretPos)` | kill | `Save_WriteFailure_OnNonFastPathEol_RestoresCaretAndScroll` |
| M4 | 通知一式を `AfterEdit()` に置換 | kill | 同上(`TopLine` 150 → 2 = `BringCaretIntoView` が caret の行へ寄せる) |
| M5 | `UpdateHorizontalScrollbar()` を足す | kill | 同上(`ScrollX` 40 → 0) |
| M6 | `_buffer.DropRedo()` を落とす | kill | `Save_WriteFailure_OnNonFastPathEol_LeavesNothingToRedo` |
| M7 | `WriteToPath` の `UndoEolConversion` 呼び出しごと落とす | kill | 上記の非 fast-path 系すべて + `Save_ExistingPathIsDriveRoot_...` + A-10 の既存 2 件 |
| M8 | `UndoHistory.ClearRedo()` → `Clear()` | kill | `DropRedo_ClearsRedoOnly_AndKeepsUndoStack` / `DropRedo_DoesNotTouchSavePoint` / `Save_WriteFailure_OnNonFastPathEol_UndoesOnlyTheConversion` |
| M9 | `_wasModified = _buffer.Modified;` を落とす | kill | `Save_WriteFailure_OnNonFastPathEol_KeepsDirtyIndicatorWorking` |
| M10 | `_uia.OnSnapshotChanged(snap)` を落とす | kill | `UndoEolConversion_UpdatesUiaSnapshot` / `UndoEolConversion_RaisesUiaEventsAfterCaretRestore` |
| M11 | `_uia.OnSnapshotChanged(snap)` を UIA イベント発火の後へ移す | kill | `UndoEolConversion_RaisesUiaEventsAfterCaretRestore`(発火時点の `TextLength` が旧値) |
| M12 | 捕捉 2 行を `ConvertEols` の**後**へ移す | 初版 fixture では **生存** → (11) の fixture 修正後 kill | `Save_WriteFailure_OnNonFastPathEol_RestoresCaretAndScroll`(`Expected 209 / Actual 207`) |

**M7 は `Save_ExistingPathIsDriveRoot_ReportsError_AndRollsBackModified` も撃墜した**
=§10.13 (9)-1 が指摘した空振り(本文 assert が無く、ロールバックが no-op でも緑)は解消した。

**当てた変異は本表と (10) の表に挙げたものだけであり、「網が完全」という主張はしない**
(§10.8 の教訓)。**初版がここに書いた「未撃墜 4 項目」のうち 3 項目は過小申告だった** —— (10) で訂正する。

#### (7) 既存テストの説明文の更新(旧機構の説明を残さない)

`FileControllerTests` の rollback 系 3 件(`SaveAs_WriteFailure_RollsBackContentEol` /
`Save_WriteFailure_RollsBackContentEol_And_KeepsModifiedFlag` /
`Save_WriteFailure_FastPath_PreservesCaretAndScroll`)のコメントは
「旧 TextBuffer 参照へ戻す」「`!ReferenceEquals` guard を消す変異を kill する」と書いており、
**assertion は正しいのに説明が事実と食い違う**状態になった。assertion は一切変えずに説明だけ
新機構へ書き換えた。特に `..._FastPath_PreservesCaretAndScroll` は、A-11 後に kill する変異が
別物(「fast-path なのに取り消してしまう」)へ変わっており、そちらの弁別力は本テストの fixture
(`Text` セッター直後=履歴が空)には**無い**ことを明記して、担い手のテスト名を書いた。

`Save_ExistingPathIsDriveRoot_...` には §10.13 (9)-1 の指示どおり本文 assert を足した。
期待値は実行して確認した **`ConvertEols` 前の値(= ロールバックで復元されるべき値)**
`"xa\r\nb\r\nc"` である。実装計画 `:875` と初版の本節が使った「**ロールバック前**の値」という
表現は、文字どおり読むと「ロールバックする直前の値」= 変換**後**の値になり網が反転するので、
テストコメントともども復元先を名指しする表現へ直した(仕様適合レビュー 6)。

#### (8) L5 チェックリストへの追加候補

§10.12 (3) が回収を求めている項目に加えて、Task 4 由来で 1 項目挙げておく:

> 読み取り専用属性のファイル(または書き込み権限の無い場所)を EOL 変換が起きる条件で開き、
> 文書の途中にキャレットを置いて Ctrl+S。
> **期待**: 「保存できませんでした」ダイアログが出て、閉じた後もキャレット位置・選択範囲・
> 表示位置が Ctrl+S の直前と同じ(本文も変換前のまま)。
> **判定**: NVDA が「キャレットが飛んだ」と読む発話が挟まらないか。

#### (9) `EditorControl.Undo()` を流用しない判断(記録漏れの補完・仕様適合レビュー 5-1)

本タスク最重要の設計判断だが、初版の §10.14 は (1) で引数の形しか論じておらず、
記録が XML doc・commit message・(6) の変異表 M2 行に散っていた。ここに集約する。

`Undo()` を流用してはならない理由は 2 つあり、**どちらも「黙って何もしない」形で失敗する**:

1. **`Undo()` は `ReadOnly` で早期 return する**(`EditorControl.cs` の `Undo` 本体)。
   `WriteToPath` は `ConvertEols` の前後でだけ `ReadOnly` を外し、`finally` で元へ戻すので、
   **catch 節に来る時点では `ReadOnly` が復元済み**である。CSV グリッドモード
   (`CsvController.Editor.ReadOnly = true`)で保存に失敗すると、ロールバックが no-op になり
   本文が EOL 変換後のまま残る=まさに A-11 が塞ごうとしている静音喪失が別経路で復活する。
   `UndoEolConversion` は `ReadOnly` を見ない。ユーザー編集ではなく、**自分が直前に加えた
   変換の取り消し**だからである(「読み取り専用なのに書き換わったまま」を防ぐ側に倒す)。
2. **`Undo()` はキャレットを `UndoResult.CaretPos` へ動かす**((1) と §10.12 (2))。

網は `Save_WriteFailure_WhileEditorReadOnly_StillRollsBackContentEol`(L3)と
`UndoEolConversion_WhileReadOnly_StillUndoes`(L2)。(6) の M2(`UndoEolConversion` に
`ReadOnly` ガードを足す=流用と同じ形にする)で撃墜を実測済み。

#### (10) 訂正: 「網が無い」と書いた 3 項目は既存インフラで書けた(過小申告・このブランチで 3 回目)

初版の (6) は未撃墜・未観測を 4 項目挙げたが、**そのうち 3 項目が過小申告**だった
(仕様適合レビュー 2 が既存インフラだけでプローブを書き、すべて撃墜することを実測)。
CLAUDE.md の「**書けるはずの網を『書けない』と宣言するのも同種の事故**」に真正面から該当し、
§10.13 (6) が同じ教訓を書いた直後の再演である。

**過小申告の原因**は論法の取り違えだった。初版は「`WriteToPath` 経由では `ConvertEols` が
直前に同じ値を入れているから等価」と考えたが、それは**呼び出し元 1 本に限った話**である。
`UndoEolConversion` は public API で、**取り消しの直前に状態をずらしてから呼べば**
いずれも観測できる。API 単体の契約テストにこの論法を持ち込んだのが誤りだった。

| 初版の申告 | 実測 | 使った既存インフラ | 追加した網 |
|---|---|---|---|
| `DesiredXpx = -1` は等価変異 | **誤り。kill する** | 同ファイルの `Caret(EditorControl)` ヘルパ(`ConvertEols_NonFastPath_ResetsDesiredXpx` が既に使用) | `UndoEolConversion_ResetsDesiredXpx` |
| `Invalidate()` は sealed で seam 無し | **誤り。kill する** | WinForms 標準の public event `Control.Invalidated`。**seam は不要だった** | `UndoEolConversion_InvalidatesOnce` |
| `PositionCaret` は観測不能・L5 対象 | **誤り。kill する** | `CaretScrollTests` の `GetCaretPos` P/Invoke と `Show()` + `Focus()` の先例 | `UndoEolConversion_RepositionsSystemCaret` |
| `DocumentTooLargeException` 経路 | **正しい** | — ((4) のとおり注入点が無い) | — |

変異実測(いずれも変異前は全件緑・変異後にこの 1 件だけが落ちる):

| 変異 | 結果 | 撃墜したテスト |
|---|---|---|
| N1 `_caretCtrl.DesiredXpx = -1;` を落とす | kill | `UndoEolConversion_ResetsDesiredXpx` |
| N2 `Invalidate();` を落とす | kill | `UndoEolConversion_InvalidatesOnce` |
| N3 `if (_hasFocus) PositionCaret();` を落とす | kill | `UndoEolConversion_RepositionsSystemCaret` |
| N4 `if (_hasFocus)` → `if (true)` | **生存(真の等価変異)** | — |

**申告すべきだったのは N4 だった**。`PositionCaret()` 自身が冒頭で自己ガードするため
`if (_hasFocus)` は冗長で、外しても観測可能な差が出ない(`kxEdit.Editor.Tests` 全件で確認)。
ガードは呼び出し側の意図を読ませる目的で残す。

**`Invalidate` の網が精密に効く理由**: ホストを `MakeHosted`(`Show` / `Focus` しない)で作ると
`_hasFocus` が false になり `PositionCaret` 経路の再描画要求が混ざらないので、明示 `Invalidate()`
だけを数えられる。逆に system caret の網はフォーカスが要るので `Show()` + `Focus()` を使う
(§10.13 (5)-9 が採用した `HostForm.CreateVisible()` は `ShowWithoutActivation` でフォーカスを
取らないため、この 1 件だけは `CaretScrollTests` の先例に合わせる)。

**同じ過小申告が §10.13 (2) 表 #3 と §10.13 (6) にも残っている**
(`ConvertEols` 側の `Invalidate` / system caret 再配置)。策定済み節なので書き換えず、
ここで訂正する: **どちらも上と同じ手法で観測できる**。`ConvertEols` 側に網を張るのは
本 fixup の範囲外(仕様適合レビューは §10.13 については訂正追記のみを求めた)なので、
**申し送りとして残す**。手法は上表のとおり確立済みで、`UndoEolConversion_InvalidatesOnce` /
`UndoEolConversion_RepositionsSystemCaret` をそのまま `ConvertEols` へ写せばよい。

#### (11) 訂正: 捕捉が `ConvertEols` の「前」であることに網が無かった(仕様適合レビュー 1)

`WriteToPath` の捕捉 2 行を **`ConvertEols` の直後へ移す変異が生存していた**
(`kxEdit.App.Tests` / `kxEdit.Editor.Tests` 全件緑)。§10.12 (2) の核心要求
「位置は `ConvertEols` を呼ぶ前に捕捉する」が完全に無防備だった。

**原因は fixture**。初版の `Save_WriteFailure_OnNonFastPathEol_RestoresCaretAndScroll` は
caret / anchor を `'W' * 200` の**行 0 の中**(2 / 5)に置いていた。行 0 は改行を含まないので
CRLF → LF 変換でオフセットが 1 も動かず、「変換前に捕捉したか / 変換後に捕捉したか」を
**原理的に弁別できない**。実運用でキャレットが行 0 にあることは稀で、この退行が入れば
保存失敗のたびに**キャレットが「その位置より前にある改行の数」だけ手前へずれる**。

**対応**: caret / anchor を行 0 の外へ移した(anchor 205 / caret 209 =行 1 の途中 → 行 2 の先頭。
行 0 = `[0,200)` / CRLF = 200,201 / `"line1"` = `[202,207)` / CRLF = 207,208 / `"line2"` = `[209,214)`)。
caret 209 の手前には CRLF が 2 個あるので、捕捉を後ろへ動かすと復元値が 207 へずれる。

実測: **旧 fixture では同じ変異が生存**(App 全件緑)、**新 fixture では `Expected 209 / Actual 207`
で撃墜**。ScrollX / TopLine の設定はこの行より後なので、(2) の「長い行を可視域外へ」という
罠の仕込みには影響しない。

**教訓**: 「非既定位置から始める」(CLAUDE.md §4-B)は座標が 0 でないだけでは足りない。
**検証したい差分がその位置で実際に現れるか**まで確かめること。今回は「変換でオフセットが動く
位置か」が条件だった。§10.13 (7) の「fixture 1 本から一般化した」失敗と同型である。

#### (12) 受容としてユーザー / PR へ上げるもの(コードは変更しない)

1. **(6) の M4 / M5 は §7.4 の宣言に反して GUI 側へ変異を当てている。**
   §7.4 は「`ConvertEols` の GUI 側(caret / スクロール復元)と `WriteToPath` のダイアログ経路には
   **適用しない**(CLAUDE.md §4-A の禁止領域)」と自ら宣言していた。M4(`AfterEdit()` 置換)と
   M5(`UpdateHorizontalScrollbar()` 追加)はまさにその領域である。
   §10.13 (7) が実測した実退行(Ctrl+S のたびに水平スクロール位置が消えていた)を理由に
   **例外として実施**したが、CLAUDE.md §4-A の文言は「全面禁止」であり、
   **規範解釈はユーザー判断を仰ぐ**。テスト自体は退行を実際に捕まえているので削除しない。
2. **Redo 破棄((3))にユーザー承認が無い。** 実装計画 Task 4 Step 4 は「どちらを採るかは
   実装時にユーザーへ確認し、§5.3 へ結論を追記する」と明示していた。判断の中身と逸脱は
   (3) に記録済みだが、承認は未取得のまま。PR で明示して承認を得る。
3. **網の層の偏り(解消済み)。** 仕様適合レビュー時点では M5(水平スクロールバー)と
   M6(`DropRedo`)が **`kxEdit.Editor.Tests` では完全に生存**し、App 統合テスト 2 本だけが
   唯一の防壁だった。本 fixup で L2 の網
   (`UndoEolConversion_KeepsHorizontalScroll_WhenLongLineOffScreen` /
   `UndoEolConversion_DropsRedo`)を追加し、両変異が L2 単独でも撃墜されることを実測した。
   **App 側の 2 本(`..._RestoresCaretAndScroll` / `..._LeavesNothingToRedo`)も取り外さないこと**:
   L2 は API 単体の契約を、L3 は「`WriteToPath` がその契約を正しく使っているか」を守っており、
   守備範囲が違う((11) の捕捉順の網は L3 にしか置けない)。

### 10.15 受容: EOL 変換で常駐メモリが文書 1 つぶん増える(最終レビュー 品質 I-1 / 脆弱性 M-V1・2026-08-29 追記)

**両レビューパスが独立に見つけた唯一の項目。** §4.3 が A-9 側で「`string` を実体化しないので
512MB 級でもピークメモリは増えない」と丁寧に書いている一方、A-11 側でメモリが倍増することが
一言も書かれていないのは資料として不均衡なので、ここに記録する。

#### 何が起きるか

`TextBufferBuilder.Add` は `new byte[]` へ**コピー**してから `TextChunk` にする
(「TextChunk が参照を保持するため必ず自前の配列にコピー」とコード側にも明記されている)。
したがって `ConvertEols` が組む新しい木は、**変換前の木とバイト列を 1 つも共有しない**。

main は `ReplaceSource` で旧 `TextBuffer` ごと捨てていたので旧木は GC 対象だった。
現在は `ReplaceAllRecordingUndo` が `rootBefore` を Undo エントリへ格納し、`UndoHistory` は
`List<Entry>` で**エントリ数の上限を持たない**ため、変換前の木が生き続ける。

- 300MB の LF 文書を CRLF で保存 → 常駐 約 310MB → 約 610MB(レビュアー試算)
- 本セッションの実測(8.4M 文字 / 全 LF / 40 byte 行・Release・`GC.GetTotalMemory(true)`):

| 時点 | 値 | 差分 |
|---|---|---|
| `ConvertEols` 前 | 18,560 KB | — |
| 1 回目の変換後 | 26,829 KB | **+8,268 KB**(= 文書 1 つぶんの木) |
| さらに 50 回 `ConvertEols` | 26,829 KB | **+0 KB** |

#### 受容する理由

**「Undo で戻せる」ことは「戻し先の木を持っている」ことなので、これは A-11 の修正に内在する
コストである。** 旧木を捨てれば Undo できず、A-11 のバグがそのまま残る。
Undo 履歴に上限を設ける案は本テーマの範囲を超える(通常の編集履歴の設計変更になる)。

**増加は有界**でもある。2 回目以降の保存は本文がすでに目的 EOL なので `IsEolAlreadyUniform` が
true =fast-path で何も積まない。上表の「50 回追加して +0 KB」がそれを示している。
エントリが積み上がるのは「EOL 設定を変えながら保存を繰り返す」場合だけで、これは通常の編集で
履歴が伸びるのと同じ性質である(`EmptyUndoBuffer` = 開き直し・復元で解放される)。

#### コード側の対応

- `TextBuffer.ReplaceAllRecordingUndo` の `<remarks>` に「変換前の木は Undo 履歴が保持する」旨を明記した。
- **`SearchController.cs` のコメントを訂正した(必須)**。
  「開き直し・復元・**EOL 変換(ReplaceSource)で捨てられた**旧バッファのピース木をピン留めしない」
  という記述は、`_selectionScope` を弱参照にする理由として書かれていたが**もう成立しない**
  (変換前の木は Undo 履歴が強参照で保持するので、弱参照にしていても回収されない)。
  監査 §9 の V-4 / V-6「コメントが実在しない防御を謳っている」と同型で、次に触る人が誤った前提で
  判断する。弱参照そのものは開き直し・復元・タブクローズに対しては有効なので維持し、
  EOL 変換については防御が無いことを明記した。

### 10.16 最終ブランチレビュー(2 パス)の反映記録(2026-08-29 追記)

CLAUDE.md §3-5 の最終ブランチレビューを**コード品質パス / 脆弱性パスの独立した 2 エージェント**で
実施した。判定は **Critical ゼロ・マージ可**。両パスが独立に見つけた 1 件(メモリ保持)は §10.15。
本節はそれ以外の反映を記録する。

#### (1) 訂正: §10.7「本ブランチの差分については問題なし」は Task 1 時点の確認だった

§10.7 は XML doc の `cref` について「品質レビュアーが `-p:GenerateDocumentationFile=true` で実ビルドし、
今回の差分が追加した `cref` はすべて解決することを確認している(警告ゼロ)」と書いている。
**これは Task 1(A-9)時点の確認**であり、Task 3 / Task 4 が追加した `cref` には当てはまらなかった。
実測で次の 3 件が CS1574(解決できない `cref`)になっていた:

```
FileController.cs(817,56): cref 'SetOrReplaceSource'
FileController.cs(822,20): cref 'UndoEolConversion'
FileController.cs(830,29): cref 'Undo'
```

原因は `FileController.cs` に `using kxEdit.Editor;` が無いこと(コードは `doc.Editor.XXX` 経由で
using を必要としないが、`cref` は名前解決を要求する)。**完全修飾**
(`kxEdit.Editor.EditorControl.UndoEolConversion` 等)で解決した。`using` の追加は IDE0005 を招くので採らない。

再確認コマンドと結果: 上のビルドで `FileController.cs` の CS1574 は **0 件**。
残る CS1574(`EditorControl.cs` の `SearchController` / `SelectAll` / `DrawImeOverlay`)は
`f2258bb` 以前からの既存債務で、§10.7 が挙げた「有効化は 70 件の債務返済とセットの独立テーマ」に含まれる。

**教訓**: `GenerateDocumentationFile` が無効な間、`cref` はビルドで守られない(§10.7 の申し送りそのもの)。
**ブランチの途中で `cref` を足したら、その都度上のコマンドを回すこと**。1 回確認したという記録は
その時点までしか意味を持たない。

#### (2) 無効化: §10.8 に残っているテスト件数(CLAUDE.md §5)

§10.8 に「1,272 件すべて緑」「1,271 件すべて緑」という記述がある。**テスト数は文書に書かない**
(CLAUDE.md §5。fact 追加で必ず陳腐化する)。§10.11 (2) が同じ理由で件数を撤回したのと同じ扱いにする:
§10.8 の当該 2 箇所の**数値は無効**とし、定性表現「(当該テストプロジェクトの)全件が緑のまま通った=変異は生存」が正である。
§10.8 は策定済み節なので本文は書き換えない。

#### (3) 訂正: §10.13 (6) の表「スクロールバー同期」の観測手段

表は「リフレクションで `_vscroll` / `_hscroll` を観測するプローブ」と書いているが、
**出荷された網はそうなっていない**。`"_hscroll"` を含むテストファイルは 0 件で、
`ConvertEols_NonFastPath_KeepsHorizontalScroll*` は public な `TopLine` / `ScrollX` で観測している。

リフレクションのプローブは**調査中に使い、退行(§10.13 (7))を見つけた道具**であって、出荷物ではない。
**実装のほうが表より良い**(private フィールドへの結合が無い)。放置すると次の読者が
「ここは private 結合済みだから増やしてよい」と誤解するので訂正する。

#### (4) 訂正: `ConvertEols_NonFastPath_DoesNotCoalesceWithPrecedingTyping` は 2 つのガードの網ではない

同テストのコメントは「前置 `BreakCoalescing` + `insertHasBreak: true` を Editor 側から観測する」と
書いていたが、fixture が通常形(`removed=4` / `inserted=5`)なので `UndoHistory.Record` の融合判定
(`pureInsert` / `pureDelete` の形でしか通らない)に構造的に掛からず、**どちらのガードを外しても緑**である。

皮肉なことに、同じブランチの `TextBufferReplaceAllTests` と §10.11 (1) はこの事実を正確に書いていた
(担い手は Core の退化形テスト 2 件)。**Editor 側のコメントだけが §10.11 (1) で訂正済みの誤解を持っていた。**
コメントを「通常形が独立エントリになることの characterization。ガードの担い手は Core の退化形 2 件」へ直した。

#### (5) App の新規テストに「catch 節へ到達した」証拠を足した

Task 4 で足した 6 件は失敗の証拠が `Assert.False(host.File.Save())` だけで、近傍の既存テストが持つ
`host.Prompt.Log` の assert が無かった。とくに `Save_WriteFailure_OnFastPathEol_DoesNotUndoUserEdit` は
**全 assert が「何も変わらないこと」**なので、将来 `TryInspectSaveTarget` 等がドライブルートを前段で
弾くようになって失敗点が `ConvertEols` より上流へ移ると**黙って空振り**する。

`Assert.Contains(host.Prompt.Log, ... "保存できませんでした" ...)` を 6 件すべてに足した。
**実測**: `WriteToPath` の `TryInspectSaveTarget` の直後に
「`Path.GetDirectoryName(path)` が null なら return false」という前段ガードを足す変異(=上のシナリオ)を
当てると、**この 6 件と `Save_ExistingPathIsDriveRoot_...` が撃墜される**。足す前は 6 件すべて緑のまま通った。

#### (6) `DocumentTooLargeException`: 文言を分け、網を 1 件足した(脆弱性 L-V3)

- **文言**: 500MB 級の LF 文書を CRLF で保存すると変換後が上限を超えるが、**文書自体は上限内**である。
  共通文言「保存できませんでした: 文書サイズ上限(512 MB)を超えました。」だけだと
  「この文書は一切保存できない」と読め、逃げ道に辿り着けない。上限超過だけ分岐させ、
  **「改行コードを変換すると…超えます。『名前を付けて保存』で改行コードに LF を指定すると
  サイズを増やさずに保存できる場合があります」**へ変えた。文言は固定文字列で組むので
  `SanitizeForDisplay` は不要(外部入力を含まない)。
- **網**: `Save_DocumentTooLarge_IsCaught_AndSuggestsLfEol`。実経路(`ConvertEols`)の発火には
  512MB 級の文書が要るため(§10.14 (4))、`try` の内側にある唯一の注入点 `metaChanged` から
  同じ例外を投げ、**catch フィルタと文言だけ**を固定する。テストホスト側に `MetaChangedThrow` を足した。
  **実測**: フィルタから `or DocumentTooLargeException` を落とす変異 → 未処理例外で撃墜。
  専用文言の分岐を落とす変異 → `Assert.Contains` で撃墜。
- **判明した非対称**: 読み込み側(`LoadInto` の catch)は**以前から** `DocumentTooLargeException` を
  フィルタに持っていた。書き込み側にだけ無かった=§10.11 (8) が指摘した穴は「読み書きの非対称」だった。

#### (7) SavePoint 系テストの位置づけを明示した(過大主張の是正)

`ConvertEols_NonFastPath_*` の SavePoint 系 6 件のうち、**AfterEdit 変異下で赤くなるのは
`..._OnSavedDocument_FiresNoSavePointEvents` の 1 件だけ**である。とくに
`..._ThenSetSavePoint_FiresReachedOnce` は **`ConvertEols` の呼び出しを丸ごと削除しても緑**になる
(`SetSavePoint()` が `SavePointReached` を無条件発火するため)。名前が `ConvertEols_NonFastPath_` で
始まるのは過大主張なので、セクションコメントに「characterization(main との対照群)であり
弁別力は持たない。撃墜の担い手は `..._OnSavedDocument_FiresNoSavePointEvents`」と明示した。
残す理由は §10.13 (3) の発火列対比表そのものであり、将来 main と比較し直す人の出発点になるため。

#### (8) 同じ設計根拠の二重記述を一本化した(品質 I-3)

`WriteToPath` の `<remarks>` と `UndoEolConversion` の `<remarks>` が**同じ 3 点**
(fast-path で取り消さない / `Undo` を流用しない / caret を明示復元)を prose で二重に説明していた。
片方だけ直せばもう片方は黙って陳腐化する —— `ReplaceSource` の remarks が
「列挙の同期はコードでもテストでも守られていない…現在 4 箇所に散っている」と自己申告しているのと同じ失敗形で、
**4 箇所目を増やす**ところだった。

**契約の根拠は API 側(`UndoEolConversion`)に一本化**し、`WriteToPath` 側は呼び出し位置固有のこと
(捕捉が `ConvertEols` より前・`try` の外である理由)+ ポインタだけに絞った。

#### (9) 折り返し ON の垂直位置に網を足した(品質 m-2)

スクロール系の網はすべて `WrapColumns = 0` で `_topSegment` が常に 0 だったため、
§5.2 契約表 #2 の「`SetTopPosition` で**視覚行位置**を保つ」に実質的な網が無かった。
折り返し ON の垂直位置は A-5 / A-6 で継続的に事故が出ている領域なので、
`WrapColumns` 非 0 かつ `TopSegment` 非 0 から始める黒箱テスト
`ConvertEolsAndUndo_KeepVisualRowPosition_WhenWrapOn` を足した(変換と取り消しの両方を見る)。
**実測**: `ConvertEols` の `SetTopPosition(savedTopLine, savedTopSegment)` を `TopLine = savedTopLine`
(= 論理行だけ戻してセグメントを 0 に潰す)へ変える変異で `Expected 2 / Actual 0` で撃墜。

#### (10) 受容(コード変更なし)

- **m-1: `ConvertEols` と `UndoEolConversion` でスクロール復元のルールが正反対**。
  `ConvertEols` 側の `SetTopPosition` / `ScrollX` 復元は現在**完全な no-op** だが、§10.14 (2) は
  `UndoEolConversion` について「値が同じで常に早期 return する等価な行が増えるだけ」として足さなかった。
  **`ConvertEols` 側は残す**(将来スクロールを動かす副作用が入ったときの防御・(9) の網の対象でもある)。
  非対称を明示するため、`UndoEolConversion` の remarks に「**こちらには同等の防御が無い**」と 1 行足した。
- **m-9: `UndoEolConversion(bool, int, int)` は誤用可能**。`conversionRecorded` に `true` を渡す /
  捕捉を後ろに置く、のどちらも型では防げない(トークンを返す `readonly record struct` にすれば防げる)。
  呼び出し元が 1 本しかなく、網(M1 / M12)と XML doc で守られているため**受容**。
  呼び出し元が増えるときは型で防ぐ形へ移すこと。
- **§7.4 の宣言との矛盾(§10.14 (12) 1)と Redo 破棄の承認(同 2)は未決のまま**。PR で扱う。
- **m-3 / m-7 / m-8**: 多数決 tail の重複・冗長テスト・fixture コストは申し送り。
- **m-4: テストヘルパの重複**(§10.13 (10) の申し送りへ次を追記):
  `SendMouseWheel` は **3 バリアントが並存**しており診断品質がファイルごとに違う。
  `Caret(EditorControl)` は**同一ファイル内で 2 重定義**されている。
  `tests/kxEdit.Editor.Tests/TestHost.cs` へ集約するときに取りこぼさないこと。

#### (11) 脆弱性パスが確認した既存の窓(悪化なし)

- **L-V1 / L-V4**: main 既存または受容済みで、本ブランチによる悪化は無い。
- **L-V2: §10.13 (5)-2 の主張が独立検証で成立した**。「caret 復元と `OnSnapshotChanged` の間の窓で
  RPC スレッドが観測しうる最悪値は**旧文書末尾に縮退した選択範囲**であり、例外も範囲外読みも起きない」
  という主張について、レビュアーが `TextRangeProviderV2` を生成する経路を**全列挙**し、
  すべて ctor の `Math.Clamp(start, 0, owner.Host.TextLength)` を通ることを確認した。
  さらに `IUiaTextHost` のオフセットを受け取る全メンバも個別に clamp していることを確認している。
  §10.13 (5)-2 が書いた「将来 `TextProviderImplV2.GetSelection` がクランプを通さない経路を足したら
  本判断は再検証が要る」はそのまま有効。
- **性能の非対称(申し送り)**: `ConvertEols` / `IsEolAlreadyUniform` の内側ループは 1 バイトずつの
  `for` のままで、A-9 が `LineEndingDetector.Detect` に入れた `IndexOfAny` の SIMD 化(§10.3)が入っていない。
  保存 1 回で全文を 2 周する。既存構造であり本ブランチの退行ではないが、**A-9 側の高速化と非対称**なので
  §10.6 の `EolSegments` seam を回収するときの対象として記録する
  (§10.6 自身が「`ConvertEols` の 1 バイトずつの `outBuf[outLen++] = b` が span 単位コピーになる」と
  同じ効果を予告している)。

---

### 10.17 決着: §7.4 の「GUI 側には変異を当てない」宣言との矛盾(ユーザー承認・2026-08-29)

§10.14 (12) 1 が「規範解釈はユーザー判断を仰ぐ」として PR へ上げていた件が決着した。

**ユーザー判断: 今回限りの例外として、実施したミューテーション検証を認める。**

したがって:

- **CLAUDE.md §4-A の文言は変更しない。** 最終レビュー(コード品質パス)が提案していた
  恒久的な例外条項の追加(「実測で再現した退行の修正確認に限り一回限りの適用を認める」)は
  **採らない**。§4-A は「UI の操作性や見た目に関わる部分…は全面禁止」のままである。
- **今回の適用は例外であって前例ではない。** 後続タスクがこの節を根拠に GUI 側へ変異検証を
  行ってはならない。同種の状況(実測で再現した退行の修正確認)が再び生じた場合も、
  改めてユーザー判断を仰ぐこと。
- **テストは 1 件も削らない**(元々そのつもりだった)。M4 / M5 が撃墜する
  `ConvertEols_NonFastPath_KeepsHorizontalScroll_WhenLongLineOffFirstScreen` と
  `Save_WriteFailure_OnNonFastPathEol_RestoresCaretAndScroll` は実在の退行
  (§10.13 (7): Ctrl+S のたびに水平スクロール位置が消える。**main 既存バグでもあった**)を
  捕まえた黒箱契約テストであり、削ると再発を検出できなくなる。
  **変異検証の記録とテストの存在価値は独立である。**

**該当した変異**(§10.14 (6) の表より):

| 変異 | 撃墜した assert | §4-A 上の位置づけ |
|---|---|---|
| M4 `AfterEdit()` 置換 | `TopLine 150 → 2` | 「UI の操作性や見た目」= 禁止領域 |
| M5 `UpdateHorizontalScrollbar()` 追加 | `ScrollX 40 → 0` | 「GUI のレイアウト」= 禁止領域 |

なお §10.14 (12) 2(Redo 破棄のユーザー承認)は本節の対象外だが、同日に承認された(§10.18)。

---

### 10.18 決着: Redo 破棄のユーザー承認(2026-08-29)

実装計画 Task 4 Step 4 は「どちらを採るかは**実装時にユーザーへ確認**し、設計書 §5.3 へ
結論を追記する」と定めていたが、§10.14 (3) は確認を経ずに「捨てる」で実装し、
その事実自体を §10.14 (12) 2 に申し送っていた。

**ユーザー判断: 現状(捨てる)でよい。**

したがって `TextBuffer.DropRedo()` / `UndoHistory.ClearRedo()` と、
`UndoEolConversion` からのその呼び出しは**確定**である。

決定内容の再掲(§10.14 (3)):

| 選択肢 | 帰結 |
|---|---|
| **捨てる**(採用) | 保存失敗のロールバック後、Ctrl+Y で EOL 変換をやり直せない |
| 捨てない | 保存に失敗しただけで「やり直し」メニューが有効になり、Ctrl+Y が**ユーザーの要求していない全文 EOL 変換を再適用する** |

採用理由は非対称性(捨てない側に利点が無い)。**1 行で反転可能**である点も変わらない
(`UndoEolConversion` 内の `_buffer.DropRedo();` を外すだけ)。

これに伴い、挙動変更「保存**失敗**時に既存の Redo スタックが失われる」(§10.14 (12) の
対比表 / PR description の挙動変更 4)も承認済みとなる。main は旧バッファ参照ごと戻すので
残っていたが、成功時は main も消えるため差が出るのは失敗パスのみ。
`_redo.Clear()` 済みから復元する手段は Core に無く、Task 4 では原理的に直せない。

---

## 11. 現状サマリ・訂正インデックス(2026-08-29 追記)

本書は CLAUDE.md §8 により**策定時スナップショットを書き換えない**方針で運用しており、
その結果 §10 の中に訂正が入れ子になっている(例: 網の状況を知るには §10.13 (6) → §10.14 (6)
→ §10.14 (10) を順に読む必要がある)。**将来の読者が「今の正」に辿り着けない**ため、
どの記述がどこで置き換わったかの一覧をここに置く。**本節だけは常に最新へ更新してよい。**

### 11.1 訂正インデックス(左が古い記述・右が現在の正)

| 旧記述の場所 | 内容 | 現在の正 |
|---|---|---|
| §4.3 | 「検出の追加コストは小さい」 | **§10.3**(実測で warm +59〜63% → `IndexOfAny` 化で 4.8〜12.5 倍改善) |
| §5.1 | `insertHasBreak: true` は「coalescing を必ず切る」/ 括弧内の融合の向き | **§10.11 (1)**(必ずは切らない。担い手は前置 `BreakCoalescing` と 2 本立て) |
| §5.1 | API は `TextBuffer` を受けて戻り値なし | **§10.11 (3)**(`bool ReplaceAllRecordingUndo(TextSnapshot)`) |
| §5.2 契約表 | 表は 9 行 | **§10.12 (1)**(+2 行)→ **§10.13 (8)**(さらに `DesiredXpx` が漏れていた=計 13 項目) |
| §5.2「意図的に変える点」 | 「SR への影響は L5 でのみ判定できる」 | **§10.13 (8) 2**(機構は L2 で固定できる。L5 に残るのは実発声だけ) |
| §5.3 | Redo の扱いは実装時に決める | **§10.14 (3)** → **§10.18**(捨てる。`TextBuffer.DropRedo`。**ユーザー承認済み**) |
| §7.4 | GUI 側には変異を当てない | **§10.14 (12) 1** → **§10.17**(実際には当てた。**ユーザー承認により今回限りの例外として決着**) |
| §10.3 / §10.4 | 「生存する変異は空ピースガードだけ」 | **§10.8**(`i = 1;` の変異も生存していた) |
| §10.4 表 2 行目 | 変異名「持ち越し後に `continue`」 | **§10.9**(`IndexOfAny` 化後は `else` 節に `i = 1;` を足す形) |
| §10.7 | 「本ブランチの差分が追加した `cref` はすべて解決する」 | **§10.16 (1)**(Task 1 時点の確認。Task 3 / 4 の 3 件が CS1574 だった=完全修飾で修正済み) |
| §10.8 の件数(2 箇所) | 「1,272 件 / 1,271 件すべて緑」 | **§10.16 (2)**(テスト数は文書に書かない=**数値は無効**。定性表現が正) |
| §10.10 | Task 2 の変異表・`insertHasBreak` の説明 | **§10.11 (2)(7)**(件数の撤回・シグネチャ変更後に全変異を当て直し) |
| §10.13 (2) 表 #3 / §10.13 (6) | `Invalidate` / system caret は「観測不能」 | **§10.14 (10)**(どちらも観測できる。`Control.Invalidated` / `GetCaretPos`。`ConvertEols` 側に網を張るのは申し送り) |
| §10.13 (3) 発火列表の失敗パス | 「ロールバックで `Reached` が 1 回焚かれる(新規挙動)」 | **§10.14 (5)**(焚かない。保存操作の発火列は main と一致。ただし**保存成功後の Ctrl+Z** は新規に `Left` を焚く=意図した挙動変更) |
| §10.13 (6) 表「スクロールバー同期」 | 「リフレクションで `_vscroll` / `_hscroll` を観測」 | **§10.16 (3)**(それは調査プローブ。出荷した網は public な `TopLine` / `ScrollX`) |
| §10.13 (7) 初版 | 「main には無い退行」 | 同節内の**訂正**(main も条件次第で同じ挙動=挙動改善でもある) |
| §10.14 (4) | 「`EolMode` も未変更」 | **§10.14 (4) 内の訂正**(`ApplyEol` が先に代入する。main と同挙動) |
| §10.14 (6) 未撃墜表 | 「未撃墜 4 項目」 | **§10.14 (10)**(うち 3 項目は過小申告。真の等価変異は `if (_hasFocus)`) |
| `ConvertEols_NonFastPath_DoesNotCoalesceWithPrecedingTyping` のコメント | 「2 つのガードを Editor 側から観測する」 | **§10.16 (4)**(通常形なのでガードを外しても緑。担い手は Core の退化形 2 件) |

### 11.2 現状サマリ(この 3 点を押さえれば読める)

1. **A-9**: `LineEndingDetector.Detect(TextSnapshot)` が全文を byte 走査する(`IndexOfAny` で SIMD 化)。
   4,096 文字窓は撤廃。多数決の意味論は `Detect(string)` と同一。
2. **A-11 Core / Editor**: `ConvertEols` は `TextBuffer.ReplaceAllRecordingUndo(TextSnapshot)` で
   **in-place の 1 Undo 単位**になった。`_savedRoot` は触らないので変換直後は `Modified` が true。
   `AfterEdit` は使わず副作用を個別に打つ。水平スクロールバーは再計算しない。
3. **A-11 App**: `WriteToPath` は `ConvertEols` の**戻り値**でロールバック要否を判定し、
   `EditorControl.UndoEolConversion(recorded, anchorBefore, caretBefore)` で 1 つだけ取り消す。
   キャレット / 選択は**変換前に捕捉した値**へ明示復元し、スクロールには触れない。

### 11.3 未決・申し送り(回収先が決まっていないもの)

| 項目 | 記録場所 |
|---|---|
| `EolSegments` seam(EOL トークナイザの共通化 + `ConvertEols` の SIMD 化) | §10.6 / §10.16 (11) |
| XML doc `GenerateDocumentationFile` の有効化(既存債務 70 件) | §10.7 |
| `ConvertEols` 側の `Invalidate` / system caret の網 | §10.14 (10) |
| `UndoEolConversion` の誤用をトークン型で防ぐ | §10.16 (10) m-9 |
| テストヘルパの集約(`SendMouseWheel` 3 バリアント / `Caret` 2 重定義を含む) | §10.13 (10) / §10.16 (10) m-4 |
| 書き出し側 EOL 変換案(M-25 も同時に消える) | §10 冒頭 |
| EOL 混在文書が黙って統一されること(説明書への明記の要否) | §10 冒頭 / §4.4 |
