namespace kxEdit.Core.Session;

/// <summary>
/// タブ 1 個の永続表現。通常終了時に AppSettings.LastSession の一部として settings.json へ保存し、
/// 次回起動時に前回タブ列を復元する。無題タブの本文本体は BufferKey で LastSessionBuffersStore を参照する
/// (=大きな本文で settings.json が肥大化するのを避ける)。
/// CodePage / HasBom / LineEnding / WasModified は設計書 §8「dirty タブの静音復元」向けに追加
/// (レガシー settings.json との後方互換のため既定値付き; 0 = 未指定/レガシー)。
/// </summary>
public sealed record SessionTabRecord(
    string? Path,
    int UntitledNumber,
    string? BufferKey,
    bool IsActive,
    int CaretLine,
    int CaretColumn,
    int CodePage = 0,
    bool HasBom = false,
    int LineEnding = 0,
    bool WasModified = false
);
