# HrOsd
一个基于HWinfo的硬件监控覆盖层
[README.md](https://github.com/user-attachments/files/33849293/README.md)
# HrOsd — 不注入游戏的性能覆盖层

## 为什么做这个

RTSS 通过 inline hook 改写 `dxgi.dll` 的机器码来统计帧率，依赖一张硬编码的函数偏移表。
Windows 每次功能更新重新编译 `dxgi.dll`，偏移表作废，`jmp` 就会写进错误位置——
游戏一启动就在 `dxgi.dll` 里 `0xC0000005` 崩掉，没有任何报错提示。

本程序全部走公开接口（NVML / ETW / WMI / Win32），一个字节都不碰游戏的代码。
代价是独占全屏下覆盖层不可见、数据有几十毫秒延迟；换来的是不会因为系统更新而崩。

**Version 1.0.0**　（Windows 10/11 x64 · .NET 8 Desktop Runtime · NVIDIA 显卡）

给《Hellraiser: Revival》这类 UE5 新游戏用的实时监控悬浮窗。

**核心特点：全程不注入游戏进程**，因此不会像 RTSS 那样因为系统 `dxgi.dll` 版本更新而崩溃。

---

## 数据来源

| 数据 | 来源 | 依赖 |
|---|---|---|
| GPU 占用 / 温度 / 核心频率 / 显存频率 / 功耗 / 风扇 / 显存 | **NVML**（显卡驱动自带 `nvml.dll`） | 无 |
| **GPU 电压** | **HWiNFO 共享内存** | 需 HWiNFO 常驻运行 |
| CPU 占用 | `GetSystemTimes` | 无 |
| CPU 频率 | WMI `Win32_Processor` | 无 |
| CPU 温度 / 功耗 | HWiNFO 共享内存 | 需 HWiNFO 常驻运行 |
| 内存占用 | `GlobalMemoryStatusEx` | 无 |
| FPS / 帧时间 / 1% Low | **PresentMon**（ETW 事件追踪） | 需 `presentmon.exe` |

> NVML 官方 C API 里**没有电压接口**，这是 NVIDIA 的功能缺失，不是实现偷懒。
> 所以电压这一项必须靠 HWiNFO 补。HWiNFO 没开时，电压显示 `N/A`，其余数据照常。

---

## 前置准备

### 1. PresentMon（必需）

下载地址：<https://github.com/GameTechDev/PresentMon/releases>

取 `PresentMon-x.x.x-x64.exe`，**改名为 `presentmon.exe`**，放到 `HrOsd.exe` 同目录。

> PresentMon 需要管理员权限才能创建 ETW 内核会话，所以本程序以管理员身份运行。

### 2. HWiNFO（只有电压/CPU温度需要）

1. 装 [HWiNFO](https://www.hwinfo.com/download/)（免费版够用）
2. 打开 → `Settings` → **`General / User Interface`** → 勾选 **Shared Memory Support** → 确定
   （注意：这一项在 General 页，不是 Safety 页）
3. 建议顺便勾上 `Show Sensors on Startup` 和 `Minimize Sensors on Startup`，
   否则 HWiNFO 不开 Sensors 窗口就不会真正去读传感器
4. **务必取消勾选同一个页面上的 `PresentMon Support`**，原因见下
5. 保持 HWiNFO 在后台运行（可最小化到托盘）

> ⚠️ **为什么必须关掉 HWiNFO 的 PresentMon Support**
>
> 它和本程序调用的 `presentmon.exe` **抢同一个 ETW 会话名 "PresentMon"**。
> 两边同时开，会报 `a trace session named "PresentMon" is already running`，
> 帧率数据会断断续续。让 HWiNFO 只做传感器、presentmon 只做帧率，各司其职。

> ⚠️ 免费版 HWiNFO 有 **12 小时运行上限**，到点会自动退出，共享内存随之消失。
> 长时间挂机要么买 Pro，要么定期重启一次 HWiNFO。

### 3. .NET 8 SDK

编译需要，运行不需要（下面用 self-contained 发布可免运行时）。

---

## 编译

```bash
cd HrOsd

# 体积小，但目标机器要装 .NET 8 Desktop Runtime
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true

# 体积大（约 150MB），但拷走就能跑
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
```

产物在 `bin/Release/net8.0-windows/win-x64/publish/HrOsd.exe`。

把 `presentmon.exe` 一起拷进去。

---

## 使用

- 双击 `HrOsd.exe`，UAC 点「是」
- 默认热键 **Ctrl+Alt+O** 显示/隐藏，**Ctrl+Alt+P** 打开设置
- 托盘图标右键也有菜单
- 设置里可以：勾选显示项、换字体、改文字/背景颜色、调不透明度、改热键、置顶开关、鼠标穿透开关、微调位置

配置文件在 `%APPDATA%\HrOsd\config.json`，可以直接改。

---

## 关于「鼠标穿透」

勾选后游戏内点击会直接穿过覆盖层，不会误触。

但穿透状态下**无法拖动窗口**（收不到鼠标事件）。要挪位置：

1. 设置里临时取消穿透 → 拖动 → 再勾回来
2. 或者直接在「位置 X / Y」里填数值

---

## 已知限制

**① 独占全屏下不可见**

游戏以 Exclusive Fullscreen 运行时独占显示输出，DWM 不参与合成，任何外部窗口都盖不上去。这是原理限制，无解。

UE5 默认走 Flip Model 的无边框全屏，**没有这个问题**。如果你的游戏切了独占全屏，改成「无边框窗口」即可。

**② 数据有 10~50ms 延迟**

ETW 是内核事件流，带缓冲。看平均帧率、1% low 完全够用；如果要抓单帧毛刺，精度不如 RTSS。

**③ CPU 温度依赖 HWiNFO**

用户态程序读不到 CPU 核心温度（必须 Ring0 驱动）。所以走 HWiNFO。没开 HWiNFO 时显示 `N/A`。

**④ HWiNFO 结构体解析是探测式的**

HWiNFO 的共享内存结构有 `pack(1)` 和默认对齐两种布局，且 `sizeReadingElement`
可能是 460 这种带保留位的值（结构本体只有 320 字节）。程序会：
先用范围校验 + label 可读性判定布局，再用 CPU 温度做校准确定 Value 偏移。
如果哪天 HWiNFO 又改结构导致读不出值，看 `startup.log` 里 `[HWiNFO]` 那几行即可定位。

**⑤ 中文传感器名有 GBK 回退**

HWiNFO 的 label 混用 UTF-8 和 GBK，纯 UTF-8 解码会把 `GPU 核心电压` 变成乱码。
程序会先试严格 UTF-8，失败后回退 GBK(936)。

**⑥ 只支持 NVIDIA 显卡**

GPU 部分走 NVML。A 卡要改成 ADL，I 卡要改成 IGCL，需要另写。

---

## 为什么不用 RTSS

RTSS 通过 inline hook 改写 `dxgi.dll` 的机器码来统计帧率，它依赖一张硬编码的函数偏移表。Windows 每次功能更新重新编译 `dxgi.dll`，偏移表就作废，然后它会把 `jmp` 写进错误的位置——游戏一启动就在 `dxgi.dll` 里 `0xC0000005` 崩掉。

本程序全部走**公开接口**（NVML / ETW / WMI / Win32），一个字节都不碰游戏的代码，所以不存在这个问题。

---

## 许可证

[MIT](LICENSE)

## 致谢

- [PresentMon](https://github.com/GameTechDev/PresentMon)（Intel）— 基于 ETW 的帧率采集
- [HWiNFO](https://www.hwinfo.com/) — 共享内存传感器数据
- NVML / NVIDIA 驱动 — 显卡实时数据


