using Markdig;
using Markdig.Extensions.Abbreviations;
using Markdig.Extensions.GenericAttributes;

namespace kxEdit.Core.Text;

/// <summary>マークダウン本文を、プレビュー表示用の完結した HTML 文書へ変換する。</summary>
public static class MarkdownRenderer
{
    /// <summary>プレビュー用の仮想ホスト名（相対リソース解決の基準。App 側のマッピングと一致させる）。</summary>
    public const string PreviewVirtualHost = "kxedit.preview";

    /// <summary>
    /// preview 仮想ホストの origin URL (<see cref="PreviewVirtualHost"/> への https URL)。
    /// <para>
    /// A-2 (案 B) で <c>&lt;base href&gt;</c> の出力を廃止したので、名前に反して HTML の
    /// base href としては使われない。現在の用途は 2 つ:
    /// ① <c>PreviewUrlResolver</c> が <c>new Uri(PreviewBase, url)</c> の解決基準および
    ///    事後条件 (解決先 origin) の照合値として使う
    /// ② <see cref="Render"/> の MD-L-4 allow-list が受け付けるトークン
    /// (改名は <c>MainForm</c> とテストへ波及するため申し送り。)
    /// </para>
    /// </summary>
    public const string PreviewBaseHref = "https://" + PreviewVirtualHost + "/";

    /// <summary>
    /// D (最終レビュー): URI のホストが preview 仮想ホストを指すかを、ブラウザ側の解釈に
    /// 寄せて判定する<b>唯一の実装</b>。以前は <c>PreviewUrlResolver</c> と App 層の
    /// <c>PreviewNavigationPolicy</c> に同じ判断が逐語コピーされており、実際に
    /// 「片方だけ直す」事故 (F-7 → F-3) を踏んだため Core の 1 箇所へ集約した。
    /// App 層から見える必要があるので <c>public</c> (Core の <c>InternalsVisibleTo</c> は
    /// <c>kxEdit.App.Tests</c> であって <c>kxEdit.App</c> ではない)。
    /// <para>
    /// 判定は <see cref="Uri.Host"/> ではなく <see cref="Uri.IdnHost"/> の末尾ドット除去。
    /// <c>Host</c> は Unicode をそのまま保つので <c>https://kxedit。preview/</c> (U+3002) や
    /// <c>https://ｋxedit.preview/</c> (全角 k) が一致せず素通りするが、Markdig の
    /// <c>WriteEscapeUrl</c> も WebView2 も出力/解決時に IDNA 正規化するため、最終的には
    /// 本物の preview ホスト宛になる (F-1・実測)。末尾ドット (<c>kxedit.preview.</c>) は
    /// <c>Host</c> / <c>IdnHost</c> のいずれも保持するので明示的に削る (F-3・実測)。
    /// </para>
    /// <para>
    /// <b>戻り値が Try パターンなのが要点 (A・最終レビュー)。</b>
    /// <see cref="Uri.IdnHost"/> は <c>Uri.TryCreate(..., Absolute)</c> が<b>成功した</b>
    /// URI に対してすら <see cref="UriFormatException"/> を投げる ——
    /// <c>https://xn--あ/x</c> / ホストに U+200B や U+FFFD を含む形で実測。
    /// 例外を握らないと <c>MarkdownRenderer.Render</c> から抜けて未捕捉になり、
    /// crafted な .md でプレビューを押しただけでアプリが落ちた (本ブランチが作った退行)。
    /// U+FFFD は文字コード誤検出でも混入しうるので攻撃者不在でも踏む。
    /// </para>
    /// <para>
    /// <b>「判断がつかない」ときにどちらへ倒すかは呼び出し側の責務</b>にしてある。
    /// 2 つの呼び出し側で倒す向きが逆だから (<c>PreviewUrlResolver</c> は「無害化する」側 =
    /// default-deny、<c>PreviewNavigationPolicy</c> は <c>Block</c> 側 = safe by default)。
    /// 呼び出し側はこの述語を直接使わず、倒す向きを名前に持つ private ヘルパー越しに使う。
    /// </para>
    /// </summary>
    /// <param name="uri">絶対 URI。相対 URI を渡すのは呼び出し側のバグなので
    /// <see cref="Uri.IdnHost"/> の <see cref="InvalidOperationException"/> をそのまま通す。</param>
    /// <param name="isPreviewHost">判定できた場合の結果。できなかった場合は false。</param>
    /// <returns>判定できたら true。IDNA 変換に失敗して判断がつかないときは false。</returns>
    public static bool TryIsPreviewHost(Uri uri, out bool isPreviewHost)
    {
        ArgumentNullException.ThrowIfNull(uri);
        try
        {
            isPreviewHost = string.Equals(
                uri.IdnHost.TrimEnd('.'),
                PreviewVirtualHost,
                StringComparison.OrdinalIgnoreCase
            );
            return true;
        }
        catch (UriFormatException)
        {
            // IDNA 標準で不正な文字を含むホスト。ここで「preview ではない」と即断すると
            // 呼び出し側が安全でない向きへ倒れるので、判定不能であることを返す。
            isPreviewHost = false;
            return false;
        }
    }

