# `WordBoundary.maxScan` の契約表明を実装の実態へ合わせる — 設計書

- 日付: 2026-08-08
- 対象: `src/yEdit.Core/Editing/WordBoundary.cs` / `tests/yEdit.Core.Tests/Editing/WordBoundaryTests.cs`
- 出自: PR #37 の申し送り **S-5**(PR #38 のトリアージで「次点」と判定)

## 0. 要旨

`WordBoundary` の public API 4 本は冒頭で `Debug.Assert(maxScan >= 1, ...)` を実行し、
メッセージで「**0 以下は未規定=正規化しない**」と表明している。一方
`WordBoundaryTests.MaxScan_NonPositive_NeverRemovesScanLimit` は非正値 4 種
(`int.MinValue` / `-7` / `-1` / `0`)を渡して「**どの値でも上限が消えないこと**」を固定する。
両者は正面から食い違い、**Debug 構成でこの Theory の 4 ケースが赤になる**。

本設計は**実装側の表明を実態へ合わせる**。`Debug.Assert` を削除し、
`maxScan <= 0` の挙動を「予算を使い切った状態と同じ」という**規定挙動**として文書化する。
ロジックは 1 行も変えない。

## 1. 現状の実測(main / 2026-08-07)

| 構成 | 結果 |
|---|---|
| Debug ビルド(`yEdit.sln`) | **0 warning** |
| Debug `Core.Tests` | **失敗 4 / 合格 1124 / 計 1128** |
| Debug `Editor.Tests` | 366 全緑 |
| Debug `App.Tests` | 467 全緑 |

赤 4 件はすべて `MaxScan_NonPositive_NeverRemovesScanLimit` の 4 InlineData。
テストホストが `Debug.Fail` を `DebugAssertException` に翻訳して落ちている
(`WordBoundary.cs:258` = `PrevWordStart` の assert が最初に当たる)。

**Debug 固有の赤はこの 1 メソッドだけ**であり、他に潜在的な契約不一致は存在しない。

## 2. 不一致の正体 — 表明だけが V-1 修正前のまま取り残されている

PR #36 の最終レビュー 脆弱性パス **V-1** で、`PrevWordStart` は次の形に修正された。

```csharp
int budget = maxScan > 0 ? maxScan - 1 : 0;
```

修正前の `int budget = maxScan; budget--;` は `maxScan == int.MinValue` のとき
unchecked underflow で `int.MaxValue` へ化け、**上限そのものが消えていた**
(実測: `'a'` × 200,000 の `PrevWordStart(snap, 100_000, int.MinValue)` が 0 を返して 964 ms)。
上限の導入自体が DoS 対策なので、この形は残せないという判断だった。

つまり **V-1 は「非正値を明示的に正規化する」ことを選んだ**。他 3 本(`NextWordStart` /
`WordStart` / `WordEnd`)も `while (budget > 0)` および `PrevWordStart` への委譲によって
非正値で自然に縮退する。**実装は 4 本とも非正値を安全に扱っている。**

にもかかわらず `Debug.Assert` のメッセージは「0 以下は未規定=正規化しない」のまま残った。
**食い違っているのは表明の側**であり、テストは実装の実態を正しく固定している。

## 3. 検討した 3 案と選択

| 案 | 内容 | 判定 |
|---|---|---|
| **A** | **実装側 — `Debug.Assert` を削除し、非正値の縮退を規定挙動として文書化** | **採用** |
| B | テスト側 — `Debug.Assert` は残し、テスト内で `Trace.Listeners` を退避 → `Clear` → 復元して発火を抑止 | 却下 |
| C | テスト側 — 契約違反ケースを `#if DEBUG` で Skip | 却下 |

**A を採る理由**: §2 のとおり実装は既に非正値を規定どおりに扱っており、
表明の文言だけが取り残されている。テストは無改修で緑になり、DoS 網はそのまま残る。

