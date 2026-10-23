using System.Diagnostics;
using System.Globalization;

namespace HrOsd.Sensors;

/// <summary>
/// 通过 PresentMon 子进程读取帧率。
///
/// 通用性设计：不再用 --process_name 锁定单一进程，而是让 PresentMon 捕获所有进程的
/// Present 事件，再按「当前前台窗口的进程」过滤。这样切换任何游戏都能自动跟上，
/// 不需要为每个游戏改配置。
///
/// 也可以在配置里填 GameProcess 固定跟踪某个进程（留空 = 自动跟随前台）。
/// </summary>
internal sealed class FpsSource : IDisposable
{
    // 不带 process_name：捕获所有进程。--terminate_on_proc_exit 在这种模式下会立即退出，所以不加。
    static readonly string[] ArgTemplates =
    {
        "--output_stdout --stop_existing_session",
        "-output_stdout -stop_existing_session",
    };

    Process _proc;
    Thread _reader;
    volatile bool _stop;

    readonly object _lock = new();
    readonly Queue<double> _frames = new();   // 当前目标进程最近的 MsBetweenPresents
    const int Window = 300;

    int _msCol = -1;
    int _appCol = -1;
    int _argVariant;

    volatile string _target = "";

    public double Fps { get; private set; }
    public double FrameMs { get; private set; }
    public double Fps1Low { get; private set; }
    public string Target => _target;

    string _status = "未启动";
    public string Status
    {
        get => _status;
        private set
        {
            if (_status == value) return;
            _status = value;
            try { Program.Log("    [FPS] " + value); } catch { }
        }
    }

    public bool Connected => _proc != null && !_proc.HasExited;

    string _exePath = "presentmon.exe";
    string _failedArgs = "";

    public void Start(string exePath)
    {
        _stop = false;
        _exePath = exePath;
        _reader = new Thread(Loop) { IsBackground = true, Name = "fps-source" };
        _reader.Start();
    }

    /// <summary>切换跟踪目标。传空字符串表示暂无目标（如当前在桌面）。</summary>
    public void SetTarget(string name)
    {
        name ??= "";
        if (string.Equals(_target, name, StringComparison.OrdinalIgnoreCase)) return;

        _target = name;
        lock (_lock)
        {
            _frames.Clear();
            Fps = 0;
            FrameMs = 0;
            Fps1Low = 0;
        }
        Program.Log("    [FPS] 跟踪目标 -> " + (name.Length == 0 ? "(无)" : name));
    }

    void Loop()
    {
        while (!_stop)
        {
            if (_proc == null || _proc.HasExited) TryStart();

            for (int i = 0; i < 30 && !_stop; i++) Thread.Sleep(100); // 3 秒巡检
        }
    }

    void TryStart()
    {
        try { _proc?.Dispose(); } catch { }
        _proc = null;

        for (int v = _argVariant; v < ArgTemplates.Length; v++)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = _exePath,
                    Arguments = ArgTemplates[v],
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                var p = Process.Start(psi);
                if (p == null) continue;

                _argVariant = v;
                _failedArgs = "";
                _proc = p;
                Status = "已启动，等待数据…";

                var t = new Thread(() => Pump(p)) { IsBackground = true };
                t.Start();
                return;
            }
            catch (Exception ex)
            {
                Status = "启动失败：" + ex.GetType().Name + "（presentmon.exe 放在程序目录了吗）";
            }
        }
    }

    void Pump(Process p)
    {
        try
        {
            string line;
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                if (_stop) break;
                ParseLine(line);
            }
        }
        catch { }

        try
        {
            var err = p.StandardError.ReadLine();
            if (!string.IsNullOrWhiteSpace(err))
            {
                _failedArgs = err.Trim();
                Status = "PresentMon: " + _failedArgs;
            }
        }
        catch { }

        if (!_stop) Status = "进程已退出";
    }

    void ParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        // 表头行：定位 Application 与 MsBetweenPresents 两列
        if (line.IndexOf("MsBetweenPresents", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            var cols = line.Split(',');
            _msCol = -1;
            _appCol = -1;
            for (int i = 0; i < cols.Length; i++)
            {
                var c = cols[i].Trim();
                if (c.Equals("MsBetweenPresents", StringComparison.OrdinalIgnoreCase)) _msCol = i;
                if (c.Equals("Application", StringComparison.OrdinalIgnoreCase)) _appCol = i;
            }
            Program.Log($"    [FPS] 表头解析：Application 列={_appCol}，MsBetweenPresents 列={_msCol}");
            return;
        }

        if (_msCol < 0) return;

        string target = _target;
        if (target.Length == 0) return;   // 当前没有目标进程，不统计

        // 廉价预过滤：整行不含目标名就直接丢掉，避免对每帧都做 Split
        if (line.IndexOf(target, StringComparison.OrdinalIgnoreCase) < 0) return;

        var parts = line.Split(',');
        if (parts.Length <= _msCol) return;

        // 只收目标进程的行
        if (_appCol >= 0 && parts.Length > _appCol)
        {
            string app = parts[_appCol].Trim();
            if (app.IndexOf(target, StringComparison.OrdinalIgnoreCase) < 0) return;
        }

        if (!double.TryParse(parts[_msCol].Trim(), NumberStyles.Float,
                CultureInfo.InvariantCulture, out double ms))
            return;
        if (ms <= 0 || ms > 10000) return;

        lock (_lock)
        {
            _frames.Enqueue(ms);
            while (_frames.Count > Window) _frames.Dequeue();
            Recalc();
        }
    }

    void Recalc()
    {
        if (_frames.Count == 0) return;

        double sum = 0;
        foreach (var v in _frames) sum += v;
        double avg = sum / _frames.Count;

        FrameMs = avg;
        Fps = avg > 0 ? 1000.0 / avg : 0;

        if (_frames.Count >= 100)
        {
            var arr = _frames.ToArray();
            Array.Sort(arr);
            int n = Math.Max(1, (int)Math.Round(arr.Length * 0.01));
            double slowSum = 0;
            for (int i = arr.Length - n; i < arr.Length; i++) slowSum += arr[i];
            double slowAvg = slowSum / n;
            Fps1Low = slowAvg > 0 ? 1000.0 / slowAvg : 0;
        }
        else Fps1Low = 0;
    }

    public void Dispose()
    {
        _stop = true;
        try { _proc?.Kill(true); } catch { }
        try { _proc?.Dispose(); } catch { }
        try { _reader?.Join(800); } catch { }
    }
}
