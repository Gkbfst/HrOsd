using System.Diagnostics;
using HrOsd.Sensors;

namespace HrOsd;

internal static class Program
{
    static readonly string LogDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HrOsd");

    public static string LogPath => Path.Combine(LogDir, "startup.log");

    public static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            File.AppendAllText(LogPath, $"{DateTime.Now:HH:mm:ss.fff}  {msg}\r\n");
        }
        catch { }
    }

    [STAThread]
    static void Main()
    {
        Log("==================== 启动 ====================");
        Log("exe 路径      : " + Environment.ProcessPath);
        Log("工作目录      : " + Environment.CurrentDirectory);
        Log("运行时版本    : " + Environment.Version);
        Log("系统          : " + Environment.OSVersion);
        Log("是否 64 位进程: " + Environment.Is64BitProcess);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log("!! 未处理异常(AppDomain): " + e.ExceptionObject);
        };

        try
        {
            Log("步骤 1/6：单实例检查");
            using var mutex = new Mutex(true, @"Local\HrOsd_SingleInstance", out bool isNew);
            if (!isNew)
            {
                Log("已有实例在运行，退出");
                MessageBox.Show("HrOsd 已经在运行了。\n看任务栏右下角的托盘图标（可能被折叠了），\n或用设置热键 Ctrl+Alt+P 唤出面板。",
                    "HrOsd", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            Log("步骤 2/6：初始化 WinForms");
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            Application.ThreadException += (_, e) =>
            {
                Log("!! UI 线程异常: " + e.Exception);
                MessageBox.Show("界面线程出错：\n\n" + e.Exception.Message + "\n\n详情见：\n" + LogPath,
                    "HrOsd 错误", MessageBoxButtons.OK, MessageBoxIcon.Error);
            };

            Log("步骤 3/6：创建主上下文");
            var ctx = new AppContext();

            Log("步骤 4/6：进入消息循环");
            Application.Run(ctx);

            Log("步骤 5/6：消息循环结束，正常退出");
        }
        catch (Exception ex)
        {
            Log("!! 致命错误: " + ex);
            try
            {
                MessageBox.Show(
                    "HrOsd 启动失败：\n\n" + ex.GetType().Name + "\n" + ex.Message +
                    "\n\n完整堆栈已写入：\n" + LogPath,
                    "HrOsd 启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
        finally
        {
            Log("步骤 6/6：进程结束");
        }
    }
}

/// <summary>隐藏窗口，专门接收 WM_HOTKEY。</summary>
internal sealed class HotkeyWindow : NativeWindow
{
    public event Action<int> Pressed;

    public HotkeyWindow()
    {
        CreateHandle(new CreateParams());
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_HOTKEY)
        {
            try { Pressed?.Invoke(m.WParam.ToInt32()); } catch { }
        }
        base.WndProc(ref m);
    }
}

internal sealed class AppContext : ApplicationContext
{
    const int HK_TOGGLE = 1;
    const int HK_SETTINGS = 2;

    readonly Config _cfg;
    readonly OverlayWindow _overlay;
    readonly Ticker _ticker;
    readonly HotkeyWindow _hkWin = new();
    readonly NotifyIcon _tray = new();
    readonly System.Windows.Forms.Timer _timer = new();

    bool _toggledVisible = true;

    public AppContext()
    {
        Program.Log("  A：读取配置");
        _cfg = Config.Load();
        Program.Log($"     可见项 {_cfg.Visible.Count} 个，位置 {_cfg.PosX},{_cfg.PosY}，不透明度 {_cfg.Opacity}");

        Program.Log("  B：创建覆盖层窗口");
        _overlay = new OverlayWindow(_cfg);
        ClampToScreen(_cfg);

        Program.Log("  C：显示窗口");
        _overlay.Show();
        Program.Log($"     窗口已显示：Location={_overlay.Location}  Size={_overlay.Size}  Visible={_overlay.Visible}");

        _overlay.UpdateData(new List<(string, string)> { ("准备中", "…") });

        Program.Log("  D：初始化传感器");
        // PresentMon 用绝对路径，避免工作目录不同导致找不到
        var pm = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "presentmon.exe");
        if (File.Exists(pm)) _cfg.PresentMonPath = pm;
        Program.Log("     presentmon: " + (File.Exists(pm) ? pm : "未找到（FPS 将不可用）"));
        _ticker = new Ticker(_cfg);
        Program.Log("     GPU: " + _ticker.DiagGpu);
        Program.Log("     HWiNFO: " + _ticker.DiagHwInfo);

        Program.Log("  E：创建托盘");
        BuildTray();

        Program.Log("  F：注册热键");
        RegisterHotkeys();

        Program.Log("  G：启动采样定时器");
        _timer.Interval = Math.Clamp(_cfg.SampleMs, 100, 5000);
        _timer.Tick += (_, _) => Tick();
        _timer.Start();

        Tick();
        Program.Log("  H：初始化完成");
    }

    void ClampToScreen(Config c)
    {
        var scr = Screen.PrimaryScreen ?? (Screen.AllScreens.Length > 0 ? Screen.AllScreens[0] : null);
        if (scr == null) return;
        var wa = scr.WorkingArea;
        if (c.PosX < wa.Left - 50) c.PosX = wa.Left + 20;
        if (c.PosY < wa.Top - 50) c.PosY = wa.Top + 20;
        if (c.PosX > wa.Right - 40) c.PosX = Math.Max(wa.Left, wa.Right - 320);
        if (c.PosY > wa.Bottom - 40) c.PosY = Math.Max(wa.Top, wa.Bottom - 240);
    }

    void BuildTray()
    {
        _tray.Icon = SystemIcons.Application;
        _tray.Text = "HrOsd 性能覆盖层";
        _tray.Visible = true;

        var menu = new ContextMenuStrip();
        menu.Items.Add("显示 / 隐藏", null, (_, _) => ToggleOverlay());
        menu.Items.Add("设置…", null, (_, _) => OpenSettings());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("导出 HWiNFO 传感器清单", null, (_, _) =>
        {
            try
            {
                var f = Path.Combine(Config.Dir, "hwinfo-sensors.txt");
                var r = _ticker.DumpHwInfo(f);
                MessageBox.Show("已导出到：\n" + r, "HrOsd", MessageBoxButtons.OK, MessageBoxIcon.Information);
                try { Process.Start("notepad.exe", f); } catch { }
            }
            catch (Exception ex) { MessageBox.Show(ex.Message); }
        });
        menu.Items.Add("打开日志", null, (_, _) =>
        {
            try { Process.Start("notepad.exe", Program.LogPath); } catch { }
        });
        menu.Items.Add("打开配置文件位置", null, (_, _) =>
        {
            try { Process.Start("explorer.exe", Config.Dir); } catch { }
        });
        menu.Items.Add("退出", null, (_, _) => ExitApp());

        _tray.ContextMenuStrip = menu;
        _tray.DoubleClick += (_, _) => ToggleOverlay();
    }

    void RegisterHotkeys()
    {
        UnregisterAll();

        var (m1, k1) = Hotkey.Parse(_cfg.HotkeyToggle);
        bool ok1 = k1 != 0 && Native.RegisterHotKey(_hkWin.Handle, HK_TOGGLE, m1 | Native.MOD_NOREPEAT, k1);

        var (m2, k2) = Hotkey.Parse(_cfg.HotkeySettings);
        bool ok2 = k2 != 0 && Native.RegisterHotKey(_hkWin.Handle, HK_SETTINGS, m2 | Native.MOD_NOREPEAT, k2);

        Program.Log($"     热键 {_cfg.HotkeyToggle} => {ok1} ；{_cfg.HotkeySettings} => {ok2}");

        _hkWin.Pressed -= OnHotkey;
        _hkWin.Pressed += OnHotkey;
    }

    void UnregisterAll()
    {
        try { Native.UnregisterHotKey(_hkWin.Handle, HK_TOGGLE); } catch { }
        try { Native.UnregisterHotKey(_hkWin.Handle, HK_SETTINGS); } catch { }
    }

    void OnHotkey(int id)
    {
        if (id == HK_TOGGLE) ToggleOverlay();
        else if (id == HK_SETTINGS) OpenSettings();
    }

    void ToggleOverlay()
    {
        _toggledVisible = !_toggledVisible;
        _overlay.Visible = _toggledVisible;
        _timer.Enabled = _toggledVisible;
        Program.Log("切换可见性 => " + _toggledVisible);
    }

    void Tick()
    {
        try
        {
            _ticker.Sample();
            _overlay.UpdateData(_ticker.Lines);
        }
        catch (Exception ex)
        {
            Program.Log("采样异常: " + ex.Message);
        }
    }

    void OpenSettings()
    {
        using var dlg = new SettingsWindow(_cfg);
        var r = dlg.ShowDialog();
        if (r != DialogResult.OK) return;

        _cfg.Save();
        _overlay.ApplyConfig();
        _timer.Interval = Math.Clamp(_cfg.SampleMs, 100, 5000);
        RegisterHotkeys();
        Tick();
        Program.Log("设置已保存");
    }

    void ExitApp()
    {
        Program.Log("用户退出");
        _timer.Stop();
        UnregisterAll();
        try { _tray.Visible = false; _tray.Dispose(); } catch { }
        try { _ticker.Dispose(); } catch { }
        try { _overlay.Close(); } catch { }
        try { _cfg.Save(); } catch { }
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            try { _timer.Dispose(); } catch { }
            try { _tray.Dispose(); } catch { }
        }
        base.Dispose(disposing);
    }
}

/// <summary>热键字符串 &lt;-&gt; Win32 修饰键/虚拟键 互转。</summary>
internal static class Hotkey
{
    public static (uint mod, uint vk) Parse(string s)
    {
        uint mod = 0, vk = 0;
        if (string.IsNullOrWhiteSpace(s)) return (0, 0);

        foreach (var raw in s.Split('+'))
        {
            var t = raw.Trim();
            if (t.Length == 0) continue;

            switch (t.ToLowerInvariant())
            {
                case "ctrl": case "control": mod |= Native.MOD_CONTROL; break;
                case "alt":  mod |= Native.MOD_ALT; break;
                case "shift":mod |= Native.MOD_SHIFT; break;
                case "win":  mod |= Native.MOD_WIN; break;
                default:
                    if (Enum.TryParse<Keys>(t, true, out var k)) vk = (uint)k;
                    else if (t.Length == 1 && char.IsDigit(t[0])) vk = (uint)(Keys.D0 + (t[0] - '0'));
                    else if (t.StartsWith("F") && int.TryParse(t[1..], out int f) && f >= 1 && f <= 24)
                        vk = (uint)(Keys.F1 + f - 1);
                    break;
            }
        }
        return (mod, vk);
    }
}
