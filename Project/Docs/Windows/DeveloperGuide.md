# 飞鸽快传 Windows 原生开发手册

## 当前状态（2026-10-26）

当前源码最新 Core 回归 **449/449** 通过，包含 1.2.0 交付后的接收确认、名称同步、刷新反馈和更新源改造。本轮 Mac 测试机仅安装 .NET 11 RC，采用 `DOTNET_ROLL_FORWARD=Major` 执行 net10 测试，并非 Windows .NET 10 验收；WinUI 双架构构建与真实双机测试也尚未重跑。1.2.0 安装包未替换，仍对应 392 项交付基线。更新服务已配置 Mac mini LAN 端点但尚无清单/包发布，详见[更新接入文档](UpdateService.md)。

本手册按时间倒序保留实现记录，下面各节的“未打包 / 测试数 / 构建结果”均指条目发生时，不代表当前状态。

## 历史记录：设备改名与刷新反馈（2026-10-24，当时未打包）

`ConversationDisplayName` 保存会话当前显示名；`AppSession` 接收发现快照后，按 v3 实例名和服务名匹配已知会话，更新列表、头像及当前聊天标题/输入提示。名称限制为 128 UTF-8 字节且不能为空白或包含控制字符。mDNS 名称只是显示提示，不能作为身份认证或自动同意依据；完整握手指纹、连接资料和历史消息名称不随广播改写，不创建或恢复隐藏会话。

`V3PeerListener.AcceptAsync(Func<LocalDevice>, …)` 在接受 TCP 后读取最新设备名称，避免等待连接时捕获旧值；原有 `LocalDevice` 重载仍保留。此调整不改变 v3 线协议或单次连接同意。

手动刷新由 `AppSession.IsRefreshingPeers`、`LastPeerRefreshAt`、`PeerRefreshFailed` 提供状态，页面显示进度环、结果时间和错误反馈。刷新期间禁止再次提交；重建发现后等待 2 秒接收首批异步响应，随后后台发现继续进行。退出取消等待，刷新本身不主动断开已有 TCP。原生点击/动画及窄窗口显示尚待复验。

当时名称同步轮次核心回归为 **405/405**，Windows SDK 10.0.402 双架构构建通过；那是历史结果，不是当前验收结论。

## 代理与网络变化接入（2026-10-24）

收发只使用 `TcpClient` / `Socket`，不接入 HTTP/SOCKS 代理协议；环境变量及系统 HTTP 代理不作为本机传输前提，VPN/TUN/透明代理仍服从系统路由与过滤。跨路由器是否可达由实际网络决定，不通过公网探测、关闭防火墙或改写路由表“修复”。

`LanAddressPolicy` 统一发现候选与 v3 拨号的局域网目标约束；`PeerBrowser` 对每个实例保存多个活地址，按已公布源地址及本机前缀排序，地址/端口更新发布新快照，TTL 0 或地址过期后切换或撤回。候选数最多 16。`V3PeerConnector` 只竞争 TCP 建连，200 ms 间隔、10 秒共享预算；清理其他套接字后才启动一条 v3 握手。身份/审批失败不回退，避免重复询问或把认证失败误作地址不可用。

UDP 接收使用包信息取得接收网卡，将 IPv4 入站网卡映射到相应 IPv6 网卡范围，给线上的无范围 `fe80::` 地址补上接收方范围。IPv6 多播目的地址显式带发出网卡编号，逐网卡处理地址族异常，IPv4/IPv6 发现可以独立启动。所有启动入口串行化，避免共享传输的并发发送创建重复接收循环。

`AppSession` 的 TCP 就绪与自动发现可用性分开。发现故障可显示警告并保留手动直连；地址变化回调防抖后只重建发现，刷新与更名共用启动锁，丢弃旧发现实例晚到的列表回调。保持已建立 TCP 的端口、身份和连接同意；底层 TCP 自然断开时仍需重新同意。稍后模式/退出不被地址变化事件重新启用。


连接失败分类由 `NetworkFailureAdvice` 处理；仅超时、不可达、套接字拒绝/重置等网络故障显示条件排查建议，保留原错误并提供“连接排查”入口。取消、对方拒绝连接请求、身份/协议失败和磁盘错误不显示代理建议；新连接清除旧提示，不推断已检测到 VPN 或确认为代理故障。

## 首次安装引导接入（2026-10-24）

共同设计以 main 的《首次安装引导方案》为准，Windows 使用原生 `SetupPage` 两页容器。`AppSettings` 仅加载配置并判定初始路由，不触发身份或网络；`MainWindow.InitializeExperienceAsync` 决定欢迎、恢复、主界面或损坏资料修复页。

`LocalPreferences` 新增可选 `SetupProgress`、自动检查更新及通知偏好。引导方案版本、步骤、结束/稍后、首次提示分别保存；不持久化“权限已经允许”或网络就绪。EXE 沿用 `settings.json` 的原子替换；MSIX 将完整 JSON 放在同一个 `ApplicationDataCompositeValue` 中一次提交，旧独立键仍可读。失败不推进内存状态，未知版本/损坏设置不覆盖原件。

旧配置或当前渠道资料目录内的身份/历史痕迹按已有用户处理；明确未完成的新状态优先，不能仅用默认设备名或消息是否为空判断。新用户通过 `SHGetKnownFolderPath(FOLDERID_Downloads)` 获取下载目录；已有用户的旧默认和明确设置保持不变。系统名称使用文本元素边界截断，目录探针采用独立名称与 DeleteOnClose。

`AppSession` 将本机记录读取、身份预检、目录检查、网络启动分别处理，历史可在稍后模式中读取。准备采用外部取消令牌和启动信号量，网络另有生命周期令牌；返回或稍后先取消等待，再停止本轮监听与发现。退出取消整个应用生命周期，晚到的身份结果不得启动网络。首次引导未结束不弹接收同意；正常进入后继续只使用正式连接确认。

通知仅在保存启用偏好后注册；首次真实送达/完整接收触发一次非阻塞邀请。设置帮助不启动另一套权限流程。1.2.0 交付包仅保存自动更新偏好；交付后 Windows EXE 源码已接入独立更新客户端与 Mac mini LAN 清单/下载源，但清单与新包尚未发布，Windows 端到端安装验收未完成。更新运行/安全状态以[Windows 更新接入文档](UpdateService.md)为准。应用关闭说明在支持通知且已启用时可用系统提示，否则在重开主窗口时显示非阻塞说明。


