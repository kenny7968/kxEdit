# マークダウンプレビューの CSS・相対画像の解決を回復する(A-2 / A-21)設計書

作成日: 2026-08-22 / ブランチ: `feature/preview-base-uri-fix` / 起点 main = `aa0c44e`

対象は [`docs/plans/2026-08-22-v0.2-release-bug-audit.md`](./2026-08-22-v0.2-release-bug-audit.md) の
**A-2**(優先度 1)と **A-21**(優先度 2)。監査書 §8-2 の「A-2 は別ブランチ・A-21 は同ブランチで判断」に従う。

本書は策定時スナップショット(CLAUDE.md §8)。実装時の精密化と実施記録の追記のみ行う。

## 1. 問題

### 1.1 A-2 — CSS と相対パス画像が一切効かない(v0.1.1 MD-M-2 からの退行)[実機]

マークダウンプレビューで、

- 同梱 CSS(blockquote の左ボーダー・本文パディング・フォント指定等)が全く適用されない
- `![local image](pic.png)` が壊れた画像 + alt テキストになる

説明書 §12.1「文書と同じフォルダなど、ローカルにある画像は表示されます」と矛盾する。
晴眼・弱視ユーザーが初日に気づく水準の退行(CLAUDE.md §2「晴眼・弱視ユーザーも第一級」)。

**機構**:

1. プレビュー文書は `CoreWebView2.NavigateToString(html)` で流し込まれる
   (`MarkdownPreviewForm.cs`)。この文書の origin は `data:text/html;charset=utf-16;base64,...` で、
   相対 URL・ルート相対 URL の解決基準を持たない。
2. そのため相対解決は `<base href="https://kxedit.preview/">`(`MarkdownRenderer.cs:124-126`)
   だけが頼りになっている。
3. ところが同じ `<head>` の先行位置にある `<meta http-equiv="Content-Security-Policy">` が
   `base-uri 'none'`(`MarkdownRenderer.cs:46`)を宣言しており、CSP 仕様上 `<base href>` は
   **無効化される**。
4. 結果、`<link href="/_kxedit/styles.css">`(`:141`)も本文中の `pic.png` も解決先を失う。

退行の構造は「MD-M-2 が **CSS の外部化**(`<style>` → `<link>` = base 依存の発生)と
**`base-uri 'none'` の追加**(base の無効化)を同一変更で入れた」こと。両者が食い合っている。

### 1.2 A-21 — Markdig `GenericAttributes` が `on*` 属性を素通しする [DLL]

`UseAdvancedExtensions()` が同梱する `GenericAttributesExtension` により、

- `[y](x){onclick="evil()"}`
- `![a](x){onerror=alert(1)}`
- `[y](x){href="javascript:..."}`(`SafeLinkExtension` が落とした href を後段で復活させる)

が**そのまま属性として出力**される。現状は CSP(`default-src 'none'` = script-src なし)が
実行を止めるため live XSS ではないが、`SafeLinkExtension` という二層目の防御が無効化されている。

A-2 は CSP を触る変更なので、監査書 §8-2 の方針「**CSP を触る前に塞ぐ**」に従い同ブランチで対処する。

## 2. 修正方針

### 2.1 A-2: `base-uri` を preview 仮想ホストに限定する

`MarkdownRenderer.PreviewCspHeader` の

```
base-uri 'none';
```

を

```
base-uri https://kxedit.preview;
```

へ変更する(文字列は `PreviewVirtualHost` 定数から生成し、`img-src` / `media-src` /
`style-src` / `font-src` と同じ書式・同じ single source of truth に揃える)。

これで `<base href="https://kxedit.preview/">` が有効になり、

| リソース | 解決後 URL | 応答する層 |
|---|---|---|
| `/_kxedit/styles.css` | `https://kxedit.preview/_kxedit/styles.css` | `PreviewCspHeaderInjector`(`WebResourceRequested` の virtual response) |
| `pic.png` | `https://kxedit.preview/pic.png` | `SetVirtualHostNameToFolderMapping`(.md のフォルダ) |

CSS は**未保存タブでも復活する**。`_baseDir` が null のとき仮想ホストマッピングは張られないが、
`WebResourceRequested` はマッピングの有無に関わらずネットワーク解決の手前で発火し、
Injector が CSS 実体を返すため。

#### 検討した代替案

| 案 | 内容 | 不採用の理由 |
|---|---|---|
| B | `base-uri 'none'` を維持し、`<link>` と本文中の相対リンク/画像を render 時に絶対 URL へ書き換える | Markdig の `LinkInline` 書き換え拡張を新設する必要があり、リリース直前に実装・テスト面が増える |
| C | `NavigateToString` をやめ、仮想ホスト上の実 URL へ `Navigate` する | 文書 origin が `https://kxedit.preview` になり `<base>` 自体が不要・CSP を HTTP header で一次配布できて設計としては最も素直。ただし文書がユーザーフォルダの `.html` / `.svg` と same-origin になり MD-H-1 の前提を作り直す必要があるため改修範囲が最大 |

### 2.2 A-2 のセキュリティ影響(緩和の妥当性)

`base-uri` は「**その文書内の `<base>` 要素が指してよい URL**」を縛る directive であり、
緩めても攻撃者が指せる先は preview 仮想ホスト = その .md 自身のフォルダに限られる。
加えて本文から `<base>` 要素を注入する経路が存在しない:

- `DisableHtml()` により raw HTML はパーサ段で無効化される(`<base>` はテキストになる)。
- `GenericAttributes` は既存要素への属性付与のみで新規要素を作れない。かつ本ブランチ §2.3 で除去する。

したがって実効的な攻撃面の増加はゼロと判断する。MD-M-2 が `base-uri` に込めた
「`<base>` を攻撃者ホストへ向けられない」という意図は、許可先をホスト 1 つに限定することで保たれる。

