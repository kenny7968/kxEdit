using System.ComponentModel;
using System.Runtime.InteropServices;
using kxEdit.Core.Buffers;
using kxEdit.Core.Editing;
using kxEdit.Core.Layout;
using kxEdit.Core.Settings;
using kxEdit.Core.Text;
using kxEdit.Editor.Abstractions;
// System.Windows.Forms.SelectionRange(MonthCalendar 用)と同名のため別名で解決する。
using SelectionRange = kxEdit.Core.Layout.SelectionRange;

namespace kxEdit.Editor;

/// <summary>
/// P2 で導入する自作エディットコントロール。P1 の <see cref="TextBuffer"/>/<see cref="TextSnapshot"/>
/// をソースに、Layout 層(<c>ViewportLayout</c>/<c>FrameBuilder</c>)が組み立てた <see cref="Frame"/> を
/// GDI 呼び出しで描画する。P6 で ScintillaHost を完全置換・P7 で並行運用終了(NVDA が Scintilla クラス名を
/// 特別扱いする問題を回避する v2 UIA 単一経路の本命実装)。UI スレッド専用
/// (<see cref="GdiCharMetrics"/>・<c>SetSource</c> は 1 度だけ)。
/// </summary>
public sealed partial class EditorControl : Control, kxEdit.Accessibility.IUiaTextHost
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

    // 2026-08-22 A-6: 可視域最上段が属する視覚セグメント index(設計書 不変条件 I-2)。
    // 折り返し OFF では常に 0=全式が導入前に退化する(I-3)。
    // 「セグメント index の意味が変わる契機」では 0 に戻す(SetSource / ReplaceSource /
    // TopLine セッター / WrapColumns セッター / ApplyAppearance / VScrollBar の防御クランプ)。
    // 編集ではリセットしない(巨大段落の途中を編集するたび段落先頭へ飛ぶのを避ける。
    // 実セグメント数を超えた場合は ViewportLayout.Build 側でクランプされる)。
    private int _topSegment;
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
    // (1〜5)を反映。弱視のキャレット視認性要件(設計原則 kxedit-sighted-users-first-class)。
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
    private volatile kxEdit.Core.Layout.Frame? _lastFrame;

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
        _topSegment = 0;
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
    /// <para>
    /// <b>副作用を追加・削除するときは <see cref="ConvertEols"/> も見ること</b> —— A-11(2026-08-28)で
    /// <c>ConvertEols</c> は本メソッドを呼ばなくなり、必要な副作用だけを<b>明示列挙</b>する形になった。
    /// 列挙の同期はコードでもテストでも守られていない(ここに副作用を 1 行足しても
    /// <c>ConvertEols_*</c> は 1 件も赤くならない)。同種の列挙は <see cref="SetSource"/> /
    /// <c>AfterEdit</c> にもあり、現在 4 箇所に散っている。
    /// </para>
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
        _topSegment = 0;
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
    /// 可視の視覚行を列挙する唯一の入口。<c>OnPaint</c> と <see cref="GetVisibleCharRange"/> が
    /// 同じ起点 (TopLine, TopSegment) と同じ折り返し設定を使うことを、言葉の約束ではなく
    /// 呼び出しの共有で保証する(「どこまで見えているか」の定義を二重化しない)。
    /// </summary>
    /// <remarks>
    /// <paramref name="heightPx"/> だけは共有しない。<c>OnPaint</c> は同じ値を
    /// <c>FrameBuilder.Build</c> にも渡す必要があり、ローカルへ 1 度だけ受けた
    /// <c>paintHeight</c> をそのまま流す契約になっているため(<c>EditorControl.Paint.cs</c> の
    /// 同旨のコメント参照)。<c>UpdateHorizontalScrollbar</c> は<b>本ヘルパを使わない</b>=
    /// 折り返し OFF 専用で topSegment が 0 固定の別経路であり、起点の意味が違う。
    /// </remarks>
    private IReadOnlyList<VisualRow> BuildVisibleRows(TextSnapshot snap, int heightPx) =>
        ViewportLayout.Build(snap, _topLine, _topSegment, heightPx, _wrapColumns, _metrics);

    /// <summary>
    /// UIA <c>ITextProvider.GetVisibleRanges</c> の実処理(UI スレッド専用)。
    /// 現在ビューポートに見えている本文の範囲 [Start, End) を返す。
    /// </summary>
    /// <remarks>
    /// 描画 (<c>EditorControl.Paint.cs</c>) と**同じ** <see cref="BuildVisibleRows"/> と
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
        var rows = BuildVisibleRows(snap, PaintHeightPx);
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
    /// 既存本文の <c>\r\n</c> / <c>\r</c> / <c>\n</c> を検出→指定 EOL に置換した全文で
    /// <b>現在の <see cref="TextBuffer"/> を in-place に差し替え</b>、<see cref="EolMode"/> も
    /// 同時に更新する。SetSource 前は no-op。
    /// </summary>
    /// <returns>
    /// 変換を Undo 履歴へ記録したら true。fast-path(すでに目的 EOL で統一済み)・SetSource 前・
    /// 記録の結果が無変化だった場合は false。<b>呼び出し側は経路から推論せず本戻り値で判定する</b>
    /// (A-11: false のときに Undo を打つと直前のユーザー編集を巻き戻してしまう)。
    /// 取り消し方は <see cref="UndoEolConversion"/> を使う(<see cref="Undo"/> は流用できない)。
    /// </returns>
    /// <remarks>
    /// <see cref="EolMode"/> は「以後の Enter 押下で挿入する改行」の設定であり、既存本文には
    /// 効かない。App 層(FileController の保存経路)は保存前に本 API で本文の改行を統一する。
    /// <para>
    /// A-11(2026-08-28): 旧実装は <see cref="ReplaceSource(TextBuffer)"/> でバッファ参照ごと
    /// 差し替えており、新バッファの Undo 履歴が空=<b>変換前の Undo/Redo 履歴が全消去</b>されていた。
    /// 現在は <see cref="TextBuffer.ReplaceAllRecordingUndo"/> で<b>同一バッファへ 1 Undo 単位として</b>
    /// 記録する(=保存後の Ctrl+Z で変換が取り消せ、さらに遡って変換前の編集も辿れる)。
    /// 保存点(<c>_savedRoot</c>)は触らないので、変換直後は <see cref="Modified"/> が true になり、
    /// Undo で保存点のルートへ戻れば false へ復す。Redo スタックは通常の編集と同じく破棄される。
    /// </para>
    /// <para>
    /// no-op fast-path(=すでに目的 EOL で統一されている場合)は <see cref="EolMode"/> だけ更新して
    /// 抜ける=本文・キャレット・選択・スクロール・Undo 履歴のいずれにも触れず、UIA 通知も
    /// <see cref="UpdateUI"/> も発火しない。
    /// </para>
    /// <para>
    /// non fast-path では「行 index + 改行文字以外の相対 offset」の対で caret/anchor/topLine/
    /// topSegment/scrollX を保存→復元する(P6 レビュー I-2: Save 毎に caret が先頭へ飛ぶ退行を回避)。
    /// <see cref="ReplaceSource(TextBuffer)"/> が担っていた副作用のうち、EOL 変換で意味を失うもの
    /// (セル強調・IME 未確定・DesiredXpx)の破棄と、通知契約(垂直スクロールバー同期・Invalidate・
    /// UIA スナップショット更新/TextChanged/SelectionChanged・<see cref="UpdateUI"/>)は明示的に打つ。
    /// <b>水平スクロールバーだけは再計算しない</b>(理由は該当行のコメント参照=復元済みの
    /// <c>_topLine</c> で評価すると <c>_scrollX</c> が失われる。EOL 変換で水平 extent は不変)。
    /// <c>AfterEdit</c> は<b>使わない</b>: <c>BringCaretIntoView</c> が復元済みのスクロール位置を
    /// 上書きし、かつ <c>_wasModified</c> 遷移から保存処理の途中で <see cref="SavePointLeft"/> が
    /// 焚かれるため(設計書 2026-08-28 §10.12 (1))。
    /// <see cref="UpdateUI"/> の発火は旧経路(caret を 0 に潰した直後)から caret 復元後へ移った
    /// =ハンドラが読む caret 位置が正しくなる(設計書 §10.13 (2) の表)。
    /// </para>
    /// </remarks>
    public bool ConvertEols(LineEnding eol)
    {
        if (_buffer is null)
            return false;
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
        // すでに全 EOL が target で統一されていれば差し替え(キャレット/選択/スクロール復元・
        // Undo 記録・UIA 通知)を丸ごと回避し、EolMode だけ更新して抜ける。
        // A-11: 何も記録していないので false を返す(呼び出し側が Undo を打たないための唯一の根拠)。
        if (IsEolAlreadyUniform(snap, targetBytes))
        {
            EolMode = eol;
            return false;
        }

        // 変換前の caret/anchor を「改行以外の文字数+改行数」で分解=変換後も同じ論理位置を再構成できる。
        // SnapshotReader で chunked 走査(旧実装は SnapshotText 全文化=1GB 級 peak を招いていた)。
        var (caretM, caretK) = CountNonBreakAndBreaksInSnapshot(snap, _caretCtrl.Caret);
        var (anchorM, anchorK) = CountNonBreakAndBreaksInSnapshot(snap, _caretCtrl.Anchor);
        int savedTopLine = _topLine;
        // 2026-08-22 A-6: EOL 変換は行本文(改行を除く)を変えない=セグメント分割は不変なので、
        // 段落の途中を表示していたスクロール位置もそのまま復元できる(_scrollX と対称)。
        int savedTopSegment = _topSegment;
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

        // A-11: バッファ参照ごと差し替える ReplaceSource(=新バッファの空 UndoHistory へ乗り換え)を
        // やめ、同一 TextBuffer へ 1 Undo 単位として記録する。ReplaceSource が担っていた副作用は
        // 下で個別に打つ(AfterEdit を使わない理由は本メソッドの remarks / 設計書 §10.12 (1))。
        // Build() を先に評価するのは旧 `ReplaceSource(builder.Build())` と同じ順序を保つため
        // (Build は carry の不正 UTF-8 や上限超過で throw しうる=IME 取消より前に投げる)。
        var rebuilt = builder.Build().Current;
        // §4-6: 他の状態変異 API と同じく IME 未確定はまず確定キャンセルする(ReplaceSource 冒頭と同旨)。
        if (IsComposing)
            CancelCompositionAndDefault();
        bool recorded = _buffer.ReplaceAllRecordingUndo(rebuilt);
        _cellHighlight = null; // 変換前オフセット由来のセル強調は無効化(EOL 変換で位置がずれる)
        _caretCtrl.DesiredXpx = -1; // ReplaceSource と同じく縦移動の目標 X を捨てる
        // === スクロールバーの扱い(垂直は呼ぶ / 水平は呼ばない)===
        // 前提(EOL 変換でスクロール extent が不変であることの根拠。ここが崩れたら本判断は無効。
        //       設計書 §10.5 が PieceStats.Breaks への結合を却下したのと同じ「黙って壊れる結合」
        //       なので、結論だけでなく依存先を書き出しておく):
        //  - Piece.cs の Breaks 規約 = LF / 単独 CR(末尾 CR 含む)をそれぞれ 1 と数える
        //    → 各改行が target 1 個へ 1:1 に写るので TextSnapshot.LineCount は不変。
        //      垂直の Maximum/LargeChange も水平の lnWidth = MeasureLineNumberWidth(snap.LineCount)
        //      も動かない。単独 CR を break と数えなくなる等の変更で同時に崩れる。
        //  - ViewportLayout.VisualRow.SegmentLength は「改行を含まない」
        //    → 幅測定の対象文字列が変換前後で完全に同一。含む定義に変われば LF→CRLF で幅が伸びる。
        //
        // 垂直: 値は動かないが、_vscroll.Value を _topLine へ同期する ReplaceSource の契約は維持する。
        UpdateVerticalScrollbar();
        // 水平: **あえて再計算しない**(A-11 レビュー I-1 のプローブで実測)。
        // UpdateHorizontalScrollbar は「可視行のうち最長 pixel 幅」で extent を決めるので、
        // 評価時点の _topLine に依存する。ReplaceSource は _topLine=0 に潰した後に呼んでいたが、
        // in-place 化では _topLine を潰さないため、復元済みの起点で評価することになる。
        // 長い行が可視域に無いと HideAndResetHScroll が走って _scrollX が 0 に落ち、
        // 直後の `ScrollX = savedScrollX` は「HScroll 非表示」で早期 return する
        // =保存のたびに水平スクロール位置が失われる。
        // 上の前提より水平 extent は不変なので、そもそも再計算する理由が無い。
        // 以後の編集/リサイズ/スクロールが従来どおり更新する。
        // 契約は EditorControlConvertEolsTests の ConvertEols_NonFastPath_KeepsHorizontalScroll と
        // ConvertEols_NonFastPath_KeepsHorizontalScroll_WhenLongLineOffFirstScreen で固定。
        // 後者は main では赤い(main も _topLine=0 評価ゆえ同じ経路で _scrollX を失う)
        // =本判断は挙動不変ではなく main 既存バグの解消でもある(設計書 §10.13 (7))。

        int total = _buffer.Current.CharLength;
        // アンカー/キャレットは元の (m, k) 分解から再構成して復元する(ConvertEols 前後で
        // 同じ論理位置を保つ)。in-place 化後も char 位置は変換でずれるため再設定が要る。
        _caretCtrl.SetSelection(
            Math.Min(anchorM + anchorK * targetCharLen, total),
            Math.Min(caretM + caretK * targetCharLen, total),
            _buffer.Current
        );
        // A-11 レビュー I-4: RPC スレッド用スナップショットの差し替えは caret 復元の「直後」に置く。
        // AfterEdit と同じ「caret 先 → snapshot 後」の順序を保ちつつ、両者の間の窓から
        // PositionCaret(ComputeCaretPoint のレイアウト計算)と Invalidate を外して実質ゼロにする。
        // 窓の中で RPC スレッドが観測しうる最悪値は「旧文書末尾に縮退した選択範囲」であり、
        // 例外にも範囲外読みにもならない(根拠は設計書 §10.13 (5)-2=TextRangeProviderV2 の
        // ctor が Math.Clamp(start, 0, owner.Host.TextLength) を掛けている)。
        _uia.OnSnapshotChanged(_buffer.Current);
        // TopLine セッターは「その行の先頭視覚行から」の意味を持ち _topSegment を 0 に落とすため、
        // 視覚行位置を保つ SetTopPosition で復元する(クランプ+VScrollBar 同期は同じ)。
        SetTopPosition(savedTopLine, savedTopSegment);
        ScrollX = savedScrollX; // 同上
        // P7 別エージェント最終レビュー Important-2: TopLine/ScrollX の値が不変(小文書で先頭表示中)
        // だと setter が no-op で PositionCaret 再発火されず、Win32 system caret(SetCaretPos)が
        // 復元前の pos 0 に残る。UIA v2 単一経路に統一した P7 以降は SR の system caret 追跡依存度が
        // 上がるため、Save 直後の system caret 位置ずれを避けるべく明示的に再配置する。
        // A-11 で in-place 化した後は SetTopPosition / ScrollX が常に no-op(値を潰していないため)
        // になるので、この再配置は「保険」ではなく system caret 更新の唯一の経路になった。
        if (_hasFocus)
            PositionCaret();
        Invalidate();
        // A-11: 以下は ReplaceSource が担っていた通知契約の再現
        // (スナップショット差し替えは上の caret 復元直後で済ませてある)。
        // 設計書 §10.12 (1): _wasModified は ReplaceSource:301 と同じく「代入で揃える」。
        // ReplaceAllRecordingUndo は _savedRoot を触らない=Modified が false→true へ遷移するため、
        // AfterEdit の遷移検出に載せると保存処理の途中で SavePointLeft が焚かれる(新規の挙動)。
        _wasModified = _buffer.Modified;
        _uia.RaiseTextChanged();
        // 意図的な挙動変更(監査 A-11 が副作用として指摘): 旧経路は ReplaceSource が caret=0 に
        // 潰した中間状態で SelectionChanged を先に飛ばしていた。in-place 化でその中間状態が消え、
        // caret 復元後に 1 回だけ発火する。SR の実発声への影響は L5 でのみ判定できる。
        if (RaiseUiaSelectionEvents)
            _uia.RaiseSelectionChanged();
        // P6 Task 4: 本文全差し替えは App 層のステータスバー更新契機なので UpdateUI 発火。
        UpdateUI?.Invoke(this, EventArgs.Empty);
        EolMode = eol;
        // 経路(非 fast-path)ではなく Core の記録結果をそのまま返す。IsEolAlreadyUniform を
        // すり抜けた無変化(root 同一)でも false になり、呼び出し側の余分な Undo を防ぐ。
        return recorded;
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
            // 同じ論理行への代入でも「その行の先頭視覚行から」の意味を回復させるため、
            // _topSegment != 0 のときは早期 return しない(2026-08-22 A-6)。
            if (clamped == _topLine && _topSegment == 0)
                return;
            _topLine = clamped;
            _topSegment = 0;
            if (_vscroll.Value != clamped)
                _vscroll.Value = clamped;
            PositionCaret();
            Invalidate();
        }
    }

    /// <summary>可視域最上段の視覚セグメント index(設計書 I-2)。折り返し OFF では常に 0。</summary>
    internal int TopSegment => _topSegment;

    /// <summary>
    /// 可視域の起点を<b>視覚行</b>単位で設定する(設計書 I-2)。<see cref="TopLine"/> セッターと違い
    /// <see cref="TopSegment"/> を保つ=巨大段落の途中を先頭に置ける。
    /// 論理行がクランプされた場合はセグメント index の意味が失われるので 0 に落とす。
    /// </summary>
    /// <remarks>
    /// VScrollBar は論理行基準のまま(Value = TopLine)である。段落の途中をスクロールしている間
    /// サムは動かず、論理行 1 本の文書ではバーが無効のままになる=意識的な近似
    /// (全文の視覚行数を数えると O(文書) になり PR #35 の退行になるため。設計書 §4.4 / 申し送り S-3)。
    /// </remarks>
    internal void SetTopPosition(int line, int segment)
    {
        int clampedLine = ClampTopLine(line);
        // Math.Max(0, segment) は load-bearing: 負のセグメントが _topSegment に入ると
        // WalkBackVisualRows の `n -= seg` で n が増え、上方向の歩きが文書頭まで暴走する
        // (Task 2 fixup で ViewportLayout.Build の topSegment 負値に張ったガードと対称)。
        int clampedSeg = clampedLine == line ? Math.Max(0, segment) : 0;
        if (clampedLine == _topLine && clampedSeg == _topSegment)
            return;
        _topLine = clampedLine;
        _topSegment = clampedSeg;
        if (_vscroll.Value != clampedLine)
            _vscroll.Value = clampedLine;
        PositionCaret();
        Invalidate();
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
            // 2026-08-22 A-6: 折り返し幅が変わればセグメント分割そのものが変わる=index の意味が失われる。
            _topSegment = 0;
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
    /// A-13(設計 2026-08-29 §4.3): クリップボード操作が
    /// <see cref="ExternalException"/> で失敗した(他プロセスがクリップボードを保持中など)。
    /// </summary>
    /// <remarks>
    /// App 層が SR へ通知するための<b>唯一の通知源</b>。Editor 層は <c>IAnnouncer</c> を
    /// 参照できない(層の向きが逆になる)ためイベントで上へ渡す。
    /// <see cref="Copy"/> / <see cref="Paste"/> が <c>false</c> を返す理由は「失敗」だけではない
    /// (選択なし・クリップボードが空 等の no-op でも false)ので、
    /// <b>失敗の判定は必ず本イベントで行う</b>こと。
    /// <b>購読側は例外を投げないこと</b>: 本イベントは <see cref="Copy"/> / <see cref="Paste"/> の
    /// catch 節の中から発火するため、ハンドラの例外はそのまま呼び出し側へ抜け、
    /// A-13 が塞いだ「未処理例外」の経路へ戻る(最終的には App 層の <c>CrashHandler</c> が
    /// 受けて終了する=通知したいだけの場面でアプリが落ちる)。
    /// </remarks>
    public event EventHandler<ClipboardFailureKind>? ClipboardFailed;

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

    /// <summary>
    /// [start, start+length) だけを厳密に置換する。両端が CRLF / サロゲートペアの内側を指していても、
    /// <see cref="ReplaceCharRange"/> のように外側の文字を巻き込んで捨てず、はみ出し分を復元して書き戻す。
    /// ただしゼロ幅(純挿入)は広げず境界へスナップする。サロゲートを割るヒットでは半身が
    /// U+FFFD になる(いずれも remarks 参照)。
    /// </summary>
    /// <returns>
    /// 置換文字列の直後の位置(置換後の文書における char offset)。
    /// <b>この API は範囲を外側へ広げることがあり、広げ方は呼び出し側から予測できないので、
    /// 次の位置は必ずこの戻り値を使うこと</b>=<c>start + replacement.Length</c> で計算しては
    /// ならない(ゼロ幅では始端自体が境界へ後退するため合わない。例: <c>"abc\r\ndef"</c> の
    /// <c>(4, 0, "X")</c> は挿入点が 3 へ後退して戻り値 4 になるが、<c>start + 1</c> は 5)。
    /// キャレット(<see cref="CaretCharOffset"/>)の読み戻しでも代用できない。キャレットは
    /// 「広げた範囲の末尾」に立つので、非ゼロ幅では復元した suffix の分だけ先へ行く
    /// (<c>(3, 1, "X")</c> は戻り値 4 に対しキャレット 5)。
    /// <para>
    /// <see cref="ReadOnly"/> / SetSource 前の no-op では <c>Math.Clamp(start, 0, TextLength)</c>
    /// (=何も動いていないので始端そのもの)を返す。常に文書内の有効な位置を返す規約にして、
    /// 戻り値をオフセットとして無条件に使えるようにするため(番兵値 -1 を混ぜると、それを
    /// オフセットに使う新種のバグを作る)。<b>その代償として戻り値だけでは置換の有無を
    /// 判別できない</b>=読み取り専用文書で「戻り値を次の起点にして進む」ループを書くと
    /// 同じ位置を回り続ける。置換されたかどうかが要るなら <see cref="ReadOnly"/> を先に見ること。
    /// </para>
    /// </returns>
    /// <remarks>
    /// 検索の単発置換(A-14 / 2026-08-29)がこれを使う。正規表現 <c>\n</c> は CRLF 文書で LF
    /// だけにヒットするが、<see cref="ReplaceCharRange"/> は両端をスナップするので CR ごと消える。
    /// 一括置換(<c>SnapshotSearcher.ReplaceInRange</c> + 範囲丸ごと差し替え)は両端が
    /// <b>論理文字境界</b>(文書端、または「選択範囲のみ」で捕捉したスコープの選択端)に乗るため
    /// 同じ問題を踏まない。本 API は単発置換の結果を一括置換に揃える。
    /// <para>
    /// 実装は外側へ広げた範囲を <see cref="ReplaceCharRange"/> へ<b>委譲する</b>。委譲先の
    /// 再スナップは <c>s</c> / <c>e</c> が既に論理文字境界にあるため恒等であり、編集の副作用
    /// (<c>AfterEdit</c> / キャレット規約 / Undo 単位 / UIA イベント)は 1 箇所に保たれる。
    /// </para>
    /// <para>
    /// <b>IME 未確定の取消はスナップショットを読む前に行うこと。</b>
    /// <c>CancelCompositionAndDefault</c> 自体はバッファを書かない(未確定文字列は overlay
    /// <c>ImeCompositionState</c> にあり本文には入っていないため、<see cref="ImeController.Cancel"/> は
    /// overlay のクリアと再描画だけを行う)。ただし <c>ImmNotifyIME(CPS_CANCEL)</c> が
    /// <c>WM_IME_COMPOSITION</c>(GCS_RESULTSTR)を同期配送する IME では、再入で本文が動きうる。
    /// そのためスナップショットは取消の<b>後</b>に読む。
    /// テスト用の IME コンテキスト(<c>Fakes/FakeImeContext.cs</c>)は再入しないため、
    /// <b>この順序は網では固定できない</b>(<c>var snap = _buffer.Current;</c> を取消の上へ動かす
    /// 変異は生存する)。
    /// </para>
    /// <para>
    /// <b>事後条件。</b> 次の位置が要るなら<b>戻り値を使うこと</b>(returns 参照)。
    /// 呼び出し側で <c>start</c> から導出してはならない=「接頭辞の復元は長さ保存だから
    /// <c>start</c> は動かない」は非ゼロ幅でしか成り立たず、ゼロ幅では始端自体が境界へ
    /// 後退する。正しい値を組めるのはこのメソッドの内部だけ(<c>s</c> と復元した接頭辞の
    /// 長さの両方を持っているため)なので、推測させずに返す。
    /// </para>
    /// <para>
    /// <b>キャレットは戻り値とは別の位置に立つ</b>(こちらは独立した性質)。委譲先の規約
    /// (<c>s + text.Length</c>)に従って<b>広げた範囲の末尾</b>=
    /// <c>戻り値 + 復元した suffix の長さ</c>に立ち、選択は解除される(例: <c>"abc\r\ndef"</c> の
    /// <c>(3, 1, "X")</c> は本文 <c>"abcX\ndef"</c> ・戻り値 4 ・キャレット 5)。
    /// キャレットを置換文字列の直後へ戻す補正は<b>あえて入れていない</b>=補正すると UIA
    /// イベントが増え、編集の副作用が 1 箇所に留まらなくなるため。
    /// </para>
    /// <para>
    /// <b>ゼロ幅(純挿入)は外側へ広げない。</b> 巻き込み復元は「論理文字の内側にある文字を
    /// <b>置換する</b>」ために要るものであり、挿入には分割すべき文字が無い。広げると CRLF や
    /// サロゲートペアを割って書き戻すことになり、孤立サロゲートが U+FFFD へ潰れて
    /// <see cref="ReplaceCharRange"/> なら無傷だった文字を壊す(例: <c>"a😀b"</c> の
    /// <c>(2, 0, "X")</c>)。<b>その結果、ゼロ幅マッチに限り一括置換
    /// (<c>SnapshotSearcher.ReplaceInRange</c> 経由)と結果が食い違う</b>。一括側は範囲を広げない
    /// =範囲全体を materialize して <c>Regex.Replace</c> がペアの内側へ挿入し、その結果を
    /// 書き戻すときに孤立サロゲートが潰れる(機序は違うが結末は U+FFFD 化で同じ)。
    /// 単発 / 一括の一致より無警告のデータ破壊を消すほうを採った意図的なトレードオフである。
    /// </para>
    /// <para>
    /// <b>非ゼロ幅でサロゲートペアを割るヒットは、復元した半身が単独で残る限り救出できない</b>
    /// (CRLF を割る場合と非対称)。.NET の正規表現 <c>.</c> は UTF-16 code unit 単位で照合するため
    /// 孤立サロゲートに単独ヒットしうるが、本文はピース木に UTF-8 で入るため、復元しようとした
    /// 孤立サロゲートは <c>AppendBuffer.Append</c> の既定フォールバックで U+FFFD へ潰れる。
    /// これは保存層の制約であり本 API 固有ではない=この場合は一括置換と同じ結果になる。
    /// <paramref name="replacement"/> が対の相手(復元される半身と繋がって正しいペアになる
    /// サロゲート)で始まる / 終わる場合だけは半身が生き残るため、この限りではない。
    /// </para>
    /// </remarks>
    public int ReplaceCharRangeExact(int start, int length, string replacement)
    {
        if (IsComposing)
            CancelCompositionAndDefault();
        if (_buffer is null || ReadOnly)
            return Math.Clamp(start, 0, TextLength); // no-op=位置は動かない(returns 参照)
        ArgumentNullException.ThrowIfNull(replacement);
        var snap = _buffer.Current;
        var (s0, e0, s, e) = ExactRangeParts(snap, start, length);
        if (s0 == e0)
        {
            // 挿入点は境界へ後退しうるので、戻り値はスナップ後の位置から作る
            // (s0 から作ると論理文字 1 つ分ずれる=呼び出し側が start から導出するのと同じ誤り)。
            ReplaceCharRange(s, 0, replacement);
            return s + replacement.Length;
        }
        int prefixLen = s0 - s; // 復元する接頭辞。長さ保存で書き戻すので戻り値にも効く
        // 恒等ケース(s == s0 && e == e0)の分岐は置いていない。GetText(x, 0) は空を返し
        // (TextSnapshot.GetText の length == 0 早期 return。ただし範囲検査はその手前なので
        // 空が返るのは x ∈ [0, CharLength] のときだけ=s / e0 は常にこの範囲)、string 連結は空オペランドを
        // 短絡して残り 1 つの参照をそのまま返すため、分岐しても結果は同じ。
        string text = snap.GetText(s, prefixLen) + replacement + snap.GetText(e0, e - e0);
        ReplaceCharRange(s, e - s, text);
        return s + prefixLen + replacement.Length;
    }

    /// <summary>
    /// <see cref="ReplaceCharRangeExact"/> の範囲計算。要求範囲 <c>[S0,E0)</c>(クランプ済み)と、
    /// 巻き込み復元のために外側へ広げた範囲 <c>[S,E)</c> を返す。
    /// </summary>
    /// <remarks>
    /// <b>共有するのは「答え」ではなく範囲計算の素材</b>(<c>S0/E0/S/E</c> の導出)である。
    /// <see cref="ReplaceCharRangeExact"/> と <see cref="GetExactChangeRange"/> の<b>返り値は
    /// 意図的に異なる</b>=前者は広げた範囲 <c>[S,E)</c> へ書き、後者は<b>内容が変わりうる範囲</b>を
    /// 端ごとに判断して返すため、CRLF を割った側は広げた分を<b>捨てる</b>
    /// (例: <c>"abc\r\ndef"</c> の <c>(4, 1)</c> は <c>S=3, E=5</c> に対し戻り値 <c>(4, 5)</c>)。
    /// 捨ててよいのは巻き込み復元が<b>長さ保存</b>で、割った <c>\r</c> / <c>\n</c> がそのまま
    /// 書き戻される=内容が変わらないから。捨てられないのは復元する半身が孤立サロゲートになる側で、
    /// そこは UTF-8 往復で U+FFFD へ潰れるため広げた分を残す。弁別の規則は
    /// <see cref="GetExactChangeRange"/> の remarks が持つ。
    /// <para>
    /// <b>この範囲計算をここ以外に書かないこと。</b> 分かれてはならないのは<b>素材</b>の計算であって
    /// 返り値ではない。素材が 2 実装に分かれた瞬間に「問うた範囲と実際に書く範囲が違う」という
    /// 最悪の形で腐る。
    /// </para>
    /// <para>
    /// <b><see cref="GetExactChangeRange"/> が CRLF 分割で <c>[S0,E0)</c> を返す分岐を、
    /// 「doc と実装の食い違い」と読んで削らないこと。</b> それが守っているのは
    /// 「スコープ端が CRLF の内側にある一括置換は成功しなければならない」=PR #56 §9.9 が
    /// main 既存バグとして根治した挙動であり、
    /// <c>SearchControllerTests.ReplaceAll_InSelection_ScopeEndInsideCrlf_DoesNotDuplicateCr</c> /
    /// <c>..._ScopeStartInsideCrlf_DoesNotDeleteOutsideCr</c> が固定している。
    /// </para>
    /// <para>
    /// <b>ただし上の 2 件が今すぐ番人になるわけではない</b>(実測 / 2026-09-01 B2 Task 3)。
    /// 現時点で <see cref="GetExactChangeRange"/> に production の呼び出し元は無いため、
    /// 端ごとの弁別を落として常に広げた範囲を返す変異を当てても、赤くなるのは
    /// <c>EditorControlReplaceExactTests.GetExactChangeRange_CrlfSplit_DoesNotWiden</c> の
    /// 1 本だけで、App 側は 714 件 green のままだった。<b>この分岐の唯一の番人は今のところ
    /// その Editor テストである。</b> <c>SearchController</c> の包含検査がこれを呼ぶのは
    /// B2 Task 4 からで、繋がった後は上の 2 件も拒否側へ倒れて赤くなる(想定・未実測)。
    /// </para>
    /// <para>
    /// ゼロ幅(<c>S0 == E0</c>)は外側へ広げず、挿入点だけを境界へ後退させて
    /// <c>S == E == 後退後の位置</c> を返す(理由は
    /// <see cref="ReplaceCharRangeExact"/> の remarks「ゼロ幅は広げない」)。
    /// </para>
    /// </remarks>
    private static (int S0, int E0, int S, int E) ExactRangeParts(
        TextSnapshot snap,
        int start,
        int length
    )
    {
        int s0 = Math.Clamp(start, 0, snap.CharLength);
        // start + length は int 加算だとオーバーフローで負値になり得るため long 経由
        // (ReplaceCharRange / EnsureVisibleCharRange と同じ流儀)。
        long endLong = (long)start + Math.Max(0, length);
        int e0 = (int)Math.Clamp(endLong, s0, (long)snap.CharLength);
        if (s0 == e0)
        {
            int at = TextBoundary.SnapToLogicalCharStart(snap, s0);
            return (s0, e0, at, at);
        }
        return (
            s0,
            e0,
            TextBoundary.SnapToLogicalCharStart(snap, s0), // 外側へ(index が減る向き)
            TextBoundary.SnapToLogicalCharEnd(snap, e0) // 外側へ(index が増える向き)
        );
    }

    /// <summary>
    /// <see cref="ReplaceCharRangeExact"/> が同じ引数・同じ世代で呼ばれたときに、
    /// <b>本文の内容が変わりうる文字範囲</b>を、何も書かずに返す。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 返すのは <see cref="ReplaceCharRange"/> へ渡す「広げた範囲」<b>ではない</b>。巻き込み復元は
    /// 長さ保存で、広げた分の接頭辞 / 接尾辞はそのまま書き戻されるため、CRLF を割ったときの
    /// <c>\r</c> / <c>\n</c> は無傷で戻る=内容は変わらない。例外は復元する半身が
    /// <b>孤立サロゲートになる場合</b>で、このとき UTF-8 往復で U+FFFD へ潰れる
    /// (<see cref="ReplaceCharRangeExact"/> の remarks 参照)。この 1 形だけ広げた範囲を返す。
    /// </para>
    /// <para>
    /// ゼロ幅(純挿入)は挿入点が論理文字の境界まで<b>後退</b>しうるので、後退後の位置の空範囲を返す。
    /// 呼び出し側が <c>start</c> から導出してはならないのは
    /// <see cref="ReplaceCharRangeExact"/> の戻り値と同じ理由。
    /// </para>
    /// <para>
    /// <b>用途</b>: 「選択範囲のみ」の置換が、ユーザーの選んでいない位置を書き換えないことを
    /// <b>書く前に</b>確かめる(<c>SearchController</c>)。後退が起きる条件を呼び出し側で
    /// 数え上げるのは本クラスの規則の複製であり、規則が変われば黙って腐る。
    /// </para>
    /// <para>
    /// 書けない状態(<c>_buffer is null</c> / <see cref="ReadOnly"/>)では何も変わらないので
    /// クランプした位置の空範囲を返す。
    /// </para>
    /// </remarks>
    /// <returns>内容が変わりうる文字範囲 <c>[Start, End)</c>。この範囲の外側は変わらない。</returns>
    public (int Start, int End) GetExactChangeRange(int start, int length)
    {
        if (_buffer is null || ReadOnly)
        {
            int noop = Math.Clamp(start, 0, TextLength);
            return (noop, noop);
        }
        var snap = _buffer.Current;
        var (s0, e0, s, e) = ExactRangeParts(snap, start, length);
        if (s0 == e0)
            return (s, s); // ゼロ幅=後退後の挿入点
        // s < s0 になる後退要因は「s0 が low サロゲート」か「s0 が LF で直前が CR」の 2 つだけなので、
        // s0 の文字が low サロゲートかで弁別できる(終端側も同じ)。
        // s < s0 は s0 < CharLength を含意する(SnapToLogicalCharStart は EOF を動かさない)ので
        // GetChar(s0) は常に安全。
        int changeStart = s < s0 && char.IsLowSurrogate(snap.GetChar(s0)) ? s : s0;
        int changeEnd = e > e0 && char.IsLowSurrogate(snap.GetChar(e0)) ? e : e0;
        return (changeStart, changeEnd);
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
        {
            _topLine = maxLine;
            // 2026-08-22 A-6: 行が消えた後のセグメント index は無意味(設計書 §4.1)。
            _topSegment = 0;
        }
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
        // ここは BuildVisibleRows を<b>使わない</b>(2026-08-22 A-6)。冒頭のガードで折り返し OFF
        // 専用と確定しており(_wrapColumns > 0 なら既に return 済み・wrapColumns: 0 を渡すのも
        // 同じ理由)、topSegment は 0 固定である。設計書 I-3 のとおり OFF では TopSegment は常に
        // 0 なので _topSegment を渡しても値は同じだが、この経路の起点は「描画/可視域報告の起点」
        // ではなく「HScroll 幅を推定するための走査開始点」であり意味が違う。共有ヘルパに載せると
        // その差が消えるため、定数のまま別呼び出しに保つ。
        var rows = ViewportLayout.Build(
            snap,
            _topLine,
            topSegment: 0,
            probeHeight,
            wrapColumns: 0,
            _metrics
        );
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
        // A-20(設計 2026-08-29 §6.2): 保留中の高サロゲートの破棄は「契機の列挙」ではなく
        // <b>事後条件</b>側にも置く。本文が変わった=保留は対にならないので捨てる。
        // 列挙(OnKeyDown / OnLostFocus)だけだと原理的に漏れるため、編集経路の唯一の後処理である
        // ここを最後の砦にする。ペア挿入自身もここを通るが、その時点で保留は既にクリア済み=no-op。
        // 回帰テスト: SurrogatePairInputTests.HighThenNonKeyEdit_DropsPending
        // (メニュー経由の貼り付け=OnKeyDown を伴わない編集は、ここだけが破棄を担当する)。
        DropPendingHighSurrogate();
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
    /// A-11(2026-08-28): 直前の <see cref="ConvertEols"/> が積んだ EOL 変換エントリを 1 つだけ
    /// 取り消し、キャレット / 選択を<b>変換前の位置</b>へ戻す(保存失敗時のロールバック専用)。
    /// 取り消したら true。
    /// </summary>
    /// <param name="conversionRecorded">
    /// <see cref="ConvertEols"/> の戻り値をそのまま渡す(fast-path・SetSource 前・無変化なら false)。
    /// </param>
    /// <param name="anchorBefore"><see cref="ConvertEols"/> を呼ぶ<b>前</b>の <see cref="SelectionAnchor"/>。</param>
    /// <param name="caretBefore"><see cref="ConvertEols"/> を呼ぶ<b>前</b>の <see cref="CaretCharOffset"/>。</param>
    /// <remarks>
    /// <b><see cref="Undo"/> を流用してはならない理由が 2 つある</b>:
    /// <list type="number">
    /// <item><see cref="Undo"/> は <see cref="ReadOnly"/> で早期 return する。<c>FileController.WriteToPath</c>
    /// は <see cref="ConvertEols"/> の前後でだけ ReadOnly を外すので、catch 節に来る時点では
    /// 復元済み= CSV グリッドモード(<c>CsvController.Editor.ReadOnly = true</c>)ではロールバックが
    /// <b>黙って no-op</b> になる。本メソッドは ReadOnly を見ない(ユーザー編集ではなく、
    /// 自分が直前に加えた変換の取り消しだから)。</item>
    /// <item><see cref="Undo"/> はキャレットを <c>UndoResult.CaretPos</c>(全文差し替えでは
    /// <b>文書末尾</b>)へ動かす。ユーザーが Ctrl+Z を押した場合はそれで妥当だが
    /// (設計書 2026-08-28 §10.11 (5) で受容)、ロールバックはユーザーが Undo を要求していないので、
    /// 保存失敗ダイアログの裏でキャレットが黙って末尾へ飛ぶ。本メソッドは戻り値を無視し、
    /// 呼び出し元が変換前に捕捉した位置へ戻す(§10.12 (2) の決定)。EOL 変換前のオフセットは、
    /// 変換を取り消した本文に対してそのまま有効である。</item>
    /// </list>
    /// <para>
    /// 保存点(<see cref="Modified"/>)も一緒に戻る: <c>TextBuffer._savedRoot</c> は誰も触らない設計
    /// なので、ルートが変換前へ戻れば参照比較で自動的に復す(<c>TextBuffer.ReplaceAllRecordingUndo</c>)。
    /// </para>
    /// <para>
    /// <b>fast-path では絶対に取り消してはならない</b>: <see cref="ConvertEols"/> が何も記録して
    /// いないのに <see cref="TextBuffer.Undo"/> を打つと、ユーザーの直前の編集が 1 つ消える
    /// (設計書 §5.3)。判別は経路からの推論ではなく <paramref name="conversionRecorded"/> で行う。
    /// </para>
    /// <para>
    /// <b>スクロール位置(<c>_topLine</c> / <c>_topSegment</c> / <c>_scrollX</c>)には触れない。</b>
    /// これが §10.12 (2) の「スクロールを保存前の位置へ戻す」の実装である:
    /// <see cref="ConvertEols"/> の in-place 化以降、変換もルート差し戻しもスクロール状態を
    /// 動かさないため、<b>何もしないことが復元</b>になる。逆に <see cref="AfterEdit"/> を呼ぶと
    /// <c>BringCaretIntoView</c> が復元済みの表示位置を動かし、<c>UpdateHorizontalScrollbar</c> が
    /// 復元済みの <c>_topLine</c> で評価されて <c>_scrollX</c> を落とす(設計書 §10.13 (7))。
    /// 垂直スクロールバーの再計算も不要である(EOL 変換は <c>LineCount</c> を変えないので
    /// 取り消しても行数は同じ)。
    /// <b>注意(最終レビュー m-1)</b>: <see cref="ConvertEols"/> 側は同じ理由で no-op になった
    /// <c>SetTopPosition</c> / <c>ScrollX</c> の復元を<b>あえて残している</b>(将来スクロールを
    /// 動かす副作用が入ったときの防御)。<b>こちらには同等の防御が無い</b> —— 取り消し経路で
    /// スクロールを動かす副作用を足すときは、ここにも復元を書き足すこと。
    /// </para>
    /// <para>
    /// 通知契約(UIA スナップショット / TextChanged / SelectionChanged / <see cref="UpdateUI"/> /
    /// <c>Invalidate</c> / system caret 再配置)は <see cref="ConvertEols"/> の鏡写しで打つ。
    /// <c>_wasModified</c> も <see cref="ConvertEols"/> と同じく<b>代入で揃える</b>=保存処理の途中で
    /// <see cref="SavePointReached"/> を焚かない(main のロールバックもイベントを焚かなかった。
    /// 設計書 §10.12 (1) と対称)。
    /// </para>
    /// <para>
    /// IME 未確定の確定キャンセルは行わない: <paramref name="conversionRecorded"/> が true なら
    /// <see cref="ConvertEols"/> が非 fast-path を通って既にキャンセル済みで、そこから本メソッドまでの
    /// 間(<c>TextFileService.Save</c>)に新しい composition は始まらない。
    /// </para>
    /// <para>
    /// Redo スタックは <see cref="TextBuffer.DropRedo"/> で捨てる。ユーザーが一度も要求していない
    /// 「全文 EOL 変換」を Ctrl+Y に差し出さないため(設計書 §5.3 の「Redo の扱い」の結論)。
    /// </para>
    /// </remarks>
    public bool UndoEolConversion(bool conversionRecorded, int anchorBefore, int caretBefore)
    {
        // fast-path(何も記録していない)で 1 つ戻すと、ユーザーの直前の編集が消える。
        // 経路ではなく ConvertEols の戻り値で判定する=唯一の根拠。
        if (!conversionRecorded || _buffer is null)
            return false;
        if (_buffer.Undo() is null)
            return false;
        _buffer.DropRedo();
        var snap = _buffer.Current;
        _caretCtrl.DesiredXpx = -1; // キャレットが動く経路の共通作法(縦移動の目標 X を捨てる)
        // UndoResult.CaretPos(=文書末尾)は使わない。変換前のオフセットは取り消し後の本文に
        // そのまま有効なので、呼び出し元が捕捉した anchor/caret を復元する(SetSelection がクランプ)。
        _caretCtrl.SetSelection(anchorBefore, caretBefore, snap);
        // ConvertEols と同じく caret 復元の直後(RPC スレッドから見える窓を最小にする)。
        _uia.OnSnapshotChanged(snap);
        if (_hasFocus)
            PositionCaret();
        Invalidate();
        // ConvertEols と同じ扱い: 遷移検出(AfterEdit)に載せず代入で揃える。ここで
        // SavePointReached を焚くと「保存に失敗しただけ」なのに保存点到達イベントが飛ぶ。
        _wasModified = _buffer.Modified;
        _uia.RaiseTextChanged();
        if (RaiseUiaSelectionEvents)
            _uia.RaiseSelectionChanged();
        UpdateUI?.Invoke(this, EventArgs.Empty);
        return true;
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

    // A-13(設計 2026-08-29 §4): 既定は実クリップボード。テストだけが差し替える
    // (他プロセスがクリップボードを保持している状態=ExternalException の経路を作るため)。
    private IClipboard _clipboard = new WinClipboard();

    /// <summary>
    /// テスト専用: クリップボード seam を差し替える。本番経路では呼ばれない。
    /// <c>IImeContext</c> は <c>ImeController</c> の ctor で <c>Func&lt;IImeContext&gt;</c> を受ける形だが、
    /// <see cref="EditorControl"/> は WinForms の <c>Control</c> で引数なし ctor が要るため
    /// setter にしている(同じ趣旨=本番では差し替えない seam・注入の形は違う)。
    /// </summary>
    internal void SetClipboardForTest(IClipboard clipboard) =>
        _clipboard = clipboard ?? throw new ArgumentNullException(nameof(clipboard));

    /// <summary>
    /// 選択範囲のテキストをクリップボード(<see cref="TextDataFormat.UnicodeText"/> 固定・設計書 §0-10)へ書き込む。
    /// 選択なしのときは no-op(=クリップボード内容は保持)。本文不変=<see cref="ReadOnly"/> でも動く
    /// (Notepad と同挙動)。SetSource 前は no-op。
    /// </summary>
    /// <returns>
    /// 選択内容をクリップボードへ書けたと<b>確認できた</b>とき true。false は
    /// 「SetSource 前 / 選択なし(=no-op)」と「A-13 の失敗」の両方を含むため、
    /// 失敗の判定には使えない(失敗時は <see cref="ClipboardFailed"/> が必ず発火する)。
    /// 逆に false は「クリップボードが変わっていない」の保証でもない
    /// (<see cref="Clipboard.SetText(string, TextDataFormat)"/> は内部で置き換え後の
    /// flush でも失敗しうる)。安全側=<see cref="Cut"/> が本文を消さない側に倒れる。
    /// <see cref="Cut"/> は本戻り値で「書けていないのに本文を消す」事故を止める。
    /// </returns>
    /// <remarks>
    /// P6 の <c>ScintillaHost.Copy</c> と同名(App 層メニュー配線=<c>_docs.Active?.Editor.Copy()</c>
    /// と機械的置換用)。<see cref="Clipboard.SetText(string, TextDataFormat)"/> は STA 必須=
    /// 本コントロールが WinForms UI スレッド専用契約のため常に満たされる。
    /// 「行末改行がない選択のときは 1 行選択と見なして EOL を付ける」等の Scintilla 独自仕様は
    /// v1 では真似ず、素直に選択文字列だけを扱う(設計書 Task 11)。
    /// A-13(設計 2026-08-29 §4.1): 捕捉するのは <see cref="ExternalException"/> だけ
    /// (<see cref="COMException"/> はその派生)。<c>catch (Exception)</c> にはしない=
    /// <see cref="ArgumentNullException"/> 等の呼び出し側バグを握り潰さない。
    /// </remarks>
    public bool Copy()
    {
        if (_buffer is null)
            return false;
        var (s, en) = GetSelectionCharRange();
        if (s == en)
            return false;
        string text = _buffer.Current.GetText(s, en - s);
        try
        {
            _clipboard.SetUnicodeText(text);
        }
        catch (ExternalException)
        {
            // A-13: 他プロセスがクリップボードを保持中。本文には触っていないので状態は無傷。
            ClipboardFailed?.Invoke(this, ClipboardFailureKind.Write);
            return false;
        }
        return true;
    }

    /// <summary>
    /// 選択範囲のテキストをクリップボードへ書き込み、その範囲を削除する。
    /// <see cref="ReadOnly"/> / 選択なし / SetSource 前は no-op(現行 Scintilla と一致)。
    /// キャレットは削除位置(=元の選択開始オフセット)へ移動し、選択は解除される。
    /// </summary>
    /// <remarks>
    /// P6 の <c>ScintillaHost.Cut</c> と同名。<see cref="Copy"/> → <see cref="TextBuffer.Replace"/>
    /// で「クリップボード書き込み → 本文削除」の順に実行する。
    /// <b>不変条件: クリップボードへ書けなければ本文を消さない。</b>
    /// A-13(設計 2026-08-29 §4.2)より前はこれを「<c>Clipboard.SetText</c> の例外が本メソッドを
    /// 貫通して <see cref="AfterEdit"/> へ到達しない」ことで担保していたが、
    /// 例外を <see cref="Copy"/> 内で捕捉するようにしたため、いまは
    /// <b><see cref="Copy"/> の戻り値で早期 return すること</b>が唯一の担保である。
    /// ここを崩すと「クリップボードに入っていないのに本文が消える」= A-13 より重い
    /// データ喪失に化ける(回帰テスト: <c>ClipboardFailureTests.Cut_ClipboardBusy_DoesNotDeleteText</c>)。
    /// 失敗の通知は <see cref="Copy"/> が <see cref="ClipboardFailed"/> で既に上げているため
    /// 本メソッドは何もしない(=二重通知しない)。
    /// なお <see cref="CancelCompositionAndDefault"/> は失敗しても巻き戻さない(設計 §4.4):
    /// IME 取消は Ctrl+X を押した時点でユーザーの意図として確定している。
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
        if (!Copy())
            return;
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
    /// <returns>
    /// クリップボードの文字列を本文へ<b>挿入した</b>とき true。false は
    /// 「SetSource 前 / <see cref="ReadOnly"/> / UnicodeText 無し / 空(=no-op)」と
    /// 「A-13 の失敗」の両方を含むため、失敗の判定には使えない
    /// (失敗時は <see cref="ClipboardFailed"/> が必ず発火する)。
    /// <see cref="Copy"/> と戻り値の意味を揃えている(true=意図した転送が行われた)。
    /// </returns>
    /// <remarks>
    /// P6 の <c>ScintillaHost.Paste</c> と同名。<see cref="Clipboard.ContainsText(TextDataFormat)"/>
    /// で先にチェックしても実装差で空文字列を返すケースが理論上残り得るため、防御的に
    /// <c>string.IsNullOrEmpty</c> でも早期 return する(空文字列 Replace は本文不変だが履歴に
    /// 積む副作用があるため避けたい)。
    /// A-13(設計 2026-08-29 §4): 読み取りは <c>Contains</c> / <c>Get</c> の 2 呼び出しで、
    /// どちらが <see cref="ExternalException"/> を投げても同じ 1 つの catch で受ける
    /// (ユーザーから見た原因は同じ「クリップボードが使えない」)。
    /// 例外が出るのは本文に触る前だけなので、失敗時の本文は無傷である。
    /// </remarks>
    public bool Paste()
    {
        if (IsComposing)
            CancelCompositionAndDefault(); // §4-6(Task 13 レビュー I-1)
        if (_buffer is null || ReadOnly)
            return false;
        string text;
        try
        {
            if (!_clipboard.ContainsUnicodeText())
                return false;
            text = _clipboard.GetUnicodeText();
        }
        catch (ExternalException)
        {
            ClipboardFailed?.Invoke(this, ClipboardFailureKind.Read);
            return false;
        }
        if (string.IsNullOrEmpty(text))
            return false;
        var (s, en) = GetSelectionCharRange();
        _buffer.Replace(s, en - s, text);
        _caretCtrl.SetTo(s + text.Length, _buffer.Current);
        _caretCtrl.DesiredXpx = -1;
        AfterEdit();
        return true;
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
        // A-20(設計 2026-08-29 §6.2): フォーカスが移ったら保留中の高サロゲートは対にならない。
        DropPendingHighSurrogate();
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

    // ── 視覚行ヘルパ(2026-08-22 A-6 / 設計書 不変条件 I-2・I-4)─────────────────────
    // 可視判定・スクロール判断・座標算出・ヒットテストが「起点から視覚行数で数える」ための共有部品。
    // 「どのセグメントに属するか」の規約を二重化しないため、判定は LocateSegmentIndex 1 箇所に置く。
    // 歩き/数えは I-4 に従い必要本数で打ち切る(文書全体・論理行全体を無条件に Wrap しない)。

    /// <summary>折り返し幅(px)。折り返し OFF は 0(=LineLayout.Wrap が単一セグメントを返す)。</summary>
    /// <remarks>
    /// OFF 側の三項は<b>到達する</b>。<see cref="LocateVisualRow"/> と
    /// <see cref="SegmentCountCapped"/> は <c>_wrapColumns &lt;= 0</c> を先に短絡するので
    /// この 2 経路からは踏まないが、<see cref="ComputeCaretPoint"/> と
    /// <see cref="OffsetFromClientPoint"/> は折り返し OFF でも走り、0 を
    /// <c>LineLayout</c> へ渡して「折り返し無し」を表現する(=<c>maxWidthPx &lt;= 0</c> の
    /// 契約に乗る)。したがって <see cref="ViewportLayout.Build"/> の
    /// 「到達可能な生きた防御」と同じく<b>外してはならない</b>。
    /// </remarks>
    private int MaxWrapWidthPx => _wrapColumns > 0 ? _wrapColumns * _metrics.MeasureRun("0") : 0;

    /// <summary>論理行 1 本の本文(改行を含まない)。空行は空文字列。</summary>
    private static string LineTextOf(TextSnapshot snap, int line)
    {
        int ls = snap.GetLineStart(line);
        int le = snap.GetLineEnd(line, includeBreak: false);
        return le == ls ? string.Empty : snap.GetText(ls, le - ls);
    }

    /// <summary>
    /// 論理行内オフセットが属する視覚セグメントの index を返す(設計書 I-2 の単一定義)。
    /// 通常は「<c>seg.OffsetInLine + seg.Length</c> で終わる直前」まで。最終セグメントに限り
    /// 「末尾ちょうど」も許容する(EOL キャレット位置)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// EOL 分岐は「<paramref name="segments"/> の最後の要素 = 論理行の最終セグメント」を仮定して
    /// おり、打ち切られている(<paramref name="reachedLineEnd"/> == false)ときその仮定は成り立た
    /// ない。よって条件に <paramref name="reachedLineEnd"/> を足してあるが、これは防御ではなく
    /// 「依存関係を明示するドキュメント」である。実際:
    /// <list type="bullet">
    /// <item><c>LineLayout.WrapThroughOffset</c> は「covered &gt; offsetInLine」を厳密な不等号で
    /// 保証するため、打ち切り時は最後の要素で必ず <c>offsetInLine &lt; segEnd</c> が先に成立し
    /// EOL 分岐には到達しない。</item>
    /// <item>そもそも EOL 分岐は segIdx を <c>segments.Count - 1</c>(初期値と同じ)にするだけ
    /// なので、発火してもしなくても結果は変わらない=<paramref name="reachedLineEnd"/> を外しても
    /// 観測可能な挙動は変化しない(実証済み)。</item>
    /// </list>
    /// </para>
    /// <para>
    /// 本当に load-bearing なのは <c>LineLayout.WrapCore</c> の「<c>segStart &gt; minCoverOffset</c>」
    /// の「&gt;」1 文字の方。ここを「&gt;=」に緩めると「セグメント境界ちょうど」のキャレット
    /// (offset == 次セグメント先頭)が打ち切りに巻き込まれ、次の視覚行へ降りずに 1 行「上」・
    /// 前セグメント「末尾」(右端)に留まる。行末キャレットは segStart が line.Length に到達しない
    /// ため打ち切られず無傷=症状は行末ではなくセグメント境界に出る。この「&gt;」は
    /// <c>EditorControlWrapCaretTests</c>(Editor 13 件)と <c>LineLayoutPrefixTests</c>(Core 5 件)
    /// が守っている。
    /// </para>
    /// </remarks>
    private static int LocateSegmentIndex(
        IReadOnlyList<WrapSegment> segments,
        bool reachedLineEnd,
        int offsetInLine
    )
    {
        int segIdx = segments.Count - 1;
        for (int i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            int segEnd = seg.OffsetInLine + seg.Length;
            if (
                offsetInLine < segEnd
                || (reachedLineEnd && i == segments.Count - 1 && offsetInLine == segEnd)
            )
            {
                segIdx = i;
                break;
            }
        }
        return segIdx;
    }

    /// <summary>
    /// char offset の視覚行位置 (論理行, セグメント index) を返す(設計書 I-2)。
    /// 折り返し OFF は Wrap を一切呼ばず (論理行, 0) を返す=I-3。
    /// </summary>
    private (int Line, int Seg) LocateVisualRow(TextSnapshot snap, int offset)
    {
        int line = snap.GetLineIndexOfChar(offset);
        if (_wrapColumns <= 0)
            return (line, 0);
        int lineStart = snap.GetLineStart(line);
        int offsetInLine = offset - lineStart;
        var wrapped = LineLayout.WrapThroughOffset(
            LineTextOf(snap, line),
            MaxWrapWidthPx,
            _metrics,
            offsetInLine
        );
        return (line, LocateSegmentIndex(wrapped.Segments, wrapped.ReachedLineEnd, offsetInLine));
    }

    /// <summary>
    /// 論理行 <paramref name="line"/> の視覚行数を最大 <paramref name="cap"/> 本まで数える
    /// (設計書 I-4: 打ち切れる歩きは必ず打ち切る)。
    /// 戻り値が <paramref name="cap"/> に等しいときは打ち切られている可能性があり、
    /// 実際の本数はそれ以上である(下の「打ち切りが起きたときの Count」の項を参照)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 折り返し OFF(<c>_wrapColumns &lt;= 0</c>)では 1 論理行 = 1 視覚行が確定しているので、
    /// <see cref="LineTextOf"/> による行全文の materialize すらせずに即答する(I-3)。これは
    /// <c>LineLayout.WrapCore</c> の OFF 分岐(<c>maxWidthPx &lt;= 0</c> なら必ず
    /// <c>[(0, line.Length)]</c> と <c>ReachedLineEnd: true</c> を返す)と<b>厳密に等価</b>な短絡で
    /// あり、挙動は変わらない。この短絡が無いと、OFF でも視覚行を数えるたびに論理行全文の
    /// string を確保することになる(巨大 1 行で 1 回 1MB 級=PR #35 が潰したコスト階級の再導入)。
    /// </para>
    /// <para>
    /// <paramref name="cap"/> は 1 未満でも 1 として扱う(<see cref="LineLayout.Wrap"/> は必ず
    /// 1 個以上返す契約であり、<see cref="LineLayout.WrapFirstSegments"/> は 0 以下で投げるため)。
    /// この丸めは<b>現状の呼び出し元からは到達しない</b>(3 経路とも 1 以上を渡す)。将来の
    /// 呼び出し元のための契約であり、<see cref="ViewportLayout.Build"/> の同型の
    /// <c>Math.Max(1, ...)</c> に付いている「到達可能な生きた防御(外してはならない)」とは
    /// 性質が違う。
    /// 打ち切りが起きたときの Count は要求値に厳密に等しい(WrapCore の打ち切り判定が
    /// <c>result.Add</c> の直後にしかないため。<see cref="ViewportLayout.Build"/> の同旨のコメント参照)。
    /// </para>
    /// <para>
    /// <b>打ち切りの有無は返さない</b>。<c>WrapFirstSegments</c> の
    /// <c>ReachedLineEnd</c> を素通しする設計だったが、3 呼び出し元とも捨てており
    /// テストも見ていなかった(死んだ API 面=最終レビュー品質パス)。呼び出し元は
    /// いずれも「要求値に達したか」を自前の文脈から導けている:
    /// <see cref="WalkForwardVisualRows"/> は <c>landing &lt; count</c> の分岐で、
    /// <see cref="CountVisualRowsForward"/> は <c>rows &lt; cap</c> の不変条件で、
    /// <see cref="WalkBackVisualRows"/> は <c>cap = int.MaxValue</c>(打ち切りが起きない)で。
    /// 必要になったら <c>WrapResult</c> をそのまま返す形に戻せばよい。
    /// </para>
    /// </remarks>
    private int SegmentCountCapped(TextSnapshot snap, int line, int cap)
    {
        // 折り返し OFF は 1 論理行 = 1 視覚行(WrapCore の OFF 分岐と等価)。行全文を取らない。
        if (_wrapColumns <= 0)
            return 1;
        return LineLayout
            .WrapFirstSegments(LineTextOf(snap, line), MaxWrapWidthPx, _metrics, Math.Max(1, cap))
            .Segments.Count;
    }

    /// <summary>
    /// (fromLine, fromSeg) から (toLine, toSeg) までの視覚行距離を数える。
    /// <paramref name="cap"/> 本を超えたら <paramref name="cap"/> を返して打ち切る(I-4)。
    /// 「可視域 visibleRows 本に収まるか」の判定にだけ使うため、cap 超過の正確な値は要らない。
    /// </summary>
    /// <remarks>
    /// 前方距離しか意味を持たないため、(toLine, toSeg) が起点より<b>手前</b>のときは 0 を返す
    /// (呼び出し側が先に辞書順比較で「起点より上」を弁別している前提。設計書 §4.2 の 2→3 の順)。
    /// <b>複数の論理行を跨ぐ場合に限り</b>、<paramref name="fromSeg"/> が先頭行の実セグメント数
    /// 以上なら最終セグメントへ寄せて数える(<see cref="ViewportLayout.Build"/> の topSegment
    /// クランプと同じ寄せ方)。同一論理行内(<c>toLine == fromLine</c>)は行の実セグメント数を
    /// 見ずに <c>toSeg - fromSeg</c> の引き算で答えるため、この寄せは働かない。
    /// <paramref name="cap"/> は 1 以上であること(可視行数を渡す想定)。0 以下は 1 として扱う。
    /// </remarks>
    private int CountVisualRowsForward(
        TextSnapshot snap,
        int fromLine,
        int fromSeg,
        int toLine,
        int toSeg,
        int cap
    )
    {
        // cap <= 0 は契約違反(可視行数は 1 以上)。負値を Math.Min で素通しすると負の距離を
        // 返し、呼び出し側の可視判定を反転させるため 1 に丸める。
        cap = Math.Max(1, cap);
        if (toLine < fromLine)
            return 0;
        if (toLine == fromLine)
            return Math.Min(cap, Math.Max(0, toSeg - fromSeg));

        int rows = 0;
        for (int line = fromLine; line < toLine; line++)
        {
            // この打ち切りは<b>値としては等価</b>(下の Math.Min が同じ cap に丸める)ため、
            // 落とす変異を値ベースのテストで殺すことは原理的にできない=網は張れない。
            // それでも外してはならない: 各論理行が必ず 1 本以上を寄与するので、この return が
            // 反復数を cap 本に抑えている。外すと O(toLine - fromLine) になり、100 万行文書の
            // Ctrl+End 1 回で 100 万回の SegmentCountCapped を払う(I-4 の実効的な砦)。
            if (rows >= cap)
                return cap;
            int skip = line == fromLine ? fromSeg : 0;
            // 読み飛ばす skip 本も Wrap の要求本数に足す(打ち切り結果は完全結果の prefix)。
            // rows < cap がここで保証されているので needed >= skip + 1 > skip=
            // 打ち切り時に skip >= count は成立しない(下の Math.Min が最終セグメントを
            // 誤認しないことの根拠。ViewportLayout.Build の同旨のコメント参照)。
            long needed = (long)(cap - rows) + skip;
            int count = SegmentCountCapped(
                snap,
                line,
                needed > int.MaxValue ? int.MaxValue : (int)needed
            );
            int eff = Math.Min(skip, count - 1);
            rows += count - eff;
        }
        // rows + toSeg は long 経由(上の needed と同じ理由)。toSeg は通常 LocateVisualRow 由来の
        // 実 index だが、internal な直接呼び出しで int.MaxValue 級を渡されると素の int 加算では
        // 負へ回り込み「距離が負=既に可視」と誤判定する(最終レビュー脆弱性パス Low)。
        long total = (long)rows + toSeg;
        return (int)Math.Min(cap, total);
    }

    /// <summary>
    /// 視覚行を n 本ぶん前へ進めた位置を返す。文書末に達したらそこで打ち切り、
    /// <c>Exhausted</c> に true を入れる(=<b>要求 n 本を歩き切れずに文書末で止まった</b>)。
    /// 歩き切れた場合は false。n が 0 以下なら起点をそのまま返す(Exhausted=false)。
    /// </summary>
    /// <remarks>
    /// <paramref name="seg"/> がその行の実セグメント数以上(編集で段落が縮み <c>_topSegment</c> が
    /// 陳腐化した状態)なら最終セグメントへ寄せて数える=<see cref="ViewportLayout.Build"/> の
    /// topSegment クランプおよび <see cref="CountVisualRowsForward"/> と挙動が一致する。
    /// ここで寄せられるのは、その行の<b>真の</b>総数 <c>count</c> が既に手元にあり追加コストが
    /// ゼロだからである。総数を得るのに追加の Wrap が要る <see cref="WalkBackVisualRows"/> は
    /// 寄せない=非対称は意図的であり、理由は同メソッドの remarks を参照。
    /// <para>
    /// <b>事前条件</b>: <paramref name="seg"/> と <paramref name="n"/> は非負であること。
    /// 和が <c>int.MaxValue</c> を超える破れ自体は内部で long 経由にして防いでいるが、
    /// 意味のある結果を返せるのは呼び出し側が実在の起点を渡した場合だけである。
    /// </para>
    /// </remarks>
    private (int Line, int Seg, bool Exhausted) WalkForwardVisualRows(
        TextSnapshot snap,
        int line,
        int seg,
        int n
    )
    {
        while (n > 0)
        {
            // この行に「seg + n」本目があるかだけ判れば良いので打ち切って数える(I-4)。
            long cap = (long)seg + n + 1;
            int count = SegmentCountCapped(
                snap,
                line,
                cap > int.MaxValue ? int.MaxValue : (int)cap
            );
            // seg + n も long 経由。素の int 加算だと int.MaxValue 級の seg / n で負へ回り込み、
            // 負のセグメント index を Exhausted=false で返して呼び出し側(OffsetFromClientPoint)を
            // 落とす(最終レビュー脆弱性パス Low)。cap の long 化と対で守る。
            long landing = (long)seg + n;
            if (landing < count)
                return (line, (int)landing, false);
            // ここに到達した時点の count は打ち切られていない=その行の真の総数である
            // (打ち切られていれば count == seg + n + 1 > seg + n となり上で早期 return する)。
            // よって count - 1 は真の最終セグメント index であり、陳腐化した seg をそこへ
            // 寄せるのは ViewportLayout.Build の topSegment クランプと同じ意味になる。
            // seg <= count - 1(正常時)では eff == seg なので現行式と完全に同一。
            int eff = Math.Min(seg, count - 1);
            n -= count - eff; // この行の残り本数 + 次行先頭へ移る 1 本
            if (line + 1 >= snap.LineCount)
                return (line, count - 1, true); // 文書末で打ち切り=要求を歩き切れていない
            line++;
            seg = 0;
        }
        return (line, seg, false);
    }

    /// <summary>視覚行を n 本ぶん遡った位置を返す。文書頭で打ち切る。</summary>
    /// <remarks>
    /// <para>
    /// 前の論理行へ入るときだけ<b>正確な</b>視覚行数が要る(最終セグメントから数えるため)ので、
    /// そこは打ち切れない完全 Wrap になる。巨大行を下から遡る場合の 1 回だけで、
    /// PR #35 の幅メモ化により CJK 500K 行で約 30 ms(設計書 §5)。
    /// </para>
    /// <para>
    /// <paramref name="seg"/> が現在行の実セグメント数以上(陳腐化した <c>_topSegment</c>)でも
    /// <b>クランプしない</b>=<see cref="WalkForwardVisualRows"/> との意図的な非対称である。
    /// 陳腐化の検出には現在行の実セグメント数が要り、そのための
    /// <c>SegmentCountCapped(snap, line, seg + 1)</c> は O(seg) の Wrap を毎回払う。本メソッドは
    /// 折り返し ON の常用経路(キャレットの追従スクロール)から呼ばれるため、巨大段落では
    /// 1 打鍵あたりもう 1 回ぶんの Wrap が乗ることになる。<b>有効な seg を渡すのは呼び出し側の
    /// 責務</b>とする(キャレット由来の seg は <see cref="LocateVisualRow"/> が返すので常に有効。
    /// 陳腐化しうるのは <c>_topSegment</c> を渡すホイール経路だけ)。
    /// 帰結は「大量削除の直後にホイール上方向が空振りする」ことで、描画は
    /// <see cref="ViewportLayout.Build"/> のクランプにより破綻しない。しかも <c>seg - n</c> が
    /// いずれ実セグメント数を下回るため<b>自己修復する</b>(キャレット追従は起点をキャレット位置へ
    /// 寄せるので修復を早める)。
    /// <b>空振りの上界は「数ノッチ」ではない</b>: 陳腐化の超過分(<c>seg</c> − 実セグメント数)を
    /// 1 ノッチの視覚行数で割った切り上げ回数ぶん空振りし、超過分そのものに上限は無い
    /// (実測: <c>_topSegment</c>=99 / 実セグメント 5 本 / 1 ノッチ 3 行で、5 ノッチ回しても
    /// <c>GetVisibleCharRange().Start</c> が動かない)。当初「数ノッチ程度」と書いていたのは
    /// 過小申告だった(最終レビュー品質パス)。
    /// </para>
    /// </remarks>
    private (int Line, int Seg) WalkBackVisualRows(TextSnapshot snap, int line, int seg, int n)
    {
        while (n > 0)
        {
            if (seg >= n)
                return (line, seg - n);
            n -= seg; // (line, 0) までで seg 本
            if (line == 0)
                return (0, 0); // 文書頭で打ち切り
            line--;
            // LineLayout.Wrap は WrapCore(minSegments: int.MaxValue, minCoverOffset: -1) と
            // 同一実装なので、cap に int.MaxValue を渡す SegmentCountCapped と厳密に等価
            // (どちらも「打ち切りが起きない」)。こちら経由にすることで折り返し OFF のガード
            // (行全文を取らない)を継承し、捨てるだけのセグメントリスト構築も省ける。
            int count = SegmentCountCapped(snap, line, int.MaxValue);
            seg = count - 1; // 前行の最終視覚行へ移る = さらに 1 本
            n--;
        }
        return (line, seg);
    }

    /// <summary>
    /// 可視域の起点を<b>視覚行</b>単位で相対移動する(ホイール用)。
    /// <paramref name="deltaRows"/> は正 = 下方向(文書末へ)・負 = 上方向。
    /// 折り返し OFF は <see cref="TopLine"/> の相対移動に委譲する = 導入前と同一(設計書 I-3)。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 折り返し ON では <see cref="TopLine"/> セッターを使ってはならない。論理行 1 本の文書では
    /// <c>ClampTopLine</c> の上限が 0 になり、ホイールが<b>完全に効かなかった</b>(監査 A-6)。
    /// </para>
    /// <para>
    /// 上方向の <see cref="WalkBackVisualRows"/> には陳腐化しうる <c>_topSegment</c> を渡す
    /// (キャレット由来ではないため)。同メソッドの remarks のとおりクランプしない設計で、
    /// 帰結は「大量削除の直後に上方向が空振りする」こと(空振り回数の上界は陳腐化の超過分に
    /// 比例し、上限は無い=同 remarks 参照)・描画は
    /// <see cref="ViewportLayout.Build"/> のクランプで破綻せず自己修復する。
    /// </para>
    /// <para>
    /// 戻り値の型が違う(前進は Exhausted を持つ)ため三項演算子では書けない。
    /// 文書末に達した <c>Exhausted</c> は破棄する = そこで打ち切られた位置が答えであり、
    /// ホイールでは「最終視覚行が最上段」で止まるのが期待動作(<see cref="TopLine"/> 経由の
    /// 従来挙動が maxLine で頭打ちになるのと同じ性質)。
    /// </para>
    /// </remarks>
    private void ScrollByVisualRows(int deltaRows)
    {
        if (_buffer is null || deltaRows == 0)
            return;
        if (_wrapColumns <= 0)
        {
            TopLine = _topLine + deltaRows;
            return;
        }
        var snap = _buffer.Current;
        int line;
        int seg;
        if (deltaRows < 0)
            (line, seg) = WalkBackVisualRows(snap, _topLine, _topSegment, -deltaRows);
        else
            (line, seg, _) = WalkForwardVisualRows(snap, _topLine, _topSegment, deltaRows);
        SetTopPosition(line, seg);
    }

    /// <summary>
    /// 与えられた UTF-16 char offset のクライアント座標(px)と可視性を算出する純ロジック。
    /// - Visible=false: TopLine 未到達 / TopSegment より上の視覚行 /
    ///   paintHeight を超える論理行 / y &gt;= paintHeight
    /// - Visible=true: (X, Y) は「行番号マージン含む・_scrollX を引く前」の座標
    /// </summary>
    /// <remarks>
    /// 折り返し ON 時は TopLine ～ 対象行までの各論理行に対して折り返しを計算し直す
    /// (1 論理行ずつ GetText + Wrap)。Task 14 のベンチで顕在化するようなら
    /// Frame の再利用等で最適化する(Task 9 レビュー M-3 の申し送り)。
    /// Task 10 レビュー I-1 対応: 積み上げループ内で paintHeight 超えを検出したら早期退避する
    /// (100 万行のような巨大文書でキャレットが末尾方向にあるとき無駄な Wrap を避けるため)。
    /// 2026-08-02 変更 B-2: どちらの Wrap も「その呼び出しで実際に必要な分」で打ち切る
    /// (キャレット行は <see cref="LineLayout.WrapThroughOffset"/>・手前の行は
    /// <see cref="LineLayout.WrapFirstSegments"/>)。打ち切り結果は完全な Wrap 結果の
    /// prefix なので、返す座標・可視性は変わらない。根拠は本体のコメントを参照。
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
        int maxWidthPx = MaxWrapWidthPx;
        int caretInLine = offset - lineStart;

        // 2026-08-02 変更 B-2: キャレットを含むセグメントが確定した時点で Wrap を打ち切る。
        // 行末キャレットのときは打ち切れない(行末まで走らないと「含むセグメント」が決まらない)
        // が、その場合のコストは変更 A(GdiCharMetrics のコードポイント幅メモ化)が受け持つ。
        var wrapped = LineLayout.WrapThroughOffset(lineText, maxWidthPx, _metrics, caretInLine);
        var segments = wrapped.Segments;

        // 対象がどの視覚セグメントに属するかの規約は seam に集約する(設計書 §4.3=
        // 「どのセグメントに属するか」を二重化しない)。選択規約そのもの・条件に
        // ReachedLineEnd を足してある理由・「本当に load-bearing なのは LineLayout.WrapCore の
        // 『>』1 文字の方」という依存関係は、すべて LocateSegmentIndex の doc に書いてある。
        int segIdx = LocateSegmentIndex(segments, wrapped.ReachedLineEnd, caretInLine);

        // I-2: TopLine の途中セグメントから描いている場合、その上のセグメントは不可視。
        // (論理行での「TopLine 未到達」判定と対になる、視覚行での上方はみ出し判定。)
        if (logicalLine == _topLine && segIdx < _topSegment)
            return (0, 0, false);

        var chosenSeg = segments[segIdx];
        int localOffset = caretInLine - chosenSeg.OffsetInLine;
        var segSpan = lineText.AsSpan(chosenSeg.OffsetInLine, chosenSeg.Length);
        int xInSeg = PixelMapper.OffsetToPx(segSpan, localOffset, _metrics);

        int lineHeight = _metrics.LineHeightPx;
        int paintHeight = PaintHeightPx;

        // TopLine の先頭視覚行を Y=0 として、対象視覚行までの積み上げ視覚行数を算出。
        // paintHeight を超えたら以降の Wrap は無駄なので早期退避(Task 10 I-1)。
        //
        // 2026-08-02 変更 B-2: 各行も「まだ意味のある視覚行数」までで打ち切って数える。
        // 早期退避の条件 visualRowsBeforeThisLine * lineHeight >= paintHeight は、整数の
        // 積み上げ数について「accumulated >= ceil(paintHeight / lineHeight)」と同値。
        // よって積み上げの伸びをその ceil 値(=maxUsefulRows)で頭打ちにするのは厳密に正しい:
        //   - 真の segs.Count が accumulated を maxUsefulRows 以上へ押し上げるなら、
        //     打ち切っても accumulated == maxUsefulRows となり同じ分岐に入る
        //   - 真の segs.Count が accumulated を maxUsefulRows 未満に留めるなら、
        //     そもそも打ち切りが起きず値は正確
        // この上限を 1 でも小さく取ると視覚行数を過小評価し、本来不可視の位置を可視として
        // 返す(=行がずれた座標を返す)ので、ceil を floor や -1 に緩めてはならない。
        int visualRowsBeforeThisLine = 0;
        int maxUsefulRows =
            lineHeight > 0 ? (paintHeight + lineHeight - 1) / lineHeight : int.MaxValue;
        for (int line = _topLine; line < logicalLine; line++)
        {
            // I-2: 先頭論理行は _topSegment 本ぶん画面外にあるので積み上げから差し引く。
            int skip = line == _topLine ? _topSegment : 0;
            // Math.Max(1, ...) は到達可能な生きた防御(外してはならない)。
            // PaintHeightPx は 0 になり得る(フォーム最小化・レイアウト確定前・
            // hscroll より低いペイン)。そのとき maxUsefulRows=0 → rowsNeeded=0 となり、
            // WrapFirstSegments の ThrowIfNegativeOrZero が発火して
            // PositionCaret / OnPaint / UIA 経路へ ArgumentOutOfRangeException が抜ける
            // (打ち切り導入で新設された例外面。変更前の Wrap は投げなかった)。
            // ループ継続中の通常ケースでは accumulated < maxUsefulRows が成り立つため
            // rowsNeeded は 1 以上になる。
            //
            // 読み飛ばす skip 本も Wrap の要求本数に足す(打ち切り結果は完全結果の prefix なので
            // 「可視分 + 読み飛ばし分」を求めれば足りる)。maxUsefulRows は lineHeight <= 0 で
            // int.MaxValue になり得るため skip の加算は long で受ける
            // (CountVisualRowsForward の同旨の long 経由と同じ理由)。
            long needed = (long)maxUsefulRows - visualRowsBeforeThisLine + skip;
            int rowsNeeded = needed > int.MaxValue ? int.MaxValue : (int)needed;
            var segs = LineLayout
                .WrapFirstSegments(
                    LineTextOf(snap, line),
                    maxWidthPx,
                    _metrics,
                    Math.Max(1, rowsNeeded)
                )
                .Segments;
            // ViewportLayout.Build と同じクランプ(topSegment が実数以上なら最終セグメント)。
            int eff = Math.Min(skip, segs.Count - 1);
            visualRowsBeforeThisLine += segs.Count - eff;
            if (visualRowsBeforeThisLine * lineHeight >= paintHeight)
                return (0, 0, false);
        }
        int totalVisualRow =
            visualRowsBeforeThisLine + segIdx - (logicalLine == _topLine ? _topSegment : 0);

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
        // キャレット太さ(弱視のキャレット視認性・kxedit-sighted-users-first-class)
        _caretWidthPx = Math.Clamp(settings.CaretWidth, 1, 5);
        // WrapColumns の実値が変わったときだけ ScrollX をリセットする(フォント色だけ変更等で
        // 横スクロール位置が不用意にホームへ戻る副作用を避ける)。折り返し ON への遷移では
        // ScrollX=0 が必要=UpdateHorizontalScrollbar 内の HideAndResetHScroll でも 0 にされるが、
        // ここでも先に落としておくことで PositionCaret が過渡的な旧 _scrollX を参照するのを防ぐ。
        int oldWrapColumns = _wrapColumns;
        _wrapColumns = Math.Max(0, settings.WrapColumnEnabled ? settings.WrapColumn : 0);
        if (_wrapColumns != oldWrapColumns)
            _scrollX = 0;
        // 2026-08-22 A-6: フォント/metrics/折り返し幅のいずれが変わってもセグメント分割が変わる=
        // セグメント index の意味が失われるので無条件に 0 へ戻す(設計書 §4.1)。
        _topSegment = 0;

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
