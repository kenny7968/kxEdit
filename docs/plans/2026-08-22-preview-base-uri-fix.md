# プレビューの CSS・相対画像解決 回復(A-2 / A-21)実装計画

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

> **⚠️ 実装時の設計変更(2026-08-22)**: 下の Goal / Architecture と Task 1〜6 は**案 A の
> 策定時スナップショット**。最終ブランチレビューで案 A がページ内アンカーを全滅させることが
> 判明し、**Task 8 で案 B(`<base>` を出力せず AST 段で絶対化)へ切り替えた**。
> **現在の正は設計書 §7 と本書の「追加タスク」節(Task 7 / Task 8)**。上から順に読む場合は
> 先にそちらを参照すること。

**Goal:** マークダウンプレビューで CSS と相対パス画像が解決されるよう CSP の `base-uri` を preview 仮想ホストに限定し、あわせて Markdig `GenericAttributes` による `on*` 属性素通し(A-21)を塞ぐ。

**Architecture:** 変更は `src/kxEdit.Core/Text/MarkdownRenderer.cs` 1 ファイルに閉じる。`BuildPipeline()` から `GenericAttributesExtension` を除去(A-21)し、`PreviewCspHeader` 定数の `base-uri 'none'` を `base-uri https://kxedit.preview` へ変更(A-2)する。App 層(`MarkdownPreviewForm` / `PreviewCspHeaderInjector` / `PreviewNavigationPolicy`)は一切触らない。

**Tech Stack:** .NET / C# / Markdig 1.3.2 / xUnit / WebView2(実機検証のみ)

**設計書:** [`2026-08-22-preview-base-uri-fix-design.md`](./2026-08-22-preview-base-uri-fix-design.md)
**出典:** [`2026-08-22-v0.2-release-bug-audit.md`](./2026-08-22-v0.2-release-bug-audit.md) A-2 / A-21
**ブランチ:** `feature/preview-base-uri-fix`(起点 main = `aa0c44e`、設計書 commit = `0f1c2b5`)

---

## タスク順序の理由

監査書 §8-2 の「**CSP を触る前に塞ぐ**」に従い、A-21(Task 1)を先に、A-2(Task 2)を後に実施する。
逆順にすると、CSP を緩めた状態で `GenericAttributes` が生きている瞬間がブランチ履歴に残る。

---

## Task 1: A-21 — Markdig `GenericAttributes` を除去する

**Files:**
- Modify: `src/kxEdit.Core/Text/MarkdownRenderer.cs:1`(using 追加)、`:76-84`(`BuildPipeline`)
- Test: `tests/kxEdit.Core.Tests/Text/MarkdownRendererTests.cs`(末尾にテスト 4 本を追加)

### 背景(実装者向け)

`UseAdvancedExtensions()` は Markdig の advanced 拡張を一括で有効化するが、その中に
`GenericAttributesExtension` が含まれる。これは `[y](x){onclick="evil()"}` という記法を
`<a href="x" onclick="evil()">y</a>` へ変換する。結果:

- `on*` 属性が HTML に出力される
- `{href="javascript:..."}` で `SafeLinkExtension` が落とした href を復活させられる

現状はプレビューの CSP に `script-src` が無い(`default-src 'none'`)ため実行はされないが、
`SafeLinkExtension` という**二層目の防御が無効化されている**状態。Task 2 で CSP を触るので、
その前に塞ぐ。

除去しても他の advanced 拡張(pipe table・task list・autolink・auto identifier 等)は残る。
見出しの id は別拡張 `UseAutoIdentifiers` が生成し続けるのでアンカーは維持される。

> **実装時の訂正(Task 1 仕様レビュー)**: 2 点、計画側の誤りが判明した。
> 1. 「アンカーは維持される」は誤り。id の**値が変わる**(`id="custom"` → `id="title-custom"`)ため
>    既存 .md の `[link](#custom)` は切れる。
> 2. Step 1 のテスト 3(`[y](x){href="javascript:..."}`)は**本物のバイパス経路を通らない**。
>    この入力では SafeLinkExtension は何も落としておらず、GenericAttributes が 2 つ目の href を
>    追記するだけで HTML の先勝ち規則により実挙動は守られていた。drop された href が復活する
>    のは `[y](javascript:alert(1)){href="javascript:alert(1)"}` の形のみ。5 本目として追加した。

**Step 1: 失敗するテストを書く**

`tests/kxEdit.Core.Tests/Text/MarkdownRendererTests.cs` の**クラス末尾**(最後の `}` の直前)に追加:

```csharp

    // ---------------------------------------------------------------------
    // A-21 (v0.2 リリース前バグ監査): UseAdvancedExtensions 同梱の GenericAttributes を
    // 除去し、`{...}` 属性記法が HTML 属性として出力されないことを機械固定する。
    // 現状は CSP (script-src なし) が実行を止めているだけで、SafeLinkExtension の
    // 二層目 (scheme whitelist) は `{href="javascript:..."}` で上書きできてしまっていた。
    // ---------------------------------------------------------------------

    [Fact]
    public void Render_GenericAttributes_DoesNotEmit_OnClickOnLink()
    {
        string html = MarkdownRenderer.Render("[y](x){onclick=\"evil()\"}", Base);
        Assert.DoesNotMatch(@"<a[^>]*onclick", html);
    }

    [Fact]
    public void Render_GenericAttributes_DoesNotEmit_OnErrorOnImage()
    {
        string html = MarkdownRenderer.Render("![a](x){onerror=alert(1)}", Base);
        Assert.DoesNotMatch(@"<img[^>]*onerror", html);
    }

    [Fact]
    public void Render_GenericAttributes_CannotRestoreDroppedHref()
    {
        // SafeLinkExtension が落とした href を {href="javascript:..."} で上書きできないこと。
        string html = MarkdownRenderer.Render("[y](x){href=\"javascript:alert(1)\"}", Base);
        Assert.DoesNotMatch(@"<a[^>]*javascript:", html);
    }

    [Fact]
    public void Render_GenericAttributes_Syntax_BecomesLiteralText()
    {
        // 拡張除去に伴う挙動変化を仕様として固定する ({#id} は本文にそのまま出る)。
        // 見出しの id は UseAutoIdentifiers が別途生成するのでアンカーは失われない。
        string html = MarkdownRenderer.Render("# 見出し {#custom}", Base);
        Assert.Contains("{#custom}", html);
    }
```

**Step 2: テストを実行して赤を確認する**

```
dotnet test tests/kxEdit.Core.Tests/kxEdit.Core.Tests.csproj --filter "FullyQualifiedName~MarkdownRendererTests.Render_GenericAttributes"
```

期待: **4 本すべて FAIL**。

⚠️ **1 本でも緑で通ったら止まって調査すること。** 「変更前から通るテスト」は網として無価値で、
実装後に緑になっても何も証明しない(CLAUDE.md §4 のミューテーション検証と同じ理由)。
Markdig の実際の出力を確認してから assertion を組み直す。

**Step 3: 最小の実装を書く**

`src/kxEdit.Core/Text/MarkdownRenderer.cs` の 1 行目を:

```csharp
using Markdig;
```

から:

```csharp
using Markdig;
using Markdig.Extensions.GenericAttributes;
```

へ変更する。次に `BuildPipeline()`(`:76-84` 付近)を次で置き換える:

```csharp
    private static MarkdownPipeline BuildPipeline()
    {
        // CSP との二重防御: raw HTML (script/iframe/on* 等) をパーサ段で無効化。
        var builder = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml();
        // A-21 (2026-08-22): UseAdvancedExtensions が同梱する GenericAttributes は
        // `[y](x){onclick="evil()"}` を HTML 属性としてそのまま出力し、SafeLinkExtension が
        // 落とした href すら `{href="javascript:..."}` で復活させられる。CSP (script-src なし)
        // が実行を止めているだけの状態なので、二層目の防御を回復するため拡張ごと外す。
        // 代償: `{#id}` / `{.class}` 記法は本文にリテラル表示される (見出し id は
        // UseAutoIdentifiers が引き続き生成するのでアンカーは維持される)。
        builder.Extensions.RemoveAll(e => e is GenericAttributesExtension);
        // MD-M-3: リンク URL scheme whitelist (二層目の防御)。CSP を弱めた瞬間の
        // live XSS を防ぐため javascript:/vbscript:/data:/file: 等は href を drop する。
        builder.Extensions.AddIfNotAlready<SafeLinkExtension>();
        return builder.Build();
    }
```

**Step 4: テストを実行して緑を確認する**

```
dotnet test tests/kxEdit.Core.Tests/kxEdit.Core.Tests.csproj --filter "FullyQualifiedName~MarkdownRendererTests"
```

期待: `MarkdownRendererTests` の全テストが PASS(新規 4 本 + 既存すべて)。

既存の pipe table / task list / fenced code のテストが緑のままであることが、
**他の advanced 拡張を巻き添えにしていない証拠**になる。ここで既存テストが落ちたら
`RemoveAll` の述語が広すぎる。

**Step 5: コミット**

```bash
git add src/kxEdit.Core/Text/MarkdownRenderer.cs tests/kxEdit.Core.Tests/Text/MarkdownRendererTests.cs
git commit -m "fix(core): Markdig GenericAttributes を除去して on* 属性の素通しを塞ぐ(A-21)"
```

---

## Task 2: A-2 — `base-uri` を preview 仮想ホストに限定する

**Files:**
- Modify: `src/kxEdit.Core/Text/MarkdownRenderer.cs:30-40`(XML doc)、`:46`(CSP 定数)
- Test: `tests/kxEdit.Core.Tests/Text/MarkdownRendererTests.cs:334-335`(既存テスト置換)、`:395`(assertion 更新)

### 背景(実装者向け)

プレビュー文書は `CoreWebView2.NavigateToString(html)` で流し込まれ、origin は
`data:text/html;...` になる。相対 URL の解決基準を持たないので、
`<base href="https://kxedit.preview/">` が唯一の頼り。

ところが同じ `<head>` の先行位置にある meta CSP が `base-uri 'none'` を宣言しており、
**CSP 仕様上 `<base href>` は無効化される**。その結果:

- `<link href="/_kxedit/styles.css">` が解決されない → CSS が一切効かない
- 本文中の `pic.png` が解決されない → 画像が壊れる

`base-uri` は「その文書内の `<base>` 要素が指してよい URL」を縛る directive なので、
preview 仮想ホスト 1 つに限定すれば MD-M-2 の意図(攻撃者ホストへ `<base>` を向けさせない)は
保たれる。かつ `DisableHtml()` と Task 1 の `GenericAttributes` 除去により、本文から
`<base>` 要素を注入する経路は存在しない。

**Step 1: 失敗するテストを書く**

まずテストファイル冒頭の using に追加する:

```csharp
using System.Text.RegularExpressions;
using kxEdit.Core.Text;
```

次に `tests/kxEdit.Core.Tests/Text/MarkdownRendererTests.cs:334-335` の既存テスト:

```csharp
    [Fact]
    public void Meta_Contains_BaseUri_None() =>
        Assert.Contains("base-uri 'none'", MarkdownRenderer.Render("x", Base));