### 2.3 A-21: `GenericAttributesExtension` を除去する

`MarkdownRenderer.BuildPipeline()` で `UseAdvancedExtensions()` の後に除去する。

```csharp
var builder = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml();
// A-21: UseAdvancedExtensions が同梱する GenericAttributes は {onclick="..."} 等を
// そのまま属性出力し、SafeLinkExtension の二層目を無効化するため除去する。
builder.Extensions.RemoveAll(e => e is GenericAttributesExtension);
```

型 `Markdig.Extensions.GenericAttributes.GenericAttributesExtension` の存在は
Markdig 1.3.2 の DLL で確認済み。`MarkdownPipelineBuilder.Extensions` は
`OrderedList<IMarkdownExtension>`(`List<T>` 派生)なので `RemoveAll` が使える。

**機能影響**: `{#id}` / `{.class}` 記法が本文にリテラル表示されるようになる。
見出しの id は別拡張 `UseAutoIdentifiers` が生成し続けるためアンカーは維持される。
プレビューの CSS は固定(ユーザーがスタイルシートを差し替える経路がない)ので、
class 指定に実用価値はない。既存テストに generic attributes 依存は 0 件。

## 3. 範囲外(明示)

**相対リンクのクリックは本修正後も動かない。** 監査書 A-2 の表題は「CSS・相対パス画像・相対リンクが
解決されない」だが、`https://kxedit.preview/*` への top-level ナビゲーションは
`PreviewNavigationPolicy.Classify` が **MD-H-1(PR #18)で意図的に Block** している。
攻撃者が .md と同梱した CSP 未適用の `.html` / `.svg` へ in-frame 遷移されると
same-origin でスクリプトが走るためで、これは退行ではなく設計判断である。

`<base>` が回復すると相対リンクの解決先が `https://kxedit.preview/...` になるので、
クリックは「解決されない」から「Block される」へ変わる。ユーザーから見た結果(遷移しない)は同じ。

## 4. テスト

### 4.1 自動テスト(L1: `kxEdit.Core.Tests/Text/MarkdownRendererTests.cs`)

| # | テスト | 意図 |
|---|---|---|
| 1 | 既存 `Meta_Contains_BaseUri_None` を置換し、base-uri directive を regex で切り出して `https://kxedit.preview` のみ・`'none'` と `*` と他ホストが入らないことを機械固定 | 既存 `Meta_ImgSrc_Excludes_Data_Scheme` の M-6 補正パターンに倣う(insertion mutation 耐性) |
| 2 | `PreviewBaseHref` が base-uri の source をオリジンとして持つことを固定 | **今回の「CSP と `<base>` の食い合い」の再発だけは自動で捕まえられる**ため網を張る。片方だけ変えると赤になる |
| 3 | `{onclick=...}` / `{onerror=...}` / `{href="javascript:..."}` が属性として出力されないこと(3 本) | A-21 の回帰防止 |
| 4 | `{#id}` がリテラル表示になること | A-21 の挙動変化を仕様として固定 |
| 5 | 既存の pipe table / task list 等 advanced 拡張のテストが緑のまま | 他拡張を巻き添えにしていない証拠 |

### 4.2 自動テストの限界

**CSP 同士の食い合いそのものは自動テストでは検出できない。** `PreviewCspHeaderInjectorTests` は
WebView2 実機を使わず、meta と HTTP header の文字列一致しか見ない(監査書 A-2 補足)。
上記 #2 は「base-uri の source が base href と一致するか」までしか固定できず、
CSP の他 directive が将来 `<base>` を再び殺す可能性を網羅はしない。

### 4.3 L5(実機 SR 検証)— 本修正の実質ゲート

`docs/plans/2026-08-21-rename-to-kxedit-l5-checklist.md` の **④ 相対パス画像**を実施し、
本ブランチ用に次を追加する:

- CSS が適用されていること(blockquote の左ボーダー・本文パディング・フォントが素の HTML でない)
- **未保存タブ**(仮想ホストマッピングなし)でも CSS が適用されること
- 相対リンクのクリックが遷移しないこと(§3 の仕様確認。クラッシュ・外部ブラウザ起動が起きないこと)

監査書 §5 のとおり L5 は PR #36〜#39 分と合わせてまとめて 1 回実施する想定。
本ブランチ単独でのマージ可否はユーザー判断。

## 5. 工程

CLAUDE.md §3「簡略化の基準」に該当する小変更(src 変更は `MarkdownRenderer.cs` 1 ファイル)。
実装は 1〜2 タスク・単一 commit でよい。

ただし WebView2 / プレビュー = **セキュリティ敏感面**のため、最終ブランチレビューは
**コード品質パスと脆弱性パスを別エージェント 2 本**で回す(1 本に統合しない)。
脆弱性パスの焦点:

- `base-uri` 緩和後に `<base>` 要素を注入する経路が本当に存在しないか
- `GenericAttributes` 除去で他の sanitize 経路(`SafeLinkExtension`・`DisableHtml`)に穴が開かないか

品質ゲートは `tools/pre-merge-check.ps1` EXIT 0(CLAUDE.md §6)。

## 6. 申し送り

- **説明書 148 行の記述**: 「文書と同じフォルダにある画像やファイルへの相対パスのリンクは、
  プレビュー内で表示・参照できます」は、MD-H-1 以降リンク側が成立していない(§3)。
  説明書はユーザー編集版が正(CLAUDE.md §8)なので本ブランチでは改稿せず、ユーザーへ提起のみ行う。
- **案 C(実 URL への Navigate)**: CSP を HTTP header で一次配布できる・`<base>` 依存を消せる、
  という構造上の利点は残る。プレビューを次に大きく触るときの選択肢として記録する。
