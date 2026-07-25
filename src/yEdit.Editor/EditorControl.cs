using System.ComponentModel;
using System.Runtime.InteropServices;
using yEdit.Core.Buffers;
using yEdit.Core.Editing;
using yEdit.Core.Layout;
using yEdit.Core.Settings;
using yEdit.Core.Text;
// System.Windows.Forms.SelectionRange(MonthCalendar 用)と同名のため別名で解決する。
using SelectionRange = yEdit.Core.Layout.SelectionRange;

namespace yEdit.Editor;

/// <summary>
/// P2 で導入する自作エディットコントロール。P1 の <see cref="TextBuffer"/>/<see cref="TextSnapshot"/>
/// をソースに、Layout 層(<c>ViewportLayout</c>/<c>FrameBuilder</c>)が組み立てた <see cref="Frame"/> を
/// GDI 呼び出しで描画する。P6 で ScintillaHost を完全置換・P7 で並行運用終了(NVDA が Scintilla クラス名を
/// 特別扱いする問題を回避する v2 UIA 単一経路の本命実装)。UI スレッド専用
/// (<see cref="GdiCharMetrics"/>・<c>SetSource</c> は 1 度だけ)。
/// </summary>
public sealed partial class EditorControl : Control, yEdit.Accessibility.IUiaTextHost
{
    // Task 13 で ApplyAppearance によりフォント差し替え/GdiCharMetrics 再構築/ViewportStyle 差し替えを
    // 行うため readonly を外した(Font 差し替え時は明示的に古い Font.Dispose を呼ぶ責務)。
    private Font _font;

    // Task 9 レビュー I-1: IME overlay 用の下線フォントは打鍵毎の OnPaint で使う=
    // 毎回 new すると GDI HFONT 割当が積む。_font と寿命同期でキャッシュ(ApplyAppearance で再構築)。
    private Font _underlineFontCache;

    // Task 10: 変換対象節(TargetConverted)用の Underline|Bold フォント。_underlineFontCache と対称に
    // ctor/ApplyAppearance で寿命同期する(GDI HFONT リーク回避=§0-6 リソース管理)。
    private Font _targetFontCache;

    // CA1859: 実体は常に GdiCharMetrics(ctor / ApplyAppearance 両経路)であり、
    // 内部の Paint hot-path から呼ばれる MeasureRun 等が interface dispatch を通らないよう concrete 型で保持する。
    // 外部公開 (`Metrics` property) は ICharMetrics のまま (contract 不変)。
    private GdiCharMetrics _metrics;
    private ViewportStyle _style;
    private readonly VScrollBar _vscroll;
    private readonly HScrollBar _hscroll;
    private TextBuffer? _buffer;
    private int _topLine;
    private int _wrapColumns;
    private int _scrollX;
    private bool _showLineNumbers;
    private bool _highlightCurrentLine;
    private bool _showWhitespace;

    // キャレット/選択/desired X の state は Phase 3 (Task 3b) で CaretController へ移譲。
    // - 選択範囲は [Math.Min(Anchor, Caret), Math.Max(Anchor, Caret)]。
    // - Anchor == Caret: 選択なし(単純キャレット位置)
    // - Anchor <  Caret: 右方向に伸びた選択(キャレットが末尾)
    // - Anchor >  Caret: 左方向に伸びた選択(キャレットが先頭・shift+←/Home で作られる)
    // 副作用(Invalidate/PositionCaret/AfterEdit/UIA イベント発火)は EditorControl 側に残置=
    // Controller は state 操作(SnapAndClamp + 選択セマンティクス)のみを担う。
    private readonly CaretController _caretCtrl = new();

    // Phase 3 (Task 3c) で抽出した入力ディスパッチャ。keymap Dictionary + MouseEventKind 経路を
    // 保持する pure dispatcher。state は持たない(readonly _host/_caret/_keyMap のみ)ため、
    // 契約テスト InputRouterContractTests.InputRouter_HasNoInstanceStateFields で機械固定する。
    // 初期化は ctor で `_caretCtrl` 生成後に new する(先方参照を避けるため field 宣言も後ろに置く)。
    private readonly InputRouter _input;

    // Phase 3 (Task 3a) で抽出した IME controller。_ime (ImeCompositionState) の所有権をここに移譲し、
    // WM_IME_* の状態機械 / Imm32 P/Invoke ラップ / overlay 描画を bit-perfect 移設した。
    // 副作用 (Invalidate / PositionCaret / AfterEdit) は host (EditorControl 側 IImeOverlayHost 実装) に
    // 委譲する契約=Controller は state 操作と P/Invoke ラップに専念する。
    // 初期化は ctor で _caretCtrl / _font / _metrics 等が揃った後に new する (host=this を渡すため)。
    private readonly ImeController _imeCtrl;

    // Phase 3 (Task 3d) で抽出した UIA テキストホスト adapter。IUiaTextHost 全メンバ実装 +
    // Uia 系 12 field (_bufferSnapshot / _bounds / _boundsSync / _clientToScreenX/Y /
    // _lastLineSegs / _hwnd / _provider / _testHook_LastGetObjectServed /
    // _uiaTextChangedCount / _uiaSelectionChangedCount / _uiaFocusChangedCount) の所有権をここに移譲。
    // UI thread 側からは OnSnapshotChanged / OnBoundsChanged / RaiseTextChanged 等の通知経路で呼ぶ。
    // EditorControl 側の IUiaTextHost 実装 (EditorControl.Uia.cs) はこの Adapter への薄いラッパのみ。
    private readonly UiaTextHostAdapter _uia;

    /// <summary>
    /// IME 未確定期間中か。Task 6 以降の描画/イベント発火の分岐に使う。
    /// 純ロジックは <see cref="ImeCompositionState"/>(P4 Task 2)側で、
    /// 状態と Imm32 P/Invoke ラップは <see cref="ImeController"/>(Phase 3 Task 3a)側。
    /// </summary>
    private bool IsComposing => _imeCtrl.IsActive;

    // Task 10: システムキャレットのフォーカス状態フラグ。CreateCaret/DestroyCaret はフォーカスを
    // 持つ間のみ有効なため、SetCaretCharOffset 等から PositionCaret を呼ぶ際にガードに使う。
    private bool _hasFocus;

    // P3 Task 6: 上下移動(Up/Down/PageUp/PageDown)で保持する desired X(px)。
    // Phase 3 Task 3b で CaretController.DesiredXpx へ移譲(コメントは _caretCtrl 参照)。

    // Task 15: システムキャレットの太さ(px)。既定 2・ApplyAppearance で AppSettings.CaretWidth
    // (1〜5)を反映。弱視のキャレット視認性要件(設計原則 yedit-sighted-users-first-class)。
    private int _caretWidthPx = 2;

    // セルハイライト状態(HighlightCharRange で設定・ClearHighlight で null)。
    // テキスト選択(_caretCtrl.Anchor/_caretCtrl.Caret)とは独立した装飾で、単一アクティブ。
    private SelectionRange? _cellHighlight;

    // P3 Task 12: ホイールデルタ蓄積(1 tick = 120)。
    // トラックパッド等の細切れ発火で 40+40+40=120 のように 1 tick を溜めるため、
    // 発火閾値 (>=120 / <=-120) に達したら SystemInformation.MouseWheelScrollLines 行送りを 1 回発動する。
    private int _wheelAccum;

    // Phase 3 Task 3d: Uia 系 12 field (_bufferSnapshot / _bounds / _boundsSync /
    // _clientToScreenX/Y / _lastLineSegs / _hwnd / _provider / _testHook_LastGetObjectServed /
    // _uiaTextChangedCount / _uiaSelectionChangedCount / _uiaFocusChangedCount) の所有権は
    // UiaTextHostAdapter (_uia) へ移譲済み。EditorControl 本体は Adapter への通知経路
    // (OnSnapshotChanged / OnBoundsChanged / RaiseTextChanged) のみを持つ。
    //
    // _lastFrame は Paint (OnPaint) のスナップショットで Uia 座標 API 用に公開している独立フィールド
    // (Adapter 移譲対象外=Test hook TestHook_GetLastFrame でも参照)。
    private volatile yEdit.Core.Layout.Frame? _lastFrame;

    // P6 Task 10 レビュー M-2: CurrentBuffer の null 経路で毎回 new すると
    // Assert.Same(ctrl.CurrentBuffer, ctrl.CurrentBuffer) が SetSource 前で失敗する反直観挙動になる。
    // 空 TextBuffer は immutable な使い方に留める前提(=呼び出し側は Save 読み出し等の read-only 用途)
    // のため、プロセス寿命の静的キャッシュで参照同一性を保証する。
    private static readonly TextBuffer s_emptyBuffer = TextBuffer.FromString(string.Empty);

    // P6 Task 4: 直前の Modified 状態(SavePointLeft 検出用)。AfterEdit で Modified=false→true
    // 遷移を検出して SavePointLeft を発火する。SetSource/ReplaceSource/SetSavePoint で
    // 初期同期する(初回編集後の spurious fire 回避=SetSource 直後のバッファが Modified=true
    // だった場合に AfterEdit なしで SavePointLeft が「打たれてないのに」焚かれないよう、
    // 初期化時点で Modified に合わせる)。
    private bool _wasModified;

    public EditorControl()
    {
        SetStyle(
            ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.ResizeRedraw
                | ControlStyles.UserPaint
                | ControlStyles.Selectable,
            true
        );
        TabStop = true;
        BackColor = Color.White;
        ForeColor = Color.Black;
        _font = new Font("MS ゴシック", 12f);
        _underlineFontCache = new Font(_font, _font.Style | FontStyle.Underline);
        _targetFontCache = new Font(_font, _font.Style | FontStyle.Underline | FontStyle.Bold); // Task 10
        _metrics = new GdiCharMetrics(_font);
        _style = DefaultStyle();
        Cursor = Cursors.IBeam;

        // 空文書想定で初期は Enabled=false。SetSource で有効化される。
        // Scroll イベントは「ユーザー操作(ドラッグ/ホイール/キー)」でのみ発火。
        // TopLine setter からの `_vscroll.Value = ...` では発火しないため、
        // TopLine ↔ VScrollBar 間の無限ループは起こらない(セッター側の != チェックは念のため)。
        //
        // Dock 順の注意: WinForms の DefaultLayout は Controls コレクションを逆順で docking する。
        // 「後に Add した子ほど先に dock 処理される=フルエッジを取る」ため、HScrollBar を先に、
        // VScrollBar を後に Add することで:
        //   - VScrollBar が右端全高(Explorer と同じ慣習)
        //   - HScrollBar が下端の残り幅(VScroll の左まで)
        // となる。ここを逆順にすると HScrollBar が下端全幅を取ってしまい、右下の角に
        // VScroll が張り付かない見た目になる。
        _hscroll = new HScrollBar
        {
            Dock = DockStyle.Bottom,
            SmallChange = 10,
            Visible = false,
        };
        _hscroll.Scroll += (_, e) => ScrollX = e.NewValue;
        Controls.Add(_hscroll);

        _vscroll = new VScrollBar
        {
            Dock = DockStyle.Right,
            SmallChange = 1,
            Enabled = false,
        };
        _vscroll.Scroll += (_, e) => TopLine = e.NewValue;
        Controls.Add(_vscroll);

        // Task 3c: InputRouter は _caretCtrl 生成後に組み立てる(dispatcher が保持する参照は
        // readonly なので後段で差し替えられない=ctor 内で 1 度だけ new する)。
        _input = new InputRouter(this, _caretCtrl);

        // Task 3a: ImeController は host (this=IImeOverlayHost) + _caretCtrl + insertConfirmedText
        // (private method group) を注入する。IImeContext は Handle 要り=呼び出し時に new する
        // factory pattern (Handle は Control が lazy に materialize するが、IME イベント時には
        // 既に materialize 済み)。
        _imeCtrl = new ImeController(
            contextFactory: () => new WinImeContext(Handle),
            caret: _caretCtrl,
            host: this,
            insertConfirmedText: InsertConfirmedText
        );

        // Task 3d: UiaTextHostAdapter (IUiaTextHost 全メンバ実装 + Uia 系 12 field 所有)。
        // this を UI thread 側 host として渡す (RectangleToScreen / PointToScreen / InvokeRequired /
        // BeginInvoke / IsHandleCreated / IsDisposed / Handle / ComputeCaretPointForUia /
        // OffsetFromClientPoint / Metrics / WrapColumns / HasFocusCached / SetSelectionCharRange /
        // ScrollCharRangeIntoView / Focus を Adapter から呼ぶ)。
        _uia = new UiaTextHostAdapter(this, _caretCtrl);
    }