    /// <summary>
    /// MD-M-2: プレビュー CSS を供給する仮想パス。App 層の
    /// <c>PreviewCspHeaderInjector</c> が <c>WebResourceRequested</c> でこのパスの
    /// GET を intercept し <see cref="PreviewStylesheet"/> を返す (実 file は無い)。
    /// <para>
    /// 先頭アンダースコアで .md フォルダ内のユーザ同名ファイルとの衝突リスクを
    /// 実質ゼロに落とす (Google/Firebase 等の "_next"/"_app" 命名慣例に倣う)。
    /// </para>
    /// </summary>
    public const string PreviewStylesheetPath = "/_kxedit/styles.css";

    /// <summary>
    /// プレビュー CSS の絶対 URL。A-2 / 設計書 §7: 文書に <c>&lt;base&gt;</c> を出さない
    /// (案 B) ため、<c>NavigateToString</c> の data: origin でも解決できるよう絶対 URL で出す。
    /// baseHref が空文字の経路でも CSS が効く。
    /// </summary>
    public const string PreviewStylesheetUrl =
        "https://" + PreviewVirtualHost + PreviewStylesheetPath;

    /// <summary>
    /// MD-M-2 + MD-L-1: プレビュー CSP を single source of truth 化した文字列。
    /// meta http-equiv 側と HTTP header 側 (PreviewCspHeaderInjector) が同じ定数を参照して
    /// 二経路の食い違いによる防御差を無くす。
    /// <para>
    /// 主要 directive:
    /// <list type="bullet">
    ///   <item><c>default-src 'none'</c>: 明示的に許可しない全 origin を block</item>
    ///   <item>MD-M-2 追加: <c>base-uri/form-action/frame-ancestors/object-src/worker-src/
    ///     manifest-src/connect-src</c> を全て <c>'none'</c> (fetch/submit/embed/worker 経路
    ///     を封鎖)</item>
    ///   <item>V-5 (2026-09-03): 上のうち <c>frame-ancestors 'none'</c> だけは
    ///     <b>meta http-equiv 配信では仕様上無視される</b> (HTTP response header 側でのみ
    ///     有効な directive)。プレビュー文書は <c>data:text/html</c> 起点で header を注入
    ///     できないため、<b>現在この directive が効く経路は無い</b>。<c>MarkdownPreviewForm</c>
    ///     は iframe に置かれないので実害は無く、将来 header 経路で文書を配信するときに
    ///     効くので残すが、<b>多層防御が在るとは読まないこと</b>。</item>
    ///   <item>A-2 (2026-08-22・案 B): <c>base-uri</c> は <c>'none'</c> のまま維持する。
    ///     文書に <c>&lt;base&gt;</c> 要素を一切出力しない方式へ切り替えたため
    ///     (相対 URL は <see cref="PreviewRelativeUrlExtension"/> が描画前に絶対化し、
    ///     CSS の <c>&lt;link&gt;</c> は <see cref="PreviewStylesheetUrl"/> で絶対指定する)、
    ///     最も強い設定で問題ない。<c>&lt;base&gt;</c> を復活させる案は、裸のフラグメント URL
    ///     まで base 基準で解決され目次リンクと脚注の戻りリンクが MD-H-1 の Block に
    ///     巻き込まれるため採らない (設計書 §7.1)。</item>
    ///   <item>MD-L-1: <c>img-src</c> から <c>data:</c> を削除 (base64 SVG 埋め込み XSS 対策)</item>
    ///   <item><c>style-src https://kxedit.preview</c>: inline <c>&lt;style&gt;</c> 撤去
    ///     に伴い <c>'unsafe-inline'</c> を削除。V-4 (2026-09-03): <c>'self'</c> も削除した。
    ///     プレビュー文書は <c>NavigateToString</c> の <c>data:text/html</c> 起点で origin が
    ///     <b>opaque</b> なので <c>'self'</c> は何にもマッチせず、防御として機能していなかった
    ///     (旧コメントの「data: URI 起点の bootstrap でも動く保険」は実在しない防御)。
    ///     <c>&lt;link&gt;</c> を実際に通しているのは <c>https://kxedit.preview</c> の方で、
    ///     将来プレビュー文書を仮想ホスト経由で配信しても origin は同じ値なので不足しない。</item>
    ///   <item><c>font-src</c> の <c>data:</c> は保持 (@font-face の data URI 埋め込み対応)</item>
    /// </list>
    /// </para>
    /// </summary>
    public const string PreviewCspHeader =
        "default-src 'none'; "
        + "base-uri 'none'; "
        + "form-action 'none'; "
        + "frame-ancestors 'none'; "
        + "object-src 'none'; "
        + "worker-src 'none'; "
        + "manifest-src 'none'; "
        + "connect-src 'none'; "
        + "img-src https://"
        + PreviewVirtualHost
        + "; "
        + "media-src https://"
        + PreviewVirtualHost
        + "; "
        + "style-src https://"
        + PreviewVirtualHost
        + "; "
        + "font-src https://"
        + PreviewVirtualHost
        + " data:";