**B の却下理由**: `Trace.Listeners` はプロセス全域の静的状態で、xunit はテストクラスを
並列実行する。抑止中に**別テストの assert を握りつぶす偽緑リスク**があり、
コレクション直列化まで持ち込む必要が出る。得られるものは
「本番経路に非正値を渡す呼び出しが増えたら Debug で気づける」だけで、
本番呼び出し 6 箇所はすべて定数 `WordBoundary.DefaultMaxScan` を渡している(§5)。

**C の却下理由**: Debug 構成で網が消える。ゲートを Debug へ広げても意味が半減する。

## 4. 規定挙動の正確な定義

**「1 歩も走らない」は不正確である。** `PrevWordStart` の手順 2(最初の 1 code point 左への
移動)は予算の外で無条件に実行されるため、非正値でも 1 歩だけ動く。

| API | `maxScan <= 0` の返り値 |
|---|---|
| `NextWordStart(caret)` | `caret`(0 歩) |
| `WordEnd(pos)` | `pos`(0 歩) |
| `PrevWordStart(caret)` | `caret` の **1 code point 左**(手順 2 の 1 歩は予算外) |
| `WordStart(pos)` | `pos`(`PrevWordStart(pos + 1)` の 1 歩が `pos` へ戻って終わる) |

統一的な言い方は「**予算を使い切った状態と同じ**」。既存テストの 4 assert が
この表そのものを固定している(`WordBoundaryTests.cs:318-323`)。

xmldoc にはこの表現で書く。「1 歩も走らない」と書くと `PrevWordStart` で嘘になる。

## 5. 「非正値も安全」は defense in depth であって推奨ではない

本番呼び出しは 6 箇所すべてが定数 `WordBoundary.DefaultMaxScan`(= 128)を渡している。

- `src/yEdit.Editor/InputRouter.cs:163, 177, 529, 530`(Ctrl+←→ / ダブルクリック)
- `src/yEdit.Editor/UiaTextHostAdapter.cs:510, 523, 536, 549`(SR 読み上げスパン)

**非正値が本番で入る経路は現状ゼロ。** 非正値の安全性が要るのは、`WordStart` の xmldoc が
棄却案として記録している `PrevWordStart(snap, pos + 1, maxScan + 1)` のような
**内部の算術が将来オーバーフローを生む**場合に備えるためである
(`NoScanLimit == int.MaxValue` に +1 して `int.MinValue`)。

したがって xmldoc は次の 2 つを**両方**書く。

1. 推奨は `maxScan >= 1`。新しい呼び出しで非正値を渡してはならない。
2. それでも非正値は未定義動作ではなく §4 の規定へ縮退する(DoS 対策の多重防御)。

## 6. 変更内容

### `src/yEdit.Core/Editing/WordBoundary.cs`

- 4 本の `Debug.Assert(maxScan >= 1, MaxScanContract, nameof(maxScan));` を削除
  (`NextWordStart` / `PrevWordStart` / `WordStart` / `WordEnd`)
- `private const string MaxScanContract` を削除(参照元がこの 4 行だけ)
- `using System.Diagnostics;` を削除(同上)
- クラス `<remarks>`(58 行目)の「契約は `maxScan >= 1`(各 API 冒頭の `Debug.Assert` で検証)」
  を §4 / §5 の内容へ差し替え
- 4 本の `<param name="maxScan">` の「契約 `>= 1`」を「推奨 `>= 1`」+ 規定挙動の参照へ差し替え
- `PrevWordStart` 内の V-1 コメント(261-266 行目)は**残す**。非正値クランプの根拠そのもの

**ロジックは 1 行も変更しない。**

### `tests/yEdit.Core.Tests/Editing/WordBoundaryTests.cs`

- `MaxScan_NonPositive_NeverRemovesScanLimit` の `<remarks>` 末尾 2 行
  (「`maxScan <= 0` は `Debug.Assert` の契約違反でもあるが、ビルド / CI / ローカルゲートは
  すべて Release 構成なので発火しない」)を実態へ書き換える。
  この記述は **Debug 構成で 4 件赤だった事実そのものを見落としている**ため、
  そのまま残すと同じ勘違いを再生産する。
