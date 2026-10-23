using System.Management;

namespace HrOsd.Sensors;

/// <summary>CPU 占用 / CPU 频率 / 内存占用。全部用户态读取，无驱动依赖。</summary>
internal sealed class CpuMem : IDisposable
{
    ulong _prevIdle, _prevKernel, _prevUser;
    bool _primed;

    Thread _clockThread;
    volatile bool _stop;

    public double CpuLoadPct { get; private set; }
    public double CpuMHz { get; private set; } = -1;
    public double RamUsedGB { get; private set; }
    public double RamTotalGB { get; private set; }
    public double RamPct { get; private set; }

    public void Start()
    {
        // 频率用 WMI 查，比较慢，单独线程低频刷新
        _stop = false;
        _clockThread = new Thread(ClockLoop) { IsBackground = true, Name = "cpu-clock" };
        _clockThread.Start();
    }

    void ClockLoop()
    {
        // 基础频率只取一次（Win32_Processor.MaxClockSpeed 是标称值，稳定不变）
        double baseMHz = BaseClockMHz();
        Program.Log($"    [CPU] 基础频率 {baseMHz:0} MHz");

        while (!_stop)
        {
            try
            {
                // PercentProcessorPerformance 是当前性能相对基础频率的百分比，
                // 任务管理器显示的速度就是这个算法：base × pct / 100
                double pct = ReadPerformancePercent();
                if (pct > 0 && baseMHz > 0)
                {
                    double mhz = baseMHz * pct / 100.0;
                    // 合理区间：基础频率的 30% ~ 200%
                    if (mhz > baseMHz * 0.3 && mhz < baseMHz * 2.0) CpuMHz = mhz;
                }
            }
            catch { }

            for (int i = 0; i < 20 && !_stop; i++) Thread.Sleep(100); // 2 秒
        }
    }

    /// <summary>取标称基础频率（MHz）。</summary>
    static double BaseClockMHz()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT MaxClockSpeed FROM Win32_Processor");
            double sum = 0;
            int n = 0;
            foreach (ManagementObject mo in searcher.Get())
            {
                try
                {
                    var v = mo["MaxClockSpeed"];
                    if (v != null) { sum += Convert.ToDouble(v); n++; }
                }
                catch { }
                mo.Dispose();
            }
            if (n > 0 && sum > 0) return sum / n;
        }
        catch { }

        // 兜底：注册表里的 ~MHz
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var v = key?.GetValue("~MHz");
            if (v != null) return Convert.ToDouble(v);
        }
        catch { }

        return 0;
    }

    /// <summary>
    /// 读 % Processor Performance。用 Win32_PerfFormattedData 而不是本地化的
    /// PerformanceCounter，避免中文系统上计数器名对不上。
    /// </summary>
    static double ReadPerformancePercent()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "SELECT Name, PercentProcessorPerformance FROM Win32_PerfFormattedData_Counters_ProcessorInformation");

            double total = double.NaN;
            double sum = 0;
            int n = 0;

            foreach (ManagementObject mo in searcher.Get())
            {
                try
                {
                    var name = mo["Name"] as string ?? "";
                    var v = mo["PercentProcessorPerformance"];
                    if (v == null) continue;

                    double d = Convert.ToDouble(v);
                    if (name.IndexOf("_Total", StringComparison.OrdinalIgnoreCase) >= 0) total = d;
                    else { sum += d; n++; }
                }
                catch { }
                mo.Dispose();
            }

            if (!double.IsNaN(total)) return total;
            if (n > 0) return sum / n;
        }
        catch { }
        return 0;
    }

    /// <summary>每次采样调用。CPU 占用需要两次采样求差，第一次调用只做基线。</summary>
    public void Sample()
    {
        // CPU 占用
        if (Native.GetSystemTimes(out var idle, out var kern, out var user))
        {
            ulong i = idle.ToUInt64();
            ulong k = kern.ToUInt64();
            ulong u = user.ToUInt64();

            if (_primed)
            {
                ulong di = i - _prevIdle;
                ulong dk = k - _prevKernel;
                ulong du = u - _prevUser;
                ulong total = dk + du;              // 内核时间已包含空闲时间
                if (total > 0)
                {
                    double busy = (double)(total - di) / total * 100.0;
                    if (busy < 0) busy = 0;
                    if (busy > 100) busy = 100;
                    CpuLoadPct = busy;
                }
            }
            _prevIdle = i; _prevKernel = k; _prevUser = u;
            _primed = true;
        }

        // 内存
        var ms = new Native.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<Native.MEMORYSTATUSEX>() };
        if (Native.GlobalMemoryStatusEx(ref ms))
        {
            RamTotalGB = ms.ullTotalPhys / 1024.0 / 1024.0 / 1024.0;
            double availGB = ms.ullAvailPhys / 1024.0 / 1024.0 / 1024.0;
            RamUsedGB = RamTotalGB - availGB;
            RamPct = ms.dwMemoryLoad;
        }
    }

    public void Dispose()
    {
        _stop = true;
        try { _clockThread?.Join(1500); } catch { }
    }
}