    /// <summary>
    /// MD-L-3: レンダー入力サイズ上限 (4,000,000 文字 = 8 MB UTF-16 相当)。
    /// <para>
    /// <b>訂正 (B・最終レビュー)</b>: 旧記述は「ネスト深度 / テーブルサイズの pre-scan は
    /// 入れず、入口一箇所の cap でパーサ側の pathological な計算量爆発を封じる」だったが、
    /// <b>実態と違う</b>。pre-scan を入れていないのは事実だが、ネスト深度を実際に封じて
    /// いるのは <b>Markdig 自身の <c>MaximumNestingDepth</c> (既定 128)</b> であり、
    /// その失敗様式は「例外を投げる」——<c>"&gt; " × 200</c> (<b>400 バイト</b>) で発火する
    /// (実測)。この cap が担っているのは<b>文字数の壁だけ</b>で、深いネストはここより
    /// はるか手前で Markdig が止める。
    /// </para>
    /// <para>
    /// その例外は <see cref="Render"/> が <see cref="MarkdownTooComplexException"/> へ
    /// 翻訳し、caller (<c>MainForm.ShowMarkdownPreview</c>) がダイアログで提示する。
    /// 翻訳前は未捕捉例外としてアプリが落ちていた (main 既存の欠陥)。
    /// </para>
    /// </summary>
    public const int MaxMarkdownChars = 4_000_000;

    /// <summary>
    /// M-23: <paramref name="charCount"/> 文字の本文が <see cref="MaxMarkdownChars"/> を
    /// 超えるか。<b>全文を string 化する前に</b> caller が判定できるようにするための述語で、
    /// <see cref="Render"/> 内の cap と同じ不等号を使う (二重基準を作らない)。
    /// </summary>
    public static bool ExceedsMaxChars(int charCount) => charCount > MaxMarkdownChars;

