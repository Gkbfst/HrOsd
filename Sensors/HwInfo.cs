using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace HrOsd.Sensors;

/// <summary>
/// 读取 HWiNFO 的传感器共享内存。
/// NVML 没有电压接口，所以 GPU 电压 / CPU 温度 / CPU 功耗 只能从这里拿。
///
/// 兼容性要点：HWiNFO 的结构体是否 pack(1) 在不同版本/构建下并不统一。
/// 这里不做假设，而是把两种可能布局的字段都读出来，
/// 用「偏移 + 元素大小 × 元素数 是否落在映射范围内」做校验，
/// 再用「第一个元素的 label 是否是可读文本」做最终裁决。
/// </summary>
internal sealed class HwInfo : IDisposable
{
    static readonly string[] Names =
    {
        @"Global\HWiNFO_SENS_SM2",
        @"Global\HWiNFO_SENS_SM",
    };

    const uint SIGNATURE = 0x53695748; // 'HWiS'

    // Reading 元素内字段偏移（两种对齐下都一样，因为前面没有 8 字节类型）
    const int OFF_LABEL_ORIG  = 12;   // int tReading + uint sensorIndex + uint readingId
    const int OFF_LABEL_USER  = 140;  // 12 + 128
    const int OFF_UNIT        = 268;  // 140 + 128
    const int TAIL_SIZE       = 32;   // 4 个 double: Value / Min / Max / Avg

    MemoryMappedFile _mmf;
    MemoryMappedViewAccessor _view;
    string _openedName = "";

    readonly Dictionary<string, long> _index = new(StringComparer.OrdinalIgnoreCase);
    readonly List<(string Label, string Unit)> _catalog = new();
    long _size;

    public bool Ready { get; private set; }
    public string Status { get; private set; } = "未连接";
    string _lastReported = "";

    static void Log(string s)
    {
        try { Program.Log("    [HWiNFO] " + s); } catch { }
    }

    public bool TryOpen()
    {
        Close();

        foreach (var name in Names)
        {
            try
            {
                _mmf = MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.Read);
                _openedName = name;
                break;
            }
            catch (Exception ex)
            {
                if (name == Names[^1])
                {
                    Status = "共享内存不存在（" + ex.GetType().Name + "）";
                    Report("打开失败：" + ex.Message);
                    return false;
                }
            }
        }

