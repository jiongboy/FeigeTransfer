# 跨平台更新日志

> 只记录对双端或协作方式有影响的变更，按时间倒序排列。平台内部实现细节见各平台开发日志：
> [macOS 开发日志](../MacOS/DevelopmentLog.md) · [Windows 开发日志](../Windows/DevelopmentLog.md)

## 2026-11-03 正式发布 Windows 1.3.0 与 macOS 0.5.0

- 从 master `c6d68c4` 构建。Windows 1.3.0 build 1306 在 ROG 用发布证书签名（x64、arm64，带时间戳）；macOS 0.5.0 build 16 用 Developer ID 签名并经 Apple 公证（Accepted，已 staple），`spctl` 结果为 Notarized Developer ID。交付物、校验文件和发布说明位于 `Release/Windows/1.3.0/` 与 `Release/MacOS/0.5.0/`。
- GitHub：新建 `v1.3.0`（Windows，设为 Latest）与 `macos-v0.5.0`（不设 Latest，避免占用 Windows 回退源的 `latest/download`）。两者都只含安装包和校验文件；从 GitHub 重新下载后 SHA-256 一致。
- Mac mini 局域网更新源：Windows 清单切到 1.3.0 b1306，首次同时提供 x64 和 arm64；macOS 清单从 0.4.0 b11 切到 0.5.0 b16（0.4.1 未上过更新源）。版本包先上传到临时目录核对哈希，再整体改名为 `versions/<tag>`，经 HTTP 重新下载校验后才替换清单，旧清单保留为 `*.previous.json`。两份清单的 `protocolVersion` 均为 6。
- 发布中发现并修复：`Feige.UpdateTool sign` 仍按重构前的 `<root>/Windows/dist/build` 定位，改为 `Project/Windows/dist/build`；`New-UpdateRecord.ps1` 把 `protocolVersion` 写死为 3，改为必填参数 `-ProtocolVersion`；`Project/Windows/dist/` 实际被 `!Project/**` 重新纳入 Git，已补忽略规则。
- 未完成：两个正式包的干净机器安装、从 Windows 1.2.1 / macOS 0.4.1 的应用内升级均未实测。GitHub 回退源仍没有可用的 Windows 清单（局域网清单的下载地址不能用于 GitHub 来源）。

## 2026-11-03 身份密钥备份与正式发布准备

- 起因：同一台电脑卸载重装或资料目录被删后会生成新的身份密钥，对方看到的就是“另一台设备”，聊天因此分成多段。
- 双端在资料目录之外各保存一份已加密的身份密钥副本：macOS 存在钥匙串（通用密码项 `identity-sealed-key`，仅本机），Windows 存在 `HKCU\Software\Songli\FeigeTransfer` 的 `IdentityBackup`（DPAPI 当前用户保护）。启动时如果资料目录里的密钥文件不见了，就从副本恢复，指纹保持不变；旧安装升级后首次启动会自动补建副本。重装系统、换电脑或换 Windows 账户、清除钥匙串条目时仍会生成新身份，这种情况用“合并到其他会话…”处理。
- 发布准备：macOS 升到 0.5.0（build 16），Windows 升到 1.3.0（备用调试用的 MSIX 清单同步为 1.3.0.0）。确定 Windows 正式发布只提供 EXE，不发布 MSIX，规则、更新规范与调试指南已同步，已发布的 macOS 0.4.1 与 Windows 1.2.1 交付物保持原样。macOS 打包脚本在版本目录已有交付物时拒绝覆盖。Windows 启动日志里的协议版本改为读取广播能力常量。此前放进 `Release/Windows/1.2.1/` 的 b1303/b1304 未签名测试包移到 `Project/Windows/obj/TestInstallers/1.2.1/`（被 Git 忽略）。
- 密钥文件存在时以文件为准，副本不一致会被改写。两项 macOS 测试原先误用正式钥匙串 service，已改为各自独立的 service。
- 验证：Windows Core 616/616、App 7/7（新增注册表副本恢复测试）；macOS 应用 96 项（跳过 5 项）、核心 75 项全部通过，新增“删除资料目录后指纹不变”测试。正式包尚未生成：需要先提交并合并代码、完成界面与双机互通验收，再在 ROG 用正式证书签名 Windows 安装包、在 Mac 上用 Developer ID 签名并公证。

