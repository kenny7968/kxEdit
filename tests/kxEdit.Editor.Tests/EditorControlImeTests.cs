namespace kxEdit.Editor.Tests;

/// <summary>
/// P4 Task 4-8: EditorControl の IME メッセージ処理(WndProc override)の契約テスト。
/// - WM_IME_SETCONTEXT: lParam の ISC_SHOWUICOMPOSITIONWINDOW ビットを落として base に流す
///   (=既定 composition ウィンドウ描画を抑止し、Task 6 以降の自前描画に切り替える下地)。
/// - WM_IME_STARTCOMPOSITION: 選択削除 + _ime.Start をキャレット位置で初期化(Task 5)。
/// - WM_IME_COMPOSITION: GCS_COMPSTR(未確定=Task 6) / GCS_RESULTSTR(確定=Task 7)を反映。
/// - WM_IME_ENDCOMPOSITION: _ime クリア(Task 8)。
/// - ReadOnly=true / OnLostFocus: 未確定期間中の縁ケース強制取消/確定試行(Task 8・§4-2/4-3)。
/// </summary>
public class EditorControlImeTests
{
    // ClipboardTests / CaretScrollTests と同じ流儀で Form と EditorControl を
    // タプルで返し、テスト側で using (f) using (c) の二重 using で Dispose する。
    // Sta.Run 内で全てを書き下すことも可能だが、IME テストは Task 5〜8 で数が
    // 増えるためヘルパを切り出しておく(P4 Task 5)。
    private static (Form f, EditorControl c) MakeControl(string text)
    {
        var f = new Form { Visible = false };
        var c = new EditorControl();
        f.Controls.Add(c);
        _ = f.Handle;
        c.SetSource(TextBuffer.FromString(text));
        return (f, c);
    }

    // WndProc override が呼ばれ、WM_IME_SETCONTEXT の lParam から
    // ISC_SHOWUICOMPOSITIONWINDOW ビットが落ちて base に流されることを確認する。
    // WndProc は protected なため internal ヘルパー __TestProcessMessage(ref Message) を用意し
    // InternalsVisibleTo でテストから呼べるようにする(EditorControl.csproj 側で既に設定済み)。
    [Fact]
    public void WndProc_ImeSetContext_ClearsShowUICompositionWindowBit() =>
        Sta.Run(() =>
        {
            using var c = new EditorControl();
            using var f = new Form { Visible = false };
            f.Controls.Add(c);
            _ = f.Handle; // Handle プロパティ読み取り=下地の win32 ハンドル生成が目的の副作用

            var m = new Message
            {
                HWnd = c.Handle,
                Msg = NativeMethods.WM_IME_SETCONTEXT,
                WParam = 1,
                LParam = new nint(NativeMethods.ISC_SHOWUICOMPOSITIONWINDOW | 0x0F),
            };
            c.__TestProcessMessage(ref m);
            // 期待: base に渡す前に ISC_SHOWUICOMPOSITIONWINDOW ビットが落ちている(=lParam に反映済)
            Assert.Equal(0x0F, m.LParam.ToInt64() & 0xFFFFFFFFL);
        });