## 阅读范围与当前同步目标

本手册主体记录 `2c37f61` 已落地的 **v1 基线**，包括 189 项测试与签名 MSIX 的历史验收结果。文中旧协议章节号、SAS、16 字节 nonce 和单文件限制均指 [v1 历史协议](../Public/ProtocolV1-Historical.md)，不能直接套用到新会话协议。

当前升级按 [v3 协议](../Public/ProtocolV3.md)及 [UI 与交互同步规范](../Public/UISpecification.md)执行：12 字节 nonce（4 字节前缀 + 8 字节序列号）、一次连接同意、双向文字/图片/文件、流式文件无业务大小上限。已交付的 WinUI 1.2.0 已接入 v3，基线 392 项核心测试在 macOS 和 Windows ARM64 的 .NET SDK 10.0.402 / Runtime 10.0.13 下通过，ARM64 和 x64 应用构建均通过。用户已启用开发者模式并成功启动，反馈 Windows↔Mac 基本互通。新增 EXE 安装流程，ARM64 安装/启动/覆盖重装/卸载已通过；完整 UI、双机测试矩阵和新版可信签名仍待完成。旧 MSIX 发布包属 v1。

## 交付与验收边界（2026-10-26）

旧交付目录中的 1.2.0 测试包仍对应 392 项基线，未形成新版签名发行包。交付后另有文件接收确认、名称同步、刷新反馈、签名链路和 Mac mini LAN 更新源接入。接收确认需双方使用兼容版本，详见[接收确认验收矩阵](FileReceiveApproval.md)。EXE 更新源及 Mac mini 部署/发布状态见[更新接入文档](UpdateService.md)：尚无清单/包发布，Windows 客户端更新检查、下载、安装尚未端到端验收。MSIX 不由该 EXE updater 更新。Core 测试不替代 WinUI 构建、安装或双机验收。

## v3 核心接入约定（2026-10-23）

- 新入口为 `Net/V3PeerListener` / `V3PeerConnector`，返回 `Conversations/V3Connection`。不要复用 v1 `PeerSession` 的配对码、SAS 或单文件连接语义。发现显式传 `LanDiscovery(..., version: 3)`，默认值仍是 v1，避免让旧应用广播不支持的协议。
- 发起方不弹授权框，接收方的异步同意回调只调用一次；可用取消令牌撤销，晚到的同意不会恢复连接。局域网数字 IP 策略同时检查输入/发现目标和真实 TCP 远端，包括 IPv4 映射的 IPv6。
- 建立连接后先配置 `TextReceived` / `FileReceived` / `ReceiveProgress`，再启动并观察唯一的 `RunAsync` 任务；该循环处理双向回执及接收，不得另开读协程。它持续到断线/取消，调用方应捕获异常并恢复应用连接状态。
- `TextReceived` 必须完成本机消息记录才返回；未配置处理器或记录失败会断开，不发送伪送达回执。`SendTextAsync` 仅在匹配回执后成功。应用应按指纹保存历史，重试复用原 UUID。
- 应用持有共享 `V3TextInbox` 并通过 `RunAsync(..., textInbox: inbox)` 传入，跨重连保留最多 1000 个已接收文字 ID；启动时可由接收历史初始化。窗口本身不持久化正文。进行中的本地记录即使在断线后完成，也会先登记去重再释放后续重试。
- `SendFileAsync` 每方向同时一个；附件队列由应用层串行提交。`kind="image"` 传原图字节，缩略图由 UI 另行实现。无业务大小或总传输时长上限，流式读文件并同步计算摘要；空文件直接 finish。收到完整 finish、校验、刷盘并原子提交后才发 receipt；取消/错误清理未提交暂存文件。
- v3 接收落盘使用 `TransferFileSink.CreateForSession`，拒绝 Windows 非法字符、设备名、尾随点/空格；保留合法长名称，重名添加编号。v1 `Create` 的旧限制和名称整理行为保留。
- `Fixtures/v3.json` 为 `Transfer/main` 提交 `80d355f0469fa03495727cb71824213a0a2bb4d8` 中公共夹具的原样快照；测试比较 transcript、签名、HKDF、双向记录、UUID 字节及摘要。公共规范更新时须明确同步夹具，不由 C# 实现自行生成期望值。

1.2.0 交付基线的 392 项核心测试包含原 v1 189 项、v3 96 项、EXE 设置存储 5 项、附件预览 21 项、会话管理 12 项、首次引导 27 项、网络韧性/代理 24 项与网络失败提示分类 18 项，分别在 macOS 和 Windows ARM64 执行。后续名称同步一度将源码回归增加到 405 项；当前源码更新确认相关测试后 Core 总数为 449 项（详见本手册开头的 runtime 限制和最新边界）。历史 WinUI 双架构编译、Windows 11 ARM64 界面检查见开发日志；不替代当前源码 Windows 构建、真实双机/接收确认、Windows 10、Intel/AMD、真实 TUN/多层路由和新版 MSIX 验收。

## v3 WinUI 接入与消息记录（2026-10-23）

