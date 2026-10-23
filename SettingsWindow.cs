namespace HrOsd;

/// <summary>设置窗口：数据项勾选、字体/颜色/透明度、热键、置顶与穿透。</summary>
internal sealed class SettingsWindow : Form
{
    readonly Config _cfg;
    public Config Result { get; private set; }

    readonly CheckedListBox _list = new();
    readonly Button _btnFont = new();
    readonly Button _btnTextColor = new();
    readonly Button _btnBackColor = new();
    readonly TrackBar _opacity = new();
    readonly Label _lblOpacity = new();
    readonly CheckBox _chkTop = new();
    readonly CheckBox _chkThrough = new();
    readonly NumericUpDown _numX = new();
    readonly NumericUpDown _numY = new();
    readonly TextBox _tbToggleKey = new();
    readonly TextBox _tbSettingsKey = new();
    readonly Button _btnOk = new();
    readonly Button _btnCancel = new();
    readonly Label _hint = new();

    // 工作副本，点确定才写回
    string _fontName;
    float _fontSize;
    bool _fontBold;
    int _textRgb;
    int _backRgb;

    public SettingsWindow(Config cfg)
    {
        _cfg = cfg;
        _fontName = cfg.FontName;
        _fontSize = cfg.FontSize;
        _fontBold = cfg.FontBold;
        _textRgb = cfg.TextRgb;
        _backRgb = cfg.BackRgb;

        Text = "HrOsd 设置";
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(760, 640);
        TopMost = true;
        Font = new Font("Microsoft YaHei UI", 9f);

        BuildUi();
        LoadValues();
    }

