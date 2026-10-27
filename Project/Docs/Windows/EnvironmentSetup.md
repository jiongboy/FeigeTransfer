# Windows 原生开发环境搭建

本文档记录飞鸽快传 Windows 客户端（WinUI 3 / Windows App SDK）所需的软件、系统设置和验证步骤。

> 结论先行：仓库里 `Windows项目.md` 和 `Windows开发手册.md` 写的 **Visual Studio 2022 已经过期**。微软当前对 WinUI 3 的官方要求是 **Visual Studio 2026 + 「WinUI 应用程序开发」工作负载 + .NET 10 SDK**。以本文档为准。

## 一、版本基线（2026-10 确认）

| 组件 | 选定版本 | 说明 |
| --- | --- | --- |
| 操作系统 | Windows 11 25H2 (build 26200) | 最低支持 Windows 10 1809 (17763)，产品目标 Win10 22H2 (19045)+ |
| IDE | Visual Studio 2026 Community 18.11.x | 频道 `VisualStudio.18.Release`；VS2022 不再满足 WinUI 3 官方要求 |
| .NET | **.NET 10 SDK (LTS)** | 10.0.402；LTS 服务到 2028-11。**不要用 .NET 11**（2026-11 发布，STS 短期支持） |
| UI 框架 | **Windows App SDK 2.5.1**（stable） | WinAppSDK 2.x 起 NuGet 版本号与 SDK 版本号一致 |
| Windows SDK | 10.0.26100 系列（**非必需**） | TFM 用 `net10.0-windows10.0.26100.0`；`makeappx.exe` / `signtool.exe` 由 NuGet 包 `Microsoft.Windows.SDK.BuildTools` 提供，无需独立安装 SDK |
| 命令行工具 | WinApp CLI 0.7.1 | `winapp new` / `winapp run`，可脱离 VS 独立构建运行 |

### 为什么锁 .NET 10 而不是更高版本

.NET 11 是 2026 年 11 月发布的 **STS** 版本，只有 18 个月支持。这个项目是要装到用户机器上长期运行的工具软件，必须用 LTS。同理锁 WinAppSDK 2.5.1 stable，不用 2.5.4-experimental。

## 二、两条安装路线

微软官方提供两条路线，二者产出的应用一致，区别只在开发体验：

| | 路线 A：完整 IDE | 路线 B：纯 CLI |
| --- | --- | --- |
| 安装体积 | 约 15 GB | 约 1 GB |
| 耗时 | 30–60 分钟 | 约 5 分钟 |
| XAML 设计器 | 有（可视化拖拽） | 无，纯手写 XAML |
| 调试 | 断点、可视化树、热重载 | `dotnet run` + 日志 |
| MSIX 打包 | 项目属性里点一下 | `winapp package` |
| 适合 | 多人协作、UI 复杂、要交付正式安装包 | 快速验证、CI 构建、AI 辅助编码 |

**本项目选定路线 A（完整 IDE）**，因为客户端有传输概览、发送、接收、历史、设置 5 个页面，纯手写 XAML 效率太低。

## 三、一键安装

脚本在 `Project/Windows/scripts/`，全部幂等，重复执行安全。

```powershell
# 以管理员身份打开 PowerShell
cd Project\Windows\scripts

# 环境体检（只读，不改任何东西）
powershell -NoProfile -ExecutionPolicy Bypass -File .\Check-DevEnv.ps1

# 全量安装：开发者模式 + 长路径 + .NET 10 SDK + WinApp CLI + VS 2026 + 网络改专用
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-DevEnv.ps1 -SetPrivateNetwork

# 只要命令行链路，不装 VS
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-DevEnv.ps1 -SkipVisualStudio

# 只看会执行什么，不真装
powershell -NoProfile -ExecutionPolicy Bypass -File .\Install-DevEnv.ps1 -DryRun

# 端到端冒烟测试：建空白工程 → 编译 → 以 MSIX 身份启动 → 验证窗口 → 清理
powershell -NoProfile -ExecutionPolicy Bypass -File .\Test-Toolchain.ps1
```

`Check-DevEnv.ps1` 会用 PASS/WARN/FAIL 表格列出 17 项检查，退出码 0 表示无阻塞项，1 表示还有缺失。

`Test-Toolchain.ps1` 是真正意义上的验收：它不检查「装了什么」，而是真的编译并启动一个 WinUI 应用，验证整条链路可用，结束后自动删除临时工程和调试包注册。CI 上可以直接用它做环境门禁。

