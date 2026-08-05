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

> **追記(2026-08-05・Task 4 実施時)— テスト設計の教訓を 1 件足す。**
>
> ### 7.5 「合計を見る網」と「初回呼び出しを見る網」は別物
>
> Task 4 品質レビュー I-5 は「6 API がキャッシュを共有していることが固定されていない」という
> **正しい指摘**だったが、**添えられていたテストはその懸念を検出できなかった**:
>
> ```csharp
> // 6 API を順に叩いて…
> Assert.Equal(1, s.MaterializeCountForTest);   // ← 迂回変異を一切殺せない
> ```
>
> `FindNext` が `TextOf` を迂回しても、次の API がキャッシュを埋めるので**合計は 1 のまま**。
> 6 API のうち 5 個を迂回させても、残り 1 個が材質化すれば 1 になる。
>
> | 網 | 殺せる変異 | 殺せない変異 |
> |---|---|---|
> | **合計回数**を見る | 回数を**増やす**(多スロット化・辞書実装) | 回数を**減らす**(キャッシュ迂回) |
> | **新品の戦略へ 1 呼び出し**して 0 でないことを見る | 回数を**減らす**(迂回。どの API かを名指しできる) | 回数を**増やす** |
>
> **両方要る。** 片方だけでは網に穴が開く。実際に両方を実装し、迂回変異が 2 本目だけを
> 名指しで赤にすること・辞書変異が 1 本目だけを赤にすることを実測で確認した。
>
> **一般化**: 「指摘は鵜呑みにしない」(CLAUDE.md §4)は、指摘の妥当性だけでなく
> **指摘が持ってきた検出手段(テストコード)の有効性**にも適用すること。
> 指摘が正しくても、提示された網が機能するとは限らない。
>
> ### 7.6 網は「意図が言う場所」ではなく「コードが実際に分岐する場所」に置く
>
> 本ブランチで**同じ失敗が 3 度**起きた。いずれも「指摘・指示は正しいのに、添えられたテストが
> 対象の変異を殺せない」形である。
>
> | # | Task | 指示された網 | 殺せなかった理由 |
> |---|---|---|---|
> | 1 | 1 | 閾値境界に空文書(`threshold: 0`)を使う既存テスト | 空文書は**両経路とも同じ値**(`0` / `null` / `("", 0)`)を返すので差が出ない |
> | 2 | 4 | 6 API を順に叩いて材質化回数の**合計**が 1 であること | 迂回は回数を**減らす**だけ。次の API が埋めるので合計は 1 のまま |
> | 3 | 5 | 閾値超 `FindPrev(snap, CharLength + 100)` | リテラル戦略では `before` の効き先が `Math.Min(before + overlap, CharLength)` と `absStart < before` の 2 箇所だけで、**どちらも `CharLength` で頭打ち**。差が出るのは `before + overlap` が int を溢れるときだけ |
> | 4 | 5 fixup | (既存テストが網になっていると思われていた 2 件) | `FindNext_PastEnd_returns_null_above_threshold` は `from == CharLength` **ちょうど**で、ガード条件(厳密超え `>`)に当たらない。`FindPrev_LiteralAboveThreshold_matches_below` の `Assert.Null(above.FindPrev(snap, 0))` は**リテラル戦略**なので `before = 0` でも `end = Min(0 + overlap, L)` が小さく `while (end > 0)` が空回りして null を返すだけ=**例外にならない**(壊れるのは regex 戦略だけ) |
>
> 4 番の教訓は独立に言う価値がある: **「同じ形の assert が既にある」は「網がある」を意味しない。**
> 引数の値が境界の**どちら側か**、経路が**どの戦略か**まで一致していなければ、
> 見た目が同じ assert でも別の変異を見ている。
>
> **5 例目(Task 7)は向きが逆で、網が実装の穴を捕まえた。** 計画の指示どおりに書くと
> `ResolveSearcher` 内の `DropSearcher()` が**到達不能な死にコード**になっていた
> (各メソッドが先に `if (opts is null) return;` で早期 return するため、4 呼び出し側すべてで到達しない)。
> 不変条件のテストが赤くなって発覚した。**§7.6 は「網を疑え」だけでなく「網が赤いなら実装を疑え」でもある。**
>
> ### 7.7 計画に書くミューテーションは「コンパイルが通る等価変異」でなければならない
>
> Task 7 で、計画が指定した変異 2 件が**文字どおりには実行不可能**だった。
>
> | 指定した変異 | 実際に起きたこと |
> |---|---|
> | `DocumentClosed` の発火を削除 | `error CS0067`(イベントが使用されていない)+ Sonar `error S3264`(Remove the unused event ... or invoke it) |
> | `_searcherOptions != opts` を `false` 固定 | `error S1125`(不要な bool リテラル)+ `error S4487`(未読 private field) |
>
> **このリポジトリでは、イベントを orphan にする種類の退行がコンパイル段階で止まる。**
> `-warnaserror` + SonarAnalyzer の設定そのものが網として機能している(副次的な価値)。
>
> 計画にミューテーションを書くときは、**アナライザを通過する等価変異**を指定すること
> (例: 発火を却下パスへ移す / `_searcherOptions is null` で常時再利用にする)。
> 通らない変異を指定すると、実行者が現場で構成し直す手間が生じ、
> 「変異が通らなかった=網がある」と誤読される危険もある。
>
> **共通の構造**: テストを「意図(この行は文書長超の before をクランプする)」から書くと、
> **コードが実際に値を区別する領域**を外す。3 番の例では、クランプの意図は「文書長超の丸め込み」
> だが、**観測可能な唯一の効果はオーバーフロー防止**だった。
>
> **手順として**: 網を書いたら**必ずその場で対象の変異を入れて赤くなることを確認する**。
> 赤くならなければ、網が悪いか、守っているつもりのものが実は別物である。
> 後者なら**コメントの記述も実態に合わせて直す**(意図と実効果がずれたまま残ると、
> 次に読む人が同じ誤解をする)。
>
> 3 番はメモリーの `maxScan == int.MinValue` で上限が消える件と同型で、
> 「**クランプを外しても普通の値では全緑**」という危険な形である。

