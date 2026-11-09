# <img src="../desktop/AirFlash.App/Assets/app.svg" width="36" height="36" alt="" style="vertical-align:middle" /> Airplay

<p align="center">
  <a href="../README.md">English</a> ·
  <a href="README.zh-CN.md">简体中文</a>
</p>

<p align="center">
  <img src="images/airflash-zh-CN.png" alt="Airplay 中文界面" width="550" />
</p>

Airplay 支持现有的双 HomePod 立体声组合和 Windows 低延迟播放。
通过原生 AirPlay 2 发送端，将 Windows 桌面声音发送到 HomePod，支持 HomePod OS 27。

Airplay 是 TuneBlade（AirPlay 1）的 AirPlay 2 替代方案，旨在让 Windows
能够向运行较新版本 HomePod 软件的设备流送音频。

## 安装

从 [GitHub Releases](https://github.com/weixuanng/AirFlash_Wei/releases/latest) 下载并运行 `Airplay-X.Y.Z.msi` 安装，或直接运行便携版 `Airplay.exe`。

最低兼容目标为 **Windows x64，Windows 10 1809 / Build 17763（LTSC 2019）**，
同时覆盖 LTSC 2021、22H2 和 Windows 11。当前仍为兼容候选版本，需受影响用户实机验收。
启动失败时可使用 Windows 自带 PowerShell 5.1 收集报告；步骤与验收范围见
[Windows 兼容性与启动诊断](WINDOWS-COMPATIBILITY.md)。程序自包含，无需另装 .NET、Python 或 VC++ 运行库。

Airplay 提供 Windows x64 可执行文件和可独立运行的 MSI 安装包。由于发布文件没有代码签名，Windows
可能显示 SmartScreen 警告。只有在确认下载文件来源可信时，才使用“更多信息”
和“仍要运行”。

Airplay 从默认播放设备采集 Windows 系统声音（回环）。
要只投放某个应用的声音，可以下载安装 [Virtual Audio Cable（VAC Lite）](https://software.muzychenko.net/freeware/vac471lite.zip)
等工具创建虚拟端点，将该应用的输出路由到虚拟端点，并将其设为默认播放设备。

## 主要功能

- 通过 AirPlay 2 将 Windows 系统声音发送到 HomePod。
- 支持现有双 HomePod 立体声组合的同步播放。
- 新增实验性双接收端软件立体声、独立硬件音量、按设备 PCM/ALAC 与 UDP/TCP 设置，以及 5 ms 音频连续性过渡。
- 支持自动发现设备。
- 提供托盘运行、静音恢复、自动重连和连接诊断。延迟固定为实时模式（120 ms）。
- 从默认播放设备采集 Windows 系统声音，包括虚拟音频设备。
- 会话在静音期间保持连接，短暂断网或被其他设备抢占后自动恢复。
- 提供便携式 Windows x64 程序，支持英文、简体中文、繁体中文和德文界面。

## 功能计划

设置方法、schema v4、协议与测试范围见 [音频连续性与软件立体声](audio-stereo-v3.md)。
三星、Mac 和跨品牌组的实际出声、防爆音与声学同步仍待实机验收。

- [ ] 支持杜比 Atmos。

## 使用方法

1. 将电脑和两台 HomePod 连接到同一可互通的局域网。
2. 启动 Airplay，等待接收端列表出现。
3. 点击“连接”连接到 HomePod。

延迟固定为实时模式（120 ms），为最快选项，没有延迟设置。引擎按接收器 `/info` 能力自动协商流格式（编码、采样率、UDP/TCP 传输和时钟策略），优先 44.1 kHz。这些调度数值并非实测端到端声音延迟。

停止播放或取消连接后，按钮立即显示“正在断开”，旧会话清理完成后恢复播放操作。
退出时程序继续读取引擎收尾输出，避免管道写满阻塞；停止过程共用两秒引擎退出预算，
超时会结束本次会话的引擎进程。

设备发现在控制面板可见时运行；活动软件立体声组也保持拓扑监听。
控制面板隐藏后暂停发现。
暂停时保留上次结果，不会中断播放。如果启用了全局或单个设备的自动连接，
程序启动后还会扫描 10 秒；之后打开设备列表即可恢复发现。

Airplay 使用全新的应用数据目录 `%APPDATA%/Airplay` 和
`%LOCALAPPDATA%/Airplay`。它不会读取旧产品名称创建的配置、凭据或引擎
缓存；安装 Airplay 后需要重新配置并重新配对。

在托盘菜单（右键托盘图标）中选择“检查更新”，手动查询正式版本并打开下载页；程序不会自动联网检查或安装更新。

实时模式目标延迟为 120 ms；目标值和本机传输统计不代表端到端声学延迟的实测值。

版本预留与发布方法见 [发布流程](RELEASING.md)。

矢量源图、资源生成与高 DPI 验收步骤见 [图标维护](ICONS.md)。

Airplay 通过回环采集默认播放设备，电脑上播放的声音都会投放到 HomePod。
自动本地静音仅适用于播放设备。

### 虚拟音频路由

要让目标应用只在 HomePod 播放，同时让游戏和通知继续使用耳机，可安装
[VB-CABLE](https://vb-audio.com/Cable/) 等虚拟声卡，再通过 **打开 Windows 音量混合器**
仅将目标应用的输出设为 **CABLE Input**，并将 **CABLE Input** 设为默认播放设备。
关闭“侦听此设备”及本地监听。停止 Airplay 后，Windows 路由设置仍保留；
需要本地播放时可自行改回。虚拟声卡不保证采集受 DRM 保护的内容，采集、重采样和格式转换
也不保证逐位无损。

## 技术栈

- **C# / .NET 10 / WPF**：Windows 界面、托盘和设备发现
- **Rust**：WASAPI 采集、AirPlay 2、HAP、PTP、RTP 和音频传输
- **Windows DNS-SD**：接收端发现
- **WASAPI loopback**：系统声音采集
- **PCM 和 ALAC**：通过加密 RTP 发送到 HomePod
- **uv、pytest、Ruff**：限时验证工具和 Python 检查

发布程序自包含，Rust 引擎静态链接 MSVC 运行库，不需要 Python、单独安装 .NET 运行时或单独安装 VC++ Redistributable。

## 开发与构建

开发需要 Windows 10/11 x64、.NET 10 SDK、Rust MSVC 工具链，以及 Visual
Studio C++/Windows SDK 组件。Python 3.12+ 和 `uv` 仅用于验证脚本。

```powershell
uv sync --locked
pwsh scripts/build-native.ps1 -Check
pwsh scripts/dotnet.ps1 restore AirFlash.sln --locked-mode
pwsh scripts/dotnet.ps1 run --project AirFlash.App/AirFlash.App.csproj
pwsh scripts/build-dev.ps1
```

构建生成：

- `dist/dev/Airplay.exe`
- `dist/dev/Airplay-X.Y.Z-dev.<commit>.msi`

开发包以本地最近一个正式 `vX.Y.Z` 标签为基准，并附加当前提交号；它不会保留版本号或推送标签。安装开发 MSI 前，请先卸载已安装的同版本正式包。

只有正式发版时才使用 `pwsh scripts/build-release.ps1`；它会要求干净的发布分支、保留下一个正式版本，并把产物写入 `dist/`。

Rust 引擎会嵌入 WPF 可执行文件，运行时释放到
`%LOCALAPPDATA%/Airplay/engine/<hash>` 内容寻址缓存。

## 测试

```powershell
pwsh scripts/dotnet.ps1 test AirFlash.sln
uv run pytest
uv run ruff check .
pwsh scripts/build-native.ps1 -Check
```

被忽略的 Rust 压力测试只使用 localhost UDP 接收端。真实 HomePod 测试严格
遵守仓库规定的低音量、最长五秒流程。

## 贡献

修改时请明确协议行为、配对边界和测量限制。欢迎提交 PR！

## 许可证

双重许可。Airplay 可依据 GNU 通用公共许可证第 3 版或任何更高版本
（见 [LICENSE-GPLv3](../LICENSE-GPLv3)），或依据 Airplay 商业授权
（见 [LICENSE-COMMERCIAL.md](../LICENSE-COMMERCIAL.md)；联系
https://github.com/Ding-Kyoma/AirFlash/issues）使用。协议实现和依赖声明见
[native/airflash-engine/README.md](../native/airflash-engine/README.md) 与
[desktop/THIRD-PARTY-NOTICES.md](../desktop/THIRD-PARTY-NOTICES.md)。
