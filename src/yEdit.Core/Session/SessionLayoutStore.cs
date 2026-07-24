using System.Text.Encodings.Web;
using System.Text.Json;
using yEdit.Core.Backup;
using yEdit.Core.Text;

namespace yEdit.Core.Session;

/// <summary>
/// session-state.json(タブレイアウトの定期スナップショット)の Load/Save/Delete。
/// 外部入力(改竄可能)として扱い、Load 時に Normalize で防御する(設計 §2.3)。
/// 書込は AtomicFile(temp→Replace)。単一ファイル・last-writer-wins(複数インスタンスは M9+)。
/// </summary>
public static class SessionLayoutStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, // 日本語パスを生 UTF-8 で
    };

    /// <summary>タブ数上限(設計 §2.3)。超過は先頭 MaxTabs 件で切り詰め+Trace 警告。
    /// FileController.RestoreSession の extras 復元上限も同じ定数を参照する(対称防御・二重定義回避)。</summary>
    public const int MaxTabs = 200;

    /// <summary>Load 時 size cap。レイアウトは本文を含まない(数 KB 想定)ため 4 MB で攻撃 JSON を遮断。</summary>
    internal const long MaxLoadFileSizeBytes = 4L * 1024 * 1024;

    /// <summary>%APPDATA%\yEdit\session-state.json(SettingsStore.DefaultPath と同ディレクトリ)。</summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "yEdit",
            "session-state.json"
        );

    public static SessionLayout? Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;
            var info = new FileInfo(path);
            if (info.Length > MaxLoadFileSizeBytes)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "yEdit: session-state.json too large ({0} bytes); ignoring.",
                    info.Length
                );
                return null;
            }
            var raw = JsonSerializer.Deserialize<SessionLayout>(File.ReadAllText(path), Options);
            return raw is null ? null : Normalize(raw);
        }
        catch (Exception ex)
        {
            // 最終品質パス M-3: size cap 超過はトレースするのに破損 JSON(E5'=タブ順の silent 消失)は
            // 無音、の非対称を解消する。例外型名のみ載せる(攻撃者制御のメッセージ文字列は載せない)。
            System.Diagnostics.Trace.TraceWarning(
                "yEdit: session-state.json unreadable ({0}); ignoring.",
                ex.GetType().Name
            );
            return null; // 破損=レイアウトなし扱い(E5'。extras 復元は呼び出し側で継続)
        }
    }

    /// <summary>設計 §2.3 の防御的補正。Load 経路専用(Save 側は自プロセス生成値=補正不要)。</summary>
    internal static SessionLayout Normalize(SessionLayout raw)
    {
        var source = raw.Tabs ?? new List<SessionLayoutRecord>();
        var cleaned = new List<SessionLayoutRecord>(Math.Min(source.Count, MaxTabs));
        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        bool activeSeen = false;
        foreach (var t in source)
        {
            if (cleaned.Count >= MaxTabs)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "yEdit: session-layout-tabs-capped ({0} -> {1})",
                    source.Count,
                    MaxTabs
                );
                break;
            }
            if (t is null)
                continue;
            if (t.Path is not null && string.IsNullOrWhiteSpace(t.Path))
                continue;
            string? backupId = t.BackupId;
            if (backupId is not null && !BackupIdValidator.IsValid(backupId))
            {
                // 設計 §2.3: 不正 Id は null 化+トレース(MaxTabs 切り詰めと対称)。生 Id は
                // 攻撃者制御下(改行=ログ行偽装・BiDi・長大文字列)のため OneLine で無害化して載せる。
                System.Diagnostics.Trace.TraceWarning(
                    "yEdit: session-layout-invalid-backup-id ({0})",
                    SanitizeForDisplay.OneLine(backupId, 200)
                );
                backupId = null; // 不正 Id はパストラバーサル痕跡の可能性 → 参照ごと捨てる
            }
            if (backupId is not null && !seenIds.Add(backupId))
                backupId = null; // 1 バックアップ 1 タブの不変(重複参照は 2 個目以降を demote)
            bool isActive = t.IsActive && !activeSeen;
            if (isActive)
                activeSeen = true;
            cleaned.Add(
                t with
                {
                    BackupId = backupId,
                    IsActive = isActive,
                    UntitledNumber = Math.Max(0, t.UntitledNumber),
                    CaretLine = Math.Max(0, t.CaretLine),
                    CaretColumn = Math.Max(0, t.CaretColumn),
                    LineEnding = Math.Max(0, t.LineEnding),
                }
            );
        }
        return new SessionLayout(cleaned, raw.SavedAtUtc);
    }

    public static void Save(string path, SessionLayout layout)
    {
        // Save は例外を握らない=呼び出し側(SerialBackupWriter のジョブ catch)が受ける契約
        // (BackupStore.Write と対称)。
        string dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        IO.AtomicFile.Write(path, JsonSerializer.SerializeToUtf8Bytes(layout, Options));
    }

    public static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        { /* 削除失敗は無害(次回上書き) */
        }
    }
}