- `AppSession` 后台加载 DPAPI 身份并执行随机挑战签名/验签，成功前不监听和发现；失败保留窗口并可重试，退出后晚到结果会释放。
- 应用显式使用 `LanDiscovery(..., version: 3)`；一次一个活动连接或连接请求。异步 `UiPromptService` 串行显示原生对话框，取消时关闭授权弹窗；会话以认证指纹归组，不用发现名称归组。
- `ConversationHistory` 写 `conversations.json`，键为指纹/方向/UUID；刷盘后原子替换，失败不更新内存、不发送文字回执。未知版本/损坏记录原样保留并报告启动失败，不默默覆盖。保留最近 1000 条结束消息和所有活动项；重启和断线将未完成项转失败，不覆盖已送达记录。
- `V3Connection.FileReceiving` 在发 decision 前保存活动附件卡片；失败断开并清理暂存。文字重试复用 UUID，附件重试使用新 ID。应用从接收历史初始化共享去重窗口，草稿按会话保存；查看其他设备历史不能误发给当前连接。
- 原生会话页接入多行文字、Enter/Shift+Enter、组合输入标记、图片/文件多选和拖放；超长正文提示并保留草稿。图片通过系统异步缩略图请求 640 像素，不改写原文件；无法预览或文件移走时保留附件信息并提示。**图片/代码/PDF/DOCX 预览已完成原生 UI 检查，中文输入法与完整拖放矩阵仍待实际验收。**
- 名称限制 1–48 字符、128 UTF-8 字节、无控制字符；名称/目录先持久化再更新对象，目录先探测写入。旧 `history.json` 仍由传输记录入口切换收/发查看，清空它不会删除会话正文或磁盘文件。
- `AppInstance` 注册单实例并重定向激活；关闭窗口隐藏并继续连接，开始菜单再次启动恢复，最小化时还原；设置页底部“应用运行”中的“退出应用”才取消网络退出。没有托盘入口。**独立预览已验证关闭隐藏、再次启动恢复和设置页退出；最小化还原仍需补充实际 UI 验收。**
- `AttachmentPreview` 按扩展名识别图片、文本/代码、PDF 与 DOCX；原生 `AttachmentPreviewDialog` 提供代码行号与基础着色、文本复制、图片缩放及 PDF 翻页。DOCX 只提取正文，禁用 XML 外部实体，按文件/正文大小和字符/行数限流；预览不运行代码，也不上传文件。
- 会话列表右键支持打开、隐藏、删除记录；设置页可逐项或全部恢复隐藏会话。`conversations.json` 仍为 version=1，新增可选 `hiddenFingerprints`；原子提交同时更新消息和隐藏状态，新增收到的消息自动解除隐藏，更新已有消息/回执不解除。删除只清除指定认证指纹的消息及隐藏状态，保留磁盘文件，活动会话/传输先结束后再删除。行内操作从列表项容器获取实际对象，防止列表复用时误操作其他会话或文件。
- NuGet 固定 Windows App SDK 2.5.1、SDK BuildTools 10.0.28000.2705、WinApp 0.7.1，取代通配符。

历史 1.2.0 验收记录（2026-10-24）：Windows 虚拟机使用独立 `C:\Tools\FeigeDotnet` SDK 和 `C:\Tools\FeigeV3` 测试副本，未复用 macOS 构建产物；当时两个目标完成了编译和自包含发布。x64 EXE 已在 ARM64 Windows 的兼容模式启动并验证 v3 监听，仍需 Intel/AMD 实机安装验收。此前 AppX 队列阻塞已由用户启用开发者模式、重启后恢复；用户确认 Windows↔Mac 基本互通。普通用户使用新增 EXE 安装流程，见 [Windows README](../../Windows/README.md)。这些结果不代表当前源码已完成 Windows 构建或打包。

