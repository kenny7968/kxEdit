using System.Collections.Generic;
using System.Windows.Forms;
using kxEdit.Accessibility;
using kxEdit.Core.Buffers;
using kxEdit.Core.Text;
using kxEdit.Editor;
using Xunit;

namespace kxEdit.Editor.Tests;

public class EditorControlConvertEolsTests
{
    [Fact]
    public void ConvertEols_ToCrlf_ReplacesLoneLfs()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("aaa\nbbb\nccc"));
            ctrl.ConvertEols(LineEnding.Crlf);
            Assert.Equal("aaa\r\nbbb\r\nccc", ctrl.SnapshotText);
            Assert.Equal(LineEnding.Crlf, ctrl.EolMode);
        });
    }

    [Fact]
    public void ConvertEols_ToLf_ReplacesCrlfs()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("aaa\r\nbbb\r\nccc"));
            ctrl.ConvertEols(LineEnding.Lf);
            Assert.Equal("aaa\nbbb\nccc", ctrl.SnapshotText);
        });
    }

    [Fact]
    public void ConvertEols_ToCr_ReplacesAll()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("aaa\r\nbbb\nccc\r"));
            ctrl.ConvertEols(LineEnding.Cr);
            Assert.Equal("aaa\rbbb\rccc\r", ctrl.SnapshotText);
        });
    }

    [Fact]
    public void ConvertEols_BeforeSetSource_NoOp()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            // Should not throw; buffer is null so early return
            ctrl.ConvertEols(LineEnding.Crlf);
            Assert.Equal(string.Empty, ctrl.SnapshotText);
        });
    }

    [Fact]
    public void ConvertEols_FastPath_PreservesCaretAndSetsEolMode()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            // Buffer already in LF form — target=Lf means converted == src → fast-path
            ctrl.SetSource(TextBuffer.FromString("aaa\nbbb\nccc"));
            ctrl.SetCaretCharOffset(5);
            Assert.Equal(5, ctrl.CaretCharOffset);

            ctrl.ConvertEols(LineEnding.Lf);

            // Fast-path: buffer NOT rebuilt via ReplaceSource, so caret preserved
            Assert.Equal(5, ctrl.CaretCharOffset);
            Assert.Equal(LineEnding.Lf, ctrl.EolMode);
            Assert.Equal("aaa\nbbb\nccc", ctrl.SnapshotText);
        });
    }

    // P6 レビュー I-2 回帰: 非 fast-path でも caret 論理位置が保持される
    [Fact]
    public void ConvertEols_NonFastPath_PreservesCaretLogicalPosition_LfToCrlf()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            // 3 行 LF、行 1 の 2 文字目にキャレット="aaa\n"+"b"+"b" の直後
            ctrl.SetSource(TextBuffer.FromString("aaa\nbbb\nccc"));
            ctrl.SetCaretCharOffset(6); // 'b' 'b' の間=行 1 の offset 2

            ctrl.ConvertEols(LineEnding.Crlf);

            // 変換後: "aaa\r\nbbb\r\nccc"、同じ論理位置は行 1 の offset 2=absolute offset 7
            // (非改行文字 5=a a a b b + 改行数 1=\n→\r\n の 2 chars)
            Assert.Equal("aaa\r\nbbb\r\nccc", ctrl.SnapshotText);
            Assert.Equal(7, ctrl.CaretCharOffset);
            Assert.Equal(LineEnding.Crlf, ctrl.EolMode);
        });
    }

    [Fact]
    public void ConvertEols_NonFastPath_PreservesCaretLogicalPosition_CrlfToLf()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            // 3 行 CRLF、行 2 の先頭="aaa\r\nbbb\r\n"+"ccc" の先頭 c
            ctrl.SetSource(TextBuffer.FromString("aaa\r\nbbb\r\nccc"));
            ctrl.SetCaretCharOffset(10); // 行 2 の offset 0

            ctrl.ConvertEols(LineEnding.Lf);

            // 変換後: "aaa\nbbb\nccc"、同じ論理位置は絶対 8(非改行 6+改行 2)
            Assert.Equal("aaa\nbbb\nccc", ctrl.SnapshotText);
            Assert.Equal(8, ctrl.CaretCharOffset);
            Assert.Equal(LineEnding.Lf, ctrl.EolMode);
        });
    }

    [Fact]
    public void ConvertEols_NonFastPath_PreservesAnchorForSelection()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("aaa\nbbb\nccc"));
            // 選択: [4, 7)=行 1 全体("bbb")
            ctrl.SelectCharRange(4, 3);
            var (s0, e0) = ctrl.GetSelectionCharRange();
            Assert.Equal((4, 7), (s0, e0));

            ctrl.ConvertEols(LineEnding.Crlf);

            // 変換後 "aaa\r\nbbb\r\nccc" で行 1 全体="bbb"=[5, 8)
            var (s1, e1) = ctrl.GetSelectionCharRange();
            Assert.Equal((5, 8), (s1, e1));
        });
    }

    // P7 I-3 Task 3: chunk 境界(=TextBufferBuilder.TargetChunkBytes 近傍)で
    // CRLF が別チャンクへ跨っても LF に正しく統一される(byte 単位走査+pendingCr 吸収の回帰)。
    [Fact]
    public void ConvertEols_Utf8_LargeContent_ChunkBoundary_CrlfSpansChunks()
    {
        Sta.Run(() =>
        {
            // 4MB(TextBufferBuilder.TargetChunkBytes)近傍で CRLF が切れるように文字列を組む
            // ASCII のみで 4MB - 1 バイトのフィラー + "\r\n" を境界に置く
            int fill = 4 * 1024 * 1024 - 1;
            string body = new string('a', fill) + "\r\n" + "tail\n";
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString(body));
            ctrl.ConvertEols(LineEnding.Lf);
            string result = ctrl.SnapshotText;
            Assert.Equal(new string('a', fill) + "\n" + "tail\n", result);
        });
    }

    // P7 I-3 Task 3: 混在 EOL(CRLF/CR/LF)が一括で target=CRLF に統一される(fast-path 非適用パス)。
    [Fact]
    public void ConvertEols_Utf8_MixedEols_AllConvertedToTarget()
    {
        Sta.Run(() =>
        {
            string body = "a\r\nb\rc\nd\r\ne";
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString(body));
            ctrl.ConvertEols(LineEnding.Crlf);
            Assert.Equal("a\r\nb\r\nc\r\nd\r\ne", ctrl.SnapshotText);
        });
    }

    // P7 I-3 Task 3 Minor-2: 文書末尾が孤立 CR = foreach 後の `if (pendingCr) EmitEol` の drain 経路を検証。
    [Fact]
    public void ConvertEols_TrailingLoneCr_ToLf_DrainedByPendingCr()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc\r"));
            ctrl.ConvertEols(LineEnding.Lf);
            Assert.Equal("abc\n", ctrl.SnapshotText);
        });
    }

    // P7 I-3 Task 3 Minor-2: CRLF が 4MB ピース境界を跨ぐ +全体 CRLF 統一 +target=CRLF
    // → IsEolAlreadyUniform が pendingCr で境界跨ぎ CRLF を正しく accept、fast-path で return する。
    // fast-path 発火の証拠: EolMode だけ更新される(挙動観察=結果本文が完全に不変)。
    [Fact]
    public void ConvertEols_FastPath_CrlfSpansChunks_WithTargetCrlf_NoRebuild()
    {
        Sta.Run(() =>
        {
            int fill = 4 * 1024 * 1024 - 1;
            string body = new string('a', fill) + "\r\n" + new string('b', 100) + "\r\n";
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString(body));
            ctrl.ConvertEols(LineEnding.Crlf);
            // 変換不要=完全不変
            Assert.Equal(body, ctrl.SnapshotText);
            Assert.Equal(LineEnding.Crlf, ctrl.EolMode);
        });
    }

    // P7 I-3 Task 3 Minor-2 → 2026-07-24 CRLF atomic caret 対応で更新:
    // 文書 "a\r\nb"(char length=4)に SetCaretCharOffset(2) を要求しても、
    // CaretController.SnapAndClamp が mid-CRLF を CR の前(offset=1)へスナップするため
    // caret はそもそも LF 位置に立たない(=境界回帰: 復元後も mid-CRLF に落ちない)。
    // ConvertEols(Lf) 変換後 "a\nb"(char length=3)は「a」の直後=offset 1 のまま保たれる
    // (prefix "a" は非改行 1・改行 0=1+0*1=1)。
    [Fact]
    public void ConvertEols_CaretRequestedMidCrlf_SnappedAndPreservedAcrossConversion()
    {
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\r\nb"));
            ctrl.SetCaretCharOffset(2); // mid-CRLF 要求 → SnapAndClamp が offset=1 へスナップ
            Assert.Equal(1, ctrl.CaretCharOffset); // 中央 seam で snap されたことを明示
            ctrl.ConvertEols(LineEnding.Lf);
            Assert.Equal("a\nb", ctrl.SnapshotText);
            // caret は「a」の直後(=位置 1)。変換前の論理位置と一致(prefix=1 非改行+0 改行)
            Assert.Equal(1, ctrl.CaretCharOffset);
        });
    }

    // ===== A-11: ConvertEols の Undo/Redo 履歴保存(2026-08-28) =====
    // 監査 A-11: 非 fast-path の ConvertEols が ReplaceSource で新規 TextBuffer に差し替わり、
    // 変換前の Undo/Redo 履歴が全消去されていた。CRLF 文書に LF 混じりを貼って Ctrl+S すると
    // 直後の Ctrl+Z が無反応になる症状。

    /// <summary>SavePointLeft / SavePointReached の発火列を記録する(購読後の分だけ)。</summary>
    private static List<string> RecordSavePointEvents(EditorControl ctrl)
    {
        var log = new List<string>();
        ctrl.SavePointLeft += (_, _) => log.Add("Left");
        ctrl.SavePointReached += (_, _) => log.Add("Reached");
        return log;
    }

    [Fact]
    public void ConvertEols_NonFastPath_IsUndoable() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\nb\nc"));
            ctrl.ConvertEols(LineEnding.Crlf);
            Assert.Equal("a\r\nb\r\nc", ctrl.SnapshotText);
            Assert.True(ctrl.CanUndo);
            ctrl.Undo();
            Assert.Equal("a\nb\nc", ctrl.SnapshotText);
        });

    // A-11 の本質: 変換前に積んだ編集履歴が変換後も辿れること。
    [Fact]
    public void ConvertEols_NonFastPath_PreservesEarlierUndoHistory() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a"));
            ctrl.ReplaceCharRange(1, 0, "\nX");
            ctrl.CurrentBuffer.BreakUndoCoalescing();
            ctrl.ReplaceCharRange(3, 0, "\nY");
            Assert.Equal("a\nX\nY", ctrl.SnapshotText);
            ctrl.ConvertEols(LineEnding.Crlf);
            Assert.Equal("a\r\nX\r\nY", ctrl.SnapshotText);
            ctrl.Undo();
            Assert.Equal("a\nX\nY", ctrl.SnapshotText);
            ctrl.Undo();
            Assert.Equal("a\nX", ctrl.SnapshotText);
            ctrl.Undo();
            Assert.Equal("a", ctrl.SnapshotText);
        });

    // 変換エントリは 1 Undo 単位=1 回の Undo で変換前へ戻り、2 回目は変換前の編集へ進む
    // (=変換が複数エントリに割れていないことの確認)。
    [Fact]
    public void ConvertEols_NonFastPath_RecordsExactlyOneUndoEntry() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\nb\nc"));
            ctrl.ReplaceCharRange(5, 0, "d"); // "a\nb\ncd"(変換前の編集を 1 つ積む)
            ctrl.ConvertEols(LineEnding.Crlf);
            Assert.Equal("a\r\nb\r\ncd", ctrl.SnapshotText);
            ctrl.Undo();
            Assert.Equal("a\nb\ncd", ctrl.SnapshotText); // 1 回で変換前へ
            ctrl.Undo();
            Assert.Equal("a\nb\nc", ctrl.SnapshotText); // 2 回目は直前の編集を戻す
            Assert.False(ctrl.CanUndo);
        });

    // 変換エントリは直前のタイプ操作へ融合しない(ReplaceAllRecordingUndo の前置
    // BreakCoalescing + insertHasBreak: true を Editor 側から観測する)。
    [Fact]
    public void ConvertEols_NonFastPath_DoesNotCoalesceWithPrecedingTyping() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\nb"));
            ctrl.ReplaceCharRange(3, 0, "x"); // 1 文字タイプ相当="a\nbx"
            ctrl.ConvertEols(LineEnding.Crlf);
            ctrl.Undo();
            // 融合していれば "a\nb" まで一気に戻ってしまう。
            Assert.Equal("a\nbx", ctrl.SnapshotText);
        });

    // fast-path では履歴に何も積まれないこと。
    // no-change テストなので既定値(履歴空)ではなく、履歴を 1 つ積んだ非既定状態から始める
    // (CLAUDE.md §4-B)。
    [Fact]
    public void ConvertEols_FastPath_RecordsNothingInHistory() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\r\nb"));
            ctrl.ReplaceCharRange(4, 0, "Z");
            Assert.Equal("a\r\nbZ", ctrl.SnapshotText);
            ctrl.ConvertEols(LineEnding.Crlf); // すでに CRLF 統一=fast-path
            ctrl.Undo();
            Assert.Equal("a\r\nb", ctrl.SnapshotText);
            Assert.False(ctrl.CanUndo);
        });

    // ===== A-11: SavePoint イベント発火列(設計書 §10.12 (1)) =====
    // AfterEdit() を呼ぶ素直な実装は _wasModified の false→true 遷移で
    // 「保存処理の途中に」SavePointLeft を焚く。main は ReplaceSource:301 の直接代入で
    // 一切焚かないため、それが退行にならないことをここで固定する。

    [Fact]
    public void ConvertEols_NonFastPath_OnSavedDocument_FiresNoSavePointEvents() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\nb\nc"));
            ctrl.SetSavePoint(); // 保存済み=Modified false から始める
            var log = RecordSavePointEvents(ctrl);

            ctrl.ConvertEols(LineEnding.Crlf);

            Assert.Empty(log);
        });

    [Fact]
    public void ConvertEols_NonFastPath_OnDirtyDocument_FiresNoSavePointEvents() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\nb\nc"));
            ctrl.SetSavePoint();
            ctrl.ReplaceCharRange(5, 0, "d"); // dirty 化(ここで SavePointLeft は消費済み)
            var log = RecordSavePointEvents(ctrl);

            ctrl.ConvertEols(LineEnding.Crlf);

            Assert.Empty(log);
        });

    // 保存成功パス: ConvertEols → SetSavePoint。発火列は ["Reached"] 1 件のみ。
    [Fact]
    public void ConvertEols_NonFastPath_ThenSetSavePoint_FiresReachedOnce() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\nb\nc"));
            ctrl.SetSavePoint();
            var log = RecordSavePointEvents(ctrl);

            ctrl.ConvertEols(LineEnding.Crlf);
            ctrl.SetSavePoint(); // 書き込み成功後に App 層が打つ保存点

            Assert.Equal(new[] { "Reached" }, log);
            Assert.False(ctrl.Modified);
        });

    // 保存失敗パス(Task 4 のロールバック形): ConvertEols → Undo。
    // _savedRoot を触らない設計なので Undo で保存点の root に戻り Modified が false へ復す。
    [Fact]
    public void ConvertEols_NonFastPath_ThenUndo_RestoresSavePoint() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\nb\nc"));
            ctrl.SetSavePoint();
            var log = RecordSavePointEvents(ctrl);

            ctrl.ConvertEols(LineEnding.Crlf);
            Assert.True(ctrl.Modified); // 差し替えで保存点から離れる(イベントは焚かない)
            Assert.Empty(log);

            ctrl.Undo();

            Assert.Equal("a\nb\nc", ctrl.SnapshotText);
            Assert.False(ctrl.Modified);
            Assert.Equal(new[] { "Reached" }, log);
        });

    // 保存成功パス(dirty 文書): ConvertEols → SetSavePoint。dirty でも発火列は ["Reached"]。
    [Fact]
    public void ConvertEols_NonFastPath_OnDirtyDocument_ThenSetSavePoint_FiresReachedOnce() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\nb\nc"));
            ctrl.SetSavePoint();
            ctrl.ReplaceCharRange(5, 0, "d"); // dirty 化
            var log = RecordSavePointEvents(ctrl);

            ctrl.ConvertEols(LineEnding.Crlf);
            ctrl.SetSavePoint();

            Assert.Equal(new[] { "Reached" }, log);
            Assert.False(ctrl.Modified);
        });

    // 保存失敗パス(dirty 文書): ConvertEols → Undo。保存点には戻らない=遷移が無いので無発火。
    [Fact]
    public void ConvertEols_NonFastPath_OnDirtyDocument_ThenUndo_FiresNoSavePointEvents() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\nb\nc"));
            ctrl.SetSavePoint();
            ctrl.ReplaceCharRange(5, 0, "d"); // dirty 化="a\nb\ncd"
            var log = RecordSavePointEvents(ctrl);

            ctrl.ConvertEols(LineEnding.Crlf);
            ctrl.Undo();

            Assert.Equal("a\nb\ncd", ctrl.SnapshotText);
            Assert.True(ctrl.Modified);
            Assert.Empty(log);
        });

    // ===== A-11: ReplaceSource が担っていた副作用の明示化(設計書 §5.2 契約表) =====

    // UIA スナップショット更新: RPC スレッドが読む _bufferSnapshot が変換後本文になっていること。
    [Fact]
    public void ConvertEols_NonFastPath_UpdatesUiaSnapshot() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\nb\nc"));
            var host = (IUiaTextHost)ctrl;
            Assert.Equal(5, host.TextLength);

            ctrl.ConvertEols(LineEnding.Crlf);

            Assert.Equal(7, host.TextLength);
            Assert.Equal("a\r\nb\r\nc", host.GetTextRange(0, 7));
        });

    // UpdateUI(App 層ステータスバー更新契機)は変換 1 回につき 1 回。
    [Fact]
    public void ConvertEols_NonFastPath_FiresUpdateUiOnce() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\nb\nc"));
            int updateUi = 0;
            ctrl.UpdateUI += (_, _) => updateUi++;

            ctrl.ConvertEols(LineEnding.Crlf);

            Assert.Equal(1, updateUi);
        });

    // fast-path は EolMode 更新のみ=UpdateUI も焚かない。
    // 非既定状態(1 回カウント済み)から始めて既定値 0 と区別する(CLAUDE.md §4-B)。
    [Fact]
    public void ConvertEols_FastPath_FiresNoUpdateUi() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\nb\nc"));
            int updateUi = 0;
            ctrl.UpdateUI += (_, _) => updateUi++;
            ctrl.ConvertEols(LineEnding.Crlf); // 非 fast-path=1 回焚かれる
            Assert.Equal(1, updateUi);

            ctrl.ConvertEols(LineEnding.Crlf); // 2 回目は fast-path

            Assert.Equal(1, updateUi);
        });

    // 設計書 §10.12 (1): MouseDragging のリセットは in-place 化で意図的に落とす
    // (バッファ参照が変わらない=ドラッグ選択の途中状態を破棄する理由が無い)。
    [Fact]
    public void ConvertEols_NonFastPath_KeepsMouseDragging() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\nb\nc"));
            ctrl.MouseDragging = true;

            ctrl.ConvertEols(LineEnding.Crlf);

            Assert.True(ctrl.MouseDragging);
        });

    // ===== A-11: 戻り値の契約(Task 4 のロールバック判定の唯一の根拠) =====

    [Fact]
    public void ConvertEols_NonFastPath_ReturnsTrue() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\nb\nc"));
            Assert.True(ctrl.ConvertEols(LineEnding.Crlf));
        });

    [Fact]
    public void ConvertEols_FastPath_ReturnsFalse() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("a\r\nb\r\nc"));
            Assert.False(ctrl.ConvertEols(LineEnding.Crlf));
        });

    // 改行を 1 つも持たない本文は fast-path(=どの target でも「統一済み」)。
    [Fact]
    public void ConvertEols_NoLineBreaks_ReturnsFalse() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            ctrl.SetSource(TextBuffer.FromString("abc"));
            Assert.False(ctrl.ConvertEols(LineEnding.Crlf));
            Assert.False(ctrl.CanUndo);
        });

    [Fact]
    public void ConvertEols_BeforeSetSource_ReturnsFalse() =>
        Sta.Run(() =>
        {
            using var ctrl = new EditorControl();
            Assert.False(ctrl.ConvertEols(LineEnding.Crlf));
        });
}