    /// <summary>
    /// M-23: 上限超過をユーザーへ伝える詳細文言。<see cref="Render"/> が投げる
    /// <see cref="DocumentTooLargeException"/> と、事前判定した caller のダイアログで
    /// <b>同じ文面</b>を使うための single source of truth。
    /// </summary>
    public static string TooLargeDetail(int charCount) =>
        $"マークダウン本文が上限を超えました({charCount:N0}/{MaxMarkdownChars:N0} 文字)";

    /// <summary>
    /// B: <see cref="MarkdownTooComplexException"/> の文言 (<see cref="TooLargeDetail"/> と
    /// 対称に、例外とダイアログで同じ文面を使うための single source of truth)。
    /// </summary>
    public const string TooComplexDetail =
        "マークダウンの入れ子が深すぎます(引用・リスト・表などの深さがパーサの上限を超えました)";

    // CommonMark + GFM 拡張（表・チェックリスト・自動リンク等）。スレッドセーフなので使い回す。
    //
    // A-2 (2026-08-22・案 B): 相対 URL の絶対化は preview 経路だけで行うためパイプラインを 2 本持つ。
    // baseHref が空文字の経路は解決基準を持たないので書き換えない (相対のまま出す)。
    //
    // ただし Render の唯一の caller (MainForm.ShowMarkdownPreview) は常に PreviewBaseHref を
    // 渡すため、空 baseHref 側 (Pipeline) は現状 production から使われないテスト専用経路である。
    // MD-L-4 の allow-list が空文字を受け付ける契約なので分岐ごと残す (2 本化は受容)。
    //
    // セキュリティ網のうち「両経路を回すべきもの」と「preview 経路専用のもの」を取り違えないこと
    // (F-9・2026-09-03):
    //   - DisableHtml / GenericAttributes・Abbreviations の除去 / SafeLinkExtension は
    //     両パイプライン共通なので、網も両経路で回す価値がある。
    //   - V-3 の区切りエスケープ無害化 (PreviewRelativeUrlExtension が LinkRewriter として
    //     設定する) は preview 経路専用。空 baseHref 経路には仮想ホストによるフォルダー解決が
    //     無く (上記のとおり production caller が存在せず WebView2 へ渡らない)、
    //     無害化する対象そのものが無いため。この境界は
    //     MarkdownRendererTests.EmptyBaseHref_EncodedSeparators_AreNotNeutralized で固定してある。
    private static readonly MarkdownPipeline PreviewPipeline = BuildPipeline(
        rewriteRelativeUrls: true
    );
    private static readonly MarkdownPipeline Pipeline = BuildPipeline(rewriteRelativeUrls: false);

    private static MarkdownPipeline BuildPipeline(bool rewriteRelativeUrls)
    {
        // CSP との二重防御: raw HTML (script/iframe/on* 等) をパーサ段で無効化。
        var builder = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml();
        // A-21 (2026-08-22): UseAdvancedExtensions が同梱する GenericAttributes は
        // `[y](x){onclick="evil()"}` を HTML 属性としてそのまま出力し、SafeLinkExtension が
        // 落とした href すら `{href="javascript:..."}` で復活させられる。CSP (script-src なし)
        // が実行を止めているだけの状態なので、二層目の防御を回復するため拡張ごと外す。
        // 代償: `{#id}` / `{.class}` 記法は本文にリテラル表示される。見出し id は
        // UseAutoIdentifiers が引き続き生成するが、`{#id}` で指定していたカスタム id は
        // 自動生成値へ変わるため (`# Title {#custom}` は id="custom" → id="title-custom")、
        // 既存 .md 内の `[link](#custom)` は切れる。
        //
        // FINDING 1 (2026-08-22): 同じく UseAdvancedExtensions 同梱の Abbreviations も外す。
        // Markdig の略語レンダラは title (展開文) 側だけを WriteEscape し、`*[ラベル]: 展開`
        // のラベル側を生のまま出力するため、`*[<meta http-equiv=refresh ...>]: x` が
        // `DisableHtml()` を全面バイパスして HTML 要素になる。CSP に該当 directive が無い
        // meta refresh は、プレビューを開くだけで App 側の LaunchExternal 経路を発火させ
        // 既定ブラウザに攻撃者 URL を開かせうる。
        // 代償: 略語が <abbr> へ展開されなくなる (`*[...]: ...` 定義行はそのまま表示される)。
        builder.Extensions.RemoveAll(e =>
            e is GenericAttributesExtension || e is AbbreviationExtension
        );
        // A-2 (2026-08-22・案 B): 相対 URL を描画前 (DocumentProcessed) に絶対化する。
        // SafeLinkExtension は描画時に効くため、こちらが必ず前段になる。scheme 付き URL は
        // PreviewUrlResolver が触らないので whitelist の判定結果は不変。
        if (rewriteRelativeUrls)
        {
            builder.Extensions.AddIfNotAlready<PreviewRelativeUrlExtension>();
        }
        // MD-M-3: リンク URL scheme whitelist (二層目の防御)。CSP を弱めた瞬間の
        // live XSS を防ぐため javascript:/vbscript:/data:/file: 等は href を drop する。
        builder.Extensions.AddIfNotAlready<SafeLinkExtension>();
        return builder.Build();
    }

