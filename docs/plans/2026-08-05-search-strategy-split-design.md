# 検索照合の戦略分離(`SnapshotSearcher` リファクタ) 設計書

作成日: 2026-08-05
ブランチ: `feature/search-strategy-split`
対象: `yEdit.Core/Search/SnapshotSearcher.cs` + `yEdit.App/SearchController.cs`

本書は「エディターコントロール以外のリファクタリング候補」調査(2026-08-05)で
**優先度 高**と判定した項目 A の設計を確定する。

## 0. 位置づけと前提

### 0.1 これは性能改善 PR ではない

調査時点で、実測(L4 Bench)は**ユーザー判断により意図的にスキップ**した。
「構造的に問題があるなら、実害が測れなくてもリファクタ対象とする」という方針である。

したがって本 PR は **挙動不変の構造改善**として立てる。
性能はあくまで副次効果であり、**PR description にも設計書にも性能数値は書かない**
(測っていないものを主張しない)。CLAUDE.md §2「リファクタは挙動不変が原則」に素直に乗る。

> この判断は後から覆せる。実測が必要になったら、本リファクタ完了後に
> `tests/yEdit.Core.Bench` へ検索系のケースを足す別テーマとして扱う。

### 0.2 調査で確定した事実

| # | 事実 | 典拠 |
|---|---|---|
| 1 | `SnapshotSearcher` の利用者は `SearchController` だけ(4 箇所) | `SearchController.cs:90,123,186,276` |
| 2 | grep は `SnapshotSearcher` を通らず `TextSearcher` を直接使う | `GrepService.cs:45` / `GrepController.cs:97` |
| 3 | 閾値超経路の private メソッドは 6×2=12 個で、接尾辞 `*LiteralWindow` / `*RegexPerLine` により**既に戦略ごとに対称**に並んでいる | `SnapshotSearcher.cs:161-503` |
| 4 | `SearchOptions` は `record` = 構造的等価 | `SearchOptions.cs:7` |
| 5 | `TextBuffer.Current` は編集/Undo/Redo でのみ `_current` を差し替えるフィールド返し | `TextBuffer.cs:42,57,67` |
| 6 | `IFindReplaceView` に閉じ通知が無い(`Visible` / `IsDisposed` のみ) | `IFindReplaceView.cs:25-40` |

事実 1・2 により **`SnapshotSearcher` の public API を変える自由度が高い**(本設計では変えないが、
制約として効いてこない)。事実 3 により**抽出はほぼ機械的な移動**になる。

## 1. 解決する構造問題

### 1.1 材質化が無条件・無キャッシュ

`Materialize(snap)` = `snap.GetText(0, CharLength)` が 6 つの public メソッド**全てで無条件に**
呼ばれる(`SnapshotSearcher.cs:77,87,103,116,128,151`)。閾値は 32M chars なので、
実用ファイルはほぼ全部この経路を通る。

結果として同一スナップショットに対する連続呼び出しが、毎回全文を作り直す:

| 経路 | 材質化回数 |
|---|---|
| F3 一回(`FindNext` → `Locate`) | 2 |
| 検索ボックスの 1 打鍵(`UpdateCount`) | 1 |
| `ReplaceOne`(`ReplacementAt` → `FindNext` → `FindNext` → `Locate`) | 最大 4 |

`GetText` は `StringBuilder` + `ToString()` なので 1 回あたり文書サイズの約 2 倍を確保する
(`TextSnapshot.cs:51-53`)。

### 1.2 三重分岐が 6 箇所に手書きで散っている

6 つの public メソッドがそれぞれ
`(閾値以下 → 材質化) / (閾値超 かつ regex → 行単位) / (閾値超 かつ literal → 窓照合)`
を独立に書いている。分岐条件が 6 箇所に複製されているため、
戦略を足す・条件を変えるときに 6 箇所すべてを揃えて直す必要がある。

### 1.3 呼び出しごとに searcher を作り直している

