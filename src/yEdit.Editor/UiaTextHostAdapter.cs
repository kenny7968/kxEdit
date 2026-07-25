// UiaTextHostAdapter.cs
// Phase 3 Task 3d で EditorControl.Uia.cs から IUiaTextHost 22 メンバ + Uia 系 12 field の
// 所有権を bit-perfect 移設した adapter。§C.4 例外 (OnHandle*/On*Changed の Uia.cs 帰属)
// を解消し、EditorControl 本体側の OnHandle*/On*Changed から Adapter へ通知する形に統一する。
//
// 責務:
//   - Uia 系 12 field の単一所有 (_bufferSnapshot / _bounds / _boundsSync /
//     _clientToScreenX / _clientToScreenY / _lastLineSegs / _hwnd / _provider /
//     _testHook_LastGetObjectServed / _uiaTextChangedCount / _uiaSelectionChangedCount /
//     _uiaFocusChangedCount)
//   - IUiaTextHost 全メンバの実装 (RPC スレッドから呼ばれ得る=不変スナップショット参照 +
//     キャッシュ値応答。SetSelection / SetFocus / ScrollRangeIntoView のみ UI スレッドへ Invoke)
//   - UI スレッド側からの通知経路: OnSnapshotChanged / OnBoundsChanged /
//     OnHandleCreated / OnHandleDestroyed / RaiseTextChanged / RaiseSelectionChanged /
//     RaiseFocusChanged / EnsureProvider
//   - UIA プロバイダ (TextControlProviderV2) の lazy 生成
//
// 非責務 (host 側に残す):
//   - ComputeCaretPoint (UI スレッド専用状態 _topLine / _scrollX 等を参照) = host.ComputeCaretPoint 経由
//   - OffsetFromClientPoint (同上) = host.OffsetFromClientPoint 経由
//   - SetSelectionCharRange / ScrollCharRangeIntoView / Focus (UI スレッド専用の caret / 選択 /
//     スクロール操作) = host 経由
//
// Task 3b Obs 4 復旧: GetSelection は _caret.Caret / _caret.Anchor を local capture して
// Min/Max を計算する (2 field read 窓)。CaretController.Selection 経由だと 4 field read
// (Math.Min/Max それぞれで 2 回) になり torn-read 窓が拡張されるため。
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Provider;
using System.Windows.Forms;
using yEdit.Accessibility;
using yEdit.Core.Buffers;
using yEdit.Core.Layout;

namespace yEdit.Editor;

// UIA-L-2 (PR-G Task 5): sealed を外す=RaiseUia の PerformRaiseAutomationEvent seam を
// テスト subclass から override して「AutomationInteropProvider.RaiseAutomationEvent が例外を
// 投げたときに _trace に落ちる」経路を deterministically に駆動できるようにするため。
// internal は維持=外部 assembly から派生不可・InternalsVisibleTo (Editor.Tests) 経由のみ。
internal class UiaTextHostAdapter : IUiaTextHost
{
    // === 依存 (readonly = 差替不可) ===
    private readonly EditorControl _host;
    private readonly CaretController _caret;

    // UIA-L-2: AutomationInteropProvider.RaiseAutomationEvent 失敗の観測用 trace sink。
    // 既定 null で silent 継続=本番挙動は不変 (視覚のみに縮退)。
    private readonly IUiaTraceSink? _trace;

    // === 12 field: Uia 系 state の単一所有 (Task 3d) ===

    // P5 Task 5: UIA v2 用 RPC スレッド安全キャッシュ。
    // _bufferSnapshot は不変(TextSnapshot は immutable)なので UI スレッドで参照を差し替えるだけで
    // RPC スレッドは自己整合なスナップショットを読める。編集経路(SetSource/AfterEdit)で更新する。
    // _bounds は WPF Rect のためロック越しで読み書き(参照差替不可の struct)。
    private volatile TextSnapshot? _bufferSnapshot;
    private readonly object _boundsSync = new();
    private System.Windows.Rect _bounds;

