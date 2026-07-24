using System.Text.Encodings.Web;
using System.Text.Json;

namespace yEdit.Core.Session;

/// <summary>
/// レガシー移行(PR #22 形式からの一回限り読み替え)の Load/Delete 専用。
/// 旧形式=タブ本文を BufferKey→本文 のマップとして保存した単一 JSON ファイル。
/// 破損時は空 Dict を返し呼び出し側が graceful degradation で continue する。
/// 次リリースで完全削除予定(設計 2026-07-23 統合 §8/§9)。
/// </summary>
public static class LastSessionBuffersStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        // 歴史的経緯(旧 I-1): 旧書込側は非 ASCII を \uXXXX に展開せず生 UTF-8 で書き出していた。
        // Save 退役後(Task 7)の本クラスは Load/Delete 専用だが、Deserialize は生 UTF-8 と
        // \uXXXX の両方を等しく読めるためレガシーファイルの読取互換に影響しない。
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// Load 時のファイルサイズ上限(bytes)。旧書込側 cap(1M chars/tab・最大 10 タブ級で
    /// 20 MB 前後の想定)を引き継いだ 32 MB。この上限を超える入力は stale/攻撃 JSON と見なし、
    /// Load は empty を返して Trace.TraceWarning する(BackupCoordinator BK-M-3 と対称の防御)。
    /// </summary>
    internal const long MaxLoadFileSizeBytes = 32L * 1024 * 1024;

    /// <summary>%APPDATA%\yEdit\last-session-buffers.json(SettingsStore.DefaultPath と同ディレクトリ)。</summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "yEdit",
            "last-session-buffers.json"
        );

    public static IReadOnlyDictionary<string, string> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new Dictionary<string, string>();
            // 事前 size cap(§Load-size-cap): 32 MB を超えるファイルは stale/攻撃入力と見なし
            // 読まずに empty で返す(BackupCoordinator の BK-M-3 と対称の防御=書込側キャップと二重に張る)。
            var info = new FileInfo(path);
            if (info.Length > MaxLoadFileSizeBytes)
            {
                System.Diagnostics.Trace.TraceWarning(
                    "yEdit: last-session-buffers.json too large ({0} bytes); ignoring.",
                    info.Length
                );
                return new Dictionary<string, string>();
            }
            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json))
                return new Dictionary<string, string>();
            var map = JsonSerializer.Deserialize<Dictionary<string, string?>>(json, Options);
            if (map is null)
                return new Dictionary<string, string>();
            // 値が明示 null のエントリは skip(復元契約=キー欠落と同じ扱い=空タブを追加しない)
            var result = new Dictionary<string, string>(map.Count);
            foreach (var kvp in map.Where(kvp => kvp.Value is not null))
                result[kvp.Key] = kvp.Value!;
            return result;
        }
        catch
        {
            return new Dictionary<string, string>();
        }
    }

    public static void Delete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // 削除失敗は致命でない ― 本ストアは移行読取専用(書込なし)であり、残骸の掃除は
            // 次回の移行パス(ON 起動時)または OFF 終了時のクリーンアップで再試行される。
        }
    }
}