## 四、手工安装（脚本对应的原始命令）

### 4.1 基础工具链

```powershell
winget install --id Microsoft.DotNet.SDK.10 --exact --source winget
winget install --id Microsoft.WinAppCli --exact --source winget
```

安装完**必须开新终端**，否则 `dotnet` / `winapp` 不在 PATH 里。

### 4.2 Visual Studio 2026

官方推荐用配置文件一条命令装齐（会自动开开发者模式）：

```powershell
winget configure -f https://aka.ms/winui-config
```

该配置文件的实质内容（已核对）：

```yaml
- resource: Microsoft.WinGet.DSC/WinGetPackage
  settings: { id: Microsoft.VisualStudio.Community, source: winget }
- resource: Microsoft.VisualStudio.DSC/VSComponents
  settings:
    productId: Microsoft.VisualStudio.Product.Community
    channelId: VisualStudio.18.Release
    components:
      - Microsoft.VisualStudio.Workload.ManagedDesktop
      - Microsoft.VisualStudio.Workload.Universal
      - Microsoft.VisualStudio.ComponentGroup.WindowsAppSDK.Cs
```

如果 `winget configure` 因 DSC 模块下载失败不可用，等价手工命令：

```powershell
winget install --id Microsoft.VisualStudio.Community --exact --source winget --silent `
  --override "--quiet --wait --norestart --add Microsoft.VisualStudio.Workload.ManagedDesktop --add Microsoft.VisualStudio.Workload.Universal --add Microsoft.VisualStudio.ComponentGroup.WindowsAppSDK.Cs"
```

`--override` 会替换 winget 的默认参数，直接传给 VS 引导器，因此可以避免装上一堆用不到的默认工作负载（约省 5 GB）。

装完在 VS Installer 里确认 **WinUI 应用程序开发** 工作负载已勾选；没有的话点「修改」补上，然后重启 VS。

### 4.3 系统设置

| 设置 | 位置 | 为什么必须要 |
| --- | --- | --- |
| 开发者模式 | `HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock\AllowDevelopmentWithoutDevLicense = 1` | 侧载未上架商店的 MSIX，F5 调试部署的前提 |
| 长路径支持 | `HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\LongPathsEnabled = 1` | `obj\` 里 MSIX 中间产物路径极深，不开会撞 MAX_PATH (260) |
| 网络配置文件改「专用」 | `Set-NetConnectionProfile -NetworkCategory Private` | **本项目特有**：公用网络下系统防火墙默认丢弃入站，mDNS 发现和传输端口都收不到包 |

回滚：

```powershell
# 关开发者模式
Set-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock' AllowDevelopmentWithoutDevLicense 0
# 网络改回公用
Set-NetConnectionProfile -InterfaceAlias 'WLAN' -NetworkCategory Public
```

## 五、本项目特有的环境问题

这些不是通用 WinUI 开发问题，而是「局域网文件传输」这个产品形态带来的，必须在写代码前就想清楚：

1. **Windows 没有 Bonjour 服务**。macOS 自带 mDNSResponder，Windows 没有。可选方案：用 WinRT 的 `Windows.Networking.ServiceDiscovery.Dns`（系统 DNS-SD 客户端，只能查询不能注册）、自带 mDNS 库（如 Makaretu.Dns）、或用 UDP 广播发现。这个决策会直接影响能否和 Mac 端互相发现，属于协议冻结前必须定的项。
2. **入站端口要显式放行**。第一次 F5 调试时会弹 Windows Defender 防火墙授权框，选「专用网络」允许。发布版要在 MSIX 清单里声明 `internetClientServer` + `privateNetworkClientServer` 能力，否则打包后收不到连接。
3. **公用 Wi-Fi 会杀掉一切**。咖啡厅/酒店网络是 Public 配置文件，入站全丢；而且访客网络通常开了 AP 隔离，同一网段也互相不通。这是必须给用户的错误提示，不是代码能绕过的。
4. **多网卡 / VPN / 代理**。Clash、Tailscale、VMware 虚拟网卡都会让「本机 IP」变成多个。发现和传输必须绑定具体网卡，不能依赖默认路由。本机就装着 Clash（`127.0.0.1:7897`），开发时注意别让代理劫持 LAN 流量。
5. **休眠恢复后 socket 会僵死**。合盖唤醒后旧连接不可用，需要重连逻辑。
6. **x64 与 ARM64 双架构**。MSIX 要么分别出包，要么做 `.msixbundle`。开发机是 x64，ARM64 需要额外真机或虚拟机验证。

## 六、验证环境是否可用

装完先跑体检，再跑一个空白 WinUI 窗口确认整条链路通：

```powershell
# 全自动：建工程 → 编译 → 启动 → 验证窗口 → 清理
powershell -NoProfile -ExecutionPolicy Bypass -File Project\Windows\scripts\Test-Toolchain.ps1
```

成功时最后一行输出 `SMOKE TEST PASSED: build, MSIX identity launch and window creation all work.`

手工验证步骤（等价于脚本做的事）：

```powershell
# 1. 体检
powershell -NoProfile -ExecutionPolicy Bypass -File Project\Windows\scripts\Check-DevEnv.ps1

