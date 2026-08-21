using kxEdit.Core.Reading;
using Xunit;

namespace kxEdit.Core.Tests.Reading;

/// <summary>PositionFormatter の出力を固定する。
/// 2026-07-25 変更: 文書情報ダイアログ導入に伴い、位置照会から「文字数 M」「選択 K 文字」を削除
/// (文字数の詳細は [ファイル]&gt;文書情報 へ集約。設計 2026-07-25 §0)。</summary>
public class PositionFormatterTests
{
    [Fact]
    public void Formats_line_total_and_column() =>
        Assert.Equal(
            "行 12 / 全 340、桁 5",
            PositionFormatter.Format(line: 12, totalLines: 340, column: 5)
        );

    [Fact]
    public void Appends_overtype_when_set() =>
        Assert.Equal(
            "行 1 / 全 1、桁 1、上書き",
            PositionFormatter.Format(1, 1, 1, overtype: true)
        );

    /// <summary>overtype 既定(false)では「、上書き」を付けない(挿入モードの pin)。</summary>
    [Fact]
    public void Omits_overtype_by_default() =>
        Assert.Equal("行 3 / 全 9、桁 2", PositionFormatter.Format(3, 9, 2));
}