```

を、次の 2 本で置き換える:

```csharp
    [Fact]
    public void Meta_BaseUri_Is_Limited_To_PreviewHost()
    {
        // A-2 (2026-08-22): base-uri 'none' は仕様上 <base href> を無効化するため使えない。
        // directive 全体を切り出し、source が preview 仮想ホスト 1 つだけであることを
        // 機械固定する (Meta_ImgSrc_Excludes_Data_Scheme と同じ insertion mutation 耐性)。
        string html = MarkdownRenderer.Render("x", Base);
        var m = Regex.Match(html, @"base-uri\s+([^;]*);");
        Assert.True(m.Success, "base-uri directive が見つからない");
        Assert.Equal("https://kxedit.preview", m.Groups[1].Value.Trim());
    }

    [Fact]
    public void Meta_BaseUri_Matches_PreviewBaseHref()
    {
        // A-2 の再発防止: CSP の base-uri と <base href> が食い違うと <base> が無効化され、
        // CSS も相対画像も解決できなくなる。この「CSP 同士 / CSP と base の食い合い」のうち、
        // 自動テストで捕まえられるのはこの対応関係だけなので網を張る。
        // (ブラウザ実挙動そのものは L5 でしか検証できない。)
        string html = MarkdownRenderer.Render("x", Base);
        var m = Regex.Match(html, @"base-uri\s+([^;]*);");
        Assert.True(m.Success, "base-uri directive が見つからない");
        string source = m.Groups[1].Value.Trim();
        string normalized = source.EndsWith('/') ? source : source + "/";
        Assert.Equal(MarkdownRenderer.PreviewBaseHref, normalized);
        Assert.Contains($"<base href=\"{MarkdownRenderer.PreviewBaseHref}\">", html);
    }
```

さらに `PreviewCspHeader_ContainsAllDirectives`(`:395` 付近)の 1 行:

```csharp
        Assert.Contains("base-uri 'none'", csp);
```

を:

```csharp
        // A-2: base-uri だけは 'none' ではなく preview 仮想ホスト限定 (詳細は
        // Meta_BaseUri_Is_Limited_To_PreviewHost / Meta_BaseUri_Matches_PreviewBaseHref)。
        Assert.Contains("base-uri https://kxedit.preview", csp);
```

へ変更する。

**Step 2: テストを実行して赤を確認する**

```
dotnet test tests/kxEdit.Core.Tests/kxEdit.Core.Tests.csproj --filter "FullyQualifiedName~MarkdownRendererTests"
```

期待: `Meta_BaseUri_Is_Limited_To_PreviewHost` / `Meta_BaseUri_Matches_PreviewBaseHref` /
`PreviewCspHeader_ContainsAllDirectives` の **3 本が FAIL**(実際の値は `'none'`)。
他は PASS。

**Step 3: 最小の実装を書く**

`src/kxEdit.Core/Text/MarkdownRenderer.cs:46` の:

```csharp
        + "base-uri 'none'; "
```

を:

```csharp
        + "base-uri https://"
        + PreviewVirtualHost
        + "; "
```

へ変更する(他の directive と同じく `PreviewVirtualHost` 定数から生成し、
ホスト名の single source of truth を保つ)。

続いて `PreviewCspHeader` の XML doc(`:33-35` 付近)の該当 `<item>`:

```csharp
    ///   <item>MD-M-2 追加: <c>base-uri/form-action/frame-ancestors/object-src/worker-src/
    ///     manifest-src/connect-src</c> を全て <c>'none'</c> (fetch/submit/embed/worker 経路
    ///     を封鎖)</item>
```

を次で置き換える:

```csharp
    ///   <item>MD-M-2 追加: <c>form-action/frame-ancestors/object-src/worker-src/
    ///     manifest-src/connect-src</c> を全て <c>'none'</c> (fetch/submit/embed/worker 経路
    ///     を封鎖)</item>
    ///   <item>A-2 (2026-08-22): <c>base-uri</c> だけは <c>'none'</c> にしない。
    ///     <c>'none'</c> は CSP 仕様上 <see cref="PreviewBaseHref"/> の <c>&lt;base&gt;</c> を
    ///     無効化し、<c>NavigateToString</c> (data: origin) では CSS も相対画像も解決不能に
    ///     なる (v0.1.1 MD-M-2 からの退行)。本文から <c>&lt;base&gt;</c> 要素を注入する経路は
    ///     <c>DisableHtml()</c> と GenericAttributes 除去 (A-21) により存在しないため、
    ///     許可先を preview 仮想ホスト 1 つに絞れば MD-M-2 の意図は保たれる。</item>