# 2. 装 WinUI 模板（一次即可）
dotnet new install Microsoft.WindowsAppSDK.WinUI.CSharp.Templates

# 3. 建一个临时验证工程（不要建在仓库里）
cd $env:TEMP
dotnet new winui -n WinUISmokeTest
cd WinUISmokeTest
dotnet run
```

能弹出一个空白窗口就说明 .NET SDK、Windows App SDK、MSIX 调试身份注册全部正常。

> 模板自带 `Microsoft.Windows.SDK.BuildTools.WinApp`，`dotnet run` 会自动注册调试用包标识，不需要手工部署。这也是**不装 VS 也能跑**的原因，可作为 CI 和 AI 辅助编码的路径。

### 关于「是否需要独立 Windows SDK」

**C# WinUI 3 不需要安装独立的 Windows SDK。** 本项目实测：WinUI 工作负载只装了 NuGet 形式的 `Microsoft.Windows.SDK.BuildTools`，`C:\Program Files (x86)\Windows Kits` 下没有 SDK，工程照样编译成功、打包启动成功。

原因：TFM `net10.0-windows10.0.26100.0` 所需的 WinRT 投影来自 NuGet 包 `Microsoft.Windows.SDK.NET.Ref`，而 `makeappx.exe` / `signtool.exe` 由 `Microsoft.Windows.SDK.BuildTools` 包提供（实测路径 `~/.nuget/packages/microsoft.windows.sdk.buildtools/10.0.28000.2705/bin/10.0.28000.0/x64/`）。

只有写 C++/WinRT 或需要 SDK 自带的调试工具（如 WinDbg 扩展、ETW 工具）时，才需要装独立 SDK：

```powershell
winget install --id Microsoft.WindowsSDK.10.0.26100 --exact --source winget
```

VS 路线验证：新建项目搜 `WinUI`，选 **WinUI Blank App (Packaged) C#**，F5，看窗口是否出现。

## 七、常见故障

| 现象 | 原因 | 处理 |
| --- | --- | --- |
| **VS 安装报错 5002 / `已添加项。字典中的关键字:"http_proxy"所添加的关键字:"HTTP_PROXY"`** | 进程环境里同时存在大小写不同的同名变量，.NET 建大小写不敏感字典时抛重复键异常，VS 引导器直接崩溃 | 见下方「重复环境变量」 |
| `Start-Process` 报同样的重复键错误 | 同上，`Start-Process` 也会建字典 | 见下方「重复环境变量」 |
| `dotnet`/`winapp` 不识别 | PATH 没刷新 | 关掉终端重开，或重启 |
| VS 里搜不到 WinUI 模板 | 工作负载没装或没重启 | VS Installer → 修改 → 勾「WinUI 应用程序开发」→ 重启 VS |
| 部署报「未启用开发者模式」 | 注册表项未生效 | 设置 → 系统 → 高级 → 开发人员模式；或重跑脚本后重启 |
| `dotnet run` 报 SDK 解析失败 | WinAppSDK 版本与 TFM 不匹配 | 确认 csproj 里 `TargetFramework=net10.0-windows10.0.26100.0`、`WindowsAppSDK` 为 2.5.1 |
| MSIX 打包报路径过长 | 长路径未开 | 开 `LongPathsEnabled`，并把仓库放在短路径下（如 `C:\Work`，本项目已在短路径） |
| 装包后应用收不到请求 | 清单缺网络能力 / 网络是公用 | 检查 `Package.appxmanifest` 的 capabilities；见第五节第 2、3 条 |
| `winget` 从 GitHub 下载失败（`0x80072efd`） | 本机直连 GitHub 不通 | 走代理下载后再 `Add-AppxPackage`；或临时打开系统代理 |

### 重复环境变量（本机真实踩到）

本机进程环境里同时有 `http_proxy` 和 `HTTP_PROXY`（由启动方注入，用户级/系统级注册表里都没有）。Windows 环境变量名大小写不敏感，任何 .NET 代码用 `Environment.GetEnvironmentVariables()` 建字典都会抛「已添加了具有相同键的项」。

判定方法：`Get-ChildItem Env:` 本身就会抛异常。

修复（只影响当前进程，安全）：

```powershell
foreach ($n in 'http_proxy','https_proxy','all_proxy','no_proxy') {
    [Environment]::SetEnvironmentVariable($n, $null, 'Process')
}
[Environment]::SetEnvironmentVariable('HTTP_PROXY','http://127.0.0.1:7897','Process')
```

`Install-DevEnv.ps1` 和 `Test-Toolchain.ps1` 已内置该修复，会在启动任何子进程前自动执行。`Check-DevEnv.ps1` 的 **Env hygiene** 一项会检测这个问题。

## 八、本机实测记录（2026-10-23，Windows 11 25H2 / build 26200.9457）

### 安装前

```
[OK]   OS version            Windows 11 build 26200.9457 (25H2)
[OK]   CPU architecture      x64
[OK]   Elevation             running as Administrator
[FAIL] Developer Mode        disabled
[WARN] Long path support     LongPathsEnabled = 0
[FAIL] dotnet SDK            no SDK, runtime 8.0.21 only
[WARN] Desktop runtime       Microsoft.WindowsDesktop.App missing
[FAIL] Windows SDK           none installed
[FAIL] MSIX tooling          makeappx / signtool not found
[FAIL] Visual Studio         not installed
[OK]   Windows App Runtime   Microsoft.WindowsAppRuntime.2 2.5.1.0
[WARN] WinApp CLI            not on PATH
[WARN] Dev signing cert      none
[WARN] Network profile       Public only
[WARN] Firewall rules        none yet
[WARN] git                   not on PowerShell PATH
  -> 4 OK / 7 WARN / 5 FAIL
