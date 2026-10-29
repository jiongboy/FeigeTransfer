# 本地调试与发布流程

本文档规定 Windows 客户端从改代码到出正式包的完整路径：**先在本机虚拟机验证，确认无误后再到 ROG 签名打包**。

> 原则：ROG 只做正式签名与交付，不承担日常调试。日常调试在 Parallels 的 Windows 11 虚拟机（`Songli-M5max-Win`）里完成。

## 一、三个阶段

| 阶段 | 位置 | 目的 | 产物 |
| --- | --- | --- | --- |
| 1. 开发 | 本机 macOS | 改代码、跑单元测试 | 提交并推送到 `origin/master` |
| 2. 调试 | 本机 Parallels 虚拟机 | 验证功能与界面 | 自签名 MSIX，本地安装 |
| 3. 发布 | ROG 真机 | 正式签名、交付 | `Release/Windows/<version>/` |

## 二、阶段 1：本机开发

```sh
# Windows 侧逻辑改动的主要验证手段
dotnet test Project/Windows/tests/Feige.Core.Tests/Feige.Core.Tests.csproj   # ROG 或虚拟机

# macOS 客户端
cd Project/MacOS && swift build && swift test
```

`Project/Windows/src/Feige.Core` 的平台无关逻辑由 466 个用例覆盖，提交前应全部通过。

提交规范：每个 Git 提交都必须先经用户确认再执行，`Release/` 永不提交。

## 三、阶段 2：虚拟机调试

### 3.1 环境（已就绪）

| 组件 | 位置 |
| --- | --- |
| 代码 | `C:\Work\FeigeTransfer`（git 克隆，身份 `Songli` / `138game@gmail.com`） |
| .NET SDK | `C:\Tools\FeigeDotnet`（10.0.402） |
| Inno Setup | `C:\Tools\FeigeInno\ISCC.exe` |
| Windows App Runtime | `Microsoft.WindowsAppRuntime.2` 2.5.1.0 |
| 开发证书 | `CN=Songli`，友好名 `Feige Dev Signing`，在 `CurrentUser\TrustedPeople` |

签名证书与私钥**只在虚拟机和 ROG 各自存在**，从不跨机复制。

### 3.2 更新代码

```powershell
& "C:\Program Files\Git\cmd\git.exe" -C C:\Work\FeigeTransfer pull --ff-only origin master
```

### 3.3 构建并安装（推荐：自签名 MSIX）

```powershell
cd C:\Work\FeigeTransfer\Project\Windows\scripts
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Msix.ps1 -NewDevCert -TrustDevCert -Platform arm64
```

首次会生成自签名证书并信任，之后复用。安装：

```powershell
Add-AppxPackage -Path 'C:\Work\FeigeTransfer\Release\Windows\1.2.1\Feige.App_1.2.0.0_arm64_Test\Feige.App_1.2.0.0_arm64.msix' -ForceUpdateFromAnyVersion
```

从开始菜单或 `explorer.exe shell:AppsFolder\<PackageFamilyName>!App` 启动。

### 3.4 为什么必须用 MSIX，不能直接跑 exe

未签名的裸 `Feige.App.exe` 在这台虚拟机上启动即崩：

```
出错模块名称: Microsoft.UI.Input.dll
异常代码:     0xc0000602   (STATUS_INVALID_IMAGE_HASH)
```

这是系统的代码完整性策略拒绝了未签名映像，**不是应用缺陷，也不是运行时版本问题**——装好 Windows App Runtime 2.5 后依然崩溃。

有包身份的 MSIX 走不同的加载校验路径，可以正常启动。因此虚拟机里调试一律走 MSIX；`Build-Installer.ps1 -AllowUnsignedTest` 产出的安装包只用来验证打包链路本身，不能用来验证界面。

### 3.5 打包链路快速验证

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File .\Build-Installer.ps1 -Platform arm64 -AllowUnsignedTest `
  -DotnetPath 'C:\Tools\FeigeDotnet\dotnet.exe' -IsccPath 'C:\Tools\FeigeInno\ISCC.exe'
```

成功标志：输出 `Installer ready: ...\FeigeSetup-<version>-arm64.exe`，且同目录有对应的 `.json` 元数据（含 `sha256`、`signed: false`）。

### 3.6 用计划任务以用户身份执行

`prlctl exec` 始终以 `NT AUTHORITY\SYSTEM` 运行，而 MSIX 部署禁止 SYSTEM 账户（`0x80073CF9`）。需要用一次性的计划任务以当前登录用户身份执行：

```powershell
$act = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument '-NoProfile -File C:\path\to\task.ps1'
$pr  = New-ScheduledTaskPrincipal -UserId "$env:COMPUTERNAME\songli" -LogonType Interactive -RunLevel Highest
Register-ScheduledTask -TaskName 'FeigeDebug' -Action $act -Principal $pr -Force
Start-ScheduledTask -TaskName 'FeigeDebug'
```

## 四、阶段 3：ROG 正式发布

确认虚拟机验证通过后，在 ROG 上：

```powershell
cd C:\Work\FeigeTransfer\Project\Windows\scripts

# 用正式发布者证书签名（指纹须与 Updates/UpdateSource.json 固定的 SHA-256 一致）
.\Build-Installer.ps1 -Platform x64 -CertThumbprint <40 位指纹>

# 或直接给 PFX（私钥不进机器证书存储）
.\Build-Installer.ps1 -Platform x64 -SigningPfxPath C:\Keys\FeigeRelease\codesign-songli.pfx `
  -SigningPfxPassword <密码>
```

产物落在 `Release/Windows/<version>/`，包含安装包、`.json` 元数据（含 `signed: true` 与 `signedPayloadFiles`）。

**ROG 的正式证书不可用于虚拟机**，虚拟机也不要生成或复制正式证书。

## 五、两个打包脚本的关键约定

| 约定 | 原因 |
| --- | --- |
| 用 `dotnet msbuild -t:Publish`，不用 `dotnet publish` | CLI 会按发布配置文件改写输出目录，产物为空时仍可能返回 0 |
| 发布前显式 `dotnet restore` | `dotnet msbuild` 没有 `dotnet publish` 的隐式还原，干净克隆会报 `NETSDK1004` |
| 安装包构建清空 `PublishProfile` | 否则 pubxml 里的 `PublishDir` 覆盖 `-p:PublishDir` |
| `IncludeInstallerXamlResources` 用 `BeforeTargets` | 用 `AfterTargets` 晚于复制步骤，`.pri`/`.xbf` 不会进产物 |
| 发布后校验 `Feige.App.exe` 存在 | 防止「成功但空目录」被当成通过 |
| MSIX 构建传 `-p:PublishReadyToRun=false` | 无 RID 时开启 ReadyToRun 会报 `NETSDK1094` |

## 六、验收清单

发布前在虚拟机中确认：

- [ ] `Feige.Core.Tests` 全部通过
- [ ] MSIX 自签名构建成功，安装后窗口正常打开
- [ ] 改动涉及的功能实际点过一遍（收发、会话、设置等）
- [ ] `Build-Installer.ps1 -AllowUnsignedTest` 产出安装包与 `.json`
- [ ] 提交已推送，虚拟机与 ROG 同一提交