`SearchController` は操作のたび `new SnapshotSearcher(opts)` する
(`SearchController.cs:90,123,186,276`)。内部で `new TextSearcher(options)` →
`new Regex(...)` が走る(`TextSearcher.cs:43`)。
**インスタンス生成の `Regex` は .NET の静的キャッシュに乗らない**ため、
検索ボックスの打鍵ごとにパターンを再コンパイルしている。

この構造がある限り、Core 側にインスタンスキャッシュを置いても打ち消される。
**1.1 の解決には App 側の変更が不可欠**であり、本 PR のスコープが 2 層にまたがる理由である。

## 2. スコープ

### 2.1 やること

1. **戦略抽出** — `ISnapshotSearchStrategy` と 3 実装への分離(§3)
2. **スナップショット単位キャッシュ** — 材質化済み文字列を戦略の状態として保持(§4)
3. **`SearchController` の searcher 保持** — 照合条件が変わるまで使い回す(§5)

### 2.2 やらないこと(申し送りは §8)

- 閾値超 `ReplaceInRange` の Fragment string 組み立ての解消(元「P7 送り」)
- 閾値 32M chars を境にした意味論分裂そのものの是正
- 閾値・窓サイズの値の見直し
- grep 側(`GrepService` / `GrepController`)の変更

## 3. 型と責務

```
ISnapshotSearchStrategy  (internal)
  Count / FindNext / FindPrev / Locate / ReplacementAt / ReplaceInRange
  ├─ MaterializedSearchStrategy    全文材質化 + TextSearcher。意味論は「正」。材質化済み文字列を保持
  ├─ LiteralWindowSearchStrategy   閾値超リテラル(窓照合)。snapshot 非依存(状態を持たない)
  └─ RegexPerLineSearchStrategy    閾値超 regex(行単位)。snapshot 非依存(状態を持たない)

SnapshotSearcher  (public・現行 API 不変)
  戦略を選んで委譲するだけのファサード
```

`Core.Tests` には `InternalsVisibleTo` があるため、**各戦略を直接単体テストできる**
(現在は `SnapshotSearcher` の閾値注入コンストラクタ経由でしか叩けない)。

### 3.1 移動の対応表

| 移動元(`SnapshotSearcher.cs`) | 移動先 |
|---|---|
| `*LiteralWindow` 6 個 + `GetLiteralComparison` + `IsWordChar` / `IsBoundary` / `IsWordBoundaryMatch` | `LiteralWindowSearchStrategy` |
| `*RegexPerLine` 6 個 + `ReadLine` | `RegexPerLineSearchStrategy` |
| `Materialize` + `_inner` への 6 委譲 | `MaterializedSearchStrategy` |
| `IsLarge` + public 6 メソッドの分岐 | `SnapshotSearcher`(セレクタ) |

`TextSearcher` は `Materialized` と `RegexPerLine` の両方が使うため、
`SnapshotSearcher` が 1 個生成して両者へ渡す(現行と同じく `IsValid` / `Error` の出所も
この 1 個に一本化される)。

### 3.2 `IsValid` が false のときの短絡

現行は 6 つの public メソッドが先頭で `if (!IsValid)` を判定し、
それぞれ固有の値(`0` / `null` / `(GetText(...), 0)`)を返す(`SnapshotSearcher.cs:74,84,100,113,125,148`)。
**この短絡はファサード側に残す**(戦略へ降ろさない)。
戦略は「有効な照合条件が与えられている」ことを前提にでき、3 実装に同じガードが 3 重化するのを避ける。

## 4. 戦略選択とキャッシュ寿命

### 4.1 選択規則(現行と同一)

```
IsLarge(snap) == false          →  Materialized
IsLarge(snap) && opts.UseRegex  →  RegexPerLine
IsLarge(snap) && !opts.UseRegex →  LiteralWindow
```

`IsLarge` は `snap.CharLength > _thresholdChars`。**比較演算子は現行のまま `>`**
(`>=` にすると閾値ちょうどの文書の意味論が変わる = 挙動変更)。

### 4.2 キャッシュ

閾値超の 2 戦略は snapshot 非依存なので `SnapshotSearcher` 生成時に 1 個ずつ作って使い回す。