```

**Step 4: テストを実行して緑を確認する**

```
dotnet test tests/kxEdit.Core.Tests/kxEdit.Core.Tests.csproj --filter "FullyQualifiedName~MarkdownRendererTests"
```

期待: 全 PASS。

**Step 5: ミューテーション検証(スポットチェック)**

CLAUDE.md §4 の高価値テスト検証。`Meta_BaseUri_Matches_PreviewBaseHref` が
本当に食い違いを殺せるか確認する。

1. `MarkdownRenderer.cs` の base-uri を一時的に `+ "base-uri 'none'; "` へ戻す
2. `dotnet test tests/kxEdit.Core.Tests/kxEdit.Core.Tests.csproj --filter "FullyQualifiedName~MarkdownRendererTests.Meta_BaseUri"` → **2 本とも赤**を確認
3. `PreviewBaseHref` 側だけを `"https://evil.example/"` へ一時変更(base-uri は正しい値に戻す)
   → `Meta_BaseUri_Matches_PreviewBaseHref` が**赤**、`Meta_BaseUri_Is_Limited_To_PreviewHost` は
   緑のままであることを確認(2 本が別の破れ方を捕まえている証拠)
4. **変異を必ず元に戻し、`git diff` で意図した差分だけが残っていることを目視確認する**
5. もう一度 `dotnet test ... --filter "FullyQualifiedName~MarkdownRendererTests"` で全緑を確認

⚠️ 過去に「変異を戻さずに完了報告した」事故がある。Step 4 の `git diff` 確認は省略しない。

> **実装時の訂正(Task 2 仕様レビュー)**: 上記 3. の変異は**実行できない**。テスト側の
> `Base` は `MarkdownRendererTests.cs:8` のハードコードリテラルなので、`PreviewBaseHref`
> だけを変異させると `Render` の MD-L-4 allow-list ガードが先に `ArgumentException` を投げ、
> assertion に到達する前に両方が落ちる。代替として「CSP は正しいまま `<base>` タグの出力
> だけをずらす」変異を使ったところ、`Meta_BaseUri_Matches_PreviewBaseHref` に加え既存
> `Base_href_is_injected` / `Render_Accepts_PreviewBaseHref` も赤になった。
>
> **判定**: `Meta_BaseUri_Matches_PreviewBaseHref` は vacuous ではない(変異 1. で落ちる)が、
> 現時点では他 2 本に論理的に包含されており**固有のキル能力はゼロ**。
> `normalized` が末尾スラッシュを吸収するため、この assertion が通す集合は
> `Meta_BaseUri_Is_Limited_To_PreviewHost` が通す集合の厳密な上位集合になる。
> それでも残す(3 択の ② 受容): 他 2 本は期待値をリテラルでハードコードしているため、
> 将来 `PreviewVirtualHost` を改名すると「実際値をコピーして直す」保守で網としての力を失う。
> `Matches` は両辺が同時に動くのでその直し方ができず、CSP と `<base>` の**対応関係**を
> 改名を跨いで守り続ける。設計書 §4.1 #2 の意図はこの形でのみ担保される。

**Step 6: コミット**

```bash
git add src/kxEdit.Core/Text/MarkdownRenderer.cs tests/kxEdit.Core.Tests/Text/MarkdownRendererTests.cs
git commit -m "fix(core): CSP の base-uri を preview 仮想ホストに限定し CSS/相対画像の解決を回復(A-2)"
```

---

## Task 3: 品質ゲート

**Files:** なし(実行のみ)

**Step 1: ローカルゲートを実行する**

```
pwsh -File tools/pre-merge-check.ps1
```

期待: **EXIT 0**、0 warning。テスト数は Core が +6(A-21 の 4 本 + base-uri の 2 本、
既存 `Meta_Contains_BaseUri_None` 1 本の削除を差し引き +5)。
数値は文書に書かない(CLAUDE.md §5)。実行結果が正。

**Step 2: 失敗したら**

`-warnaserror` 稼働中なので警告 1 件でも赤になる。`RemoveAll` の述語で
`e is GenericAttributesExtension` を書いたときの未使用 using などが典型。
`--no-verify` で pre-commit フックを飛ばさない(CLAUDE.md §6)。

---

## Task 4: 最終ブランチレビュー(2 パス・別エージェント)

WebView2 / プレビューはセキュリティ敏感面なので、CLAUDE.md §3-5 の 2 パスを
**統合せず**、独立した別エージェント 2 本で実施する。

**パス A: コード品質**

焦点:
- Task 1 の `RemoveAll` が他の advanced 拡張を巻き込んでいないか
- 新規テストが vacuous でないか(Task 2 Step 5 のミューテーション結果を提示する)
- XML doc と実際の CSP 文字列が食い違っていないか

**パス B: 脆弱性**

焦点(設計書 §5):
- `base-uri` 緩和後に `<base>` 要素を本文から注入する経路が本当に存在しないか
  (`DisableHtml()` の抜け・Markdig の他拡張・autolink・HTML エンティティ経由を含めて)
- `GenericAttributes` 除去で `SafeLinkExtension` / `DisableHtml` の他の防御に穴が開かないか
- CSP の他 directive が `<base>` や `<link>` を別経路で殺していないか

指摘への対応は 3 択(① fixup commit / ② PR description に記載して受容 / ③ 理由付き却下)で明示し、
**元 commit を書き換えず別 fixup commit で積む**(CLAUDE.md §4)。

---

## Task 5: L5(実機 SR 検証)チェック項目

**自動テストでは CSP と `<base>` の実挙動を検証できない。L5 が本修正の実質ゲート。**
`PreviewCspHeaderInjectorTests` は WebView2 実機を使わず文字列一致しか見ない(監査書 A-2 補足)。

監査書 §5 のとおり、L5 は PR #36〜#39 分とまとめて 1 回実施する想定。
本ブランチ分の確認項目:

| # | 項目 | 手順 | 期待 |
|---|------|------|------|
| L5-1 | 相対パス画像 | 既存 `2026-08-21-rename-to-kxedit-l5-checklist.md` の ④ を実施 | 画像が表示される(壊れた画像 + alt ではない) |
| L5-2 | CSS 適用 | 同じ .md に blockquote(`> 引用`)を含めてプレビュー | 左ボーダー・本文パディング・フォントが効いている(素の HTML に見えない) |
| L5-3 | 未保存タブの CSS | 無題タブにマークダウンを書いてプレビュー(仮想ホストマッピングなし) | CSS が適用される |
| L5-4 | 相対リンク | `[link](other.md)` をクリック | 遷移しない(MD-H-1 の仕様)。クラッシュせず、外部ブラウザも起動しない |
| L5-5 | A-21 の見た目 | **ASCII 見出し**を使い `# Title {#custom}` と `[y](x){.cls}` を含む .md をプレビュー | `{#custom}` / `{.cls}` が本文にリテラル表示される(仕様変更の確認) |
| L5-6 | A-21 のアンカー切れ | 上と同じ .md に `[jump](#custom)` を置いてクリック | 飛ばない(自動生成 id が `title-custom` に変わるため)。**代償の実機確認**であり退行ではない |