    // P8 Minor-5: SR の Line 単位連続読み(LineStartOf/LineEndNoBreakOf/LineEnd)で
    // 同一 (snap, logicalLine, wrap) が繰り返されるため単一エントリキャッシュ。
    // UI スレッド上でのみ更新される(TryFindVisualSegmentCore は Invoke マーシャリング後)。
    // 無効化ポイント: OnSnapshotChanged / InvalidateLastLineSegs (WrapColumns setter /
    // ApplyAppearance から)。
    // 設計原則: _bufferSnapshot を更新するすべての経路で _lastLineSegs も破棄する
    // (correctness は ReferenceEquals(c.Snap) 判定で守られるが、旧 TextSnapshot の Root
    //  PieceTree が強参照で pin されて大容量ファイル差替後の GC を阻害するため)。
    private (TextSnapshot Snap, int Line, int Wrap, IReadOnlyList<WrapSegment> Segs)? _lastLineSegs;

    // P5 Task 10: client→screen オフセットキャッシュ (座標 API 用)。
    // OnPaint / OnBoundsChanged で更新した client 原点のスクリーン座標。
    private int _clientToScreenX,
        _clientToScreenY;

    // P5 Task 14 (I-2): UIA プロバイダは RPC スレッドから Handle を取得する。Control.Handle は
    // Handle 未生成時に CreateHandle を誘発し得るため、OnHandleCreated で捕捉した値をキャッシュ。
    // v1 ScintillaHost._hwnd と同形。
    private nint _hwnd;

    // P5 Task 6: UIA プロバイダ(v2)は WM_GETOBJECT(UiaRootObjectId)で lazy 生成する。
    // インスタンスの寿命は EditorControl と同じ(Dispose で解放不要=マネージ参照のみ)。
    private TextControlProviderV2? _provider;

    // Task 6 テスト用フック: WndProc 経路と self-served 判定を Editor.Tests から観察する。
    private bool _testHook_LastGetObjectServed;

    // P5 Task 8: UIA イベント発火カウンタ (Editor.Tests から観測)。
    private int _uiaTextChangedCount,
        _uiaSelectionChangedCount,
        _uiaFocusChangedCount;

    // === ctor ===

