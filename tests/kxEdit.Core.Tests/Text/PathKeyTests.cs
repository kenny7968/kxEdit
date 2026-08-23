using kxEdit.Core.Text;
using Xunit;

namespace kxEdit.Core.Tests.Text;

// ===== PathKey(Issue #48 / 設計書 §3.2)=====
// 契約は ForNormalized の 1 本だけ。ToLowerInvariant のみで、ファイルシステムに触れない。
//
// 生入力版 PathKey.For(内部で GetFullPath)は最終ブランチレビュー Q-I-2 で削除した
// (実消費者ゼロ・S-15 の凶器そのもの・経緯は PathKey の remarks)。
// それに伴い For 単独のテスト 5 本(大小同一視 / 区切り吸収 / `..` 畳み / 空 / CSV-L-8 の
// invalid→空文字)も消えている。大小同一視と空入力は下の ForNormalized 版が引き継ぎ、
// 「吸収する」側の 3 本は**もう仕様ではない**(吸収しないことを弁別テストが固定する)。
public class PathKeyTests
{
    [Fact]
    public void ForNormalized_lowercases_only() =>
        Assert.Equal(@"c:\temp\memo.txt", PathKey.ForNormalized(@"C:\Temp\Memo.TXT"));

    [Fact]
    public void ForNormalized_same_path_different_case_yields_same_key() =>
        Assert.Equal(
            PathKey.ForNormalized(@"C:\Temp\Memo.txt"),
            PathKey.ForNormalized(@"c:\temp\memo.TXT")
        );

    [Fact]
    public void ForNormalized_does_not_normalize_separators()
    {
        // ForNormalized は正規化しないので区切り差は別キーになる。
        // 呼出側が正規化済みパスを渡す契約(設計書 §3.1 の不変条件)を、ここで明文化して固定する。
        // このテストが無いと「ForNormalized の中で GetFullPath も呼ぶ」書き損じが
        // 全緑で通り、S-15 が丸ごと戻る。
        //
        // **対照群が load-bearing**: これが無いと NotEqual は「別々の文字列は別キー」という
        // 自明な主張に落ちる。主張の中身は「**同一ファイルを指す 2 綴り**なのに別キーになる」で、
        // その「同一ファイルを指す」の証人が対照群。以前は PathKey.For を証人に使っていたが、
        // 最終レビュー Q-I-2 で削除したので、For がやっていたこと(= GetFullPath)を
        // テスト内で直に呼んで役割を保つ。本番経路が GetFullPath を呼ばないことは
        // RecentFilesListTests / DocumentManagerTests / FileControllerTests の IL 網が見ている。
        Assert.Equal(
            System.IO.Path.GetFullPath(@"C:\Temp\a.txt"),
            System.IO.Path.GetFullPath("C:/Temp/a.txt")
        );
        Assert.NotEqual(
            PathKey.ForNormalized(@"C:\Temp\a.txt"),
            PathKey.ForNormalized("C:/Temp/a.txt")
        );
    }

    [Fact]
    public void ForNormalized_does_not_collapse_relative_segments()
    {
        // 同上の弁別(2 軸目)。`x\..` を畳まないことが GetFullPath 非経由の証拠になる。
        // 対照群の役割は上と同じ(同一ファイルを指す 2 綴りであることの証人)。
        Assert.Equal(
            System.IO.Path.GetFullPath(@"C:\Temp\b.txt"),
            System.IO.Path.GetFullPath(@"C:\Temp\x\..\b.txt")
        );
        Assert.NotEqual(
            PathKey.ForNormalized(@"C:\Temp\b.txt"),
            PathKey.ForNormalized(@"C:\Temp\x\..\b.txt")
        );
    }

    [Fact]
    public void ForNormalized_empty_returns_empty() =>
        Assert.Equal(string.Empty, PathKey.ForNormalized(""));

    [Fact]
    public void ForNormalized_does_not_touch_filesystem_for_invalid_input() =>
        // 埋め込み NUL のような正規化不能入力でも例外にならず、そのまま小文字化して返す。
        // 旧 PathKey.For はここで GetFullPath の例外を空文字へ落として「invalid はまとめて
        // 1 件」に集約していた(CSV-L-8)。ForNormalized は正規化しない=例外の出どころ自体が
        // 無いので、落とす対象も無い。この差が「ファイルシステムに触れない」ことの挙動側の証拠。
        Assert.Equal("a\0b", PathKey.ForNormalized("a\0b"));
}