L5-4 は範囲外項目の確認(設計書 §3)。ここで**外部ブラウザが起動したら退行**なので必ず見る。

---

## Task 6: PR

**Step 1: PR を作成する**

```bash
git push -u origin feature/preview-base-uri-fix
gh pr create --base main
```

**Step 2: description に含める(日本語・CLAUDE.md §7)**

- 目的: 監査書 A-2(プレビューの CSS・相対画像が解決されない退行)と A-21(GenericAttributes)
- 退行の構造: MD-M-2 が「CSS の外部化(base 依存の発生)」と「`base-uri 'none'`(base の無効化)」を
  同一変更で入れた
- 採用した案と不採用の案(設計書 §2.2 の代替案 B / C)
- **範囲外**: 相対リンクのクリックは MD-H-1 により Block のまま(退行ではなく設計判断)
- レビュー経緯: 2 パスの指摘と 3 択の対応
- **L5 の状態**(実施済みなら結果、未実施ならその旨を明記)
> **実装時の訂正(最終レビュー・品質パス M-6)**: 上の「テスト設計の受容判断」項目は
> 案 A 前提で書かれており、そこに挙げた 3 本のテストは Task 8 で**すべて削除済み**。
> そのまま PR に書くと事実と異なるため、下の 2 項目へ差し替える。

- **ユーザー影響のある挙動変更(2 件)**:
  - A-21 により `{#id}` のカスタム見出し id が失われ、自動生成値へ変わる
    (`id="custom"` → `id="title-custom"`)。手元の .md で `[link](#custom)` を
    使っている場合は切れる
  - FINDING 1 により略語記法(`*[HTML]: HyperText Markup Language`)が `<abbr>` へ
    展開されなくなり、定義行が本文にリテラル表示される
  - どちらもリリースノートへの記載を検討する
- **テスト設計の受容判断**: パイプライン 2 本化のうち空 baseHref 側は
  production から使われないテスト専用経路だが、テストの `""` 呼び出しが多数あるため残す
- 申し送り: 説明書 148 行「ファイルへの相対パスのリンクは…参照できます」が MD-H-1 以降
  成立していない件(説明書はユーザー編集版が正のため本 PR では改稿しない)

---

## 申し送り(設計書 §6 の再掲)

- **説明書 148 行の齟齬**: ユーザーへ提起のみ。本ブランチでは改稿しない。
- **案 C(仮想ホスト上の実 URL へ Navigate)**: CSP を HTTP header で一次配布でき、
  `<base>` 依存を消せるという構造上の利点は残る。プレビューを次に大きく触るときの選択肢。

