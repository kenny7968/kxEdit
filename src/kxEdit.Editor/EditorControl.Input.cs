// EditorControl.Input.cs
// Phase 2 (Task 2c) で切り出したキーボード/マウス入力分割。フィールドは EditorControl.cs 本体で宣言。
// Phase 3 (Task 3c) で OnKey* / OnMouse{Down,Move,Up,DoubleClick} の分岐ロジックを InputRouter へ
// 移譲。本ファイルには以下だけが残る:
//   - OnKeyDown / OnMouseDown / OnMouseMove / OnMouseUp / OnMouseDoubleClick の薄ラッパ
//   - OnMouseWheel(精度改善版=Router に載せず現状維持)
//   - OnKeyPress + InsertConfirmedText(IME 確定と共有する挿入経路・分岐なし)
//   - IsInputKey(WinForms のフォーカス遷移/ダイアログ既定ボタン発火を抑止する OS レベル契約)
//   - OffsetFromClientPoint(Router のマウスハンドラから叩く座標→char 変換の
//     内部ヘルパ・EditorControl 状態を多量に参照するので host 側にとどめる。
//     視覚行の前進そのものは EditorControl.cs の視覚行 seam に載せている=2026-08-22 A-6)
// MouseDragging / _wheelAccum の所有権は EditorControl に残す(Router は state を持たない契約)。
using kxEdit.Core.Buffers;
using kxEdit.Core.Editing;
using kxEdit.Core.Layout;
using kxEdit.Core.Text;

namespace kxEdit.Editor;

public sealed partial class EditorControl
{
    // ===== OnKey* / OnMouse* オーバーライド(Router へ委譲する薄ラッパ) =====