`MaterializedSearchStrategy` だけが snapshot 依存で、
`ReferenceEquals(snap, 前回の snap)` が偽のときだけ作り直す。
**保持されるのは常に最大 1 本**で、編集・Undo・Redo で自動的に無効化される。

参照同一性を「文書が変わっていない」の signal に使う根拠:
`TextBuffer.Current` は編集時にのみ `_current` を差し替えるフィールド返しであり(事実 5)、
同じ idiom を `TextBuffer.Modified` が既に採用している(`TextBuffer.cs:46` の
`ReferenceEquals(_current.Root, _savedRoot)`)。**新しい前提を持ち込まない**。

## 5. `SearchController` 側の searcher 保持

### 5.1 再生成の条件

`SearchOptions` は record = 構造的等価(事実 4)なので、
`保持中の options != 今回の options` のときだけ `SnapshotSearcher` を作り直す。
これで打鍵ごとの `new Regex` が消える。

### 5.2 破棄トリガ

| # | トリガ | 実装 |
|---|---|---|
| i | 照合条件の変化 | 5.1 の再生成に含まれる |
| ii | アクティブ文書の切替 | 既存購読 `SearchController.cs:34` に破棄を追加 |
| iii | ~~検索ダイアログのクローズ~~ → **ユーザーによる検索の終了(Dismissed)** | **`IFindReplaceView` への契約追加が必要**(§5.3) |

> **精密化 1(2026-08-05・計画策定時)**: (iii) を「クローズ」から「Dismissed」へ改める。
>
> `FindReplaceDialog.OnFormClosing`(`:124-133`)は `CloseReason.UserClosing` を
> **キャンセルして `Hide()` に差し替えている**。つまりユーザー操作でこのダイアログが
> 閉じることはなく、Dispose されるのは MainForm ごと落ちるときだけである。
> 「閉じ通知」を足してもアプリ終了時にしか発火せず、破棄トリガとして機能しない。
>
> ただしダイアログ側は**発生源では両者を区別できている**:
>
> | `Hide()` の呼び出し元 | 意味 | 通知 |
> |---|---|---|
> | `_close.Click:60` / `Escape:108` / `OnFormClosing:129` | ユーザーが検索を終えた | **発火する** |
> | `_next.Click:51` / `_prev.Click:56` / `Enter:118` | G-2 の一時退避 | 発火しない |
>
> よって通知は「閉じた」ではなく**「ユーザーが検索を終えた(Dismissed)」**として、
> 上段 3 箇所からだけ発火させる。§5.3 で決めた意図(保持はダイアログを使っている間だけ /
> `!Visible` は使わない)はそのまま満たされ、影響ファイルも当初見積もりと同じ 3 本。

### 5.3 `!Visible` を破棄トリガに使ってはいけない

G-2 の仕様により「次を検索」の後にダイアログは**自らを Hide する**
(`IFindReplaceView.cs:7-8` の `FindNext/FindPrev` の bool 契約)。
**非表示のまま F3 を連打する経路こそキャッシュが最も効く場面**であり、
`!Visible` で破棄すると本リファクタの狙いが丸ごと消える。

したがって (iii) は `Visible` では代替できず、閉じたことの明示的な通知が要る。
`IFindReplaceView` に閉じ通知を追加する。**これが本リファクタで唯一ビュー契約に触れる箇所**である。

影響は 3 ファイル:

- `src/yEdit.App/Abstractions/IFindReplaceView.cs`(通知の宣言)
- `src/yEdit.App/FindReplaceDialog.cs`(発火)
- `tests/yEdit.App.Tests/Fakes/FakeFindReplaceView.cs`(フェイクの追随)

> 代案として「契約を触らず (i)+(ii) だけにする」も検討したが、その場合
> 「ダイアログを閉じた後、編集もタブ切替もしなければ文書 1 本ぶんの string が残る」。
> キャッシュ寿命を「ダイアログが開いている間」と決めた以上、契約追加を採る。

## 6. 契約ドキュメントの扱い