---

# 追加タスク(最終ブランチレビュー由来・2026-08-22)

最終レビューの 2 パスで、設計段階の見落とし 1 件と既存脆弱性 1 件が出た。
設計書 §7(実装時の設計変更)が以降の正。**Task 7 → Task 8 の順で実施する。**

## Task 7: FINDING 1 — 略語ラベルの未エスケープを塞ぐ

**Files:** Modify `src/kxEdit.Core/Text/MarkdownRenderer.cs`(`BuildPipeline` の `RemoveAll` 1 行)
/ Test `tests/kxEdit.Core.Tests/Text/MarkdownRendererTests.cs`

### 背景

`UseAdvancedExtensions()` 同梱の `AbbreviationExtension` が略語**ラベル**をエスケープせずに
出力し、`DisableHtml()` が全面バイパスされる(設計書 §7.5)。起点 main でも同じで本ブランチの
退行ではないが、A-21 と同じ類型・同じ行で塞げる。

### Step 1: 失敗するテストを書く

A-21 のテスト群の隣に追加する。

```csharp
    [Fact]
    public void Render_AbbreviationLabel_DoesNotEmit_RawHtml()
    {
        // FINDING 1: Markdig の HtmlAbbreviationRenderer はラベルを WriteEscape せず出力するため、
        // DisableHtml() が全面バイパスされていた (title 側は正しくエスケープされる)。
        string md = "*[<script>fetch(1)</script>]: x\n\n<script>fetch(1)</script>\n";
        string html = MarkdownRenderer.Render(md, Base);
        Assert.DoesNotContain("<script", html);
    }

    [Fact]
    public void Render_AbbreviationLabel_DoesNotEmit_MetaRefresh()
    {
        // 最も実害のある注入。CSP に該当 directive が無いため、プレビューを開くだけで
        // MarkdownPreviewForm の LaunchExternal 経路が発火し既定ブラウザが開く。
        string md =
            "*[<meta http-equiv=refresh content=0;url=https://evil.example/pwn>]: x\n\n"
            + "<meta http-equiv=refresh content=0;url=https://evil.example/pwn>\n";
        string html = MarkdownRenderer.Render(md, Base);
        Assert.DoesNotContain("<meta http-equiv=refresh", html);
    }
```

### Step 2: 赤を確認

```
dotnet test tests/kxEdit.Core.Tests/kxEdit.Core.Tests.csproj --filter "FullyQualifiedName~MarkdownRendererTests"
```

期待: 新規 2 本が FAIL。

### Step 3: 実装

`using Markdig.Extensions.Abbreviations;` を追加し、述語を拡張する。

```csharp
        builder.Extensions.RemoveAll(e =>
            e is GenericAttributesExtension || e is AbbreviationExtension
        );
```

### Step 4: 緑を確認 + **OR ガードの条件別ミューテーション**

⚠️ **OR ガードは条件ごとに 1 行ずつ変異させる**(過去に「両方の条件を同時に消しても全緑」で
vacuous な網を通してしまった事故がある)。

1. `|| e is AbbreviationExtension` だけを削る → 新規 2 本が赤・A-21 の 5 本は緑 を確認
2. `e is GenericAttributesExtension ||` だけを削る → A-21 の 5 本が赤・新規 2 本は緑 を確認
3. 両方復元し、`git diff` で変異が残っていないことを目視確認
4. `MarkdownRendererTests` 全体で緑を確認(**`--filter` で 2 本に絞らない**)

### Step 5: コミット

コミットメッセージ: `fix(core): Markdig AbbreviationExtension を除去して生 HTML 出力を塞ぐ(FINDING 1)`

---

## Task 8: 案 B — `<base>` を捨てて相対 URL を描画前に絶対化する

**Files:**
- Create: `src/kxEdit.Core/Text/PreviewUrlResolver.cs`、`src/kxEdit.Core/Text/PreviewRelativeUrlExtension.cs`
- Modify: `src/kxEdit.Core/Text/MarkdownRenderer.cs`
- Test: `tests/kxEdit.Core.Tests/Text/PreviewUrlResolverTests.cs`(新規)、
  `tests/kxEdit.Core.Tests/Text/MarkdownRendererTests.cs`

### 背景

設計書 §7 を読むこと。要点は「`<base>` があると裸のフラグメント URL まで base 基準で解決され、
目次リンクと脚注の戻りリンクが MD-H-1 の Block に巻き込まれて全滅する」。
`<base>` を出力せず、相対 URL を AST 段で絶対化する。

### Step 1: `PreviewUrlResolver` のテストを書く(新規ファイル)

規則は設計書 §7.2 の表。**規則 2(`#` 始まりは書き換えない)が FINDING 3 の要**。