    void BuildUi()
    {
        // ---------- 左：数据项 ----------
        var lblItems = new Label
        {
            Text = "显示的数据项（勾选后立即生效）",
            Location = new Point(14, 12),
            AutoSize = true
        };
        Controls.Add(lblItems);

        _list.Location = new Point(14, 36);
        _list.Size = new Size(300, 520);
        _list.CheckOnClick = true;
        _list.Font = new Font("Microsoft YaHei UI", 10f);
        Controls.Add(_list);

        // ---------- 右：外观与行为 ----------
        int x = 336, y = 12;

        Controls.Add(MkLabel("字体", x, y + 4));
        _btnFont.Location = new Point(x + 78, y);
        _btnFont.Size = new Size(300, 30);
        _btnFont.Click += (_, _) =>
        {
            using var fd = new FontDialog { Font = new Font(_fontName, _fontSize, _fontBold ? FontStyle.Bold : FontStyle.Regular), ShowEffects = false };
            if (fd.ShowDialog(this) == DialogResult.OK)
            {
                _fontName = fd.Font.Name;
                _fontSize = fd.Font.Size;
                _fontBold = fd.Font.Bold;
                RefreshFontButton();
            }
        };
        Controls.Add(_btnFont);
        y += 44;

        Controls.Add(MkLabel("文字颜色", x, y + 4));
        _btnTextColor.Location = new Point(x + 78, y);
        _btnTextColor.Size = new Size(300, 30);
        _btnTextColor.Click += (_, _) =>
        {
            using var cd = new ColorDialog { Color = ColorUtil.FromRgb(_textRgb), FullOpen = true };
            if (cd.ShowDialog(this) == DialogResult.OK)
            {
                _textRgb = (cd.Color.R << 16) | (cd.Color.G << 8) | cd.Color.B;
                RefreshColorButtons();
            }
        };
        Controls.Add(_btnTextColor);
        y += 40;

        Controls.Add(MkLabel("背景颜色", x, y + 4));
        _btnBackColor.Location = new Point(x + 78, y);
        _btnBackColor.Size = new Size(300, 30);
        _btnBackColor.Click += (_, _) =>
        {
            using var cd = new ColorDialog { Color = ColorUtil.FromRgb(_backRgb), FullOpen = true };
            if (cd.ShowDialog(this) == DialogResult.OK)
            {
                _backRgb = (cd.Color.R << 16) | (cd.Color.G << 8) | cd.Color.B;
                RefreshColorButtons();
            }
        };
        Controls.Add(_btnBackColor);
        y += 44;

        Controls.Add(MkLabel("不透明度", x, y + 4));
        _opacity.Location = new Point(x + 78, y);
        _opacity.Size = new Size(230, 32);
        _opacity.Minimum = 10;
        _opacity.Maximum = 100;
        _opacity.TickFrequency = 10;
        _opacity.ValueChanged += (_, _) => _lblOpacity.Text = _opacity.Value + "%";
        Controls.Add(_opacity);

        _lblOpacity.Location = new Point(x + 316, y + 8);
        _lblOpacity.AutoSize = true;
        Controls.Add(_lblOpacity);
        y += 40;

        _chkTop.Text = "永久置顶";
        _chkTop.Location = new Point(x + 78, y);
        _chkTop.AutoSize = true;
        Controls.Add(_chkTop);
        y += 28;

        _chkThrough.Text = "鼠标穿透（勾选后游戏内点击不会被拦截）";
        _chkThrough.Location = new Point(x + 78, y);
        _chkThrough.AutoSize = true;
        Controls.Add(_chkThrough);
        y += 36;

        Controls.Add(MkLabel("位置 X / Y", x, y + 4));
        _numX.Location = new Point(x + 78, y);
        _numX.Size = new Size(80, 28);
        _numX.Minimum = -3000;
        _numX.Maximum = 8000;
        Controls.Add(_numX);

        _numY.Location = new Point(x + 168, y);
        _numY.Size = new Size(80, 28);
        _numY.Minimum = -3000;
        _numY.Maximum = 8000;
        Controls.Add(_numY);

        var btnCenter = new Button { Text = "左上角", Location = new Point(x + 258, y - 1), Size = new Size(70, 30) };
        btnCenter.Click += (_, _) => { _numX.Value = 20; _numY.Value = 20; };
        Controls.Add(btnCenter);

        var btnRightTop = new Button { Text = "右上角", Location = new Point(x + 334, y - 1), Size = new Size(70, 30) };
        btnRightTop.Click += (_, _) =>
        {
            var scr = Screen.PrimaryScreen.WorkingArea;
            _numX.Value = scr.Right - 320;
            _numY.Value = 20;
        };
        Controls.Add(btnRightTop);
        y += 46;

        Controls.Add(MkLabel("显示/隐藏热键", x, y + 4));
        _tbToggleKey.Location = new Point(x + 100, y);
        _tbToggleKey.Size = new Size(200, 28);
        _tbToggleKey.ReadOnly = true;
        _tbToggleKey.TextAlign = HorizontalAlignment.Center;
        _tbToggleKey.BackColor = Color.White;
        _tbToggleKey.KeyDown += HotkeyBox_KeyDown;
        Controls.Add(_tbToggleKey);
        y += 38;

        Controls.Add(MkLabel("打开设置热键", x, y + 4));
        _tbSettingsKey.Location = new Point(x + 100, y);
        _tbSettingsKey.Size = new Size(200, 28);
        _tbSettingsKey.ReadOnly = true;
        _tbSettingsKey.TextAlign = HorizontalAlignment.Center;
        _tbSettingsKey.BackColor = Color.White;
        _tbSettingsKey.KeyDown += HotkeyBox_KeyDown;
        Controls.Add(_tbSettingsKey);
        y += 44;

        _hint.MaximumSize = new Size(300, 0);
        _hint.AutoSize = true;
        _hint.ForeColor = Color.DimGray;
        _hint.Text = "点进输入框后直接按组合键即可。需要至少包含一个修饰键（Ctrl/Alt/Shift）。";
        _hint.Location = new Point(x + 2, y);
        Controls.Add(_hint);

        // ---------- 底部按钮 ----------
        _btnOk.Text = "确定";
        _btnOk.Size = new Size(96, 34);
        _btnOk.Location = new Point(ClientSize.Width - 226, ClientSize.Height - 52);
        _btnOk.DialogResult = DialogResult.None;
        _btnOk.Click += (_, _) => SaveAndClose();
        Controls.Add(_btnOk);

        _btnCancel.Text = "取消";
        _btnCancel.Size = new Size(96, 34);
        _btnCancel.Location = new Point(ClientSize.Width - 118, ClientSize.Height - 52);
        _btnCancel.DialogResult = DialogResult.Cancel;
        Controls.Add(_btnCancel);
    }