    /// <summary>
    /// UIA-L-2: <paramref name="trace"/>=null で silent 継続 (本番挙動不変)。テストは Fake
    /// <see cref="IUiaTraceSink"/> を注入して UIA raise 失敗の観測を検証する。
    /// </summary>
    public UiaTextHostAdapter(
        EditorControl host,
        CaretController caret,
        IUiaTraceSink? trace = null
    )
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _caret = caret ?? throw new ArgumentNullException(nameof(caret));
        _trace = trace;
    }

    // === UI thread 側からの通知経路 ===

    /// <summary>
    /// バッファスナップショット更新 (SetSource / ReplaceSource / AfterEdit から呼ばれる)。
    /// _bufferSnapshot 差替と _lastLineSegs 破棄を 1 経路に集約 (元 CacheSnapshot + 6 箇所の
    /// `_lastLineSegs = null` の統一)。null 渡しはガードなし=呼び出し側で保証。
    /// </summary>
    public void OnSnapshotChanged(TextSnapshot newSnap)
    {
        _bufferSnapshot = newSnap;
        _lastLineSegs = null;
    }

    /// <summary>
    /// _lastLineSegs キャッシュのみ破棄する (WrapColumns setter / ApplyAppearance で metrics/wrap
    /// が変化したとき用)。_bufferSnapshot は変更しない (バッファ本体は不変)。
    /// </summary>
    public void InvalidateLastLineSegs()
    {
        _lastLineSegs = null;
    }

    /// <summary>
    /// bounds キャッシュ更新 (OnHandleCreated / OnSizeChanged / OnLocationChanged から)。
    /// hwnd == 0 (Handle 破棄済) や Handle 未生成では早期 return (元 UpdateBoundsCache の
    /// IsHandleCreated ガード相当)。lock 越しで _bounds を書き、_clientToScreenX/Y も更新する。
    /// </summary>
    public void OnBoundsChanged()
    {
        if (!_host.IsHandleCreated)
            return;
        var r = _host.RectangleToScreen(_host.ClientRectangle);
        lock (_boundsSync)
            _bounds = new System.Windows.Rect(r.Left, r.Top, r.Width, r.Height);
        // P5 Task 10: client→screen オフセットも同時に更新
        var origin = _host.PointToScreen(new System.Drawing.Point(0, 0));
        _clientToScreenX = origin.X;
        _clientToScreenY = origin.Y;
    }

    /// <summary>
    /// OnPaint 末尾からの client→screen オフセット refresh。DPI 変化・親コントロール移動などで
    /// スクロールなしでも値が変わり得る (元 EditorControl.Paint.cs 末尾のコード)。
    /// </summary>
    /// <remarks>
    /// Task 3d fixup (FIX-2): 元コード (EditorControl.Paint.cs OnPaint 末尾 2 行代入) は
    /// IsHandleCreated guard を持たなかったため、guard を削除して bit-perfect に戻す。
    /// 呼び出し元 (OnPaint) は Handle 生成後にしか動かない = WinForms 保証で実挙動影響なし。
    /// </remarks>
    public void RefreshClientToScreenOrigin()
    {
        var origin = _host.PointToScreen(new System.Drawing.Point(0, 0));
        _clientToScreenX = origin.X;
        _clientToScreenY = origin.Y;
    }

    /// <summary>
    /// Handle 生成通知 (EditorControl.OnHandleCreated から)。_hwnd キャッシュ + 初期 bounds 計算。
    /// </summary>
    public void OnHandleCreated()
    {
        _hwnd = _host.Handle; // P5 Task 14 (I-2): RPC スレッドが安全に読める hwnd キャッシュ
        OnBoundsChanged();
    }

    /// <summary>
    /// Handle 破棄通知 (EditorControl.OnHandleDestroyed から)。_hwnd を Zero に戻す
    /// (元コードは _provider を触らないため、こちらも触らない=bit-perfect)。
    /// </summary>
    public void OnHandleDestroyed()
    {
        _hwnd = IntPtr.Zero;
    }

    /// <summary>
    /// WM_GETOBJECT (UiaRootObjectId) から呼ばれる: TextControlProviderV2 を lazy 生成し、
    /// AutomationInteropProvider.ReturnRawElementProvider で応答する。self-served フラグも立てる。
    /// </summary>
    /// <remarks>
    /// Task 3d fixup (FIX-1): _testHook_LastGetObjectServed = true は ReturnRawElementProvider
    /// 呼び出しの後に置く (元 EditorControl.cs bit-perfect)。ReturnRawElementProvider が例外を
    /// 投げた場合の flag 値を「false のまま」に保つ = 元コード契約と一致させるため。
    /// </remarks>
    public IntPtr HandleWmGetObject(nint controlHandle, IntPtr wParam, IntPtr lParam)
    {
        _provider ??= new TextControlProviderV2(this);
        var result = AutomationInteropProvider.ReturnRawElementProvider(
            controlHandle,
            wParam,
            lParam,
            _provider
        );
        _testHook_LastGetObjectServed = true;
        return result;
    }

    /// <summary>WM_GETOBJECT の non-UiaRootObjectId 経路: self-served フラグを false に落とす。</summary>
    public void MarkGetObjectNotServed()
    {
        _testHook_LastGetObjectServed = false;
    }

    /// <summary>UIA TextChangedEvent 発火 (元 RaiseUia(TextChangedEvent))。</summary>
    public void RaiseTextChanged() => RaiseUia(TextPatternIdentifiers.TextChangedEvent);

    /// <summary>UIA TextSelectionChangedEvent 発火 (元 RaiseUia(TextSelectionChangedEvent))。</summary>
    public void RaiseSelectionChanged() =>
        RaiseUia(TextPatternIdentifiers.TextSelectionChangedEvent);

    /// <summary>UIA AutomationFocusChangedEvent 発火 (元 RaiseUia(AutomationFocusChangedEvent))。</summary>
    public void RaiseFocusChanged() =>
        RaiseUia(AutomationElementIdentifiers.AutomationFocusChangedEvent);

    /// <summary>UIA イベントを発火する共通ヘルパ。プロバイダ未生成・SR 未リッスン時はスキップ。</summary>
    /// <remarks>
    /// 元 EditorControl.Uia.cs の RaiseUia を bit-perfect 移設。
    /// UIA-L-2 (PR-G Task 5): 元 <c>catch { }</c> の握りつぶしを <see cref="_trace"/> 経由の可観測化に差替。
    /// 本体に影響させない silent 継続の契約は維持しつつ、trace があれば失敗を "raise-automation-event"
    /// カテゴリで通知する (SR キューの詰まり・UIA サーバ側 race を運用側で観察可能に)。
    /// </remarks>
    private void RaiseUia(AutomationEvent ev)
    {
        if (_provider is null)
            return;
        if (
            !EditorControl.TestHook_ForceUiaListen && !AutomationInteropProvider.ClientsAreListening
        )
            return;
        try
        {
            PerformRaiseAutomationEvent(ev, _provider, new AutomationEventArgs(ev));
            if (ev == TextPatternIdentifiers.TextChangedEvent)
                _uiaTextChangedCount++;
            else if (ev == TextPatternIdentifiers.TextSelectionChangedEvent)
                _uiaSelectionChangedCount++;
            else if (ev == AutomationElementIdentifiers.AutomationFocusChangedEvent)
                _uiaFocusChangedCount++;
        }
        catch (Exception ex)
        {
            // UIA-L-2: UIA サーバ側の失敗は本体に影響させない=trace で観測可能にしつつ silent 継続。
            // I-1 fixup: trace sink 自身 (Debug/Trace listener I/O・formatting 等) が投げても
            //            本体 (編集経路) には影響させない=元 `catch { }` の握りつぶし契約を維持する。
            try
            {
                _trace?.Warn(
                    "raise-automation-event",
                    ev?.ProgrammaticName
                        ?? ev?.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)
                        ?? "unknown",
                    ex
                );
            }
            catch
            { /* trace sink 自身が投げても本体には影響させない (UIA-L-2 I-1 fixup) */
            }
        }
    }

    /// <summary>UIA-L-2: <see cref="RaiseUia"/> の try 内で走る「実際の RaiseAutomationEvent 呼出」の seam。
    /// テストではこれを override して「RaiseUia の catch/trace 経路」を deterministically に駆動する
    /// (Windows UIA インフラは opaque なため本物の失敗を作れない)。</summary>
    protected internal virtual void PerformRaiseAutomationEvent(
        AutomationEvent ev,
        IRawElementProviderSimple provider,
        AutomationEventArgs args
    )
    {
        AutomationInteropProvider.RaiseAutomationEvent(ev, provider, args);
    }

    /// <summary>event counters の一括リセット (Test hook 経由)。</summary>
    public void ResetUiaEventCounts()
    {
        _uiaTextChangedCount = _uiaSelectionChangedCount = _uiaFocusChangedCount = 0;
    }

    /// <summary>UIA event counters の read (Test hook 経由)。</summary>
    public (int TextChanged, int SelChanged, int FocusChanged) UiaEventCounts =>
        (_uiaTextChangedCount, _uiaSelectionChangedCount, _uiaFocusChangedCount);

    /// <summary>_testHook_LastGetObjectServed の read (Test hook 経由)。</summary>
    public bool TestHook_LastGetObjectServed => _testHook_LastGetObjectServed;

    // === IUiaTextHost 全メンバ実装 (RPC thread から呼ばれ得る) ===
    // 元 EditorControl.Uia.cs から bit-perfect 移設 (field 参照だけ「自分の field」へ付け替え・
    // Compute* / OffsetFromClientPoint / SetSelectionCharRange / Focus は _host へ委譲)。

    string IUiaTextHost.GetTextRange(int start, int length)
    {
        var snap = _bufferSnapshot;
        if (snap is null)
            return "";
        int s = Math.Clamp(start, 0, snap.CharLength);
        int l = Math.Clamp(length, 0, snap.CharLength - s);
        return snap.GetText(s, l);
    }

    int IUiaTextHost.TextLength => _bufferSnapshot?.CharLength ?? 0;

    (int Start, int End) IUiaTextHost.GetSelection()
    {
        // Task 3b Obs 4 fixup: torn-read 窓拡張の復旧。
        // 元コードは `int c = _caret, a = _anchor;` の 2 field 読み → local Min/Max だった。
        // CaretController.Selection は 4 field 読み (Math.Min/Max それぞれ 2 回) で窓が拡張されるため、
        // Adapter で local capture して 2 field 読みに戻す (RPC スレッド安全性の復旧)。
        int c = _caret.Caret;
        int a = _caret.Anchor;
        return (Math.Min(c, a), Math.Max(c, a));
    }

    void IUiaTextHost.SetSelection(int start, int end)
    {
        // P5 Task 14 (I-3): 破棄後 / Handle 未生成での BeginInvoke による InvalidOperationException を防ぐ
        if (_host.IsDisposed || !_host.IsHandleCreated)
            return;
        if (_host.InvokeRequired)
        {
            _host.BeginInvoke(new Action(() => ((IUiaTextHost)this).SetSelection(start, end)));
            return;
        }
        _host.SetSelectionCharRange(start, end);
    }

    int IUiaTextHost.NextChar(int offset)
    {
        var snap = _bufferSnapshot;
        if (snap is null)
            return 0;
        int o = Math.Clamp(offset, 0, snap.CharLength);
        if (o >= snap.CharLength)
            return snap.CharLength;
        char c = snap.GetChar(o);
        if (
            char.IsHighSurrogate(c)
            && o + 1 < snap.CharLength
            && char.IsLowSurrogate(snap.GetChar(o + 1))
        )
            return o + 2;
        // CRLF pair (2026-07-24: サロゲート atomic と対称=CR と LF の間にキャレットを立てない)
        if (c == '\r' && o + 1 < snap.CharLength && snap.GetChar(o + 1) == '\n')
            return o + 2;
        return o + 1;
    }

    int IUiaTextHost.PrevChar(int offset)
    {
        var snap = _bufferSnapshot;
        if (snap is null)
            return 0;
        int o = Math.Clamp(offset, 0, snap.CharLength);
        if (o <= 0)
            return 0;
        if (
            char.IsLowSurrogate(snap.GetChar(o - 1))
            && o - 2 >= 0
            && char.IsHighSurrogate(snap.GetChar(o - 2))
        )
            return o - 2;
        // CRLF pair (2026-07-24: サロゲート atomic と対称=CR と LF の間にキャレットを立てない)
        if (snap.GetChar(o - 1) == '\n' && o - 2 >= 0 && snap.GetChar(o - 2) == '\r')
            return o - 2;
        return o - 1;
    }

    int IUiaTextHost.LineStartOf(int offset)
    {
        var snap = _bufferSnapshot;
        if (snap is null)
            return 0;
        int o = Math.Clamp(offset, 0, snap.CharLength);
        int line = snap.GetLineIndexOfChar(o);
        int logicalStart = snap.GetLineStart(line);
        // P8-1c: 折り返し ON の視覚行=キャレット位置が属する視覚セグメントの先頭を返す。
        // wrap OFF は論理行先頭(既存挙動)。
        var visual = TryFindVisualSegment(snap, line, o - logicalStart);
        return visual is { } vs ? logicalStart + vs.OffsetInLine : logicalStart;
    }

    int IUiaTextHost.LineEnd(int offset)
    {
        var snap = _bufferSnapshot;
        if (snap is null)
            return 0;
        int o = Math.Clamp(offset, 0, snap.CharLength);
        int line = snap.GetLineIndexOfChar(o);
        int logicalStart = snap.GetLineStart(line);
        int logicalEnd = snap.GetLineEnd(line, includeBreak: false);
        // P8-1c: 折り返し ON の視覚行=次の視覚行の先頭(=現在視覚行の直後)。
        // 継続セグメント(=論理行末に達しないセグメント)は次 seg 先頭=改行を跨がない。
        var visual = TryFindVisualSegment(snap, line, o - logicalStart);
        if (visual is { } vs)
        {
            int visualEndInLine = vs.OffsetInLine + vs.Length;
            if (logicalStart + visualEndInLine < logicalEnd)
                return logicalStart + visualEndInLine; // 継続 seg=改行手前で終了
        }
        // 論理行最終視覚行(または wrap OFF)=改行を含めて次論理行先頭 or TextLength
        if (line + 1 < snap.LineCount)
            return snap.GetLineStart(line + 1);
        return snap.CharLength;
    }

    int IUiaTextHost.LineEndNoBreakOf(int offset)
    {
        var snap = _bufferSnapshot;
        if (snap is null)
            return 0;
        int e = ((IUiaTextHost)this).LineEnd(offset);
        // CRLF 混在対応: LF → CR の順で剥がす(継続セグメントの場合 e は改行前=剥がすものが無いので no-op)
        if (e > 0 && snap.GetChar(e - 1) == '\n')
            e--;
        if (e > 0 && snap.GetChar(e - 1) == '\r')
            e--;
        return e;
    }

    /// <summary>
    /// P8-1c: 論理行 <paramref name="line"/> 内の <paramref name="offsetInLine"/> が属する視覚セグメントを返す。
    /// wrap OFF(<c>WrapColumns &lt;= 0</c>)または空行のときは null=呼び出し側で論理行フォールバック。
    /// </summary>
    /// <remarks>
    /// P8 レビュー Important-1 対応: <see cref="GdiCharMetrics.MeasureRun"/> は非 ASCII(日本語含む)で
    /// <see cref="System.Windows.Forms.TextRenderer.MeasureText"/>(GDI)へ落ちる=UI スレッド専用。
    /// UIA RPC スレッドから直接呼ぶと契約違反+<c>ApplyAppearance</c> による <c>Font</c> 差替時の
    /// disposed reference レースが発生する。RPC スレッドからは <see cref="Control.Invoke(Delegate)"/> で
    /// UI スレッドへマーシャリングして両問題を解決する(SR の Line 単位読みは典型的に秒あたり数回=
    /// Invoke レイテンシ数 ms は許容)。Handle 未生成時(SetSource 前)は null=論理行フォールバック。
    /// </remarks>
    private WrapSegment? TryFindVisualSegment(TextSnapshot snap, int line, int offsetInLine)
    {
        int wrap = _host.WrapColumns;
        if (wrap <= 0)
            return null;
        if (!_host.IsHandleCreated)
            return null; // UI スレッドが束縛されていない=論理行フォールバック
        if (_host.InvokeRequired)
        {
            try
            {
                return _host.Invoke(
                    new Func<WrapSegment?>(() =>
                        TryFindVisualSegmentCore(snap, line, offsetInLine, wrap)
                    )
                );
            }
            catch (ObjectDisposedException)
            {
                return null;
            }
            catch (InvalidOperationException)
            {
                return null;
            } // Handle 破棄との race
        }
        return TryFindVisualSegmentCore(snap, line, offsetInLine, wrap);
    }

    /// <summary>UI スレッド上での視覚セグメント検索本体(<see cref="TryFindVisualSegment"/> から Invoke マーシャリング後)。</summary>
    private WrapSegment? TryFindVisualSegmentCore(
        TextSnapshot snap,
        int line,
        int offsetInLine,
        int wrap
    )
    {
        IReadOnlyList<WrapSegment> segs;

        if (
            _lastLineSegs is { } c
            && ReferenceEquals(c.Snap, snap)
            && c.Line == line
            && c.Wrap == wrap
        )
        {
            segs = c.Segs;
            TestHook_LastLineSegsHitCount++;
        }
        else
        {
            var metrics = _host.Metrics;
            int logicalStart = snap.GetLineStart(line);
            int logicalEnd = snap.GetLineEnd(line, includeBreak: false);
            if (logicalStart == logicalEnd)
                return null;
            string lineText = snap.GetText(logicalStart, logicalEnd - logicalStart);
            int maxWidthPx = wrap * metrics.MeasureRun("0".AsSpan());
            segs = LineLayout.Wrap(lineText.AsSpan(), maxWidthPx, metrics);
            _lastLineSegs = (snap, line, wrap, segs);
            TestHook_LastLineSegsMissCount++;
        }

        return VisualSegments.FindContaining(segs, offsetInLine).Segment;
    }

    int IUiaTextHost.WordStart(int offset)
    {
        var snap = _bufferSnapshot;
        if (snap is null)
            return 0;
        int o = Math.Clamp(offset, 0, snap.CharLength);
        return WordBoundary_WordStart(snap, o);
    }

    int IUiaTextHost.WordEnd(int offset)
    {
        var snap = _bufferSnapshot;
        if (snap is null)
            return 0;
        int o = Math.Clamp(offset, 0, snap.CharLength);
        return WordBoundary_WordEnd(snap, o);
    }

    int IUiaTextHost.NextWordStart(int offset)
    {
        var snap = _bufferSnapshot;
        if (snap is null)
            return 0;
        int o = Math.Clamp(offset, 0, snap.CharLength);
        return yEdit.Core.Editing.WordBoundary.NextWordStart(snap, o);
    }

    int IUiaTextHost.PrevWordStart(int offset)
    {
        var snap = _bufferSnapshot;
        if (snap is null)
            return 0;
        int o = Math.Clamp(offset, 0, snap.CharLength);
        return yEdit.Core.Editing.WordBoundary.PrevWordStart(snap, o);
    }

    // WordStart/WordEnd は Core WordBoundary に直接メンバがないため、
    // 「offset を含む単語の左/右端(空白でない連続の左/右端)」を素朴実装する
    // (計画書 §5-5: v1 の TextNavigation.WordStart と同じ流儀=空白区切りだけ)。
    private static int WordBoundary_WordStart(TextSnapshot snap, int pos)
    {
        if (pos <= 0)
            return 0;
        int p = pos;
        while (p > 0)
        {
            int prev = p - 1;
            if (
                prev > 0
                && char.IsLowSurrogate(snap.GetChar(prev))
                && char.IsHighSurrogate(snap.GetChar(prev - 1))
            )
                prev--;
            char pc = snap.GetChar(prev);
            if (char.IsWhiteSpace(pc) || pc == '\r' || pc == '\n')
                break;
            p = prev;
        }
        return p;
    }

    private static int WordBoundary_WordEnd(TextSnapshot snap, int pos)
    {
        int p = pos;
        while (p < snap.CharLength)
        {
            char c = snap.GetChar(p);
            if (char.IsWhiteSpace(c) || c == '\r' || c == '\n')
                break;
            if (
                char.IsHighSurrogate(c)
                && p + 1 < snap.CharLength
                && char.IsLowSurrogate(snap.GetChar(p + 1))
            )
                p += 2;
            else
                p++;
        }
        return p;
    }

    System.Windows.Rect IUiaTextHost.BoundingRectangle
    {
        get
        {
            lock (_boundsSync)
                return _bounds;
        }
    }

    // P5 Task 10: 座標 API 本実装
    // [start, end) を含む各行のスクリーン矩形を UIA 形式 (x,y,w,h, ...) で返す。
    // ComputeCaretPoint は UI スレッド専用の状態(_topLine 等)を参照するため、
    // RPC スレッドから呼ばれた場合は Invoke で UI スレッドへマーシャリングする。
    // ハンドル未生成 / 非可視範囲は空配列。
    double[] IUiaTextHost.GetBoundingRectangles(int start, int end)
    {
        if (_host.InvokeRequired)
        {
            if (!_host.IsHandleCreated)
                return Array.Empty<double>();
            return _host.Invoke(new Func<double[]>(() => ComputeBoundingRectangles(start, end)));
        }
        return ComputeBoundingRectangles(start, end);
    }

    private double[] ComputeBoundingRectangles(int start, int end)
    {
        var snap = _bufferSnapshot;
        if (snap is null)
            return Array.Empty<double>();
        int s = Math.Clamp(start, 0, snap.CharLength);
        int en = Math.Clamp(end, 0, snap.CharLength);
        if (s >= en)
            return Array.Empty<double>();

        int csx = _clientToScreenX,
            csy = _clientToScreenY;
        int lineHeight = _host.Metrics.LineHeightPx;
        var rects = new List<double>(16);

        int pos = s;
        int safety = 0;
        while (pos < en && safety++ < 100_000)
        {
            int line = snap.GetLineIndexOfChar(pos);
            int lineEndNoBreak = snap.GetLineEnd(line, includeBreak: false);
            int rangeEnd = Math.Min(en, lineEndNoBreak);

            var (x1, y1, visible) = _host.ComputeCaretPointForUia(pos);
            var (x2, _, _) = _host.ComputeCaretPointForUia(rangeEnd);
            if (visible)
            {
                double w = Math.Max(1, x2 - x1);
                rects.Add(csx + x1);
                rects.Add(csy + y1);
                rects.Add(w);
                rects.Add(lineHeight);
            }

            int nextLineStart =
                (line + 1 < snap.LineCount) ? snap.GetLineStart(line + 1) : snap.CharLength;
            if (nextLineStart <= pos)
                break;
            pos = nextLineStart;
        }
        return rects.ToArray();
    }

    // P5 Task 11: OffsetFromScreenPoint 本実装
    // スクリーン座標 (x, y) 直下の文字オフセットを返す(HitTest 相当)。範囲外は clamp。
    // 既存の OffsetFromClientPoint(UI スレッド専用)を再利用し、
    // RPC スレッドから呼ばれた場合は Invoke で UI スレッドへマーシャリングする。
    int IUiaTextHost.OffsetFromScreenPoint(double x, double y)
    {
        if (_host.InvokeRequired)
        {
            if (!_host.IsHandleCreated)
                return 0;
            return _host.Invoke(new Func<int>(() => ComputeOffsetFromScreenPoint(x, y)));
        }
        return ComputeOffsetFromScreenPoint(x, y);
    }

    private int ComputeOffsetFromScreenPoint(double x, double y)
    {
        var snap = _bufferSnapshot;
        if (snap is null)
            return 0;
        // スクリーン→クライアント変換(client 原点は _clientToScreenX/Y)。範囲外は
        // OffsetFromClientPoint 側で「Y<0=先頭視覚行の X」「exhausted=文書末尾」に丸める。
        int clientX = (int)(x - _clientToScreenX);
        int clientY = (int)(y - _clientToScreenY);
        // 負座標はゼロ扱い(文書先頭 0 に落ちる=clamp)。上限は OffsetFromClientPoint が自然に処理。
        if (clientX < 0)
            clientX = 0;
        if (clientY < 0)
            clientY = 0;
        int pos = _host.OffsetFromClientPoint(clientX, clientY);
        return Math.Clamp(pos, 0, snap.CharLength);
    }

    // === スクロール (interface の節順に合わせてここに置く: 座標 → スクロール → 属性) ===

    void IUiaTextHost.ScrollRangeIntoView(int start, int end, bool alignToTop)
    {
        // SetSelection と同形 (P5 Task 14 I-3)。このガードは 2 つの意味で load-bearing:
        //   1. 破棄後 / Handle 未生成での BeginInvoke による InvalidOperationException を防ぐ
        //   2. Control.InvokeRequired は Handle 未生成 / 破棄後に false を返す=ガードが無いと
        //      RPC スレッドがそのまま UI スレッド専有状態 (ClientSize / _hscroll.Visible /
        //      PositionCaret) に触れる。CLAUDE.md §2 の a11y 鉄則違反になる。
        if (_host.IsDisposed || !_host.IsHandleCreated)
            return;
        if (_host.InvokeRequired)
        {
            // 書き込み系は fire-and-forget (RPC スレッドを待たせない=deadlock 回避)。
            // WinForms の invoke キューは FIFO のため、SR が直後に呼ぶ同期 Invoke 系
            // (GetBoundingRectangles 等) はこのスクロールの後に走る。
            //
            // 自身へ再入するのは、UI スレッド到達時に上の Dispose / Handle ガードを再評価するため
            // (到達時は InvokeRequired == false なので無限ループにはならない)。
            // 隣の SetFocus は `() => _host.Focus()` で再入しないが、そちらに合わせて
            // 揃えないこと=再評価の効果が失われる。
            _host.BeginInvoke(
                new Action(() => ((IUiaTextHost)this).ScrollRangeIntoView(start, end, alignToTop))
            );
            return;
        }
        _host.ScrollCharRangeIntoView(start, end, alignToTop);
    }

    // P5 Task 14 (I-2): live プロパティ Handle は RPC で CreateHandle を誘発し得るためキャッシュ返し。
    nint IUiaTextHost.Handle => _hwnd;

    // P5 Task 14 (I-1): Focused は内部で GetFocus() を呼ぶ=RPC スレッドから読むと常に false に落ちる。
    // OnGotFocus/OnLostFocus で管理している _hasFocus キャッシュを返す(v1 ScintillaHost 同形)。
    bool IUiaTextHost.HasFocus => _host.HasFocusCached;

    int IUiaTextHost.ControlTypeId => System.Windows.Automation.ControlType.Document.Id;

    string IUiaTextHost.Name => "本文";

    string IUiaTextHost.AutomationId => "editor";

    void IUiaTextHost.SetFocus()
    {
        // P5 Task 14 (I-3): 破棄後 / Handle 未生成での BeginInvoke による InvalidOperationException を防ぐ
        if (_host.IsDisposed || !_host.IsHandleCreated)
            return;
        if (_host.InvokeRequired)
        {
            _host.BeginInvoke(new Action(() => _host.Focus()));
            return;
        }
        _host.Focus();
    }

    // === Test hook (Editor.Tests から観測) ===

    // Editor.Tests から観測するためのヒットカウンタ(internal・テスト以外の呼び出しは想定しない)。
    internal long TestHook_LastLineSegsHitCount { get; private set; }
    internal long TestHook_LastLineSegsMissCount { get; private set; }

    internal void TestHook_ResetLastLineSegsCounters()
    {
        TestHook_LastLineSegsHitCount = 0;
        TestHook_LastLineSegsMissCount = 0;
    }
}