```csharp
using kxEdit.Core.Text;

namespace kxEdit.Core.Tests.Text;

public class PreviewUrlResolverTests
{
    [Theory]
    [InlineData("pic.png", "https://kxedit.preview/pic.png")]
    [InlineData("sub/other.md", "https://kxedit.preview/sub/other.md")]
    [InlineData("/root.png", "https://kxedit.preview/root.png")]
    [InlineData("./pic.png", "https://kxedit.preview/pic.png")]
    public void Relative_IsResolved(string input, string expected)
    {
        Assert.True(PreviewUrlResolver.TryResolve(input, out string? actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("#midashi")]          // FINDING 3: 同一文書内アンカーを守る
    [InlineData("#fn:1")]             // 脚注の戻りリンク
    [InlineData("//evil.example/p")]  // protocol-relative は別ホストへ飛ぶので触らない
    [InlineData("https://example.com/")]
    [InlineData("http://example.com/")]
    [InlineData("mailto:a@b.c")]
    [InlineData("javascript:alert(1)")] // scheme 付きは SafeLinkExtension の管轄
    [InlineData("data:text/html,x")]
    public void NotRewritten(string? input)
    {
        Assert.False(PreviewUrlResolver.TryResolve(input, out string? actual));
        Assert.Null(actual);
    }
}
```

### Step 2: 赤を確認(型が無いのでコンパイルエラー)

### Step 3: `PreviewUrlResolver` を実装

```csharp
namespace kxEdit.Core.Text;

/// <summary>
/// プレビュー本文中の URL を、preview 仮想ホスト基準の絶対 URL へ解決する純粋ロジック。
/// <para>
/// A-2 / 設計書 §7: プレビュー文書は <c>NavigateToString</c> 経由で origin が
/// <c>data:text/html;...</c> になるため、相対 URL の解決基準を持たない。
/// <c>&lt;base href&gt;</c> を置く案は、裸のフラグメント URL (<c>#section</c>) まで base 基準で
/// 解決してしまい、目次リンクと脚注の戻りリンクが MD-H-1 の Block に巻き込まれて全滅するため
/// 採らない。代わりに描画前の AST 段でここが絶対化する。
/// </para>
/// </summary>
internal static class PreviewUrlResolver
{
    private static readonly Uri PreviewBase = new(MarkdownRenderer.PreviewBaseHref);

    /// <summary>
    /// 相対 URL なら preview 仮想ホスト基準の絶対 URL を返す。書き換え不要なら false。
    /// 判定順は設計書 §7.2 の表のとおり。
    /// </summary>
    internal static bool TryResolve(string? url, out string? absolute)
    {
        absolute = null;
        if (string.IsNullOrEmpty(url))
        {
            return false;
        }
        // FINDING 3: 裸のフラグメントは同一文書内スクロールなので絶対に触らない。
        if (url[0] == '#')
        {
            return false;
        }
        // protocol-relative は new Uri(base, "//host/p") が別ホストへ飛ぶので触らない。
        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }
        // scheme 付きは SafeLinkExtension の whitelist が扱う (javascript: 等)。
        if (Uri.TryCreate(url, UriKind.Absolute, out _))
        {
            return false;
        }
        try
        {
            absolute = new Uri(PreviewBase, url).ToString();
            return true;
        }
        catch (UriFormatException)
        {
            return false; // 解決不能は安全側に倒して書き換えない
        }
    }
}
```

⚠️ Core のテストから `internal` が見えるかを確認すること。既存の `SafeLinkExtension` が
`internal sealed` でテストされているので同じ構成でよい。見えなければ `public` 化ではなく
`InternalsVisibleTo` の追加で解決する。

### Step 4: `PreviewRelativeUrlExtension` を実装

```csharp
using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace kxEdit.Core.Text;

/// <summary>
/// 本文中の相対 URL を <see cref="PreviewUrlResolver"/> で絶対化する Markdig 拡張。
/// <c>LinkInline</c> はリンクと画像の両方を表すので 1 箇所で足りる。
/// 書き換えは <c>DocumentProcessed</c> (描画前) で行うため、描画時に効く
/// <see cref="SafeLinkExtension"/> の scheme whitelist より前段になる。
/// scheme 付き URL は <see cref="PreviewUrlResolver"/> が触らないので whitelist の判定は不変。
/// </summary>
internal sealed class PreviewRelativeUrlExtension : IMarkdownExtension
{
    public void Setup(MarkdownPipelineBuilder pipeline)
    {
        pipeline.DocumentProcessed -= OnDocumentProcessed;
        pipeline.DocumentProcessed += OnDocumentProcessed;
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer) { }

    private static void OnDocumentProcessed(MarkdownDocument document)
    {
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (PreviewUrlResolver.TryResolve(link.Url, out string? absolute))
            {
                link.Url = absolute;
            }
        }
    }
}
```

Markdig の API 名(`DocumentProcessed` / `Descendants` / `LinkInline` / `IMarkdownExtension`)は
1.3.2 の DLL で存在確認済み。**シグネチャが違ったら止めて報告すること**(推測で直さない)。

### Step 5: `MarkdownRenderer` を変更

1. **パイプラインを 2 本にする** — preview 経路は相対 URL を絶対化、`baseHref` 空文字の経路は
   解決基準が無いので書き換えない。`BuildPipeline(bool rewriteRelativeUrls)` にして
   `rewriteRelativeUrls` なら `builder.Extensions.AddIfNotAlready<PreviewRelativeUrlExtension>()` を
   `SafeLinkExtension` の**前**に足す。

   > **実装時の訂正(最終レビュー・品質パス)**: 「`SafeLinkExtension` の**前**」は
   > **実在しない制約**だった。`DocumentProcessed` は描画より前のフェーズなので登録順に
   > 依存せず、後ろへ移す変異を入れても全テストが緑のまま(生存)。害はないので実装はそのまま。

