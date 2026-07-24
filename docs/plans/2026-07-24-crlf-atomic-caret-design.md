# CRLF を 1 論理文字として扱う(キャレット atomic 化)設計書

作成日: 2026-07-24
Status: 承認済み(ブレインストーミング完了)

## 1. 背景と問題

CRLF 改行のファイルで以下の症状が発生している:

1. **矢印キー**: 改行を挿入した位置を ← → で辿ると 2 回移動が発生(CR と LF が別位置扱い)。
2. **スクリーンリーダー**: 1 つ目を「復帰(CR)」、2 つ目を「改行(LF)」と別々に読み上げる。
3. **文字数照会**: 改行 1 回で「文字数」が 2 加算される(CRLF が UTF-16 code unit 2 個としてカウントされる)。

**根本原因**: `TextSnapshot` は UTF-16 code unit を位置単位としており、キャレットは CR と LF の間に立てる。ナビゲーション(`NavigationCommands.MoveLeftChar/MoveRightChar`)は
サロゲートペアのみ atomic 扱いで、CRLF pair は atomic ではない。UIA(SR インターフェース)側の `NextChar/PrevChar` も同様。
`PositionFormatter` は明示的に「CRLF=2 で数える」設計となっている(前回はこれが意図的判断だった)。

## 2. 方針(承認済み)

**方針**: **バッファ層は不変**(UTF-16 code unit 保持・piece tree の byte 完全一致契約維持)。
**ナビゲーション/UIA/表示層で「CRLF pair を 1 論理文字」として振る舞う**。
サロゲートペア中間位置を禁止する既存の発想を CRLF pair にも対称適用する。

