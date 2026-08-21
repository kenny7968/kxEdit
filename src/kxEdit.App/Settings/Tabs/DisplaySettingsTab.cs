using kxEdit.App.Settings;
using kxEdit.Core.Settings;

namespace kxEdit.App.Settings.Tabs;

/// <summary>「表示」タブ。フォントと配色テーマを扱う。</summary>
public sealed class DisplaySettingsTab : ISettingsTab
{
    public string Title => "表示";

    private string _fontName = "";
    private float _fontSize = 12f;

    private readonly Label _fontLabel = new() { AutoSize = true };
    private readonly Button _fontButton = new() { Text = "変更(&F)...", AutoSize = true };
    private readonly ComboBox _theme = new()
    {
        DropDownStyle = ComboBoxStyle.DropDownList,
        Width = 240,
        AccessibleName = "配色テーマ",
    };
    private readonly CheckBox _showLineNumbers = new()
    {
        Text = "行番号を表示する(&N)",
        AutoSize = true,
    };
    private readonly CheckBox _highlightCurrentLine = new()
    {
        Text = "現在行を強調表示する(&H)",
        AutoSize = true,
    };
    private readonly NumericUpDown _caretWidth = new()
    {
        Minimum = 1,
        Maximum = 5,
        Width = 100,
        AccessibleName = "キャレットの太さ",
    };
    private readonly CheckBox _showWhitespace = new()
    {
        Text = "空白・改行文字を表示する(&B)",
        AutoSize = true,
    };

    public Control BuildPage()
    {
        foreach (var t in AppearanceThemes.All)
            _theme.Items.Add(t.DisplayName);
        _fontButton.Click += (_, _) => PickFont();

        var root = SettingsTabLayoutHelper.NewRoot();

        // フォント行: ラベル ＋ [現在表示 + 変更ボタン]。アクセスキー &F はボタン側に一本化。
        var fontLabelCol = new Label
        {
            Text = "フォント:",
            AutoSize = true,
            TabIndex = 0,
        };
        var fontPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            TabIndex = 1,
        };
        fontPanel.Controls.Add(_fontLabel);
        fontPanel.Controls.Add(_fontButton);
        root.Controls.Add(fontLabelCol, 0, 0);
        root.Controls.Add(fontPanel, 1, 0);

        SettingsTabLayoutHelper.AddRow(root, 1, "配色(&C):", _theme, tabBase: 2);

        _showLineNumbers.TabIndex = 4;
        root.Controls.Add(_showLineNumbers, 0, 2);
        root.SetColumnSpan(_showLineNumbers, 2);

        _highlightCurrentLine.TabIndex = 5;
        root.Controls.Add(_highlightCurrentLine, 0, 3);
        root.SetColumnSpan(_highlightCurrentLine, 2);

        // キャレットの太さ: ラベル ＋ NumericUpDown（px・弱視の視認性対策）。
        var caretPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            FlowDirection = FlowDirection.LeftToRight,
            TabIndex = 6,
        };
        var caretLbl = new Label
        {
            Text = "キャレットの太さ(&W):",
            AutoSize = true,
            TabIndex = 6,
            Anchor = AnchorStyles.Left,
        };
        _caretWidth.TabIndex = 7;
        caretPanel.Controls.Add(caretLbl);
        caretPanel.Controls.Add(_caretWidth);
        root.Controls.Add(caretPanel, 0, 4);

        _showWhitespace.TabIndex = 8;
        root.Controls.Add(_showWhitespace, 0, 5);
        root.SetColumnSpan(_showWhitespace, 2);

        return root;
    }

    public void LoadFrom(AppSettings s)
    {
        _fontName = s.FontName;
        _fontSize = s.FontSize;
        UpdateFontLabel();
        _theme.SelectedIndex = IndexOfTheme(s.Theme);
        _showLineNumbers.Checked = s.ShowLineNumbers;
        _highlightCurrentLine.Checked = s.HighlightCurrentLine;
        _caretWidth.Value = Math.Clamp(
            s.CaretWidth,
            (int)_caretWidth.Minimum,
            (int)_caretWidth.Maximum
        );
        _showWhitespace.Checked = s.ShowWhitespace;
    }

    public void SaveTo(AppSettings r)
    {
        r.FontName = _fontName;
        r.FontSize = _fontSize;
        r.Theme = AppearanceThemes.All[_theme.SelectedIndex].Id;
        r.ShowLineNumbers = _showLineNumbers.Checked;
        r.HighlightCurrentLine = _highlightCurrentLine.Checked;
        r.CaretWidth = (int)_caretWidth.Value;
        r.ShowWhitespace = _showWhitespace.Checked;
    }

    private static int IndexOfTheme(string id)
    {
        for (int i = 0; i < AppearanceThemes.All.Count; i++)
            if (AppearanceThemes.All[i].Id == id)
                return i;
        return 0;
    }

    private void UpdateFontLabel()
    {
        string desc = $"{_fontName}, {_fontSize:0.#} pt";
        _fontLabel.Text = desc;
        // 変更ボタンに現在値を載せ、フォーカス時に SR が「フォント変更 現在 …」と読めるようにする。
        _fontButton.AccessibleName = $"フォント変更 現在 {desc}";
    }

    private void PickFont()
    {
        // タブは Form ではないため、親ダイアログは FindForm() で取得する（フォーカスが親に戻る挙動を保つ）。
        using var dlg = new FontDialog
        {
            Font = SafeFont(),
            ShowEffects = false,
            FontMustExist = true,
        };
        if (dlg.ShowDialog(_fontButton.FindForm()) != DialogResult.OK)
            return;
        _fontName = dlg.Font.Name;
        _fontSize = dlg.Font.Size;
        UpdateFontLabel();
    }

    private Font SafeFont()
    {
        try
        {
            return new Font(_fontName, _fontSize <= 0 ? 12f : _fontSize);
        }
        catch
        {
            return new Font(FontFamily.GenericMonospace, 12f);
        }
    }

    // CA1001 対応(Sub 3.4-B): BuildPage() 経由で Form の Controls ツリーに接続された
    // 場合は Form.Dispose 経由で二重に呼ばれるが、Control.Dispose は冪等なので安全。
    // BuildPage 未呼び出しで破棄された場合(異常系/テスト)のリーク防止が本 Dispose の主目的。
    public void Dispose()
    {
        _fontLabel.Dispose();
        _fontButton.Dispose();
        _theme.Dispose();
        _showLineNumbers.Dispose();
        _highlightCurrentLine.Dispose();
        _caretWidth.Dispose();
        _showWhitespace.Dispose();
    }
}