    static Label MkLabel(string text, int x, int y) => new()
    {
        Text = text,
        Location = new Point(x, y),
        AutoSize = true
    };

    void LoadValues()
    {
        _list.Items.Clear();
        foreach (var it in ItemCatalog.All)
        {
            _list.Items.Add(it.Label, _cfg.IsVisible(it.Key));
        }

        RefreshFontButton();
        RefreshColorButtons();

        _opacity.Value = Math.Clamp(_cfg.Opacity, 10, 100);
        _lblOpacity.Text = _opacity.Value + "%";
        _chkTop.Checked = _cfg.TopMost;
        _chkThrough.Checked = _cfg.ClickThrough;
        _numX.Value = Math.Clamp(_cfg.PosX, -3000, 8000);
        _numY.Value = Math.Clamp(_cfg.PosY, -3000, 8000);
        _tbToggleKey.Text = _cfg.HotkeyToggle;
        _tbSettingsKey.Text = _cfg.HotkeySettings;
    }

    void RefreshFontButton() =>
        _btnFont.Text = $"{_fontName}, {_fontSize:F1}pt{(_fontBold ? " 粗体" : "")}";

    void RefreshColorButtons()
    {
        _btnTextColor.Text = $"#{_textRgb:X6}";
        _btnTextColor.BackColor = ColorUtil.FromRgb(_textRgb);
        _btnTextColor.ForeColor = Brightness(_textRgb) > 140 ? Color.Black : Color.White;

        _btnBackColor.Text = $"#{_backRgb:X6}";
        _btnBackColor.BackColor = ColorUtil.FromRgb(_backRgb);
        _btnBackColor.ForeColor = Brightness(_backRgb) > 140 ? Color.Black : Color.White;
    }

    static int Brightness(int rgb) =>
        (((rgb >> 16) & 0xFF) * 299 + ((rgb >> 8) & 0xFF) * 587 + (rgb & 0xFF) * 114) / 1000;

    void HotkeyBox_KeyDown(object sender, KeyEventArgs e)
    {
        e.SuppressKeyPress = true;
        e.Handled = true;

        var key = e.KeyCode;
        if (key is Keys.ControlKey or Keys.ShiftKey or Keys.Menu or Keys.LWin or Keys.RWin)
            return;

        var parts = new List<string>();
        if (e.Control) parts.Add("Ctrl");
        if (e.Alt) parts.Add("Alt");
        if (e.Shift) parts.Add("Shift");

        if (parts.Count == 0)
        {
            _hint.Text = "至少需要一个修饰键（Ctrl / Alt / Shift），请重按。";
            _hint.ForeColor = Color.Firebrick;
            return;
        }

        parts.Add(key.ToString());
        ((TextBox)sender).Text = string.Join("+", parts);

        _hint.Text = "点进输入框后直接按组合键即可。需要至少包含一个修饰键（Ctrl/Alt/Shift）。";
        _hint.ForeColor = Color.DimGray;
    }

    void SaveAndClose()
    {
        // 数据项按列表顺序重建
        var vis = new List<string>();
        for (int i = 0; i < _list.Items.Count; i++)
        {
            if (_list.GetItemChecked(i))
                vis.Add(ItemCatalog.All[i].Key);
        }

        if (vis.Count == 0)
        {
            MessageBox.Show(this, "至少要勾选一项，否则覆盖层是空的。", "提示",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _cfg.Visible = vis;
        _cfg.FontName = _fontName;
        _cfg.FontSize = _fontSize;
        _cfg.FontBold = _fontBold;
        _cfg.TextRgb = _textRgb;
        _cfg.BackRgb = _backRgb;
        _cfg.Opacity = _opacity.Value;
        _cfg.TopMost = _chkTop.Checked;
        _cfg.ClickThrough = _chkThrough.Checked;
        _cfg.PosX = (int)_numX.Value;
        _cfg.PosY = (int)_numY.Value;
        _cfg.HotkeyToggle = _tbToggleKey.Text;
        _cfg.HotkeySettings = _tbSettingsKey.Text;

        Result = _cfg;
        DialogResult = DialogResult.OK;
        Close();
    }
}
