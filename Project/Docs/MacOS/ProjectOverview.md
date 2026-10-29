# 飞鸽快传 Mac 原生项目

macOS 13+，SwiftUI / Network.framework / CryptoKit / Keychain / UserNotifications。当前版本 0.4.0（构建 7），功能已按 Windows 1.2.x 对齐。

## 当前操作流程

应用自动发现局域网设备；发起连接后，只让接收方点一次同意。已连接的双方可以文字聊天、发送原图，以及连续、多批次和同时双向发送文件。没有配对码、短码重输或逐文件确认。断开后再次请求连接会重新询问。

取消人为文件大小上限和固定总传输时长上限，支持空文件。大文件按块传输并流式计算 SHA-256；接收方校验并原子落盘后才回执。

草稿按会话持久化，重启后仍在，发送成功后清除。输入区内置表情面板。网络变化后自动重建设备发现，已建立的连接与监听端口保持不变。关闭窗口后在后台继续运行并提示一次，可从 Dock 或 ⌘1 重开。

## 与 Windows 对齐的能力（0.4.0）

| 能力 | 实现 |
| --- | --- |
| 设置窗口 | 设备名称、接收文件夹、本机连接信息、版本与更新、通知、本机记录、隐藏会话、设备身份、应用运行、版权，与 Windows 设置卡片对应 |
| 提示条 | 授权准备、原资料、首次连接、首次收发、通知邀请、后台运行、更新与结果、错误，均为可关闭提示 |
| 通知 | 首次收发后邀请；可启用、关闭、发送测试通知、打开系统通知设置；失败不影响收发 |
| 更新 | 签名清单（P-256/Ed25519）、24 小时静默检查、用户确认下载、大小与 SHA-256 校验、等待收发结束、签名校验后原子替换并重启、结果提示 |
| 手动连接 | 仅接受字面量局域网/私网地址与 1–65535 端口 |

## 代码结构

| 文件 | 职责 |
| --- | --- |
| `Sources/FeigeMacApp.swift` | 前台激活、菜单命令（⌘1、偏好设置、检查更新）、窗口关闭提示与退出清理 |
| `Sources/ContentView.swift` / `SetupView.swift` / `SendView.swift` / `GuidancePanel.swift` | 设备会话、连接同意、设置卡片、手动地址连接与提示条 |
| `Sources/ChatComposer.swift` / `ChatMessageView.swift` / `AttachmentPreviewView.swift` / `DOCXTextExtractor.swift` / `EmojiPalette.swift` | 原生输入与粘贴、附件卡片、只读预览、表情面板 |
| `Sources/ChatMessage.swift` / `ConversationArchive.swift` | 设备指纹归组的消息、会话隐藏/恢复/删除与兼容持久化 |
| `Sources/TransferModel.swift` | 长连接生命周期、一次接收同意、发送队列、双向进度、取消重试、配置、草稿、通知、网络变化、更新接线 |
| `Sources/TransferNotifier.swift` / `AppLogFile.swift` / `AppDisplay.swift` | 系统通知、脱敏诊断日志、版本与大小显示 |
| `Sources/UpdateController.swift` / `UpdateManifest.swift` / `UpdateSignature.swift` / `UpdateSource.swift` / `UpdateState.swift` / `UpdateInstaller.swift` | 更新检查、清单验签、下载校验、安装交接与结果 |
| `Sources/LANService.swift` / `ConnectionHelpView.swift` | 自动 Bonjour 广播/发现、TCP 监听、使用帮助与只读网络排查建议，TXT 协议版本为 3 |
| `Identity/Sources/ConnectionService.swift` | v3 签名握手、自动密钥协商、接收方同意及加密决定回执 |
| `FileConnection.swift` | 连接复用、单读循环与有序写入、文字回执和双向文件路由、无大小阈值的流式收发与校验 |
| `Identity/Sources/TemporaryFileSink.swift` / `TemporaryFileSinkRecovery.swift` | 描述符锁定目录、安全暂存、不覆盖同名文件、原子落盘及按用户保存目录触发的孤立文件恢复清理 |
| `Identity/Sources/NetworkByteStream.swift` | 有界网络读写、局域网实际端点检查、取消与超时关闭底层连接 |

v1 的 `TransferService` / `TransferSession` / SAS 原语留作回归参考，当前应用入口只使用 v3，不会显示旧流程。

## 验证与边界

已验证本机真实 TCP 上的一次同意、多文件与反向发送；已验证超过旧 100 MiB 阈值的 128 MiB+17 字节文件，源和目标摘要一致。v2 同一连接双向同时传输、空文件、同名保护及异常清理有针对性测试。

当前实现按[Public protocol v3](../Public/ProtocolV3.md)执行，与旧版 v1/v2 不互通；Windows v3 实现和基本互通已有用户反馈，完整双机矩阵仍待验收。实现细节见[开发手册](DeveloperGuide.md)。macOS 0.4.0 build 11 已发布到 Mac mini 局域网更新源；build 9 → 10 升级链路及 build 10 → 11 更新成功提示关闭均已实测。文字/图片/文件并发、文字去重、记录恢复、草稿、设置兼容、更新清单验签与下载校验已有测试。

尚未完成完整实体双机、Windows 互通矩阵、更新边界场景（跳过、间隔、收发中等待、失败拒绝）、DOCX 复杂样式/分页预览、特殊网络/磁盘场景、Developer ID 公证与通用架构验收。Mac mini 当前为 macOS 12.7.6 Intel，不能运行最低要求为 macOS 13.0 的客户端；真实升级在本机 arm64 Mac 验收。目录传输、断点续传仍未实现；孤立暂存文件扫描清理已实现，但只在用户保存接收目录时触发。详见[验收清单](AcceptanceChecklist.md)。