## 2026-11-03 会话手动合并与附近设备去重

- 起因：Windows 上同一台 Mac 出现两段会话。核实两段会话指纹不同：该 Mac 的应用资料目录（含身份）在 2026-11-03 09:15 被整体重建，改名本身不会改变指纹。按名称自动合并会让同名陌生设备混入，因此不做自动合并。
- 双端新增会话右键“合并到其他会话…”：用户确认是同一台设备后，旧会话记录改挂到所选会话的指纹下并按时间排序，旧会话、其常驻联系人设置与未读数一并清除；仍连接、有待发送或正在收发的会话不能合并。
- 附近设备列表按广播的完整指纹去重：已有可见会话的设备不再显示，Windows 会话标题按钮在未连接且对方在附近时显示“连接”；macOS 原按服务名去重，改为优先按指纹匹配，避免对方改名后重复显示。
- 验证：Windows Core 616/616（新增合并测试 2 项；修复 `LoopbackScanFindsResponder` 随机端口偶发冲突，改为向系统申请空闲端口），虚拟机内 App 6/6 并生成、安装 `1.2.1` b1304 ARM64 未签名测试包，截图确认附近设备不再重复显示；macOS 新增合并测试。合并操作的界面点击待人工验收。

## 2026-11-03 协作增强：跨网段、未读通知、常驻联系人、文件夹与续传（v6）、离线待发

- 按[开发计划](CollaborationEnhancementPlan.md)在双端实现五项能力：跨网段单播探测、未读计数与托盘/Dock/系统通知、常驻联系人自动连接（退避 30 秒/1/5/30 分钟）、[Protocol v6](ProtocolV6.md) 文件夹发送与断点续传、私聊离线待发队列。
- v6 只用于私聊，发现能力为 `caps=3,4,5,6`；群聊保持 v4/v5。离线待发不改线格式，消息记录新增可选字段 `outboxSince`，旧版本读取时忽略。
- 验证：macOS **169 项测试通过**（应用 95，其中 5 项依赖环境跳过；核心 74），含 v6 共享握手向量、续传/文件夹 8 项、离线待发 2 项（含真实回环连接的顺序投递、原 ID 去重、源文件缺失、拒绝不重试）；Windows **Core 614 项测试通过**。
- 未验证：Windows WinUI 工程未能完整编译（Windows 虚拟机无法建立会话）；已在 macOS 上关闭 XAML 编译单独编译全部 App C# 代码，剩余错误全部是 XAML 生成的控件名，并借此发现并修复两处编译错误。XAML 本身、界面与两台真实设备之间的 Mac↔Windows 互通（跨网段、续传、离线投递）均待实机验收。未打包。

## 2026-11-02 新手引导、群成员面板与预置头像

- 按[已批准需求](UserExperienceEnhancementRequirements.md)在双端实现：首次引导可选头像（可跳过），设备列表空状态显示“尚未找到设备/正在搜索附近设备…”并提供“重新搜索”“连接排查”；群聊标题处打开成员面板，显示成员数/20、头像、名称、群主/成员与在线状态，支持搜索；成员操作文案两端统一为“申请退出群聊”“移出群聊”“结束群聊”“退出并转让群主”。
- 10 个内置头像（`bird`、`cat`、`fox`、`panda`、`bear`、`fish`、`rocket`、`sun`、`star`、`leaf`）以 `Project/Ui/Avatars/` 为素材源，未知或缺失回退到 `bird`。跨设备只通过 Bonjour/mDNS TXT 可选 `avatar=` 字段传播 ID，且仅在完整指纹与已认证连接匹配后才保存；Protocol v4/v5 群控制消息不变。
- 验证：macOS 148 项测试通过（5 项跳过）；Windows WinUI（含 XAML）在 Parallels Windows 11 ARM64 构建通过，`Feige.App.Tests` 8/8，Core 590/590。界面点击、浅深色和跨端头像展示尚未人工验收。