// A-11: UIA イベント発火は静的な TestHook_ForceUiaListen を使うため、既存の
// EditorControlUiaEventsTests と同じ collection に入れて並列実行から隔離する。
[Collection("UiaEventHook")]
public class EditorControlConvertEolsUiaEventTests
{
    // 設計書 §5.2 契約表: ReplaceSource が担っていた UIA TextChanged / SelectionChanged を
    // in-place 化後も同じ回数だけ打つ(発火「順序」= caret 復元後になった点は意図的な変更で、
    // 回数では区別できない。L5 で SR の実発声を確認する)。
    [Fact]
    public void ConvertEols_NonFastPath_RaisesTextChangedAndSelectionChangedOnce() =>
        Sta.Run(() =>
        {
            EditorControl.TestHook_ForceUiaListen = true;
            try
            {
                using var ctrl = new EditorControl();
                ctrl.SetSource(TextBuffer.FromString("a\nb\nc"));
                using var form = new Form();
                form.Controls.Add(ctrl);
                form.Show();
                try
                {
                    // WM_GETOBJECT 経由でプロバイダを生成させる(=RaiseUia の early return を回避)
                    var msg = Message.Create(
                        ctrl.Handle,
                        0x003D,
                        System.IntPtr.Zero,
                        new System.IntPtr(-25)
                    );
                    EditorControl.TestHook_WndProc(ctrl, ref msg);
                    EditorControl.TestHook_ResetUiaEventCounts(ctrl);

                    ctrl.ConvertEols(LineEnding.Crlf);
                    Application.DoEvents();

                    var (textChanged, selChanged, _) = EditorControl.TestHook_UiaEventCounts(ctrl);
                    Assert.Equal(1, textChanged);
                    Assert.Equal(1, selChanged);
                }
                finally
                {
                    form.Close();
                }
            }
            finally
            {
                EditorControl.TestHook_ForceUiaListen = false;
            }
        });

