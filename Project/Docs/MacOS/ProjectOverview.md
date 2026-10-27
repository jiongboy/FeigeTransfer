# 飞鸽快传 Mac 原生项目

macOS 13+，SwiftUI / Network.framework / CryptoKit / Keychain。

## 当前操作流程

应用自动发现局域网设备；发起连接后，只让接收方点一次同意。已连接的双方可以文字聊天、发送原图，以及连续、多批次和同时双向发送文件。没有配对码、短码重输或逐文件确认。断开后再次请求连接会重新询问。

取消人为文件大小上限和固定总传输时长上限，支持空文件。大文件按块传输并流式计算 SHA-256；接收方校验并原子落盘后才回执。

## 代码结构

| 文件 | 职责 |
| --- | --- |
| `Sources/FeigeMacApp.swift` | 前台激活、窗口与退出清理 |
| `Sources/ContentView.swift` / `SetupView.swift` / `SendView.swift` | 设备会话、连接同意、设置、手动地址连接 |
| `Sources/ChatComposer.swift` / `ChatMessageView.swift` / `AttachmentPreviewView.swift` / `DOCXTextExtractor.swift` | 原生输入与粘贴、附件卡片、图片/PDF/文本/DOCX 只读预览 |
| `Sources/ChatMessage.swift` / `ConversationArchive.swift` | 设备指纹归组的消息、会话隐藏/恢复/删除与兼容持久化 |
| `Sources/TransferModel.swift` | 长连接生命周期、一次接收同意、发送队列、双向进度、取消重试、配置、会话与成功历史 |
| `Sources/LANService.swift` / `ConnectionHelpView.swift` | 自动 Bonjour 广播/发现、TCP 监听与只读网络排查建议，TXT 协议版本为 3 |
| `Identity/Sources/ConnectionService.swift` | v3 签名握手、自动密钥协商、接收方同意及加密决定回执 |
| `FileConnection.swift` | 连接复用、单读循环与有序写入、文字回执和双向文件路由、无大小阈值的流式收发与校验 |
| `Identity/Sources/TemporaryFileSink.swift` / `TemporaryFileSinkRecovery.swift` | 描述符锁定目录、安全暂存、不覆盖同名文件、原子落盘及按用户保存目录触发的孤立文件恢复清理 |
| `Identity/Sources/NetworkByteStream.swift` | 有界网络读写、局域网实际端点检查、取消与超时关闭底层连接 |

v1 的 `TransferService` / `TransferSession` / SAS 原语留作回归参考，当前应用入口只使用 v3，不会显示旧流程。

## 验证与边界

已验证本机真实 TCP 上的一次同意、多文件与反向发送；已验证超过旧 100 MiB 阈值的 128 MiB+17 字节文件，源和目标摘要一致。v2 同一连接双向同时传输、空文件、同名保护及异常清理有针对性测试。

当前实现按[Public protocol v3](../Public/ProtocolV3.md)执行，与旧版 v1/v2 不互通；Windows v3 实现和基本互通已有用户反馈，完整双机矩阵仍待验收。实现细节见[开发手册](DeveloperGuide.md)。预览包版本为 0.3.3。文字/图片/文件并发、文字去重和记录恢复已有测试。

尚未完成完整实体双机、原生界面自动操作、Windows 互通矩阵、显式多地址回退/网络变化恢复、DOCX 复杂样式/分页预览、通知/更新接入、特殊网络/磁盘场景、Developer ID 公证与通用架构验收。目录传输、断点续传仍未实现；孤立暂存文件扫描清理已实现，但只在用户保存接收目录时触发。详见[验收清单](AcceptanceChecklist.md)。
