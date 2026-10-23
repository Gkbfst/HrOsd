using System.Runtime.InteropServices;
using System.Text;

namespace HrOsd.Sensors;

/// <summary>
/// 通过 NVML（驱动自带的 nvml.dll）读取显卡实时数据。
/// 动态加载 + GetProcAddress，避免 nvml.dll 不在 PATH 时直接崩溃。
/// 注意：NVML 官方 C API 没有电压接口，电压由 HwInfo 提供。
/// </summary>
internal sealed class Nvml : IDisposable
{
    // ---------- 原生结构 ----------
    [StructLayout(LayoutKind.Sequential)]
    struct Util { public uint Gpu; public uint Memory; }

    [StructLayout(LayoutKind.Sequential)]
    struct Mem { public ulong Total; public ulong Free; public ulong Used; }

    // ---------- 委托 ----------
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_Init();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_Shutdown();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_GetHandle(uint index, out IntPtr dev);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_GetName(IntPtr dev, StringBuilder name, uint len);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_GetTemp(IntPtr dev, int sensor, out uint val);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_GetClock(IntPtr dev, int type, out uint val);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_GetPower(IntPtr dev, out uint mw);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_GetUtil(IntPtr dev, out Util u);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_GetMem(IntPtr dev, out Mem m);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int D_GetFan(IntPtr dev, out uint val);

    // NVML 常量
    const int TEMPERATURE_GPU = 0;
    const int CLOCK_SM        = 0;   // 核心
    const int CLOCK_MEM       = 2;   // 显存

    IntPtr _h = IntPtr.Zero;
    IntPtr _dev = IntPtr.Zero;

    D_Init     _init;
    D_Shutdown _shutdown;
    D_GetHandle _getHandle;
    D_GetName  _getName;
    D_GetTemp  _getTemp;
    D_GetClock _getClock;
    D_GetPower _getPower;
    D_GetUtil  _getUtil;
    D_GetMem   _getMem;
    D_GetFan   _getFan;

    public bool Ready { get; private set; }
    public string GpuName { get; private set; } = "GPU";
    public string LastError { get; private set; } = "";

    public bool Init()
    {
        string[] candidates =
        {
            "nvml.dll",
            @"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll",
            @"C:\Windows\System32\nvml.dll",
        };

        foreach (var path in candidates)
        {
            try
            {
                _h = Native.LoadLibrary(path);
                if (_h != IntPtr.Zero) break;
            }
            catch { }
        }

        if (_h == IntPtr.Zero) { LastError = "找不到 nvml.dll（未安装 NVIDIA 驱动？）"; return false; }

        _init     = Fn<D_Init>("nvmlInit_v2") ?? Fn<D_Init>("nvmlInit");
        _shutdown = Fn<D_Shutdown>("nvmlShutdown");
        _getHandle= Fn<D_GetHandle>("nvmlDeviceGetHandleByIndex_v2") ?? Fn<D_GetHandle>("nvmlDeviceGetHandleByIndex");
        _getName  = Fn<D_GetName>("nvmlDeviceGetName");
        _getTemp  = Fn<D_GetTemp>("nvmlDeviceGetTemperature");
        _getClock = Fn<D_GetClock>("nvmlDeviceGetClockInfo");
        _getPower = Fn<D_GetPower>("nvmlDeviceGetPowerUsage");
        _getUtil  = Fn<D_GetUtil>("nvmlDeviceGetUtilizationRates");
        _getMem   = Fn<D_GetMem>("nvmlDeviceGetMemoryInfo");
        _getFan   = Fn<D_GetFan>("nvmlDeviceGetFanSpeed");

        if (_init == null || _getHandle == null) { LastError = "NVML 导出函数缺失"; return false; }
        if (_init() != 0) { LastError = "NVML 初始化失败"; return false; }
        if (_getHandle(0, out _dev) != 0) { LastError = "取不到 GPU 句柄"; return false; }

        try
        {
            var sb = new StringBuilder(96);
            if (_getName != null && _getName(_dev, sb, 96) == 0) GpuName = sb.ToString();
        }
        catch { }

        Ready = true;
        return true;
    }

    T Fn<T>(string name) where T : Delegate
    {
        var p = Native.GetProcAddress(_h, name);
        if (p == IntPtr.Zero) return null;
        try { return Marshal.GetDelegateForFunctionPointer<T>(p); } catch { return null; }
    }

    public GpuReading Read()
    {
        var r = new GpuReading { Name = GpuName };
        if (!Ready) return r;

        try { if (_getUtil != null && _getUtil(_dev, out var u) == 0) r.LoadPct = u.Gpu; } catch { }
        try { if (_getTemp != null && _getTemp(_dev, TEMPERATURE_GPU, out var t) == 0) r.TempC = t; } catch { }
        try { if (_getClock != null && _getClock(_dev, CLOCK_SM, out var c) == 0) r.CoreMHz = c; } catch { }
        try { if (_getClock != null && _getClock(_dev, CLOCK_MEM, out var m) == 0) r.MemMHz = m; } catch { }
        try { if (_getPower != null && _getPower(_dev, out var p) == 0) r.PowerW = p / 1000.0; } catch { }
        try
        {
            if (_getMem != null && _getMem(_dev, out var mm) == 0)
            {
                r.VramUsedMB  = mm.Used  / (1024.0 * 1024.0);
                r.VramTotalMB = mm.Total / (1024.0 * 1024.0);
            }
        }
        catch { }
        try { if (_getFan != null && _getFan(_dev, out var f) == 0) r.FanPct = f; } catch { }

        return r;
    }

    public void Dispose()
    {
        try { _shutdown?.Invoke(); } catch { }
        if (_h != IntPtr.Zero) { try { Native.FreeLibrary(_h); } catch { } _h = IntPtr.Zero; }
        Ready = false;
    }
}

internal sealed class GpuReading
{
    public string Name = "GPU";
    public uint   LoadPct;
    public uint   TempC;
    public uint   CoreMHz;
    public uint   MemMHz;
    public double PowerW;
    public double VramUsedMB;
    public double VramTotalMB;
    public uint   FanPct;
    public double VoltV = -1;   // 由 HwInfo 填充，-1 表示不可用
}