## 2026-10-29 macOS：发布 LAN 更新源并修复升级提示关闭

- 配置独立 P-256 macOS 更新信任根与固定 Mac mini LAN 来源；`0.4.0` build 11 已发布，Windows RSA 更新密钥保持独立。
- 在 macOS 27 arm64 客户端完成 build 9 → 10 的完整应用内更新，以及 build 10 → 11 的修复版更新。
- 修复更新成功横幅的嵌套状态观察问题；build 11 自动重启后，点击关闭按钮可立即移除成功提示。
- 发布限制：包使用 Apple Distribution 签名，未公证；Mac mini macOS 12.7.6 Intel 仅作为 LAN 文件服务器，客户端测试在本机完成。公网回退和正式公证仍待处理。

## 2026-10-28 两端对齐：逐个文件接收确认、源码着色、剪贴板图片

- **macOS 新增逐个文件接收确认**：接收方在文件元数据到达后先看到“等待你确认接收”，可“接收”或“拒绝”。确认发生在创建暂存文件之前，拒绝不会写盘；拒绝后发送 `decision(false)` 并关闭连接并按“已拒绝接收”记录。复用既有 `decision` 记录，线格式未变，协议文档同步说明“接收方可以询问用户”与拒绝语义。
- 修正发送方等待 decision 的超时：Mac 原为 30 秒，与 Windows 的 2 分 10 秒不一致，用户犹豫会被误断线；已对齐为 130 秒。
- **macOS 源码预览增加语法着色**：与 Windows 使用同一套正则与四类 token，保留行号，超长文件转纯文本。
- **Windows 新增剪贴板图片粘贴**：Ctrl+V 可直接粘贴截图发送（剪贴板含文件时优先按文件处理）。
- 验证：Mac **114 项测试通过**（应用 62、核心 52，Bonjour 1 项跳过）；Windows **Core 466 项测试通过**（.NET 11 SDK roll-forward 运行 net10 目标）。对剪贴板代码做了基于真实 WinRT 签名的类型检查，发现并修复一处 `using var` 编译错误。WinUI 工程需 Windows 开发机编译与实机验证。未打包、未提交。

## 2026-10-28 macOS：彻底消除钥匙串密码弹窗

- 修复“每次安装/更新后启动都要输入 Mac 登录密码授权钥匙串”。根因有两条：打包脚本在未设置 `FEIGE_SIGN_IDENTITY` 时静默回退 ad-hoc 签名（无证书、无 Team ID，系统按代码哈希识别应用，每次更新都变成陌生程序）；设备身份又以 `kSecClassKey` 存入钥匙串，这类项必然绑定 ACL 授权。
- 打包脚本改为自动发现稳定签名证书（`Developer ID Application` → `Apple Distribution` → `FEIGE_SIGN_IDENTITY`），仅在无证书时回退 ad-hoc 并明确警告。
- 设备身份改存为 AES-256-GCM 加密文件（权限 600），包装密钥使用不经过 ACL 授权的钥匙串通用密码项；不再创建 key 类项。旧版本升级自动迁移并**保持设备指纹不变**。实际测试：重启与替换二进制后启动 4–5 秒就绪、无系统弹窗；ad-hoc 版本则卡在等待钥匙串授权。
- 安全取舍：私钥不再不可导出，同登录用户进程理论可读取；设备身份仅用于局域网设备互认，软件与更新签名使用独立证书。已写入 macOS README 与开发手册。
- 验证：macOS 106 项测试通过（应用 54、核心 52，Bonjour 1 项跳过）。0.4.0 仍未打包、未公证；当前为 `Apple Distribution` 证书，公开分发前需改用 `Developer ID Application` 并完成公证。

