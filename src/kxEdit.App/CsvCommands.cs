using kxEdit.Core.Csv;

namespace kxEdit.App;

/// <summary>
/// CSVモードの素キーコマンド表（ProcessCmdKey の横取り用）の単一の真実源。
/// メニューには出さないキー専用コマンド群（キー一覧は将来のヘルプに記載する）。
/// </summary>
internal static class CsvCommands
{
    /// <summary>ProcessCmdKey 用のキー→コマンド表。
    /// Add 形式の初期化子はキー重複時に静的初期化で例外になる（重複の黙殺防止）。</summary>
    internal static readonly IReadOnlyDictionary<Keys, Action<CsvController>> ByKey =
        new Dictionary<Keys, Action<CsvController>>
        {
            // 隣接セルへの移動
            { Keys.Up, c => c.Move(Direction.Up) }, // 上のセル
            { Keys.Down, c => c.Move(Direction.Down) }, // 下のセル
            { Keys.Left, c => c.Move(Direction.Left) }, // 左のセル
            { Keys.Right, c => c.Move(Direction.Right) }, // 右のセル
            // 読み上げのみ（移動なし）
            { Keys.Tab, c => c.ReadCurrent() }, // 現在セルを読み上げ
            { Keys.C, c => c.ReadColumnTop() }, // 列の見出しを読み上げ
            { Keys.R, c => c.ReadRowHead() }, // 行の見出しを読み上げ
            // 行/列の端へのジャンプ
            { Keys.Home, c => c.MoveRowStart() }, // 行頭へ
            { Keys.End, c => c.MoveRowEnd() }, // 行末へ
            { Keys.PageUp, c => c.MoveColumnTop() }, // 列頭へ
            { Keys.PageDown, c => c.MoveColumnBottom() }, // 列末へ
            { Keys.Control | Keys.Home, c => c.MoveTopLeft() }, // 左上へ
            { Keys.Control | Keys.End, c => c.MoveBottomRight() }, // 右下へ
            // セル指定・編集
            { Keys.G, c => c.GoToCell() }, // セルへ移動
            { Keys.F2, c => c.BeginEdit() }, // セルを編集
            // 別名・ガード
            { Keys.Shift | Keys.Tab, c => c.ReadCurrent() }, // Shift+Tab でフォーカスがシンクから逃げるのを防ぐ
            { Keys.Control | Keys.G, c => c.GoToCell() }, // 行ジャンプはCSVモード中セル指定に読み替え（素通りするとキャレット移動＋生読みを誘発）
        };
}
