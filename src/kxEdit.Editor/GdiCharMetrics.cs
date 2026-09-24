using kxEdit.Core.Layout;
using kxEdit.Core.Text;

namespace kxEdit.Editor;

/// <summary>
/// TextRenderer(GDI)ベースの <see cref="ICharMetrics"/> 実装(UI スレッド専用)。
/// ASCII(0..127)の 1 文字幅を構築時に前計算してキャッシュし、ホットパス(<see cref="MeasureRun"/> は
/// 1000 文字行なら 1000 回呼ばれる)ではキャッシュ加算で完結させる。カーニングは無視する。
/// TAB は半角スペース幅として扱う(タブ揃えの本実装は入力側 P3 に配置)。
/// 非 ASCII の 1 コードポイントは初回だけ GDI で測り、以後はメモ化した値を返す(下記)。
/// 非 ASCII を含む複数コードポイントの run も、run 全体の MeasureText の結果を
/// 合計文字数の上限付きでメモ化する(<see cref="MaxCachedRunChars"/> / <see cref="RunCacheBudgetChars"/>)。
/// </summary>
/// <remarks>
/// <b>スレッド安全性(重要)</b>: 本クラスは <b>UI スレッド専用</b>である。
/// <see cref="_nonAsciiWidths"/> は非スレッドセーフな <see cref="Dictionary{TKey,TValue}"/> で、
/// 複数スレッドから同時に書かれると値がずれるのではなく<b>構造が壊れる</b>(無限ループ・例外)。
/// UIA RPC スレッドからは <c>UiaTextHostAdapter</c> が UI スレッドへマーシャリングしてから呼ぶ。
/// <see cref="Control.Invoke(Delegate)"/> で同期マーシャリングする読み取り系は
/// <c>TryFindVisualSegment</c> / <c>GetVisibleRange</c> / <c>GetBoundingRectangles</c> /
/// <c>OffsetFromScreenPoint</c> の 4 経路。書き込み系(<c>SetSelection</c> / <c>SetFocus</c> /
/// <c>ScrollRangeIntoView</c>)も同様に UI スレッドへ移してから呼ぶ。この契約を崩さないこと。
/// <para>
/// <b>マーシャリングの前提</b>: 7 経路とも <c>IsHandleCreated</c> を
/// <c>InvokeRequired</c> の<b>手前</b>で見て、Handle が無ければ縮退値を返して抜ける。
/// <see cref="Control.InvokeRequired"/> は Handle 未生成 / 破棄後に <c>false</c> を返すため、
/// この順序が崩れると RPC スレッドがマーシャリングを経ずに本クラスへ到達する。
/// 順序を変えないこと(2026-08-02 に <c>GetBoundingRectangles</c> /
/// <c>OffsetFromScreenPoint</c> の 2 経路を実測で発見して是正した)。
/// </para>
/// </remarks>
public sealed class GdiCharMetrics : ICharMetrics
{
    private static readonly Size MaxSize = new(int.MaxValue, int.MaxValue);
    private const TextFormatFlags MeasureFlags =
        TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix;

    private readonly Font _font;
    private readonly int[] _asciiWidths;

    // 2026-08-02 変更 A: 非 ASCII 1 コードポイントの幅メモ化。
    // LineLayout.Wrap は 1 コードポイントずつ MeasureRun を呼ぶため(LineLayout.cs)、
    // 長大行では同じ文字種を何度も GDI で測り直していた(CJK 500K 文字で約 40 秒 =
    // docs/plans/2026-08-02-large-line-resilience-design.md §2.3)。
    // 格納するのは MeasureText の結果そのもの = 返す値は不変。
    //
    // 無効化は不要: 本クラスはフォント単位で生成され、フォント変更時は
    // インスタンスごと差し替えられる(EditorControl の初期化と ApplyAppearance)。
    // = キャッシュの寿命がフォントの寿命と一致する。
    //
    // 初回コストは文書長ではなく「異なるコードポイント数」で決まる(日本語なら 2,000 種程度)。
    // エントリ数は文書に現れるコードポイント数で有界。
    private readonly Dictionary<int, int> _nonAsciiWidths = new();

    // 2026-09-24 性能改善フェーズ 1(P-2): 非 ASCII を含む複数コードポイントの run の幅メモ。
    // 描画(FrameBuilder の本文 run)・横スクロールバー(UpdateHorizontalScrollbar の全可視行)・
    // キャレット X(PixelMapper.OffsetToPx の prefix)が、同じ run を描画・打鍵ごとに測り直していた
    // (ja10k で 2.5 ms/打鍵 = 調査記録 §9.2)。格納するのは MeasureText の結果そのもの = 返す値は不変。
    //
    // 上限は件数ではなくキーの合計文字数で決める(件数だと最悪 4,096 字 × 件数に膨らみ、
    // それがタブ数倍になる)。溢れたら全消去する。1 件が MaxCachedRunChars を超える run は格納しない。
    // 寿命は _nonAsciiWidths と同じ = フォントの寿命。UI スレッド専用(クラス doc の契約)。
    internal const int MaxCachedRunChars = 4096;
    internal const int RunCacheBudgetChars = 256 * 1024;

