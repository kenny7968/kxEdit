namespace kxEdit.Core.Text;

/// <summary>アセンブリのバージョン属性を表示用の文字列へ整形する。</summary>
public static class VersionText
{
    /// <summary>
    /// <c>AssemblyInformationalVersion</c> から表示用のバージョンを取り出す。
    /// </summary>
    /// <remarks>
    /// .NET 8 以降は <c>IncludeSourceRevisionInInformationalVersion</c> が既定 true のため、
    /// 属性値は <c>0.2.0+&lt;commit sha&gt;</c> の形になる。そのまま表示すると
    /// 40 桁の hash が読み上げられてしまうので、最初の <c>+</c> 以降を落とす。
    /// プレリリース識別子(<c>0.2.0-rc.1</c>)は <c>+</c> より前にあるため保たれる。
    /// </remarks>
    /// <param name="informationalVersion">属性値。null / 空を許容する。</param>
    /// <returns>整形後のバージョン。取り出せないときは空文字。</returns>
    public static string FromInformationalVersion(string? informationalVersion)
    {
        if (string.IsNullOrWhiteSpace(informationalVersion))
        {
            return string.Empty;
        }

        string trimmed = informationalVersion.Trim();
        int plus = trimmed.IndexOf('+');
        return plus >= 0 ? trimmed[..plus] : trimmed;
    }
}