    /// <summary>
    /// markdown を HTML 化し、charset・読みやすい CSS を備えた完結した HTML 文書文字列を返す。
    /// baseHref は相対リソース解決の基準 URL で、<see cref="PreviewBaseHref"/> 定数か
    /// 空文字のみ受け付ける (MD-L-4)。
    /// <para>
    /// A-2 (2026-08-22・案 B): baseHref は <c>&lt;base&gt;</c> 要素としては出力しない
    /// (設計書 §7)。<see cref="PreviewBaseHref"/> が渡されたときだけ、本文中の相対 URL を
    /// <see cref="PreviewRelativeUrlExtension"/> が描画前に絶対化する。
    /// </para>
    /// </summary>
    /// <exception cref="ArgumentException">
    /// baseHref が空文字でも <see cref="PreviewBaseHref"/> 定数でもない場合。
    /// 単一 caller の防御的ガードで、将来 caller が増えた際の混入を fail-fast で止める。
    /// <b>これは呼び出し側の実装バグ</b>なので caller は握り潰してはならない。
    /// </exception>
    /// <exception cref="MarkdownTooComplexException">
    /// Markdig のネスト深度上限を超える入力 (B)。<c>"&gt; " × 200</c> = 400 バイトで発火する
    /// ので、<see cref="MaxMarkdownChars"/> の cap では止まらない。
    /// caller (MainForm.ShowMarkdownPreview) が捕えてユーザに MessageBox で提示する。
    /// </exception>
    /// <exception cref="DocumentTooLargeException">
    /// markdown が <see cref="MaxMarkdownChars"/> を超える場合 (MD-L-3)。
    /// caller (MainForm.ShowMarkdownPreview) が捕えてユーザに MessageBox で提示する。
    /// M-23: その caller は全文を string 化する前に <see cref="ExceedsMaxChars"/> で弾くので
    /// 通常ここへは到達しない。この cap は将来 caller が増えたときのための二重の壁。
    /// </exception>
    public static string Render(string? markdown, string baseHref)
    {
        // MD-L-4: baseHref は空文字か PreviewBaseHref 定数のみを受け付ける allow-list ガード。
        // A-2 (案 B) で <base> の出力を廃止したので、baseHref が HTML へ interpolate される
        // 攻撃面はもう存在しない。現在の役割は「どちらのパイプラインを使うか」を決める
        // トークンの fail-fast 検証で、将来 caller が増えたときの混入をここで止める。
        if (baseHref != string.Empty && baseHref != PreviewBaseHref)
        {
            throw new ArgumentException(
                $"baseHref must be either empty or MarkdownRenderer.PreviewBaseHref (\"{PreviewBaseHref}\").",
                nameof(baseHref)
            );
        }

        // MD-L-3: 入力サイズ cap (4M 文字 = 8 MB UTF-16 相当)。null は既存の
        // ?? string.Empty で吸収されるため対象外。
        // B (訂正): ここが封じるのは文字数だけ。ネスト深度 / テーブルサイズは
        // pre-scan していないし、この cap でも止まらない (400 バイトで Markdig 側の
        // 上限に当たる・実測)。それは下の catch が MarkdownTooComplexException へ翻訳する。
        // M-23: caller (MainForm.ShowMarkdownPreview) は全文 string 化の前に
        // ExceedsMaxChars で弾くが、この cap も残す (二重の壁。将来 caller が増えうる)。
        if (markdown != null && ExceedsMaxChars(markdown.Length))
        {
            long attemptedBytes = (long)markdown.Length * 2L;
            throw new DocumentTooLargeException(attemptedBytes, TooLargeDetail(markdown.Length));
        }

        // A-2 (案 B): preview 経路だけ相対 URL を絶対化するパイプラインを使う。
        MarkdownPipeline pipeline = baseHref == PreviewBaseHref ? PreviewPipeline : Pipeline;
        string body;
        try
        {
            body = Markdown.ToHtml(markdown ?? string.Empty, pipeline);
        }
        // B: Markdig は MaximumNestingDepth (既定 128) 超過を素の ArgumentException で
        // 投げる。上の MD-L-4 ガードも ArgumentException なので、ここで無差別に捕まえると
        // 「呼び出し側の実装バグ」まで握り潰す。try を ToHtml の 1 行だけに絞って
        // 構造的に分けてある (ParamName で弁別しない: Markdig 側は null・実測)。
        // ArgumentNullException は実装バグの伝播原則に従いそのまま抜けさせる。
        catch (ArgumentException ex) when (ex is not ArgumentNullException)
        {
            throw new MarkdownTooComplexException(TooComplexDetail, ex);
        }
        // MD-M-2: CSP は HTTP header (PreviewCspHeaderInjector) 側が第一防御で、
        // meta http-equiv は WebResourceRequested 未サポート環境および
        // NavigateToString(html) 初回 bootstrap の data:text/html origin (header 注入不可) 用の
        // fallback。同じ PreviewCspHeader 定数を参照して食い違いを防ぐ。
        // MD-M-2: <style>{Css}</style> を撤去し <link> で外部化。CSS 実体は
        // App 層の Injector が virtual response で供給する (PreviewStylesheetPath 経由)。
        // A-2 (案 B): href は <base> に依存させないため絶対 URL (PreviewStylesheetUrl)。
        return $$"""
            <!DOCTYPE html>
            <html lang="ja">
            <head>
            <meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="{{PreviewCspHeader}}">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <link rel="stylesheet" href="{{PreviewStylesheetUrl}}">
            </head>
            <body>
            {{body}}
            </body>
            </html>
            """;
    }

