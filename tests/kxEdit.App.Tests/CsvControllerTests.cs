using System.Reflection;
using kxEdit.App.Tests.Fakes;
using kxEdit.Core.Csv;
using kxEdit.Core.Text;
using kxEdit.Editor;
// System.Windows.Forms.SelectionRange(DateTime の範囲型)と名前が衝突するので別名で入れる。
using CellHighlightRange = kxEdit.Core.Layout.SelectionRange;

namespace kxEdit.App.Tests;

/// <summary>
/// Phase 2 Stage 6: CsvController の配線・状態機械・端メッセージ・GoToCell の 3 分岐・
/// BeginEdit の起動配線・parse-error 後始末・DocumentState 書き戻しのテスト。
/// 実 DocumentManager+実 EditorControl を STA 上で使い、Form 境界(FakeCellPicker)と
/// 通知(FakeAnnouncer)だけを偽物にする。CsvDocument の照合正しさ(Core 検証済み)は
/// 再検証しない(責務=配線・遷移・SR 誤読み抑止フラグ・通知文言・DocumentState 書き戻し)。
/// </summary>
public class CsvControllerTests
{
    /// <summary>CsvController を Fake 境界で配線したテストホスト(共通 HostForm.CreateWithDocs を使う)。</summary>
    private sealed class Host : IDisposable
    {
        public Form Form { get; }
        public DocumentManager Docs { get; }
        public FakeAnnouncer Announcer { get; } = new();
        public FakeCellPicker Picker { get; } = new();
        public CsvController Csv { get; }

        public Host()
        {
            var (form, docs) = HostForm.CreateWithDocs();
            Form = form;
            Docs = docs;
            Csv = new CsvController(docs: Docs, announcer: Announcer, cellPicker: Picker);
        }

        /// <summary>本文に CSV テキストを載せて Active に返す(EditorControl.Text は新バッファ=Modified=false)。</summary>
        public Document NewCsvDoc(string csv)
        {
            var doc = Docs.CreateNew();
            doc.Editor.Text = csv;
            return doc;
        }

        public void Dispose()
        {
            Csv.AbortEdit(); // 進行中の F2 編集を落とす(冪等)
            Form.Dispose();
        }
    }

    // 3×3 の素朴 CSV。行 = 頭文字(a=1行目・b=2行目・c=3行目)、列 = 末尾数字(1=1列目・2=2列目・3=3列目)。
    // 例: "b2" は 2 行 2 列。改行は LF 固定。
    private const string Grid3x3 = "a1,a2,a3\n" + "b1,b2,b3\n" + "c1,c2,c3";

    // 5×5 の CSV。値 = "r{行0始まり}c{列0始まり}" で全セルユニーク(行/列の取り違えを文言で検出)。
    // 開始位置 (2,2) は全方向に 2 以上の余地があり、隣接移動と端ジャンプの到達先が必ず異なる。
    private const string Grid5x5 =
        "r0c0,r0c1,r0c2,r0c3,r0c4\n"
        + "r1c0,r1c1,r1c2,r1c3,r1c4\n"
        + "r2c0,r2c1,r2c2,r2c3,r2c4\n"
        + "r3c0,r3c1,r3c2,r3c3,r3c4\n"
        + "r4c0,r4c1,r4c2,r4c3,r4c4";

    /// <summary>CSV モードへ入り、開始セルを (row,col) に直接設定する(非既定位置からの検証開始標準)。
    /// DocumentState が真実源(TryContext が読む)なので直接設定で十分。通知履歴はクリアして返す。</summary>
    private static Document EnterAt(Host host, string csv, int row, int col)
    {
        var doc = host.NewCsvDoc(csv);
        Assert.True(host.Csv.TryEnterMode(doc));
        doc.State.CsvRow = row;
        doc.State.CsvCol = col;
        host.Announcer.Said.Clear();
        return doc;
    }

    /// <summary>Grid5x5 の (2,2) から開始(端ジャンプ/ByKey 共通 fixture)。</summary>
    private static Document EnterAt22(Host host) => EnterAt(host, Grid5x5, 2, 2);

    /// <summary>Grid5x5 上で (row0,col0) に居ることを State と Cell 文言の両面で assert する。
    /// 文言はセル値がユニーク("r{row}c{col}")なので行/列の取り違えも検出する。</summary>
    private static void AssertAt(Host host, Document doc, int row0, int col0)
    {
        Assert.Equal(row0, doc.State.CsvRow);
        Assert.Equal(col0, doc.State.CsvCol);
        Assert.Equal(
            CsvAnnounceFormatter.Cell($"r{row0}c{col0}", row0 + 1, col0 + 1),
            host.Announcer.Said[^1]
        );
    }

    // ===== ctor(対応固定=Picker は ctor で呼ばれない) =====