    /// <summary>ソースの <see cref="TextBuffer"/> を差し込む(1 度だけ)。</summary>
    /// <remarks>
    /// SetSource 前にフォーカスを得ていた場合(OnGotFocus は buffer null で早期 return するため
    /// キャレット未生成)は、SetSource 末尾でシステムキャレットを生成する。P6 のタブ切替で
    /// 「Controls.Add → 自動 Focus → 遅延 SetSource」の順で組み立てるパターンでもキャレットが
    /// 確実に立つ(Task 15 レビュー I-2)。
    /// </remarks>
    public void SetSource(TextBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (_buffer is not null)
            throw new InvalidOperationException("SetSource は 1 度だけ");
        _buffer = buffer;
        _topLine = 0;
        UpdateVerticalScrollbar();
        UpdateHorizontalScrollbar();
        if (_hasFocus)
        {
            NativeMethods.CreateCaret(Handle, nint.Zero, _caretWidthPx, _metrics.LineHeightPx);
            PositionCaret();
            NativeMethods.ShowCaret(Handle);
        }
        Invalidate();
        // Task 12: 初期化時に未確定文字列用フォントを IME に通知(候補窓/未確定描画のメトリクス整合)。
        _imeCtrl.NotifyCompositionFont();
        // P5 Task 5 / Task 3d: RPC スレッド用スナップショットキャッシュを初期化 (Adapter 経由=
        // 元 CacheSnapshot() + `_lastLineSegs = null;` を 1 経路に集約)。
        _uia.OnSnapshotChanged(_buffer.Current);
        // P6 Task 4: SavePointLeft 検出用の直前状態をバッファに同期
        // (FromString で生まれるバッファは Modified=false 前提だが、既に Modified=true な
        //  バッファを差し込まれた場合に初回 AfterEdit で SavePointLeft が spurious 発火するのを防ぐ)。
        _wasModified = _buffer.Modified;
    }

    /// <summary>
    /// P6 Task 1: 本文全体を string で読み書きする互換 API。
    /// getter は現在の TextBuffer スナップショットから全文を返す(内部 GetText と同じ経路)。
    /// setter は新規 TextBuffer を組み立てて <see cref="SetOrReplaceSource"/> に流す
    /// (Task 10 レビュー M-1: SetSource/ReplaceSource 分岐は 1 箇所に集約)。
    /// SetSource 前 / _buffer=null で getter は空文字列を返し、setter は初回 SetSource として扱う。
    /// </summary>
    /// <remarks>
    /// Control.Text と同名だが、Control.Text は本文非公開原則(§0-6 / P5 Task 7)により
    /// WM_GETTEXT/WM_GETTEXTLENGTH で応答しない=シャドウ new が必要。
    /// </remarks>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public new string Text
    {
        get => _buffer?.Current.GetText(0, _buffer.Current.CharLength) ?? string.Empty;
        set => SetOrReplaceSource(TextBuffer.FromString(value ?? string.Empty));
    }