- **assert・InlineData・fixture は一切変更しない。**

## 7. 再発防止 — assert を戻させない

ゲートを Release 一本に据え置く(§9)以上、`Debug.Assert` が戻されても
ゲートでも CI でも検出できない。削除跡にコメントを 1 つ残す。

> `Debug.Assert(maxScan >= 1)` をここへ戻さないこと。
> `WordBoundaryTests.MaxScan_NonPositive_NeverRemovesScanLimit` が **Debug 構成でだけ**赤くなり、
> ゲート / CI は Release 一本なので検出できない(2026-08-08 / 申し送り S-5)。

## 8. 挙動不変の根拠

`Debug.Assert` は `[Conditional("DEBUG")]` である。**Release ビルドでは呼び出し自体が
コンパイラによって生成されていない**ため、Release の実行時挙動は 1 命令も変わらない。
`MaxScanContract` は private const で、その 4 行からしか参照されない。

帰結として:

- **L5 実機 SR 検証は不要**。SR 経路(`UiaTextHostAdapter` → `WordBoundary`)の Release 挙動が
  変わらないため。CLAUDE.md §5 の「SR 経路不変の挙動不変リファクタは省略可」に該当する。
- Debug 構成では挙動が変わる(契約違反時に落ちなくなる)。これが本修正の目的そのものである。

## 9. 受容した判断 — ゲートは Release 一本のまま

S-5 は「`Debug.Assert` は Release で消えるためゲートも CI も素通り=**ゲートが Release 一本で
あることの盲点**」も指摘していた。ゲートを Debug へ広げる案は**ユーザー判断で見送る**。

理由: 本修正後に Debug でしか動かない機構は `TextSnapshot.DecodeUtf16At` の
`Debug.Assert` 4 本(`TextSnapshot.cs:130, 133, 136, 141`)だけになる。これらは
「UTF-8 バッファが壊れていない」という内部不変条件で、破れていれば Release 側のテストも
内容ズレで赤になる可能性が高い。ゲート時間 +1 分弱を恒久に背負う価値は薄いと判断した。

## 10. 検証

| 項目 | 内容 |
|---|---|
| Debug | `Core` / `Editor` / `App` の 3 プロジェクトが全緑(現状 Core 4 赤 → 0) |
| Release | `tools/pre-merge-check.ps1` が **EXIT 0** |
| レビュー | 別エージェントによるレビュー 1 回(CLAUDE.md §3 簡略化基準によりコード品質 / 脆弱性の 2 パスを統合) |
| L5 | **不要**(§8) |

## 11. 申し送り

- **F-1**: ゲートを Debug 構成へ広げる案は §9 のとおり見送った。Debug 専用機構
  (現状 `TextSnapshot` の 4 assert)が今後増えるときに再検討する。
- 残る PR #37 申し送りは **S-4**(XML doc 腐り)/ **S-6**(CLAUDE.md 環境ノート追記候補)/
  **S-1〜S-3**、および PR #38 の **T-3**(`ReplaceOne` が「選択範囲のみ」を参照しない)。

---

## 12. 2026-09-01 追記 — B1 着手時の精密化

本節より上は **2026-08-08 の策定時スナップショット**である(CLAUDE.md §8)。本節は着手時に
判明した差分の記録であり、上の本文は書き換えていない。**上の §9 と本節は矛盾する。本節が正。**

### 12.1 §9 の判断(ゲートは Release 一本のまま)は撤回された

