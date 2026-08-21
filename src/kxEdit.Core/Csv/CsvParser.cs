using System.Text;
using kxEdit.Core.Buffers;

namespace kxEdit.Core.Csv;

/// <summary>
/// RFC 4180 準拠の CSV パーサ（区切りはカンマ固定）。各フィールドの元テキスト上の
/// UTF-16 スパン（引用符込み）を保持する。引用符内のカンマ・改行・"" エスケープに対応。
/// 引用符が閉じないまま EOF に達した場合のみ Ok=false。
/// 極端に大きい入力での OOM を避けるため、単一フィールド長・総セル数・総行数・総 chars 合計
/// の 4 次元に上限を設ける(通常業務利用の実測上限を大きく超える値)。超えた場合は Ok=false
/// でフォールバックする。総 chars 合計 (CSV-M-5) は 3 次元別上限を素通りする組合せ攻撃
/// (10M セル × 各 100KB 等) による .NET string allocator の OutOfMemoryException を防ぐ。
/// </summary>
public static class CsvParser
{
    // ---- OOM 防御ハードキャップ(通常業務利用の実測上限を大きく超える値) ----
    public const int MaxFieldChars = 8 * 1024 * 1024; // 単一フィールド 8M chars (~16MB UTF-16)
    public const int MaxTotalCells = 10_000_000;
    public const int MaxTotalRows = 1_000_000;
    public const long MaxTotalChars = 256L * 1024 * 1024; // 総 chars 合計 256M chars (~512MB UTF-16)

    // ParseLimits (テスト専用シーム): プロパティ名衝突は下記 pragma で抑止
#pragma warning disable S3218 // reason: 内側 record struct のプロパティ名が外側 CsvParser の public const と同名だが、テストが named argument で指定する契約=改名不可
    internal readonly record struct ParseLimits(
        int MaxFieldChars,
        int MaxTotalCells,
        int MaxTotalRows,
        long MaxTotalChars
    );
#pragma warning restore S3218

    private static readonly ParseLimits Default = new(
        MaxFieldChars,
        MaxTotalCells,
        MaxTotalRows,
        MaxTotalChars
    );

    public static CsvDocument Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        using var reader = new StringReader(text);
        return ParseCore(reader, Default);
    }

    /// <summary>
    /// TextSnapshot 全文をチャンク供給で読みながらパースする。全文 string 実体化を経由せず、
    /// 1GB 級 CSV でもピーク使用量が O(chunk + パース中間) に収まる。
    /// </summary>
    public static CsvDocument Parse(TextSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        using var reader = snapshot.CreateReader();
        return ParseCore(reader, Default);
    }

    /// <summary>上限違反経路を機械固定するためのテスト専用シーム。</summary>
    internal static CsvDocument ParseForTest(string text, ParseLimits limits)
    {
        using var reader = new StringReader(text);
        return ParseCore(reader, limits);
    }

    /// <summary>Parse(string) と Parse(TextSnapshot) の共通実装。挙動は
    /// 元の char index ベース実装と等価(text[i]→Read()、text[i+1]→Peek()、i+=2→Read() 追加消費)。</summary>
    private static CsvDocument ParseCore(TextReader reader, ParseLimits limits)
    {
        var rows = new List<IReadOnlyList<CsvField>>();
        var row = new List<CsvField>();
        var sb = new StringBuilder(); // 現在フィールドの論理値
        int pos = 0;
        int fieldStart = 0;
        bool inQuotes = false;
        bool ok = true;
        int totalCells = 0;
        long totalChars = 0; // CSV-M-5: 全フィールド sb.Length 総和 (3 次元別上限を素通りする組合せ攻撃対策)

        void EndField(int endExclusive)
        {
            row.Add(new CsvField(fieldStart, endExclusive - fieldStart, sb.ToString()));
            totalChars += sb.Length;
            if (totalChars > limits.MaxTotalChars)
                ok = false; // 呼び出し側で !ok を検知してループを break / EndRow をスキップ
            sb.Clear();
        }
        void EndRow()
        {
            rows.Add(row);
            row = new List<CsvField>();
        }

        int ci;
        while ((ci = reader.Read()) != -1)
        {
            char c = (char)ci;
            if (inQuotes)
            {
                if (c == '"')
                {
                    if (reader.Peek() == '"')
                    {
                        sb.Append('"');
                        if (sb.Length > limits.MaxFieldChars)
                        {
                            ok = false;
                            break;
                        }
                        reader.Read();
                        pos += 2;
                        continue;
                    }
                    inQuotes = false;
                    pos++;
                    continue; // 閉じ引用符
                }
                sb.Append(c);
                if (sb.Length > limits.MaxFieldChars)
                {
                    ok = false;
                    break;
                }
                pos++;
                continue; // 引用符内のカンマ・改行も literal
            }

            if (c == '"' && pos == fieldStart)
            {
                inQuotes = true;
                pos++;
                continue;
            } // 開き引用符（フィールド先頭のみ）
            if (c == ',')
            {
                EndField(pos);
                if (!ok)
                    break; // CSV-M-5: EndField 内で totalChars 超過→即 break
                totalCells++;
                if (totalCells > limits.MaxTotalCells)
                {
                    ok = false;
                    break;
                }
                pos++;
                fieldStart = pos;
                continue;
            }
            if (c == '\r' || c == '\n')
            {
                EndField(pos);
                if (!ok)
                    break; // CSV-M-5: EndField 内で totalChars 超過→即 break
                totalCells++;
                if (totalCells > limits.MaxTotalCells)
                {
                    ok = false;
                    break;
                }
                int lb = 1;
                if (c == '\r' && reader.Peek() == '\n')
                {
                    reader.Read();
                    lb = 2;
                }
                EndRow();
                if (rows.Count > limits.MaxTotalRows)
                {
                    ok = false;
                    break;
                }
                pos += lb;
                fieldStart = pos;
                continue;
            }
            sb.Append(c);
            if (sb.Length > limits.MaxFieldChars)
            {
                ok = false;
                break;
            }
            pos++; // 通常文字（閉じ引用符後の余剰文字も寛容に literal 扱い）
        }

        if (inQuotes)
            ok = false; // 引用符未終端

        // 末尾レコードの確定: 直近の改行後に内容が無い「末尾の空レコード」は捨てる。
        // 上限違反で break した場合は不完全な末尾レコードを rows に混ぜない。
        if (ok && (pos > fieldStart || row.Count > 0))
        {
            EndField(pos);
            if (ok) // CSV-M-5: 末尾 EndField で totalChars 超過→EndRow をスキップ(loop break と対称)
                EndRow();
        }

        return new CsvDocument(rows, ok);
    }
}