## 2026-10-28 macOS 0.4.0：同步 Windows 功能

- macOS 客户端补齐与 Windows 1.2.x 对应的能力：卡片式设置窗口（设备与连接信息、版本与更新、通知、本机记录、隐藏会话、身份、应用运行）、可关闭提示条、系统通知邀请与测试、按会话持久化草稿、内置表情面板、网络变化自动重建发现、手动连接地址校验、使用帮助入口。
- **应用内更新首次接入 macOS**：签名清单（P-256/Ed25519，CryptoKit 校验）、24 小时静默检查、用户确认下载、大小与 SHA-256 校验、等待收发结束、签名验证后原子替换并重启、结果提示。与 Windows 共用信封结构与签名原文前缀，算法和安装方式按平台独立；安装前校验代码签名，失败保留当前版本。尚未配置正式更新源与签名密钥，客户端明确显示未配置且不联网。
- 修复两个真实缺陷：新增设置字段会让旧版 `settings.json` 整体解码失败；更新源配置因变量遮蔽被静默视为未配置。
- 验证：Mac 99 项测试通过（应用 54、核心 45，Bonjour 1 项按条件跳过）；更新链路用本机回环签名清单实测检查/下载/校验/安装守卫；真实用户数据下应用与设置窗口可正常启动。未打包、未公证、未做真实升级验收。
- 未完成：正式更新密钥与更新源、真实“旧版 → 新版”升级、实体双机通知/网络切换矩阵。

## 2026-10-24 首次安装引导方案

- 新增[首次安装引导方案](FirstRunOnboarding.md)，设计“两页引导 + 主界面首次提示”：本机名称与接收目录、顺序身份与网络准备、可稍后处理、首次连接与可选通知。
- 明确旧用户与中断恢复逻辑，新默认接收目录不迁移已有位置；首次引导关闭窗口停止初始化，正常使用后的关闭仍保持后台运行。
- 同步公共索引、开发总则、UI 规范、更新检查时机与分支文档归属。本次仅整理方案，未修改客户端行为、打包或对外发布。

## 2026-10-24 应用更新共同规则

- 新增[应用更新规则](UpdatePolicy.md)，统一后台检查、用户确认下载与安装、收发结束后安全退出、平台更新渠道、签名验证、资料迁移、发布顺序和首版验收要求。
- 明确 Windows EXE、macOS Sparkle 与可选 MSIX 渠道分别接入，产品版本和通信协议分别管理；首次接入需手动升级一次。
- 同步公共索引、开发总则、UI 规范与分支文档归属。此项仅整理设计和验收规则，未实现客户端更新功能，未生成交付包或对外发布。

## 双平台能力快照（截至 2026-10-29）