現在 `SnapshotSearcher.cs:16-31` の `<summary>` が「壊れる契約」4 項目
(改行跨ぎ不可 / アンカーの行束縛 / WholeWord の ASCII 判定 / ReplaceAll の string 組み立て)を
列挙する**唯一の場所**である。

分割でこれを散逸させると、次に触る人が契約を見失う。したがって:

- **ファサード**(`SnapshotSearcher`)には「どの条件でどの戦略が選ばれるか」の要約表を残す
- **各戦略クラス**に自分の契約の詳細を持たせる
- `SnapshotSearcherRegexAnchorTests` が参照している記述(アンカーの行束縛)との対応を切らさない

## 7. テスト戦略

### 7.1 挙動不変の証明

第一の証明は、以下 4 本を**一行も変えずに**緑を維持することである。

| テスト | 行数 | 役割 |
|---|---|---|
| `Core.Tests/Search/SnapshotSearcherTests.cs` | 299 | 閾値二層化の挙動 |
| `Core.Tests/Search/SnapshotSearcherRegexAnchorTests.cs` | 74 | 閾値超アンカー挙動の凍結 |
| `Core.Tests/Search/TextSearcherTests.cs` | 199 | 照合エンジン本体 |
| `App.Tests/SearchControllerTests.cs` | 567 | 通知内容を含む Controller 挙動 |

### 7.2 新規テストは「挙動不変の証明」に数えない

キャッシュ無効化のテスト(**編集後に同じ searcher で検索すると新しい本文が見える**)は
**新規の不変条件**であり、変更前の src では常に緑になる。
よって「新規テストを変更前 src で走らせて挙動不変を証明する」手法の対象外である。

- **既存テスト** = 挙動不変の証明
- **新規テスト** = 新しい不変条件の固定

この 2 つを混同して「新規テストも変更前で緑だったから挙動不変」と書かないこと
(嘘の安全宣言になる)。

### 7.3 新規に足すテスト

1. キャッシュ無効化 — 編集 → 同一 searcher で再検索 → 新しい本文がヒットする
2. 戦略選択 — 閾値の前後で選ばれる戦略が切り替わる(内部可視性を使った直接検証)
3. 各戦略の単体テスト — 抽出によって初めて可能になったぶん(既存の閾値注入経由テストと重複しない範囲で)
4. **閾値境界**(`CharLength == thresholdChars` ちょうど・空でない文書)— §7.4 精密化 2 で
   必須と確定。`>` / `>=` の変異を kill できる形にすること

### 7.4 ミューテーション検証(最終品質パスのスポットチェック)

対象は戦略セレクタの分岐に絞る: `IsLarge` の比較演算子(`>` → `>=`)と `UseRegex` の反転。