    // WM_IME_STARTCOMPOSITION 受信時:
    // - 選択があれば削除して先頭位置にキャレットを寄せる(1 Splice=1 Undo)
    // - _ime.Start = 現在のキャレット位置 に初期化
    // - _ime.Text は空のまま(=IsComposing は false)
    [Fact]
    public void ImeStartComposition_ClearsSelectionAndInitializesImeStart() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdef");
            using (f)
            using (c)
            {
                c.SetSelectionCharRange(1, 4); // 選択 "bcd"

                var m = new Message
                {
                    HWnd = c.Handle,
                    Msg = NativeMethods.WM_IME_STARTCOMPOSITION,
                };
                c.__TestProcessMessage(ref m);

                Assert.Equal("aef", c.GetText()); // 選択削除
                Assert.Equal(1, c.CaretCharOffset); // キャレット = 選択先頭
                Assert.Equal(1, c.__TestImeStart());
                Assert.False(c.__TestIsComposing()); // 未確定文字列はまだ空
            }
        });

    // 選択が無い状態で WM_IME_STARTCOMPOSITION を受けた場合、キャレット位置は
    // 変わらず、_ime.Start が現在のキャレット位置で初期化される。
    // (本文編集が発生しないため AfterEdit の副作用は走らない=Task 5 設計)
    [Fact]
    public void ImeStartComposition_NoSelection_KeepsCaretAndInitializesImeStart() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdef");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(3);

                var m = new Message
                {
                    HWnd = c.Handle,
                    Msg = NativeMethods.WM_IME_STARTCOMPOSITION,
                };
                c.__TestProcessMessage(ref m);

                Assert.Equal("abcdef", c.GetText()); // 本文不変
                Assert.Equal(3, c.CaretCharOffset);
                Assert.Equal(3, c.__TestImeStart());
                Assert.False(c.__TestIsComposing());
            }
        });

    // WM_IME_COMPOSITION 相当を __TestApplyComposition 経由で流し、_ime に Text/CursorPos/
    // Attrs/Clauses が反映されて IsComposing=true になることを確認する(Task 6)。
    // STARTCOMPOSITION で凍結した _ime.Start は多重メッセージでも維持される
    // (=以後の COMPOSITION で Start は上書きされない=設計 §0 4-9)。
    [Fact]
    public void ApplyComposition_UpdatesImeFields() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);

                // STARTCOMPOSITION 相当
                var m1 = new Message
                {
                    HWnd = c.Handle,
                    Msg = NativeMethods.WM_IME_STARTCOMPOSITION,
                };
                c.__TestProcessMessage(ref m1);

                // COMPOSITION 相当を internal 経由で流す
                c.__TestApplyComposition("あい", cursorPos: 2, attrs: [0, 0], clauses: [0, 2]);

                Assert.True(c.__TestIsComposing());
                Assert.Equal("あい", c.__TestImeText());
                Assert.Equal(1, c.__TestImeStart()); // STARTCOMPOSITION で凍結した _caret
            }
        });

    // 空文字への遷移(例: 全消し/取消)で IsComposing=false に戻る。
    // Start は Task 6 の範囲では触らず、Text.Length で IsActive を判定する仕様
    // (=クリーンアップは Task 8 の WM_IME_ENDCOMPOSITION 側で担う)。
    [Fact]
    public void ApplyComposition_EmptyText_ReturnsToEmpty() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                var m = new Message
                {
                    HWnd = c.Handle,
                    Msg = NativeMethods.WM_IME_STARTCOMPOSITION,
                };
                c.__TestProcessMessage(ref m);
                c.__TestApplyComposition("あ", cursorPos: 1, attrs: [0], clauses: [0, 1]);
                c.__TestApplyComposition("", cursorPos: 0, attrs: [], clauses: []);

                Assert.False(c.__TestIsComposing());
            }
        });

    // 多重 COMPOSITION での Start 保持 (§0 4-9): START(caret=1)で凍結された Start=1 が、
    // 以降 2 回連続する COMPOSITION で書き換えられないことを直接ロックする。Task 6 レビュー M-4。
    [Fact]
    public void ApplyComposition_MultipleUpdates_KeepsInitialStart() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);
                var m = new Message
                {
                    HWnd = c.Handle,
                    Msg = NativeMethods.WM_IME_STARTCOMPOSITION,
                };
                c.__TestProcessMessage(ref m);

                c.__TestApplyComposition("あ", cursorPos: 1, attrs: [0], clauses: [0, 1]);
                Assert.Equal(1, c.__TestImeStart());

                c.__TestApplyComposition("あい", cursorPos: 2, attrs: [0, 0], clauses: [0, 2]);
                Assert.Equal(1, c.__TestImeStart()); // 2 回目以降も凍結値のまま

                c.__TestApplyComposition("あいう", cursorPos: 3, attrs: [0, 0, 0], clauses: [0, 3]);
                Assert.Equal(1, c.__TestImeStart());
            }
        });

    // Task 7: GCS_RESULTSTR 確定通知の適用本体を検証する。
    // - _ime を先にクリアしてから InsertConfirmedText(Task 3)を呼ぶ=1 Splice=1 Undo
    // - IsComposing が false に戻る(overlay は本文に確定=もう浮いていない)
    // - キャレットは Start + 確定文字数 に位置する
    // - 1 度の Undo で確定前の本文/キャレット状態(=元の "abc"・キャレット位置 1)に戻る
    //   (InsertConfirmedText 内の _buffer.Insert が単一 Splice で履歴に積まれることを担保する)
    [Fact]
    public void ApplyResult_InsertsAsSingleUndo() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);

                var m = new Message
                {
                    HWnd = c.Handle,
                    Msg = NativeMethods.WM_IME_STARTCOMPOSITION,
                };
                c.__TestProcessMessage(ref m);
                c.__TestApplyComposition("あい", cursorPos: 2, attrs: [0, 0], clauses: [0, 2]);
                c.__TestApplyResult("漢字");

                Assert.False(c.__TestIsComposing());
                Assert.Equal("a漢字bc", c.GetText());
                Assert.Equal(1 + 2, c.CaretCharOffset); // Start + 確定文字数
                Assert.True(c.CanUndo);
                c.Undo();
                Assert.Equal("abc", c.GetText()); // 1 Undo で元に戻る
            }
        });

    // Task 7: ESC 取消時など GCS_RESULTSTR が空文字列で来た場合、本文/キャレットは
    // 一切変わらず overlay だけがクリアされる(=IsComposing=false)。
    // InsertConfirmedText は「text.Length > 0」ガードで空文字を捨てるため履歴に積まれない
    // (=Undo エントリが増えず、直前の本文編集を巻き戻したい呼び出し側が困らない)。
    [Fact]
    public void ApplyResult_EmptyString_NoInsert() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(1);
                var m = new Message
                {
                    HWnd = c.Handle,
                    Msg = NativeMethods.WM_IME_STARTCOMPOSITION,
                };
                c.__TestProcessMessage(ref m);
                c.__TestApplyComposition("あ", cursorPos: 1, attrs: [0], clauses: [0, 1]);
                c.__TestApplyResult(""); // ESC 取消相当

                Assert.False(c.__TestIsComposing());
                Assert.Equal("abc", c.GetText());
                Assert.Equal(1, c.CaretCharOffset);
                Assert.False(c.CanUndo); // 履歴に副作用を残さない(Task 7 レビュー M-1)
            }
        });

    // Task 8: WM_IME_ENDCOMPOSITION 受信時に _ime がクリアされ、IsComposing=false に戻る。
    // OnImeEndComposition は overlay(_ime)を Empty へリセットして Invalidate を呼ぶだけ
    // (=本文への副作用は無い。既に GCS_RESULTSTR で確定済みであれば ApplyResult で
    //  overlay は落ちているが、ENDCOMPOSITION は「未確定期間の終端」を示す独立メッセージで、
    //  取消経路(空 RESULTSTR + ENDCOMPOSITION)からも呼ばれる=無条件クリアが正しい)。
    [Fact]
    public void EndComposition_ClearsIme() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                var m1 = new Message
                {
                    HWnd = c.Handle,
                    Msg = NativeMethods.WM_IME_STARTCOMPOSITION,
                };
                c.__TestProcessMessage(ref m1);
                c.__TestApplyComposition("あ", cursorPos: 1, attrs: [0], clauses: [0, 1]);
                Assert.True(c.__TestIsComposing());

                var m2 = new Message { HWnd = c.Handle, Msg = NativeMethods.WM_IME_ENDCOMPOSITION };
                c.__TestProcessMessage(ref m2);

                Assert.False(c.__TestIsComposing());
            }
        });

    // Task 8: ReadOnly = true への切り替え時、未確定期間中なら CancelCompositionAndDefault
    // 経路で _ime を強制クリアする(§4-2)。ImmNotifyIME(CPS_CANCEL) の失敗有無に依らず
    // overlay は必ずクリアされる(=IME 無効/取得失敗環境でも overlay が浮きっぱなしにならない)。
    // 既存の ReadOnly=true 使用箇所は未確定期間外なので、CancelCompositionAndDefault の
    // 早期 return で no-op となり挙動不変(§Task 8 注意書き)。
    [Fact]
    public void ReadOnly_True_DuringComposition_ForceCancels() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                var m = new Message
                {
                    HWnd = c.Handle,
                    Msg = NativeMethods.WM_IME_STARTCOMPOSITION,
                };
                c.__TestProcessMessage(ref m);
                c.__TestApplyComposition("あ", cursorPos: 1, attrs: [0], clauses: [0, 1]);
                Assert.True(c.__TestIsComposing());

                c.ReadOnly = true;
                Assert.False(c.__TestIsComposing()); // 強制取消で _ime クリア
            }
        });

    // Task 8: OnLostFocus 拡張(§4-3)。フォーカスが他コントロールへ移ったとき、
    // 未確定期間中なら CPS_COMPLETE を試みて overlay を落とす。ImmNotifyIME が
    // 届かない環境(テスト/一部 IME 無効)でも _ime を Empty にリセットする保険が入っている。
    // NOTE: Control.Select() は CanFocus=true(=可視+enabled)を要求するため、
    // 既存の CaretScrollTests の流儀(f.Show() でフォーカスを機能させる)に合わせる。
    [Fact]
    public void LostFocus_DuringComposition_ClearsIme() =>
        Sta.Run(() =>
        {
            using var f = new Form { Size = new System.Drawing.Size(200, 60) };
            using var c = new EditorControl();
            using var other = new Button();
            f.Controls.Add(c);
            f.Controls.Add(other);
            f.Show(); // フォーカス機能を有効化(不可視だと Select() が no-op)
            c.SetSource(TextBuffer.FromString("abc"));
            c.Select();

            var m = new Message { HWnd = c.Handle, Msg = NativeMethods.WM_IME_STARTCOMPOSITION };
            c.__TestProcessMessage(ref m);
            c.__TestApplyComposition("あ", cursorPos: 1, attrs: [0], clauses: [0, 1]);
            Assert.True(c.__TestIsComposing());

            other.Select(); // フォーカス移動 → OnLostFocus 経路
            Assert.False(c.__TestIsComposing());
        });

    // Task 13: 未確定期間中に外部から編集 API(ReplaceCharRange)が呼ばれた場合、
    // まず未確定を強制取消してから置換を実行する(=undefined behavior 防止・§4-6)。
    // - IsComposing=false に戻る
    // - 置換自体は実行され、期待どおりの本文になる
    //   (=ガードは cancel だけ実行し、本来の置換動作は妨げない)
    [Fact]
    public void ReplaceCharRange_DuringComposition_CancelsFirst() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abcdef");
            using (f)
            using (c)
            {
                c.SetCaretCharOffset(3);
                var m = new Message
                {
                    HWnd = c.Handle,
                    Msg = NativeMethods.WM_IME_STARTCOMPOSITION,
                };
                c.__TestProcessMessage(ref m);
                c.__TestApplyComposition("あい", cursorPos: 2, attrs: [0, 0], clauses: [0, 2]);
                Assert.True(c.__TestIsComposing());

                c.ReplaceCharRange(0, 3, "XYZ");
                Assert.False(c.__TestIsComposing());
                Assert.Equal("XYZdef", c.GetText());
            }
        });

    // Task 13: 未確定期間中に SetCaretCharOffset が呼ばれた場合も同様に強制取消する。
    // App 層 programmatic ジャンプ(検索ジャンプ・GoTo 等)が未確定中に走っても
    // overlay が浮きっぱなしにならない(§4-6)。
    [Fact]
    public void SetCaretCharOffset_DuringComposition_Cancels() =>
        Sta.Run(() =>
        {
            var (f, c) = MakeControl("abc");
            using (f)
            using (c)
            {
                var m = new Message
                {
                    HWnd = c.Handle,
                    Msg = NativeMethods.WM_IME_STARTCOMPOSITION,
                };
                c.__TestProcessMessage(ref m);
                c.__TestApplyComposition("あ", cursorPos: 1, attrs: [0], clauses: [0, 1]);
                c.SetCaretCharOffset(2);
                Assert.False(c.__TestIsComposing());
            }
        });
}
