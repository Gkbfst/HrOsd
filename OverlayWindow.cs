using System.Drawing.Text;

namespace HrOsd;

/// <summary>
/// 覆盖层窗口。无边框、可置顶、可点击穿透、不抢焦点、不占 Alt+Tab。
/// 用 GDI 自绘，避免 WPF 透明窗口与游戏 D3D 设备争用。
/// </summary>
internal sealed class OverlayWindow : Form
{
    readonly Config _cfg;
    Font _font;
    List<(string Label, string Value)> _lines = new();

    int _labelW = 60;
    int _valueW = 100;
    const int Gap = 14;

    public OverlayWindow(Config cfg)
    {
        _cfg = cfg;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = ColorUtil.FromRgb(cfg.BackRgb);
        Opacity = Math.Clamp(cfg.Opacity, 10, 100) / 100.0;
        TopMost = cfg.TopMost;
        Location = new Point(cfg.PosX, cfg.PosY);
        Size = new Size(240, 120);   // 先给个可见的初始尺寸，首次 Relayout 会精确调整

        SetStyle(ControlStyles.OptimizedDoubleBuffer |
                 ControlStyles.AllPaintingInWmPaint |
                 ControlStyles.UserPaint, true);

        _font = BuildFont();
        ApplyExStyle();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            // 注意：Form 的基类构造函数会读取本属性，此时 _cfg 尚未赋值，必须判空
            if (_cfg != null && _cfg.ClickThrough) cp.ExStyle |= Native.WS_EX_TRANSPARENT;
            return cp;
        }
    }

    Font BuildFont()
    {
        FontStyle st = _cfg.FontBold ? FontStyle.Bold : FontStyle.Regular;
        try { return new Font(_cfg.FontName, _cfg.FontSize, st, GraphicsUnit.Point); }
        catch { return new Font("Segoe UI", _cfg.FontSize, st, GraphicsUnit.Point); }
    }

    /// <summary>运行时切换置顶 / 穿透，需要改扩展样式而非重建窗口。</summary>
    public void ApplyExStyle()
    {
        if (!IsHandleCreated) return;

        int ex = Native.GetWindowLong(Handle, Native.GWL_EXSTYLE);
        if (_cfg.ClickThrough) ex |= Native.WS_EX_TRANSPARENT;
        else ex &= ~Native.WS_EX_TRANSPARENT;
        Native.SetWindowLong(Handle, Native.GWL_EXSTYLE, ex);

        TopMost = _cfg.TopMost;
        Native.SetWindowPos(Handle,
            _cfg.TopMost ? Native.HWND_TOPMOST : Native.HWND_NOTOPMOST,
            0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>配置改动后调用：重建字体、更新颜色与透明度。</summary>
    public void ApplyConfig()
    {
        var old = _font;
        _font = BuildFont();
        try { old?.Dispose(); } catch { }

        BackColor = ColorUtil.FromRgb(_cfg.BackRgb);
        Opacity = Math.Clamp(_cfg.Opacity, 10, 100) / 100.0;
        ApplyExStyle();
        Relayout();
        Invalidate();
    }

    /// <summary>刷新数据，同时按内容重新计算窗口尺寸。</summary>
    public void UpdateData(List<(string Label, string Value)> lines)
    {
        _lines = lines;
        Relayout();
        Invalidate();
    }

    void Relayout()
    {
        if (_lines.Count == 0) return;
        if (!IsHandleCreated) return;

        using var g = CreateGraphics();

        int lw = 0, vw = 0;
        foreach (var (l, v) in _lines)
        {
            var a = TextRenderer.MeasureText(g, l, _font, Size.Empty, TextFormatFlags.NoPadding);
            var b = TextRenderer.MeasureText(g, v, _font, Size.Empty, TextFormatFlags.NoPadding);
            if (a.Width > lw) lw = a.Width;
            if (b.Width > vw) vw = b.Width;
        }

        _labelW = lw;
        _valueW = Math.Max(vw, 96);   // 留最小宽度，避免数值变化导致窗口抖动

        int pad = _cfg.Padding;
        int lineH = LineHeight(g);
        int w = pad * 2 + _labelW + Gap + _valueW;
        int h = pad * 2 + lineH * _lines.Count;
        if (w < 120) w = 120;
        if (h < 48) h = 48;

        if (Width != w || Height != h)
        {
            // 用 SetWindowPos 避免 Size 触发的布局风暴
            Native.SetWindowPos(Handle, IntPtr.Zero, 0, 0, w, h,
                Native.SWP_NOMOVE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
    }

    int LineHeight(Graphics g)
    {
        var sz = TextRenderer.MeasureText(g, "Ag国", _font, Size.Empty, TextFormatFlags.NoPadding);
        return sz.Height + _cfg.LineGap;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;

        if (_lines.Count == 0) return;

        int pad = _cfg.Padding;
        int lineH = LineHeight(g);
        int y = pad;

        

        foreach (var (label, value) in _lines)
        {
            TextRenderer.DrawText(g, label, _font,
                new Point(pad, y), ColorUtil.FromRgb(_cfg.TextRgb),
                TextFormatFlags.NoPadding);

            var vRect = new Rectangle(pad + _labelW + Gap, y, _valueW, lineH);
            TextRenderer.DrawText(g, value, _font, vRect, ColorUtil.FromRgb(_cfg.TextRgb),
                TextFormatFlags.NoPadding | TextFormatFlags.Right);

            y += lineH;
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && !_cfg.ClickThrough)
        {
            try
            {
                ReleaseCapture();
                SendMessage(Handle, 0xA1, 0x2, 0);   // WM_NCLBUTTONDOWN / HTCAPTION
            }
            catch { }
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool ReleaseCapture();

    [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
    static extern IntPtr SendMessage(IntPtr hWnd, int msg, int wParam, int lParam);

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // 位置变化时记住
        _cfg.PosX = Left;
        _cfg.PosY = Top;
        base.OnFormClosing(e);
    }
}
