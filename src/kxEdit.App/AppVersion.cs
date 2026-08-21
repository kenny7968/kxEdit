using System.Reflection;
using kxEdit.Core.Text;

namespace kxEdit.App;

/// <summary>バージョン情報ダイアログに出す文字列を組み立てる。</summary>
/// <remarks>
/// バージョンの管理点は <c>Directory.Build.props</c> の <c>&lt;Version&gt;</c> 一箇所。
/// リリースビルドでは <c>release.yml</c> が <c>-p:Version</c> でタグの値に上書きするため、
/// 配布物の表示はタグへ自動的に追随する。
/// <para>
/// アプリ名は <c>AssemblyProduct</c> から読まず literal のまま持つ。この文字列は
/// <c>%AppData%\kxEdit\</c> のフォルダ名にも使われており、アセンブリ属性依存にすると
/// プロジェクト名の変更やビルド構成の差でユーザーデータの場所が黙って移動しうる。
/// </para>
/// </remarks>
internal static class AppVersion
{
    private const string AppName = "kxEdit";

    /// <summary>バージョン情報ダイアログの表示文字列。</summary>
    internal static string DisplayText => Compose(ReadInformationalVersion());

    /// <summary>属性値から表示文字列を作る。属性が無い/空なら名前だけを返す。</summary>
    internal static string Compose(string? informationalVersion)
    {
        string version = VersionText.FromInformationalVersion(informationalVersion);
        return version.Length == 0 ? AppName : $"{AppName} v{version}";
    }

    private static string? ReadInformationalVersion() =>
        typeof(AppVersion)
            .Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;
}