    private readonly Dictionary<string, int> _runWidths = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int>.AlternateLookup<ReadOnlySpan<char>> _runWidthsBySpan;
    private int _runCacheChars;

    internal int TestHook_RunCacheCount => _runWidths.Count;
    internal int TestHook_RunCacheChars => _runCacheChars;

    public GdiCharMetrics(Font font)
    {
        ArgumentNullException.ThrowIfNull(font);
        _font = font;
        LineHeightPx = TextRenderer.MeasureText("Mg", font, MaxSize, MeasureFlags).Height;
        _asciiWidths = new int[128];
        for (int c = 0; c < 128; c++)
        {
            _asciiWidths[c] = TextRenderer
                .MeasureText(((char)c).ToString(), font, MaxSize, MeasureFlags)
                .Width;
        }
        _asciiWidths['\t'] = _asciiWidths[' '];
        _runWidthsBySpan = _runWidths.GetAlternateLookup<ReadOnlySpan<char>>();
    }

    public int LineHeightPx { get; }

    public int MeasureRun(ReadOnlySpan<char> text)
    {
        // ホットパス: 非 ASCII の単一コードポイント(= LineLayout.Wrap の呼び方)はメモ化で返す。
        // 複数コードポイントの run は一括 MeasureText の既存挙動を維持する
        // (run 全体の計測結果はコードポイント幅の和と一致するとは限らないため。run 単位でメモ化する)。
        if (
            text.Length > 0
            && text[0] >= 128
            && TextBoundary.CodePointLengthAt(text, 0) == text.Length
        )
        {
            return CachedCodePointWidth(text);
        }

        int px = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c >= 128)
                return CachedRunWidth(text);
            px += _asciiWidths[c];
        }
        return px;
    }

    /// <summary>
    /// 非 ASCII 1 コードポイントの幅をメモ化して返す。
    /// キーは長さ 2 なら UTF-32 コードポイント(&gt;= 0x10000)、長さ 1 ならその char 値
    /// (&lt;= 0xFFFF)。両者は値域が重ならないため、単独サロゲートとサロゲートペアが
    /// 同じエントリを共有することはない。
    /// </summary>
    /// <remarks>
    /// <paramref name="cp"/> の長さが 2 のとき <see cref="char.ConvertToUtf32(char, char)"/> が
    /// 安全なのは、呼び出し元のガードが <c>TextBoundary.CodePointLengthAt(text, 0) == text.Length</c>
    /// を要求しており、span 版 <c>CodePointLengthAt</c> が 2 を返すのは
    /// <b>正当なサロゲートペアのときだけ</b>だから(単独サロゲート・逆順ペアはいずれも 1 を返し、
    /// 長さ 1 の <c>cp[0]</c> 側へ落ちる)。
    /// </remarks>
    private int CachedCodePointWidth(ReadOnlySpan<char> cp)
    {
        int key = cp.Length == 2 ? char.ConvertToUtf32(cp[0], cp[1]) : cp[0];
        if (_nonAsciiWidths.TryGetValue(key, out int cached))
            return cached;

        int width = TextRenderer.MeasureText(cp.ToString(), _font, MaxSize, MeasureFlags).Width;
        _nonAsciiWidths[key] = width;
        return width;
    }

    /// <summary>
    /// 非 ASCII を含む複数コードポイントの run の幅をメモ化して返す(フィールドのコメント参照)。
    /// ヒット時は文字列を割り当てない(span のまま引く)。
    /// </summary>
    private int CachedRunWidth(ReadOnlySpan<char> text)
    {
        if (text.Length <= MaxCachedRunChars && _runWidthsBySpan.TryGetValue(text, out int cached))
            return cached;
        string s = text.ToString();
        int width = TextRenderer.MeasureText(s, _font, MaxSize, MeasureFlags).Width;
        if (s.Length <= MaxCachedRunChars)
        {
            if (_runCacheChars + s.Length > RunCacheBudgetChars)
            {
                _runWidths.Clear();
                // Dictionary.Clear は内部配列の容量を保持するので、最悪時(短い run が大量)の数 MB を返す。
                // 同じインスタンスのままなので _runWidthsBySpan はそのまま有効。
                _runWidths.TrimExcess();
                _runCacheChars = 0;
            }
            // 検索と格納の条件が将来ずれても文字数を二重計上しないよう、新規格納のときだけ加算する。
            if (_runWidths.TryAdd(s, width))
                _runCacheChars += s.Length;
        }
        return width;
    }
}