    // fast-path は UIA 通知も打たない。no-change テストなので非既定状態
    // (非 fast-path で 1 回ずつ焚いた後)から始める(CLAUDE.md §4-B)。
    [Fact]
    public void ConvertEols_FastPath_RaisesNoUiaEvents() =>
        Sta.Run(() =>
        {
            EditorControl.TestHook_ForceUiaListen = true;
            try
            {
                using var ctrl = new EditorControl();
                ctrl.SetSource(TextBuffer.FromString("a\nb\nc"));
                using var form = new Form();
                form.Controls.Add(ctrl);
                form.Show();
                try
                {
                    var msg = Message.Create(
                        ctrl.Handle,
                        0x003D,
                        System.IntPtr.Zero,
                        new System.IntPtr(-25)
                    );
                    EditorControl.TestHook_WndProc(ctrl, ref msg);
                    EditorControl.TestHook_ResetUiaEventCounts(ctrl);

                    ctrl.ConvertEols(LineEnding.Crlf); // 非 fast-path=1 回ずつ焚く
                    Application.DoEvents();
                    Assert.Equal((1, 1), Counts(ctrl));

                    ctrl.ConvertEols(LineEnding.Crlf); // 2 回目は fast-path
                    Application.DoEvents();
                    Assert.Equal((1, 1), Counts(ctrl));
                }
                finally
                {
                    form.Close();
                }
            }
            finally
            {
                EditorControl.TestHook_ForceUiaListen = false;
            }
        });

    private static (int TextChanged, int SelChanged) Counts(EditorControl ctrl)
    {
        var (textChanged, selChanged, _) = EditorControl.TestHook_UiaEventCounts(ctrl);
        return (textChanged, selChanged);
    }
}