    [Fact]
    public void Ctor_DoesNotInvokePicker_NorAnnouncer() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            Assert.Equal(0, host.Picker.PickCount);
            Assert.Empty(host.Announcer.Said);
        });

    // ===== TryEnterMode(5 分岐) =====

    [Fact]
    public void TryEnterMode_AlreadyInMode_ReturnsFalse_NoChange() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);
            Assert.True(host.Csv.TryEnterMode(doc));
            int saidBefore = host.Announcer.Said.Count;

            Assert.False(host.Csv.TryEnterMode(doc)); // 2 回目は false・追加通知なし
            Assert.Equal(saidBefore, host.Announcer.Said.Count);
        });

    [Fact]
    public void TryEnterMode_UnparseableCsv_AnnouncesParseError_DoesNotEnter() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            // 引用符未終端 → CsvDocument.Ok=false
            var doc = host.NewCsvDoc("a1,\"b1\na2,b2");

            Assert.False(host.Csv.TryEnterMode(doc));
            Assert.False(doc.State.CsvMode);
            Assert.False(doc.Editor.ReadOnly);
            Assert.Contains(CsvAnnounceFormatter.ParseError, host.Announcer.Said);
        });

    [Fact]
    public void TryEnterMode_EmptyCsv_EntersMode_AnnouncesModeOnOnly() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(""); // Rows.Count=0

            Assert.True(host.Csv.TryEnterMode(doc));
            Assert.True(doc.State.CsvMode);
            Assert.True(doc.Editor.ReadOnly);
            Assert.False(doc.Editor.RaiseUiaSelectionEvents); // SR 誤読み抑止
            // データ無しは ModeOn のみ(セル情報なし)
            Assert.Single(host.Announcer.Said);
            Assert.Equal(CsvAnnounceFormatter.ModeOn, host.Announcer.Said[0]);
        });

    [Fact]
    public void TryEnterMode_ParseableCsv_EntersMode_ReadOnlyAndUiaOff_AnnouncesModeOnAndCell() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);

            Assert.True(host.Csv.TryEnterMode(doc));
            Assert.True(doc.State.CsvMode);
            Assert.True(doc.Editor.ReadOnly);
            Assert.False(doc.Editor.RaiseUiaSelectionEvents);
            // ModeOn + Cell が結合された 1 通知(現行実装=1 回 Say)。初期セルは caret=0→(0,0)="a1"。
            Assert.Contains(
                $"{CsvAnnounceFormatter.ModeOn} {CsvAnnounceFormatter.Cell("a1", 1, 1)}",
                host.Announcer.Said
            );
        });

    [Fact]
    public void TryEnterMode_InitialCell_IsDerivedFromCaretPosition() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);
            // "a1,a2,a3\nb1,b2,b3\n..." の 2 行目 "b2" 相当の位置(0 始まり=9+3=12 前後)にキャレットを寄せる。
            // 正確なオフセットは EditorControl の EOL 処理に依存するため、CsvDocument.FindCell に任せて
            // "b" が含まれる位置(text.IndexOf("b2"))へキャレットを置く。
            int caret = doc.Editor.SnapshotText.IndexOf("b2", StringComparison.Ordinal);
            doc.Editor.MoveCaretCharOffset(caret);

            Assert.True(host.Csv.TryEnterMode(doc));
            Assert.Equal(1, doc.State.CsvRow); // 0 始まり=2 行目
            Assert.Equal(1, doc.State.CsvCol); // 0 始まり=2 列目
        });

    // ===== ExitMode(ToggleMode 経由・外部 API は ToggleMode のみ) =====

    [Fact]
    public void ToggleMode_FromOn_ExitsMode_RestoresReadWriteAndUia_AnnouncesModeOff() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);
            host.Csv.TryEnterMode(doc);
            host.Announcer.Said.Clear();

            host.Csv.ToggleMode();

            Assert.False(doc.State.CsvMode);
            Assert.False(doc.Editor.ReadOnly);
            Assert.True(doc.Editor.RaiseUiaSelectionEvents); // 通常編集の SR 挙動に戻す
            Assert.Equal(CsvAnnounceFormatter.ModeOff, host.Announcer.Said[^1]);
        });

    [Fact]
    public void ToggleMode_FromOn_MovesCaretToLastCellStart() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);
            host.Csv.TryEnterMode(doc);
            host.Csv.Move(Direction.Right); // (0,0)→(0,1)="a2"
            int expected = doc.Editor.SnapshotText.IndexOf("a2", StringComparison.Ordinal);

            host.Csv.ToggleMode();

            Assert.Equal(expected, doc.Editor.CaretCharOffset);
        });

    [Fact]
    public void ToggleMode_NoActiveDoc_IsNoOp() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            // Docs.CreateNew を呼ばない(Active=null)
            host.Csv.ToggleMode();

            Assert.Empty(host.Announcer.Said); // 通知も発火しない
        });

    // ===== ToggleMode(進入方向) =====

    [Fact]
    public void ToggleMode_FromOff_EntersMode() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);

            host.Csv.ToggleMode();

            Assert.True(doc.State.CsvMode);
        });

    // ===== Move(移動+読み上げ・端メッセージ) =====

    [Fact]
    public void Move_ToAdjacentCell_UpdatesStateAndAnnouncesCell() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);
            host.Csv.TryEnterMode(doc);
            host.Announcer.Said.Clear();

            host.Csv.Move(Direction.Right); // (0,0)→(0,1)

            Assert.Equal(0, doc.State.CsvRow);
            Assert.Equal(1, doc.State.CsvCol);
            Assert.Equal(CsvAnnounceFormatter.Cell("a2", 1, 2), host.Announcer.Said[^1]);
        });

    [Fact]
    public void Move_AtLeftEdge_AnnouncesLeftEdge_NoChange() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);
            host.Csv.TryEnterMode(doc); // (0,0) から開始
            host.Announcer.Said.Clear();

            host.Csv.Move(Direction.Left); // 左端

            Assert.Equal(0, doc.State.CsvCol);
            Assert.Equal(CsvAnnounceFormatter.LeftEdge, host.Announcer.Said[^1]);
        });

    // kill 対象: EdgeMessage の Right→LeftEdge 化(変異 C)・右端判定(列数-1)の破壊。
    [Fact]
    public void Move_AtRightEdge_AnnouncesRightEdge_NoChange() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = EnterAt(host, Grid5x5, 2, 4); // 右端(2,4)

            host.Csv.Move(Direction.Right);

            Assert.Equal(2, doc.State.CsvRow);
            Assert.Equal(4, doc.State.CsvCol); // 動かない
            Assert.Equal(CsvAnnounceFormatter.RightEdge, host.Announcer.Said[^1]);
        });

    // kill 対象: EdgeMessage の Up 分岐削除(default=BottomEdge へ落ちる)・先頭行判定の破壊。
    [Fact]
    public void Move_AtTopEdge_AnnouncesTopEdge_NoChange() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = EnterAt(host, Grid5x5, 0, 2); // 先頭行(0,2)

            host.Csv.Move(Direction.Up);

            Assert.Equal(0, doc.State.CsvRow); // 動かない
            Assert.Equal(2, doc.State.CsvCol);
            Assert.Equal(CsvAnnounceFormatter.TopEdge, host.Announcer.Said[^1]);
        });

    // kill 対象: EdgeMessage の default(BottomEdge)の他文言化・最終行判定の破壊。
    [Fact]
    public void Move_AtBottomEdge_AnnouncesBottomEdge_NoChange() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = EnterAt(host, Grid5x5, 4, 2); // 最終行(4,2)

            host.Csv.Move(Direction.Down);

            Assert.Equal(4, doc.State.CsvRow); // 動かない
            Assert.Equal(2, doc.State.CsvCol);
            Assert.Equal(CsvAnnounceFormatter.BottomEdge, host.Announcer.Said[^1]);
        });

    [Fact]
    public void Move_NotInMode_IsNoOp() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewCsvDoc(Grid3x3); // モードには入らない

            host.Csv.Move(Direction.Right);

            Assert.Empty(host.Announcer.Said);
        });

    // ===== 端ジャンプ(6 API から代表 2 件・残りは第 2 弾で被覆) =====

    [Fact]
    public void MoveTopLeft_MovesTo_0_0() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);
            host.Csv.TryEnterMode(doc);
            host.Csv.Move(Direction.Right);
            host.Csv.Move(Direction.Down); // (1,1) へ
            host.Announcer.Said.Clear();

            host.Csv.MoveTopLeft();

            Assert.Equal(0, doc.State.CsvRow);
            Assert.Equal(0, doc.State.CsvCol);
            Assert.Equal(CsvAnnounceFormatter.Cell("a1", 1, 1), host.Announcer.Said[^1]);
        });

    // ===== 端ジャンプ(残り) =====

    [Fact]
    public void MoveBottomRight_MovesToLastCell() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);
            host.Csv.TryEnterMode(doc);
            host.Announcer.Said.Clear();

            host.Csv.MoveBottomRight();

            Assert.Equal(2, doc.State.CsvRow);
            Assert.Equal(2, doc.State.CsvCol);
            Assert.Equal(CsvAnnounceFormatter.Cell("c3", 3, 3), host.Announcer.Said[^1]);
        });

    // ===== 端ジャンプ(第 2 弾=残り 4 API・(2,2) 起点で隣接移動との取り違えも kill) =====

    // kill 対象: RowStart→RowEnd の取り違え(変異 A)・Left 隣接移動との混同((2,1) なら赤)。
    [Fact]
    public void MoveRowStart_From22_MovesToRowHead() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = EnterAt22(host);

            host.Csv.MoveRowStart();

            AssertAt(host, doc, 2, 0);
        });

    // kill 対象: RowEnd→RowStart の取り違え・Right 隣接移動との混同((2,3) なら赤)。
    [Fact]
    public void MoveRowEnd_From22_MovesToRowTail() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = EnterAt22(host);

            host.Csv.MoveRowEnd();

            AssertAt(host, doc, 2, 4);
        });

    // kill 対象: ColumnTop→ColumnBottom/RowStart の取り違え・Up 隣接移動との混同((1,2) なら赤)。
    [Fact]
    public void MoveColumnTop_From22_MovesToColumnHead() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = EnterAt22(host);

            host.Csv.MoveColumnTop();

            AssertAt(host, doc, 0, 2);
        });

    // kill 対象: ColumnBottom→ColumnTop/RowEnd の取り違え・Down 隣接移動との混同((3,2) なら赤)。
    [Fact]
    public void MoveColumnBottom_From22_MovesToColumnTail() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = EnterAt22(host);

            host.Csv.MoveColumnBottom();

            AssertAt(host, doc, 4, 2);
        });

    // ===== GoToCell(3 分岐+対応固定) =====

    [Fact]
    public void GoToCell_PickerCanceled_NoAnnounce_NoChange() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);
            host.Csv.TryEnterMode(doc);
            host.Csv.Move(Direction.Right);
            host.Csv.Move(Direction.Down); // 既定 (0,0) から (1,1) へ
            host.Announcer.Said.Clear();
            host.Picker.NextResult = CellPickResult.Canceled;

            host.Csv.GoToCell();

            Assert.Equal(1, host.Picker.PickCount);
            Assert.Empty(host.Announcer.Said); // Cancel は無音
            Assert.Equal(1, doc.State.CsvRow); // 変化なし=(1,1) のまま
            Assert.Equal(1, doc.State.CsvCol);
        });

    [Fact]
    public void GoToCell_InvalidFormat_AnnouncesBadFormat_NoChange() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);
            host.Csv.TryEnterMode(doc);
            host.Csv.Move(Direction.Right);
            host.Csv.Move(Direction.Down); // 既定 (0,0) から (1,1) へ
            host.Announcer.Said.Clear();
            host.Picker.NextResult = CellPickResult.InvalidFormat;

            host.Csv.GoToCell();

            Assert.Equal(CsvAnnounceFormatter.BadCellFormat, host.Announcer.Said[^1]);
            Assert.Equal(1, doc.State.CsvRow); // 変化なし=(1,1) のまま
            Assert.Equal(1, doc.State.CsvCol);
        });

    [Fact]
    public void GoToCell_OutOfRange_AnnouncesOutOfRange_NoChange() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);
            host.Csv.TryEnterMode(doc);
            host.Csv.Move(Direction.Right);
            host.Csv.Move(Direction.Down); // 既定 (0,0) から (1,1) へ
            host.Announcer.Said.Clear();
            host.Picker.NextResult = CellPickResult.Ok(99, 99); // 3×3 の外

            host.Csv.GoToCell();

            Assert.Equal(CsvAnnounceFormatter.OutOfRange, host.Announcer.Said[^1]);
            Assert.Equal(1, doc.State.CsvRow); // 変化なし=(1,1) のまま
            Assert.Equal(1, doc.State.CsvCol);
        });

    [Fact]
    public void GoToCell_Ok_MovesToTarget_AnnouncesCell() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);
            host.Csv.TryEnterMode(doc);
            host.Announcer.Said.Clear();
            host.Picker.NextResult = CellPickResult.Ok(3, 2); // 1 始まり=(2,1) 0 始まり="c2"

            host.Csv.GoToCell();

            Assert.Equal(2, doc.State.CsvRow);
            Assert.Equal(1, doc.State.CsvCol);
            Assert.Equal(CsvAnnounceFormatter.Cell("c2", 3, 2), host.Announcer.Said[^1]);
        });

    [Fact]
    public void GoToCell_PassesCurrentCellToPicker_As1Based() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);
            host.Csv.TryEnterMode(doc);
            // 非対称位置(1,2)= 2 行 3 列。Pick 呼び出しで row と col の取り違えを検出可能にする。
            host.Csv.Move(Direction.Down);
            host.Csv.Move(Direction.Right);
            host.Csv.Move(Direction.Right);
            host.Picker.NextResult = CellPickResult.Canceled;

            host.Csv.GoToCell();

            Assert.Equal(2, host.Picker.LastCurrentRow1); // 2 行(1 始まり)
            Assert.Equal(3, host.Picker.LastCurrentCol1); // 3 列(1 始まり)
        });

    // ===== 読み上げ(移動なし) =====

    [Fact]
    public void ReadCurrent_AnnouncesCurrentCell_NoStateChange() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);
            host.Csv.TryEnterMode(doc);
            host.Csv.Move(Direction.Right); // (0,1)
            host.Announcer.Said.Clear();

            host.Csv.ReadCurrent();

            Assert.Equal(CsvAnnounceFormatter.Cell("a2", 1, 2), host.Announcer.Said[^1]);
            Assert.Equal(0, doc.State.CsvRow); // 位置は動かない
            Assert.Equal(1, doc.State.CsvCol);
        });

    [Fact]
    public void ReadColumnTopAndRowHead_AnnounceHeaders() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);
            host.Csv.TryEnterMode(doc);
            host.Csv.Move(Direction.Right);
            host.Csv.Move(Direction.Down); // (1,1)
            host.Announcer.Said.Clear();

            host.Csv.ReadColumnTop();
            Assert.Equal(CsvAnnounceFormatter.Header("a2"), host.Announcer.Said[^1]);

            host.Csv.ReadRowHead();
            Assert.Equal(CsvAnnounceFormatter.Header("b1"), host.Announcer.Said[^1]);
        });

    // ===== BeginEdit/AbortEdit(オーバーレイの起動配線のみ検証・Enter/Esc の E2E は L5 領分) =====

    [Fact]
    public void BeginEdit_NotInMode_IsNoOp() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            host.NewCsvDoc(Grid3x3); // モードに入らない

            host.Csv.BeginEdit();

            Assert.False(host.Csv.IsEditing);
        });

    [Fact]
    public void BeginEdit_InMode_StartsOverlay_IsEditingTrue() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);
            host.Csv.TryEnterMode(doc);

            host.Csv.BeginEdit();

            Assert.True(host.Csv.IsEditing);
            host.Csv.AbortEdit(); // 後始末(HostForm 破棄前に必ず落とす)
        });

    [Fact]
    public void AbortEdit_WhenEditing_ExitsEditing_AndIsIdempotent() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);
            host.Csv.TryEnterMode(doc);
            host.Csv.BeginEdit();
            Assert.True(host.Csv.IsEditing);

            host.Csv.AbortEdit();
            Assert.False(host.Csv.IsEditing);

            host.Csv.AbortEdit(); // 2 回目=冪等(例外を出さない)
            Assert.False(host.Csv.IsEditing);
        });

    // ===== BeginEdit の Commit/Cancel 経路(Task 7・F2 UX 保護=L3 で厚めに固定) =====
    // CsvCellEditor は internal(Task 7 で公開表面を削減)。テストは InternalsVisibleTo 経由で
    // 直接 Commit()/CancelEdit() を呼び、キー入力の実機化(SendKeys 等)を挟まずに
    // F2 経路の観測を決定的に固定する(Sta.Run はメッセージポンプを回さないため、
    // TextBox.KeyDown 経由の実キー配送は再現しない)。
    // refocusTarget の Focus() 副作用は非アクティブ HostForm 上で観測困難だが、
    // Teardown が最後まで走ったことは IsEditing=false + 本文の反映有無で十分検出できる
    // (Close→Teardown が途中で早退すると IsEditing/本文の少なくとも一方が期待と食い違う)。

    /// <summary>CsvController の内部 CsvCellEditor(private field _editor)へ到達する。
    /// F2 経路のフルワイヤ(BeginEdit→CsvCellEditor.Begin→Commit/CancelEdit→onCommit/onCancel)
    /// をテストで観測するため、Fake で置換せず実インスタンスを取り出す。</summary>
    private static CsvCellEditor GetCellEditor(CsvController controller)
    {
        var field = typeof(CsvController).GetField(
            "_editor",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        return (CsvCellEditor)field.GetValue(controller)!;
    }

    /// <summary>CsvCellEditor の内部 TextBox(private field _box)を取り出す。
    /// Begin 中はセル値で初期化された TextBox が入っており、Commit 前に Text を書き換えると
    /// 「編集後の確定値」が onCommit へ伝わる。IsEditing=false 時は null が返る想定なので、
    /// 呼び出し側は BeginEdit 直後にのみ使う。</summary>
    private static TextBox GetOverlayBox(CsvCellEditor editor)
    {
        var field = typeof(CsvCellEditor).GetField(
            "_box",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        return (TextBox)field.GetValue(editor)!;
    }

    /// <summary>F2 編集中に「別経路が本文を書き換えた」状況を作る。CSV モード中は
    /// ReadOnly=true で ReplaceCharRange が no-op になるため、production の onCommit と
    /// 同じ流儀で ReadOnly を一時的に落として書き、元へ戻す。</summary>
    private static void MutateBodyWhileEditing(EditorControl ed, Action<EditorControl> mutate)
    {
        bool wasRo = ed.ReadOnly;
        ed.ReadOnly = false;
        mutate(ed);
        ed.ReadOnly = wasRo;
    }

    /// <summary>セル強調(private フィールド <c>_cellHighlight</c>)を取り出す。
    /// <c>EditorControlConvertEolsTests.CellHighlight</c> と同じ流儀で、リネームで静かに
    /// 緑になる事故を防ぐためフィールド名つきで明示的に落とす。</summary>
    private static CellHighlightRange? CellHighlight(EditorControl ed)
    {
        var fi = typeof(EditorControl).GetField(
            "_cellHighlight",
            BindingFlags.NonPublic | BindingFlags.Instance
        );
        Assert.True(
            fi is not null,
            "EditorControl に private フィールド _cellHighlight が見つからない"
        );
        return (CellHighlightRange?)fi!.GetValue(ed);
    }

    // kill 対象: onCommit 内の ReplaceCharRange の削除/引数取り違え・serialized 未反映・
    // Commit の onCommit 呼び出し漏れ(Close だけして早退)・_box.Text ではなく初期値を渡す変異。
    [Fact]
    public void BeginEdit_ThenCommit_ReplacesCurrentCell_WithEditedText_AndEndsEditing() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);
            host.Csv.TryEnterMode(doc);
            // 初期位置 (0,0)="a1" を編集対象にする(セル境界 [0,2) を "NEW" で置換=長さ変化あり)。
            host.Csv.BeginEdit();
            Assert.True(host.Csv.IsEditing);

            var editor = GetCellEditor(host.Csv);
            var box = GetOverlayBox(editor);
            box.Text = "NEW"; // ユーザーが編集した状態を再現(セル内改行なし=EscapeField は素通し)
            editor.Commit(); // Enter 相当

            Assert.False(host.Csv.IsEditing);
            // (0,0)="a1"(len=2) → "NEW"(len=3) に置換され、以降のセルは相対位置がズレるだけ
            Assert.Equal("NEW,a2,a3\nb1,b2,b3\nc1,c2,c3", doc.Editor.SnapshotText);
        });

    // kill 対象: onCancel が本文へ触ってしまう変異(CancelEdit が誤って onCommit を呼ぶ)・
    // CancelEdit の early return 削除で TextBox.Text が本文に漏れる変異・二重解放。
    [Fact]
    public void BeginEdit_ThenCancel_LeavesCellContentUnchanged_AndEndsEditing() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);
            host.Csv.TryEnterMode(doc);
            host.Csv.BeginEdit();
            Assert.True(host.Csv.IsEditing);

            var editor = GetCellEditor(host.Csv);
            var box = GetOverlayBox(editor);
            box.Text = "SHOULD_NOT_APPLY"; // 変更を入力した後で Cancel=本文に混ざってはならない
            editor.CancelEdit(); // Esc 相当

            Assert.False(host.Csv.IsEditing);
            Assert.Equal(Grid3x3, doc.Editor.SnapshotText); // Cancel は本文へ一切書き込まない
        });

    // kill 対象(2 つ・いずれも実測):
    //  1. CsvCellEditor.Commit の EOL 正規化を落とす変異(`string text = _box.Text;`)。
    //     Alt+Enter はセル内改行として "\r\n" を TextBox へ挿入する(CsvCellEditor.cs:79)ため、
    //     正規化を落とすと EscapeField が CR ごと引用符で包み、本文に CR が残る。
    //  2. CsvWriter.NormalizeEols が改行の連続を 1 個へ畳む変異。fixture が単発改行だと
    //     畳み込みが観測できないので、Alt+Enter 2 回相当("\r\n\r\n")を入力にしてある。
    // 既存の Commit テストは値が "NEW"(CR なし)なので 1. も 2. も素通しする。
    // これは「規則を関数へ括り出しても、呼出側がその規則を使い続けることは関数側の網が
    // 守らない」ことの網。Core 側の NormalizeEols テストはここを 1 ビットも守らない。
    [Fact]
    public void BeginEdit_ThenCommit_NormalizesCrlfInCellValue_BeforeSerializing() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);
            host.Csv.TryEnterMode(doc);
            host.Csv.BeginEdit(); // 初期位置 (0,0)="a1" を編集対象にする
            Assert.True(host.Csv.IsEditing);

            var editor = GetCellEditor(host.Csv);
            var box = GetOverlayBox(editor);
            box.Text = "x\r\n\r\ny"; // Alt+Enter を 2 回=セル内に空行を作った状態を再現
            editor.Commit(); // Enter 相当

            Assert.False(host.Csv.IsEditing);
            // 確定値は LF へ正規化されてから EscapeField に渡る=本文に CR は現れず、
            // かつ連続改行は畳まれない(ユーザーが入れた空行が残る)。
            Assert.Equal("\"x\n\ny\",a2,a3\nb1,b2,b3\nc1,c2,c3", doc.Editor.SnapshotText);
        });

    // ===== M-25: F2 確定が「開始時の座標」を持ち越さないこと(2026-09-01 設計書) =====
    // 実運用の再現経路は「F2 編集中の Ctrl+S」。MainForm.ProcessCmdKey の CSV 素キー横取りは
    // !_csv.IsEditing で自分を無効化するため Ctrl+S はメニューショートカットへ素通りし、
    // FileController.SaveDocument が ConvertEols で本文を差し替える。ここではその 1 手
    // (ConvertEols)だけを直接呼んで、UI とファイル I/O を挟まずに同じ状態を作る。
    // ConvertEols は ReadOnly を見ない(EditorControl.cs の ConvertEols にガードが無い)ので、
    // CSV モード(ReadOnly=true)のままでも本文が差し替わる=これが到達経路の実体そのもの。

    // セル内 LF を持つ混在 EOL。編集対象 (1,0) は自分自身に LF を含むので、ConvertEols で
    // 「長さ」も「Value」も変わる。前後に無傷であるべき行(a1,a2 / c1,c2)を置き、全書き換えと区別する。
    // 「Value も変わる」ことには第 2 の役割がある: Task 3 の同一性検証は開始時と確定時の Value を
    // 比べるので、そこから EOL 正規化を外すと "x\ny" と "x\r\ny" が不一致になり確定が拒否される
    // = この fixture だけが「正規化を省いた同一性検証」を殺す(下の T1 の kill 対象 (e) 参照)。
    // MixedEolCsv を編集するときはこの役割を壊さないこと(セル内改行を落とすと網が黙って消える)。
    private const string MixedEolCsv = "a1,a2\r\n\"x\ny\",b2\r\nc1,c2";

    // 先行セルだけが LF を持つ混在 EOL。編集対象 (1,1)="b2" 自身は改行を含まないので、
    // 「オフセットが後ろへずれるだけ」のケースを T1 と分離できる。
    private const string ShiftOnlyCsv = "a1,\"p\nq\"\r\nb1,b2\r\nc1,c2";

    // kill 対象(実測。onCommit の書込先 2 引数を 1 つずつ陳腐化させた総当たり):
    //   (a) start/length を両方持ち越す(=修正前の実装そのもの) → 落ちるのは下の 2 本だけ
    //   (b) start は解決し直すが length を持ち越す             → 落ちるのは本テストだけ
    //   (c) length は解決し直すが start を持ち越す             → 落ちるのは次のテストだけ
    // (a) を注入した実測では App テスト 724 本中の失敗はこの 2 本のみ=既存の網は 1 本も
    // この欠陥を捕まえていなかった。(b)/(c) が別々のテストを落とすので 2 本は互いに冗長でない。
    // 本テストは (a)(b) を殺す。修正前の src での Actual は
    // "a1,a2\r\nNEW\",b2\r\nc1,c2"(閉じ引用符が残る)。
    //
    // Task 3 で追加の kill 対象が 1 つ増えた(実測):
    //   (e) 同一性検証から EOL 正規化を外す(startValue = f.Value / 素の target.Value と比較)
    //       → 落ちるのは **本テストだけ**(App 730 本中 1 失敗)。Actual は
    //          "a1,a2\r\n\"x\r\ny\",b2\r\nc1,c2" = 確定が拒否されてセルが編集前のまま残る。
    //       T2 は編集セル自身が改行を持たないので (e) の下で恒等になり、殺せない。
    [Fact]
    public void Commit_AfterEolConversion_WritesEditedCell_NotStaleOffsets() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = EnterAt(host, MixedEolCsv, 1, 0); // 非既定位置 (1,0) から開始
            host.Csv.BeginEdit();
            Assert.True(host.Csv.IsEditing);
            var editor = GetCellEditor(host.Csv);
            var box = GetOverlayBox(editor);
            box.Text = "NEW";

            // Ctrl+S 相当: 保存前の EOL 統一でバッファが差し替わる(セル内 LF → CRLF)。
            Assert.True(doc.Editor.ConvertEols(LineEnding.Crlf));

            editor.Commit();

            Assert.False(host.Csv.IsEditing);
            // (1,0) の "x\r\ny"(引用符込み 6 文字)だけが NEW になり、引用符も区切りも残らない。
            Assert.Equal("a1,a2\r\nNEW,b2\r\nc1,c2", doc.Editor.SnapshotText);
        });

    // kill 対象(実測): 上の表の (a)(c)。編集セル自身は改行を含まない=ConvertEols で長さが
    // 変わらないので (b) はこの fixture 上で恒等になり、本テストでは殺せない(だから 2 本要る)。
    // 修正前の src での Actual は "a1,\"p\r\nq\"\r\nb1NEW2\r\nc1,c2"(区切りカンマが食われる)。
    [Fact]
    public void Commit_AfterEolConversion_WritesShiftedCell_NotStaleOffsets() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = EnterAt(host, ShiftOnlyCsv, 1, 1); // 非既定位置 (1,1)="b2"
            host.Csv.BeginEdit();
            var editor = GetCellEditor(host.Csv);
            GetOverlayBox(editor).Text = "NEW";

            Assert.True(doc.Editor.ConvertEols(LineEnding.Crlf));

            editor.Commit();

            Assert.Equal("a1,\"p\r\nq\"\r\nb1,NEW\r\nc1,c2", doc.Editor.SnapshotText);
        });

    // ===== M-25: (row,col) が別セルを指していたら書かない(設計書 §4.2) =====
    // 以下 6 本が踏む枝は、現行の配線では実運用から到達できない。到達経路は設計書 §3 の表のとおり
    // Ctrl+S → ConvertEols の 1 本だけで、ConvertEols は CSV の行列構造を変えないため
    // 同一性検証は必ず一致する。ここはテストからだけ踏める「将来配線が増えたときの受け皿」で、
    // 網があること自体を安全宣言に使ってはならない。
    //
    // 各テストの kill 対象は、下の「変異 × テスト」実測表から書き写している(注入 → App 全件実行 →
    // revert を 1 変異ずつ実施。CLAUDE.md §4 / 設計書 §8.7 の手順)。✗ = そのテストが落ちる。
    //
    //   変異(すべて CsvController.BeginEdit の onCommit ガードに対して)
    //     (m1) csvNow.Ok 判定削除 …… var target = csvNow.GetField(row, col);
    //     (m2) 行数比較削除(startRowCount ごと)
    //     (m3) 列数比較削除(startColCount ごと)
    //     (m4) 値比較削除(startValue ごと)
    //     (m6) target is null 判定を条件から削除(残る参照を target! 化)
    //     (m7) 同一性検証を丸ごと削除(= Task 2 の状態 = target is null だけ)
    //     (m8) 拒否枝のセル強調復元(ApplyCell)を削除
    //     (m9) 発声を CsvAnnounceFormatter.ParseError に戻す
    //     (m10) target is null のときだけ ParseError を言う(Task 2 の暫定を残す)
    //     (m11) 強調復元の前置ガード `if (target is not null)` だけ削除
    //
    //   テスト                          | m1 | m2 | m3 | m4 | m6 | m7 | m8 | m9 | m10 | m11 |
    //   ...BecameAnotherCell            | –  | –  | –  | ✗  | –  | ✗  | –  | ✗  |  –  |  –  |
    //   ...Disappeared                  | –  | –  | –  | –  | –  | –  | –  | ✗  |  ✗  |  ✗  |
    //   ...RowCountChanged              | –  | ✗  | –  | –  | –  | ✗  | –  | ✗  |  –  |  –  |
    //   ...ColumnCountChanged           | –  | –  | ✗  | –  | –  | ✗  | –  | ✗  |  –  |  –  |
    //   ...BodyBecameUnparsable         | ✗  | –  | –  | –  | ✗  | –  | –  | ✗  |  ✗  |  –  |
    //   ...RestoresCellHighlight...     | –  | –  | –  | ✗  | –  | ✗  | ✗  | ✗  |  –  |  –  |
    //   App 全体の失敗数(730 本中)      | 1  | 1  | 1  | 2  | 1  | 4  | 1  | 6  |  2  |  1  |
    //
    // 注入できなかった変異(アナライザが先に殺す。-warnaserror 以前に error 相当):
    //   比較 1 本だけを消して startXxx を残す → S1481 / target is null を false や
    //   Rows.Count < 0 へ置換 → S1125・CS8602・RCS1215・S3981。

    // kill 対象(実測): (m4) 値比較削除 / (m7) 同一性検証の丸ごと削除 / (m9) 文言の据え置き。
    // (m4)(m7) は ...RestoresCellHighlight... も落とすので単独の網ではないが、
    // 「値だけが別物になった」最も素朴な形をここで固定する。
    [Fact]
    public void Commit_WhenCellAtRowColBecameAnotherCell_WritesNothing_AndAnnounces() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = EnterAt(host, Grid3x3, 1, 1); // (1,1)="b2" を編集開始
            host.Csv.BeginEdit();
            var editor = GetCellEditor(host.Csv);
            GetOverlayBox(editor).Text = "NEW";

            // (1,1) の中身だけを別物へ差し替える = 座標は生きているがセルは別物。
            MutateBodyWhileEditing(doc.Editor, ed => ed.ReplaceCharRange(12, 2, "ZZ"));
            string afterMutation = doc.Editor.SnapshotText;
            Assert.Equal("a1,a2,a3\nb1,ZZ,b3\nc1,c2,c3", afterMutation); // 前提の固定
            host.Announcer.Said.Clear();

            editor.Commit();

            Assert.False(host.Csv.IsEditing);
            Assert.Equal(afterMutation, doc.Editor.SnapshotText); // 1 文字も書いていない
            Assert.Equal(CsvAnnounceFormatter.CommitTargetChanged, host.Announcer.Said[^1]);
        });

    // kill 対象(実測): (m11) 強調復元の前置ガード削除 —— **これを殺すのは本テストだけ**。
    // ほかに (m9) 文言の据え置きと (m10) target is null だけ ParseError を言う変異を殺す。
    // (m6) null 判定削除は本テストでは殺せない: 行が消えると行数比較が先に true になるため
    // target.Value まで到達せず、NullReferenceException にならない(想定と実測が食い違った点)。
    [Fact]
    public void Commit_WhenCellAtRowColDisappeared_WritesNothing_AndAnnounces() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = EnterAt(host, Grid3x3, 2, 2); // 最終行 (2,2)="c3" を編集開始
            host.Csv.BeginEdit();
            var editor = GetCellEditor(host.Csv);
            GetOverlayBox(editor).Text = "NEW";

            // 3 行目ごと削る = GetField(2,2) が null になる。
            MutateBodyWhileEditing(doc.Editor, ed => ed.ReplaceCharRange(17, 9, ""));
            string afterMutation = doc.Editor.SnapshotText;
            Assert.Equal("a1,a2,a3\nb1,b2,b3", afterMutation); // 前提の固定
            host.Announcer.Said.Clear();

            editor.Commit();

            Assert.False(host.Csv.IsEditing);
            Assert.Equal(afterMutation, doc.Editor.SnapshotText);
            // Said[^1] ではなく全件で固定する。セルが消えている枝で強調復元を無条件に呼ぶと
            // ApplyCell が「移動できません」を先に喋る=余計な発声が 1 本増えるが、
            // 末尾だけ見る assert はそれを素通しする(実測: m11 が全緑で生存した)。
            Assert.Equal(new[] { CsvAnnounceFormatter.CommitTargetChanged }, host.Announcer.Said);
        });

    // ===== M-25: 形が変われば値が一致していても書かない =====
    // 下 2 本は「値の一致」だけの guard では素通りする。形(行数・その行の列数)の検査だけが殺す。

    // 行が消えて (row,col) が「同じ値の別セル」を指す。
    // kill 対象(実測): (m2) 行数比較削除 —— **これを殺すのは本テストだけ**。
    // ほかに (m7) 同一性検証の丸ごと削除と (m9) 文言の据え置きを殺す。
    [Fact]
    public void Commit_WhenRowCountChanged_AndValueCoincides_WritesNothing_AndAnnounces() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = EnterAt(host, "p,q\nX,Y\nX,Y", 1, 0); // (1,0)="X" を編集開始
            host.Csv.BeginEdit();
            var editor = GetCellEditor(host.Csv);
            GetOverlayBox(editor).Text = "NEW";

            // 先頭行 "p,q\n" を削る → (1,0) は 3 行目だった "X" を指す = 値は一致するが別セル。
            MutateBodyWhileEditing(doc.Editor, ed => ed.ReplaceCharRange(0, 4, ""));
            string afterMutation = doc.Editor.SnapshotText;
            Assert.Equal("X,Y\nX,Y", afterMutation);
            host.Announcer.Said.Clear();

            editor.Commit();

            Assert.Equal(afterMutation, doc.Editor.SnapshotText);
            Assert.Equal(CsvAnnounceFormatter.CommitTargetChanged, host.Announcer.Said[^1]);
        });

    // 列が増えて (row,col) が「同じ値の別セル」を指す。
    // kill 対象(実測): (m3) 列数比較削除 —— **これを殺すのは本テストだけ**。
    // ほかに (m7) 同一性検証の丸ごと削除と (m9) 文言の据え置きを殺す。
    [Fact]
    public void Commit_WhenColumnCountChanged_AndValueCoincides_WritesNothing_AndAnnounces() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = EnterAt(host, "p,q\nX,X", 1, 1); // (1,1)="X" を編集開始
            host.Csv.BeginEdit();
            var editor = GetCellEditor(host.Csv);
            GetOverlayBox(editor).Text = "NEW";

            // 2 行目の先頭へ列を 1 つ挿す → (1,1) は元 (1,0) だった "X" を指す。
            MutateBodyWhileEditing(doc.Editor, ed => ed.ReplaceCharRange(4, 0, "X,"));
            string afterMutation = doc.Editor.SnapshotText;
            Assert.Equal("p,q\nX,X,X", afterMutation);
            host.Announcer.Said.Clear();

            editor.Commit();

            Assert.Equal(afterMutation, doc.Editor.SnapshotText);
            Assert.Equal(CsvAnnounceFormatter.CommitTargetChanged, host.Announcer.Said[^1]);
        });

    // ===== M-25: パースが壊れていたら書かない(csvNow.Ok の網) =====
    // kill 対象(実測): (m1) csvNow.Ok 判定削除 と (m6) target is null 判定削除 ——
    // **どちらも殺すのは本テストだけ**。ほかに (m9) 文言の据え置きと (m10) を殺す。
    // 「値 + 形」の guard は本テストの fixture を 1 つも弾けない: 変異後の本文でも
    // Rows.Count=2 / Rows[0].Count=2 / GetField(0,0)="a1" が開始時と全部一致するので、
    // 上の 4 本は素通りする。csvNow.Ok だけが書込を止める。
    [Fact]
    public void Commit_WhenBodyBecameUnparsable_WritesNothing_AndAnnounces() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = EnterAt(host, "a1,a2\nb1,b2", 0, 0); // (0,0)="a1" を編集開始
            host.Csv.BeginEdit();
            var editor = GetCellEditor(host.Csv);
            GetOverlayBox(editor).Text = "NEW";

            // 末尾へ未終端引用符のレコードを足す。CsvParser は `if (inQuotes) ok = false;` で
            // Ok=false にしつつ、`if (ok && (pos > fieldStart || row.Count > 0))` が末尾の
            // 不完全レコードを rows へ混ぜないので、先行 2 行の行数・列数・値は 1 つも変わらない
            // (L1 Unterminated_trailing_record_sets_not_ok_but_leaves_preceding_rows_intact で実測)。
            MutateBodyWhileEditing(doc.Editor, ed => ed.ReplaceCharRange(11, 0, "\n\"x"));
            string afterMutation = doc.Editor.SnapshotText;
            Assert.Equal("a1,a2\nb1,b2\n\"x", afterMutation);
            host.Announcer.Said.Clear();

            editor.Commit();

            Assert.Equal(afterMutation, doc.Editor.SnapshotText);
            Assert.Equal(CsvAnnounceFormatter.CommitTargetChanged, host.Announcer.Said[^1]);
        });

    // ===== M-25 / M-1: 書かないと決めた枝でもセル強調は残す =====
    // 混在 EOL(1 行目末尾が LF・2 行目末尾が CRLF)。ConvertEols(Crlf) が fast-path へ落ちず
    // 本文を差し替える = EditorControl が _cellHighlight を捨てる状態を作れる唯一の fixture 形。
    private const string MixedEolGrid = "a1,a2\nb1,b2\r\nc1,c2";

    // kill 対象(実測): (m8) 拒否枝のセル強調復元(ApplyCell)の削除 —— **これを殺すのは本テストだけ**。
    // ほかに (m4) 値比較削除・(m7) 同一性検証の丸ごと削除・(m9) 文言の据え置きを殺す。
    [Fact]
    public void Commit_WhenRejected_RestoresCellHighlightClearedByEolConversion() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = EnterAt(host, MixedEolGrid, 0, 0);
            host.Csv.Move(Direction.Down); // (1,0)="b1" へ移動 = ApplyCell がセル強調を張る
            Assert.Equal(new CellHighlightRange(6, 8), CellHighlight(doc.Editor)); // "b1" = [6,8)
            host.Csv.BeginEdit();
            var editor = GetCellEditor(host.Csv);
            GetOverlayBox(editor).Text = "NEW";

            // 1 手目: Ctrl+S 相当。本文が差し替わり、EditorControl がセル強調を捨てる
            // (EditorControl.ConvertEols の `_cellHighlight = null;`)。
            Assert.True(doc.Editor.ConvertEols(LineEnding.Crlf));
            Assert.Null(CellHighlight(doc.Editor));

            // 2 手目: (1,0) の値だけを別物へ変える。ConvertEols だけでは行列構造も値も変わらず
            // 拒否枝に入らないので、「強調が消えている」と「拒否される」を 2 手で同時に作る。
            MutateBodyWhileEditing(doc.Editor, ed => ed.ReplaceCharRange(7, 2, "ZZ"));
            string afterMutation = doc.Editor.SnapshotText;
            Assert.Equal("a1,a2\r\nZZ,b2\r\nc1,c2", afterMutation);
            host.Announcer.Said.Clear();

            editor.Commit();

            Assert.Equal(afterMutation, doc.Editor.SnapshotText);
            Assert.Equal(CsvAnnounceFormatter.CommitTargetChanged, host.Announcer.Said[^1]);
            // 確定は捨てたが、セルは現存している。晴眼・弱視ユーザーが現在セルを見失わないよう
            // 強調は現在の (1,0)=[7,9) へ戻す(CLAUDE.md §2)。
            Assert.Equal(new CellHighlightRange(7, 9), CellHighlight(doc.Editor));
        });

    // ===== GoToCell の列側境界(Task 8・行側は ReadCurrent 経由で ClampRow 側を固定済) =====
    // GoToCell は picker が返した Ok(row1,col1) を csv.GoTo(row1-1, col1-1) に投げ、
    // 範囲外なら OutOfRange 通知(=クランプではない)。ここで列側の巨大値/負値を pin する。

    // kill 対象: csv.GoTo 内の col 上限判定(`col < Rows[row].Count`)削除で ApplyCell に落ち、
    // CannotMove に化ける変異(OutOfRange と別文言なので検出可能)。
    [Fact]
    public void GoToCell_ColumnBeyondMax_AnnouncesOutOfRange_NoChange() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);
            host.Csv.TryEnterMode(doc);
            host.Csv.Move(Direction.Right);
            host.Csv.Move(Direction.Down); // (0,0)→(1,1)
            host.Announcer.Said.Clear();
            host.Picker.NextResult = CellPickResult.Ok(1, 9999); // 行は範囲内・列だけ巨大

            host.Csv.GoToCell();

            Assert.Equal(CsvAnnounceFormatter.OutOfRange, host.Announcer.Said[^1]);
            Assert.Equal(1, doc.State.CsvRow); // 位置変化なし=(1,1) のまま
            Assert.Equal(1, doc.State.CsvCol);
        });

    // kill 対象: csv.GoTo 内の col 下限判定(`col >= 0`)削除で ApplyCell に落ち CannotMove 化する変異・
    // 列を無条件で 0 にクランプする「(不正な)防御コード追加」も OutOfRange と食い違うので落ちる。
    [Fact]
    public void GoToCell_NegativeColumn_AnnouncesOutOfRange_NoChange() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);
            host.Csv.TryEnterMode(doc);
            host.Csv.Move(Direction.Right);
            host.Csv.Move(Direction.Down); // (0,0)→(1,1)
            host.Announcer.Said.Clear();
            host.Picker.NextResult = CellPickResult.Ok(1, -1); // col1=-1 → 内部 col=-2

            host.Csv.GoToCell();

            Assert.Equal(CsvAnnounceFormatter.OutOfRange, host.Announcer.Said[^1]);
            Assert.Equal(1, doc.State.CsvRow); // 位置変化なし
            Assert.Equal(1, doc.State.CsvCol);
        });

    // ===== GoToCell の default: throw(Task 9・switch 完全被覆) =====
    // 現行 3 相(Canceled/InvalidFormat/Ok)以外の Kind を返す不正な ICellPicker を注入し、
    // switch の default 分岐(=想定外 Kind への防御的 throw)まで踏む。

    /// <summary>未定義の <see cref="CellPickKind"/> 値を返す不正 ICellPicker(default: 分岐の踏み台)。
    /// enum のキャストで defined 外の値を返すため、record ctor の非バリデート性に依存する
    /// (<see cref="CellPickResult.Ok"/> ファクトリを避けて record ctor 直呼び)。</summary>
    private sealed class UnknownKindPicker : ICellPicker
    {
        public CellPickResult Pick(IWin32Window owner, int currentRow1, int currentCol1) =>
            new CellPickResult((CellPickKind)99, 0, 0);
    }

    // kill 対象: default: の throw を return / break に化かす変異(=無音で戻る=switch カバレッジ穴)。
    // 実装が InvalidOperationException を投げることも同時に固定(実装:CsvController.cs の default 節)。
    [Fact]
    public void GoToCell_UnknownResultKind_Throws() =>
        Sta.Run(() =>
        {
            var (form, docs) = HostForm.CreateWithDocs();
            using var _ = form;
            var announcer = new FakeAnnouncer();
            var csv = new CsvController(
                docs: docs,
                announcer: announcer,
                cellPicker: new UnknownKindPicker()
            );
            var doc = docs.CreateNew();
            doc.Editor.Text = Grid3x3;
            Assert.True(csv.TryEnterMode(doc));

            Assert.Throws<InvalidOperationException>(() => csv.GoToCell());
        });

    // ===== クランプ(本文編集で行/列が減った後の補正) =====

    [Fact]
    public void Move_AfterContentReducedRows_ClampsToLastRow() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);
            host.Csv.TryEnterMode(doc);
            host.Csv.MoveBottomRight(); // (2,2)
            // 本文を 1 行だけに置換(モード中でも Text setter は無条件で通る=クランプ機構のテスト)
            doc.Editor.ReadOnly = false;
            doc.Editor.Text = "x1,x2,x3";
            doc.Editor.ReadOnly = true;
            host.Announcer.Said.Clear();

            host.Csv.ReadCurrent(); // (2,2) → クランプ → (0,2)

            Assert.Equal(0, doc.State.CsvRow);
            Assert.Equal(2, doc.State.CsvCol);
            Assert.Equal(CsvAnnounceFormatter.Cell("x3", 1, 3), host.Announcer.Said[^1]);
        });

    // ragged CSV(1 行目 3 列・2 行目 1 列)。クランプは「その行の幅」基準であることの検証にも使う。
    private const string Ragged = "a,b,c\n" + "d";

    // kill 対象: ClampCol の上限クランプ除去(変異 D=範囲外のまま→CannotMove/例外)・書き戻し(State 更新)の削除。
    [Fact]
    public void ReadCurrent_ColBeyondRowWidth_ClampsToLastCol_AndWritesBack() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = EnterAt(host, Ragged, row: 0, col: 99); // 幅 3 の行で列 99

            host.Csv.ReadCurrent();

            Assert.Equal(CsvAnnounceFormatter.Cell("c", 1, 3), host.Announcer.Said[^1]);
            Assert.Equal(2, doc.State.CsvCol); // クランプ結果の書き戻し
            Assert.Equal(0, doc.State.CsvRow);
        });

    // kill 対象: ClampCol の下限(c<0→0)クランプ除去(負のまま→CannotMove/例外)・書き戻しの削除。
    [Fact]
    public void ReadCurrent_NegativeCol_ClampsToZero_AndWritesBack() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = EnterAt(host, Ragged, row: 0, col: -1);

            host.Csv.ReadCurrent();

            Assert.Equal(CsvAnnounceFormatter.Cell("a", 1, 1), host.Announcer.Said[^1]);
            Assert.Equal(0, doc.State.CsvCol); // クランプ結果の書き戻し
        });

    // kill 対象: ClampRow の下限(r<0→0)クランプ除去(負のまま→CannotMove/例外)・書き戻しの削除。
    // 列は 1 に置き、行クランプ後も列が保存されること(原点フォールバックとの混同)も検出する。
    [Fact]
    public void ReadCurrent_NegativeRow_ClampsToRowZero_AndWritesBack() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = EnterAt(host, Ragged, row: -5, col: 1);

            host.Csv.ReadCurrent();

            Assert.Equal(CsvAnnounceFormatter.Cell("b", 1, 2), host.Announcer.Said[^1]);
            Assert.Equal(0, doc.State.CsvRow); // クランプ結果の書き戻し
            Assert.Equal(1, doc.State.CsvCol);
        });

    // ===== parse-error 後始末(モード中に本文が引用符未終端になったケース) =====

    [Fact]
    public void AnyCommand_AfterContentBecomesUnparseable_AnnouncesParseError() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewCsvDoc(Grid3x3);
            host.Csv.TryEnterMode(doc);
            // モード中に本文を書き換えて Ok=false 化(引用符未終端)
            doc.Editor.ReadOnly = false;
            doc.Editor.Text = "a1,\"broken\nx,y";
            doc.Editor.ReadOnly = true;
            doc.ClearCsvCache(); // Snapshot の再パースを強制
            host.Announcer.Said.Clear();

            host.Csv.Move(Direction.Right); // TryContext が ParseError を通知

            Assert.Contains(CsvAnnounceFormatter.ParseError, host.Announcer.Said);
        });

    // ===== CsvCommands.ByKey(素キー表=SR ユーザーの主要動線。キー→コマンドの対応固定) =====

    // kill 対象: 表エントリの追加/削除の黙殺(Theory 側は ByKey.Keys 列挙+default throw で自動追随)。
    // 17 = 隣接 4+読み上げ 3(Tab/C/R)+端ジャンプ 6+G/F2+別名 2(Shift+Tab/Ctrl+G)。
    [Fact]
    public void ByKey_HasExactly17Entries() => Assert.Equal(17, CsvCommands.ByKey.Count);

    /// <summary>ByKey の全キーを列挙する(表にエントリが増えると Theory の default 分岐が落ちる=網羅の機械保証)。</summary>
    public static TheoryData<Keys> ByKeyAllKeys()
    {
        var data = new TheoryData<Keys>();
        foreach (var key in CsvCommands.ByKey.Keys)
            data.Add(key);
        return data;
    }

    // kill 対象: キー→delegate の取り違え全般(変異 B=Home↔End 入替など)。
    // 全 17 エントリを (2,2) 起点の独立セットアップで invoke し、キーごとの期待効果
    // (到達セル/現在セル読み/見出し読み/Picker 移動/F2 編集開始)を assert する。
    // 隣接(Up/Down/Left/Right)と端ジャンプ(Home/End/PageUp/PageDown)は到達先が必ず異なる。
    [Theory]
    [MemberData(nameof(ByKeyAllKeys))]
    public void ByKey_MapsAllEntriesToExpectedCommands(Keys key) =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = EnterAt22(host);
            if (key is Keys.G or (Keys.Control | Keys.G))
                host.Picker.NextResult = CellPickResult.Ok(4, 4); // 1 始まり (4,4)=0 始まり (3,3)="r3c3"

            CsvCommands.ByKey[key](host.Csv);

            switch (key)
            {
                // 隣接セルへの移動
                case Keys.Up:
                    AssertAt(host, doc, 1, 2);
                    break;
                case Keys.Down:
                    AssertAt(host, doc, 3, 2);
                    break;
                case Keys.Left:
                    AssertAt(host, doc, 2, 1);
                    break;
                case Keys.Right:
                    AssertAt(host, doc, 2, 3);
                    break;
                // 行/列の端へのジャンプ(隣接と異なる到達先=取り違え kill)
                case Keys.Home:
                    AssertAt(host, doc, 2, 0);
                    break;
                case Keys.End:
                    AssertAt(host, doc, 2, 4);
                    break;
                case Keys.PageUp:
                    AssertAt(host, doc, 0, 2);
                    break;
                case Keys.PageDown:
                    AssertAt(host, doc, 4, 2);
                    break;
                case Keys.Control | Keys.Home:
                    AssertAt(host, doc, 0, 0);
                    break;
                case Keys.Control | Keys.End:
                    AssertAt(host, doc, 4, 4);
                    break;
                // 読み上げのみ(移動なし。AssertAt の State assert が「動かない」も固定する)
                case Keys.Tab:
                case Keys.Shift | Keys.Tab:
                    AssertAt(host, doc, 2, 2);
                    break;
                case Keys.C: // 列の見出し=(0,2)
                    Assert.Equal(CsvAnnounceFormatter.Header("r0c2"), host.Announcer.Said[^1]);
                    Assert.Equal(2, doc.State.CsvRow);
                    Assert.Equal(2, doc.State.CsvCol);
                    break;
                case Keys.R: // 行の見出し=(2,0)
                    Assert.Equal(CsvAnnounceFormatter.Header("r2c0"), host.Announcer.Said[^1]);
                    Assert.Equal(2, doc.State.CsvRow);
                    Assert.Equal(2, doc.State.CsvCol);
                    break;
                // セル指定・編集
                case Keys.G:
                case Keys.Control | Keys.G:
                    Assert.Equal(1, host.Picker.PickCount); // Picker 経由であることも固定
                    AssertAt(host, doc, 3, 3);
                    break;
                case Keys.F2:
                    Assert.True(host.Csv.IsEditing); // 後始末は Host.Dispose の AbortEdit
                    break;
                default:
                    throw new Xunit.Sdk.XunitException(
                        $"ByKey に本テスト表に無いキーがあります: {key}。エントリ追加時はここへ期待効果を追記すること。"
                    );
            }
        });
}
