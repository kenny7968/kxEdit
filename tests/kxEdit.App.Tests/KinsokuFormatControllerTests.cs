using kxEdit.App.Tests.Fakes;
using kxEdit.Core.Csv;
using kxEdit.Core.Settings;
using kxEdit.Core.Text;

namespace kxEdit.App.Tests;

/// <summary>
/// Phase 2 Stage 8 Task B: KinsokuFormatController の抽出(MainForm.FormatWithKinsoku から)。
/// 9 Fact + 3 Theory rows = 計 12 件: 部分整形(×2)/全文整形/変更なし/CSV 抑止/空 no-op/EOL 追随/
/// 禁則パラメータ配線 3 件(行頭↔行末 swap・TabWidth・HangChars=Phase 2 レビュー回収)。
/// 実 DocumentManager+実 EditorControl を STA 上で使い、通知(FakeAnnouncer)だけを偽物にする。
/// AppSettings は Run 引数(=呼び出し時解決)なので POCO を直接渡す。
/// </summary>
public class KinsokuFormatControllerTests
{
    /// <summary>KinsokuFormatController を Fake 境界で配線したテストホスト(共通 HostForm.CreateWithDocs を使う)。</summary>
    private sealed class Host : IDisposable
    {
        public Form Form { get; }
        public DocumentManager Docs { get; }
        public FakeAnnouncer Announcer { get; } = new();
        public KinsokuFormatController Kinsoku { get; }

        /// <summary>WrapColumn=20(半角換算=CJK 10 文字で折返し)。禁則集合は最小 POCO(テストが必要とする範囲のみ)。</summary>
        public AppSettings Settings { get; } =
            new()
            {
                WrapColumn = 20,
                KinsokuLineStartChars = "、。",
                KinsokuLineEndChars = "「（",
                KinsokuHangChars = "",
                TabWidth = 4,
            };

        public Host()
        {
            var (form, docs) = HostForm.CreateWithDocs();
            Form = form;
            Docs = docs;
            Kinsoku = new KinsokuFormatController(Docs, Announcer);
        }

        /// <summary>クリーンな本文を持つアクティブ文書を作る(Text セッター=新規バッファで Modified=false・キャレット 0)。</summary>
        public Document NewDoc(string text)
        {
            var doc = Docs.CreateNew();
            doc.Editor.Text = text;
            return doc;
        }

        public void Dispose() => Form.Dispose();
    }

    // ===== 1. 部分選択整形 =====

    [Fact]
    public void PartialSelection_Formats_AndSelectsChangedRange_AndAnnouncesSuccess() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            // 20 CJK 文字(=40 半角桁) > WrapColumn=20 → 少なくとも 1 回は改行挿入されて formatted != target
            var doc = host.NewDoc("あいうえおかきくけこさしすせそたちつてと");
            doc.Editor.SelectCharRange(0, 20); // 全 20 文字を選択(部分選択パスに入る=selStart != selEnd)

            host.Kinsoku.Run(host.Settings);