## 8. 申し送り

本 PR では触らず、記録のみ残す。

| ID | 内容 | 理由 |
|---|---|---|
| S-1 | 閾値超 `ReplaceInRange` が Fragment を string で組み立てる(元「P7 送り」・`SnapshotSearcher.cs:30,137`) | 置換経路 = データ破壊リスク。対象は 64MB 超の文書のみで、実測なしで触るには重い |
| S-2 | 閾値 32M chars を境に**意味論の異なる 2 エンジンが無言で切り替わる**構造そのもの(改行跨ぎ不可 / アンカーの行束縛 / WholeWord が ASCII 判定) | 是正は挙動変更を伴い、別テーマの企画になる |
| S-3 | `GrepController.cs:97` の `new TextSearcher(opts).IsValid` — 検証のためだけに生成して捨てている | 本 PR のスコープ(grep 非対象)の外 |

> **追記(2026-08-05・Task 2 fixup 実施時)**: 実装中に発見した申し送りを 1 件足す。
>
> | ID | 内容 | 理由 |
> |---|---|---|
> | S-7 | **`_selectionScope` / `_lastHit` がタブクローズで持ち越される**(最終脆弱性パス §6・**ユーザー判断で別テーマへ送付**)。アクティブタブを閉じて `TabControl.Selected` が発火しなかった場合、`SearchController` のこの 2 つが旧文書由来のまま残る。新文書が旧スコープより**長い**とクランプが効かず、**ユーザーが一度も選択していない範囲に「すべて置換」が実行され「N 件置換しました」と発声する**。クラッシュせず(`ReplaceCharRange` の `SnapAndClamp`)・データ捏造もなく(fragment は新文書由来)・Undo で戻せる | **main と挙動同一で本 PR 由来ではない。** 本 PR は「既存テスト無変更 + 凍結テスト 2 本が main とバイト同一」で挙動不変を証明することに価値があり、置換経路の挙動変更を混ぜるとその主張が濁る。L5 の範囲も広がる。**修正は `DocumentClosed` の購読で `_lastHit` / `_selectionScope` も落とす 1 行**(既存の `ActiveDocumentChanged` ハンドラとの対称化)。本 PR が `DocumentClosed` を新設したことで初めて 1 行で直せるようになった |
> | S-6 | **CLAUDE.md の環境ノートへ追記する候補**: Windows PowerShell 5.1 の `Get-Content` は既定が ANSI 読みで、**日本語コメントを含むファイルを比較すると全ファイルが差分ありと誤検出される**(Task 5 fixup で実際に踏んだ)。差分検証には `git diff -I'^\s*///'` や `git cat-file blob` + ハッシュ比較を使うこと。既存の「ログ出力は UTF-8 を明示する」ノートと同種の落とし穴 | 本 PR のスコープ外(CLAUDE.md はプロジェクト全体の規範文書)。別途 docs 変更として起票 |
> | S-5 | **`main` の Core テストが Debug 構成で 4 件赤**(本ブランチ無関係・**Task 2 / 3 / 5 の 3 レビューが独立に報告**)。`WordBoundaryTests.MaxScan_NonPositive_NeverRemovesScanLimit`(maxScan: 0 / -1 / -7 / `int.MinValue`)が `WordBoundary.cs:258` の `Debug.Fail`(「maxScan は 1 以上でなければならない(0 以下は未規定=正規化しない)」)で落ちる。**テストは「非正の maxScan では上限を外さない」と主張し、実装は同じ入力を「未規定」として拒否しており、契約が食い違っている**。merge-base `40aa4f5` で再現確認済み | 本 PR のスコープ外。`Debug.Fail` は Release で消えるため **Release では全緑**で、`tools/pre-merge-check.ps1`(Release)も CI も素通りする=これまで表面化しなかった。PR #36(UIA 単語単位・`maxScan == int.MinValue` で上限が消える件)の後始末として**別途トリアージすること** |
> | S-4 | **リポジトリ全体の XML doc 腐り**。本リポジトリは `GenerateDocumentationFile` が無効なため **cref 切れが機械検出されない**。`-p:GenerateDocumentationFile=true -p:TreatWarningsAsErrors=false` の一時ビルドで棚卸ししたところ、`SnapshotSearcher.cs` のクラス doc に `ThresholdChars` / `WindowSize`(実体は `DefaultThresholdChars` / `DefaultWindowSize`)、`TextFileService.cs` に cref 3 件、`SafeLinkExtension.cs:125-129` に **XML そのものが壊れた CS1570 が 6 件**。 | 本 PR は `SnapshotSearcher.cs` の 2 件のみ Task 5 のクラス doc 書き直しで回収し、残りは別テーマ。**手法(`GenerateDocumentationFile=true` の一時ビルドで doc 腐りを棚卸しする)自体が再利用価値を持つ** |

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