2. **`Render` で使い分ける**: `baseHref == PreviewBaseHref` なら preview 用、空文字なら素の方。
3. **`<base>` タグの出力を削除する**(`baseTag` 変数ごと)。
4. **スタイルシートの `<link>` を絶対 URL にする**。定数を追加:

```csharp
    /// <summary>プレビュー CSS の絶対 URL。<c>&lt;base&gt;</c> に依存せず解決させるため絶対で出す。</summary>
    public const string PreviewStylesheetUrl =
        "https://" + PreviewVirtualHost + PreviewStylesheetPath;
```

`PreviewStylesheetPath` は `PreviewCspHeaderInjector` が filter に使うので**残す**。

5. **CSP の `base-uri` を `'none'` へ戻す**(Task 2 の変更を差し戻す)。文書に `<base>` が
   存在しなくなるので最も強い設定でよい。
6. XML doc を実態に合わせる(`<base href>` を備えた… という記述を削る)。

### Step 6: `MarkdownRendererTests` を更新

Task 2 で入れたテストのうち base-uri 関連を差し戻し、案 B の不変条件を固定する。

- `Meta_BaseUri_Is_Limited_To_PreviewHost` / `Meta_BaseUri_Matches_PreviewBaseHref` を削除し、
  `base-uri 'none'` を固定するテストへ戻す(directive 全体を regex で切り出す形は維持してよい)
- `PreviewCspHeader_ContainsAllDirectives` の assertion を `base-uri 'none'` へ戻す
- `Base_href_is_injected` / `Empty_base_href_omits_base_tag` / `Render_Accepts_PreviewBaseHref` を
  **`<base` タグが一切出力されないこと**を固定するテストへ置き換える
- 新規:
  - `Document_StylesheetLink_IsAbsolutePreviewUrl`
  - `![](pic.png)` → `src="https://kxedit.preview/pic.png"`
  - `[y](other.md)` → `href="https://kxedit.preview/other.md"`
  - **`[目次](#midashi)` → `href="#midashi"` が不変**(FINDING 3 の回帰防止・最重要)
  - **脚注の戻りリンクが `#fn:1` / `#fnref:1` のまま**(同上)
  - `[y](https://example.com/)` が不変
  - `[y](javascript:alert(1))` の href drop が維持(`SafeLinkExtension` 不変の証拠)
  - 空 baseHref では書き換えが起きないこと

### Step 7: ミューテーション検証

⚠️ **`--filter` で絞らず `MarkdownRendererTests` + `PreviewUrlResolverTests` 全体で走らせる。**

1. `PreviewUrlResolver` の `if (url[0] == '#') return false;` を削る
   → **フラグメント不変のテストが赤になること**(FINDING 3 の網が実在する証拠)
2. `if (url.StartsWith("//"))` を削る → protocol-relative のテストが赤
3. `if (Uri.TryCreate(url, UriKind.Absolute, out _))` を削る → 絶対 URL 不変のテストが赤
4. `<link>` の href を相対に戻す → `Document_StylesheetLink_IsAbsolutePreviewUrl` が赤
5. すべて復元し `git diff` で残骸ゼロを確認

### Step 8: App 側の確認

`PreviewCspHeaderInjectorTests` が `PreviewStylesheetPath` / `PreviewCspHeader` を参照している。
`dotnet test tests/kxEdit.App.Tests/kxEdit.App.Tests.csproj --filter "FullyQualifiedName~Preview"` で緑を確認。

### Step 9: コミット

コミットメッセージ: `fix(core): <base> を捨てて相対 URL を描画前に絶対化する(案 B・FINDING 3)`

---

## L5 チェック項目の差し替え(案 B 版)

| # | 項目 | 期待 |
|---|------|------|
| L5-1 | 相対パス画像(既存チェックリスト ④) | 画像が表示される |
| L5-2 | CSS 適用(blockquote の左ボーダー・本文パディング) | 効いている |
| L5-3 | 未保存タブのプレビュー | CSS が適用される |
| L5-4 | 相対リンク `[link](other.md)` のクリック | 遷移しない(MD-H-1 の仕様)。クラッシュせず外部ブラウザも起動しない |
| L5-5 | `{#custom}` / `{.cls}` の表示(**ASCII 見出しで**) | 本文にリテラル表示される |
| L5-6 | `[jump](#custom)` のクリック | 飛ばない(自動生成 id が変わるため)。A-21 の代償の確認 |
| L5-7 | **目次リンク `[目次](#見出し)` のクリック** | **その見出しへスクロールする**(FINDING 3 の実機確認) |
| L5-8 | **脚注の戻りリンクのクリック** | **本文の参照位置へ戻る**(同上) |
| L5-9 | 略語記法 `*[HTML]: HyperText...` を含む .md | 展開されない(Task 7 の代償)。生 HTML が出ない |
