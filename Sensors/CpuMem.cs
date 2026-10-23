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
        while (!_stop)
        {
            try
            {
                using var searcher = new ManagementObjectSearcher(
                    "SELECT CurrentClockSpeed FROM Win32_Processor");
                double sum = 0;
                int n = 0;
                foreach (ManagementObject mo in searcher.Get())
                {
                    try
                    {
                        var v = mo["CurrentClockSpeed"];
                        if (v != null) { sum += Convert.ToDouble(v); n++; }
                    }
                    catch { }
                    mo.Dispose();
                }
                if (n > 0) CpuMHz = sum / n;
            }
            catch { }

            for (int i = 0; i < 50 && !_stop; i++) Thread.Sleep(100); // 5s
        }
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
