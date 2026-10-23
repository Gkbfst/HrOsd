namespace HrOsd.Sensors;

/// <summary>把所有数据源汇总成「标签 + 数值」两列，供覆盖层绘制。</summary>
internal sealed class Ticker : IDisposable
{
    readonly Nvml _nvml = new();
    readonly HwInfo _hwi = new();
    readonly CpuMem _cpu = new();
    readonly FpsSource _fps = new();
    readonly Config _cfg;
    readonly DateTime _started = DateTime.Now;
    int _hwiRetry;

    public List<(string Label, string Value)> Lines { get; } = new();
    public string DiagGpu { get; private set; } = "";
    public string DiagHwInfo { get; private set; } = "";
    public string DiagFps { get; private set; } = "";

    public Ticker(Config cfg)
    {
        _cfg = cfg;
        _nvml.Init();
        _hwi.TryOpen();
        _cpu.Start();
        _fps.Start(cfg.PresentMonPath);
        Sample();   // 立刻采一次，保证 Diag* 字段有值
    }

    public string DumpHwInfo(string path) => _hwi.DumpCatalog(path);

    public void Sample()
    {
        // 每轮都刷新跟踪目标：配置里留空则自动跟随当前前台窗口
        _fps.SetTarget(ResolveTarget());

        // HWiNFO 可能中途启动/退出，没连上就偶尔重试
        if (!_hwi.Ready && (++_hwiRetry % 20) == 0) _hwi.TryOpen();

        _cpu.Sample();
        var gpu = _nvml.Read();
        gpu.VoltV = ReadGpuVolt();

        var fps = _fps;

        // PresentMon 子进程没起来时，退而用 HWiNFO 自带的 PresentMon 集成数据
        double hwFps = double.NaN;
        if (fps.Fps <= 0 && _hwi.Ready) hwFps = _hwi.Find("framerate", "presented");

        Lines.Clear();

        foreach (var key in _cfg.Visible)
        {
            string label, value;
            switch (key)
            {
                case "fps":
                    label = "帧数";
                    double fv = fps.Fps > 0 ? fps.Fps : hwFps;
                    value = !double.IsNaN(fv) && fv > 0 ? $"{fv:F0}" : "--";
                    break;
                case "frametime":
                    label = "帧时间";
                    double fm = fps.FrameMs > 0 ? fps.FrameMs
                              : (!double.IsNaN(hwFps) && hwFps > 0 ? 1000.0 / hwFps : 0);
                    value = fm > 0 ? $"{fm:F1} ms" : "--";
                    break;
                case "fps1low":    label = "1% Low";    value = fps.Fps1Low > 0 ? $"{fps.Fps1Low:F0}" : "--"; break;

                case "gpu.load":   label = "GPU 占用";  value = $"{gpu.LoadPct}%"; break;
                case "gpu.temp":   label = "GPU 温度";  value = $"{gpu.TempC}°C"; break;
                case "gpu.core":   label = "GPU 频率";  value = $"{gpu.CoreMHz} MHz"; break;
                case "gpu.memclk": label = "显存频率";  value = $"{gpu.MemMHz} MHz"; break;
                case "gpu.volt":   label = "GPU 电压";  value = FormatVolt(gpu.VoltV); break;
                case "gpu.power":  label = "GPU 功耗";  value = $"{gpu.PowerW:F0} W"; break;
                case "gpu.fan":    label = "风扇转速";  value = $"{gpu.FanPct}%"; break;
                case "gpu.vram":   label = "显存占用";
                                   value = gpu.VramTotalMB > 0
                                         ? $"{gpu.VramUsedMB / 1024.0:F1}/{gpu.VramTotalMB / 1024.0:F0} GB"
                                         : "--";
                                   break;

                case "cpu.load":   label = "CPU 占用";  value = $"{_cpu.CpuLoadPct:F0}%"; break;
                case "cpu.clock":  label = "CPU 频率";
                                   value = _cpu.CpuMHz > 0 ? $"{_cpu.CpuMHz / 1000.0:F2} GHz" : "--";
                                   break;
                case "cpu.temp":   label = "CPU 温度";  value = FormatTemp(ReadCpuTemp()); break;
                case "cpu.power":  label = "CPU 功耗";  value = FormatWatt(ReadCpuPower()); break;

                case "ram.load":   label = "内存占用";
                                   value = _cpu.RamTotalGB > 0
                                         ? $"{_cpu.RamUsedGB:F1}/{_cpu.RamTotalGB:F0} GB"
                                         : "--";
                                   break;

                case "time":
                    label = "运行时长";
                    var t = DateTime.Now - _started;
                    value = $"{(int)t.TotalHours:D2}:{t.Minutes:D2}:{t.Seconds:D2}";
                    break;

                default: label = key; value = "--"; break;
            }

            Lines.Add((label, value));
        }

        DiagGpu = _nvml.Ready
            ? $"{_nvml.GpuName}  显存 {gpu.VramTotalMB / 1024.0:F0} GB"
            : "NVML 不可用：" + _nvml.LastError;
        DiagHwInfo = _hwi.Status;
        DiagFps = _fps.Status;
    }

