# 上書き保存の符号化劣化が無警告(A-10)設計書

- 対象: `docs/plans/2026-08-22-v0.2-release-bug-audit.md` §4 A-10
- 前提 main: `86d7145`(PR #52 マージ後)
- 本書は**策定時スナップショット**(CLAUDE.md §8)。実装中の精密化と実施記録の追記のみ行う。

## 1. 目的

Shift_JIS / EUC-JP で開いた文書に、そのコードページで表せない文字(絵文字・`€`・`–` 等)を
貼って Ctrl+S すると、**無警告で `?`(0x3F)に置換されて保存される**。エディタ上の表示は
元の文字のままなので、ユーザーはファイルを閉じて開き直すまで喪失に気づかない。

同じ判定を行う `CanEncodeBuffer` は既に存在するが、**「名前を付けて保存」からしか呼ばれていない**。
本ブランチのゴールは、**上書き保存経路にも同じ事前確認を置き、SaveAs との非対称を解消する**ことに絞る。

## 2. 現行 main での実在確認(2026-08-28)

監査は `119ae33` に対するものだが、その後の PR #42〜#52 は本経路に触れていない。
現行コードで全段を追い、**実在を確認した**。

| 段 | 場所 | 事実 |
|---|---|---|
| ① Ctrl+S の入口 | `FileController.Save()`(`:439`)→ `SaveDocument`(`:456`) | `State.Path` が null なら SaveAs へフォールバック。それ以外は重複タブガード(`:472`)を通って `WriteToPath`(`:491`)へ直行 |
| ② 書込 | `WriteToPath`(`:761`)→ `TextFileService.Save` | 到達性プローブ → `ApplyEol` → `ConvertEols` → 書込。符号化可否の判定は**どこにも無い** |
| ③ 実際の符号化 | `TextFileService.cs:389` | `Encoding.GetEncoding(codePage, EncoderFallback.ReplacementFallback, ...)`。表せない文字は例外を投げず `?` へ落ちる |
| ④ 既存の判定器 | `CanEncodeBuffer`(`FileController.cs:688`) | `ExceptionFallback` でバッファ全走査。呼出元は `SaveAsDocument`(`:650`)**のみ** |

**警告点の棚卸し**(`rg "文字コードの警告" src/`)= 2 箇所だけ:

- `FileController.cs:391` — 読込時の `HadReplacementChar` 警告
- `FileController.cs:653` — SaveAs の符号化劣化警告(C-2 追補 I-2)

上書き保存にはひとつも無い。

### 2.1 気づけない理由(A-10 が Important である根拠)

読込側の U+FFFD は**本文に見える形で残る**(`FileController.cs:382` のコメントが
「silent data loss ではない」と明記)。対して保存側の `?` 置換は**ディスク上でしか起きない**。
バッファは絵文字を保持したままなので、画面・文字数・SR の読み上げのいずれにも痕跡が出ない。
検出できるのは再オープン時だけで、そのときには元データは既に無い。

## 3. 決定した方針(2026-08-28・ユーザー承認済み)

| 論点 | 決定 | 却下した案と理由 |
|---|---|---|
| 検出方式 | **`SaveDocument` で事前判定**(既存 `CanEncodeBuffer` を再利用) | ① 実書込中に `ExceptionFallback` で検出し、続行なら再書込 — 損失なしの保存で走査ゼロという利点はあるが、Core の契約変更が要り、`EncoderFallbackException` が `ArgumentException` 派生のため `WriteToPath` の catch フィルタ順序が load-bearing になる。② 保存後の事後通知 — ファイルは既に壊れており、SaveAs(事前確認)と非対称 |
| 「続行」後の再保存 | **毎回確認する** | 承諾フラグを持つと無効化条件(本文編集・文字コード変更・SaveAs)の設計とテストが要る。保存のたびに実際にデータが失われる以上、毎回問うのが正直。メモ帳 / Notepad++ と同じ |
| 逃げ道ボタン | **出さない**(OK/キャンセルのみ) | 「はい=UTF-8 で保存」を足すと Ctrl+S が文書の文字コードを変える挙動変更になり、BOM 有無の決定と `State` / hot exit レイアウトへの波及設計が要る。文言で「名前を付けて保存」へ誘導する |
| 範囲 | **A-10 単独** | A-9(改行コード判定 4,096 文字窓)は「保存時の無警告データ書換」という症状は同種だが、原因も修正面(`TextFileService` の EOL 検出窓)も別。設計書が 2 本立てになる |

## 4. 設計

### 4.1 変更点(App 層のみ・Core 不変)

`FileController.SaveDocument` の**重複タブガードの後・`WriteToPath` の前**に 1 ブロック追加する。
`CanEncodeBuffer` は既に `private static` なのでシグネチャ変更は不要。

```csharp
// A-10: 上書き保存経路にも符号化劣化の事前確認を置く(SaveAs の C-2 追補 I-2 と対称)。
if (
    doc.State.Encoding.CodePage != 65001
    && !CanEncodeBuffer(doc.Editor.CurrentBuffer, doc.State.Encoding)
    && !_prompt.OkCancel(
        "現在の文字コードで表せない文字が含まれています。'?' として保存されデータが失われます。"
            + "元の文字を残すには「名前を付けて保存」で UTF-8 を選んでください。続行しますか?",
        "文字コードの警告",
        defaultCancel: true
    )
)
{
    return false; // SaveAs と違い戻る先のダイアログがないので中止する
}
```

### 4.2 配置の根拠(3 点とも load-bearing)

- **重複タブガード(`:472`)の後** — 重複時はそもそも保存させないので、バッファ全走査を無駄打ちしない。
  ガードが先という順序は、遠隔共有で無駄な 5 秒を待たせないために `TryInspectSaveTarget` より前へ
  置いた A-7 (b) の判断と同じ系列にある。
- **`WriteToPath`(`:491`)の前** — `ApplyEol` / `ConvertEols` の副作用を起こす前に短絡する。
  `ConvertEols` が触るのは CR / LF だけで、CR / LF は対象の全コードページ(932 / 51932)で
  表現可能なので、判定を `ConvertEols` の前に置いても**答えは変わらない**。
  SaveAs 側も警告 → `WriteToPath` の順で、順序が揃う。
- **`State.Path is null` 分岐(SaveAs フォールバック)の後** — SaveAs は自前の警告(`:649`)を
  持つので、無題タブの Ctrl+S で二重に出ない。

### 4.3 `CodePage != 65001` ガード

UTF-8 は BMP + astral を全て表現できるので走査が常に無駄になる。外すと**大半の保存**
(既定は UTF-8)に全走査が入る。SaveAs 側の既存ガード(`:649`)と同一の根拠であり、
性能上の意味を持つ **load-bearing な条件**。テストで固定する(§5-3)。

SaveAs が扱う保存先コードページは `EncodingCatalog` の 3 種(65001 / 932 / 51932)だけなので、
非 UTF-8 = 932 か 51932 に限られる。

### 4.4 文言

SaveAs 版とは主語が違うので、**文言を共有せず別に書く**。

- SaveAs: 「**選択した**文字コードで表せない文字が…」= いまダイアログで選んだ値
- Ctrl+S: 「**現在の**文字コードで表せない文字が…」= 文書が持っている値

逃げ道ボタンを出さないと決めた(§3)ので、代わりに文中で「名前を付けて保存」へ誘導する。
問い(「続行しますか?」)を末尾に置く形は既存 2 件と揃える — SR は本文を頭から読むため、
何を聞かれているかが先に立つ構成にする。

### 4.5 `defaultCancel: true`

破壊的な確認なので安全側に倒す。SaveAs の S-12 / C-2 追補と**対称**。
ここでも load-bearing で、既定が OK 側だと次の 2 経路で警告が無力化される:

- Ctrl+S 直後に Enter を叩く操作(読み上げが遅いときの連打)
- 閉じる確認の「はい」→ そのまま Enter が確認へ抜ける連鎖(§4.6)

### 4.6 波及

| 面 | 内容 |
|---|---|
| 閉じる確認 | `ConfirmDiscardIfDirty`(`:840`)の `DialogResult.Yes => SaveDocument(doc)`(`:847`)経由で警告が出る。キャンセルすると `false` が伝播し**クローズが中止**される。データを失わずに戻れるので正しいが、**挙動変更**なのでテストで固定する(§5-6) |
| hot exit / バックアップ | `BackupStore` は UTF-8 固定で `SaveDocument` を通らない。影響なし |
| アプリ終了・Windows シャットダウン | 既存の未保存確認と同じ場所に MessageBox が 1 枚増えるだけで、**新しい待機を足さない**。A-8 で判明した「STA の管理待機は SENT メッセージを配送する」再入問題の条件を作らないことを実装時に確認する |
| 副次的な改善 | 誤った文字コードで開いて U+FFFD が入った文書(読込時に `:391` で警告済み)も Ctrl+S で止まる。U+FFFD は 932 / 51932 で表現不能なため。現状は無言で `?` 化している |
| 性能 | 非 UTF-8 文書のみ 1 パス追加。損失ありの文書は最初の該当文字で `EncoderFallbackException` により短絡するので、警告が出るケースほど速い |

## 5. テスト(L3 = kxEdit.App.Tests)

既存の `SaveAs_LossyEncoding_*` 群(`FileControllerTests.cs:1615` 以降)の対称形として置く。

1. `Save_LossyEncoding_Cancel...` — キャンセル → ディスク未変更・`Modified` 維持・保存点未更新
2. `Save_LossyEncoding_OkProceedsAndWrites` — 続行 → `?` で書かれ保存点が進む
3. `Save_Utf8_DoesNotWarn` — UTF-8 文書 + 絵文字 → 警告なしで保存(**§4.3 ガードの網**)
4. `Save_SjisEncodable_DoesNotWarn` — 932 で表現可能な本文 → 警告なし(空振り警告の回帰防止)
5. `Save_LossyEncoding_DefaultsToCancel` — `OkCancelCalls` が `("文字コードの警告", true)`
   (**`defaultCancel` 変異の網**)
6. `CloseConfirm_...CancelAbortsClose` — 閉じる確認「はい」→ 警告キャンセル → タブが閉じない(§4.6)
7. 既存の重複タブテストに 1 行追加 — `Error` のみが出て「文字コードの警告」が出ないこと
   (**§4.2 の順序の網**)

fixture の注意(CLAUDE.md §4.B):

- 3 は「既定値と区別できる」ことが要る。UTF-8 は既定なので、**警告が出ないこと**だけでなく
  **保存が成功して内容が絵文字のまま**であることまで見る(ガードを外すと 4 と同じく落ちる)。
- 4 は 3 と別方向の網。3 だけだと `CanEncodeBuffer` の呼出自体を消す変異が生き残る。

### 5.1 ミューテーション検証は実施しない

CLAUDE.md §4.A の**禁止側**に該当する(ファイルの入出力処理・単純なイベントハンドリング)。
ユーザーのグローバル規範も「原則実施しない」。テスト設計時のセルフチェック
(§5 の 3 / 4 / 5 が実際に変異を殺す形になっているか)のみ行う。

## 6. 進め方(CLAUDE.md §3「簡略化の基準」に該当)

単一ファイル・数十行の変更なので、**実装を 1 タスクに統合し単一 commit**、最終レビューも
品質パスと脆弱性パスを 1 回に統合してよい。ただし**別エージェントレビューと品質ゲートは省略しない**。

- ブランチ: `feature/save-encoding-loss-warning`(main `86d7145` から作成)
- ゲート: `tools/pre-merge-check.ps1` → **EXIT 0**
- **L5 は必要と判定**(1 項目): SR 経路のコードには触れないが、SR ユーザーの主経路である
  Ctrl+S に新しい MessageBox が増える。NVDA が文言を通しで読むこと・既定フォーカスが
  キャンセル側にあることを実機確認する。CLAUDE.md §5「判定に迷ったら『必要』に倒す」に従う。

## 7. 非目標(YAGNI)

- A-9(改行コード判定の 4,096 文字窓)— 別テーマ(§3)
- 承諾の記憶 / 一度だけ確認 — §3 で却下
- 「UTF-8 で保存し直す」ボタン — §3 で却下
- `TextFileService.Save` の `ReplacementFallback` 自体の変更 — 警告を通った後の書込は
  従来どおり `?` に落とす。Core の契約は不変

## 8. 申し送り(実装時に追記する)

### 8.1 §4.3 の根拠は不完全だった(2026-08-28・実装時に判明)

本書 §4.3 は `CodePage != 65001` ガードを**性能上の** load-bearing と説明したが、これは
**不完全**だった。実装中の網のセルフチェックで、ガードは**挙動**も担っていることが実測で判明した。

`CanEncodeBuffer`(`FileController.cs`)は 8,192 文字のチャンクごとに**独立して**
`GetByteCount` を呼ぶ。`SnapshotReader.Read`(`Core/Buffer/SnapshotReader.cs:48`)は
`while (count > 0 && Ensure())` でピース境界を跨いでバッファを埋めきるため、
**サロゲートペアはチャンク境界で確実に割れる**。割れた側は孤立サロゲートに見えるので
`EncoderFallback.ExceptionFallback` が飛び、**正当な UTF-8 文書に対して `false` が返る**。

.NET 9 実測(実装者 + 仕様レビューが独立に再現):

- チャンク 1(`'a'`×8191 + U+D83D)→ `EncoderFallbackException idx=8191 char=U+D83D`
- チャンク 2(単独 U+DE00)→ 同じく throw
- 分割せず一括 → OK

したがってガードを外すと、8,192 の境界に astral 文字が来た UTF-8 文書の Ctrl+S が
毎回誤警告を出す。§4.3 は「性能上の意味を持つ」と書いたが、正しくは
**「性能 + 挙動の両方」**である。

### 8.2 `CanEncodeBuffer` のチャンク境界誤検知(既存の潜在欠陥・本テーマの対象外)

§8.1 の性質は本ブランチが作ったものではなく、`CanEncodeBuffer` に元からある欠陥で、
**SaveAs 側の呼出(`FileController.cs:699`・A-10 実装後の行番号)にも同じ問題がある**。

**現時点で実害はない**。理由: 呼出元 2 箇所とも `CodePage != 65001` で UTF-8 を対象外に
しており、`EncodingCatalog.SelectableEncodings` の残り(932 / 51932)はどちらも astral を
表現できない。よって「astral を含む = 実際に劣化する」ので、誤検知だとしても警告は
結果的に正しい。**将来 UTF-8 を対象に含める変更を入れると、即座に誤警告になる。**

本番の書込経路(`TextFileService.Save`)は `encoder.Convert(..., flush: false)` で
**ステートフルな `Encoder` を使い回す**ので、この問題を持たない。根治する場合は
`CanEncodeBuffer` も同じ形にする(probe と本番のズレを構造的に消せる)。

回収するか否かは未決。本テーマ(A-10 = 上書き保存に警告を足す)の範囲外なので、
本ブランチでは**受容し文書化するに留める**。

### 8.3 網が将来 silent に空虚化する箇所(監視対象)

`Save_Utf8AstralAtReadChunkBoundary_DoesNotWarn` 系のテストが
`CodePage != 65001` の変異を殺せるのは、**§8.2 の欠陥が存在するあいだだけ**である。
§8.2 を直すと `CanEncodeBuffer` は UTF-8 で常に true を返すようになり、ガードは
純粋な性能ガードへ戻る。その時点でこの網は**原理的に空虚化する**。

これは正しい状態であって、偽の網を作って取り繕ってはいけない。§8.2 に着手する際は、
このテストの xmldoc を読み、網の役割を終えたことを明記して整理すること。
`8 * 1024` は `CanEncodeBuffer` の読み取りチャンク長で、**変えると無警告で網が消える**。
