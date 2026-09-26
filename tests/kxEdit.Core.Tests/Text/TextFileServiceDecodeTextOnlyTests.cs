using System.Text;
using kxEdit.Core.Text;
using Xunit;

namespace kxEdit.Core.Tests.Text;

/// <summary>
/// フェーズ 8(perf-grep): grep 用の復号 <see cref="TextFileService.DecodeTextOnly"/> が、
/// 従来 grep が使っていた <c>DecodeBytes(bytes, codePage).Text</c> と 1 文字も違わないこと。
/// </summary>
public class TextFileServiceDecodeTextOnlyTests
{
    public static TheoryData<int, byte[]> Cases()
    {
        EncodingCatalog.EnsureRegistered();
        var data = new TheoryData<int, byte[]>();
        byte[] bom = { 0xEF, 0xBB, 0xBF };
        string ja = "一行目\r\nあいう𠀀TARGET\n三行目\r末尾";
        byte[] utf8 = Encoding.UTF8.GetBytes(ja);
        data.Add(65001, utf8);
        data.Add(65001, bom.Concat(utf8).ToArray()); // BOM は剥がす
        data.Add(65001, bom); // BOM だけ
        data.Add(65001, Array.Empty<byte>());
        data.Add(65001, new byte[] { 0x41, 0xE3, 0x81, 0x0A, 0xFF, 0x42 }); // 不正列 → U+FFFD
        data.Add(932, EncodingCatalog.Get(932).GetBytes(ja.Replace("𠀀", "")));
        data.Add(932, bom.Concat(Encoding.ASCII.GetBytes("abc")).ToArray()); // SJIS に BOM はない=剥がさない
        data.Add(932, new byte[] { 0x82 }); // 途中で切れた 2 バイト文字
        data.Add(51932, EncodingCatalog.Get(51932).GetBytes(ja.Replace("𠀀", "")));
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void DecodeTextOnly_equals_DecodeBytes_text(int codePage, byte[] bytes)
    {
        string expected = TextFileService.DecodeBytes(bytes, codePage).Text;
        Assert.Equal(expected, TextFileService.DecodeTextOnly(bytes, codePage));
    }

    [Fact]
    public void DecodeTextOnly_equals_DecodeBytes_text_for_random_bytes()
    {
        var rng = new Random(20260926);
        foreach (int cp in new[] { 65001, 932, 51932 })
        {
            for (int n = 0; n < 200; n++)
            {
                var bytes = new byte[rng.Next(0, 64)];
                rng.NextBytes(bytes);
                if (n % 4 == 0 && bytes.Length >= 3)
                {
                    bytes[0] = 0xEF;
                    bytes[1] = 0xBB;
                    bytes[2] = 0xBF;
                }
                Assert.Equal(
                    TextFileService.DecodeBytes(bytes, cp).Text,
                    TextFileService.DecodeTextOnly(bytes, cp)
                );
            }
        }
    }
}
