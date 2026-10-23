using System.Text.Json;
using System.Text.Json.Serialization;

namespace HrOsd;

/// <summary>所有可显示的数据项定义，顺序即显示顺序。</summary>
public static class ItemCatalog
{
    public record Item(string Key, string Label, string Group);

    public static readonly Item[] All =
    {
        new("fps",        "帧数",         "帧率"),
        new("frametime",  "帧时间",       "帧率"),
        new("fps1low",    "1% Low",       "帧率"),
        new("gpu.load",   "GPU 占用",     "显卡"),
        new("gpu.temp",   "GPU 温度",     "显卡"),
        new("gpu.core",   "GPU 频率",     "显卡"),
        new("gpu.memclk", "显存频率",     "显卡"),
        new("gpu.volt",   "GPU 电压",     "显卡"),
        new("gpu.power",  "GPU 功耗",     "显卡"),
        new("gpu.fan",    "风扇转速",     "显卡"),
        new("gpu.vram",   "显存占用",     "显卡"),
        new("cpu.load",   "CPU 占用",     "处理器"),
        new("cpu.clock",  "CPU 频率",     "处理器"),
        new("cpu.temp",   "CPU 温度",     "处理器"),
        new("cpu.power",  "CPU 功耗",     "处理器"),
        new("ram.load",   "内存占用",     "内存"),
        new("time",       "运行时长",     "其它"),
    };

    public static string LabelOf(string key)
    {
        foreach (var i in All) if (i.Key == key) return i.Label;
        return key;
    }
}

public class Config
{
    // 显示哪些项（key 集合）
    public List<string> Visible { get; set; } = new()
    {
        "fps", "frametime", "gpu.load", "gpu.temp", "gpu.core", "gpu.volt", "gpu.power", "cpu.load", "ram.load"
    };

    // 外观
    public string FontName { get; set; } = "Microsoft YaHei UI";
    public float  FontSize { get; set; } = 11f;
    public bool   FontBold { get; set; } = true;
    public int    TextRgb  { get; set; } = 0xE8E8E8;   // 文字颜色
    public int    BackRgb  { get; set; } = 0x0C0C0C;   // 背景颜色
    public int    Opacity  { get; set; } = 82;         // 0~100

    // 行为
    public bool TopMost      { get; set; } = true;
    public bool ClickThrough { get; set; } = true;
    public int  PosX { get; set; } = 40;
    public int  PosY { get; set; } = 40;
    public int  Padding { get; set; } = 10;
    public int  LineGap { get; set; } = 2;

    // 热键（字符串形式，如 "Ctrl+Alt+O"）
    public string HotkeyToggle   { get; set; } = "Ctrl+Alt+O";
    public string HotkeySettings { get; set; } = "Ctrl+Alt+P";

    // 采集
    public int    SampleMs     { get; set; } = 500;
    public string GameProcess  { get; set; } = "";   // 留空 = 自动跟随当前前台窗口
    public string PresentMonPath { get; set; } = "presentmon.exe";

    // ---------- 持久化 ----------
    [JsonIgnore]
    public static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "HrOsd");

    [JsonIgnore]
    public static string FilePath => Path.Combine(Dir, "config.json");

    static readonly JsonSerializerOptions Opt = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static Config Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var c = JsonSerializer.Deserialize<Config>(json, Opt);
                if (c != null)
                {
                    // 旧版本把进程名写死成 Hellraiser，迁移成自动模式
                    if (c.GameProcess == "Hellraiser-Win64-Shipping") c.GameProcess = "";
                    return c;
                }
            }
        }
        catch { /* 配置坏了就用默认值，不阻塞启动 */ }
        return new Config();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opt));
        }
        catch { }
    }

    public bool IsVisible(string key) => Visible.Contains(key);

    public void SetVisible(string key, bool on)
    {
        if (on)
        {
            if (!Visible.Contains(key))
            {
                // 按 Catalog 顺序插入，保证显示顺序稳定
                var order = Array.FindIndex(ItemCatalog.All, i => i.Key == key);
                int insertAt = Visible.Count;
                for (int i = 0; i < Visible.Count; i++)
                {
                    var oi = Array.FindIndex(ItemCatalog.All, x => x.Key == Visible[i]);
                    if (oi > order) { insertAt = i; break; }
                }
                Visible.Insert(insertAt, key);
            }
        }
        else Visible.Remove(key);
    }
}

/// <summary>
/// Color.FromArgb(int) 会把最高字节当作 alpha，0x0C0C0C 会变成全透明。
/// 统一走这个转换，避免背景/文字“消失”。
/// </summary>
internal static class ColorUtil
{
    public static Color FromRgb(int rgb) =>
        Color.FromArgb(255, (rgb >> 16) & 0xFF, (rgb >> 8) & 0xFF, rgb & 0xFF);
}