2026-08-31 の傘設計書 `2026-08-31-v0.2-remaining-work-design.md` §4.1 が、B1 を 6 ブランチの
**先頭**に置く理由としてゲートの Debug 拡張そのものを挙げ、§9 の申し送りで
「`tools/pre-merge-check.ps1` に Core.Tests の Debug ステップを足す作業が 1 行も無い。
B1 を先頭に置いた理由がそこなので、必ず足すこと」と明記した。**ユーザー承認済み**(PR #59)。

判断が変わった根拠は本設計書 §9 の当時の想定が外れたことにある。§9 は
「本修正後に Debug でしか動かない機構は `TextSnapshot` の 4 assert だけ」と書いたが、
その後 Issue #48 の最終レビュー Q-I-4 が `DocumentState.Path` の `Debug.Assert` を
**実際に無効な網として実証**し(`IsPathFullyQualified` → `IsPathRooted` の変異が Release 全緑で
生存・Debug で赤)、`App.Tests` の Debug ステップが両ゲートへ入った(`7baa7f0`)。
以後は「Debug の網が存在しうる」が既定の前提になった。

さらに本ブランチ B1 は 6 本の先頭であり、B2〜B6 が Core / Editor に足す網が
**最初からゲートに乗るかどうか**を決める位置にある。

### 12.2 §7(再発防止コメント)の文言は成立しない

§7 が置こうとしたコメントは「ゲート / CI は Release 一本なので検出できない」と書いている。
§12.1 のとおり本ブランチで Core.Tests の Debug をゲートへ足すため、**この文は commit した
瞬間に偽になる**。同じ位置に、ゲートが検出**する**ことを述べるコメントへ差し替える。

### 12.3 Editor.Tests の Debug も足す(2026-09-01 ユーザー判断)

傘設計書 §9 が「B1 で扱うか次回に送るかを判断すること」とした論点。**3 本すべて揃える**を選択。

実測(2026-09-01 / main `67636bc`):

| 事実 | 値 |
|---|---|
| `src/kxEdit.Editor/` の `Debug.Assert` | **0 件**(Editor.Tests の Debug が有効化するのは Core の `TextSnapshot` 4 assert のみ) |
| Editor.Tests Debug | 失敗 0 / 合格 491(所要 約 19 秒・増分ビルド込み) |
| Core.Tests Debug | 失敗 4 / 合格 1336 / 計 1340(§1 の再確認。当時 1128 → 現在 1340) |

Editor 自身の網が今 0 件でも揃える理由: Q-I-4 が踏んだ失敗モードは
「assert を足したのにゲートステップを足し忘れる」であり、**プロジェクト単位で歯抜けにしておく
限り同じ穴が再発する**。3 本揃えれば「どの層に assert を入れてもゲートに乗る」が構造で保証される。

### 12.4 適用先は 2 箇所(release.yml には足さない)

先例 `7baa7f0` は Debug ステップを `tools/pre-merge-check.ps1` と `.github/workflows/ci.yml` の
**2 箇所だけ**に足し、`.github/workflows/release.yml` には足していない。B1 もこれに倣う。
(3 ファイルの同期コメントが求めているのは「テストプロジェクトの追加/削除」であって
構成の追加ではない。)

### 12.5 参照パスの読み替え

本設計書 §1 以降が挙げる `yEdit.Core` / `yEdit.sln` / `tests/yEdit.Core.Tests` は、PR #39
(全面改名)により現在それぞれ `kxEdit.Core` / `kxEdit.sln` / `tests/kxEdit.Core.Tests` である。
行番号も 2026-08-08 当時のもので現在とずれる。**実装計画
(`2026-09-01-wordboundary-maxscan-contract.md`)の記載を正とすること。**

### 12.6 §1 の実測値の再確認

Debug の赤 4 件が `MaxScan_NonPositive_NeverRemovesScanLimit` の 4 InlineData だけである点は
2026-09-01 に再実行して一致した(§12.3 の表)。**§2〜§8 の分析は有効。**

### 12.7 §4 の表は 1 行が偽だった — 仕様レビュー Important-1(2026-09-01)

Task 2 の仕様レビュー(別エージェント)が実測で掘り当てた。**§4 の表の
`WordStart(pos)` 行は誤りである。**

`WordStart` は 2 経路ある。

```csharp
if (pos >= snap.CharLength)
    return PrevWordStart(snap, pos, maxScan);   // ← pos + 1 を渡さない
return PrevWordStart(snap, pos + 1, maxScan);
```

§4 の表が書いた「`pos`(`PrevWordStart(pos + 1)` の 1 歩が `pos` へ戻って終わる)」が
成り立つのは**下段の経路だけ**。EOF 経路(`pos >= CharLength`)は `pos` をそのまま渡すため、
`maxScan <= 0` では `PrevWordStart` の縮退がそのまま出て **`pos` の 1 code point 左**になる。

実測(レビュー担当と実装担当が独立に再現。`maxScan ∈ {int.MinValue, -7, -1, 0}` で同値):

| 呼び出し | 実測 | §4 の表の主張 |
|---|---|---|
| `WordStart('a'×5000, 5000)` — EOF 経路 | **4999** | 5000 |
| `WordStart("ab😀", 4)` — EOF 経路・サロゲート | **2** | 4 |
| `WordStart('a'×5000, 4000)` — `pos + 1` 経路 | 4000 | 4000(一致) |

**「予算を使い切った状態と同じ」という §4 の上位原則そのものは正しい**
(`maxScan = 1` でも `WordStart(5000)` は 4999 を返す)。偽なのは表の具体値の側だけである。

#### 併せて判明した過大主張

§4 の「**既存テストの 4 assert がこの表そのものを固定している**
(`WordBoundaryTests.cs:318-323`)」も**過大主張**だった。当該テストは `pos=4000` /
`CharLength=5000` で `pos + 1` 経路しか通しておらず、**EOF 経路は 1 度も評価されていない**。
「網がある」と書いた側が実際には無網だった形で、これは
`net-absence-claims-are-also-verifiable` の裏返し(「網がある」も検証対象)にあたる。

#### 対応

| 面 | 対応 |
|---|---|
| xmldoc(正本) | `f184bf7` で EOF 経路込みへ訂正。窓の表が `maxScan >= 1` 前提であることも明示 |
| §4 本文 | **書き換えない**(CLAUDE.md §8 のスナップショット原則)。訂正は本節が持つ |
| 網 | **Task 3 で EOF 経路の assert を足す**(表を訂正しただけでは同じ穴が残る) |

この表は `WordBoundary` の 4 本の `<param>` から参照される**唯一の正本**に位置づけられており
(ファイル自身が「よくある誤読はこの節が正本」と宣言している)、誤りが参照経由で 4 箇所へ
波及していた。**「表明と実装の食い違いを潰す」ことが目的の commit に、同種の欠陥を
新しく持ち込みかけた**ことになる。設計書に書かれた表を実装で検算せずに写した結果であり、
`plan-code-is-not-ground-truth`(計画のコードは正解ではない)の再発である。

#### 文書端ガードは表に書かない — 理由を訂正(再レビュー Minor-2)

結論(表に書き足さない)は変わらないが、**最初に書いた理由は事実として偽だった**ので差し替える。

- 誤: 「文書端では『1 code point 左』にならず表と食い違うが、`WordStart` 行にだけ但し書きを
  足すと**表内で非対称になる**ので両方に足さない側を選ぶ」
- 正: **そもそもどちらの行も文書端で偽にならないので、但し書きが要らない。**
  `WordStart` 行の主張は「`pos`」であって「1 code point 左」ではなく、`WordStart(pos=0)` が
  返す `0` は `pos` そのもの=一致する。`PrevWordStart` 行の「`caret` の 1 code point 左」も、
  このファイルが歩進の定義に使う `TextBoundary.PrevCodePoint` が `pos <= 0` で 0 へクランプする
  (`TextBoundary.cs:170-171`)ため、`caret = 0` の戻り値 0 は「クランプ後の 1 code point 左」
  として読めて偽ではない。

根拠が偽のまま残ると、将来「非対称だから書けない」を理由に別の但し書きが却下される
二次被害が起きる。`rationale-not-just-conclusion`(結論が正しくても根拠が偽なら直す)に該当する。

### 12.8 窓の表も EOF 経路で偽だった — 再レビュー Important-2(2026-09-01)

§12.7 は**縮退表**(`maxScan <= 0`)を直したが、その上にある**窓の表**(`maxScan >= 1`)にも
同じ穴が残っていた。しかもこちらは **`DefaultMaxScan = 128` = 全本番呼び出しが渡す値**で起きる。

| 呼び出し | 実測 | 窓の表の主張 |
|---|---|---|
| `WordStart('a'×5000, 4000, 128)` = 3873 | 窓幅 **127** | `[pos - (maxScan - 1), pos]` = 127(一致) |
| `WordStart('a'×5000, 5000, 128)` = 4872 | 窓幅 **128** | 127(**不一致**) |

EOF 経路は `PrevWordStart(pos)` を呼ぶため予算が `maxScan - 1 = 127`、これに手順 2 の
予算外 1 歩が乗って合計 128 になる。「`pos + 1` を渡すぶん 1 狭い」という非対称が
**EOF 経路では発生しない**。

決定的なのは、`f184bf7` が足した縮退表の注記が「EOF 経路だけは `pos + 1` を渡さない」と
明言しており、**その 20 行後の「1 狭いのは `pos + 1` を渡すため」を自分の論理で否定している**
ことである。同一ファイルが同じ事実について正反対を述べる状態になっていた。しかも当該行は
自ら「cap の較正時にこの 1 のズレが効く」と宣言しており、較正のために読む者を直接誤らせる。

**対応: ① fixup。** 窓の表の下の説明へ EOF 経路の但し書きを足す。

**この指摘は再レビューでの格上げである。** 初回レビューでは Minor-1 の後半として
「修正必須ではない」と判定されていた。`f184bf7` 以前は「表が不完全」なだけだったが、
`f184bf7` 後は「明言 → 20 行後に否定」になり性質が変わったため。**修正そのものが
新しい矛盾を作ることがある**という実例として記録する。

### 12.9 申し送り — サロゲート中間の前提記述が実測と食い違う(本ブランチ対象外)

再レビューが発見。クラス `<remarks>` の「前提違反時(caret が `[0, CharLength]` を外れる /
**サロゲート中間**)は `ArgumentOutOfRangeException` が透過する」は、**両方の前提違反について
4 API 中 2 本でしか成り立たない**。

実測(`"ab😀cd"` / `CharLength = 6`。`maxScan` 5 値で同結果):

| API | 範囲外 `pos = 7` | サロゲート中間 `pos = 3` |
|---|---|---|
| `WordStart` | **例外** | 2(例外なし) |
| `PrevWordStart` | **例外** | 2(例外なし) |
| `WordEnd` | **6(例外なし)** | 4(例外なし・`maxScan >= 1` のとき。0 以下では 3) |
| `NextWordStart` | **6(例外なし)** | 4(同上) |

`WordEnd` / `NextWordStart` は冒頭の `if (pos >= snap.CharLength) return snap.CharLength;` で
受け止めるため投げない。この非対称そのものは `WordStart` の `<param>` に
「**`WordEnd` とは非対称**: あちらは `pos >= CharLength` を CharLength で受け止めるので投げない」
として**既に正しく記録されている**。食い違っているのはクラス `<remarks>` の前提記述の側である。

**当初この節は「範囲外は主張どおり例外」と書いていたが、それも半分偽だった**(再レビュー
Minor-3 で訂正)。将来の再監査者がその一文を信じると、サロゲート中間だけ直して範囲外側の
半分を温存する。同じ `<remarks>` 内の 1 文なので**一度に直すこと**。

**本ブランチでは直さない。** 当該 2 行は main から 1 文字も変わっておらず、`maxScan` 契約とも
無関係で、B1 のスコープ(表明と実装の食い違いの解消)は `maxScan` に限っている。
**次リリースの再監査で回収すること。**

### 12.10 ゲート強化の効果 — 実測(2026-09-01)

**この節は「Debug をゲートに足したから今日から新しく捕まる」と書かないために測った。**
確かめずに書けば嘘の安全宣言になる。

#### 測定の前に確定させた事実

| 項目 | 値 |
|---|---|
| `src/kxEdit.Core/` の実 `Debug.Assert` 呼び出しサイト | **4**(すべて `Buffer/TextSnapshot.cs:130 / 133 / 136 / 141`) |
| `src/kxEdit.Editor/` | **0** |
| `src/kxEdit.App/` | 1(`DocumentState.cs:43`) |

**Task 4 の担当が報告した「Core に 6 件」は誤り**である。`WordBoundary.cs` の**コメント文中で
`Debug.Assert` に言及している 2 行**を数えていた。実サイトは 4 で、B1 が 4 本削除した結果
`TextSnapshot` のぶんだけが残る(§12.3 の記録どおり)。そこから導かれた
「Core の Debug 追加は実際に網が増えている」という結論も**未検証**だったので、下で測った。

`TextSnapshot.cs` の `<remarks>` 自身が「前提そのものは
`TextSnapshotGetCharEquivalenceTests.AssertEveryPieceIsWholeCodePoints` が全ピースについて
固定している」と書いている。つまり **Release 側に既に網がある**と自認している。

#### 変異と結果

`src/kxEdit.Core/Buffer/TextChunk.cs` の `SplitStats` 内、歩幅テーブルの
`: b < 0xF0 ? 3` を `: b < 0xF0 ? 2` へ **1 箇所だけ**変更(3 バイト文字の分割位置が
コードポイント途中に落ちる)。`CharToByte` 側の同型行(`:118`)は触っていない。

| 構成 | 結果 |
|---|---|
| Release | **失敗 13 / 合格 1327 / 計 1340** |
| Debug | **失敗 13 / 合格 1327 / 計 1340** |

**落ちたテストの集合は両構成で同一。** 出力トークンの差分に
`AssertAllPositionsMatch` / `AssertEveryPieceIsWholeCodePoints` / `SnapOffSurrogateMiddle` の
出入りが見えるが、**3 つとも `private static` のヘルパーメソッド**であり、スタックトレースの
違いにすぎない(テストではない)。変異は測定後に `git checkout` で戻し、
`git status` が空であることを確認済み。

#### 結論 — 今日時点で Core の Debug ステップに固有の捕捉力は無い

策定時に用意した 3 通りの解釈のうち **「Release 赤 / Debug 赤」** に該当した。
したがって次のように書き分ける。

- **書いてよい**: B1 の価値は **B2〜B6 が Core に足す網が最初からゲートに乗ること**と、
  プロジェクト単位の歯抜けを解消して「assert を足したのにゲートステップを足し忘れる」
  (Q-I-4 が実際に踏んだ失敗モード)を構造で塞いだこと。**これは今日の捕捉力の話ではない。**
- **書いてはいけない**: 「Debug をゲートに足したので今日から新しく捕まるようになった」。
  この変異では捕まる件数が 1 件も増えていない。

#### 副産物 — 診断精度は上がる

捕捉力は同じでも、**Debug だけが破れた不変条件を名指しする**。

```
Debug のみ: DebugAssertException : Method Debug.Fail failed with
  'UTF-8 先頭バイトでない=ピース範囲がコードポイントの途中から始まっている
   (GetChar は範囲内を読んだまま静かに誤った char を返す)。byteOffset'
```

Release 側は等価性 assert が値の不一致として落ちるだけで、**どの不変条件が破れたかは言わない**。
`TextSnapshot` の 4 assert が「多重防御」として機能しているのはこの面である。
**ただしこれは「捕捉力が増えた」の言い換えではない。混同しないこと。**