> **追記(2026-08-05・Task 7 レビュー)— 確認項目を 3 件足し、検証の焦点を絞る。**
>
> | # | 操作 | 期待 | 追加理由 |
> |---|---|---|---|
> | 9 | 置換モードで「置換して次を検索」を連打 | 毎回「置換しました。**N** 件中 **M** 件目」の N が 1 ずつ減り M が正しく進む | **置換側の項目が 1 つも無かった。** `ReplaceOne` は 1 個の searcher を編集前 `snap` と編集後 `snap2` の両方に使う。searcher の寿命が操作をまたぐようになった今、**最も「古い件数を読む」事故が起きやすい面** |
> | 10 | 「すべて置換」→ 続けて F3 | 「**M** 件置換しました」の後、F3 が正しく読まれる | 同上 |
> | 11 | 検索語を全部消す | ステータスがクリアされるだけで**発声しない**(Say 契約: 空文字は視覚クリアのみ) | 逸脱 D-6 が触った唯一のユーザー可視面 |
>
> **項目 4 の補強**: 「Escape → 再 Ctrl+F」だけでなく**「閉じる(&X)」ボタンでも同じ**を確認する。
> これで Task 6/7 で新設した `Dismissed` 配線の 3 経路のうち 2 つを実機で踏める
> (タイトルバー × は `UserClosing_RaisesDismissed_Once_AndKeepsInstanceAlive` が固定済み)。
>
> **検証の焦点**: 本ブランチは `Announce` / `SetStatus` の呼び出し方も文言も一切変えていない。
> したがって L5 の目的は「発声の仕組み」ではなく「**読み上げられる数値が古くないこと**」に絞ってよい。
> 「NVDA が何と読んだか」ではなく「**読んだ数が画面表示と一致するか**」を見るのが効率的。

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