| 能力 | macOS | Windows |
| --- | --- | --- |
| 协议 | v3 | 源码已采用 v3，旧 v1 安装包不兼容 |
| 设置与提示 | 卡片式设置窗口、可关闭提示条（0.4.0） | 卡片式设置页、InfoBar 提示 |
| 通知 | UserNotifications，邀请/测试/关闭（0.4.0） | 桌面 Toast，邀请/测试/关闭 |
| 应用更新 | 自有签名清单 + 原子替换（0.4.0 build 11，LAN 更新及提示关闭实测通过；未公证） | RSA 签名清单 + Inno 安装交接，已发布 1.2.1 |
| 自动发现与监听 | Bonjour 广播/浏览、TCP 接入应用 | v3 TXT/实例约定已在实现中，完整实机矩阵待验收 |
| 身份 | 持久化 RSA-3072、启动签名授权、运行时缓存 | DPAPI 身份持久化、签名预检与运行时复用 |
| 文件与消息 | 双向多批、文字/图片、空文件、流式校验与原子提交 | 双向多批、文字/附件；接收确认及完整矩阵仍待 Windows 实机验收 |
| 原生 UI | SwiftUI 设备会话、附件预览、卡片式设置与提示条 | WinUI 会话、文字/附件、设置与记录、InfoBar |
| 安装包 | 0.3.3 ad-hoc 预览；0.4.0 未打包、未正式公证 | 签名 MSIX 与开发证书侧载验收为历史证据，不等于当前公开发行 |
| 当前验证 | 0.4.0 共 99 项测试通过（应用 54、核心 45，Bonjour 跳过）；更新链路本机回环实测 | 迁入后 Core 449 项在 Mac .NET 11 RC roll-forward 下通过；须在 Windows .NET 10 和双机环境复验 |
| 实体互通 | 完整 Mac↔Windows 验收矩阵待完成 | 完整 Windows↔Windows 与 Windows↔Mac 验收矩阵待完成 |

> 这是当前状态摘要，不是完整验收声明。平台详细状态、历史证据与待办分别见 [Windows](../Windows/README.md) 和 [MacOS](../MacOS/README.md) 文档。

## 2026-10-23 Windows 最新进展复核（旧仓库 windows 2c37f61 / main e7339d0）

本次整理时远端 Windows 已继续完成 DNS-SD、DPAPI 持久化、WinUI 原生收发/设置、系统通知与历史，报告 189 项测试通过；签名 MSIX 的开发证书侧载安装→启动→端口→卸载已验收。main 新提交的 v1 DNS-SD 约定与 .gitignore 均保留。当前差异是 v1→v3 的协议及设备会话交互升级，不能再把 Windows 描述为没有业务界面或发现。v3 文档明确 `v=1/name/fp` → `version=3/name` 和实例名差异，避免只升级握手却互相发现不到。

## 2026-10-23 macOS 0.3.3：设备会话、简化连接、文字/图片及窗口体验同步

- 当前产品要求替换旧 v1 流程：接收方同意连接一次后双方直接互传，移除配对码/SAS 输入和逐文件审批；无产品文件大小或总时长限制，支持空文件。
- v3 线上格式明确更新为 12 字节 nonce、连接决定、持久双向记录流、带 UUID/offset 的 chunk、finish 最终摘要和文字回执；与 v1/v2 不互通，不宣称 Windows 已兼容。
- UI 从工作台改为设备会话：左侧设备与历史，右侧文字气泡、原图缩略图和文件卡片；支持 Enter/Shift+Enter、文件拖入、失败重试、打开原图/文件和访达定位。浅色侧栏改为白色，保留淡蓝选中态。
- 完成设备名称持久化/焦点修复、单主窗口与 Dock 重开、启动钥匙串真实签名预检和运行时身份复用。系统仍需选择“始终允许”；不保存登录密码，也不绕过系统权限。系统签名授权等待与普通网络超时分离。
- Mac 58 项自动测试通过，包含真实本机 TCP、Bonjour、授权准备、消息/附件/记录、旧协议回归与窗口回调；0.3.3 纯样式变更完成发布构建、签名和 zip 校验。用户反馈授权后的连接测试成功，未声明跨平台实体矩阵；不能据此宣布全面互通。
- 新增 [UI 与交互同步规范](UISpecification.md)、[通信协议 v3](ProtocolV3.md)及 [公共向量](TestVectors/v3.json)，旧文档完整保存在[通信协议 v1](ProtocolV1-Historical.md)。新向量由 Node crypto 生成并用 CryptoKit/Security 复算通过；该条记录中的 Windows 独立复算待办属于历史状态；当前实现进展见 Windows 开发日志。
- Windows 同步顺序：先升级已有 v1 核心/身份/发现，再改造现有 WinUI 为设备会话与状态，最后完成真实双向 UI/网络/安装验收。详见 UI 文档的分步清单。
- 未完成：Windows v3 与会话式聊天/图片 UI、实体完整验收、正式公证/签名分发、目录/断点续传、剪贴板图片、群聊、多设备同时会话和离线投递。