    /// <summary>
    /// MD-M-2: プレビュー用の CSS 文字列 (single source of truth)。
    /// App 層の <c>PreviewCspHeaderInjector</c> が <see cref="PreviewStylesheetPath"/> 宛
    /// の <c>WebResourceRequested</c> を intercept してこの文字列を返す
    /// (<c>Content-Type: text/css; charset=utf-8</c>)。
    /// <para>
    /// 見た目 (font/レイアウト) は 従来 inline <c>&lt;style&gt;</c> と完全同一。
    /// 変更時は L5 (実プレビュー描画) で回帰を確認。
    /// </para>
    /// </summary>
    public const string PreviewStylesheet = """
        body { font-family: "Segoe UI", "Meiryo", sans-serif; line-height: 1.6;
               max-width: 900px; margin: 0 auto; padding: 24px; color: #1f2328; }
        h1, h2 { border-bottom: 1px solid #d0d7de; padding-bottom: .3em; }
        code { background: #afb8c133; padding: .2em .4em; border-radius: 6px;
               font-family: "Consolas", monospace; }
        pre { background: #f6f8fa; padding: 16px; border-radius: 6px; overflow: auto; }
        pre code { background: none; padding: 0; }
        table { border-collapse: collapse; }
        th, td { border: 1px solid #d0d7de; padding: 6px 13px; }
        blockquote { color: #57606a; border-left: .25em solid #d0d7de;
                     padding: 0 1em; margin: 0; }
        img { max-width: 100%; }
        a { color: #0969da; }
        """;
}