    /// <summary>
    /// 移動系キーバインド配線(P3 Task 6)。編集系(BackSpace/Delete/Enter/文字挿入/Cut/Copy/Paste/
    /// Undo/Redo/Tab)は Task 8〜11 で追加した。Phase 3 (Task 3c) で分岐ロジックを
    /// <see cref="InputRouter"/> に移設・本メソッドは薄ラッパ化。
    /// </summary>
    /// <remarks>
    /// - <c>base.OnKeyDown(e)</c> は Router 呼び出しの前に必ず走る(=Control.OnKeyDown が
    ///   KeyDown イベントを外部購読者へ伝搬させる契約を維持)。
    /// - Router 内部で <c>_buffer</c> null 判定を行う(元 OnKeyDown の
    ///   <c>if (_buffer is null) return;</c> と等価)。
    /// - Router のハンドラは元 switch の case body を bit-perfect に移設した pure static メソッド。
    /// - <b>case の順序規約</b>(元コメントの申し送り): C# switch は上から順評価だが、
    ///   Router では KeyCode 単位で 1 ハンドラに集約し、修飾/ReadOnly カスケードは各ハンドラ内で
    ///   <c>if</c> チェーンとして bit-perfect に再現する(<see cref="InputRouter.HandleInsert"/>・
    ///   <see cref="InputRouter.HandleDelete"/> 参照)。
    /// </remarks>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // A-20: 文字挿入以外の入力が挟まったら、保留中の高サロゲートは対にならない(設計 §6.2)。
        // ただし VK_PACKET は「本物のキー入力」ではなく、KEYEVENTF_UNICODE の SendInput が
        // WM_CHAR を運ぶために前置する合成キーである。A-20 の現実の発現源はまさにその経路
        // (設計 §2.2)で、実際の到着順は
        //   WM_KEYDOWN VK_PACKET → WM_CHAR(高) → WM_KEYUP → WM_KEYDOWN VK_PACKET → WM_CHAR(低)
        // となる。ここで無条件に破棄すると、**直そうとしている経路でだけペアが結合しない**
        // (絵文字が U+FFFD ではなく丸ごと消える)。回帰テスト:
        // SurrogatePairInputTests.VkPacket_HighThenLow_InsertsOneCodePoint
        if (e.KeyCode != Keys.Packet)
            DropPendingHighSurrogate();
        _input.RouteKey(e);
    }

    /// <summary>
    /// 文字挿入(WM_CHAR)入り口(P3 Task 8 / P4 Task 3 で <see cref="InsertConfirmedText"/> へ委譲)。
    /// 制御文字を弾いてから確定 1 文字を <see cref="InsertConfirmedText"/> に流し
    /// P4 の IME 確定経路(WM_IME_COMPOSITION の GCS_RESULTSTR)と挿入本体を共有する。
    /// </summary>
    /// <remarks>
    /// <b>制御文字は無視</b>: WM_CHAR は Ctrl 修飾の 0x01〜0x1F(Ctrl+A=0x01 等)や
    /// Ctrl+Backspace の 0x7F(ASCII DEL・Task 8 レビュー I-1)も届くため、これらは除外する。
    /// 編集操作(BackSpace/Enter/Tab/Ctrl+A 等)は <c>OnKeyDown</c> 経路(Task 6/9)で処理済み。
    /// 選択/上書き/サロゲートの分岐と <c>_caretCtrl.DesiredXpx</c>/AfterEdit の後処理は
    /// <see cref="InsertConfirmedText"/> に集約(=1 経路)。<see cref="ReadOnly"/> ON では no-op。
    /// Task 3c: OnKeyPress 経路には分岐ロジックが無い(1 経路のみ)ため Router には載せない。
    /// A-20(設計 2026-08-29 §6): 分割到着するサロゲートペアの結合だけは本メソッドが担う
    /// (<see cref="_pendingHighSurrogate"/>)。<see cref="InsertConfirmedText"/> には
    /// 常に「完結したテキスト」だけを渡す契約を保つため。
    /// </remarks>
    protected override void OnKeyPress(KeyPressEventArgs e)
    {
        base.OnKeyPress(e);
        if (_buffer is null || ReadOnly)
            return;
        char ch = e.KeyChar;

        // 制御文字(0x00〜0x1F, 0x7F)は無視。編集用途は OnKeyDown 経路で処理する(§0-9 温存)。
        // サロゲート(U+D800〜U+DFFF)はこの条件に掛からないので、判定順は問わない。
        if (ch < 0x20 || ch == 0x7F)
        {
            DropPendingHighSurrogate();
            return;
        }

        // A-20: 高位は保留して待つ。連続して高位が来たら前の保留は捨てる。
        if (char.IsHighSurrogate(ch))
        {
            _pendingHighSurrogate = ch;
            e.Handled = true; // WM_CHAR は消費済み(挿入は次の低位到着まで待つ)
            return;
        }

        if (char.IsLowSurrogate(ch))
        {
            char hi = _pendingHighSurrogate;
            _pendingHighSurrogate = NoPendingHighSurrogate;
            // 対になる高位が無い低位は捨てる(U+FFFD を本文に残さない=設計 §3.3)。
            if (hi != NoPendingHighSurrogate)
                InsertConfirmedText(new string(new[] { hi, ch }));
            e.Handled = true;
            return;
        }

        DropPendingHighSurrogate(); // BMP 文字が来た=保留は対にならない
        InsertConfirmedText(ch.ToString());
        e.Handled = true;
    }

    /// <summary>「保留なし」を表す番兵。有効な高サロゲートは U+D800〜U+DBFF なので衝突しない。</summary>
    private const char NoPendingHighSurrogate = '\0';

    /// <summary>
    /// A-20(設計 2026-08-29 §6): WM_CHAR は UTF-16 単位で届くため、サロゲートペアが
    /// 高・低の 2 通に分かれて到着する(KEYEVENTF_UNICODE の SendInput / PostMessage 経路)。
    /// 高位を 1 つだけ保留し、直後に低位が来たときだけペアで挿入する。
    /// 保留は「直後の WM_CHAR」にだけ効く契約(Scintilla の <c>lastHighSurrogateChar</c> と同じ)。
    /// </summary>
    private char _pendingHighSurrogate = NoPendingHighSurrogate;

    /// <summary>
    /// 保留中の高サロゲートを捨てる。文字挿入以外の入力が挟まったら呼ぶ。
    /// </summary>
    /// <remarks>
    /// 破棄契機の列挙は原理的に漏れる(設計 §6.2)が、漏れても被害は
    /// 「保留が 1 文字ぶん長生きする」だけで本文は壊れない
    /// (次の非低サロゲート char が来た時点で必ず破棄されるため)。
    /// 現在の破棄契機は次の 2 系統:
    /// <list type="bullet">
    /// <item>列挙: <see cref="OnKeyDown"/>(<b><see cref="Keys.Packet"/> は除く</b>=
    /// KEYEVENTF_UNICODE が WM_CHAR を運ぶために前置する合成キーなので、
    /// ここで破棄すると A-20 の現実の発現源でだけペアが結合しない) /
    /// <see cref="EditorControl.OnLostFocus"/> /
    /// <see cref="OnKeyPress"/> の制御文字・BMP 文字分岐</item>
    /// <item>事後条件: <see cref="EditorControl.AfterEdit"/>(本文が変わった=保留は対にならない。
    /// 列挙漏れの最後の砦。メニュー経由の貼り付けなど <see cref="OnKeyDown"/> を伴わない
    /// 編集経路はここだけが担当する)</item>
    /// </list>
    /// <b>設計 §6.2 が挙げた「マウス操作」は列挙していない</b>(意図的な逸脱・設計 §10-A に記録)。
    /// マウスでキャレットを動かしても保留は残るが、実際に踏むには 1 回の SendInput が生む
    /// 2 通の WM_CHAR の<b>間に</b>マウス選択を挟む必要があり、現実性が極めて低い。
    /// 踏んだ場合の被害は「選択範囲がペアで置換される」なので、無害ではない点は認識しておく。
    /// </remarks>
    private void DropPendingHighSurrogate() => _pendingHighSurrogate = NoPendingHighSurrogate;

    /// <summary>
    /// マウスホイール(P3 Task 12 で精度改善版に更新)。
    /// - Delta は 1 tick = 120 単位。細切れデルタは <see cref="_wheelAccum"/> に蓄積し、閾値に達したら発火。
    /// - 1 tick あたりの行送り量は <see cref="SystemInformation.MouseWheelScrollLines"/> を使う
    ///   (レジストリ未設定 / 「1 ページ」設定=-1 では既定 3 にフォールバック)。
    /// - Delta&gt;0=上方向スクロール。送りの単位は<b>視覚行</b>で、実処理は
    ///   <see cref="EditorControl.ScrollByVisualRows"/>(折り返し ON は視覚行を歩き、
    ///   OFF は従来どおり <see cref="TopLine"/> の相対移動に委譲する=設計書 I-3)。
    ///   上限/下限を跨ぐ蓄積はどちらの経路でもクランプされ、自然に上端/下端で頭打ちになる。
    ///   2026-08-22 A-6: 折り返し ON で <see cref="TopLine"/> に代入していた頃は、論理行 1 本の
    ///   文書で <c>ClampTopLine</c> の上限が 0 になりホイールが完全に効かなかった。
    /// - 末尾で <see cref="HandledMouseEventArgs.Handled"/> を立てて親コンテナへのバブリングを止める
    ///   (P6 で SplitContainer / ScrollableControl 内に置いた場合の二重スクロール防止)。
    /// Task 3c: Wheel は _wheelAccum 蓄積状態を持ち、経路が独特(HandledMouseEventArgs 判定含む)なので
    /// Router には載せず現状維持。
    /// </summary>
    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_buffer is null)
            return;
        _wheelAccum += e.Delta;
        int wheelLines = SystemInformation.MouseWheelScrollLines;
        // SystemInformation.MouseWheelScrollLines は「1 ページスクロール」設定時 -1 を返す仕様。
        // <=0 の全ケースを 3 行(WinForms 標準の既定値)にフォールバックすることで簡潔化する。
        if (wheelLines <= 0)
            wheelLines = 3;
        while (_wheelAccum >= 120)
        {
            ScrollByVisualRows(-wheelLines);
            _wheelAccum -= 120;
        }
        while (_wheelAccum <= -120)
        {
            ScrollByVisualRows(wheelLines);
            _wheelAccum += 120;
        }
        // WM_MOUSEWHEEL は親コンテナへ再ディスパッチされる仕様のため、Handled=true で
        // 明示的にバブリングを止める(HandledMouseEventArgs は WinForms が MouseWheel 経路で
        // 実際に渡す派生型・is パターンで安全に判定)。
        if (e is HandledMouseEventArgs hme)
            hme.Handled = true;
    }

    /// <summary>
    /// マウス左ボタン Down(P3 Task 12・Task 3c で薄ラッパ化)。
    /// 実処理は <see cref="InputRouter.HandleMouseDown"/>。
    /// </summary>
    /// <remarks>
    /// <c>IsComposing</c> チェックと <c>base.OnMouseDown(e)</c> は Router 呼び出しの前に走る
    /// (元 OnMouseDown と同順=IME 未確定を先にキャンセルしてからマウスイベントを外部購読者へ流す)。
    /// </remarks>
    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (IsComposing)
            CancelCompositionAndDefault();
        base.OnMouseDown(e);
        _input.RouteMouse(MouseEventKind.Down, e);
    }

    /// <summary>
    /// マウス移動(P3 Task 12・ドラッグ選択・Task 3c で薄ラッパ化)。
    /// 実処理は <see cref="InputRouter.HandleMouseMove"/>。
    /// </summary>
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _input.RouteMouse(MouseEventKind.Move, e);
    }

    /// <summary>
    /// マウス左ボタン Up(P3 Task 12・Task 3c で薄ラッパ化)。
    /// 実処理は <see cref="InputRouter.HandleMouseUp"/>。
    /// </summary>
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _input.RouteMouse(MouseEventKind.Up, e);
    }

    /// <summary>
    /// マウス左ボタン ダブルクリック(P3 Task 12・単語選択・Task 3c で薄ラッパ化)。
    /// 実処理は <see cref="InputRouter.HandleMouseDoubleClick"/>。
    /// </summary>
    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        _input.RouteMouse(MouseEventKind.DoubleClick, e);
    }

    /// <summary>
    /// 移動系キー(Arrow/Home/End/PageUp/PageDown)+ Tab を「入力キー」として扱わせ、
    /// フォームレベルのフォーカス遷移やダイアログの既定ボタン発火に持っていかれないようにする。
    /// Tab は Task 9 で OnKeyDown 側に <c>\t</c> 挿入 case を追加したため、ここに含めないと
    /// WinForms のフォーカス遷移(次コントロールへの Tab 移動)に持って行かれ、
    /// <c>OnKeyDown</c> まで届かなくなる(Task 6 申し送り S-2 決着)。
    /// </summary>
    protected override bool IsInputKey(Keys keyData)
    {
        Keys code = keyData & Keys.KeyCode;
        return code switch
        {
            Keys.Left
            or Keys.Right
            or Keys.Up
            or Keys.Down
            or Keys.Home
            or Keys.End
            or Keys.PageUp
            or Keys.PageDown
            or Keys.Tab => true,
            _ => base.IsInputKey(keyData),
        };
    }

    // ===== 座標→char 変換ヘルパ(Router のマウスハンドラから叩く) =====

    /// <summary>
    /// クライアント座標(px)から論理オフセット(UTF-16 char)を算出する純ヘルパ(P3 Task 12)。
    /// - Y &lt; 0 は (<see cref="_topLine"/>, <see cref="_topSegment"/>) の視覚行にクランプ
    /// - 最終視覚行を超えた Y は文書末尾(=<see cref="TextSnapshot.CharLength"/>)にクランプ
    ///   (Notepad と同挙動=末尾行より下の空領域クリックで caret が文書末尾に来る)
    /// - X の行末超過は <see cref="PixelMapper.PxToOffset"/> がセグメント末尾にクランプ
    /// Task 3c: InputRouter の HandleMouseDown/Move/DoubleClick から呼ぶため internal 化。
    /// </summary>
    /// <remarks>
    /// 折り返し ON 時の視覚行の歩きは <c>WalkForwardVisualRows</c> →
    /// <see cref="LineLayout.WrapFirstSegments"/> の打ち切りに載せてあり(2026-08-22 A-6)、
    /// 通り過ぎる論理行は「その行に何本目があるか」が判る本数までしか Wrap しない。
    /// 完全な Wrap は<b>着地行 1 本も含めて</b>行わない=着地行も <c>segIdx + 1</c> 本で
    /// 打ち切る(設計書 I-4)。
    /// Task 14 のベンチで顕在化するようなら Frame 再利用等で最適化(<see cref="ComputeCaretPoint"/>
    /// の Task 9 レビュー M-3 と同じ申し送り)。
    /// </remarks>
    internal int OffsetFromClientPoint(int clientX, int clientY)
    {
        if (_buffer is null)
            return 0;
        var snap = _buffer.Current;
        int lineHeight = _metrics.LineHeightPx;
        int lnWidth = _showLineNumbers ? MeasureLineNumberWidth(snap.LineCount) : 0;

        int visualRowFromTop = clientY / Math.Max(1, lineHeight);
        if (visualRowFromTop < 0)
            visualRowFromTop = 0;

        int maxWidthPx = MaxWrapWidthPx;

        // (TopLine, TopSegment) の視覚行から visualRowFromTop 個進む。前進規約は seam に一本化する
        // (規約を二重定義しない・折り返し OFF ガードと打ち切りを継承する)。
        // 文書末に達した場合(Exhausted)は文書末尾へクランプする=X による位置決めは行わない。
        var (line, segIdx, exhausted) = WalkForwardVisualRows(
            snap,
            _topLine,
            _topSegment,
            visualRowFromTop
        );

        // 最終視覚行より下 → 文書末尾にクランプ
        if (exhausted)
            return snap.CharLength;

        // 対象視覚セグメントを取り出し、X → local offset へ
        int lineStart = snap.GetLineStart(line);
        int lineEnd = snap.GetLineEnd(line, includeBreak: false);
        string lineText =
            lineEnd == lineStart ? string.Empty : snap.GetText(lineStart, lineEnd - lineStart);
        // 着地行も打ち切って Wrap する(I-4)。要るのは segIdx 本目までなので segIdx + 1 本で足りる。
        // 打ち切りが起きたときの Count は要求値に厳密に等しい(= segIdx + 1)ため、下の
        // Math.Min は「打ち切られた最終要素」ではなく segIdx 自身を選ぶ=陳腐化クランプの根拠は保たれる。
        // segIdx + 1 は long 経由(WalkForwardVisualRows / CountVisualRowsForward と同じ理由=
        // int.MaxValue 級の陳腐化 seg で負へ回り込むと WrapFirstSegments が投げる)。
        long landingCap = (long)segIdx + 1;
        var segs = LineLayout
            .WrapFirstSegments(
                lineText,
                maxWidthPx,
                _metrics,
                landingCap > int.MaxValue ? int.MaxValue : (int)landingCap
            )
            .Segments;
        // 防御的にクランプ(通常は segIdx < segs.Count が保たれる)。
        // visualRowFromTop == 0 のとき WalkForwardVisualRows は while に入らず _topSegment を
        // そのまま返すため、陳腐化した _topSegment(編集で段落が縮んだ)を最終セグメントへ
        // 寄せる役目がここに残っている(=ViewportLayout.Build のクランプと同じ寄せ方)。
        int useSeg = Math.Min(segIdx, segs.Count - 1);
        var seg = segs[useSeg];

        // X からセグメント内オフセットを求める。行番号マージンを引き、_scrollX(折り返し OFF 時の
        // 水平シフト)を加算して「セグメント先頭を x=0 とする局所座標」に戻す。
        int xInBody = clientX - lnWidth + _scrollX;
        if (xInBody < 0)
            xInBody = 0;
        var segSpan = lineText.AsSpan(seg.OffsetInLine, seg.Length);
        int localOffset = PixelMapper.PxToOffset(segSpan, xInBody, _metrics);

        return lineStart + seg.OffsetInLine + localOffset;
    }

    // ===== 確定文字列挿入(OnKeyPress + IME 確定 = 2 経路共有) =====

    /// <summary>
    /// 確定文字列を挿入する共通経路(P4)。P3 の <see cref="OnKeyPress"/> と
    /// P4 の <c>WM_IME_COMPOSITION</c> (GCS_RESULTSTR) の両方から呼ばれる。
    /// 選択があれば削除→挿入。<see cref="Overtype"/> ON では直後 1 文字を潰す
    /// (改行はスキップ・サロゲートペアは 2 code units を潰す)。
    /// <see cref="ReadOnly"/> ON では no-op。
    /// </summary>
    /// <remarks>
    /// - <paramref name="text"/> の長さは制限なし(IME 確定は通常 1〜数文字だが仕様上長文もあり)
    /// - <paramref name="text"/> が空文字なら no-op(ESC 取消時の GCS_RESULTSTR=空を安全化)
    /// - <c>_caretCtrl.DesiredXpx</c> は -1 リセット(P3 §0-6)、AfterEdit で追従スクロール
    /// </remarks>
    private void InsertConfirmedText(string text)
    {
        if (_buffer is null || ReadOnly)
            return;
        if (string.IsNullOrEmpty(text))
            return;

        var (s, en) = GetSelectionCharRange();
        if (s != en)
        {
            _buffer.Replace(s, en - s, text);
            _caretCtrl.SetTo(s + text.Length, _buffer.Current);
        }
        else if (Overtype)
        {
            var snap = _buffer.Current;
            int overwriteLen = 0;
            int caret = _caretCtrl.Caret;
            if (caret < snap.CharLength)
            {
                char nc = snap.GetChar(caret);
                // 改行は潰さない(CRLF pair も含めて跨がない)= MoveRightChar とは意図的に違う
                if (nc != '\r' && nc != '\n')
                    overwriteLen = TextBoundary.CodePointLengthAt(snap, caret);
            }
            _buffer.Replace(caret, overwriteLen, text);
            _caretCtrl.SetTo(caret + text.Length, _buffer.Current);
        }
        else
        {
            int caret = _caretCtrl.Caret;
            _buffer.Insert(caret, text);
            _caretCtrl.SetTo(caret + text.Length, _buffer.Current);
        }

        _caretCtrl.DesiredXpx = -1;
        AfterEdit();
    }
}