        try
        {
            _view = _mmf.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
            _size = _view.Capacity;
            Log($"已打开 {_openedName}，映射 {_size} 字节");

            if (_size < 64) { Status = "共享内存过小"; Report(Status); return false; }

            uint sig = _view.ReadUInt32(0);
            uint ver = _view.ReadUInt32(4);
            uint rev = _view.ReadUInt32(8);
            Log($"签名 0x{sig:X8} 版本 {ver} 修订 {rev}");

            if (sig != SIGNATURE)
            {
                Status = "签名不匹配";
                Report($"签名不匹配：0x{sig:X8}，期望 0x{SIGNATURE:X8}");
                return false;
            }

            // 布局 A：pack(1)，header 共 44 字节
            long offA  = _view.ReadUInt32(32);
            long sizeA = _view.ReadUInt32(36);
            long numA  = _view.ReadUInt32(40);

            // 布局 B：默认对齐（time_t 前补 4 字节），header 共 48 字节
            long offB  = _view.ReadUInt32(36);
            long sizeB = _view.ReadUInt32(40);
            long numB  = _view.ReadUInt32(44);

            Log($"布局A(紧凑): offRead={offA} sizeRead={sizeA} numRead={numA}");
            Log($"布局B(对齐): offRead={offB} sizeRead={sizeB} numRead={numB}");

            bool okA = Valid(offA, sizeA, numA);
            bool okB = Valid(offB, sizeB, numB);

            long off, size, num;
            string pick;

            if (okA && !okB)      { pick = "A"; off = offA; size = sizeA; num = numA; }
            else if (okB && !okA) { pick = "B"; off = offB; size = sizeB; num = numB; }
            else if (okA && okB)
            {
                int sA = LabelScore(offA + OFF_LABEL_ORIG);
                int sB = LabelScore(offB + OFF_LABEL_ORIG);
                Log($"两种布局都通过范围校验，label 可读性 A={sA} B={sB}");
                if (sA >= sB) { pick = "A"; off = offA; size = sizeA; num = numA; }
                else          { pick = "B"; off = offB; size = sizeB; num = numB; }
            }
            else
            {
                Status = "header 布局无法识别";
                Report("两种布局都没通过范围校验，请把上面的日志发给开发者");
                return false;
            }

            int valueOff = ProbeValueOffset(off, size, num);
            if (valueOff <= OFF_UNIT + 8)
            {
                Status = "元素尺寸不符";
                Report($"无法确定 Value 偏移（元素 {size} 字节）");
                return false;
            }

            Log($"采用布局 {pick}：元素 {size} 字节，起点 {off}，共 {num} 项，Value 偏移 {valueOff}");

            _index.Clear();
            _catalog.Clear();

            var buf = new byte[(int)Math.Min(size, 512)];

            for (long i = 0; i < num; i++)
            {
                long baseOff = off + i * size;
                if (baseOff + size > _size) break;

                int need = (int)Math.Min(buf.Length, size);
                _view.ReadArray<byte>(baseOff, buf, 0, need);

                string lOrig = CStr(buf, OFF_LABEL_ORIG, 128);
                string lUser = CStr(buf, OFF_LABEL_USER, 128);
                string unit  = CStr(buf, OFF_UNIT, 16);
                string key = string.IsNullOrWhiteSpace(lUser) ? lOrig : lUser;
                if (string.IsNullOrWhiteSpace(key)) continue;

                string norm = key.Trim().ToLowerInvariant();
                if (!_index.ContainsKey(norm))
                {
                    _index[norm] = baseOff + valueOff;
                    _catalog.Add((key.Trim(), unit));
                }
            }

            Ready = true;
            Status = $"已连接（布局{pick}），{_catalog.Count} 项";
            Report(Status);

            int shown = 0;
            foreach (var c in _catalog)
            {
                var low = c.Label.ToLowerInvariant();
                if (low.Contains("gpu") || low.Contains("cpu") ||
                    low.Contains("volt") || low.Contains("电压") ||
                    low.Contains("framerate") || low.Contains("帧率"))
                {
                    Log("  传感器: " + c.Label + "  [" + c.Unit + "]");
                    if (++shown >= 45) break;
                }
            }
            if (shown == 0) Log("  ！没有匹配到含 gpu/cpu/volt/电压 字样的传感器");

            return true;
        }
        catch (Exception ex)
        {
            Status = "解析失败（" + ex.GetType().Name + "）";
            Report("解析异常：" + ex.Message);
            Ready = false;
            return false;
        }
    }

    /// <summary>
    /// 结构本体是 320 字节（默认对齐，Value@288）或 316 字节（pack(1)，Value@284），
    /// 但 sizeReadingElement 可能是 460 这种带保留位的值，不能拿「元素末尾往前 32 字节」去推。
    /// 这里先用已知语义的项（CPU 温度应落在 15~110）校准，再退回统计法。
    /// </summary>
    int ProbeValueOffset(long off, long size, long num)
    {
        long limit = Math.Min(num, 80);
        for (long i = 0; i < limit; i++)
        {
            long b = off + i * size;
            if (b + size > _size) break;

            var lb = new byte[128];
            _view.ReadArray<byte>(b + OFF_LABEL_ORIG, lb, 0, 128);
            string label = CStr(lb, 0, 128).ToLowerInvariant();
            if (label.Length == 0) continue;

            if (label.Contains("tctl") || label.Contains("tdie"))
            {
                double a = SafeRead(b + 284);
                double c = SafeRead(b + 288);
                Log($"  校准项 [{label}] : @284={a:0.###}  @288={c:0.###}");
                bool okA = a > 15 && a < 110;
                bool okC = c > 15 && c < 110;
                if (okA && !okC) return 284;
                if (okC && !okA) return 288;
            }
        }

        int best = 288, bestScore = -1;
        foreach (int cand in new[] { 288, 284 })
        {
            int ok = 0, total = 0;
            long n = Math.Min(num, 120);
            for (long i = 0; i < n; i++)
            {
                long p = off + i * size + cand;
                if (p + 8 > _size) break;
                double v = SafeRead(p);
                total++;
                if (!double.IsNaN(v) && Math.Abs(v) > 1e-9 && Math.Abs(v) < 1e7) ok++;
            }
            if (total == 0) continue;
            int score = ok * 100 / total;
            Log($"  候选 Value 偏移 {cand}：合理率 {score}% ({ok}/{total})");
            if (score > bestScore) { bestScore = score; best = cand; }
        }
        return best;
    }

    double SafeRead(long pos)
    {
        try
        {
            if (pos < 0 || pos + 8 > _size) return double.NaN;
            double v = _view.ReadDouble(pos);
            if (double.IsNaN(v) || double.IsInfinity(v)) return double.NaN;
            return v;
        }
        catch { return double.NaN; }
    }

    bool Valid(long off, long size, long num)
    {
        if (off <= 0 || off >= _size) return false;
        if (size < 200 || size > 2000) return false;
        if (num < 1 || num > 20000) return false;
        try { return off + size * num <= _size; }
        catch { return false; }
    }

    /// <summary>给指定位置的可读文本打分（0~100），用于判断哪个布局是对的。</summary>
    int LabelScore(long labelOff)
    {
        try
        {
            if (labelOff < 0 || labelOff + 128 > _size) return 0;
            var b = new byte[128];
            _view.ReadArray<byte>(labelOff, b, 0, 128);

            int total = 0, printable = 0;
            for (int i = 0; i < b.Length; i++)
            {
                if (b[i] == 0) break;
                total++;
                if (b[i] >= 0x20) printable++;   // UTF-8 的高位字节也算可打印
            }
            if (total < 2) return 0;
            return (int)(100.0 * printable / total);
        }
        catch { return 0; }
    }

    void Report(string msg)
    {
        if (_lastReported == msg) return;
        _lastReported = msg;
        Log(msg);
    }

    static readonly UTF8Encoding StrictUtf8 = new(false, true);
    static Encoding _gbk = Encoding.UTF8;

    static HwInfo()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _gbk = Encoding.GetEncoding(936);   // 简体中文 ANSI
        }
        catch { _gbk = Encoding.UTF8; }
    }

    /// <summary>
    /// HWiNFO 的 label 编码并不统一：纯 ASCII 和 UTF-8 混着 GBK。
    /// 先用严格 UTF-8 解，失败再用 GBK，否则中文会变成一串 �。
    /// </summary>
    static string CStr(byte[] buf, int off, int len)
    {
        int end = off;
        int max = Math.Min(off + len, buf.Length);
        while (end < max && buf[end] != 0) end++;
        if (end <= off) return "";
        int n = end - off;

        try { return StrictUtf8.GetString(buf, off, n).Trim(); }
        catch { }

        try { return _gbk.GetString(buf, off, n).Trim(); }
        catch { return ""; }
    }

    public double Find(params string[] keywords)
    {
        if (!Ready) return double.NaN;
        foreach (var kv in _index)
        {
            bool ok = true;
            foreach (var k in keywords)
            {
                if (kv.Key.IndexOf(k, StringComparison.OrdinalIgnoreCase) < 0) { ok = false; break; }
            }
            if (!ok) continue;

            try
            {
                double v = _view.ReadDouble(kv.Value);
                if (double.IsNaN(v) || double.IsInfinity(v)) continue;
                return v;
            }
            catch { }
        }
        return double.NaN;
    }

    public double FindFirst(params string[][] ruleSets)
    {
        foreach (var set in ruleSets)
        {
            double v = Find(set);
            if (!double.IsNaN(v)) return v;
        }
        return double.NaN;
    }

    public IEnumerable<string> Labels => _index.Keys;

    public string DumpCatalog(string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("HWiNFO 传感器清单  导出时间 " + DateTime.Now);
        sb.AppendLine("状态: " + Status + "   共 " + _catalog.Count + " 项");
        sb.AppendLine();
        int i = 1;
        foreach (var c in _catalog)
        {
            string v = "?";
            try
            {
                var kv = _index[c.Label.ToLowerInvariant()];
                v = _view.ReadDouble(kv).ToString("0.###");
            }
            catch { }
            sb.AppendLine($"{i,4}. {c.Label}  [{c.Unit}]  = {v}");
            i++;
        }
        try { File.WriteAllText(path, sb.ToString(), Encoding.UTF8); return path; }
        catch (Exception ex) { return "导出失败：" + ex.Message; }
    }

    void Close()
    {
        try { _view?.Dispose(); } catch { }
        try { _mmf?.Dispose(); } catch { }
        _view = null;
        _mmf = null;
        _index.Clear();
        _catalog.Clear();
        Ready = false;
    }

    public void Dispose() => Close();
}
