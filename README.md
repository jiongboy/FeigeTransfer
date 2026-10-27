# FeigeTransfer · 飞鸽快传

面向 Windows 与 macOS 的**局域网聊天与文件传输工具**。同一局域网内自动发现设备，对方同意一次即可开始聊天、互发原图与文件；不经过云端，不依赖账号。

<p align="center">
  <img src="Project/Ui/Branding/FeigeIconV1/Feige-master.png" width="128" alt="飞鸽快传图标">
</p>

## 功能

- **自动发现**：基于 Bonjour / mDNS 在局域网内发现同版本设备，无需手动填 IP；也可手动输入地址连接。
- **一次同意**：发起连接后，接收方只需同意一次，本次连接内即可持续聊天和互传文件；断开后重连需重新同意。
- **聊天与附件**：文字、原图、文件在同一个会话中进行；传输期间仍可继续聊天，支持双向同时传输。
- **无产品大小上限**：文件流式传输并实时计算 SHA-256，校验通过并原子落盘后才回执；支持空文件。
- **安全传输**：RSA-PSS 签名握手 + X25519 密钥协商 + AES-256-GCM 加密记录层，协议见 [ProtocolV3](Project/Docs/Public/ProtocolV3.md)。
- **原生界面**：Windows 使用 WinUI 3，macOS 使用 SwiftUI，各自遵循平台交互习惯。

## 平台支持

| 平台 | 技术栈 | 最低系统 | 状态 |
| --- | --- | --- | --- |
| Windows | WinUI 3 / Windows App SDK / .NET 10 | Windows 10 1809+ | 已实现 v3 协议，正式试用版已发布 |
| macOS | SwiftUI / Network.framework / CryptoKit | macOS 13 | 开发预览版 |

两端使用同一套 v3 通信协议，可以互连互通。完整的设备矩阵验收仍在进行，详见 [Windows 验收文档](Project/Docs/Windows/ProjectOverview.md) 与 [macOS 验收清单](Project/Docs/MacOS/AcceptanceChecklist.md)。

## 快速开始

**Windows**：需要 .NET 10 SDK 与 WinUI 应用开发工作负载，详见[环境搭建](Project/Docs/Windows/EnvironmentSetup.md)。

```powershell
cd Project\Windows
dotnet test .\tests\Feige.Core.Tests\Feige.Core.Tests.csproj
```

**macOS**：需要 Xcode 命令行工具，最低 macOS 13。

```sh
cd Project/MacOS
swift build
swift test
swift run
```

`swift run` 启动后会请求钥匙串与本地网络授权，按系统提示选择“始终允许”可避免重复弹窗。

## 项目结构

```
FeigeTransfer/
├── Project/
│   ├── Windows/      Windows 客户端、协议核心、测试与打包脚本
│   ├── MacOS/        macOS 客户端、身份与传输模块、测试与打包脚本
│   ├── Docs/         开发文档：Public / Windows / MacOS 三类
│   └── Ui/           图标等界面资源
├── Release/          本地打包输出（按版本号归档，不纳入 Git）
└── AGENTS.md         仓库协作与打包规则
```

## 文档

- [文档总索引](Project/Docs/README.md)
- 跨平台：[开发总则](Project/Docs/Public/DevelopmentRules.md) · [通信协议 v3](Project/Docs/Public/ProtocolV3.md) · [UI 与交互规范](Project/Docs/Public/UISpecification.md) · [应用更新规则](Project/Docs/Public/UpdatePolicy.md)
- Windows：[开发手册](Project/Docs/Windows/DeveloperGuide.md) · [环境搭建](Project/Docs/Windows/EnvironmentSetup.md) · [开发日志](Project/Docs/Windows/DevelopmentLog.md)
- macOS：[开发手册](Project/Docs/MacOS/DeveloperGuide.md) · [开发日志](Project/Docs/MacOS/DevelopmentLog.md)

## 参与贡献

欢迎提交 Issue 反馈问题或建议。提交代码前请先阅读[开发总则](Project/Docs/Public/DevelopmentRules.md)与[仓库规范](Project/Docs/Public/RepositoryPolicy.md)；协议或安全相关的改动需先更新 `Project/Docs/Public/` 下的共同规范。

## 许可证

本项目采用 [MIT License](LICENSE)。

---

**免责声明**：飞鸽快传仅用于同一局域网内的设备之间传输文件与消息，请勿在公共或不受信任的网络中传输敏感信息。