API 参考：[文字组合输入事件](https://learn.microsoft.com/en-us/windows/windows-app-sdk/api/winrt/microsoft.ui.xaml.controls.textbox.textcompositionstarted)、[单实例激活](https://learn.microsoft.com/en-us/windows/apps/develop/launch/multi-instance-apps)。

## 技术目标

Windows 10/11 原生客户端采用 WinUI 3 + Windows App SDK，UI 遵循 Fluent Design。不得把旧网页壳作为 Windows 最终交付形态。

## 开发环境

- Windows 10/11 开发机（最低 Windows 10 1809 / build 17763）
- Visual Studio 2026（18.x），工作负载：**WinUI 应用程序开发**
  （组件 `ManagedDesktop` + `Universal` + `ComponentGroup.WindowsAppSDK.Cs`）
- .NET 10 SDK（LTS）
- Windows App SDK 2.5.1（稳定版）
- Windows SDK 10.0.26100 系列（**非必需**：C# 工程的打包工具由 NuGet `Microsoft.Windows.SDK.BuildTools` 提供）
- 签名证书（正式发布）

完整安装步骤、版本决策依据和本机实测记录见 [`Windows环境搭建.md`](EnvironmentSetup.md)；
`Project/Windows/scripts/` 下提供环境体检（`Check-DevEnv.ps1`）、一键安装（`Install-DevEnv.ps1`）
和端到端冒烟测试（`Test-Toolchain.ps1`）脚本。

> 早期文档写的 Visual Studio 2022 已不满足官方 WinUI 3 要求，以环境搭建文档为准。

## 工程结构

- `Feige.Windows.sln`（已创建，经典 `.sln` 格式：.NET 10 的 `dotnet new sln` 默认产出 `.slnx`，需显式 `--format sln`）
- `src/Feige.Core`（已创建，`net10.0` 类库）：协议模型、加密、连接装配、mDNS 发现、传输状态机与安全落盘
- `tests/Feige.Core.Tests`（已创建，xunit）：协议单测与向量回归
- `src/Feige.App`（已创建）：WinUI 3 Packaged App、导航、设备列表/发送、接收、设置

```powershell
dotnet test Windows/tests/Feige.Core.Tests/Feige.Core.Tests.csproj # 目标开发机 .NET 10 下执行；参考源码当前总数 449 项，交付包基线 392 项
```

`Feige.Core` 目标框架是 `net10.0` 而非 `net10.0-windows`：协议与加密层不依赖 Windows API，这样才能在任何机器和 CI 上跑测试；只有 `Feige.App` 需要 WinUI 与 Windows 目标框架。

## v1 加密依赖（已定：BouncyCastle.Cryptography 2.7.0）

v1 实现于 2026-10-23 定稿。v1 的 X25519 与 16 字节 nonce 使用 BouncyCastle；新增 v3 核心继续复用 X25519 封装，记录层改用 .NET `AesGcm` 的 12 字节 nonce。下表仅描述 v1：

| 能力 | 实现 | 原因 |
| --- | --- | --- |
| X25519 | `Org.BouncyCastle.Crypto.Agreement.X25519Agreement` | .NET 10 无 X25519 类型；Windows CNG 的 ECDH 只有 NIST 曲线，不含 Curve25519；原生 `X25519DiffieHellman` 要到 .NET 11（STS，不可用） |
| AES-256-GCM | `Org.BouncyCastle.Crypto.Modes.GcmBlockCipher` | 协议 nonce 为 **16 字节**（8 字节前缀 + 8 字节大端序列号），而 `System.Security.Cryptography.AesGcm` 只接受 12 字节，实测抛 `ArgumentException: The specified nonce is not a valid size for this algorithm` |
| RSA-3072 / PSS | .NET `RSA` + `RSASignaturePadding.Pss` | salt 长度等于摘要长度 32，与 macOS 一致 |
| SPKI 导出 | `RSA.ExportSubjectPublicKeyInfo()` | 与 macOS 指纹字节一致 |
| HKDF-SHA256 | `HKDF.DeriveKey` | salt = transcript 摘要，info 见协议 §3.9 |
| SHA-256 / CSPRNG | `SHA256.HashData` / `RandomNumberGenerator.Fill` | — |

取舍：选纯托管的 BouncyCastle 而不是 libsodium（`Sodium.Core` + 原生 dll）。理由是 MSIX 单包同时覆盖 x64/ARM64，不需要按架构分发原生库，也不会因为原生库加载失败在握手时直接崩溃。代价是约 1 MB 依赖体积，以及一个必须锁版本的第三方加密库。

**约束**：版本锁在 `src/Feige.Core/Feige.Core.csproj`（2.7.0），升级必须重跑 `dotnet test`；BouncyCastle 只允许出现在 `Feige.Core/Crypto/` 下，协议其余部分只用 .NET 内置类型，便于将来 .NET 补上 X25519 后替换。

**验证记录**（`Project/Windows/src/Feige.Core/Crypto/` 的由来）：

1. RFC 7748 A.5 两组标量乘向量、6.1 的 alice/bob 公钥与共享密钥，全部一致；
2. 小阶对端（全零公钥）被拒绝，符合 §3.9「全零视为失败」；
3. 协议密钥派生用固定的私钥/对端公钥跑出 `shared`、`i2r`、`r2i`，与不共享代码的 Node 实现（`crypto.diffieHellman` + `hkdfSync`）**四个值逐字节相同**；
4. 用 §8.2 的记录帧验证 16 字节 nonce 能被 BouncyCastle 解密，`.NET AesGcm` 则直接拒绝。

## 记录层实现要点（已落地）

`Crypto/RecordCipher`（单方向状态机）与 `Protocol/AuthenticatedRecordStream`（分帧与重组）对应协议 §4.1、§4.2。

| 项 | 实现 |
| --- | --- |
| 帧布局 | `length(u32) \| type(1) \| sequence(8) \| ctLen(4) \| ciphertext \| tag(16)`；`length` 不含自身；`ctLen` 含 tag |
| AAD | 13 字节 header，序列号因此在认证范围内 |
| nonce | 16 字节 `prefix(8) \| seq(8, BE)`，由 `SessionKeySchedule.BuildNonce` 统一构造 |
| 序列号 | 每方向独立从 0 起，重复/跳号/乱序即 `UnexpectedSequence`；不改写、不重置 |
| 载荷 | 1..65536，仅 `finish` 允许 0；`chunk` 的业务上限 65408 属于传输层 |
| 截断检测 | `Append` 只在凑齐整条后解密；`EndOfStream` 要求恰好在 `finish` 边界 |
| 长度防护 | 先校验 `length` 落在 29..65565 再等待正文；未读缓冲上限 8 条最大帧 |

错误统一抛 `ProtocolException`，带 `ProtocolError` 枚举（`InvalidLength` / `InvalidType` / `UnexpectedSequence` / `AuthenticationFailed` / `SequenceExhausted` / `AlreadyFinished` / `UnexpectedEndOfStream` / `FrameTooLarge` / `UnexpectedTrailingData`）。异常消息**不含**记录内容、密钥或路径，可直接进日志；协议错误一律断开连接（§4.6）。

BouncyCastle 2.7.0 的两个 API 细节：`AesEngine.NewInstance()` 在该版本不可用（`CS0117`），用 `new AesEngine()`；每次加解密都要新建 `GcmBlockCipher` 并重新 `Init`，不要复用同一个实例跨记录调用。

验证方式：`tests/Feige.Core.Tests` 用 §8.2/§8.3 向量逐字节比对密文与 tag，并覆盖重放、跳号、篡改 type/序列号/密文、`ctLen` 不一致、空载荷、65536 边界、1 字节分片重组、双向密钥镜像；向量本身由 `scripts/verify-protocol-vectors.js` 独立生成。

## 握手层实现要点（已落地）

组件与协议章节的对应关系：

| 类型 | 职责 | 章节 |
| --- | --- | --- |
| `Protocol/HandshakeOffer` | 字段校验（version/role/deviceName/SPKI/临时公钥/nonce/配对码）、结构相等 | §3.3 |
| `Protocol/HandshakeTranscript` | offer 交叉校验、二进制 transcript、摘要、SAS、对端指纹 | §3.4 §3.5 §3.10 |
| `Crypto/DeviceFingerprint` | `SHA-256(SPKI)` 大写分组格式化、空格/大小写归一化比较 | §3.6 |
| `Crypto/IdentityKey` | RSA-3072 生成/导入、SPKI 导出、RSASSA-PSS 签名与验签 | §3.6 |
| `Protocol/HandshakeMessages` | offer/proof 的 JSON 编解码、`u32` 分帧 | §3.1 §3.3 §3.7 |
| `Protocol/HandshakeFrameReader` | 按字节流重组握手消息 | §3.1 |
| `Protocol/LocalHandshake` | 四消息时序、验签、本地确认、派生会话密钥 | §3.2 §3.8 §3.9 |
| `Protocol/StreamHandshakeDuplex` | `Stream`（`NetworkStream`）适配 `IHandshakeDuplex` | §3.1 |

几个容易写错的地方：

- **先写 offer 再读对端 offer**，否则两端互等死锁。已用真实 TCP 回环测过。
- **签名对象是 transcript 原始字节**，不是它的 SHA-256；PSS 内部自己摘要。
- **SPKI 而不是 PKCS#1**：`RSA.ExportSubjectPublicKeyInfo()`。用错编码两端指纹对不上。
- **proof 里回显的 offer 必须与之前收到的逐字段相等**再验签，否则可以换公钥（§3.7）。`HandshakeOffer` 因此实现了字节数组的结构相等，默认的 record 相等只比引用。
- **PSS salt 长度 32**：.NET `RSASignaturePadding.Pss` 默认就是「salt = 摘要长度」，与 macOS `rsaSignatureMessagePSSSHA256` 一致。已用 OpenSSL 双向实证：OpenSSL 签 → .NET 验通过；.NET 签 → OpenSSL `rsa_pss_saltlen:32` 判 `Verified OK`。
- **本地确认通过后才派生密钥**；拒绝时抛 `UserRejected`，不产生任何会话密钥（§3.8）。
- 临时私钥只存在于 `LocalHandshake` 内，`Dispose` 时 `ZeroMemory`，不进 offer、不落盘、不复用。

验证方式：§8.1 的两个 offer 编码、摘要 `779855ca…`、SAS `472138`、两个指纹逐字节比对；OpenSSL 生成的 PSS 签名固化为单测向量；握手流程用内存通道与 TCP 回环各跑一遍，并覆盖配对码不一致、用户拒绝、篡改签名、proof 回显不同 offer、小阶临时公钥。

## 传输层实现要点（已落地）

| 类型 | 职责 | 章节 |
| --- | --- | --- |
| `Protocol/TransferManifest` | manifest JSON 编解码、文件名/大小/摘要校验、`ForFileAsync` | §4.4 |
| `Storage/TransferFileSink` | 临时文件流式写 + 增量 SHA-256 + 校验后原子重命名 | §5.1 |
| `Storage/SafeFileName` | Windows 非法字符、保留设备名、尾随点空格的处理 | 本地存储 |
| `Protocol/IRecordChannel` / `StreamRecordChannel` | 把已握手连接适配成记录通道 | §4 |
| `Protocol/TransferSender` | manifest → decision → chunk → finish → receipt | §5.1 |
| `Protocol/TransferReceiver` | manifest → 用户确认 → chunk 落盘 → 校验 → 重命名 → receipt | §5.1 |

关键顺序与约束：

- **先校验再重命名，重命名成功才发 receipt。** `TransferFileSink.CommitAsync` 内部完成
  「字节数检查 → SHA-256 常量时间比较 → 原子重命名」，接收方拿不到最终路径就不可能发 receipt。
  这样 §5.3 记录的 macOS 缺陷（先发 receipt 后重命名）在本实现里**无法表达**。
- **确认前不碰磁盘**：`approve` 回调返回 true 之后才创建临时文件；被拒绝时回 `decision 0x00` 并抛
  `UserRejected`，目录都不会被创建。
- **临时文件先建**：在发送 `decision 0x01` 之前创建 sink，目录不可写就不会让对端白传 100 MiB。
- chunk 上限 **65408**（记录层允许 65536，业务层更严），累计长度不得超过 `manifest.size`。
- 偏移是隐含的（按顺序累加），所以只校验「单块 1..65408」和「累计 ≤ size」；收满后必须读到 `finish`
  才提交，不接受用 chunk 数量凑够大小。
- 默认不覆盖：目标名被占用时依次尝试 `name (1).ext`、`name (2).ext`，最多 1000 次；`File.Move(overwrite: false)`
  失败且目标存在才换下一个。
- 失败路径统一 `await using` 清理临时文件，不会在最终文件名下留半成品。

**一个必须注意的坑（已修）**：握手最后一条消息与第一条记录可能落在同一次 `Read` 里——对端读完我们的 proof
后立刻发 manifest，而我们的读取还在进行。`HandshakeFrameReader` 会把多读到的字节留在内部缓冲，
所以握手结束后必须调用 `StreamHandshakeDuplex.TakeRemainder()` 并传给
`new StreamRecordChannel(stream, keys, remainder)`，否则首条记录的头几个字节被丢弃，表现为「接收端一直等数据」。
`TransferFlowTests.StreamRecordChannel_ConsumesHandshakeRemainder` 专门锁住这个行为。

Windows 专属：协议只禁止 `/`、`\` 和控制字符，而 Windows 还不允许 `<>:"|?*`、保留设备名（`CON`/`NUL`/`COM1`…）
和尾随点空格。`SafeFileName.MakeWritable` 只在必要时改写，合法名字原样保留；实际落盘名以 `ReceivedFile.Path` 为准。

验证方式：§8.2/§8.3 的等价逻辑之外，新增完整栈测试——真实 TCP + 真实握手 + 120000 字节 AES-GCM 传输，
比对落盘字节与摘要；另覆盖接收方拒绝、摘要不符、超大 chunk、累计越界、manifest 之前的记录、源文件比 manifest 短。

## 连接装配层实现要点（已落地）

把「协议已实现的部分」拼成一条真正可用的连接：`Net/` 下 `PeerListener`（应答方）、`PeerConnector`（发起方）、
`PeerSession`（已认证会话）、`TimeoutStream`、`ConnectionOptions`、`LocalDevice`、`PeerTarget`。

| 类型 | 职责 |
| --- | --- |
| `Net/PeerListener` | 双栈 `TcpListener`（`IPv6Any` + `DualMode`，一个 socket 同时收 IPv4/IPv6），接到连接后以 responder 跑握手 |
| `Net/PeerConnector` | `TcpClient` 连接 + initiator 握手，失败即关闭 socket |
| `Net/PeerSession` | 握手结果 + 记录通道，`SendFileAsync`/`SendAsync`/`ReceiveFileAsync`，一连接只允许一次传输 |
| `Net/TimeoutStream` | 逐次读写超时，超时转成 `ProtocolError.Timeout`；`leaveOpen` 支持同一 socket 被包两层 |
| `Net/PeerHandshakeRunner` | 两者共用的尾部：握手 → `TakeRemainder()` → 记录通道 |

几个必须知道的设计点：

- **超时是「空闲」而非「总时长」**：`TimeoutStream` 每次 `ReadAsync`/`WriteAsync` 各给一份预算，
  所以传 100 MiB 只要数据在流动就不会被掐断；`ProtocolError.Timeout` 是本地策略错误码，不在 wire 上。
- **用户确认不受网络超时约束**：`approve` 在「读完对方 proof」和「派生密钥」之间被调用，这段没有读写在等，
  所以 15 秒握手超时不会把正在看 SAS 弹窗的用户踢下线。5 分钟确认窗口属于 UI，Core 不实现。
- **配对码必须由调用方显式给出**，`sessionCode` 不再是可选参数。之前允许传 null 各端自行随机生成，
  结果是必然的 `PairingCodeMismatch`；现在传 null 或非 6 位数字直接 `ArgumentException`。
- **一次连接一次传输写进了 API**：`PeerSession` 用 `Interlocked` 抢占，第二次调用抛 `InvalidOperationException`，
  不依赖调用方自觉。这也是 §1「传下一个文件要重新握手」的落地方式。

**Windows 特有的坑（已修）**：`TcpClient.ConnectAsync(endpoint, cancellationToken)` 在连接被拒时抛的是
`OperationCanceledException`，真正的 `SocketException` 被吞掉——UI 上会把「对端没开端口」显示成「超时」。
改成 `ConnectAsync(endpoint)` + `WaitAsync(timeout)`，并观察被放弃的 connect 任务，异常类型才正确。
本机实测 loopback 拒绝约需 2 秒（SYN 重试），所以 `ConnectTimeout` 默认 5 秒、测试里给 10 秒。

验证方式：真实 loopback socket 上的双向文件传输（发起方发 / 应答方发各一条）、receipt 与落盘顺序、
一连接二次传输被拒、配对码不一致两端同时报错、用户拒绝、静默对端超时后监听器仍可用、取消 accept、
连接被拒的错误类型、`TimeoutStream` 超时映射。

## 发现层实现要点（已落地）

Windows 没有 Bonjour 服务，WinRT 的 `Windows.Networking.ServiceDiscovery.Dnssd` 只能**注册**服务、不能浏览，
所以 DNS-SD 由客户端自己实现，`Discovery/` 目录不依赖任何第三方包。

| 类型 | 职责 |
| --- | --- |
| `Discovery/DnsName` | 标签数组（不用点分字符串，实例名本身可含点）；大小写不敏感比较 |
| `Discovery/MdnsMessage` | DNS 报文编解码：PTR/SRV/TXT/A/AAAA，**解析支持压缩指针**，写出不压缩 |
| `Discovery/UdpMdnsTransport` | 5353 双栈 socket、逐网卡加入 224.0.0.251 / ff02::fb、收发循环 |
| `Discovery/MdnsAdvertiser` | 应答 PTR/SRV/TXT/A，启动三连公告，退出发 TTL=0 告别 |
| `Discovery/PeerBrowser` | PTR → SRV → TXT → A 分段解析，TTL 过期与 goodbye 移除 |
| `Discovery/LanDiscovery` | 门面：一个 transport 同时喂应答方与浏览方（mDNS 只有一个端口，必须共用） |

服务定义（协议 §1 原本留白，现在按此实现）：

| 项 | 值 |
| --- | --- |
| 服务类型 | `_feige._tcp.local` |
| 实例名 | 设备名（截断到 63 字节 DNS 标签上限），完整名放 TXT |
| SRV 端口 | `PeerListener` 的端口；**绑定 0 由系统分配**，再把实际端口通告出去 |
| TXT | `v=1`、`name=<设备名>`、`fp=<指纹去空格>` |
| TTL | PTR 4500 秒，SRV/TXT/A 120 秒 |

**安全边界**：mDNS 完全没有认证，谁都能广播任意 `fp`。所以 TXT 里的指纹只用于设备列表展示，
用户确认与信任判断必须来自握手（`HandshakePrompt.PeerFingerprint` 与 SAS）。

两个实现期踩到的坑：

- **`DnsReader` 是 ref struct，跨函数传参必须 `ref`**。按值传会把内部的读取位置复制一份，
  返回后外层位置没变，表现为「additional 里又出现一遍 answer 记录、后面的 A 记录全丢」——
  这类错只会让对端漏掉地址，不会报错，很难从现象反推。
- **`GetIPv6Properties()` 在没有 IPv6 协议栈的网卡（虚拟网卡、部分 USB 网卡）上抛
  `NetworkInformationException`**，不是返回 null。必须逐网卡 catch，否则一台机器上只要有一块这样的网卡，
  整个发现层都起不来。`LocalAddresses()` 里的 `UnicastAddresses` 同理。

验证方式：编解码往返（五种记录类型、Cache-flush 位、TXT 键值）、手写字节的压缩指针解析（含自引用指针必须被拒）、
畸形报文返回 null 而不抛、内存介质上的完整 advertise → browse → goodbye 流程（含自我过滤、指纹与端口）、
QU 位与非 5353 源端口回单播、TTL 到期自动移除、真实 socket 的绑定/入组/收发冒烟。
同进程内两个实例共用 5353 的端到端测试不可靠（`SO_REUSEADDR` 下多播投递不保证），所以端到端用内存介质，
真实双机互通尚未验收；当前需先将 Windows 升级至 macOS 已采用的 v3。

## 应用层实现要点（已落地）

`src/Feige.App`：WinUI 3 Packaged App，TFM `net10.0-windows10.0.26100.0`，导航三页（发送 / 接收 / 设置）。

| 位置 | 职责 |
| --- | --- |
| `Pages/SendPage` | 设备列表（mDNS 发现）、刷新、手动 IP:端口、选文件 → 输配对码 → 发送与进度 |
| `Pages/ReceivePage` | 大字配对码与「换一个」、监听端口、接收文件夹、本次已接收列表 |
| `Pages/SettingsPage` | 设备名（改名后重新广播）、接收文件夹、本机指纹、版本与协议说明 |
| `Services/AppSession` | 单例：身份、监听器、发现、接收循环；事件全部发布到 UI 线程 |
| `Services/UiPromptService` | 把同步确认回调桥接成 `ContentDialog`（SAS、文件确认） |
| `Services/IdentityStore` | DPAPI 加密的身份文件 |
| `Services/AppSettings` | 设备名与接收文件夹（LocalSettings）、应用数据目录 |
| `Services/Notifier` | 系统通知（`ToastNotificationManager`，可降级） |
| `Services/AppLog` | 落盘的诊断日志（打包应用没有控制台） |
| `Services/ErrorText` | 协议错误 → 用户能照着做的中文提示 |

几个必须记住的约定：

- **拨号方发送，监听方接收。** 协议 §1 允许握手后任一端发送，但没有「谁先发」的协商记录，
  应用层把这条约定固定下来，而不是靠猜。
- **同步确认要跨线程，不能在 UI 线程上等。** `LocalHandshake.RunAsync` 与 `TransferReceiver.ReceiveAsync` 的
  `approve` 是同步委托，而两者都在线程池线程上推进，所以那里阻塞是安全的；`UiPromptService` 用
  `DispatcherQueue.TryEnqueue` + `TaskCompletionSource` 把对话框结果同步取回，并**显式拒绝在 UI 线程调用**
  （在 UI 线程上同步等自己的对话框必然死锁）。
- **会话事件只在 UI 线程抛。** `AppSession` 所有 `ObservableCollection` 变更与事件都经 `Post()` 路由，
  页面因此可以直接改控件，不需要各自 marshal。
- **首次启动会卡一秒**：RSA-3072 生成放在 `Task.Run` 里，页面此时显示「正在准备本机身份…」。
- **清单必须声明 `privateNetworkClientServer`**：否则打包应用在局域网上的入站连接与组播会被静默拦截，
  现象是「能发现别人、别人连不上」；`runFullTrust` 保留用于 DPAPI 与文件系统访问。

启动自检（已实测）：`dotnet run --project src/Feige.App` 以 MSIX 调试身份启动，
`netstat -ano | findstr <PID>` 同时出现一个随机 TCP 端口（双栈）与 UDP 5353（v4/v6），说明监听与发现都起来了。

### 事件必须经 `Post` 抛出（踩过坑，别省）

`AppSession` 的每个事件——`StatusChanged`、`ProgressChanged`、`Failed`、`FileReceived`、`FileSent`、
`StateChanged`——都由 `Post()` 统一 marshal 到 UI 线程，页面处理器因此可以直接改控件。

这条规则不是洁癖，是修过一个**静默致命**的 bug 之后写下来的：接收循环跑在线程池上，`Failed` 曾经是直接
`Invoke`。页面处理器里 `InfoBar.Severity = ...` 在非 UI 线程抛 `RPC_E_WRONG_THREAD`
（"应用程序调用一个已为另一线程整理的接口"），而它是在**循环自己的 catch 块里**抛出的——于是异常逃出
循环，`Task.Run` 的返回值又没人 await，接收服务就此死掉，且日志里一个字都没有。表现是
「第一次接收失败之后，再也收不到任何文件」，连接全部堆在 accept 队列里（`netstat` 可见 `CloseWait`）。

所以接收循环现在还有一层最外层 `try/catch`，一旦跑飞会记 `accept: loop aborted` 并提示用户。
一般的 `OperationCanceledException` 只在自己那个 token 被取消时才 `break`——单个连接超时取消不该终止整个服务。

### 系统通知

用 `Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier()`，**不用**
`Microsoft.Windows.AppNotifications.AppNotificationManager`。

后者是文档推荐给 WinUI 3 的 API，但它的 `Register()` 在本机稳定抛 `0x80070490 (Element not found)`，
即便清单里按文档写全了 `com:ExeServer` + `windows.toastNotificationActivation`（踩到两个 schema 细节：
`com:Class/@Id` 与 `ToastActivatorCLSID` 都**不能**带花括号，带 `{}` 会被 MakeAppx 拒绝）。
`AppNotificationManager` 比旧 API 多给的只有「点击通知激活应用」——而传输通知没有可激活的东西，窗口就是
你处理它的地方。所以这里直接换掉依赖，而不是绕过：`ToastNotificationManager` 在**有包标识**时无需注册、
无需 CLSID、无需清单声明，也不用在退出时反注册。相关清单声明已全部移除。

- 触发点：接收完成、发送完成、接收失败。**配对码不匹配不弹**——局域网上任何人拨号都可能打错码，
  那是常态，窗口内的状态行足够。
- 内容用 `XmlDocument` 的 DOM 拼，不用字符串拼接：文件名里的 `<`、`&`、引号不会破坏载荷。
- 通知永远是可选的：取不到 notifier 时 `IsAvailable` 为 `false`，状态提示降级到窗口内。设置页有
  「发送测试通知」按钮和当前状态，方便用户自查。

### 传输记录（跨会话）

`Feige.Core.Storage.TransferHistory`：`LocalFolder\history.json`，最多 `MaxEntries = 100` 条，新的在前。

- 放在 `Feige.Core` 而不是应用层，因为它只依赖文件系统；应用层只提供目录（`AppSettings.AppDataFolder`）。
  这样纯 .NET 测试能覆盖格式解析、坏行跳过、裁剪与原子写。
- 写入走临时文件 + `File.Move(overwrite: true)`，崩在写一半不会留下截断的 JSON。
- 读取时**逐行校验**：缺 `name`/`path`、`size` 为负、方向字段无法识别的行直接丢弃，而不是整份失败；
  文件损坏则当作空历史。历史上限、坏文件、坏行都不能阻断启动。
- 未知方向回落为「接收」，便于格式演进。

### 诊断日志

`LocalFolder\app.log`（`Services/AppLog`，仅保留最后 256 KB）。打包应用没有控制台，启动失败否则什么
痕迹都不留。记录内容：通知初始化结果、监听端口、发现层是否起来、接收循环起止、每次收发失败的类型。

- 认证线以下的东西一律不写：不写密钥、不写文件内容、不写完整路径（沿用开发总则的安全约束）。
- 逐连接的失败只写 `类型: 消息`，不写堆栈——一个坏对端就是几十行；启动失败与循环跑飞才写完整堆栈。
- `Test-MsixPackage.ps1` 在端口断言失败时会自动把这份日志的最后 15 行打出来。

未完成：与 macOS 的双机互通验收。

## 签名与打包（已落地）

两个脚本：`scripts/Build-Msix.ps1`（产出签名包）、`scripts/Test-MsixPackage.ps1`（侧载验收）。

### 包身份

| 字段 | 取值 |
| --- | --- |
| `Identity/@Name` | `FeigeTransfer.Windows` |
| `Identity/@Publisher` | `CN=Songli` |
| `Properties/@PublisherDisplayName` | `Songli` |
| `DisplayName` | 飞鸽快传 |
| 版本 | `1.0.0.0`（四段，MSIX 必需） |
| 能力 | `privateNetworkClientServer` + `runFullTrust` |

发布者主体与签名证书 `Subject` 必须**完全一致**，`Build-Msix.ps1` 在签名前会比对，不一致直接报错——
这是侧载安装最常见的失败点，报错信息是 `0x800B0109`（证书链不受信任）或 `0x80073CF3`（发布者不匹配）。

### 构建

```powershell
# 首次：生成自签名开发证书（CN=Songli）并导入当前用户 My 存储
.\scripts\Build-Msix.ps1 -NewDevCert

# 之后：复用友好名 "Feige Dev Signing" 的证书
.\scripts\Build-Msix.ps1

# 指定证书
.\scripts\Build-Msix.ps1 -CertThumbprint <40 位指纹> -TrustDevCert
```

脚本内部是 `dotnet restore` + `dotnet msbuild -t:Publish
-p:AppxBundle=Never -p:UapAppxPackageBuildMode=SideloadOnly -p:AppxPackageSigningEnabled=false
-p:PublishReadyToRun=false`，再用 `signtool sign /fd SHA256` 手工签名。
**关掉 MSBuild 的签名步骤是刻意的**：它只认 `.pfx` 文件路径，而开发证书通常装在证书存储里；
拆开之后证书来源（存储 / pfx）与签名动作互不绑定，也便于在 CI 上用环境变量注入。

用 `msbuild -t:Publish` 而不是 `dotnet publish` 的原因见 [本地调试与发布流程](LocalDebugAndRelease.md)：CLI 包装器会按发布配置文件改写输出目录，并可能在整个产物流失时仍返回 0。`dotnet msbuild` 也不执行 `dotnet publish` 的隐式还原，所以两个打包脚本都先显式 `dotnet restore`，否则干净克隆会报 `NETSDK1004`。

历史构建文档中的 MSIX 示例路径为 `download/msix/`；新项目的安装包、MSIX 包及校验文件都直接放在 `Release/Windows/<version>/`，例如 `Release/Windows/1.2.1/FeigeSetup-1.2.1-b1301-x64.exe` 或 `Release/Windows/1.2.1/Feige.App_<ver>_x64_Test/Feige.App_<ver>_x64.msix`（未裁剪时约 **81 MB**），不再额外建立 `EXE/`、`MSIX/` 子目录。

- **`PublishTrimmed` 必须为 `false`**：模板对 Release 默认开启裁剪，而 WinUI 3 的 XAML 类型解析与 WinRT
  投影依赖反射元数据，裁剪器看不见它们——包能构建出来，装上去一启动就崩。这一项只能整体关掉，没有
  中间档位。
- `PublishReadyToRun` 在安装包（`Build-Installer.ps1`）路径保持 Release 开启，只影响启动速度，与裁剪无关。
  **MSIX 路径必须显式关闭**：`Build-Msix.ps1` 不传 `RuntimeIdentifier`，而 csproj 对 Release 开启 ReadyToRun，
  会让发布直接失败（`NETSDK1094`：找不到有效的运行时包）；架构由单项目 MSIX 自己决定。
- 证书与私钥落在 `Project/Windows/.certs/`，已在 `.gitignore` 中排除（`*.pfx` / `*.cer` / `.certs/`）。
  仓库里只有脚本，谁构建谁生成证书。

### 签名校验的一个坑

`signtool verify /pa /r <root.cer>` 对自签名证书会报
`Signing Cert does not chain to the specified Root Cert`——即使证书就在信任存储里。
因为自签名证书自己是根，但 `signtool` 要求根证书与签名证书**分开**校验，而这里二者是同一张。
脚本因此在 `signtool verify` 失败时回退到 `Get-AuthenticodeSignature`，比对
`SignerCertificate.Thumbprint` 与期望指纹，一致则仅告警不中断，最终以「能不能装上」为准。

### 侧载验收

```powershell
.\scripts\Test-MsixPackage.ps1          # 装 → 启 → 验端口 → 卸
.\scripts\Test-MsixPackage.ps1 -KeepInstalled   # 保留安装
```

流程与断言：

1. 证书导入 `LocalMachine\TrustedPeople`（提权时）或 `CurrentUser\TrustedPeople`（否则）。
2. **先卸载已有包**，保证是干净安装而不是升级。
3. `Add-AppxPackage` → 断言包已注册，打印 `PackageFullName` 与 `Publisher`。
4. `shell:AppsFolder\<PackageFamilyName>!App` 启动，轮询 30 秒等待
   「进程出现 + 至少一个 TCP 监听 + UDP 5353 已绑定」三个条件同时成立。
5. 杀进程 → `Remove-AppxPackage` → 断言包与进程都无残留。

退出码 0 表示全通过。实测输出：

```
PASS: package registered
Publisher: CN=Songli
Process appeared: Feige.App (PID 33492)
PASS: TCP listener is open (port 55837)
PASS: mDNS socket 5353 is bound
PASS: package removed
PASS: no process left
Sideload acceptance passed.
```

两个脚本都开了 `Set-StrictMode -Version Latest`，写的时候踩到两处：

- **`Get-TcpListeners` 的空数组会被展开成 `$null`**。函数里 `return @(...)` 在结果为空时不返回数组而返回
  空，调用方 `$x.Count` 立刻抛 `PropertyNotFoundException`——报错位置指向 `.Count`，离真正的空结果很远。
  修法是 `return ,$items`（逗号阻止展开），这样 `$null`、单元素、多元素三种情况行为一致。
- **`Add-AppxPackage` 的部署进度条**在重定向日志里会把每一帧都写一遍，几千行噪音。开头设
  `$ProgressPreference = "SilentlyContinue"`。

需要实测安装才能证明的事情（签名有效性、清单能力是否够用）不要用静态检查替代：上面第 4 步的端口断言
是唯一能证明 `privateNetworkClientServer` 真的生效的方法。

## 实现约束

新增会话功能按 [v3 协议](../Public/ProtocolV3.md)实现；控制帧必须版本化、长度受限、字段严格验证。用户在建立连接时同意一次，会话内文件经分块流式接收、哈希验证及原子写入。

v1 历史协议 §5.3 曾记录旧 macOS 实现差异，该问题描述仅适用于当时的版本。v3 仍须在校验并完成原子落盘之后发送 `receipt`，按 transferID 校验 `finish`；一个文件结束后，会话可以继续承载后续消息与文件。

防火墙只引导必要授权，不要求关闭防护。MSIX 必须签名，并分别在干净 Windows 10/11 环境验证安装/升级/卸载。