    string _lastTarget = "";
    DateTime _lastFgAt = DateTime.MinValue;

    string ResolveTarget()
    {
        var fixedName = _cfg.GameProcess;
        if (!string.IsNullOrWhiteSpace(fixedName)) return fixedName.Trim();

        string fg = ForegroundProcessName();
        if (fg.Length > 0)
        {
            _lastTarget = fg;
            _lastFgAt = DateTime.Now;
            return fg;
        }

        // 短暂切到桌面/弹窗时保持上一次的目标，避免帧率一闪一闪
        if (_lastTarget.Length > 0 && (DateTime.Now - _lastFgAt).TotalSeconds < 5)
            return _lastTarget;

        return "";
    }

    /// <summary>取当前前台窗口所属进程名（形如 cs2.exe）。排除自己和资源管理器。</summary>
    static string ForegroundProcessName()
    {
        try
        {
            IntPtr h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero) return "";

            Native.GetWindowThreadProcessId(h, out uint pid);
            if (pid == 0) return "";

            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            string n = p.ProcessName;

            if (n.Equals("HrOsd", StringComparison.OrdinalIgnoreCase)) return "";
            if (n.Equals("explorer", StringComparison.OrdinalIgnoreCase)) return "";
            if (n.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)) return "";

            return n + ".exe";
        }
        catch { return ""; }
    }

    double ReadGpuVolt()
    {
        if (!_hwi.Ready) return -1;

        double v = _hwi.FindFirst(
            new[] { "gpu", "core", "voltage" },
            new[] { "gpu", "核心", "电压" },
            new[] { "gpu", "vdd" },
            new[] { "gpu", "voltage" },
            new[] { "gpu", "电压" }
        );
        if (double.IsNaN(v) || v <= 0) return -1;

        // HWiNFO 的配置里可能给 V 也可能给 mV
        if (v < 10) v *= 1000.0;

        // 核心电压合理区间 600~1500 mV，超出说明匹配到了输入电压之类的项
        if (v < 300 || v > 2000) return -1;
        return v;
    }

    double ReadCpuTemp()
    {
        if (!_hwi.Ready) return double.NaN;
        double v = _hwi.FindFirst(
            new[] { "cpu", "tctl" },
            new[] { "cpu", "tdie" },
            new[] { "cpu", "package" },
            new[] { "cpu", "封装" },
            new[] { "cpu", "温度" },
            new[] { "cpu", "temperature" }
        );
        if (double.IsNaN(v) || v <= 0 || v > 150) return double.NaN;
        return v;
    }

    double ReadCpuPower()
    {
        if (!_hwi.Ready) return double.NaN;
        double v = _hwi.FindFirst(
            new[] { "cpu", "package", "power" },
            new[] { "cpu", "封装", "功耗" },
            new[] { "cpu", "ppt" },
            new[] { "cpu", "power" },
            new[] { "cpu", "功耗" }
        );
        if (double.IsNaN(v) || v <= 0 || v > 1000) return double.NaN;
        return v;
    }

    static string FormatVolt(double mv) => mv > 0 ? $"{mv:F0} mV" : "N/A";
    static string FormatTemp(double c) => double.IsNaN(c) ? "N/A" : $"{c:F0}°C";
    static string FormatWatt(double w) => double.IsNaN(w) ? "N/A" : $"{w:F0} W";

    public void Dispose()
    {
        try { _nvml.Dispose(); } catch { }
        try { _hwi.Dispose(); } catch { }
        try { _cpu.Dispose(); } catch { }
        try { _fps.Dispose(); } catch { }
    }
}
