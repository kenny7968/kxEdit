// EditorControl.Uia.cs
// Phase 3 Task 3d 完了後: IUiaTextHost 全メンバ実装と Uia 系 12 field 所有権は UiaTextHostAdapter
// (_uia) へ完全移設済み。本ファイルには以下だけが残る:
//   - EditorControl の IUiaTextHost explicit interface 実装 (全て _uia への薄い delegation)
//   - Editor.Tests からの観測用 test hook forwarder (instance + static)
//   - §C.4 例外解消済: OnHandleCreated / OnHandleDestroyed は EditorControl.cs 本体側へ復帰済
//
// Phase 2 (Task 2d) 履歴: EditorControl 本体から Uia 系メンバを partial 分割。
// Phase 3 (Task 3d) 履歴: partial 分割から Adapter パターンへ格上げ (12 field 所有権移譲・
// §C.4 例外解消)。
using kxEdit.Accessibility;

namespace kxEdit.Editor;

public sealed partial class EditorControl
{
    // ==================== UIA テストフック (Editor.Tests から観測) ====================
    // Task 3d: 実データは UiaTextHostAdapter が保持=本体は adapter への薄い転送のみ。

    /// <summary>
    /// 論理行 segs 単一エントリキャッシュのヒット数 (Editor.Tests EditorControlCacheTests)。
    /// </summary>
    internal long TestHook_LastLineSegsHitCount => _uia.TestHook_LastLineSegsHitCount;

    /// <summary>
    /// 論理行 segs 単一エントリキャッシュのミス数 (Editor.Tests EditorControlCacheTests)。
    /// </summary>
    internal long TestHook_LastLineSegsMissCount => _uia.TestHook_LastLineSegsMissCount;

    /// <summary>segs キャッシュのヒット/ミスカウンタをリセット。</summary>
    internal void TestHook_ResetLastLineSegsCounters() => _uia.TestHook_ResetLastLineSegsCounters();

    // Task 6 テスト用フック: WndProc 経路と self-served 判定を Editor.Tests から観察する。
    internal static void TestHook_WndProc(EditorControl c, ref Message m) => c.WndProc(ref m);

    internal static bool TestHook_LastGetObjectServed(EditorControl c) =>
        c._uia.TestHook_LastGetObjectServed;

    // P5 Task 10/11: テスト用フック(Editor.Tests から _lastFrame を観察できるように)。
    // _lastFrame は Adapter 移譲対象外 (OnPaint 担当=EditorControl.cs 保持)。
    internal static kxEdit.Core.Layout.Frame? TestHook_GetLastFrame(EditorControl c) =>
        c._lastFrame;

    // ==================== P5 Task 8: UIA イベント発火配線 test hook ====================
    // TestHook_ForceUiaListen は AutomationInteropProvider.ClientsAreListening のバイパス
    // (Editor.Tests EditorControlUiaEventsTests / EditorControlUiaFocusEventTests から使用)。
    // Adapter の RaiseUia が本フラグを参照して発火判定する (元 EditorControl.Uia.cs の RaiseUia と同旨)。
    internal static bool TestHook_ForceUiaListen { get; set; }

    /// <summary>UIA イベントカウンタ (TextChanged/SelChanged/FocusChanged) をリセット。</summary>
    internal static void TestHook_ResetUiaEventCounts(EditorControl c) =>
        c._uia.ResetUiaEventCounts();

    /// <summary>UIA イベントカウンタ (TextChanged/SelChanged/FocusChanged) を返す。</summary>
    internal static (int textChanged, int selChanged, int focusChanged) TestHook_UiaEventCounts(
        EditorControl c
    ) => c._uia.UiaEventCounts;

    // ==================== IUiaTextHost 全メンバ (全て Adapter への薄い委譲) ====================
    // EditorControl 側で explicit interface implementation として残置する理由:
    //   - 既存 Editor.Tests (EditorControlUiaHostTests 305 行 / EditorControlBoundingRectsTests /
    //     EditorControlOffsetFromPointTests / EditorControlCacheTests 等) が `(IUiaTextHost)ctrl`
    //     でキャスト経由アクセスするため、public API 契約 (Control が IUiaTextHost を実装) を維持
    //   - Adapter 実装は internal のため直接キャストできない (`(IUiaTextHost)_uia` は internal クロージャ)
    // 実際のロジックは全て UiaTextHostAdapter 側 (bit-perfect 移設済)。

    string IUiaTextHost.GetTextRange(int start, int length) =>
        ((IUiaTextHost)_uia).GetTextRange(start, length);

    int IUiaTextHost.TextLength => ((IUiaTextHost)_uia).TextLength;

    (int Start, int End) IUiaTextHost.GetSelection() => ((IUiaTextHost)_uia).GetSelection();

    void IUiaTextHost.SetSelection(int start, int end) =>
        ((IUiaTextHost)_uia).SetSelection(start, end);

    int IUiaTextHost.NextChar(int offset) => ((IUiaTextHost)_uia).NextChar(offset);

    int IUiaTextHost.PrevChar(int offset) => ((IUiaTextHost)_uia).PrevChar(offset);

    int IUiaTextHost.LineStartOf(int offset) => ((IUiaTextHost)_uia).LineStartOf(offset);

    int IUiaTextHost.LineEndNoBreakOf(int offset) => ((IUiaTextHost)_uia).LineEndNoBreakOf(offset);

    int IUiaTextHost.LineEnd(int offset) => ((IUiaTextHost)_uia).LineEnd(offset);

    int IUiaTextHost.WordStart(int offset) => ((IUiaTextHost)_uia).WordStart(offset);

    int IUiaTextHost.WordEnd(int offset) => ((IUiaTextHost)_uia).WordEnd(offset);

    int IUiaTextHost.NextWordStart(int offset) => ((IUiaTextHost)_uia).NextWordStart(offset);

    int IUiaTextHost.PrevWordStart(int offset) => ((IUiaTextHost)_uia).PrevWordStart(offset);

    System.Windows.Rect IUiaTextHost.BoundingRectangle => ((IUiaTextHost)_uia).BoundingRectangle;

    double[] IUiaTextHost.GetBoundingRectangles(int start, int end) =>
        ((IUiaTextHost)_uia).GetBoundingRectangles(start, end);

    int IUiaTextHost.OffsetFromScreenPoint(double x, double y) =>
        ((IUiaTextHost)_uia).OffsetFromScreenPoint(x, y);

    // === スクロール / 可視域 (interface と adapter の節順に合わせる: 座標 → スクロール → 属性) ===

    void IUiaTextHost.ScrollRangeIntoView(int start, int end, bool alignToTop) =>
        ((IUiaTextHost)_uia).ScrollRangeIntoView(start, end, alignToTop);

    (int Start, int End) IUiaTextHost.GetVisibleRange() => ((IUiaTextHost)_uia).GetVisibleRange();

    nint IUiaTextHost.Handle => ((IUiaTextHost)_uia).Handle;
    bool IUiaTextHost.HasFocus => ((IUiaTextHost)_uia).HasFocus;
    int IUiaTextHost.ControlTypeId => ((IUiaTextHost)_uia).ControlTypeId;
    string IUiaTextHost.Name => ((IUiaTextHost)_uia).Name;
    string IUiaTextHost.AutomationId => ((IUiaTextHost)_uia).AutomationId;

    void IUiaTextHost.SetFocus() => ((IUiaTextHost)_uia).SetFocus();
}