            Assert.Contains("整形しました", host.Announcer.Said);
            // 部分整形: SelectCharRange(start, formatted.Length) → [0, formatted.Length)
            var (s, e) = doc.Editor.GetSelectionCharRange();
            Assert.Equal(0, s);
            Assert.Equal(doc.Editor.SnapshotText.Length, e); // 置換で buffer 全体 = formatted になり、その全長が選択される
            Assert.True(e > 20, "整形で EOL が挿入され buffer が伸びるはず(20 -> >20)");
        });

    // ===== 1b. 部分整形(prefix + 選択 + suffix)=====
    // レビュー由来: (0, text.Length) の部分選択は whole/partial の start/len 計算が同値になり
    // `start = whole ? 0 : 0` 変異(partial 経路のデータ破損)が生存するため、
    // 選択範囲外の prefix/suffix がバイト不変であることを明示的に固定する。

    [Fact]
    public void PartialSelection_OnlyFormatsSelectedRange_LeavingPrefixAndSuffixUnchanged() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            // prefix + 40 CJK(WrapColumn=20 で 3 回改行挿入) + suffix。
            // 選択部だけが整形され、prefix/suffix は物理位置ごと不変であることを検証する。
            var prefix = "PREFIX"; // 6 半角=partial 経路の外
            var target = new string('あ', 40); // 40 CJK=80 半角桁 → WrapColumn=20 で確実に改行挿入
            var suffix = "SUFFIX"; // 6 半角=partial 経路の外
            var doc = host.NewDoc(prefix + target + suffix);
            doc.Editor.SelectCharRange(prefix.Length, target.Length); // [6, 46) を選択(partial 経路)

            host.Kinsoku.Run(host.Settings);

            Assert.Contains("整形しました", host.Announcer.Said);
            string result = doc.Editor.SnapshotText;
            // (a) prefix/suffix はバイト位置ごと不変(partial 経路が start/len を正しく使っている証明)
            Assert.StartsWith(prefix, result);
            Assert.EndsWith(suffix, result);
            // (b) 中央部は改行が入って原文の target とは異なる
            string middle = result.Substring(
                prefix.Length,
                result.Length - prefix.Length - suffix.Length
            );
            Assert.NotEqual(target, middle);
            Assert.True(middle.Length > target.Length, "整形で EOL が挿入され中央部が伸びるはず");
            // (c) 選択範囲は変化後の中央部を指す(start=selStart=prefix.Length、length=formatted.Length=middle.Length)
            var (s, e) = doc.Editor.GetSelectionCharRange();
            Assert.Equal(prefix.Length, s);
            Assert.Equal(prefix.Length + middle.Length, e);
        });

    // ===== 2. 全文整形(選択なし=whole=true) =====

    [Fact]
    public void WholeText_NoSelection_Formats_AndCaretToStart_AndAnnouncesSuccess() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("あいうえおかきくけこさしすせそたちつてと"); // 折返し発生する 20 CJK
            // 非既定位置の空選択(キャレット=3・全文整形経路)から検証開始(Stage 6 標準)。
            // 空選択でキャレットのみ位置 3 に置くと selStart == selEnd == 3 → whole=true 経路に入る。
            doc.Editor.SelectCharRange(3, 0);

            host.Kinsoku.Run(host.Settings);

            Assert.Contains("整形しました", host.Announcer.Said);
            // 全文整形時はキャレットが (0, 0) に移動(全選択を避ける仕様)
            var (s, e) = doc.Editor.GetSelectionCharRange();
            Assert.Equal(0, s);
            Assert.Equal(0, e);
            Assert.True(doc.Editor.SnapshotText.Length > 20, "全文整形で EOL が挿入されているはず");
        });

    // ===== 3. 変更なし =====

    [Fact]
    public void NoChange_AnnouncesNoChange_AndBufferUnchanged() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            // 既に整形済み=WrapColumn=20 未満で改行不要な短い部分
            var doc = host.NewDoc("短い1行");
            // 非既定位置から検証開始(Stage 6 レビュー標準): 部分選択パス(selStart != selEnd)
            doc.Editor.SelectCharRange(1, 3); // [1, 4) を選択 → 部分整形パス
            string textBefore = doc.Editor.SnapshotText;

            host.Kinsoku.Run(host.Settings);

            Assert.Contains("変更なし", host.Announcer.Said);
            Assert.DoesNotContain("整形しました", host.Announcer.Said);
            Assert.Equal(textBefore, doc.Editor.SnapshotText); // バッファ不変
        });

    // ===== 4. CSV モード中は抑止 =====

    [Fact]
    public void CsvMode_Blocked_AnnouncesBlockedText_AndBufferUnchanged() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc("あいうえおかきくけこさしすせそたちつてと"); // 本来なら整形される長さ
            doc.State.CsvMode = true; // CsvController を介さず状態だけ立てる(判定は State 経由)
            string textBefore = doc.Editor.SnapshotText;

            host.Kinsoku.Run(host.Settings);

            Assert.Contains(CsvAnnounceFormatter.BlockedInCsvMode, host.Announcer.Said);
            Assert.DoesNotContain("整形しました", host.Announcer.Said);
            Assert.Equal(textBefore, doc.Editor.SnapshotText); // 誤成功通知/誤変更の両方を抑止
        });

    // ===== 5. 空バッファ no-op(len<=0) =====

    [Fact]
    public void EmptyBufferNoSelection_NoOp_NoAnnouncement() =>
        Sta.Run(() =>
        {
            using var host = new Host();
            var doc = host.NewDoc(string.Empty); // 空バッファ
            doc.Editor.SelectCharRange(0, 0);

            host.Kinsoku.Run(host.Settings);

            Assert.Empty(host.Announcer.Said); // 発声なし(誤成功/誤失敗通知どちらもしない)
            Assert.Equal(string.Empty, doc.Editor.SnapshotText);
        });

    // ===== 6. EOL 追随(CRLF/LF/CR) =====

    [Theory]
    [InlineData(LineEnding.Crlf, "\r\n")]
    [InlineData(LineEnding.Lf, "\n")]
    [InlineData(LineEnding.Cr, "\r")]
    public void UsesActiveDocumentEol(LineEnding eol, string eolString) =>
        Sta.Run(() =>
        {
            using var host = new Host();
            // 折返しが確実に発生する長さ(20 CJK 超) & 元テキストに改行を含めない(挿入 EOL のみが結果に現れる)
            var doc = host.NewDoc("あいうえおかきくけこさしすせそたちつてと");
            doc.State.LineEnding = eol;

            host.Kinsoku.Run(host.Settings);

            // 整形結果に指定した EOL が含まれる(=Document.State.LineEnding が使われている)
            string result = doc.Editor.SnapshotText;
            Assert.Contains(eolString, result);
            Assert.Contains("整形しました", host.Announcer.Said);

            // 他 EOL 種が混入していないことも確認(CRLF/LF/CR の区別が実質的に効いていることの保証)
            // - LF 選択時: \r があってはならない(挿入は "\n" のみ)
            // - CR 選択時: \n があってはならない(挿入は "\r" のみ)
            // - CRLF 選択時: \r と \n の数が一致(すべての \r に対応する \n がある)
            if (eol == LineEnding.Lf)
                Assert.DoesNotContain("\r", result);
            if (eol == LineEnding.Cr)
                Assert.DoesNotContain("\n", result);
            if (eol == LineEnding.Crlf)
                Assert.Equal(result.Count(c => c == '\r'), result.Count(c => c == '\n'));
        });

    // ===== 7. 禁則パラメータ配線(Phase 2 レビュー回収) =====
    // Run が Format の第3〜5・第7引数(行頭禁則/行末禁則/ぶら下げ/タブ幅)を settings から正しく
    // 渡していることを固定する。期待値はすべてリテラル(テスト内で Format を正引数で呼んで期待値を
    // 作ると、実装と同じ swap をした瞬間に同値になるため禁止)。導出根拠は Core の
    // KinsokuFormatterTests(LineStart_kinsoku_pushes_forbidden_char_down ほか)と同型 fixture。

    [Fact]
    public void Run_WiresLineStartAndLineEndChars_Distinctly() =>
        Sta.Run(() =>
        {
            // kill: Format 第3・第4引数(LineStartChars↔LineEndChars)の swap 変異。
            using var host = new Host();
            // WrapColumn=6(全角3文字で折返し)。2 論理行で swap の両方向を 1 fixture に固定する:
            // 行1「あいう。え」: 折れ目の「。」が行頭禁則 →「う」ごと追い出し(正: あい/う。え・swap 時: あいう/。え)
            // 行2「あい（うえ」: 行末の「（」が行末禁則 → 次行へ送る(正: あい/（うえ・swap 時: あい（/うえ)
            var doc = host.NewDoc("あいう。え\nあい（うえ");
            doc.State.LineEnding = LineEnding.Lf; // 挿入 EOL をリテラル期待値("\n")に固定
            var settings = new AppSettings
            {
                WrapColumn = 6,
                KinsokuLineStartChars = "。", // 行頭に置けない(追い出し)
                KinsokuLineEndChars = "（", // 行末に置けない(次行送り)
                KinsokuHangChars = "",
                TabWidth = 4,
            };

            host.Kinsoku.Run(settings);

            Assert.Contains("整形しました", host.Announcer.Said);
            Assert.Equal("あい\nう。え\nあい\n（うえ", doc.Editor.SnapshotText);
        });

    [Fact]
    public void Run_WiresTabWidth() =>
        Sta.Run(() =>
        {
            // kill: Format 第7引数 TabWidth の定数 4 化変異(および渡し忘れ=Format 既定 8 も同時に)。
            using var host = new Host();
            // WrapColumn=4・TabWidth=2: 行頭タブ 2 個=4 桁でちょうど収まり "ab" だけ次行 → "\t\t/ab"。
            // TabWidth=4(AppSettings 既定)でも 8(Format 引数既定=渡し忘れ)でもタブ 1 個で行が
            // 埋まり "\t/\t/ab" になるため、2 を使うことで両変異とも期待値と不一致=赤になる。
            var doc = host.NewDoc("\t\tab");
            doc.State.LineEnding = LineEnding.Lf;
            var settings = new AppSettings
            {
                WrapColumn = 4,
                KinsokuLineStartChars = "", // 禁則は全て切ってタブ幅の効果だけを分離
                KinsokuLineEndChars = "",
                KinsokuHangChars = "",
                TabWidth = 2, // 非既定値(AppSettings 既定 4 とも Format 既定 8 とも異なる)
            };

            host.Kinsoku.Run(settings);

            Assert.Contains("整形しました", host.Announcer.Said);
            Assert.Equal("\t\t\nab", doc.Editor.SnapshotText);
        });

    [Fact]
    public void Run_WiresHangChars() =>
        Sta.Run(() =>
        {
            // kill: Format 第5引数 KinsokuHangChars を "" に潰す変異(ぶら下げ無効化)。
            using var host = new Host();
            // WrapColumn=4(全角2)で「。」が次行頭に来る局面。ぶら下げ有効なら桁超過を許容して
            // 行末に残す(Core: Hang_punctuation_exceeds_column と同型)→ "あい。/う"。
            // "" 変異ではぶら下げが効かず幾何位置で折れて "あい/。う" になる=赤。
            var doc = host.NewDoc("あい。う");
            doc.State.LineEnding = LineEnding.Lf;
            var settings = new AppSettings
            {
                WrapColumn = 4,
                KinsokuLineStartChars = "", // 行頭禁則は切る(ぶら下げの効果だけを分離)
                KinsokuLineEndChars = "",
                KinsokuHangChars = "。",
                TabWidth = 4,
            };

            host.Kinsoku.Run(settings);

            Assert.Contains("整形しました", host.Announcer.Said);
            Assert.Equal("あい。\nう", doc.Editor.SnapshotText);
        });
}