**除外した代案**:
- **バッファ内 LF 正規化**(VSCode 型): 読み込み時 CRLF→LF・保存時に戻す。
  → piece tree の byte 完全一致契約が崩れる。前回の hot exit 統合(PR #24)や
    バックアップ・EncodingDetector の前提が根本から崩れる大改造となる。**却下**。
- **文字数表示のみ修正**(部分対応): SR の「復帰/改行」問題が残る。**却下**。

## 3. 不変条件(新規)

**キャレット位置は CR と LF の間に立てない**:

```
pos > 0 && GetChar(pos-1) == '\r' && pos < CharLength && GetChar(pos) == '\n'
```

を満たす `pos` は無効。**あらゆるキャレット位置設定エントリで「無効なら CR の前(=`pos - 1`)へスナップ」**する。

**スナップ方向**: 常に **CR の前(=行末位置)**。理由:
- MoveEnd(includeBreak=false) と同位置=論理行の一部として自然
- Notepad / VSCode と同方向
- マウスクリック位置が CRLF の間に落ちても「同じ行の末尾」に着地=行間ジャンプが発生しない

## 4. カウント意味論(承認済み)

- 「文字数 M」= **論理文字数**: `CharLength - CRLF pair 数`(サロゲートは 2 のまま)
- 「選択 K 文字」= 同じ論理文字数の距離: `(end - start) - CountCrlfPairs(start, end)`
- 「桁」(column): 行内に CRLF は現れない(必ず行末)=既存計算不変

**サロゲート=2 は保守契約として維持**(ユーザーから苦情なし・変更のリスクを取らない)。

## 5. コンポーネント設計

### 5.1 Core: `NavigationCommands` 拡張

**新規**: `SnapOutOfCrlf(TextSnapshot s, int pos) -> int`
- mid-CRLF なら `pos - 1`(=CR の前)、それ以外は `pos` を返す

**変更**: `MoveLeftChar` / `MoveRightChar`
- サロゲートペア判定の隣に CRLF pair 判定を追加(±2 で越える)

### 5.2 Core: `TextSnapshot` に集計 API

**新規**: `int CountCrlfPairs(int start, int endExclusive)`
- ピース走査 + carry(直前 byte が CR)で 1 パス(`WriteTo` / `CountNonBreakAndBreaksInSnapshot` と同じ流儀)
- 全文 string 化しない

**利用**: 論理文字数 = `snap.CharLength - snap.CountCrlfPairs(0, snap.CharLength)`

**実装時の精密化(2026-07-25 追記)**: 実装は `GetText(start, endExclusive - start)` で範囲 string を materialize する簡易版に落とし込んだ。位置照会ホットキー押下時のみの低頻度パスなため許容(全文コピーは pre-branch の `SnapshotText.Length` 経路と同コスト・§R-4 の脆弱性パスでも同判断)。将来 hot 経路が生まれた場合は piece-tree native scan への最適化を検討する。

### 5.3 Editor: `UiaTextHostAdapter`

- `NextChar` / `PrevChar`: CRLF pair を ±2 で越える(サロゲート隣に追加)
- `SetSelection(start, end)`: 両端子を `SnapOutOfCrlf` で snap してから host へ委譲

### 5.4 Editor: `EditorControl` / `CaretController` の入り口

**snap を通す入り口**(**すべて Core `SnapOutOfCrlf` 経由**):
- `EditorControl.SetCaretCharOffset(pos)`
- `EditorControl.SetSelectionCharRange(s, e)` / `SetSelectionAnchored(s, e)` / `MoveCaretWithSelection(pos)`
- `CaretController.SetTo(pos, snap)` / `SetSelection(anchor, caret, snap)` の内部代入前
- `EditorControl.OffsetFromClientPoint(x, y)` の返り値(マウスクリック位置)
- `EditorControl.ConvertEols` 復元後の `SetSelection(anchor_reconstructed, caret_reconstructed)` の直前
- `SnapshotSearcher` の match 位置(通常 mid-CRLF はあり得ないが防御)
- `Buffer.Replace`/`Delete` 後の caret 再計算(必要なら)

**設計原則**: 「入り口で 1 度スナップ」= 内部ロジックは snap 済み前提で unchanged。

### 5.5 App: 位置読み上げ経路

`MainForm` の位置読み上げ(§L848 付近)で:
- `snap.CharLength` を渡している所を `snap.CharLength - snap.CountCrlfPairs(0, snap.CharLength)` に差替
- 選択長は `(end - start) - snap.CountCrlfPairs(start, end)` に差替

`PositionFormatter.Format` のシグネチャは不変。docstring を「論理文字数(CRLF=1・サロゲート=2 で数える)」に更新。

### 5.6 SR/UIA 読み上げ text の正規化はしない(第一段)

- `GetTextRange` は raw text(`\r\n` のまま)を返す
- キャレット atomic 化により SR は 1 回の選択変更イベントを受け取る=NVDA は通常 `\r\n` を「改行」1 回として音声化する見込み
- offset 長と返り文字列長の乖離を回避=SR 実装差の原因になり得るリスクを取らない

**申し送り(§10)**: L5 で NVDA でも別読みされる場合のみ、`GetTextRange` で CRLF → LF 正規化を追加する fixup を検討。

## 6. 波及範囲

### 6.1 自動対応(コード変更不要)

- `InputRouter.HandleLeft/HandleRight` → `MoveLeftChar/MoveRightChar` 経由=自動 atomic
- `InputRouter.HandleBack` → `MoveLeftChar` で削除範囲決定=BS で CRLF 一括削除
- `InputRouter.HandleDelete` → `MoveRightChar` で削除範囲決定=Del で CRLF 一括削除
- Shift+Left/Right の選択拡張=同上、CRLF 一括選択
- `WordBoundary`: CR/LF を LineBreak 扱いで連続スキップ済み=不変

### 6.2 明示対応が必要

- `NavigationCommands.MoveLeftChar/MoveRightChar` の CRLF pair 判定追加
- `UiaTextHostAdapter.NextChar/PrevChar/SetSelection` の CRLF 対応
- `EditorControl` / `CaretController` の位置設定入り口に snap
- `MainForm` の位置読み上げで論理文字数採用

## 7. テスト計画

### L1(Core・自動)

- `NavigationCommandsTests`:
  - `MoveLeftChar`/`MoveRightChar` CRLF pair を 1 step で越える
  - 孤立 CR / 孤立 LF は 1 step のまま
  - 空文書・先頭・末尾の境界
  - `SnapOutOfCrlf` 単体(mid-CRLF → CR 位置・その他は不変)
- `TextSnapshotTests`:
  - `CountCrlfPairs`(空範囲・全域・部分・跨ぎピース・CRLF 混在)

### L2(Editor・自動)

- `EditorControlNavigationTests`(または新規):
  - BS/Del が CRLF を 1 括削除
  - Shift+Left/Right の選択拡張が CRLF pair を 1 括
  - マウスクリック(`OffsetFromClientPoint` の Y=行末 X=末尾)が mid-CRLF に落ちない
- `UiaTextHostAdapterTests`(または新規):
  - `NextChar/PrevChar` の CRLF atomic
  - `SetSelection(mid_crlf)` が snap されて CR 側になる
- 既存 `EditorControlConvertEolsTests`:
  - caret 復元が mid-CRLF に落ちないこと(境界回帰)

### L3(App・自動)

- 位置読み上げ経路(または App 層 helper):
  - CRLF=1 で計上・選択長も CRLF=1
  - 空選択・全選択・部分選択の境界

### L4(性能・手動)

- 不要(低頻度パスのみ)

### L5(NVDA 実機・ユーザー実施・**必須**)

CRLF ファイル・LF ファイル・CR-only ファイル・混在ファイルの各々で:
- ← → が 1 step、SR が「改行」1 回だけ発声
- BS/Del で改行が 1 回で消える
- Shift+← → で改行を 1 括選択
- 位置照会(Ctrl+? 系)で文字数が期待どおり
- 既存の空行能動発声・CSV モード・折り返しの回帰なし

## 8. 実装プロセス(CLAUDE.md §3 準拠)

- 単一 commit ではなく **Core / Editor / App の 3 タスクに分割** = subagent 個別レビューが自然
- Core → Editor → App の順(依存方向)
- 各タスクで「実装 → 仕様レビュー(subagent)」= CLAUDE.md §3-4
- **前倒しレビュー例外の該否**:
  - Task Core: 後続タスク(Editor/App)が依存する新抽象(`SnapOutOfCrlf` / `CountCrlfPairs`)= **コード品質レビュー**
  - Task Editor: 外部入力(マウス座標)・SR 経路変更あり= **脆弱性レビュー** および **コード品質レビュー**
  - Task App: 位置読み上げ経路のみ=通常の仕様レビュー
- 最終ブランチレビュー(2 パス)= コード品質 + 脆弱性
- **L5 必須**(SR 経路変更のため)= マージ前にユーザーへ実機検証依頼

## 9. リスクと緩和

- **リスク R1**: UIA GetTextRange の raw text 返却が SR によって「復帰/改行」と別読みされる
  - 緩和: L5 で確認。別読みなら §10 申し送りの正規化 fixup を追加
- **リスク R2**: snap を通す入り口の網羅漏れ = mid-CRLF に着地する経路が残る
  - 緩和: 「入り口の enumerated 一覧」(§5.4)を設計書に固定 + テストで各経路を確認
- **リスク R3**: `CountCrlfPairs` の性能(位置照会のたびに全走査)
  - 緩和: 位置照会は低頻度(ホットキー押下時のみ)= 現状 O(N) で許容。ホット経路(タイプ中の文字数表示)には出さない
- **リスク R4**: 既存テストの回帰(caret 位置期待値が mid-CRLF を含む)
  - 緩和: 既存 CRLF テスト(`EditorControlConvertEolsTests` 等)を先に走らせて回帰洗い出し

## 10. 申し送り(follow-up)

- **F-1(L5 で SR 別読みが残った場合のみ)**: `UiaTextHostAdapter.GetTextRange` で CRLF → LF 正規化を追加する fixup。offset/length 乖離の SR 実装差リスクを承知の上で対応判断
- **F-2(将来検討)**: `TextChangedEvent` の delta 通知でも CRLF pair を 1 単位として渡す最適化(現状は raw text delta で問題ないが SR 実装によっては音声化に影響し得る)
- **F-3(将来検討)**: サロゲート=1 化(現状 2)。ユーザーから要望が上がった場合に別設計として起票

## 11. 参考

- 前回関連: `EditorControl.ConvertEols`(P6 Task 5 で導入)の「(m, k) 分解による caret 復元」= 「1 改行=1 論理単位」の概念は既にコード内で使われている
- `PositionFormatter` の既存コメント(「CRLF=2・サロゲート=2 で数える」)は本設計採用時に「CRLF=1・サロゲート=2」に更新
- サロゲート atomic の実装パターン(`MoveLeftChar/MoveRightChar` の High/Low 判定)は本設計での CRLF atomic の直接テンプレート
