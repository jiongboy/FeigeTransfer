# Windows 原生项目：飞鸽快传

## 产品目标

提供 Windows 10/11 原生桌面传输应用，面向与 macOS 版本互通。Windows 客户端与 Mac 客户端共享协议和产品能力，但使用 Windows 原生 UI 与系统集成，不将 Tauri/WebView 作为最终 UI 方向。

## 目标技术栈

- UI：WinUI 3 / Windows App SDK（C# 与 .NET），遵循 Fluent Design。
- 网络：Windows 原生 Socket/WinRT 网络能力；Bonjour/mDNS 发现通过可维护的兼容层实现。
- 文件：Windows 文件选择器与 StorageFolder；接收端支持应用数据/用户授权目录。
- 安装：新增自包含 EXE 安装包（Inno Setup），按用户安装；保留 MSIX 构建。正式发布使用可信代码签名。
- 共享互通：旧发布包基于 [v1 历史协议](../Public/ProtocolV1-Historical.md)，当前 1.2.0 已切换到 [v3 协议](../Public/ProtocolV3.md)，交互以 [UI 与交互同步规范](../Public/UISpecification.md)为准。

## 当前项目状态

Windows 工程位于 `Project/Windows/`，入口见 [Windows README](../../Windows/README.md)。所有交付包及随包文件放在根目录同级 `Release/`，不提交 Git。更新服务端点使用 Mac mini LAN，发布和安全状态见[更新接入文档](UpdateService.md)。`2c37f61` 的 v1 协议层、连接层、发现层与 WinUI 3 界面已实现，基线报告 189 项测试通过；签名 MSIX 侧载验收属于历史基线（`Publisher="CN=Songli"`），不是当前 v3/接收确认版本的验收结论。

macOS 0.3.3 与当前 Windows 源码均使用 v3，旧 Windows v1 安装包不兼容。WinUI 已接入 v3 会话服务、一次同意、按指纹归组的设备历史、文字/附件时间线、图片缩略图、草稿、重试与消息存储。1.2.0 交付基线的 392 项核心测试曾在 macOS ARM64 和 Windows ARM64 的 .NET 10 上通过，WinUI ARM64、x64 构建通过（0 警告、0 错误）；这是历史交付验证，不代表当前新目录已在 Windows .NET 10 环境复验。

用户已反馈 Windows↔Mac 基本互通。1.2.0 汇总三栏界面、附件预览、会话管理、两页引导与网络韧性优化，Windows 11 ARM64 的原生窗口专项检查见开发日志。x64 / ARM64 自包含 EXE 交付与验证范围见 [1.2.0 交付说明](ReleaseNotes-1.2.0.md)。完整双机、接收确认、中文输入法/拖放、真实 TUN/多层路由、Intel/AMD 和当前版本签名 MSIX 仍待验收。

交付后源码新增文件/图片接收确认、设备名称同步、刷新状态反馈、安装签名检查及 Mac mini LAN 更新源改造。当前 Core **449/449** 测试通过（macOS .NET 11 RC runtime roll-forward 执行 net10 测试）；本轮未在 Windows .NET 10 环境重跑，WinUI 双架构与真实 UI/双机测试尚未完成。1.2.0 安装包仍对应 392 项交付基线且未更换。Mac mini 更新端点已配置，但清单和新安装包尚未发布。验收、发布边界见 [文件接收确认](FileReceiveApproval.md)、[更新接入](UpdateService.md)和[1.2.0 交付说明](ReleaseNotes-1.2.0.md)。

开发环境已在 Windows 11 25H2 就绪，搭建步骤与版本基线见 [`Windows环境搭建.md`](EnvironmentSetup.md)。后续 Windows UI/协议修改优先在 Parallels Windows 环境构建验证；文件接收确认设计与验收矩阵见 [`Windows文件接收确认.md`](FileReceiveApproval.md)。

## 开发顺序

1. 当前源码 Core 为 449 项测试通过；最新测试是在 macOS .NET 11 RC runtime roll-forward 环境执行，须由 Windows .NET 10 SDK/runtime复验。1.2.0 包仍为 392 项交付基线。接收确认、名称/刷新修复版的 WinUI ARM64/x64 构建、双机拒绝/超时/收发矩阵、最终包全量复测、真实资料升级及 Intel/AMD 实机安装待完成。Mac mini 更新端点已配置，但签名清单、更新包发布和 Windows 更新安装验收待完成。
2. 已接入 v3 会话界面和一次连接同意，继续验收文字送达/失败/重试、设备隔离、双向附件、图片缩略图与文件移走场景。
3. 已实现身份签名预检、名称/目录持久化顺序、单实例和关闭后重开代码；继续验证中文输入法、主题、高 DPI、窗口重开和退出清理。
4. 验证 Windows↔Windows、Windows↔macOS 的文字、图片、空文件和大文件，以及拒绝、断线、取消、重名与磁盘不足。
5. 重新构建签名 MSIX，完成安装/升级/卸载与防火墙、多网卡场景验收。旧包验收结果不代表 v3 新版已通过。

## 兼容性与测试

- Windows 10/11 目标版本及 x64/ARM64 分别确认。
- Windows↔Windows、Windows↔Mac 双向互传。
- 验证防火墙授权、专用/公用网络配置、访客 Wi‑Fi 隔离、代理/VPN、多网卡和休眠恢复。
- 接收方先同意本次连接，连接内不再逐文件询问；禁止路径穿越、默认覆盖和未经验证的自动启动。

## 安全边界

旧 v1 安装包与 macOS 当前 v3 格式不兼容；源码 v3 接入已完成本轮编译和核心测试，用户已确认 Windows↔Mac 基本互通，完整运行验收尚未完成。新版发布前需完成签名发行包、双机（Windows↔Windows、Windows↔macOS）完整互通验收，以及防火墙/访客网络/多网卡场景测试。
早期 Python HTTP 原型不得作为 Windows 或任何正式发布的后端服务。