## 2026-10-23 Windows：传输状态机与安全落盘落地，修掉一处跨层丢字节隐患

- `Feige.Core` 实现传输层：`TransferManifest`（§4.4 校验）、`TransferSender` / `TransferReceiver`（§5.1 全流程）、`TransferFileSink`（流式写 + 增量 SHA-256 + 校验后原子重命名）、`SafeFileName`（Windows 文件名规则）、`StreamRecordChannel`。
- **§5.3 的 macOS 缺陷在本实现里无法表达**：校验与重命名封在 `CommitAsync` 一次调用内，接收方拿不到最终路径就发不出 receipt，「先 receipt 后重命名」没有对应的 API 组合。
- **完整栈验证**：真实 TCP + 真实握手（RSA-3072 / PSS / X25519）+ 120000 字节 AES-GCM 传输 + 落盘摘要比对，通过。
- **发现一处跨层隐患并修复**：握手最后一条消息与首条记录可能落在同一次 `Read`，原实现会把多读的字节随握手对象丢弃，表现为接收端偶发卡住。新增 `HandshakeFrameReader.TakeRemainder()`，由 `StreamRecordChannel(initialBytes)` 承接，并加回归测试。此项对 macOS 侧同样适用，接入真实网络时需检查相同的边界。
- 负例：接收方拒绝、摘要不符、chunk 超 65408、累计越界、manifest 之前的记录、源文件短于 manifest。
- 测试 111 → 154 项通过。**wire 格式未变**。
- 仍未完成：Windows 尚无 WinUI 界面与设备发现；双机互传未验证。

## 2026-10-23 Windows：签名握手落地，PSS 与 OpenSSL 双向互通

- `Feige.Core` 实现握手层：`HandshakeOffer`（§3.3 全字段校验）、`HandshakeTranscript`（§3.5 编码 + §3.4 交叉校验 + 摘要/SAS）、`DeviceFingerprint`（§3.6 格式与大小写/空格归一化）、`IdentityKey`（RSA-3072、SPKI 导出、RSASSA-PSS SHA-256）、`HandshakeMessages`（§3.3/§3.7 JSON + §3.1 分帧）、`LocalHandshake`（§3.2 四消息时序 + 本地确认 + 密钥派生）。
- 对齐 §8.1 全部向量：两个 offer 的 transcript 编码逐字节相同、摘要 `779855ca…`、SAS `472138`、两个 SPKI 指纹。
- **PSS 双向互验**：OpenSSL 3.5.7 用 `rsa_pss_saltlen:32` 签署的 §8.1 transcript 被 .NET 验通（已固化为单测向量）；反向 .NET 签名被 OpenSSL 判为 `Verified OK`。证实 .NET `RSASignaturePadding.Pss` 的 salt 长度与 macOS 一致。
- 握手时序用**真实 TCP 回环**验证（两个 `LocalHandshake` 各持一端），证明「先写 offer 再读」不会互相阻塞；内存通道另测拒绝配对码不一致、用户拒绝不派生密钥、篡改签名、proof 回显不同 offer、小阶临时公钥。
- 测试 51 → 111 项通过。**wire 格式未变**，macOS 实现无需改动。
- 仍未完成：传输状态机与落盘、manifest 校验、`Feige.App`、发现层、身份持久化。

## 2026-10-23 Windows：AES-GCM 记录层落地，协议补 8.3 记录序列向量