```

### 安装后

```
[OK]   OS version            Windows 11 build 26200.9457 (25H2)
[OK]   CPU architecture      x64
[OK]   Elevation             running as Administrator
[OK]   Developer Mode        enabled
[OK]   Long path support     LongPathsEnabled = 1
[OK]   dotnet SDK            10.0.402
[OK]   Desktop runtime       Microsoft.WindowsDesktop.App 10.0.13
[OK]   Windows SDK           via NuGet BuildTools 10.0.28000.2705
[OK]   MSIX tooling          makeappx.exe + signtool.exe found
[OK]   Visual Studio         Community 2026 18.11.12315.225
[OK]   Windows App Runtime   Microsoft.WindowsAppRuntime.2 2.5.1.0
[OK]   WinApp CLI            0.7.1
[WARN] Dev signing cert      none (创建工程后再建，CN 必须与 manifest 里 Publisher 一致)
[OK]   Network profile       1 private network
[WARN] Firewall rules        none yet (首次调试会弹授权框)
[WARN] git                   not on PowerShell PATH
[WARN] Env hygiene           duplicate proxy vars (工具会话注入，脚本已自动规避)
  -> 13 OK / 4 WARN / 0 FAIL
```

### 验收结果

- `dotnet new winui` 建工程：成功，`TargetFramework = net10.0-windows10.0.26100.0`
- `dotnet build -c Debug`：成功，0 警告 0 错误，耗时约 9 秒
- `dotnet run`：以 MSIX 打包身份启动，拿到真实顶层窗口句柄，包注册状态 `Ok`
- 安装体积：VS 2026（含 WinUI 工作负载）3.6 GB；`dotnet run` 全流程约 30 秒
- VS 2026 安装耗时 44 分钟（含下载）

### 待办

- ~~**代码签名证书**~~ 已完成：`Publisher="CN=Songli"`，自签名证书由 `scripts/Build-Msix.ps1 -NewDevCert` 生成（`Subject=CN=Songli`，友好名 `Feige Dev Signing`），签名与侧载验收均已通过。见[开发手册](DeveloperGuide.md)「签名与打包」。
- **防火墙规则**：等确定监听端口和发现方式后，用 `New-NetFirewallRule` 生成专用规则，不要靠首次运行弹窗。
- **ARM64 验证**：开发机是 x64，ARM64 需要真机或虚拟机。