**タスク着手時に先に確認すること**: 閾値境界(`CharLength == thresholdChars` ちょうど)の
テストが `thresholdChars` 注入コンストラクタ経由で実在するか。
無ければミューテーションが境界に当たらず kill できない
(PR #35 で「計画指定のミューテーションが境界に当たらず kill できなかった」前例がある)。
存在しなければ §7.3 に境界テストを追加してからミューテーションを行う。

> **精密化 2(2026-08-05・計画策定時)**: 上記の事前確認を実施した。**境界テストは存在しない。**
>
> `IsLarge` の `>` を `>=` へ変異させても、既存 4 本すべてが緑のまま生き延びる。
> `CharLength == thresholdChars` になるのは `EmptySnapshot_does_not_throw_and_yields_no_hits`
> (空文書・`threshold: 0`)だけで、空文書は材質化経路でも窓照合経路でも全 API が
> 同じ値(`0` / `null` / `("", 0)`)を返すため差が出ない。他のテストは
> `MakeLarge(threshold: 4)` に対して 8〜27 文字と、閾値から十分離れている。
>
> よって**境界テストの新規追加は必須**であり、§7.3 の項目 4 として計画へ落とす。
> 空でない文書で経路差が観測できる形にすること:
> `"ab\ncd"`(5 文字)+ `threshold: 5` + 改行跨ぎ regex `b\nc` なら、
> `>` は材質化経路でヒット、`>=` は行単位経路で `null` となり変異を kill できる。

## 8. 申し送り

本 PR では触らず、記録のみ残す。

| ID | 内容 | 理由 |
|---|---|---|
| S-1 | 閾値超 `ReplaceInRange` が Fragment を string で組み立てる(元「P7 送り」・`SnapshotSearcher.cs:30,137`) | 置換経路 = データ破壊リスク。対象は 64MB 超の文書のみで、実測なしで触るには重い |
| S-2 | 閾値 32M chars を境に**意味論の異なる 2 エンジンが無言で切り替わる**構造そのもの(改行跨ぎ不可 / アンカーの行束縛 / WholeWord が ASCII 判定) | 是正は挙動変更を伴い、別テーマの企画になる |
| S-3 | `GrepController.cs:97` の `new TextSearcher(opts).IsValid` — 検証のためだけに生成して捨てている | 本 PR のスコープ(grep 非対象)の外 |

## 9. L5 実機 SR 検証

**必要**。`SearchController` は `IAnnouncer` 経由で SR 発声するため SR 経路に触れる
(CLAUDE.md §5「判定に迷ったら必要に倒す」)。

確認項目:

| # | 操作 | 期待 |
|---|---|---|
| 1 | 検索ボックスへ入力 | 件数表示が従来どおり更新される |
| 2 | F3 / Shift+F3 | 「N 件中 M 件目」が従来どおり読まれる |
| 3 | **ダイアログ非表示での F3 連打** | 発声が従来と同一(キャッシュが効く主経路) |
| 4 | 末尾での F3 | 「これ以上見つかりません」 |
| 5 | 置換 / すべて置換 | 「置換しました。N 件中 M 件目」「N 件置換しました」 |
| 6 | 本文編集 → F3 | 編集後の本文でヒットする(キャッシュが stale にならない) |
| 7 | タブ切替 → F3 | 切替先の本文でヒットする |
| 8 | 正規表現エラー / 複雑すぎる式 | 「正規表現が正しくありません」「検索式が複雑すぎます」 |

## 10. 影響ファイル

| ファイル | 変更 |
|---|---|
| `src/yEdit.Core/Search/SnapshotSearcher.cs` | ファサード化(大幅縮小) |
| `src/yEdit.Core/Search/ISnapshotSearchStrategy.cs` | 新規 |
| `src/yEdit.Core/Search/MaterializedSearchStrategy.cs` | 新規 |
| `src/yEdit.Core/Search/LiteralWindowSearchStrategy.cs` | 新規 |
| `src/yEdit.Core/Search/RegexPerLineSearchStrategy.cs` | 新規 |
| `src/yEdit.App/SearchController.cs` | searcher 保持・破棄 |
| `src/yEdit.App/Abstractions/IFindReplaceView.cs` | 閉じ通知の追加 |
| `src/yEdit.App/FindReplaceDialog.cs` | 閉じ通知の発火 |
| `tests/yEdit.App.Tests/Fakes/FakeFindReplaceView.cs` | 契約追随 |
| `tests/yEdit.Core.Tests/Search/*` | 新規テスト追加(既存は無変更) |

## 11. 本テーマの位置づけ(後続)

2026-08-05 の調査で確定した対応順序は **A → C → B → E**(D は見送り)。
本書は A のみを扱う。以降はトピックごとに独立したブランチ + PR で回す。

| ID | 内容 | 状態 |
|---|---|---|
| A | 検索照合の戦略分離 | **本書** |
| C | `MainForm` のキーバインド定義一元化(`ProcessCmdKey` と `BuildMenu` の二重管理解消) | 未着手 |
| B | `FileController` の復元責務分離(930 行 / 材質化シーケンス 5 重複 / 無題連番 4 重複) | 未着手 |
| E | `EncodingPickDialog` の絶対座標をやめて他 7 ダイアログと同じ `TableLayoutPanel` 方式へ揃える | 未着手 |
| D | `BackupCoordinator` の 3 責務分離 | **見送り**(`_map` / `_writer` / `_shutDown` を共有しており、分けると状態同期のバグ面が広がる) |