- `Feige.Core` 实现记录层：`RecordCipher`（单方向状态机，AES-256-GCM、16 字节 nonce、13 字节 header 作 AAD、序列号严格递增）与 `AuthenticatedRecordStream`（`u32` 长度前缀、任意 TCP 分片重组、`finish` 后拒收、`endOfStream` 校验）。
- 测试 51 项通过，其中记录层新增 34 项：§8.2/§8.3 向量逐字节比对、重放/跳号/乱序被拒、篡改 type 与序列号（AAD）被拒、篡改密文被拒、`ctLen` 不一致被拒、空载荷仅 `finish` 允许、65536/65537 边界、双向密钥镜像、按 1 字节分片重组。
- [历史 v1 协议](ProtocolV1-Historical.md)新增 §8.3 记录序列向量（seq 0..2，覆盖 `manifest`/`chunk`/空 `finish`）与「按序解码 + 序列号参与 AAD」的负例，8 章复算项数由 15 增至 22；9.5 验收条同步。
- **wire 格式未变**，macOS 实现无需改动。
- 仍未完成：握手帧编解码与 transcript/SAS、RSA-PSS 身份与指纹、传输状态机与落盘、`Feige.App`、发现层。

## 2026-10-23 Windows：X25519 定稿 BouncyCastle，`Feige.Core` 密钥派生落地

- X25519 依赖定稿 `BouncyCastle.Cryptography` 2.7.0（纯托管）：RFC 7748 A.5 / 6.1 向量与「小阶对端必须拒绝」全部通过，`shared` / `i2r` / `r2i` 与不共享代码的 Node 实现逐字节一致。
- 复核记录层时发现[历史 v1 协议](ProtocolV1-Historical.md) §4.1 的 nonce 长度有误：实际是 **16 字节**（`prefix(8) + sequence(8, BE)`），§8.2 向量与 macOS `makeNonce` 都是 16 字节。已更正，并补充「`.NET AesGcm` 只接受 12 字节、Windows 需用 BouncyCastle `GcmBlockCipher`」的结论（已用 §8.2 帧实测解密通过、篡改 header 被拒）。**wire 格式未变**，macOS 实现不受影响。
- 新建 `Feige.Windows.sln` 与 `src/Feige.Core`（`net10.0`，不依赖 Windows API 以便跨机跑测试）：`X25519KeyAgreement`、`SessionKeySchedule`、`SessionKeys`、`HandshakeRole`；`tests/Feige.Core.Tests` 17 项通过。
- 仍未完成：AES-GCM 记录流与序列号状态机、握手帧编解码、RSA-PSS 与指纹、`Feige.App`、发现层。

## 2026-10-23 Windows：开发环境就绪与协议文档复核

- Windows 开发机补齐原生工具链：.NET SDK 10.0.402、Visual Studio Community 2026 18.11（WinUI 应用程序开发工作负载）、Windows App SDK 2.5.1。空白 WinUI 工程 `dotnet build` 0 警告 0 错误，`dotnet run` 以 MSIX 打包身份启动并创建真实窗口。
- 确认**不需要独立 Windows SDK**：C# 的 WinRT 投影来自 `Microsoft.Windows.SDK.NET.Ref`，`makeappx`/`signtool` 来自 NuGet `Microsoft.Windows.SDK.BuildTools`；只有 C++/WinRT 才需要独立 SDK。
- 用不共享两端代码的第三方实现复算[历史 v1 协议](ProtocolV1-Historical.md)第 8 章 15 项测试向量，**全部通过**；逐条比对 `5a315e9` 源码后确认字段级描述准确，并确认 5.3 列出的两处 macOS 差异属实（`finish` 未被消费、receipt 先于原子重命名），Windows 侧不得跟抄。
- 据此修订协议文档：§7 X25519 的 Windows 选型（CNG 不支持 Curve25519、.NET 10 无内置类型、.NET 11 为 STS 不可用）、§3.8 确认令牌绑定描述拆为「规范要求 / macOS 现状」。**wire 格式未变**，macOS 实现无需改动。
- 仍未完成：Windows 无业务源码；X25519 依赖库选型（BouncyCastle / libsodium）待定；协议未冻结。