    /// <summary>
    /// P6 Task 1: 既存 TextBuffer を新しいものに差し替える(ファイル開き直し・バックアップ復元用)。
    /// SetSource が 1 度限りなのに対し、これは任意回呼べる=Document ごとに EditorControl を
    /// 作り直すのを避ける。キャレット/選択/スクロール/セル強調/IME 未確定/マウス・ホイール状態/
    /// スナップショットキャッシュをすべてリセットし、UIA TextChangedEvent(および有効時は
    /// TextSelectionChangedEvent)を発火する。
    /// </summary>
    /// <remarks>
    /// <c>SetSource</c> の 2 回目以降相当=バッファ参照の丸ごと差替え(本文の一部置換ではない=
    /// 部分置換は <see cref="ReplaceCharRange"/> を使う)。
    /// </remarks>
    public void ReplaceSource(TextBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        // §4-6: 他の状態変異 API と同じく IME 未確定はまず確定キャンセルする
        if (IsComposing)
            CancelCompositionAndDefault();
        _buffer = buffer;
        _caretCtrl.SetTo(0, _buffer.Current);
        _topLine = 0;
        _scrollX = 0;
        _caretCtrl.DesiredXpx = -1;
        _cellHighlight = null; // 旧バッファのオフセット由来のセル強調は無効化
        MouseDragging = false; // ドラッグ選択の途中状態を破棄
        _wheelAccum = 0; // ホイール蓄積(1 tick = 120)をリセット
        UpdateVerticalScrollbar();
        UpdateHorizontalScrollbar();
        if (_hasFocus)
        {
            PositionCaret();
        }
        Invalidate();
        // Task 3d: RPC スレッド用スナップショット更新 + _lastLineSegs 破棄を Adapter 経由に集約
        // (元 CacheSnapshot() + `_lastLineSegs = null;`)。
        _uia.OnSnapshotChanged(_buffer.Current);
        // P6 Task 4: 差し替え後のバッファ状態で SavePointLeft 検出用の直前状態を同期
        // (SetSource と同旨=バッファが Modified=true でも初回 AfterEdit で spurious 発火しないよう)
        _wasModified = buffer.Modified;
        // P5 Task 8 / P6 Task 1: バッファ全差替えは AfterEdit と同じ通知契約
        // (SR/UIA クライアントが旧本文をキャッシュしたままにならないよう発火)
        _uia.RaiseTextChanged();
        if (RaiseUiaSelectionEvents)
            _uia.RaiseSelectionChanged();
        // P6 Task 4: バッファ全差替えは App 層のステータスバー更新契機なので UpdateUI 発火
        UpdateUI?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// P6 Task 2: 現在の TextBuffer スナップショットから全文を返す(App 層互換)。
    /// 大容量ファイルでは 64MB 閾値二層化(<see cref="SearchController"/>)で回避されるが、
    /// 呼び出し側でメモリ配慮が必要な場面もある(App 層側で判定=§設計 §2-8)。
    /// </summary>
    public string SnapshotText =>
        _buffer?.Current.GetText(0, _buffer.Current.CharLength) ?? string.Empty;

    /// <summary>
    /// P6 Task 10: <see cref="TextBuffer"/> を差し込む(初回は <see cref="SetSource"/>・
    /// 2 度目以降は <see cref="ReplaceSource"/> に自動振り分け)。<see cref="Text"/> セッターの
    /// TextBuffer 直入れ版=App 層 Stream I/O 経路が string 全文化を経ずにバッファを流し込むための API。
    /// FileController の LoadInto / RestoreFromBackup(=fresh Document への初回差し込み or
    /// 開き直しでの差し替え)で使う。
    /// </summary>
    /// <remarks>
    /// SetSource は 1 度限りの契約(2 度目は <see cref="InvalidOperationException"/>)。
    /// ReplaceSource は _buffer 存在前提のイベント/UI 更新契約(CreateCaret/NotifyCompositionFont を
    /// 打たない)=fresh EditorControl に直接呼ぶとシステムキャレットが未生成のまま残る。
    /// 本メソッドは <see cref="Text"/> セッターの分岐と等価(の string 経由を省いた版)。
    /// </remarks>
    public void SetOrReplaceSource(TextBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (_buffer is null)
            SetSource(buffer);
        else
            ReplaceSource(buffer);
    }

    /// <summary>
    /// P6 Task 10: 現在の <see cref="TextBuffer"/> 参照を返す(App 層 Stream I/O 経路の Save 対称化用)。
    /// SetSource/ReplaceSource で差し込まれたものをそのまま返す=<see cref="TextFileService.Save(string, TextBuffer, System.Text.Encoding, bool)"/>
    /// と組み合わせて 1GB 級 UTF-8 の string 全文化を回避する契約。null 経路(SetSource 前)では
    /// プロセス寿命の静的空 TextBuffer を返す(常に non-null 保証=呼び出し側で null チェック不要・
    /// 同経路の連続呼び出しで参照同一性も保つ=Task 10 レビュー M-2)。
    /// </summary>
    /// <remarks>
    /// 返す参照は「編集用」ではなく「保存/照会用のスナップショット提供元」の位置付け。
    /// バッファは可変(TextBuffer.Insert/Delete/Replace)なので、返した参照へ外部から書き込むと
    /// EditorControl の内部状態(キャレット/選択/描画)と齟齬が出る=読み取り用途に限る。
    /// null 経路で返す静的空バッファも同様=編集してはならない(プロセス全体で共有)。
    /// </remarks>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public TextBuffer CurrentBuffer => _buffer ?? s_emptyBuffer;

    // Task 3c: InputRouter が RouteKey で null 判定に使う(SetSource 前の bare TextBuffer が
    // 欲しい=CurrentBuffer は s_emptyBuffer にフォールバックするため区別できない)。
    internal TextBuffer? Buffer => _buffer;

    // Task 3c: InputRouter の nav 系ハンドラ(Home / Up / Down / PageUp / PageDown)が
    // メトリクスを参照するため internal accessor を露出する
    // (WrapColumns は既に public プロパティ・下記 line 609 で定義)。
    internal ICharMetrics Metrics => _metrics;

    // Task 3c: InputRouter の mouse 系ハンドラ(Down/Move/Up)がドラッグフラグを読み書きするため
    // internal accessor を露出する(P3 Task 12: MouseDown で true・MouseUp / ボタン離した drift で false=
    // このフラグが立っている間だけ MouseMove がキャレット位置を更新する=非押下時の drift 無視)。
    // 所有権は EditorControl。PR4 C-6 (S2292) で auto-property 化=backing field は compiler 生成に集約。
    // WFO1000(Designer プロパティのシリアライゼーション警告)回避のため属性で明示的に非公開化する。
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    internal bool MouseDragging { get; set; }

    // Task 3d: UiaTextHostAdapter が ComputeBoundingRectangles から ComputeCaretPoint を呼ぶための
    // named accessor (直接 internal 化した ComputeCaretPoint を呼ぶ薄いラッパ・分かりやすさのため)。
    internal (int X, int Y, bool Visible) ComputeCaretPointForUia(int offset) =>
        ComputeCaretPoint(offset);

    // Task 3d: UiaTextHostAdapter が IUiaTextHost.HasFocus 実装で _hasFocus を返すための named accessor。
    // WinForms Control には ContainsFocus が居るため名前衝突しない別名で露出する
    // (Control.Focused は内部で GetFocus() を呼び RPC スレッドから読むと常に false=v1 対応と同旨)。
    internal bool HasFocusCached => _hasFocus;

    /// <summary>
    /// UIA <c>ITextProvider.GetVisibleRanges</c> の実処理(UI スレッド専用)。
    /// 現在ビューポートに見えている本文の範囲 [Start, End) を返す。
    /// </summary>
    /// <remarks>
    /// 描画 (<c>EditorControl.Paint.cs</c>) と**同じ** <see cref="ViewportLayout.Build"/> と
    /// <see cref="PaintHeightPx"/> を使う。「見えている行」の定義を二重化しないことが本メソッドの要点。
    /// 折り返し ON では視覚行境界になる。末尾行の改行は含めない。
    /// バッファ未設定・可視行ゼロでは (0, 0)。
    /// 典拠: docs/plans/2026-07-25-uia-scrollintoview-design.md §5.2。
    /// </remarks>
    internal (int Start, int End) GetVisibleCharRange()
    {
        if (_buffer is null)
            return (0, 0);
        var snap = _buffer.Current;
        var rows = ViewportLayout.Build(snap, _topLine, PaintHeightPx, _wrapColumns, _metrics);
        if (rows.Count == 0)
            return (0, 0);
        var first = rows[0];
        var last = rows[rows.Count - 1];
        return (first.SegmentStartChar, last.SegmentStartChar + last.SegmentLength);
    }

    /// <summary>P6 Task 3: 現在のバッファ論理行数(App 層互換=`Lines.Count` 相当)。</summary>
    public int LineCount => _buffer?.Current.LineCount ?? 0;

    /// <summary>
    /// 現在のバッファ文字数 (UTF-16 code units)。O(1) で <see cref="SnapshotText"/> の全文コピーを
    /// 避けたい場面(セッション保存の cap 事前判定 §復元 §8.2 等)向け。<see cref="LineCount"/> と同流儀。
    /// </summary>
    public int TextLength => _buffer?.Current.CharLength ?? 0;

    /// <summary>
    /// P6 Task 5: 本文中の改行を <paramref name="eol"/> に一括変換する(App 層互換=保存時の EOL 統一)。
    /// 既存本文の <c>\r\n</c> / <c>\r</c> / <c>\n</c> を検出→指定 EOL に置換した文字列で
    /// <see cref="ReplaceSource(TextBuffer)"/> し、<see cref="EolMode"/> も同時に更新する。
    /// SetSource 前は no-op。
    /// </summary>
    /// <remarks>
    /// <see cref="EolMode"/> は「以後の Enter 押下で挿入する改行」の設定であり、既存本文には
    /// 効かない。App 層(FileController の保存経路)は保存前に本 API で本文の改行を統一する。
    /// 実装は「一旦 LF に正規化 → 目的 EOL に置換」の 2 段階=CRLF/CR/LF 混在を安全に扱える。
    /// no-op fast-path(=すでに目的 EOL で統一されている場合)では ReplaceSource による
    /// キャレット/選択/スクロールリセット・UIA TextChanged 発火を回避する(EolMode だけ更新)。
    /// non fast-path でも「行 index + 改行文字以外の相対 offset」の対で caret/anchor/topLine/
    /// scrollX を保存→復元する(P6 レビュー I-2: Save 毎に caret が先頭へ飛ぶ退行を回避)。
    /// </remarks>
    public void ConvertEols(LineEnding eol)
    {
        if (_buffer is null)
            return;
        byte[] targetBytes = eol switch
        {
            LineEnding.Crlf => new byte[] { 0x0D, 0x0A },
            LineEnding.Lf => new byte[] { 0x0A },
            LineEnding.Cr => new byte[] { 0x0D },
            _ => new byte[] { 0x0A },
        };
        int targetCharLen = targetBytes.Length; // ASCII のみ=byte 数 = char 数
        var snap = _buffer.Current;

        // P7 I-3 Task 3: SnapshotText 全文化を撤廃=byte スキャンで fast-path 判定。
        // すでに全 EOL が target で統一されていれば ReplaceSource(キャレット/選択/スクロールリセット
        // + UIA TextChanged 発火)を回避し、EolMode だけ更新して抜ける。
        if (IsEolAlreadyUniform(snap, targetBytes))
        {
            EolMode = eol;
            return;
        }

        // 変換前の caret/anchor を「改行以外の文字数+改行数」で分解=変換後も同じ論理位置を再構成できる。
        // SnapshotReader で chunked 走査(旧実装は SnapshotText 全文化=1GB 級 peak を招いていた)。
        var (caretM, caretK) = CountNonBreakAndBreaksInSnapshot(snap, _caretCtrl.Caret);
        var (anchorM, anchorK) = CountNonBreakAndBreaksInSnapshot(snap, _caretCtrl.Anchor);
        int savedTopLine = _topLine;
        int savedScrollX = _scrollX;

        // ピース単位に UTF-8 byte を走査し、EOL(0x0D/0x0A)を target に置換しつつ
        // TextBufferBuilder にストリーム流し込みで新スナップショットを構築。
        // CRLF がピース境界に跨るケースは 1 バイト carry(pendingCr)で吸収。
        var builder = new TextBufferBuilder();
        byte[] outBuf = new byte[64 * 1024];
        int outLen = 0;
        bool pendingCr = false;

        foreach (var piece in PieceTree.Enumerate(snap.Root))
        {
            var span = piece.Chunk.Span.Slice(piece.ByteStart, piece.ByteLen);
            for (int i = 0; i < span.Length; i++)
            {
                byte b = span[i];
                if (pendingCr)
                {
                    // 前ピース末尾の CR を持ち越し中。今の byte が LF なら CRLF として 1 改行、
                    // それ以外なら CR 単独として 1 改行を吐いてから今の byte を通常処理へ進める。
                    pendingCr = false;
                    if (b == 0x0A)
                    {
                        EmitEol(targetBytes, ref outBuf, ref outLen, builder);
                        continue;
                    }
                    EmitEol(targetBytes, ref outBuf, ref outLen, builder);
                }
                if (b == 0x0D)
                {
                    if (i + 1 < span.Length && span[i + 1] == 0x0A)
                    {
                        // ピース内 CRLF
                        EmitEol(targetBytes, ref outBuf, ref outLen, builder);
                        i++;
                    }
                    else if (i + 1 < span.Length)
                    {
                        // ピース内 CR 単独(次 byte が LF 以外)
                        EmitEol(targetBytes, ref outBuf, ref outLen, builder);
                    }
                    else
                    {
                        // ピース末尾 CR=次ピース先頭を確認しないと CRLF/CR 単独が判別不能。持ち越す。
                        pendingCr = true;
                    }
                }
                else if (b == 0x0A)
                {
                    EmitEol(targetBytes, ref outBuf, ref outLen, builder);
                }
                else
                {
                    // Check-before-write: 直前 EmitEol が outLen==outBuf.Length のまま抜けたケース
                    // (EmitEol の flush 判定は `outLen + eol.Length > outBuf.Length` で = のときは flush しない)
                    // に備えて先に flush する=安全側に統一。
                    if (outLen == outBuf.Length)
                        FlushBuf(ref outBuf, ref outLen, builder);
                    outBuf[outLen++] = b;
                }
            }
        }
        if (pendingCr)
            EmitEol(targetBytes, ref outBuf, ref outLen, builder);
        if (outLen > 0)
            FlushBuf(ref outBuf, ref outLen, builder);

        ReplaceSource(builder.Build());
        int total = _buffer!.Current.CharLength;
        // アンカー/キャレットは元の (m, k) 分解から再構成して復元する(ConvertEols 前後で
        // 同じ論理位置を保つ)。ReplaceSource が両者を 0 に潰した後の再設定。
        _caretCtrl.SetSelection(
            Math.Min(anchorM + anchorK * targetCharLen, total),
            Math.Min(caretM + caretK * targetCharLen, total),
            _buffer.Current
        );
        TopLine = savedTopLine; // setter でクランプ+VScrollBar 同期
        ScrollX = savedScrollX; // 同上
        // P7 別エージェント最終レビュー Important-2: TopLine/ScrollX の値が不変(小文書で先頭表示中)
        // だと setter が no-op で PositionCaret 再発火されず、Win32 system caret(SetCaretPos)が
        // 復元前の pos 0 に残る。UIA v2 単一経路に統一した P7 以降は SR の system caret 追跡依存度が
        // 上がるため、Save 直後の system caret 位置ずれを避けるべく明示的に再配置する。
        if (_hasFocus)
            PositionCaret();
        EolMode = eol;
    }

    /// <summary>
    /// P7 I-3 Task 3: <paramref name="eol"/> バイト列を出力バッファへ書き足す。バッファ溢れ時は
    /// TextBufferBuilder へフラッシュしてから追記する(bare append より 1 分岐多いだけの hot path)。
    /// </summary>
    private static void EmitEol(
        byte[] eol,
        ref byte[] outBuf,
        ref int outLen,
        TextBufferBuilder builder
    )
    {
        if (outLen + eol.Length > outBuf.Length)
            FlushBuf(ref outBuf, ref outLen, builder);
        for (int i = 0; i < eol.Length; i++)
            outBuf[outLen++] = eol[i];
    }

    /// <summary>
    /// P7 I-3 Task 3: 出力バッファを TextBufferBuilder に流し込み、outLen をリセット。空なら no-op。
    /// </summary>
    private static void FlushBuf(ref byte[] outBuf, ref int outLen, TextBufferBuilder builder)
    {
        if (outLen == 0)
            return;
        builder.Add(new ReadOnlySpan<byte>(outBuf, 0, outLen));
        outLen = 0;
    }

    /// <summary>
    /// P7 I-3 Task 3: Snapshot 全 EOL がターゲット EOL と一致するかを byte スキャンで判定
    /// (fast-path 判定用=SnapshotText 全文化を回避)。CRLF/CR/LF 混在(target と異なる EOL が
    /// 1 つでも存在する)なら false=非 fast-path 経路で統一が必要。CR がピース境界に跨るケースは
    /// pendingCr で持ち越す(次ピース先頭が LF なら CRLF、そうでなければ CR 単独)。
    /// </summary>
    private static bool IsEolAlreadyUniform(TextSnapshot snap, byte[] targetBytes)
    {
        bool pendingCr = false;
        foreach (var piece in PieceTree.Enumerate(snap.Root))
        {
            var span = piece.Chunk.Span.Slice(piece.ByteStart, piece.ByteLen);
            for (int i = 0; i < span.Length; i++)
            {
                byte b = span[i];
                if (pendingCr)
                {
                    pendingCr = false;
                    if (b == 0x0A)
                    {
                        // 前ピース末尾 CR + 当ピース先頭 LF=CRLF。target が CRLF でなければ NG。
                        if (
                            !(
                                targetBytes.Length == 2
                                && targetBytes[0] == 0x0D
                                && targetBytes[1] == 0x0A
                            )
                        )
                            return false;
                        continue;
                    }
                    // CR 単独。target が CR でなければ NG。今の byte は落とさず後段で処理継続。
                    if (!(targetBytes.Length == 1 && targetBytes[0] == 0x0D))
                        return false;
                }
                if (b == 0x0D)
                {
                    if (i + 1 < span.Length && span[i + 1] == 0x0A)
                    {
                        if (
                            !(
                                targetBytes.Length == 2
                                && targetBytes[0] == 0x0D
                                && targetBytes[1] == 0x0A
                            )
                        )
                            return false;
                        i++;
                    }
                    else if (i + 1 < span.Length)
                    {
                        if (!(targetBytes.Length == 1 && targetBytes[0] == 0x0D))
                            return false;
                    }
                    else
                        pendingCr = true;
                }
                else if (b == 0x0A && !(targetBytes.Length == 1 && targetBytes[0] == 0x0A))
                {
                    return false;
                }
            }
        }
        // 全 span 走査後に残った CR は文書末尾の単独 CR。target が CR でなければ NG。
        if (pendingCr && !(targetBytes.Length == 1 && targetBytes[0] == 0x0D))
            return false;
        return true;
    }

    /// <summary>
    /// P7 I-3 Task 3: [0, <paramref name="pos"/>) に含まれる「改行以外の文字数」と「改行数」を
    /// SnapshotReader(chunked TextReader)で走査して返す。CRLF は 1 改行として数える(旧
    /// <c>CountNonBreakAndBreaks(string, int)</c> と等価)。ConvertEols で char 位置を EOL
    /// 変換前後にマップし直すために使う。8192 char バッファ境界で '\r' が末尾に来るケースは carry で持ち越す。
    /// pos が CRLF の LF を指す(=接頭辞末尾が CR)ケースは \r 単独として 1 改行を計上(境界を跨がない安全側)。
    /// </summary>
    private static (int NonBreakChars, int Breaks) CountNonBreakAndBreaksInSnapshot(
        TextSnapshot snap,
        int pos
    )
    {
        int m = 0,
            k = 0;
        int p = Math.Min(pos, snap.CharLength);
        if (p == 0)
            return (0, 0);
        using var reader = snap.CreateReader();
        char[] buf = new char[8192];
        int consumed = 0;
        int carry = -1; // 前ブロック末尾の '\r' を持ち越し
        while (consumed < p)
        {
            int want = Math.Min(buf.Length, p - consumed);
            int n = reader.Read(buf, 0, want);
            if (n == 0)
                break;
            for (int j = 0; j < n; j++)
            {
                char c = buf[j];
                if (carry >= 0)
                {
                    // 前ブロック末尾の CR。今の char が LF なら CRLF=1 改行、そうでなければ CR 単独=1 改行。
                    if (c == '\n')
                    {
                        k++;
                        consumed++;
                        carry = -1;
                        continue;
                    }
                    k++;
                    carry = -1;
                }
                if (c == '\r')
                {
                    if (j + 1 < n && buf[j + 1] == '\n')
                    {
                        k++;
                        j++;
                        consumed += 2;
                    }
                    else if (j + 1 == n)
                    {
                        carry = '\r';
                        consumed++;
                    }
                    else
                    {
                        k++;
                        consumed++;
                    }
                }
                else if (c == '\n')
                {
                    k++;
                    consumed++;
                }
                else
                {
                    m++;
                    consumed++;
                }
            }
        }
        if (carry >= 0)
            k++;
        return (m, k);
    }

    /// <summary>行の高さ(px)。<see cref="ICharMetrics.LineHeightPx"/> の透過。</summary>
    public int LineHeightPx => _metrics.LineHeightPx;

    // 後続タスク受け口(バッキングは auto-property・本実装は該当タスクで)
    // [Browsable(false)] + [DesignerSerializationVisibility(Hidden)] は
    // Control 派生の public プロパティに対する WFO1000 を回避する意図(デザイナ非対応の宣言)。

    /// <summary>
    /// 可視領域の先頭に置く論理行(0 始まり)。set 時は [0, LineCount-1] にクランプ、
    /// 変化時のみ VScrollBar.Value を追従させて Invalidate。折り返し ON でも TopLine の
    /// 先頭視覚行から描画する(§0-3=論理行の途中から始めない)。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int TopLine
    {
        get => _topLine;
        set
        {
            int clamped = ClampTopLine(value);
            if (clamped == _topLine)
                return;
            _topLine = clamped;
            if (_vscroll.Value != clamped)
                _vscroll.Value = clamped;
            PositionCaret();
            Invalidate();
        }
    }

    /// <summary>
    /// 折り返し桁数(半角換算)。0 以下で折り返し OFF。負値は 0 に丸める。
    /// ON: 水平スクロールバー非表示・視覚行を <c>WrapColumns × 半角1文字幅</c> で折り返す。
    /// OFF: 水平スクロールバー表示(必要な場合)+ <see cref="ScrollX"/> で表示原点を左右シフト。
    /// 変化時: <see cref="ScrollX"/> を 0 にリセット・HScrollBar 表示切替・キャレット再配置・Invalidate。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int WrapColumns
    {
        get => _wrapColumns;
        set
        {
            int clamped = Math.Max(0, value);
            if (_wrapColumns == clamped)
                return;
            _wrapColumns = clamped;
            // P8 Minor-5 / Task 3d: wrap 値変化で Adapter の _lastLineSegs キャッシュ破棄。
            _uia.InvalidateLastLineSegs();
            _scrollX = 0;
            UpdateHorizontalScrollbar();
            PositionCaret();
            Invalidate();
        }
    }

    /// <summary>
    /// 水平スクロール位置(px)。<b>折り返し OFF かつ HScrollBar 表示中のみ有効</b>
    /// (ON 時 / 内容が可視領域に収まり HScroll 非表示の間は 0 固定・set は no-op)。
    /// [0, MaxScrollX] にクランプ(MaxScrollX は HScrollBar.Maximum - LargeChange + 1 相当)。
    /// 変化時のみ HScrollBar.Value を追従・キャレット再配置・Invalidate。
    /// </summary>
    /// <remarks>
    /// HScrollBar 非表示時ガードが無いと、直前まで表示されていたときの
    /// <c>_hscroll.Maximum</c> が残存しており ClampScrollX が非ゼロ値を通してしまう
    /// (=本来スクロール不要なのに描画が左シフトする)。<see cref="UpdateHorizontalScrollbar"/>
    /// の hide 分岐で Maximum/LargeChange をリセットすることでも二重に防いでいる。
    /// </remarks>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int ScrollX
    {
        get => _scrollX;
        set
        {
            if (_wrapColumns > 0)
                return; // 折り返し ON では水平スクロール無効
            if (!_hscroll.Visible)
                return; // HScroll 非表示時は水平スクロール意味なし
            int clamped = ClampScrollX(value);
            if (clamped == _scrollX)
                return;
            _scrollX = clamped;
            if (_hscroll.Value != clamped)
                _hscroll.Value = clamped;
            PositionCaret();
            Invalidate();
        }
    }

    /// <summary>
    /// 行番号マージンを表示するか。true にすると <see cref="MeasureLineNumberWidth"/> 幅のマージンを確保し、
    /// FrameBuilder が右寄せで行番号を発行する(現在行のみ <see cref="ViewportStyle.Foreground"/> で強調)。
    /// 変化時のみ Invalidate。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowLineNumbers
    {
        get => _showLineNumbers;
        set
        {
            if (_showLineNumbers == value)
                return;
            _showLineNumbers = value;
            // 行番号マージン幅は本文 X の起点(bodyX)を変えるためシステムキャレット位置と
            // 水平スクロールの content 幅にも効く。TopLine/WrapColumns setter と同じく
            // Update → PositionCaret → Invalidate の順で反映する(Task 15 レビュー I-1)。
            UpdateHorizontalScrollbar();
            PositionCaret();
            Invalidate();
        }
    }

    /// <summary>
    /// 空白/タブ/EOL の可視化グリフ(中点/矢印)を <see cref="ViewportStyle.WhitespaceGlyph"/> 色で
    /// 本文の上から重ね塗りするか。FrameBuilder は本文とは別 op として個別 DrawText を発行し、
    /// GdiCharMetrics が同 Font を使うため座標のズレなく重なる。変化時のみ Invalidate。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ShowWhitespace
    {
        get => _showWhitespace;
        set
        {
            if (_showWhitespace == value)
                return;
            _showWhitespace = value;
            Invalidate();
        }
    }

    /// <summary>
    /// 文字オフセット範囲(UTF-16)を「セル」枠 + 半透明背景で強調する(P0 の Scintilla セル装飾を継承)。
    /// テキスト選択とは独立した装飾で、単一アクティブ(次の <see cref="HighlightCharRange"/> で
    /// 置き換え・<see cref="ClearHighlight"/> で消える)。両端は [0, CharLength] にクランプ・
    /// UTF-16 low サロゲート中間位置は前方(high)にスナップ。<paramref name="length"/> が負値のときは
    /// 0 として扱う(空範囲=装飾なし相当)。SetSource 前の呼び出しは no-op。
    /// </summary>
    /// <param name="start">開始 UTF-16 文字オフセット。</param>
    /// <param name="length">長さ(UTF-16 コード単位)。負値は 0 として扱う。</param>
    public void HighlightCharRange(int start, int length)
    {
        if (_buffer is null)
            return;
        int s = SnapAndClamp(start);
        // start + length は int 加算だとオーバーフローで負値になり s > e = SelectionRange 例外の
        // 経路が残る(実運用の CharLength は int.MaxValue 未満だが公開 API の契約防御として長型経由)。
        long endLong = (long)start + Math.Max(0, length);
        int endInt = endLong > int.MaxValue ? int.MaxValue : (int)endLong;
        int e = SnapAndClamp(endInt);
        // SnapAndClamp は単純クランプ + サロゲート前方スナップで、単調非減少
        // (証明スケッチ: snap(x) ∈ {x, x-1, 0, CharLength} かつ snap(x) <= x。a <= b の両側で成立)。
        // Math.Max(0, length) と上記オーバーフロー処理で e >= s が数学的に保証される
        // (SelectionRange invariant Start <= End にも合致)。
        var range = new SelectionRange(s, e);
        if (_cellHighlight == range)
            return;
        _cellHighlight = range;
        Invalidate();
    }

    /// <summary>
    /// セルハイライトを消す。現状 null のときは no-op。
    /// </summary>
    public void ClearHighlight()
    {
        if (_cellHighlight is null)
            return;
        _cellHighlight = null;
        Invalidate();
    }

    /// <summary>
    /// キャレット論理行の背景を <see cref="ViewportStyle.CurrentLineBack"/> で塗るか。
    /// <b>選択がある間(_caretCtrl.HasSelection)は塗らない</b>=OnPaint で FrameBuilder への
    /// currentLineLogical に -1 を渡す(選択矩形との視覚的競合を避けるため)。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool HighlightCurrentLine
    {
        get => _highlightCurrentLine;
        set
        {
            if (_highlightCurrentLine == value)
                return;
            _highlightCurrentLine = value;
            Invalidate();
        }
    }

    /// <summary>
    /// 上書き入力モード(Overtype)。true のとき文字挿入は直後 1 文字を潰す=Insert キー(修飾なし)で
    /// OnKeyDown が直接トグルする(Task 9)。改行(<c>\r</c>/<c>\n</c>)の直前では潰さず単純挿入
    /// (Scintilla 互換)。サロゲートペアの high 位置にキャレットがあるときは pair 全体(2 code units)を潰す。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Overtype { get; set; }

    /// <summary>
    /// 読み取り専用モード。true のとき編集経路(OnKeyPress・Task 9〜11 の削除/貼り付け系)を
    /// 全て早期 return する。選択状態やキャレット移動は禁止しない(閲覧用途の想定)。
    /// </summary>
    /// <remarks>
    /// P4 Task 8(§4-2): false→true の切り替え時、IME 未確定期間中なら
    /// <see cref="CancelCompositionAndDefault"/> 経路で ImmNotifyIME(CPS_CANCEL) を通知し、
    /// overlay(<c>_ime</c>)を強制的にクリアする(=読み取り専用に切り替えた瞬間に浮きっぱなしの
    /// 未確定文字が残らない)。未確定期間外の呼び出しは早期 return で no-op=既存の全 setter
    /// 呼び出し(P3 の閲覧テスト群)には副作用が無い。
    /// </remarks>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool ReadOnly
    {
        get => _readOnly;
        set
        {
            if (_readOnly == value)
                return;
            _readOnly = value;
            if (value)
                CancelCompositionAndDefault();
        }
    }
    private bool _readOnly;

    /// <summary>
    /// Enter 押下時に挿入する改行シーケンス。既定は <see cref="LineEnding.Crlf"/>(Windows 標準)。
    /// App 層 (P6) は開いた文書の実測改行(LineEndingDetector)を反映して設定する想定。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public LineEnding EolMode { get; set; } = LineEnding.Crlf;

    // P6 Task 4: App 層互換イベント。TextBuffer の Modified 状態遷移と UI 更新契機を通知する。

    /// <summary>P6 Task 4: <see cref="SetSavePoint"/>(=<see cref="TextBuffer.MarkSaved"/>)呼び出し時に発火(App 層は「保存済み表示」に切替)。</summary>
    public event EventHandler? SavePointReached;

    /// <summary>P6 Task 4: 保存後最初の編集で Modified=false→true 遷移時に発火(App 層は「変更あり」表示に切替)。</summary>
    public event EventHandler? SavePointLeft;

    /// <summary>P6 Task 4: キャレット/選択/表示範囲変化時に発火(App 層のステータスバー更新用)。</summary>
    public event EventHandler? UpdateUI;

    /// <summary>
    /// キャレット/選択移動時の UIA TextSelectionChangedEvent を発火するか。
    /// <b>P3 では受け口のみ</b>(値は読み書きできるが挙動は無し)=P5 の UIA 接続で本挙動化する。
    /// P6 の CSV モードでは false にしてシンクへ移る遷移の一瞬に SR が行を読むのを防ぐ。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool RaiseUiaSelectionEvents { get; set; } = true;

    /// <summary>
    /// SavePoint(<see cref="SetSavePoint"/>)以後にバッファが変更されたか。SetSource 前 / 現ルート ==
    /// 保存時ルート の間は false。P6 の <c>ScintillaHost.Modified</c> と同名(移植先での機械的置換用)。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool Modified => _buffer?.Modified ?? false;

    /// <summary>
    /// Undo 可否(履歴あり)。SetSource 前は false。P6 の <c>ScintillaHost.CanUndo</c> と同名。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool CanUndo => _buffer?.CanUndo ?? false;

    /// <summary>
    /// Redo 可否(Undo 後で新規編集がまだ無い)。SetSource 前は false。P6 の <c>ScintillaHost.CanRedo</c> と同名。
    /// </summary>
    [Browsable(false), DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool CanRedo => _buffer?.CanRedo ?? false;

    /// <summary>
    /// 指定範囲 <c>[start, start+length)</c> を <paramref name="replacement"/> で置換する
    /// (P6 App 層互換 API・設計書 §2-8)。両端はサロゲート中間なら前方スナップ・範囲外は
    /// [0, CharLength] にクランプ。<paramref name="length"/> が負値のときは 0 として扱い挿入となる。
    /// キャレットは置換末尾(<c>start + replacement.Length</c>=snapped 後の値)に移動し
    /// 選択は解除される。<see cref="ReadOnly"/> / SetSource 前は no-op。
    /// </summary>
    /// <remarks>
    /// P6 の <c>ScintillaHost.ReplaceCharRange</c> と同名(App 層検索置換・整形機能等からの機械的
    /// 置換用)。Undo/Redo は <see cref="TextBuffer.Replace"/> 経由で 1 単位として積まれる。
    /// クランプは <see cref="HighlightCharRange"/>/<see cref="EnsureVisibleCharRange"/> と同じ流儀=
    /// <c>start + length</c> の int 加算オーバーフローを long 経由で防ぐ。
    /// _caretCtrl.DesiredXpx リセット・<see cref="AfterEdit"/> 経由の副作用(スクロールバー再計算・
    /// キャレット再配置・追従スクロール・再描画)は編集経路(Task 8〜11)と同じ扱い。
    /// </remarks>
    public void ReplaceCharRange(int start, int length, string replacement)
    {
        if (IsComposing)
            CancelCompositionAndDefault();
        if (_buffer is null || ReadOnly)
            return;
        ArgumentNullException.ThrowIfNull(replacement);
        int s = SnapAndClamp(start);
        // start + length は int 加算だとオーバーフローで負値になり得るため long 経由(EnsureVisibleCharRange
        // と同じ流儀)。負の length は 0 として扱う=start 位置への純挿入になる。
        long endLong = (long)start + Math.Max(0, length);
        int endInt = endLong > int.MaxValue ? int.MaxValue : (int)endLong;
        int e = SnapAndClamp(endInt);
        _buffer.Replace(s, e - s, replacement);
        _caretCtrl.SetTo(s + replacement.Length, _buffer.Current);
        _caretCtrl.DesiredXpx = -1;
        AfterEdit();
    }

    private int ClampTopLine(int value)
    {
        int max = _buffer is null ? 0 : Math.Max(0, _buffer.Current.LineCount - 1);
        if (value < 0)
            return 0;
        if (value > max)
            return max;
        return value;
    }

    /// <summary>
    /// VScrollBar の Maximum / LargeChange を現在の buffer と ClientSize から再計算する。
    /// WinForms VScrollBar の到達可能な最大 Value は "Maximum - LargeChange + 1" のため、
    /// TopLine=maxLine を到達させるには Maximum = maxLine + (LargeChange - 1) と置く必要がある。
    /// 順序: Maximum → LargeChange の順に設定(逆順だと Maximum が小さいときに LargeChange が
    /// 内部で clip されて意図した値にならないケースがある)。
    /// </summary>
    private void UpdateVerticalScrollbar()
    {
        if (_buffer is null)
            return;
        var snap = _buffer.Current;
        int maxLine = Math.Max(0, snap.LineCount - 1);
        // 編集経路(Undo/Delete)で buffer が縮んだ結果 _topLine が新 maxLine を超えて
        // 残っているケースを防御的にクランプする。TopLine セッター経由の変更はここへ入る
        // 前にクランプ済みだが、AfterEdit→UpdateVerticalScrollbar の順で来ると
        // 直前の buffer 縮小分が _topLine に反映されていないためここで補正する必要がある
        // (Task 13 EmptyLineNavigationTests の Enter→Undo 経路で顕在化した既存潜在バグ)。
        if (_topLine > maxLine)
            _topLine = maxLine;
        int visibleLines = Math.Max(1, ClientSize.Height / Math.Max(1, _metrics.LineHeightPx));
        _vscroll.Maximum = maxLine + Math.Max(0, visibleLines - 1);
        _vscroll.LargeChange = visibleLines;
        _vscroll.SmallChange = 1;
        _vscroll.Value = _topLine;
        _vscroll.Enabled = maxLine > 0;
    }

    /// <summary>
    /// HScrollBar の表示可否・Maximum / LargeChange を現在の buffer と ClientSize から再計算する。
    /// - 折り返し ON / 未 SetSource / 内容が可視領域に収まる → 非表示・_scrollX=0・
    ///   Maximum/LargeChange を初期値へリセット(残存値でクランプが緩まないように)
    /// - 折り返し OFF で内容がはみ出す → 表示。可視分の視覚行のうち最長 pixel 幅を上限にする
    ///   (1GB でも計算量は O(可視行数))。
    /// 順序: Maximum → LargeChange → SmallChange → Value(<see cref="UpdateVerticalScrollbar"/> と統一。
    /// 逆順だと Maximum が小さいときに LargeChange が内部で clip されるケースがある)。
    /// </summary>
    private void UpdateHorizontalScrollbar()
    {
        if (_buffer is null || _wrapColumns > 0)
        {
            HideAndResetHScroll();
            return;
        }
        var snap = _buffer.Current;
        int paintWidth = Math.Max(0, ClientSize.Width - _vscroll.Width);
        // HScroll 表示可否を決めるための計算では、まだ表示していない前提で高さいっぱいを見る
        // (可視行がわずかに多めになるだけで最長幅の推定には害がない)。
        int probeHeight = Math.Max(0, ClientSize.Height);
        var rows = ViewportLayout.Build(snap, _topLine, probeHeight, wrapColumns: 0, _metrics);
        int lnWidth = _showLineNumbers ? MeasureLineNumberWidth(snap.LineCount) : 0;
        int maxLineWidthPx = 0;
        foreach (var row in rows)
        {
            if (row.SegmentLength == 0)
                continue;
            string lineText = snap.GetText(row.SegmentStartChar, row.SegmentLength);
            int width = _metrics.MeasureRun(lineText.AsSpan());
            if (width > maxLineWidthPx)
                maxLineWidthPx = width;
        }
        int contentWidth = lnWidth + maxLineWidthPx;
        if (contentWidth <= paintWidth)
        {
            HideAndResetHScroll();
            return;
        }

        // 表示に必要
        int largeChange = Math.Max(1, paintWidth);
        // WinForms 慣習に合わせ Maximum → LargeChange の順で設定
        // (逆順だと Maximum が小さいときに LargeChange が内部で clip されるケースがある)。
        _hscroll.Maximum = contentWidth - 1 + Math.Max(0, largeChange - 1);
        _hscroll.LargeChange = largeChange;
        _hscroll.SmallChange = Math.Max(1, _metrics.MeasureRun("0"));
        int maxScrollX = _hscroll.Maximum - Math.Max(0, largeChange - 1);
        if (_scrollX > maxScrollX)
            _scrollX = Math.Max(0, maxScrollX);
        if (_scrollX < 0)
            _scrollX = 0;
        _hscroll.Value = _scrollX;
        _hscroll.Visible = true;
    }

    /// <summary>
    /// HScrollBar を非表示にし、Maximum/LargeChange を初期値にリセットする。
    /// <see cref="ScrollX"/> は「HScroll 非表示中は set が no-op」で守られているが、
    /// 直前の表示状態で Maximum が非ゼロのまま残ると、内部から <see cref="ClampScrollX"/> 経由で
    /// 触れた場合に非ゼロ値を通してしまう(RenderFrame の一様シフトで内容が左にズレる)。
    /// リセットしておくことで防御を二重化する。
    /// </summary>
    private void HideAndResetHScroll()
    {
        _hscroll.Visible = false;
        // 縮小方向は WinForms の内部 clip が働いても LargeChange=1 に落ち着くだけなので順序不問。
        _hscroll.LargeChange = 1;
        _hscroll.Maximum = 0;
        _scrollX = 0;
        if (_hscroll.Value != 0)
            _hscroll.Value = 0;
    }

    private int ClampScrollX(int value)
    {
        int max = _hscroll.Maximum - Math.Max(0, _hscroll.LargeChange - 1);
        if (max < 0)
            max = 0;
        if (value < 0)
            return 0;
        if (value > max)
            return max;
        return value;
    }

    /// <summary>
    /// 編集(Insert/Delete/Replace)後の共通後処理: スクロールバー再計算+キャレット再配置+
    /// 追従スクロール+再描画。<c>_caretCtrl.DesiredXpx</c> は編集経路では常にリセット(-1)される
    /// 想定なので呼び出し側(OnKeyPress/Task 9〜11 の削除系)で個別に設定する。Task 9〜11 でも
    /// 共用する(§0-6 の一貫性)。
    /// </summary>
    /// <remarks>
    /// 順序は「バッファ変化 → スクロールバー再計算(Update*) → キャレット再配置(PositionCaret)
    /// → 追従スクロール(BringCaretIntoView) → 再描画(Invalidate)」。
    /// - Update*Scrollbar が先: 挿入で総行数/最長行が変わっている可能性があるため、Position/追従の
    ///   前に Maximum/LargeChange を反映する必要がある。
    /// - PositionCaret は BringCaretIntoView の内部で必要な OS 側キャレット反映を先出しする
    ///   (BringCaretIntoView 自体は TopLine/ScrollX の setter を経由するときに PositionCaret を
    ///   間接的に呼ぶが、可視範囲内で TopLine/ScrollX が変わらない編集経路では呼ばれないため)。
    /// - BringCaretIntoView は挿入後キャレットが下端/右端を越えたら TopLine/ScrollX を追随させる。
    /// - Invalidate は最後(BringCaretIntoView 経由の setter が変化なしの場合でも本文が変わっている)。
    /// </remarks>
    // Task 3c: InputRouter の編集系ハンドラ(HandleBack/Delete/Enter/Tab)から呼ぶため internal 化。
    // 既存の内部呼び出し(Ime.cs / この cs 内の Cut/Paste/Undo/Redo/InsertConfirmedText 経路)は
    // 挙動不変(private → internal は同一アセンブリでは可視性のみ拡張)。
    internal void AfterEdit()
    {
        UpdateVerticalScrollbar();
        UpdateHorizontalScrollbar();
        PositionCaret();
        BringCaretIntoView();
        Invalidate();
        // P5 Task 5 / Task 3d: 編集後に RPC スレッド用スナップショットを更新 (Adapter 経由=
        // 元 CacheSnapshot() + `_lastLineSegs = null;` を 1 経路に集約)。_buffer は非 null 経路
        // (AfterEdit は編集経路末尾=SetSource 前は呼ばれない)。
        _uia.OnSnapshotChanged(_buffer!.Current);
        // P5 Task 8: UIA イベント発火(TextChanged は編集経路の唯一の発火点)。
        // 編集は同時に選択位置も動くため TextSelectionChanged も併せて発火。
        _uia.RaiseTextChanged();
        if (RaiseUiaSelectionEvents)
            _uia.RaiseSelectionChanged();
        // P6 Task 4: Modified 遷移(false→true=SavePointLeft / true→false=SavePointReached)を
        // 両方向で発火・常時 UpdateUI を発火。state-first-then-fire で SetSavePoint / ReplaceSource と
        // 揃え、handler 内での再入(SavePointLeft ハンドラが Undo 等で AfterEdit を再呼び出しするケース)
        // でも二重発火させない(§0 設計)。Undo で保存点へ戻る経路も本メソッドが呼ばれるため、
        // SavePointReached を対称に発火しないとタブラベル「*」が消えない実挙動退行(P6 レビュー I-1)。
        bool nowModified = Modified;
        bool shouldFireLeft = !_wasModified && nowModified;
        bool shouldFireReached = _wasModified && !nowModified;
        _wasModified = nowModified;
        if (shouldFireLeft)
            SavePointLeft?.Invoke(this, EventArgs.Empty);
        if (shouldFireReached)
            SavePointReached?.Invoke(this, EventArgs.Empty);
        UpdateUI?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Undo 実行(P3 Task 10)。<see cref="TextBuffer.Undo"/> の結果を反映し、キャレットを
    /// 推奨位置(=Pos + RemovedLen=削除内容が復元された末尾)へ移動する。選択は解除
    /// (Task 8/9 と同じ「キャレットとアンカーを同位置に設定」パターン=<c>_caretCtrl.SetTo</c>)。
    /// <c>_caretCtrl.DesiredXpx</c> は編集経路の一貫性で -1 リセット。SetSource 前 / 履歴なし
    /// (<see cref="TextBuffer.Undo"/> が null) / <see cref="ReadOnly"/> は no-op。
    /// P6 の <c>ScintillaHost.Undo</c> と同名(<c>Undo</c> は <see cref="Control"/> の直接メンバではなく
    /// <c>TextBoxBase</c> で導入される名前=本クラスは Control 直接派生のため隠すべき同名メソッドが
    /// 無く <c>new</c> キーワード不要)。
    /// </summary>
    /// <remarks>
    /// ReadOnly ガードはメソッド本体側で行う(<see cref="OnKeyDown"/> の <c>Keys.Z</c> case にも
    /// <c>when !ReadOnly</c> を残しているが、これは二重防御=App 層 <c>MainForm</c> のメニュー
    /// shortcut は <see cref="OnKeyDown"/> を経由せず本メソッドを直接呼ぶため、本体側で弾く必要が
    /// ある)。Scintilla の <c>SCI_UNDO</c> が read-only モードで no-op になる挙動と合わせる=P6 で
    /// <c>ScintillaHost</c> を本コントロールへ機械的置換した際に CSV グリッドモード
    /// (<c>CsvController.Editor.ReadOnly = true</c>)などの ReadOnly 経路で挙動が退行しない。
    /// </remarks>
    public void Undo()
    {
        if (IsComposing)
            CancelCompositionAndDefault(); // §4-6(Task 13 レビュー I-1)
        if (_buffer is null || ReadOnly)
            return;
        var r = _buffer.Undo();
        if (r is null)
            return;
        int pos = Math.Clamp(r.Value.CaretPos, 0, _buffer.Current.CharLength);
        _caretCtrl.SetTo(pos, _buffer.Current);
        _caretCtrl.DesiredXpx = -1;
        AfterEdit();
    }

    /// <summary>
    /// Redo 実行(P3 Task 10)。<see cref="TextBuffer.Redo"/> の結果を反映し、キャレットを
    /// 推奨位置(=Pos + InsertedLen=再挿入内容の末尾)へ移動する。それ以外の副作用は
    /// <see cref="Undo"/> と同じ(選択解除・desiredXpx リセット・AfterEdit で追従スクロール・
    /// SetSource 前 / <see cref="ReadOnly"/> は no-op)。
    /// </summary>
    public void Redo()
    {
        if (IsComposing)
            CancelCompositionAndDefault(); // §4-6(Task 13 レビュー I-1)
        if (_buffer is null || ReadOnly)
            return;
        var r = _buffer.Redo();
        if (r is null)
            return;
        int pos = Math.Clamp(r.Value.CaretPos, 0, _buffer.Current.CharLength);
        _caretCtrl.SetTo(pos, _buffer.Current);
        _caretCtrl.DesiredXpx = -1;
        AfterEdit();
    }

    /// <summary>
    /// SavePoint を打つ(<see cref="TextBuffer.MarkSaved"/> の別名)。以後 <see cref="Modified"/> は
    /// 現ルートとの参照比較で判定される。SetSource 前は no-op。P6 の <c>ScintillaHost.SetSavePoint</c>
    /// と同名(App 層 Save 経路からの機械的置換用)。
    /// </summary>
    public void SetSavePoint()
    {
        _buffer?.MarkSaved();
        // P6 Task 4: 保存直後は「未変更」状態=次の編集が Modified=false→true 遷移として
        // SavePointLeft を発火できるよう _wasModified も同期リセット。
        _wasModified = false;
        SavePointReached?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 保存点を破棄して <see cref="Modified"/> を true に固定する(<see cref="TextBuffer.MarkUnsaved"/> の
    /// 別名)。バックアップ復元のように「fresh バッファだが内容はどのファイルにも保存されていない」文書を
    /// dirty として扱うための <see cref="SetSavePoint"/> の逆操作。SetSource 前は no-op
    /// (dirty にすべき本文がまだ存在しない)。
    /// </summary>
    public void ClearSavePoint()
    {
        if (_buffer is null)
            return;
        _buffer.MarkUnsaved();
        // SetSavePoint と対称: _wasModified を同期し(次の AfterEdit が false→true 遷移を誤検出して
        // SavePointLeft を二重発火しないように)、App 層(タブ「*」/タイトルバー)へは SavePointLeft で
        // 「変更あり」表示への切替を通知する。
        _wasModified = true;
        SavePointLeft?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// EmptyUndoBuffer 相当(<see cref="TextBuffer.ClearUndo"/> の別名)。Undo/Redo 履歴を破棄する。
    /// 保存点は維持されるため <see cref="Modified"/> の値は変わらない。SetSource 前は no-op。
    /// P6 の <c>ScintillaHost.EmptyUndoBuffer</c> と同名。
    /// </summary>
    public void EmptyUndoBuffer() => _buffer?.ClearUndo();

    /// <summary>
    /// 選択範囲のテキストをクリップボード(<see cref="TextDataFormat.UnicodeText"/> 固定・設計書 §0-10)へ書き込む。
    /// 選択なしのときは no-op(=クリップボード内容は保持)。本文不変=<see cref="ReadOnly"/> でも動く
    /// (Notepad と同挙動)。SetSource 前は no-op。
    /// </summary>
    /// <remarks>
    /// P6 の <c>ScintillaHost.Copy</c> と同名(App 層メニュー配線=<c>_docs.Active?.Editor.Copy()</c>
    /// と機械的置換用)。<see cref="Clipboard.SetText(string, TextDataFormat)"/> は STA 必須=
    /// 本コントロールが WinForms UI スレッド専用契約のため常に満たされる。
    /// 「行末改行がない選択のときは 1 行選択と見なして EOL を付ける」等の Scintilla 独自仕様は
    /// v1 では真似ず、素直に選択文字列だけを扱う(設計書 Task 11)。
    /// </remarks>
    public void Copy()
    {
        if (_buffer is null)
            return;
        var (s, en) = GetSelectionCharRange();
        if (s == en)
            return;
        string text = _buffer.Current.GetText(s, en - s);
        Clipboard.SetText(text, TextDataFormat.UnicodeText);
    }

    /// <summary>
    /// 選択範囲のテキストをクリップボードへ書き込み、その範囲を削除する。
    /// <see cref="ReadOnly"/> / 選択なし / SetSource 前は no-op(現行 Scintilla と一致)。
    /// キャレットは削除位置(=元の選択開始オフセット)へ移動し、選択は解除される。
    /// </summary>
    /// <remarks>
    /// P6 の <c>ScintillaHost.Cut</c> と同名。<see cref="Copy"/> → <see cref="TextBuffer.Replace"/>
    /// で「クリップボード書き込み → 本文削除」の順に実行する(Copy 失敗時に本文だけ消える事故を
    /// 防ぐ=<see cref="Clipboard.SetText(string, TextDataFormat)"/> が例外を投げると本メソッドも
    /// 上に throw して <see cref="AfterEdit"/> へ到達しない)。
    /// </remarks>
    public void Cut()
    {
        if (IsComposing)
            CancelCompositionAndDefault(); // §4-6(Task 13 レビュー I-1)
        if (_buffer is null || ReadOnly)
            return;
        var (s, en) = GetSelectionCharRange();
        if (s == en)
            return;
        Copy();
        _buffer.Replace(s, en - s, "");
        _caretCtrl.SetTo(s, _buffer.Current);
        _caretCtrl.DesiredXpx = -1;
        AfterEdit();
    }

    /// <summary>
    /// クリップボードの <see cref="TextDataFormat.UnicodeText"/> をキャレット位置に挿入する。
    /// 選択があるときは置換。挿入後のキャレットは挿入末尾に位置し、選択は解除される。
    /// <see cref="ReadOnly"/> / UnicodeText が無い or 空 / SetSource 前は no-op。
    /// </summary>
    /// <remarks>
    /// P6 の <c>ScintillaHost.Paste</c> と同名。<see cref="Clipboard.ContainsText(TextDataFormat)"/>
    /// で先にチェックしても実装差で空文字列を返すケースが理論上残り得るため、防御的に
    /// <c>string.IsNullOrEmpty</c> でも早期 return する(空文字列 Replace は本文不変だが履歴に
    /// 積む副作用があるため避けたい)。
    /// </remarks>
    public void Paste()
    {
        if (IsComposing)
            CancelCompositionAndDefault(); // §4-6(Task 13 レビュー I-1)
        if (_buffer is null || ReadOnly)
            return;
        if (!Clipboard.ContainsText(TextDataFormat.UnicodeText))
            return;
        string text = Clipboard.GetText(TextDataFormat.UnicodeText);
        if (string.IsNullOrEmpty(text))
            return;
        var (s, en) = GetSelectionCharRange();
        _buffer.Replace(s, en - s, text);
        _caretCtrl.SetTo(s + text.Length, _buffer.Current);
        _caretCtrl.DesiredXpx = -1;
        AfterEdit();
    }

    /// <summary>
    /// 文書全体を選択する(<see cref="SelectionAnchor"/>=0・<see cref="CaretCharOffset"/>=CharLength)。
    /// SetSource 前は no-op(CharLength=0 で空選択=<see cref="SetSelectionAnchored"/> が
    /// _buffer null で早期 return)。
    /// </summary>
    /// <remarks>
    /// P6 の <c>ScintillaHost.SelectAll</c> と同名。<see cref="Control.SelectAll"/> は
    /// <see cref="TextBoxBase"/> 以下でのみ導入されるため、Control 直接派生の本クラスでは
    /// 隠すべき同名メソッドが無く <c>new</c> キーワード不要。OnKeyDown の Ctrl+A case
    /// (Task 6)は <see cref="SetSelectionAnchored(int, int)"/> を直接呼んでいるため
    /// 本メソッド経由ではないが、App 層メニュー "すべて選択" などから直接呼ばれることを想定。
    /// </remarks>
    public void SelectAll() => SetSelectionAnchored(0, _buffer?.Current.CharLength ?? 0);

    /// <summary>診断用(テストで文書全体を取得)。SetSource 前は空文字列。</summary>
    internal string GetText() =>
        _buffer?.Current.GetText(0, _buffer.Current.CharLength) ?? string.Empty;

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        UpdateVerticalScrollbar();
        UpdateHorizontalScrollbar();
        PositionCaret();
    }

    /// <summary>
    /// フォーカスを受けたときにシステムキャレット(幅 2px・高さ LineHeightPx)を作成し、
    /// 現在の <c>_caretCtrl.Caret</c> オフセットへ位置決めして表示する。1 ウィンドウにつき Windows は
    /// 1 個のキャレットしか保持しないため、必ず OnLostFocus で DestroyCaret すること。
    /// </summary>
    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        _hasFocus = true;
        // SetSource 前は buffer が無く PositionCaret が SetCaretPos を呼ばないため、
        // ShowCaret のみ走ると未定義位置(実装依存)にキャレットが出る。SetSource 前は
        // キャレットを生成しない(次に focus を得るときに再セットアップされる)。
        if (_buffer is null)
            return;
        NativeMethods.CreateCaret(Handle, nint.Zero, _caretWidthPx, _metrics.LineHeightPx);
        PositionCaret();
        NativeMethods.ShowCaret(Handle);

        // P5 Task 9: フォーカス獲得時の UIA イベント明示発火(初出 `e24494a`・v1 ScintillaHost 踏襲)。
        // AutomationFocusChangedEvent は UIA プロバイダの標準作法。TextSelectionChangedEvent は
        // フォーカス時にキャレット位置を SR へ再提示するための意図的な追加発火で、CSV モードは
        // RaiseUiaSelectionEvents=false で抑止する。契約は EditorControlUiaFocusEventTests で固定。
        // 発火は WM_GETOBJECT(UiaRootObjectId)で実際に served になったプロバイダからのみ行う
        // (UiaTextHostAdapter.RaiseUia の _provider null ガード)。提供していないプロバイダから
        // 発火すると UIA→MSAA ブリッジのエコーが SR のフォーカス追跡を乗っ取る事故が v1 で
        // 起きている(`d1a57af`)ため、この前提を崩さないこと。
        _uia.RaiseFocusChanged();
        if (RaiseUiaSelectionEvents)
            _uia.RaiseSelectionChanged();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        // P4 Task 8(§4-3): 未確定期間中にフォーカスを失う場合、まず IME 側へ
        // CPS_COMPLETE を通知して「確定」を試みる(Scintilla 互換=ユーザーの入力途中を
        // 失わせない)。ImmNotifyIME が届かない環境(IME 無効/取得失敗)でも overlay
        // (_ime)は必ず落として、base の後続処理(_hasFocus=false / DestroyCaret)より前に
        // フィールド状態を整えておく=直後の Invalidate/paint で古い overlay が浮かない。
        // Task 3a: 上記ロジックは ImeController.Complete() が bit-perfect に担う
        // (IsActive ガード + Ctx.CompleteComposition + _ime クリア + host.Invalidate)。
        _imeCtrl.Complete();
        base.OnLostFocus(e);
        _hasFocus = false;
        NativeMethods.DestroyCaret();
    }

    /// <summary>
    /// P4 IME 経路。<see cref="OnKeyDown"/>/<see cref="OnKeyPress"/> は書き換えず、WndProc で
    /// WM_IME_* を横取りする(§0-4)。P4 Task 4/5/6/7/8 で WM_IME_SETCONTEXT /
    /// WM_IME_STARTCOMPOSITION / WM_IME_COMPOSITION(GCS_COMPSTR + GCS_RESULTSTR)/
    /// WM_IME_ENDCOMPOSITION を処理済。各 case は必ず <c>return;</c> で終える
    /// (末尾の <c>base.WndProc(ref m)</c> は unhandled 用=<c>return;</c> を忘れると
    /// 二重処理となり、base の既定 IME 挙動が KeyPress を re-post 等して 1 Splice=1 Undo
    /// が崩れる)。
    /// </summary>
    protected override void WndProc(ref Message m)
    {
        // P5 Task 6: UIA プロバイダ配線 ---- 先頭で処理
        // Task 3d: プロバイダ生成 + ReturnRawElementProvider + self-served フラグ更新は Adapter へ委譲
        // (§C.4=WndProc 分岐そのものは本体側に残す)。
        if (m.Msg == NativeMethods.WM_GETOBJECT)
        {
            long objid = m.LParam.ToInt64();
            if (objid == NativeMethods.UiaRootObjectId)
            {
                m.Result = _uia.HandleWmGetObject(Handle, m.WParam, m.LParam);
                return;
            }
            _uia.MarkGetObjectNotServed();
            // OBJID_CLIENT (=-4) / OBJID_WINDOW (=0) 等は base=DefWindowProc に流す
            // (=自前で MSAA プロキシを作らない=ネイティブ表面原則 §2-7)
        }

        // P5 Task 7: ネイティブ表面原則 = 本文非公開(WM_GETTEXT / WM_GETTEXTLENGTH に応答しない)
        if (m.Msg == NativeMethods.WM_GETTEXT || m.Msg == NativeMethods.WM_GETTEXTLENGTH)
        {
            m.Result = IntPtr.Zero;
            return;
        }

        // Task 3a: WM_IME_* は ImeController に完全委譲する (旧 OnIme{SetContext,StartComposition,
        // Composition,EndComposition} は削除)。SETCONTEXT のみ lParam マスク後に base.WndProc へ
        // 流す必要があり、他 3 者は m.Result=Zero + return で消化する。
        switch (m.Msg)
        {
            case NativeMethods.WM_IME_SETCONTEXT:
                ImeController.MaskSetContextLParam(ref m);
                base.WndProc(ref m);
                return;
            case NativeMethods.WM_IME_STARTCOMPOSITION:
                _imeCtrl.OnStartComposition();
                m.Result = IntPtr.Zero;
                return;
            case NativeMethods.WM_IME_COMPOSITION:
                _imeCtrl.OnComposition(m.LParam.ToInt64());
                m.Result = IntPtr.Zero;
                return;
            case NativeMethods.WM_IME_ENDCOMPOSITION:
                _imeCtrl.OnEndComposition();
                m.Result = IntPtr.Zero;
                return;
        }
        base.WndProc(ref m);
    }

    // Task 3d: _provider / _testHook_LastGetObjectServed は UiaTextHostAdapter (_uia) へ移譲済み。
    // WM_GETOBJECT (UiaRootObjectId) 分岐は _uia.HandleWmGetObject を呼び、
    // non-UiaRootObjectId 経路は _uia.MarkGetObjectNotServed を呼ぶ (§C.4 準拠)。

    // テスト用ヘルパ(internal・EditorControlImeTests から呼ぶ)。
    // WndProc は protected のためテストから直接呼べない=WM_IME_SETCONTEXT の lParam
    // マスク挙動を検証するための最小の受け口。
    internal void __TestProcessMessage(ref Message m) => WndProc(ref m);

    /// <summary>
    /// 与えられた UTF-16 char offset のクライアント座標(px)と可視性を算出する純ロジック。
    /// - Visible=false: TopLine 未到達 / paintHeight を超える論理行 / y &gt;= paintHeight
    /// - Visible=true: (X, Y) は「行番号マージン含む・_scrollX を引く前」の座標
    /// </summary>
    /// <remarks>
    /// 折り返し ON 時は TopLine ～ 対象行までの各論理行に対して <c>LineLayout.Wrap</c>
    /// を呼び直す(1 論理行ずつ GetText + Wrap)。Task 14 のベンチで顕在化するようなら
    /// Frame の再利用等で最適化する(Task 9 レビュー M-3 の申し送り)。
    /// Task 10 レビュー I-1 対応: 積み上げループ内で paintHeight 超えを検出したら早期退避する
    /// (100 万行のような巨大文書でキャレットが末尾方向にあるとき無駄な Wrap を避けるため)。
    /// </remarks>
    // Task 3d: UiaTextHostAdapter.ComputeBoundingRectangles / ComputeOffsetFromScreenPoint から
    // 呼び出すため internal 化 (元 private・呼び出し元は UI thread ドキュメントされている)。
    // 非 Uia 用途の内部呼び出し (PositionCaret / BringCaretIntoView / PointFromCharOffset /
    // IImeOverlayHost.ComputeCaretPoint) は引き続き同一アセンブリから呼ぶため可視性拡張のみで影響なし。
    internal (int X, int Y, bool Visible) ComputeCaretPoint(int offset)
    {
        if (_buffer is null)
            return (0, 0, false);
        var snap = _buffer.Current;
        int logicalLine = snap.GetLineIndexOfChar(offset);

        // TopLine 未到達なら不可視(スクロールで対象行が上にはみ出している)
        if (logicalLine < _topLine)
            return (0, 0, false);

        int lineStart = snap.GetLineStart(logicalLine);
        int lineEnd = snap.GetLineEnd(logicalLine, includeBreak: false);
        int lineLen = lineEnd - lineStart;
        string lineText = lineLen == 0 ? string.Empty : snap.GetText(lineStart, lineLen);
        int maxWidthPx = _wrapColumns > 0 ? _wrapColumns * _metrics.MeasureRun("0") : 0;
        var segments = LineLayout.Wrap(lineText, maxWidthPx, _metrics);

        int caretInLine = offset - lineStart;

        // 対象がどの視覚セグメントに属するかを決める。
        // - 通常は「seg.OffsetInLine + seg.Length で終わる直前」まで
        // - 最終セグメントに限り「末尾ちょうど」も許容(EOL キャレット位置)
        int segIdx = segments.Count - 1;
        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            int segEnd = seg.OffsetInLine + seg.Length;
            if (caretInLine < segEnd || (i == segments.Count - 1 && caretInLine == segEnd))
            {
                segIdx = i;
                break;
            }
        }
        var chosenSeg = segments[segIdx];
        int localOffset = caretInLine - chosenSeg.OffsetInLine;
        var segSpan = lineText.AsSpan(chosenSeg.OffsetInLine, chosenSeg.Length);
        int xInSeg = PixelMapper.OffsetToPx(segSpan, localOffset, _metrics);

        int lineHeight = _metrics.LineHeightPx;
        int paintHeight = PaintHeightPx;

        // TopLine の先頭視覚行を Y=0 として、対象視覚行までの積み上げ視覚行数を算出。
        // paintHeight を超えたら以降の Wrap は無駄なので早期退避(Task 10 I-1)。
        int visualRowsBeforeThisLine = 0;
        for (int line = _topLine; line < logicalLine; line++)
        {
            int lStart = snap.GetLineStart(line);
            int lEnd = snap.GetLineEnd(line, includeBreak: false);
            int lLen = lEnd - lStart;
            string lText = lLen == 0 ? string.Empty : snap.GetText(lStart, lLen);
            var segs = LineLayout.Wrap(lText, maxWidthPx, _metrics);
            visualRowsBeforeThisLine += segs.Count;
            if (visualRowsBeforeThisLine * lineHeight >= paintHeight)
                return (0, 0, false);
        }
        int totalVisualRow = visualRowsBeforeThisLine + segIdx;

        int lnWidth = _showLineNumbers ? MeasureLineNumberWidth(snap.LineCount) : 0;
        int x = lnWidth + xInSeg;
        int y = totalVisualRow * lineHeight;

        // 下端超過(paint 領域の高さ以上)なら不可視
        if (y >= paintHeight)
            return (0, 0, false);

        return (x, y, true);
    }

    /// <summary>
    /// <c>_caretCtrl.Caret</c>(UTF-16 char offset)からクライアント座標(px)を算出し、
    /// システムキャレット位置に反映する。可視外(TopLine 未到達 / 下端超過)は
    /// 見えない位置 (-1000, -1000) へ退避。フォーカス無し・buffer 未設定時は何もしない。
    /// 折り返し OFF 時は最終位置から <see cref="ScrollX"/> を引いてから SetCaretPos する。
    ///
    /// P4 Task 11: <see cref="IsComposing"/> 中は「未確定文字列内の IME カーソル位置」
    /// (<c>_ime.Start + _ime.CursorPos</c>)へキャレットを置く=IME 内で左右矢印を押した
    /// ときにシステムキャレットが追従するようにする。CursorPos の prefix 幅は
    /// <see cref="_underlineFontCache"/>(=Task 9 で描画に使う overlay フォント)で
    /// <see cref="TextRenderer.MeasureText"/> して加算する(<see cref="DrawImeOverlay"/>
    /// と同じ font/flags で測ることで、描画上の位置とピクセル整合を取る)。
    ///
    /// <para>Perf 注記: 未確定中のみ <see cref="Control.CreateGraphics"/> を都度作る=
    /// 未確定期間は入力の合間で相対的に短いため v1 では許容。将来 <c>_metrics</c> に
    /// 未確定文字列用の Measure API を持たせる余地がある(計画書 §Task 11 Follow-ups)。</para>
    /// </summary>
    private void PositionCaret()
    {
        if (!_hasFocus || _buffer is null)
            return;

        // P4 Task 11: 未確定中は IME 内カーソル位置 (_ime.Start + _ime.CursorPos) に SetCaretPos。
        // 非 IME 経路の前に分岐させる(視覚的にキャレット位置を反映する、というセマンティクスは同じ)。
        // Task 3a: _ime は ImeController に移譲済=state は _imeCtrl.State 経由で読む。
        if (IsComposing)
        {
            var ime = _imeCtrl.State;
            var (x, y, visible) = ComputeCaretPoint(ime.Start);
            if (!visible)
            {
                // 非 IME 経路と対称: 不可視時は画面外に退避してゴースト残留を防ぐ(Task 11 レビュー M-2)。
                NativeMethods.SetCaretPos(-1000, -1000);
                return;
            }
            // _ime.CursorPos は SnapCursorPos で 0..Text.Length にクランプ済(Task 2/6)だが、
            // 悪意/誤動作 IME 対策として範囲外を防御的にクランプ(0 なら prefix="" で幅 0=OK)。
            int cur = Math.Clamp(ime.CursorPos, 0, ime.Text.Length);
            string prefix = ime.Text[..cur];
            using var g = CreateGraphics();
            Size sz = TextRenderer.MeasureText(
                g,
                prefix,
                _underlineFontCache,
                new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix
            );
            NativeMethods.SetCaretPos(x - _scrollX + sz.Width, y);
            // Task 12: スクロール変更等で IME 行の client 座標が動いた=候補窓も追従させる。
            // NotifyCandidateWindow は自前で ComputeCaretPoint を呼び直すため、visible 分岐は
            // ここで先に済んでいるが二重呼びは低コスト(未確定中のみ・GetContext 1 回)。
            _imeCtrl.NotifyCandidateWindow();
            return;
        }

        var (cx, cy, cvisible) = ComputeCaretPoint(_caretCtrl.Caret);
        if (cvisible)
            NativeMethods.SetCaretPos(cx - _scrollX, cy);
        else
            NativeMethods.SetCaretPos(-1000, -1000);
    }

    /// <summary>
    /// <see cref="AppSettings"/> からフォント/テーマ/表示設定を反映する。App 層の
    /// <c>EditorAppearance.Apply</c>(Scintilla ホスト向け)の自作コントロール版で、
    /// P6 で App 層から呼ばれることを想定している(P2 時点では未接続=Task 14 の smoke で目視確認)。
    ///
    /// 挙動:
    /// - フォント: 既存 Font を Dispose して新 Font に差し替え、<see cref="GdiCharMetrics"/> も再構築する
    ///   (LineHeightPx が変わるため後段の VScroll/HScroll 再計算とキャレット再配置が必須)。
    /// - テーマ: <see cref="AppearanceThemes.ById"/> で解決し、<see cref="ViewportStyle"/> を算出。
    ///   現在行/行番号/空白グリフの色は fore/back のブレンドで導出(現行 App 層 Blend の移植)。
    ///   BackColor は <see cref="Graphics.Clear"/> 用に Background と一致させる(<see cref="RenderFrame"/>
    ///   の一様シフト時に右側の隙間が同色で埋まる不変を維持)。
    /// - 表示設定: <see cref="ShowLineNumbers"/>/<see cref="ShowWhitespace"/>/<see cref="HighlightCurrentLine"/>
    ///   /<see cref="WrapColumns"/> をフィールドへ直接反映(setter の Invalidate/HScroll 再計算に頼らず、
    ///   末尾でまとめて Update*Scrollbar/PositionCaret/Invalidate を 1 回ずつ呼ぶ)。
    ///   <see cref="ScrollX"/> は 0 にリセット(折り返し設定が変わっても不整合を残さないため)。
    /// - Task 13 では <c>TabWidth</c>/<c>TabsToSpaces</c> は反映しない(P3=編集入力タスクの担当・YAGNI)。
    /// </summary>
    public void ApplyAppearance(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        // フォント差し替え + GdiCharMetrics 再構築(古い Font は明示的に Dispose して GDI HFONT リーク回避)。
        // 例外安全: newFont / newMetrics を両方作り切ってから旧 Font を Dispose する。
        // GdiCharMetrics のコンストラクタが throw した場合は newFont も破棄して呼び出し元へ propagate
        // (旧 _font / _metrics は生きたまま=次回 OnPaint も従前の高さで安全に描画できる)。
        var newFont = new Font(
            string.IsNullOrEmpty(settings.FontName) ? "ＭＳ ゴシック" : settings.FontName,
            settings.FontSize > 0 ? settings.FontSize : 12f
        );
        GdiCharMetrics newMetrics;
        try
        {
            newMetrics = new GdiCharMetrics(newFont);
        }
        catch
        {
            newFont.Dispose();
            throw;
        }
        _font.Dispose();
        _underlineFontCache.Dispose();
        _targetFontCache.Dispose(); // Task 10
        _font = newFont;
        _underlineFontCache = new Font(_font, _font.Style | FontStyle.Underline);
        _targetFontCache = new Font(_font, _font.Style | FontStyle.Underline | FontStyle.Bold); // Task 10
        _metrics = newMetrics;

        // テーマから ViewportStyle 算出 + Graphics.Clear 用 BackColor 同期
        var theme = AppearanceThemes.ById(settings.Theme);
        _style = BuildStyle(theme, settings.HighlightCurrentLine);
        BackColor = FromRgb(theme.BackRgb);

        // 表示設定はフィールドへ直接反映(末尾でまとめて Invalidate/Update するため setter を経由しない)
        _showLineNumbers = settings.ShowLineNumbers;
        _showWhitespace = settings.ShowWhitespace;
        _highlightCurrentLine = settings.HighlightCurrentLine;
        // キャレット太さ(弱視のキャレット視認性・yedit-sighted-users-first-class)
        _caretWidthPx = Math.Clamp(settings.CaretWidth, 1, 5);
        // WrapColumns の実値が変わったときだけ ScrollX をリセットする(フォント色だけ変更等で
        // 横スクロール位置が不用意にホームへ戻る副作用を避ける)。折り返し ON への遷移では
        // ScrollX=0 が必要=UpdateHorizontalScrollbar 内の HideAndResetHScroll でも 0 にされるが、
        // ここでも先に落としておくことで PositionCaret が過渡的な旧 _scrollX を参照するのを防ぐ。
        int oldWrapColumns = _wrapColumns;
        _wrapColumns = Math.Max(0, settings.WrapColumnEnabled ? settings.WrapColumn : 0);
        if (_wrapColumns != oldWrapColumns)
            _scrollX = 0;

        // LineHeightPx / 折り返し設定が変わった可能性があるので両スクロールバーを再計算 →
        // キャレット再配置。
        UpdateVerticalScrollbar();
        UpdateHorizontalScrollbar();

        // フォーカス保持中に LineHeightPx が変わったら system caret を作り直す
        // (前回 OnGotFocus 時の古い高さのままだと視覚的にキャレットが行高と合わない)。
        // フォーカス無し時は次回 OnGotFocus で新しい _metrics.LineHeightPx を使って作られる。
        if (_hasFocus)
        {
            NativeMethods.DestroyCaret();
            NativeMethods.CreateCaret(Handle, nint.Zero, _caretWidthPx, _metrics.LineHeightPx);
            NativeMethods.ShowCaret(Handle);
        }
        PositionCaret();
        Invalidate();
        // Task 12: フォント変更後に IME へ未確定文字列用フォントを再通知(本文と候補窓のメトリクス整合)。
        _imeCtrl.NotifyCompositionFont();
        // P8 Minor-5 / Task 3d: metrics/wrap 変化で Adapter の _lastLineSegs キャッシュ破棄。
        _uia.InvalidateLastLineSegs();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        // Task 3d: bounds キャッシュ更新は Adapter へ委譲 (元 UpdateBoundsCache)。
        _uia.OnBoundsChanged();
    }

    protected override void OnLocationChanged(EventArgs e)
    {
        base.OnLocationChanged(e);
        // Task 3d: bounds キャッシュ更新は Adapter へ委譲 (元 UpdateBoundsCache)。
        _uia.OnBoundsChanged();
    }

    // Task 3d (§C.4 例外解消): OnHandleCreated / OnHandleDestroyed は EditorControl 本体側に統一。
    // 元 EditorControl.Uia.cs 帰属を解消し、他の OnXxx オーバーライドと同じ場所 (本体) にまとめる。
    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Adapter への通知: _hwnd キャッシュ + 初期 bounds 計算 (元 _hwnd = Handle + UpdateBoundsCache)。
        _uia.OnHandleCreated();
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        // 元コード: _hwnd = IntPtr.Zero を base 呼び出し前に実施 → Adapter に委譲。
        _uia.OnHandleDestroyed();
        base.OnHandleDestroyed(e);
    }

    /// <summary>
    /// GDI ハンドル(Font)を解放する。P6 でタブ毎にインスタンス生成/破棄する運用のため、
    /// 生存中に確保した Font が Control 破棄時に必ず解放されるようにする。
    /// </summary>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font.Dispose();
            // Task 10: ApplyAppearance と対称に IME overlay 用フォントも解放する
            // (Task 9 で追加した _underlineFontCache は Dispose 追加漏れの補正込み・§0-6)。
            _underlineFontCache.Dispose();
            _targetFontCache.Dispose();
        }
        base.Dispose(disposing);
    }
}
