using System.Text;
using kxEdit.Core.Buffers;

namespace kxEdit.Core.Text;

public static partial class TextFileService
{
    /// <summary>UTF-8 BOM(0xEF, 0xBB, 0xBF)= TextBuffer 版 Save の hasBom 分岐で毎呼出しの
    /// アロケーションを避ける(Minor-2 対応: 静的 readonly に昇格)。</summary>
    private static readonly byte[] s_utf8Bom = new byte[] { 0xEF, 0xBB, 0xBF };

    /// <summary>
    /// ファイルを読み込み本文・文字コード・改行を確定する。
    /// forcedCodePage 指定時は自動判定せずそのコードページで読む（開き直し用）。
    /// </summary>
    public static LoadedDocument Load(string path, int? forcedCodePage = null)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return DecodeBytes(bytes, forcedCodePage);
    }

    /// <summary>
    /// P6 Task 7 + P7 I-4: Stream I/O でファイルを TextBuffer に読み込む(大容量 OOM 回避)。
    /// UTF-8 は変換ゼロで <see cref="TextBufferBuilder"/> にチャンク流し(64KB 単位・Builder 内 4MB
    /// チャンク境界は carry で吸収)。SJIS/EUC-JP は <see cref="StreamReader.Read(char[], int, int)"/>
    /// の 8KB チャンクループで UTF-16 化 → <see cref="Encoding.UTF8.GetBytes(char[], int, int, byte[], int)"/>
    /// で UTF-8 化 → <see cref="TextBufferBuilder.Add"/>=UTF-8 経路と同じく peak ~1x に抑える
    /// (旧実装の ReadToEnd による全文 char[] 常駐+Builder UTF-8 常駐の 2x メモリを回避)。
    /// forcedCodePage 版の <see cref="Load"/> と違い、encoding は呼び出し側で確定済み(再オープン/明示指定用)。
    /// </summary>
    /// <param name="path">ファイルパス。</param>
    /// <param name="encoding">読み込みエンコーディング(UTF-8/SJIS/EUC-JP のいずれか想定)。</param>
    /// <param name="hasBom">UTF-8 BOM を持つファイルなら true。UTF-8 経路のみ意味を持つ(先頭 3 バイトをスキップ)。</param>
    /// <returns>
    /// <c>Buffer</c> = チャンク木構築済みの TextBuffer。
    /// <c>HadReplacement</c> = デコード中に不正バイトが U+FFFD REPLACEMENT CHARACTER で埋められたか。
    /// UTF-8 は <see cref="TextBufferBuilder.HadReplacement"/> をそのまま返し、
    /// SJIS/EUC-JP は明示的な <see cref="DecoderReplacementFallback"/>("�") で U+FFFD 化された
    /// 本文を後スキャンして判定する(<see cref="DecodeBytes"/> と同じ契約=文字コード取り違え検出用)。
    /// SJIS/EUC-JP 経路の詳細フォールバック仕様は <see cref="DecodeBytes"/> のコメントを参照。
    /// </returns>
    public static (TextBuffer Buffer, bool HadReplacement) LoadAsBuffer(
        string path,
        Encoding encoding,
        bool hasBom = false
    )
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(encoding);
        EncodingCatalog.EnsureRegistered();

        using var stream = File.OpenRead(path);
        if (encoding.CodePage == 65001)
        {
            // UTF-8: 変換ゼロで TextBufferBuilder に流す
            var builder = new TextBufferBuilder();
            byte[] buf = new byte[64 * 1024]; // 64KB チャンク
            if (hasBom)
            {
                // BOM(3 バイト)を読み飛ばす
                int skipped = 0;
                while (skipped < 3)
                {
                    int r = stream.Read(buf, 0, 3 - skipped);
                    if (r <= 0)
                        break;
                    skipped += r;
                }
            }
            int n;
            while ((n = stream.Read(buf, 0, buf.Length)) > 0)
                builder.Add(new ReadOnlySpan<byte>(buf, 0, n));
            return (builder.Build(), builder.HadReplacement);
        }
        else
        {
            // P7 I-4: SJIS/EUC-JP も StreamReader.Read(char[], 0, len) チャンクループで
            // UTF-16 化 → UTF-8 化 → TextBufferBuilder.Add に段階変換する。
            // 旧実装(ReadToEnd で全文 UTF-16 化 → TextBuffer.FromString)は数百 MB 級で
            // 「全文 char[] 常駐 + Builder 内 UTF-8 常駐」の 2x メモリ peak になっていた。
            //
            // detectEncodingFromByteOrderMarks: false = 先頭バイトを BOM 検出に流用せず、
            // 指定エンコーディングで確実にデコードする(SJIS/EUC-JP に BOM は無いが保険)。
            // 既定の Encoding.GetEncoding(cp) は DecoderFallback に PUA U+F8F3 相当を返す挙動があり、
            // 不正バイトが U+FFFD で検出できず「文字コード取り違え」警告が沈黙する。
            // DecodeBytes と同じく明示的な DecoderReplacementFallback("�") を差し込む。
            //
            // 注: Encoding.UTF8.GetBytes(char[], 0, n, ...) は stateless 変換(char[] を完全な
            //     コードポイント列として扱う)。SJIS/EUC-JP は BMP 外を表現できず、
            //     DecoderReplacementFallback の U+FFFD も BMP なので、char[] にはサロゲートが
            //     1 個も現れない=境界分断リスクなし。UTF-16LE/UTF-32 等 SMP を扱う encoding に
            //     この経路を流用する場合は StreamReader 内部の対分断特性を再検証すること。
            var decoding = Encoding.GetEncoding(
                encoding.CodePage,
                EncoderFallback.ReplacementFallback,
                new DecoderReplacementFallback("�")
            );
            using var reader = new StreamReader(
                stream,
                decoding,
                detectEncodingFromByteOrderMarks: false
            );
            var builder = new TextBufferBuilder();
            const int CharBufLen = 8 * 1024;
            char[] charBuf = new char[CharBufLen];
            byte[] utf8Buf = new byte[Encoding.UTF8.GetMaxByteCount(CharBufLen)];
            bool hadReplacement = false;
            int n;
            while ((n = reader.Read(charBuf, 0, CharBufLen)) > 0)
            {
                if (!hadReplacement)
                {
                    for (int i = 0; i < n; i++)
                        if (charBuf[i] == '�')
                        {
                            hadReplacement = true;
                            break;
                        }
                }
                int written = Encoding.UTF8.GetBytes(charBuf, 0, n, utf8Buf, 0);
                builder.Add(new ReadOnlySpan<byte>(utf8Buf, 0, written));
            }
            return (builder.Build(), hadReplacement);
        }
    }

    /// <summary>
    /// P6 Task 10 + P7 I-4: App 層一発読み(<see cref="LoadInto"/> 相当の統合エントリ)。Stream ベースで
    /// 検出 → <see cref="LoadAsBuffer"/> → LineEnding 検出を一括で行う。UTF-8 / SJIS / EUC-JP のいずれも
    /// chunk stream 経路で常駐 ~1x に抑え、1GB 級の OOM(全文 string ~3GB 常駐)を回避する。
    /// SJIS/EUC-JP の chunk read 化は P7 I-4 で完成(詳細は <see cref="LoadAsBuffer"/> のドキュメント参照)。
    /// </summary>
    /// <remarks>
    /// エンコーディング検出は先頭 64KB を prefix として <see cref="EncodingDetector.Detect"/> に渡す
    /// (BOM は先頭 3 バイト固定・UtfUnknown CharsetDetector は数十 KB で十分な精度・
    /// 厳格 UTF-8 判定は UTF-8 の chunk 境界で multibyte が分断されないよう <see cref="Utf8SafePrefixLength"/>
    /// で prefix 末尾を UTF-8 sequence 境界にトリムしてから渡す)。
    /// LineEnding は本文チャンク木の全体を byte 走査して多数決で決める
    /// (<see cref="LineEndingDetector.Detect(TextSnapshot)"/>)。A-9(2026-08-28)以前は先頭
    /// 4,096 code unit(UTF-16)だけを <c>GetText</c> して流していたが、1 行目がその窓より長い
    /// LF / CR ファイルが
    /// 改行 0 件と見なされて CRLF 既定へ倒れ、保存時に全行が無警告で書き換わっていた。
    /// 窓は復活させないこと(string を実体化しない走査なのでピークメモリは増えない)。
    /// forcedCodePage 指定時は自動判定を飛ばし <see cref="HasBomFor"/> のみでプリアンブル判定。
    /// </remarks>
    public static LoadedBuffer LoadAsBufferAuto(string path, int? forcedCodePage = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        EncodingCatalog.EnsureRegistered();

        // 1) prefix 読み(64KB or ファイル全体で小さい方)
        byte[] prefix;
        using (var probe = File.OpenRead(path))
        {
            long fileLen = probe.Length;
            int prefixLen = (int)Math.Min(64L * 1024L, fileLen);
            prefix = new byte[prefixLen];
            int read = 0;
            while (read < prefixLen)
            {
                int n = probe.Read(prefix, read, prefixLen - read);
                if (n <= 0)
                    break;
                read += n;
            }
            if (read != prefixLen)
                Array.Resize(ref prefix, read);
        }

        // 2) エンコーディング検出。厳格 UTF-8 判定が prefix 末尾で multibyte 分断により
        //    誤って false になるのを防ぐため、UTF-8 sequence 境界にトリムしてから渡す
        //    (例: 日本語 3-byte が prefix 末尾で 2 バイトだけ入っていると DecoderFallback で失敗する)。
        DetectedEncoding det;
        if (forcedCodePage is int cp)
        {
            det = new DetectedEncoding(cp, HasBomFor(prefix, cp));
        }
        else
        {
            int safeLen = Utf8SafePrefixLength(prefix);
            byte[] safePrefix =
                safeLen == prefix.Length ? prefix : prefix.AsSpan(0, safeLen).ToArray();
            det = EncodingDetector.Detect(safePrefix);
        }

        Encoding enc = EncodingCatalog.Get(det.CodePage);

        // 3) 本体を Stream で TextBuffer に読み込み
        var (buffer, hadReplacement) = LoadAsBuffer(path, enc, det.HasBom);

        // 4) LineEnding 検出。A-9(2026-08-28): 先頭 4,096 code unit(UTF-16)窓を撤廃し、バッファ全体を
        //    byte 走査する(string 化なし)。旧実装は 1 行目が窓より長い LF ファイルを
        //    CRLF と誤判定し、Ctrl+S で全行を無警告に書き換えていた。
        //    窓は P6 Task 10 の Stream 化で入った退行で、旧 DecodeBytes は全文判定だった。
        LineEnding eol = LineEndingDetector.Detect(buffer.Current);

        return new LoadedBuffer
        {
            Buffer = buffer,
            Encoding = enc,
            HasBom = det.HasBom,
            LineEnding = eol,
            HadReplacementChar = hadReplacement,
        };
    }

    /// <summary>
    /// prefix バイト列の末尾を UTF-8 シーケンス境界にトリムする長さを返す(<see cref="LoadAsBufferAuto"/> 用)。
    /// 末尾の 10xxxxxx 継続バイトを遡り、直近の leader を見つけて必要バイト数と比較する。
    /// leader から後ろが不完全なら leader 位置で切る=<see cref="EncodingDetector"/> の厳格 UTF-8
    /// 判定が「途中で切れた multibyte」で誤って failed になるのを防ぐ。
    /// 全継続バイト/不正 leader の場合は元の長さをそのまま返す(strict test に判断を委ねる)。
    /// </summary>
    private static int Utf8SafePrefixLength(byte[] bytes)
    {
        int len = bytes.Length;
        if (len == 0)
            return 0;
        // 末尾から継続バイト(10xxxxxx)を遡る
        int scan = len - 1;
        while (scan >= 0 && (bytes[scan] & 0xC0) == 0x80)
            scan--;
        if (scan < 0)
            return len; // 全部継続バイト=malformed(strict test で失敗させる)
        byte lead = bytes[scan];
        int expected;
        if ((lead & 0x80) == 0)
            expected = 1; // 0xxxxxxx
        else if ((lead & 0xE0) == 0xC0)
            expected = 2; // 110xxxxx
        else if ((lead & 0xF0) == 0xE0)
            expected = 3; // 1110xxxx
        else if ((lead & 0xF8) == 0xF0)
            expected = 4; // 11110xxx
        else
            return len; // 不正 leader
        int have = len - scan;
        return have >= expected ? len : scan; // 不完全なら leader 直前で切る
    }

    /// <summary>
    /// 既に読み込んだバイト列を本文・文字コード・改行へ復号する（grep がバイトを 1 回だけ読んで
    /// 流用するための抽出点。Load と完全に同じ判定・復号ロジック）。
    /// forcedCodePage 指定時は自動判定せずそのコードページで読む。
    /// </summary>
    public static LoadedDocument DecodeBytes(byte[] bytes, int? forcedCodePage = null)
    {
        EncodingCatalog.EnsureRegistered();

        DetectedEncoding det = forcedCodePage is int cp
            ? new DetectedEncoding(cp, HasBomFor(bytes, cp))
            : EncodingDetector.Detect(bytes);

        Encoding enc = EncodingCatalog.Get(det.CodePage);

        // 置換文字検出のため、不正バイトを U+FFFD（置換文字）に落とすデコーダを用意。
        // ※ 既定の Encoding.UTF8 インスタンスはデコード置換に U+FFFD を使うが、ここで使う
        //   Encoding.GetEncoding(cp, ...) に静的 DecoderFallback.ReplacementFallback を渡すと
        //   置換が '?'(U+003F) になる（DecoderReplacementFallback の既定文字は '?'＝実測確認済）。
        //   取り違え検出を確実にするため、明示的に U+FFFD のフォールバックを指定する。
        var decoder = Encoding.GetEncoding(
            det.CodePage,
            EncoderFallback.ReplacementFallback,
            new DecoderReplacementFallback("�")
        );

        // BOM を除いた本文部分をデコード。
        // 注意: EncodingCatalog.Get(65001) は BOM 無し UTF8Encoding を返し GetPreamble() が空になる。
        //       BOM 付き UTF-8 でも先頭 3 バイトを確実に剥がすため、preamble 長は BOM を出す
        //       codepage 既定の Encoding（decoder）から取得する。
        int preambleLen = det.HasBom ? decoder.GetPreamble().Length : 0;
        string text = decoder.GetString(bytes, preambleLen, bytes.Length - preambleLen);

        // 注: 本文中に元から U+FFFD が含まれる場合とデコード失敗の置換とを区別しない近似。
        //     文字コード取り違えの「示唆」用途であり、厳密な判定ではない。
        bool hadReplacement = text.Contains('�'); // U+FFFD REPLACEMENT CHARACTER
        LineEnding eol = LineEndingDetector.Detect(text);

        return new LoadedDocument
        {
            Text = text,
            Encoding = enc,
            HasBom = det.HasBom,
            LineEnding = eol,
            HadReplacementChar = hadReplacement,
        };
    }

    private static bool HasBomFor(byte[] bytes, int codePage)
    {
        var pre = Encoding.GetEncoding(codePage).GetPreamble();
        if (pre.Length == 0 || bytes.Length < pre.Length)
            return false;
        for (int i = 0; i < pre.Length; i++)
            if (bytes[i] != pre[i])
                return false;
        return true;
    }

    /// <summary>
    /// 原子的にテキストを保存する（実体は <see cref="IO.AtomicFile"/>：temp へ書いてから
    /// File.Replace／新規は File.Move）。「共有違反/ロック競合」で失敗した場合に限り in-place
    /// 上書きへフォールバックする。それ以外の I/O 失敗（ディスクフル等）は、原本に一切触れず
    /// そのまま例外を伝播する（= 原子的保存の目的＝原本喪失の回避を守る）。
    ///
    /// P7 I-3 以降、通常経路は Buffer 版
    /// (<see cref="Save(string, TextBuffer, Encoding, bool)"/>) を使う。
    /// 本 overload は共有違反フォールバック専用(Buffer 版の catch{} から委譲される)であり、
    /// 新規呼び出し元を作らないこと(全文字列化=大容量で peak メモリ 2〜3 倍・OOM 要因)。
    /// <c>InternalsVisibleTo(kxEdit.Core.Tests)</c> によりテストからは呼べる。
    /// </summary>
    internal static void Save(string path, string text, Encoding encoding, bool hasBom)
    {
        EncodingCatalog.EnsureRegistered();

        // BOM 制御: UTF-8 のみ hasBom に応じて preamble 有無を切替。それ以外（UTF-16 等）は
        // 渡された Encoding をそのまま使い、preamble は hasBom 指定時に GetPreamble() で手前に付ける
        // （body には含めないため二重付与しない）。
        Encoding enc =
            encoding.CodePage == 65001
                ? new UTF8Encoding(encoderShouldEmitUTF8Identifier: hasBom)
                : encoding;
        byte[] preamble = hasBom ? enc.GetPreamble() : Array.Empty<byte>();
        byte[] body = enc.GetBytes(text);

        byte[] payload;
        if (preamble.Length == 0)
        {
            payload = body;
        }
        else
        {
            payload = new byte[preamble.Length + body.Length];
            Buffer.BlockCopy(preamble, 0, payload, 0, preamble.Length);
            Buffer.BlockCopy(body, 0, payload, preamble.Length, body.Length);
        }

        try
        {
            IO.AtomicFile.Write(path, payload);
        }
        catch (IOException ex) when (IO.AtomicFile.IsShareOrLockViolation(ex))
        {
            // 共有違反/ロック競合（AV・同期ソフト等が一時的に掴んでいる）に限り in-place 上書きへ。
            // FileMode.Create はハンドルを開けてからしか切り詰めないため、ロックで open に失敗した
            // 場合は原本を 0 バイトにせず例外が伝播する（= 原本を喪失しない）。tmp は AtomicFile が掃除済み。
            // 共有違反以外（ディスクフル等）はフォールバックせず伝播する（原本を壊さない）。
            // 注: 旧実装は差替段階の共有違反のみ救済していたが、乱数名 tmp のステージング書込で
            //     共有違反は事実上起きないため、段階を区別せず単純化している（どの段階由来でも
            //     in-place は上記の非切詰め特性により安全）。
            File.WriteAllBytes(path, payload);
        }
    }

    /// <summary>
    /// P7 I-3: <see cref="TextBuffer"/> をファイルに保存する(Stream I/O 経路・chunk write)。
    /// UTF-8 は <see cref="TextSnapshot.WriteTo(Stream)"/> で変換ゼロチャンク直書き。
    /// SJIS/EUC-JP は <see cref="TextSnapshot.CreateReader"/>(SnapshotReader)経由の
    /// <see cref="Encoder.Convert"/> チャンクループで char[] → bytes を段階変換=1GB 級でも peak ~O(chunk)。
    /// AtomicFile.Write(Stream) で原子書込。共有違反時のみ payload を一括ビルドして byte[] 版 Save に委譲
    /// (in-place 上書きフォールバックの契約温存・fallback は稀=このパスだけ全文化を許容)。
    /// EOL 変換は事前に <see cref="EditorControl.ConvertEols"/> 済み前提。
    /// </summary>
    public static void Save(string path, TextBuffer buffer, Encoding encoding, bool hasBom)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentNullException.ThrowIfNull(encoding);
        EncodingCatalog.EnsureRegistered();

        var snap = buffer.Current;

        try
        {
            IO.AtomicFile.Write(
                path,
                stream =>
                {
                    if (encoding.CodePage == 65001)
                    {
                        if (hasBom)
                            stream.Write(s_utf8Bom, 0, 3);
                        snap.WriteTo(stream);
                    }
                    else
                    {
                        // Minor-1: DecoderFallback は書込経路では発火しないが、
                        // Encoding.GetEncoding(int, EncoderFallback, DecoderFallback) は 3 引数版のみで
                        // DecoderFallback を省略できない=シグネチャ要件で残置(実質デッドコード)。
                        Encoding enc = Encoding.GetEncoding(
                            encoding.CodePage,
                            EncoderFallback.ReplacementFallback,
                            DecoderFallback.ReplacementFallback
                        );
                        if (hasBom)
                        {
                            byte[] preamble = enc.GetPreamble();
                            if (preamble.Length > 0)
                                stream.Write(preamble, 0, preamble.Length);
                        }
                        using var reader = snap.CreateReader();
                        var encoder = enc.GetEncoder();
                        const int CharBufLen = 8 * 1024;
                        char[] charBuf = new char[CharBufLen];
                        byte[] byteBuf = new byte[enc.GetMaxByteCount(CharBufLen)];
                        int charRead;
                        while ((charRead = reader.Read(charBuf, 0, CharBufLen)) > 0)
                        {
                            int offset = 0;
                            while (offset < charRead)
                            {
                                encoder.Convert(
                                    charBuf,
                                    offset,
                                    charRead - offset,
                                    byteBuf,
                                    0,
                                    byteBuf.Length,
                                    flush: false,
                                    out int charsUsed,
                                    out int bytesUsed,
                                    out _
                                );
                                if (bytesUsed > 0)
                                    stream.Write(byteBuf, 0, bytesUsed);
                                offset += charsUsed;
                            }
                        }
                        // 最終 flush(サロゲート途中終わりは FFFD 化される=既存挙動と等価)
                        encoder.Convert(
                            Array.Empty<char>(),
                            0,
                            0,
                            byteBuf,
                            0,
                            byteBuf.Length,
                            flush: true,
                            out _,
                            out int flushBytes,
                            out _
                        );
                        if (flushBytes > 0)
                            stream.Write(byteBuf, 0, flushBytes);
                    }
                }
            );
        }
        catch (IOException ex) when (IO.AtomicFile.IsShareOrLockViolation(ex))
        {
            // 共有違反フォールバック=byte[] 版に委譲(全文化する稀ケース=in-place 上書き契約を温存)。
            string text = snap.GetText(0, snap.CharLength);
            Save(path, text, encoding, hasBom);
        }
    }
}