## 2026-10-23 协议文档化：Win↔Mac 通信协议 v1 草案

- 新增[历史 v1 协议](ProtocolV1-Historical.md)：按 macOS 现有实现记录握手帧、Offer/Proof、transcript 编码、RSA-PSS 签名、X25519/HKDF 密钥派生、SAS、AES-GCM 记录层、单文件传输时序、字段上限与错误处理。
- 给出可复现测试向量：transcript 摘要 `7798…`、SAS `472138`、两端指纹与一帧 AES-GCM 记录；向量已由 macOS 包内测试代码、Node 和 OpenSSL 交叉验证。
- 补充 Windows 实现对照（.NET / CNG / libsodium）与 10 条易错点；《开发总则》的控制消息章节改为与 v1 实现一致。
- 仍未完成：Windows 无实现、Mac 网络适配未接入应用、发现层未实现、`finish` 消费路径待统一。

## 2026-10-23 文档治理：跨平台文档集中到 `main`

- 新建 `main` 分支（自初始提交 `6d6be19` 起），只保存跨平台协议、安全基线、分支说明和更新日志，不含平台源码。
- `macOS` / `windows` 分支移除重复的《开发总则》，平台文档只保留自身内容，并链接回 `main`。
- 首次建立双平台更新日志与能力矩阵；Windows 分支补充开发日志。

## 2026-10-23 macOS：应用层 TransferService 编排与 SwiftUI 确认接线

- 新增 `TransferService.establishSession`：串联签名握手 → 本机用户确认对端指纹/SAS → 派生会话；确认回调在签名校验成功后、派生密钥前等待。
- `TransferModel.connect` 改用 `TransferService`，确认信息通过 continuation 挂起并发布到 SwiftUI 审批 sheet。
- 网络监听、Bonjour、socket 收发仍关闭，真实双机互通未测。
- 验证：`cd macOS && swift test`，21 项全部通过。

## 2026-10-23 macOS：端到端集成测试（握手 → 批准 → 加密传输）

- 新增 `AuthenticatedHandshakeExchange` 四消息签名握手和 `ByteTransport` 抽象；测试用 `InMemoryDuplexTransport` 在无 socket 情况下跑通全流程。
- 两台虚拟设备完成握手、双方 SAS 确认、方向密钥交叉校验，并以 AES-GCM 记录传输 12 万字节文件（manifest → decision → chunk → finish → SHA-256 校验）。
- 新增 `TransferSession`，把记录流接到 `TransferCoordinator` 状态机。
- 验证：18 项测试全部通过；真实网络适配仍未接入。

## 2026-10-23 平台拆分：Windows 工程独立到 `windows` 分支

- `Windows/` 占位工程与 Windows 平台文档移入 `windows` 分支，`macOS` 分支只保留 macOS 实现。（该平台分支做法已废止，现统一在 `main` 主线，Windows 源码为 `Project/Windows/`。）
- Windows 尚无 WinUI 3 可构建源码，仅保留项目目标、开发手册与工程规划。
- 两端协议描述开始出现分叉，为本次公共文档集中治理的直接原因。

## 2026-10-23 macOS：Keychain 设备身份

- 新增 `DeviceIdentity`，用 Keychain 持久化 RSA 3072 私钥，并按标准 SPKI DER 公钥计算 SHA-256 指纹。
- 启动时读取或创建设备身份，设置界面可查看/复制本机指纹。
- XCTest 验证身份跨加载稳定，测试后清理临时 Keychain 项。

## 2026-10-22 项目建立

- 确立 Windows/macOS 双平台原生 UI 方向：macOS 使用 SwiftUI，Windows 使用 WinUI 3，不把 Tauri/WebView 作为最终 UI。
- 建立 Mac SwiftUI 工程、Windows 项目入口和开发文档目录。
- 因身份与传输协议未完成，停用实际网络接收和广播。
