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
