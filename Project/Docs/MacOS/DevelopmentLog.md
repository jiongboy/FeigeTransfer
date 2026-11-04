# macOS 开发日志

> 按时间倒序追加；只记录已实现、已验证与明确未完成项。跨平台摘要见[更新日志](../Public/UpdateLog.md)。

> **路径写法说明（2026-10-27 重命名）**：本日志是历史记录，条目中的路径按当时布局书写。旧 `macOS/` 现为 `Project/MacOS/`，旧 `macOS/dist/` 与 `download/` 现统一为 `Release/MacOS/<版本号>/`，旧 `开发文档/` 现为 `Project/Docs/`（分为 `Public/`、`Windows/`、`MacOS/`）。

## 2026-11-04：0.5.3 build 19 版本号

- `Info.plist` 升为 0.5.3（build 19），包含 `f55ed49`（自动重连失败不弹框）与 `1d5d263`（探测发现、断线重拨、一次性同意连接）。
- 应用内更新来源保持 `192.168.50.229:8080`：`UpdateSource.validate()` 只允许这一个明文 HTTP 局域网地址，下载地址也必须与来源同主机。改为 `10.80.10.188` 需要像 Windows 一样先实现地址改写，留待后续版本。

## 2026-11-04：自动重连失败不再弹框（待下一版）

- **原因**：0.5.2 仍会弹出“局域网传输超时。”。连接尝试失败的 `catch` 对除本机拒绝外的所有失败设置 `errorMessage`，信任设备自动重连（`connectTrustedPeers`）、群组自动连接和对方发起的握手超时都会弹框；发文字因断开失败也会弹框。消息记录中没有超时失败的消息，判断为连接尝试路径。
- **改动**：新增 `userInitiatedConnectionTokens`，`beginConnection(..., userInitiated:)` 只在 `requestConnection` 非自动拨号与 `reconnectGroupMember` 时标记；只有这些尝试失败才弹框（保留“连接排查”），其他失败只把 `connectionState` 设为“连接未建立：…”。`finishConnectionAttempt` 清理标记。发文字失败沿用 `isConnectionLoss` 跳过弹框。代码已提交 `f55ed49`，未包含在已发布的 0.5.2 中。
- **验证**：全量 `swift test` 通过。未验证：实际自动重连超时场景。

## 2026-11-03：0.5.2 build 18 正式发布

- **打包与公证**：从 `4ad7245` 运行 `FEIGE_NOTARY_PROFILE=feige-notary package-app.sh`，Developer ID Application: Song Li (9C8Y647J2R) 签名，公证提交 `d4e0229f-38b0-4bd1-8f8c-e7ed4dd7a39c` 为 Accepted，已 staple；`spctl` 为 Notarized Developer ID，zip 解压后的应用同样通过；`Feige.icns` 与仓库主图标一致。`FeigeMac-0.5.2.zip` 3,444,671 字节，SHA-256 `9b36a608…7daad62a`。
- **更新源**：发布记录 `0.5.2-b18`（`protocolVersion` 6）用 `~/.feige/macos-release.pem` 经 `openssl` 签名，用 `UpdateSource.json` 公钥验签通过（同法复验 0.5.1 清单也通过）。上传到 Mac mini 临时目录 → 哈希核对 → 改名为 `versions/0.5.2-b18` → HTTP 重新下载核对 → 0.5.1 清单备份为 `feige-macos-update.previous.json` 后替换清单，线上清单与本地一致。签名清单与发布记录另存到 `Release/MacOS/0.5.2/`。
- **GitHub**：Release `macos-v0.5.2`（非 Latest，Latest 仍为 `v1.3.1`），重新下载校验通过。
- **未完成**：0.5.1 → 0.5.2 应用内升级、实际断开场景未实测。

## 2026-11-03：0.5.2：对方断开不再弹框

- **原因**：会话读循环结束（对方离线、关闭应用或网络中断）时 `TransferModel` 设置 `errorMessage`，`ContentView` 的 `.alert` 因此弹出模态提示框。
- **改动**：读循环 `catch` 不再设置 `errorMessage`，只调用 `disconnect(peerID:failureReason:preservePendingRequests:)`，会话状态与未完成消息照常更新。发文件失败时新增 `isConnectionLoss(_:)`（`ConnectionError.closed`、`NetworkTransportError`、`AuthenticatedStreamError` 的 `unexpectedEOF`/`timeout`/`cancelled`），属于断开时不弹框，其他错误仍弹出。
- **版本**：`Info.plist` 0.5.2（build 18）。
- **验证**：`swift build` 通过；`swift test --filter 'ModelTransferTests|ChatConnectionTests'` 9 项通过。未验证：实际断开场景的人工确认。

## 2026-11-03：0.5.1 build 17 正式发布

- **打包与公证**：从 `c05a9e4` 运行 `FEIGE_NOTARY_PROFILE=feige-notary package-app.sh`，公证提交 `bd8e0413-45eb-4611-8739-c1ed6fd18036` 为 Accepted，已 staple；`spctl` 为 Notarized Developer ID，zip 解压后的应用同样通过；`Feige.icns` 与仓库主图标一致。`FeigeMac-0.5.1.zip` 3,444,813 字节，SHA-256 `3cdada2c…b767ddc`。
- **更新源**：发布记录 `0.5.1-b17`（`protocolVersion` 6）用 `~/.feige/macos-release.pem` 经 `openssl` 签名，公钥与 `UpdateSource.json` 一致；上传到 Mac mini `versions/0.5.1-b17` 并替换清单。签名清单与发布记录另存到 `Release/MacOS/0.5.1/`。
- **GitHub**：Release `macos-v0.5.1`（非 Latest），重新下载校验通过。
- **未完成**：0.5.0 → 0.5.1 应用内升级未实测。

## 2026-11-03：0.5.1：选择成员弹窗与网段填写

- **选择成员**：新增 `GroupMemberPicker.swift`，创建群聊与群主添加成员共用双栏弹窗；`TransferModel.groupInviteAvailability(for:)` 给出未连接/版本过低原因，`addGroupMembers(_:to:)` 把多台新成员作为同一个名单事务提交（原 `addGroupMember` 改为调用它）。新增 `GroupMemberPickerTests` 与批量添加测试。
- **网段**：`ProbeTargets.normalized` 把三段简写补成 `/24`，`hosts(in:)` 也接受简写；`common` 列出 11 个路由器默认网段，设置页 `Menu("添加常用网段")` 只填入输入框；`company`、`automatic(for:)`、`roundHosts(saved:localAddresses:)` 在本机有 `10.80.x.x` 地址时每轮自动加入公司网段，`localIPv4Addresses()` 读取已启用的非回环 IPv4。`LANService` 的探测循环每轮重算目标。
- **版本**：`Info.plist` 0.5.1（build 17）。
- **验证**：全量 `swift test` 通过（`UnicastProbeTests` 9 项）。`testLoopbackScanFindsResponderAndIgnoresOwnFingerprint` 的随机端口偶尔与其他进程冲突，重跑通过，属已有问题。未验证：10.80.x 网络实测、选择成员弹窗人工点击。

## 2026-11-03：0.5.0 build 16 正式发布

- **打包与公证**：从 master `c6d68c4` 运行 `FEIGE_NOTARY_PROFILE=feige-notary package-app.sh`，Developer ID Application: Song Li (9C8Y647J2R) 签名，公证提交 `0aa22599-829c-483b-8890-8593c12f2c49` 为 Accepted，已 staple。`spctl` 为 accepted / Notarized Developer ID；zip 解压后的应用同样通过 stapler 与 spctl。`Feige.icns` 与仓库主图标一致，10 个头像齐全。`FeigeMac-0.5.0.zip` 3,396,083 字节，SHA-256 `0eb7cb33…bfafe550`。
- **更新源**：发布记录 `0.5.0-b16`（`protocolVersion` 6）用 `~/.feige/macos-release.pem` 签名。本机 Python 没有 `cryptography`，改用 `openssl dgst -sha256 -sign` 生成同样的 P-256 DER 签名；公钥与 `UpdateSource.json` 一致，`openssl` 验签通过。按 Windows 发布脚本的顺序上传到 Mac mini：临时目录 → 哈希核对 → 改名为 `versions/0.5.0-b16` → HTTP 重新下载核对 → 备份为 `feige-macos-update.previous.json` 后替换清单。签名清单与发布记录另存到 `Release/MacOS/0.5.0/`。
- **GitHub**：Release `macos-v0.5.0` 含 zip 与校验文件，设为非 Latest，避免覆盖 Windows 回退源用的 `latest/download`；重新下载校验通过。
- **未完成**：干净机器安装、0.4.x → 0.5.0 应用内升级未实测。macOS 仍没有 GitHub 更新来源。

## 2026-11-03：身份密钥钥匙串副本与 0.5.0 发布准备

- **身份副本**：`EncryptedIdentityStore` 写入 `identity.pkcs1.bin` 时，把同一份 AES-GCM 密文另存为钥匙串通用密码项 `identity-sealed-key`（`AfterFirstUnlockThisDeviceOnly`，与包装密钥同属一个 service）。密钥文件缺失而副本存在时，从副本恢复文件并沿用原指纹；旧安装下次启动时补建副本。`delete()` 同时清除副本。副本没有包装密钥就无法解密，因此不降低现有安全性；钥匙串被清空时仍会生成新身份。
- **版本**：`Info.plist` 升到 0.5.0（build 16）。`package-app.sh` 在 `Release/MacOS/<版本>/` 已有 `.app` 或 zip 时报错退出，不再删除重建，避免覆盖已公证的 0.4.1（build 12）。
- **以文件为准**：密钥文件存在时以文件为准，副本缺失或内容不一致都会用文件内容重写。
- **测试隔离**：`OutboxTests`、`TrustedPeerTests` 传入的 `identityStore:` 参数被 `TransferModel` 忽略，实际用的是正式的钥匙串 service。新增副本后，这两项测试把测试密钥写成了本机正式副本，第二个测试端从中恢复出同一指纹，导致连接失败。已改为各自传入唯一的 `identityService:`，并删除测试写入的正式副本项（已安装的旧版应用从未写过该项）。
- **验证**：`swift test` 全量通过：应用 96 项（跳过 5 项）、核心 75 项。新增 `testDeletedDataFolderRestoresSameIdentityFromKeychainBackup`，覆盖恢复、补建和替换过期副本。测试后确认正式 service 下没有副本项。
- **未完成**：未生成 0.5.0 正式包；公证需要 `FEIGE_NOTARY_PROFILE`。合并界面、身份副本的安装后实测和与 Windows 互通尚未验收。

## 2026-11-03：会话手动合并与附近设备去重

- **合并**：`TransferModel.mergeConversation(_:into:)` 把来源会话的 `peerID` 改为目标指纹，按时间重新排序，清除两者的隐藏标记，并迁移草稿、取消来源的常驻联系人、清除来源未读。来源会话处于连接中或有待发、正在收发的消息时拒绝合并。会话右键菜单“合并到其他会话…”弹出确认框，每个目标对应一个“并入“名称””按钮。
- **附近设备去重**：新增 `nearbyDevice(forPeer:)`，优先按指纹提示匹配，再按旧服务名匹配。`undisplayedDevices` 排除已有会话的设备，会话标题栏的“请求连接”也改用这个匹配。
- **验证**：`swift test` 通过，应用 96 项（跳过 5 项），核心 74 项，新增 `testMergeMovesOldConversationIntoTargetAndPersists`。临时签名包已重装到 `/Applications`，`codesign --verify --deep --strict` 通过，已启动。
- **未完成**：未点击验证合并界面。

## 2026-11-03：协作增强五项（跨网段、未读、常驻联系人、v6 文件夹与续传、离线待发）

- **跨网段**：`Identity/Sources/UnicastProbe.swift` 实现单播探测报文与私网来源过滤；设置中维护探测目标。
- **未读与通知**：`TransferModel+Unread.swift` 本机计数，当前激活会话不计入；Dock 角标与系统通知。
- **常驻联系人**：`DeviceSettings.trustedPeers` 保存完整指纹，自动同意连接（不跳过文件确认）；`connectTrustedPeers` 退避 30 秒/1/5/30 分钟，指纹较大的一方发现后多等 10 秒；拒绝或手动断开后暂停，手动连接恢复。
- **v6**：版本门限放宽到 6，发现 `caps=3,4,5,6`；`ResumableTransfer.swift` 提供续传暂存（`.feige-resume-<key>.part`）、文件夹状态、枚举（跳过符号链接与 `.DS_Store`）和 7 天清理；`FileConnection.sendFolder` 整个文件夹只确认一次、不合并、不覆盖。修复枚举遇到符号链接时调用 `skipDescendants()` 漏掉下一个目录的问题。
- **离线待发**：`ChatMessage.outboxSince` 标记待发；未连接或已有待发时 `sendText`/`startSending` 入队，连接安装及每条投递结束后 `flushOutbox` 按时间逐条发送，文件发送前检查源文件，对方拒绝记为“已拒绝接收”不再重试。中断、断线、重启回到待发。每会话 200 条上限，可取消，超过 7 天转失败（启动、发现事件和每小时检查）。只为常驻联系人自动拨号，其他设备需任一方手动连接后投递。
- **验证**：`swift test` 全量通过：应用 95 项（5 项依赖局域网或 Windows 环境跳过）、核心 74 项；新增 `ResumableTransferTests` 8 项、`OutboxTests` 2 项（含回环连接下的顺序投递、原 ID 去重、源文件缺失、拒绝不重试）、v6 共享握手向量。最后一轮调整后重跑 `OutboxTests`、`TrustedPeerTests` 通过。
- **未完成**：未做界面实测和与 Windows 的真实局域网互通（跨网段、续传、文件夹、离线投递）；未打包。

## 2026-11-02：新手引导、群成员面板与预置头像

- **头像**：`AvatarCatalog.swift` 读取 10 个内置头像（打包时从 `Sources/Resources/Avatars` 复制到 `Contents/Resources/Avatars`，与 `Project/Ui/Avatars` 内容一致），首次引导和设置可选择或跳过；`LANService` 在 TXT 中发布可选 `avatar=`，对端头像仅在完整指纹与已认证连接匹配后保存。
- **群成员面板**：`GroupMembersInspector.swift` 在当前群聊内显示成员数、头像、角色和在线状态，可搜索；移除确认写明目标成员名。
- **空状态**：无设备时显示“尚未找到设备”，提供“重新搜索”和“连接排查”；身份未就绪时只显示状态。
- **验证**：`swift test` 148 项通过（5 项跳过）。界面未人工验收。

## 2026-11-02：网络切换后清理失效会话通道

- **行为**：`NWPathMonitor` 跟踪网络可用状态和路径更新。网络中断或在线路径更新时关闭现有聊天通道（即使接口名称不变），将未完成消息标记为失败；会话历史和草稿继续保留。网络恢复或路径更新后等待 750 ms 再刷新设备发现。
- **重新连接**：恢复连接需要重新取得对方同意；可重试的发送项在新连接获准后重发。监听服务和身份不因网络切换重建。
- **群请求**：网络断开或传输异常时保留邀请与成员退出申请，未连接时不消费请求。双方待处理的退出请求和原始 eventID 均写入会话归档；发送结果不确定时成员重连后重发同一事件，群主按 eventID 幂等处理并持久化最终回执。接受或拒绝后的重复申请均收到同一结果，不会重新弹出已处理申请；用户再次点击“申请退出群聊”可重发待处理事件。回复发送失败时群主保留申请并恢复临时群连接；已保存的邀请接受决定会在邀请方重发时自动回执。退出申请变更尚在提交时保留申请；申请人收到最终回执前维持群通道。重发仅通过 V4+ 连接执行，V3 连接不会因不支持群控制而被断开。
- **验证与未完成**：Swift Package Release 构建通过；本轮未运行自动化测试，也未完成真实网络切换验收，相关验收项仍待执行。

## 2026-11-01：macOS 0.4.1 build 12 Developer ID 公证包

- **发行范围**：Apple Silicon arm64；最低系统版本 macOS 13。打包脚本 Release 构建通过，产物位于 `Release/MacOS/0.4.1/`：`飞鸽快传.app` 与 `FeigeMac-0.4.1.zip`。
- **签名**：使用 `Developer ID Application: Song Li (9C8Y647J2R)`；`codesign --verify --deep --strict` 通过，签名满足指定要求。
- **公证**：Apple notarytool submission `452e5a38-9850-4cba-a1ba-e6cc8e5c6a7e` 状态 `Accepted`；`xcrun stapler validate` 成功。
- **系统与归档验证**：`spctl --assess --type execute --verbose=4` 返回 `accepted` / `source=Notarized Developer ID`；二进制为 arm64；`unzip -t` 无错误。zip SHA-256：`c0f1df0344f1f9d31afe62056bdb9b2441033302bdf854b0b8bc7ce1155ad299`。
- **发布边界**：未上传飞鸽更新源，未提交 Git；`Release/MacOS/0.4.0/` 保持不变。源码自动化测试此前通过，打包脚本本轮重新完成 Release 编译。

## 2026-11-01：macOS 0.4.1 build 12 正式包预检

- **候选范围**：用户确认使用当前已通过 Release 源码构建/测试的工作树，候选版本为 0.4.1 build 12；`Resources/Info.plist` 已切换到该版本。已发布的 0.4.0 build 11 和其更新资产保留不动。
- **签名与公证**：本机 `security find-identity` 结果为 Developer ID Application **0 个**、Apple Distribution **1 个**；本次环境中的 `FEIGE_SIGN_IDENTITY` 与 `FEIGE_NOTARY_PROFILE` 均未设置。打包脚本会回退选 Apple Distribution 并跳过公证，不能作为正式公网发行包。
- **输出保护**：候选版本路径应为 `Release/MacOS/0.4.1/`。当前脚本若仍使用 0.4.0，会删除该目录中的旧 `.app` 并覆盖 zip；本轮未运行打包脚本，也未发布到更新源。
- **架构范围**：用户已确认本候选仅支持 Apple Silicon arm64；与现有 arm64 更新通道一致。Intel/x86_64 不包含在本候选中，也不宣称已完成 Intel 运行时验收。
- **下一步**：在本机安全配置 Developer ID Application 身份和 notarytool profile 后，生成并验证 arm64 候选 `.app`/zip。不得把私钥、密码或 profile 凭据放入仓库或对话。

## 2026-11-01：macOS Release 源码构建与回归

- **工具链**：本机 macOS 27 ARM64，Apple Swift 6.4.2，Xcode 27.2；目标最低版本 macOS 13。
- **源码构建**：`swift build --package-path Project/MacOS --configuration release` 通过（Build complete，18.69 秒）。
- **自动化回归**：`swift test --package-path Project/MacOS --configuration release` 退出码 0；输出确认 `FeigeIdentityTests` 59/59、0 失败，所有测试 runner 均通过。需显式 opt-in 的 mDNS 与 Windows LAN 验收未在本地全量回归中启用。
- **发布边界**：本轮只构建源码并运行测试，未调用 app 打包、签名、公证或发布脚本。

## 2026-11-01：Windows 群组 UI 与混合 LAN20 验收

- **Windows 当前源码回归**：Parallels Windows 11 ARM64 / .NET 10.0.402 上 `Feige.Core.Tests` 578/578、`Feige.App.Tests` 8/8 通过。
- **Mac 群主邀请 Windows**：`testMacOwnerInvitesWindowsAndWindowsAcceptsFromItsGroupsPage` 1/1 通过；Windows UI 接受邀请，成员和群文字持久化，Mac 收到回执。
- **Windows 群组管理 UI**：`testWindowsUiGroupCreateAddAndRemoveOverLANWhenConfigured` 1/1 通过，完成建群、添加与移除成员。
- **LAN20 群组与广播**：`testWindowsUiCreatesTwentyMemberGroupAndBroadcastsOverLANWhenConfigured` 1/1 通过，19/19 runtime 收到广播；拓扑为 1 个 Parallels Windows owner 加同一 Mac 宿主上的 19 个隔离 macOS `TransferModel` runtime，不是 20 台实体设备。
- **容量**：39 条认证 loopback TCP 连接回归通过；持续负载资源采样仍待完成，可用隔离 runtime 验证。
- **仍待完成**：Mac↔Mac、Windows↔Windows 实体双端矩阵、完整跨平台文件与异常路径、Windows 签名包和更新端到端验收，以及 macOS Developer ID 公证/Gatekeeper 验收。当前源码 Windows App x64 Release 非打包编译已通过（0 警告、0 错误）；Core 578/578、App 8/8 在隔离 ARM64 .NET host 上通过。当前 Windows VM 无 x64 .NET test host，未做 x64 运行时回归。20 台实体设备不是发布门槛。

## 2026-10-31：Mac 群主邀请 Windows 的真实跨端闭环通过

- **定位根因**：最新 Parallels Windows 实例的设备身份已变更，验收环境变量仍使用旧指纹 `0102…`。V5 握手和 Windows 接受连接都成功，但测试持续查询错误的 peer ID，造成假超时。通过 Windows 当前 DPAPI 身份仅导出公钥指纹，更新验收配置为当前身份 `3053…D6E3` 后完成联调。
- **网络复测**：Network.framework 直连 `10.211.55.4:59403` 成功，实际路径为 `bridge100`；旧 `ENETDOWN` 未复现，不需要切换 Surge 或更改系统网络设置。
- **流程结果**：Windows 群组页接受 Mac 群主邀请，Mac 持久化成员事务完成；Mac 发出的群文字在 Windows 群组存储中落盘并收到正向回执。Windows 存档确认两名成员、revision 0。
- **验收测试修正**：测试比较指纹时先统一为不带空白的大写十六进制，解决紧凑与四位分组格式差异；超时 helper 改为抛出失败，避免失败后继续执行并输出误导性完成标记。加入错误指纹快速诊断。
- **验证**：`WindowsGroupManagementAcceptanceTests/testMacOwnerInvitesWindowsAndWindowsAcceptsFromItsGroupsPage` **1/1 通过**（43.958 秒）。
- **截至 2026-10-31 当时仍未完成**：Mac↔Mac、Windows↔Windows 实机矩阵、真实 20 人 LAN 生命周期、持续负载采样；Windows 群组页创建/成员增删的完整实际点击流程。

## 2026-10-30：跨端群主邀请验收的定位记录

- **场景**：在 Windows 虚拟机可交互、用户已手动点过“同意连接”的条件下，实际运行 `testMacOwnerInvitesWindowsAndWindowsAcceptsFromItsGroupsPage`（Mac 作群主，v5 连接 Windows 真实实例）。
- **进展**：Windows 侧确实收到了 TCP 连接（日志出现 `v4 accept: EndOfStreamException` 与 `accept: IOException`），说明地址与端口正确、地址策略放行；`connectionReadiness` 返回 `ok`。
- **定位到的问题**：Mac 侧拨号发出后立即被模型自身的 `cancelConnectionAttempts()` 取消（诊断记录显示 `1 pending → 0 pending`，随即 `CancellationError()`），因此 `channelsByPeer` 始终为空、`createGroup` 前置校验失败。该取消发生在 `TransferModel` 内部，不是网络错误，需要单独排查是谁在连接建立期间触发了清理。
- **现状**：为定位临时加入的诊断输出与临时测试已全部移除，`TransferModel` 回到本轮前状态；应用全量 **82 项、5 项跳过、0 失败**确认未受影响。
- **未完成**：该取消根因未修复，因此“Mac 作群主邀请 Windows → Windows 接受”的真实界面闭环仍未跑通。

## 2026-10-30：群组建群、添加成员、移除成员端到端回归

- **新增用例** `testGroupCreateAddMemberThenRemoveMemberKeepsRosterOrderedOnEverySide`：按 Windows 群组页的操作顺序走一遍公开模型接口——与成员一建群、通过 `addGroupMember` 加入成员二、再 `removeGroupMember` 移除成员二；每一步都断言三端成员表一致、群主身份不变，且两次成员变更各推进一次版本号（最终 revision 2）。
- **覆盖价值**：此前的群组用例覆盖群文字、文件扇出、退出和群主移交，但缺少“添加后再移除”这一最常用的成员管理序列；该用例补上了成员增删的完整闭环。
- **验证**：应用全量 **82 项、5 项按条件跳过、0 失败**；`GroupConversationTransferTests` 6/6、新增用例 7.5 秒通过。

## 2026-10-30：发现矩阵补充验证

- **Bonjour 双实例发现**：以 `FEIGE_TEST_BONJOUR=1` 运行 `testBonjourDiscoversOtherInstanceAndFiltersSelf` 通过（约 1.0 秒），确认同机双实例可互相发现且过滤自身。
- **跨端规则一致性**：Windows 侧新增 `Discovery_AdvertisedTxtRoundTripsThroughTheSameRulesMacUses`，锁定 Windows 能解析 macOS 的 `v`/`caps` 广播；同时记录一处不对称：**Windows 目前不读取 macOS 广播中的 `fp` 键**，其对端指纹来自 `Feige-<20 位>` 实例名，本轮未修正产品行为。
- **容量**：Windows `V4PeerTransportTests` 39 条已认证连接复测通过（56 ms）；两端上限常量一致（20 直连 + 19 群组专用 = 39）。
- **未完成**：Mac↔Mac、Windows↔Windows 等组合的真实双机自动发现/连接矩阵，以及 Windows 界面点击验收，均受当前会话环境限制未执行。

## 2026-10-30：V5 取消重启恢复回归与 Windows 群组管理验收测试

- **新增 V5 取消重启恢复用例**：原 `testV5CancellationPersistsIntentAndRecipientTombstone` 只覆盖断线后重连，没有真正销毁并重建模型。新用例 `testV5CancellationResumesAfterOwnerRestartAndRecipientTombstoneSurvives` 在成员离线时发起取消，确认取消未完成且意图已落盘，然后 `shutdown()` 群主并重新构造 `TransferModel`：重启后读取持久 abort，成员重连时补发并按确认清除，双方都不提交被取消的成员表；再重启接收方，确认墓碑仍在且不出现该群组。
- **新增 Windows 群组管理跨平台验收用例**（`WindowsGroupManagementAcceptanceTests`）：两台隔离 Mac 实例以 v5 主动连接 Windows 运行实例，自动批准连接与邀请，等待 Windows 界面创建群组、加入第二成员、随后移除第二成员，并断言三方成员表一致；未配置环境变量时跳过。配套 `WindowsGroupAcceptanceProbeTests` 用于在验收中输出实例指纹与端口并保持运行。
- **验证**：`GroupConversationTransferTests` 5/5 通过（新增用例 2.2–2.9 秒）；应用全量 80 项、4 项按条件跳过、0 失败。
- **未完成**：Windows 界面上的实际点击尚未执行——验收时 Parallels 虚拟机登录会话失效，计划任务无法再在会话 1 启动图形进程，`prlctl` 又只能进入 SYSTEM 的会话 0。需在虚拟机中重新登录后继续；Windows 群组页尚无可靠的 UIA 标识，界面点击需要按窗口坐标操作。

## 2026-10-29：修复更新完成提示条关闭按钮

- **现象与原因**：用户反馈更新成功提示右上角关闭按钮无效。`GuidancePanel` 只通过 `TransferModel` 读取嵌套的 `UpdateController`，没有订阅其变化；按钮确实清空了 `installNotice`，但提示视图未重绘。
- **修复与发布**：让 `GuidancePanel` 订阅 `UpdateController.objectWillChange` 并刷新更新提示子视图；build 11 已签名发布到 Mac mini 的 macOS stable 更新源。
- **验证**：本机客户端从 build 10 检查、下载并校验 build 11，应用替换后自动重启到 build 11；在主窗口点击“关闭提示”，成功提示条立即消失。
- **限制**：使用 Apple Distribution 签名，未公证；正式公证、跳过/间隔等待/繁忙传输等更新场景仍待验收。

## 2026-10-29：Mac mini 局域网更新发布与真实升级验收

- **更新信任根与来源**：为 macOS 独立生成 P-256 发布密钥，私钥仅保存在本机用户目录的权限受限文件中；公钥与 `feige-macos-release-2026` key ID 写入 `Project/MacOS/Updates/UpdateSource.json`。更新源固定为 `http://192.168.50.229:8080/updates`，仅允许配置指定的 LAN 主机、端口及目录。
- **发布**：签名清单与 `0.4.0` build 10 包已推送至 Mac mini `/usr/local/var/www/feige-updates/macos/stable/`。清单、说明与 zip 均经 HTTP 200 检查；服务端 zip 大小与 SHA-256 和清单一致。macOS P-256 清单签名及应用 Apple Distribution 代码签名均已验证。
- **升级验收**：在本机 macOS 27 arm64 客户端从 build 9 发起检查，显示 build 10，下载后验证大小与 SHA-256，签名校验和原子替换成功；更新器自动启动新进程，设置页显示“更新完成，当前运行版本已确认”，bundle build 为 10。
- **验收中修复**：更新卡片直接响应 `UpdateController` 状态变化；安装结果使用清单中的真实 build 并从 `Updates/install-result.json` 读取；安装后由新进程成功启动再退出旧进程。
- **限制**：包使用 Apple Distribution 签名但未公证，仅作为受控 LAN 验收发布；Mac mini 是 macOS 12.7.6 Intel，低于客户端要求的 macOS 13.0，因此仅托管更新文件，真实客户端测试在本机完成。GitHub 回退、正式公证、跳过版本/间隔等待/繁忙传输/失败场景仍待验收。

## 2026-10-28：逐个文件接收确认与源码预览着色

- **逐个文件接收确认**：接收方在文件元数据到达后先看到“等待你确认接收”，可选择“接收”或“拒绝”，与 Windows 行为对齐。确认在创建暂存文件**之前**完成，因此拒绝不会写盘，也不会留下部分文件。拒绝时发送 `decision(accepted=false)` 并关闭连接；接收方的消息记录标记为“已拒绝接收”，不会出现伪成功。
- **协议未变**：复用既有 `decision` 记录（type 2），`FileConnection.run` 新增可选 `fileApproval` 回调，默认仍为自动接受，因此旧调用方与既有 v3 向量不受影响。协议文档已更新为“接收方可以询问用户”，并写明拒绝语义与不写盘要求。
- **超时修正**：发送方等待 decision 的超时原为 30 秒，而 Windows 为 2 分 10 秒；用户犹豫超过 30 秒就会被误断线。已对齐为 130 秒，接收方确认窗口为 2 分钟，超时按拒收处理并记录原因。
- **断线安全**：连接断开时会释放所有待确认请求并按“连接已断开，未开始接收”标记，读取循环不会挂起。
- **源码预览着色**：新增 `CodeHighlighter`，与 Windows 使用完全相同的正则与四类 token（注释/字符串/数字/关键字），保留原有行号；超过 20000 字符转为纯文本。已补“拼接无损”测试保证高亮不会改变显示文本。
- **验证**：Mac 共 **114 项测试通过、0 失败**（应用 62、核心 52；Bonjour 1 项按条件跳过）。新增 8 项：拒收不写盘且标记 declined、接收后落盘且标记 received、待确认状态与重复点击安全、状态编解码兼容、高亮分词与无损拼接。原有 `ModelTransferTests` 按新行为更新为同时应答连接同意与两侧文件确认。
- **Windows 端同步验证**：`Feige.Core` 466 项测试通过（本机 .NET 11 SDK 以 roll-forward 运行 net10 目标）。剪贴板图片粘贴代码用复刻真实 WinRT 签名的桩类型做了类型检查，据此发现并修复 `using var` 误用于 `BitmapDecoder`/`BitmapEncoder` 的编译错误；WinUI 工程需在 Windows 开发机编译。
- **未完成**：未做实体双机互验（重点是拒收时对方收到“已拒绝”而非超时）；WinUI 实机粘贴验证待 Windows 环境。

## 2026-10-28：消除钥匙串密码弹窗，修复签名回退

- **现象**：每次安装/更新后启动都要输入 Mac 登录密码授权钥匙串。已测量确认：首次访问密钥在 ad-hoc 签名下需 8–65 秒，同一代码哈希缓存后仅 0.03 秒；系统日志出现 `code requirement check failed (-67050), client is not Apple-signed`。
- **根因一（签名）**：`package-app.sh` 只在设置了 `FEIGE_SIGN_IDENTITY` 时才用证书签名，否则静默回退 `codesign --sign -`（ad-hoc）。ad-hoc 签名没有证书与 Team ID，系统按代码哈希识别应用，每次更新都成为“陌生程序”。该机器已有 `Apple Distribution: Song Li (9C8Y647J2R)`，但脚本从未使用。
- **根因二（存储）**：设备身份以 `kSecClassKey` 存入系统钥匙串，这类项必然绑定 ACL 授权；不具备稳定 Apple 签名身份时就会弹出“想使用储存在钥匙串中的机密信息”并要求输入密码。只改签名只能减少弹窗，无法彻底消除。
- **修复（签名）**：`package-app.sh` 改为自动发现证书（优先 `Developer ID Application`，其次 `Apple Distribution`，仍可用 `FEIGE_SIGN_IDENTITY` 覆盖）；仅在没有证书时回退 ad-hoc，并输出明确警告说明“每次更新都会重新要求授权”。打包后打印实际 `Authority` 与 `TeamIdentifier`。
- **修复（存储）**：新增 `EncryptedIdentityStore`。私钥以 PKCS#1 DER 经 AES-256-GCM 加密存放于 `Identity/identity.pkcs1.bin`（权限 600，临时文件加原子重命名）；32 字节包装密钥存为钥匙串**通用密码项**，不经过 ACL 授权路径。不再创建 `kSecClassKey` 项，从源头消除弹窗。
- **升级迁移**：首次启动调用 `migrateLegacyKeyIfNeeded()`，读取旧钥匙串私钥、加密写入新位置后删除旧项，**设备指纹保持不变**。实测迁移前后设置页指纹均为 `A449 3CE9 … 6451 DE21`；迁移失败时保留两份副本以便重试，不静默更换身份。`DeviceIdentity` 类型与握手签名接口未变，v3 协议与线格式零改动。
- **安全取舍（按用户确认执行）**：私钥不再受不可导出的钥匙串保护，同登录用户的进程理论可读取。设备身份仅用于局域网内认证本机设备，不签发软件、不保护静态数据；软件与更新签名使用独立证书。已在 README 与开发手册中明示。
- **验证**：`swift build` 无警告；`swift test` 共 **106 项通过、0 失败**（应用 54、核心 52；Bonjour 1 项按条件跳过），新增 7 项覆盖加密存储、600 权限、不创建 key 类项、损坏文件不静默轮换、删除后重建、包装密钥往返、旧身份迁移保指纹。真实数据实测：迁移后指纹不变、旧钥匙串项已移除；重启与**替换二进制重新签名**后均在 4–5 秒内就绪、无 `SecurityAgent` 弹窗进程；作为对照，ad-hoc 签名版本卡在“正在准备设备身份，请完成系统钥匙串授权…”。
- **待办**：尚未公证（当前证书为 `Apple Distribution`，分发下载后首次启动仍需 Gatekeeper 放行，需改用 `Developer ID Application` 并配置 `FEIGE_NOTARY_PROFILE`）；未在实体双机复验。

## 2026-10-28：同步 Windows 功能（0.4.0，构建 7，未打包）

本次按 Windows 1.2.x 源码逐项对齐，未改动 v3 线协议、传输或握手行为。

- **设置窗口**：改为卡片式滚动布局，与 Windows 设置页对应：设备名称、接收文件夹、本机连接信息（就绪状态、局域网地址与端口）、版本与更新、通知（可选）、本机记录（数量、清空传输记录、打开数据目录）、隐藏的会话、设备身份与连接安全、应用运行（退出应用）和版权。窗口由 560×620 调整为 620×760。
- **提示条**：新增 `GuidancePanel`，提供授权准备、原资料已保留、首次连接、首次收发、通知邀请、关闭窗口后仍后台运行、更新状态/结果与错误的可关闭提示；原主窗口内联的授权提示并入其中。关闭主窗口由 `CloseObserver` 转发 `windowWillClose` 记录，只有在应用外可见地关闭时才提示一次，更新替换和 ⌘Q 退出不触发。
- **通知**：新增 `TransferNotifier`（UserNotifications）。首次收发成功后邀请启用，接受或跳过后不再重复；设置页可启用、关闭、发送测试通知、打开系统通知设置。系统拒绝时状态文本说明原因，收发不受影响。没有应用标识的运行环境（XCTest、命令行）直接降级，不调用通知 API——此前未加保护会让测试进程以 ObjC 异常（signal 6）中止。
- **草稿**：按会话指纹持久化到 `drafts.json`，重启与切换设备后保留，发送成功清除；空白选择不落盘。超过 8 KB 的正文在发送时给出明确提示并保留草稿。
- **表情**：输入区新增“表情”按钮与内置面板，不依赖系统 ⌃⌘Space 是否开启。
- **网络变化**：新增 `NWPathMonitor`，网络可用后 750 ms 防抖再重建发现并刷新本机地址；已建立连接、监听端口与连接同意保持不变，稍后模式与退出不会因此重新启动服务。
- **手动连接**：要求字面量 IPv4/IPv6 局域网地址与 1–65535 端口，拒绝域名、公网地址、越界端口与前导方括号；界面改为分字段表单。
- **帮助**：侧栏新增“使用帮助”，与“连接排查”共用视图并按模式显示；包含 macOS 防火墙位置与“打开防火墙设置”入口，文案与 Windows 对齐但使用 macOS 系统名称。
- **应用更新**（`UpdateController` 等 6 个文件）：签名清单 `format/keyId/payload/signature`，签名原文 `Feige.Update.v1\n<keyId>\n` + payload；macOS 侧用 CryptoKit 校验 P-256（支持 DER 与 x9.63 公钥）或 Ed25519。检查间隔 24 小时、同构建每日最多提示一次、跳过构建不再自动提示、状态持久化到 `Updates/update-state.json`。下载流式写入更新专用目录并校验大小与 SHA-256，只有校验通过才能安装。安装前用 Security 框架验证当前应用与新包的签名，同目录重命名替换，失败回滚；等待收发结束可取消；安装后写 `install-result.json`，下次启动显示“更新完成”或“更新未确认”。
- **更新信任根与来源**：`Resources/Updates/UpdateSource.json` 随签名后的应用分发，当前仓库内为未配置占位（空公钥），客户端因此不联网并在设置页显示“更新配置无效：更新签名公钥尚未配置”；`swift run` 读取 `Project/MacOS/Updates/UpdateSource.json`。生产来源强制 HTTPS，仅 `127.0.0.1`/`localhost` 允许明文 HTTP 用于本机发布演练。
- **签名工具**：新增 `scripts/sign-macos-manifest.py`（keygen / public-key / sign，P-256，需要 `cryptography`），私钥不得进入仓库或 `Release/`。`package-app.sh` 同步把更新来源写入应用包。
- **修复两个真实缺陷**：
  1. `DeviceSettings` 新增字段后，Swift 合成的解码不会回退成员默认值，任何旧版 `settings.json` 都会整体解码失败并回退到默认设置。改为显式 `init(from:)`，缺失键取默认值，并补回归测试。
  2. `UpdateController` 的 `if let source` 绑定到了可选初始化参数而不是 `self.source`，导致已配置的更新源被静默当作“未配置”。改为读取 `self.source` 并在代码中注明原因。
- **兼容与版本**：`Info.plist` 版本 0.3.3→0.4.0、构建 6→7；旧设置文件新增五个偏好字段，均带默认值。
- **验证**：
  - `swift build` 无错误无警告；`swift test` 共 **99 项通过、0 失败**（Mac 应用 54 项、核心 45 项；Bonjour 1 项按既有条件跳过），较 0.3.3 的 73 项增加 26 项（设置兼容、草稿、偏好、手动连接、记录清空、更新验签/来源/状态/摘要）。
  - 更新链路本机回环实测：用 `sign-macos-manifest.py` 生成签名清单，真实应用内检查到新版本；独立驱动验证检查 → 下载 → 大小与 SHA-256 一致 → 可安装，错误摘要与异源链接被拒；非 `.app` 构建调用安装时被安全拒绝。
  - 应用包在真实用户数据（只读观察）与隔离副本下均能启动；偏好设置窗口逐卡片枚举确认，本机连接信息显示真实地址与端口，身份指纹与记录数量正确。未修改用户现有设置、会话或文件。
- **未完成**：未配置正式更新源与签名密钥，未做真实“旧版 → 新版”升级与公证；未在实体双机验证通知弹窗、网络切换、表情面板与拖放矩阵；未打包。

## 2026-10-26：加固 DOCX 解压限制与 ZIP 边界检查

- 将正文 DEFLATE 解压改为 64 KiB 分块输出，并在追加前限制实际展开内容不超过 2 MiB；不再按不可信的 ZIP 声明长度一次性分配整个输出区。
- 加固中央目录与本地头偏移/长度验证，拒绝超边界读取；对声明展开长度不足但实际数据超限的压缩流增加回归用例。
- 验证：DOCX 定向测试 4 项通过，含压缩率/声明长度不一致用例；全量 `FEIGE_TEST_BONJOUR=1 swift test` 共 73 项全部通过（Mac 应用 28 项、核心 45 项）。未打包。

## 2026-10-26：增加 DOCX 正文只读预览

- 附件预览支持 DOCX 文档正文提取，保留段落、换行和制表符；复杂样式、图片、页码、代码着色仍不处理，也不执行字段或外部关系内容。
- 新增受限 ZIP 中央目录读取、仅支持 stored/deflate 的正文读取、CRC 校验和 XML SAX 解析；不加载外部资源。压缩包上限 50 MiB，正文 XML 与显示文本分别限制 2 MiB，段落最多 5000 个。
- 验证：包含 deflate 正文/中英文段落的提取测试、坏 ZIP 拒绝和超限拒绝共 3 项通过；全量 `FEIGE_TEST_BONJOUR=1 swift test` 共 72 项全部通过（Mac 应用 27 项、核心 45 项）。未打包；真实窗口排版仍待人工验收。

## 2026-10-26：清理崩溃遗留的未完成接收文件

- 新增接收目录扫描，仅识别 `.feige-<UUID>.part` 规则的普通文件；通过目录描述符、`O_NOFOLLOW`、`fstatat(AT_SYMLINK_NOFOLLOW)` 与 `unlinkat` 避免跟随符号链接或路径替换。
- 用户确认保存/更改接收目录时执行清理；普通 `.part` 文件和符号链接保留。异常目录或清理失败会阻止设置保存，避免误报已完成清理。
- 验证：恢复单测、符号链接目录拒绝、文件名过滤及设置接线测试通过；完整 `FEIGE_TEST_BONJOUR=1 swift test` 共 69 项通过（Mac 应用 24 项、核心 45 项）。未打包。

## 2026-10-26：补齐会话管理与附件预览首轮

- 会话行新增右键打开、隐藏、删除和活动连接断开操作；设置窗口可逐项或全部恢复隐藏会话。删除需要确认，只移除对应设备指纹的会话消息与隐藏状态，不删磁盘文件和独立传输记录；活动连接、连接中或未结束传输禁止删除。
- `messages.json` 从旧版消息数组兼容迁移到 version 1 会话封装，隐藏指纹与消息原子写入。无法解码或未知版本时保留原文件，停止覆盖；新消息/附件完成和重新连接会恢复隐藏会话。
- 已完成附件提供图片缩放、PDF 原生预览及常见文本/源码只读预览；文本读取限制 2 MiB / 5000 行，PDF 限制 50 MiB。DOCX 正文提取、代码着色、页码工具栏仍未完成，不支持的格式仍可由系统打开。
- 刷新附近设备增加进度、重复点击保护和最近刷新时间；已存在会话显示当前发现到的设备名称。连接失败仅在 TCP 失败或传输超时时提供连接排查入口，帮助说明不自动修改系统网络设置。
- 首次设置改为两步流程，支持返回、稍后和持久化恢复当前步骤；身份授权延后到用户继续准备连接后，接收目录写入能力会先探测。
- 验证：会话管理 6 项、设置 4 项、身份准备 4 项定向测试通过；全量 `swift test` 65 项通过，1 项需本机 Bonjour 权限的测试按默认设置跳过。构建不生成安装包。原生右键菜单与预览窗口仍待人工操作检查。

## 2026-10-25：聊天输入框支持粘贴图片发送

- 原生聊天输入框检测剪贴板中的图片并转换为 PNG 附件，复用现有 v3 文件发送队列、进度、消息持久化和失败重试；不覆盖草稿文本。
- 剪贴板无图片时仍走 NSTextView 原生粘贴行为，普通文字粘贴回归通过。粘贴图片保存在应用支持目录的 `PastedImages/`，供消息历史与失败重试继续引用。
- 验证：`FEIGE_TEST_BONJOUR=1 swift test` 共 60 项全部通过（17 项应用测试、43 项核心测试），包含图片粘贴路由、文本粘贴回退及 Bonjour 发现。
- ⌘V 从真实截图/图片应用粘贴、对端原图完整性及打包后行为仍待人工验收；本次未打包。Windows 当前工作树缺少可核对的应用源码，未声称该能力与 Windows 已对齐。

## 2026-10-23：整理平台提交与 Windows 同步文档

- 汇总设备名称编辑、真实网络与安全落盘、v3 长连接、文字/原图/附件会话、启动授权缓存、窗口恢复及浅色侧栏等近期修改，统一作为 0.3.3 平台里程碑提交。
- 公共协议、安全总则、跨平台 UI/交互规范、能力矩阵和 Windows 同步清单集中提交至 main；旧 v1 文档保留历史，Mac v3 文档改为公共规范链接与实现模块对应，避免两份权威描述分叉。
- main 新增 v3 公共夹具：transcript、双向 HKDF、PSS 签名及 7 条有序 GCM 消息，由 Node crypto 生成并经 CryptoKit/Security 独立复算。Windows 复算和 v3 实体互通仍待完成。
- 复用已通过的 58 项全量测试及 0.3.3 发布构建/包校验结果；本次整理不改业务行为。构建缓存、预览安装包和本地配置均不提交。

## 2026-10-23：0.3.3 优化浅色主题侧栏

- 浅色模式下将设备会话侧栏改为不透明白色，保留淡蓝色选中态及分隔线；深色模式继续使用原有系统背景色，并随系统主题切换。
- 本地预览包更新为 0.3.3，协议仍为 v3。此改动仅涉及背景样式，验证发布构建和预览包完整性。

## 2026-10-23：0.3.2 启动时完成钥匙串身份授权

- 启动时在后台读取身份，并用私钥执行随机挑战签名与公钥验签；检查通过后才启用监听、发现和主动连接。失败提供重新授权入口，准备期间不阻塞界面。
- 在应用运行期间缓存身份引用，连接和重连不再重新读取钥匙串。关闭窗口不清除身份，正常退出清除运行时引用；不保存登录密码，不导出私钥，不修改钥匙串访问控制。系统弹窗仍需选择“始终允许”以持久允许该应用；单次允许、版本更新或系统重新锁定时不能保证完全无系统提示。
- 取消将钥匙串人工授权算进 15 秒总握手期限的旧行为；初始网络交换仍有短超时，等待签名证明单独允许 5 分钟，提供交换信息、验证身份、等待对方授权及等待连接同意等阶段提示。
- 增加授权未完成不联网、授权失败可重试、授权期间退出不恢复监听、对端授权慢于网络期限的回归验证；真实本机 TCP 验证断开重连时双方身份只加载一次。
- 验证：FEIGE_TEST_BONJOUR=1 swift test 共 58 项全部通过（15 项应用、43 项核心），包含原协议短超时、网络发现、文字/图片/文件及窗口重开回归。
- 本地预览包更新为 0.3.2，保持 v3 兼容。系统密码弹窗的手动交互及实体双机仍待人工验收。

## 2026-10-23：0.3.1 修复关闭窗口后 Dock 无法重开

- 主界面改为有固定 scene ID 的单窗口，应用代理处理 Dock reopen；已关闭或释放的窗口通过 SwiftUI openWindow 重建，仍在显示或最小化的主窗口恢复到前台。设置窗口可见时也不会阻止主窗口重开。
- 明确关闭最后一个窗口后保持应用运行，保留当前连接；正常退出仍清理连接。文件菜单新增“显示主窗口”（⌘1），重复点击复用主窗口。
- 新增 3 项窗口回归测试：窗口关闭且其他窗口可见、窗口引用释放、重复重开不创建新主窗口；连同原生文字输入测试共 4 项通过。发布构建及本地预览包同步更新为 0.3.1，仍使用 v3，与 0.3.0 互通。
- 回归测试直接触发 AppKit reopen 回调与窗口状态；真实 Dock 点击和最小化动画仍待人工验收。

## 2026-10-23：0.3.0 设备会话、文字聊天与图片

- 将工作台改为设备会话：左侧历史和附近设备，右侧文字气泡、图片缩略图和文件卡片；保留设置、手动地址连接与旧传输历史入口。
- v3 在一次连接同意后增加加密文字及文字回执，可与双向文件数据交错发送。收到回执才显示已送达；消息失败可重试，文字重试 UUID 去重。v1/v2 不会静默降级。
- manifest.kind 区分图片和普通文件。图片原始内容发送，后台 ImageIO 生成有界缩略图；支持打开原图、打开文件和访达定位。图片/文件支持选择或拖入，文件无产品大小上限。
- 原生 NSTextView 输入支持 Enter 发送、Shift+Enter 换行和系统输入法；单条文字 8 KB UTF-8，长文可发文件。附件传输期间可继续聊天。
- messages.json 按完整设备指纹保存消息，避免同名设备合并；保留 1000 条已结束消息及活动队列，重启将未完成项恢复为失败。旧 history.json 保留。
- 验证：FEIGE_TEST_BONJOUR=1 swift test 共 51 项全通过（9 项应用、42 项核心），覆盖真实本机 TCP 文字/图片/文件、重复重试、持久化及原生输入组件按键行为。
- 预览包更新为 0.3.0。正式公证、实体双机、中文输入法真实键入、图片预览与拖拽完整界面操作仍待验收。暂未提供剪贴板图片粘贴、群聊或离线投递。

## 2026-10-23：0.2.0 简化为一次连接同意，取消文件大小上限

- 根据用户明确要求，移除应用中的配对码、SAS 重输和逐文件接收弹窗。应用启动后自动发现设备，发起方请求连接，仅接收方同意一次。
- 新增 v2 `ConnectionService` / `FileConnection`：签名与加密自动完成，显式连接决定回执后启用文件通道。同一次连接支持多批文件及双向同时传输，断开撤销本次授权，重连重新询问。
- 移除 100 MiB 文件阈值和 30 分钟总传输期限；分块内存限制和网络空闲超时保留。文件一遍流式读取、加密及 SHA-256 累计，最终摘要随 finish 提交，不再预扫描大文件。支持空文件。
- 保留严格路径/大小表示/偏移/ID/摘要检查、安全暂存、原子落盘和成功后记录历史；连接内无需重复批准文件。接收路径在每个新文件开始时读取当前设置。
- v2 明确使用新版本号、HKDF 域、12 字节 nonce 和连接回执类型，旧版不会静默降级进入旧确认流程。协议差异见[连接协议 v2](ConnectionProtocolV2-Historical.md)，两端当前共同规范见 `Project/Docs/Public/ProtocolV3.md`；v2 仅作历史记录。
- 新增本机真实 TCP 128 MiB+17 字节流式传输及源/目标摘要验证；应用状态模型验证一次同意后的多文件与反向发送，核心验证同连接双向同时发送、空文件、同名保护与异常清理。
- 验证：`FEIGE_TEST_BONJOUR=1 swift test` 共 46 项全部通过（7 项应用测试、39 项核心测试），包含发现、原 v1 原语回归及新的 v2 流程。
- 预览包版本更新为 0.2.0。实体双机与完整界面操作仍待验收，正式公证条件与前一阶段相同。

## 2026-10-23：协议修复、网络与实际文件收发接入

- 修复网络 FileManifest 解码绕过字段校验、接收未消费 finish、回执早于最终落盘、控制载荷不严格及非预期消息被无限缓存的问题。
- 修复半帧之后收到满批 TCP 数据时误判缓冲超限；有界缓冲按帧顺序处理，拒绝结束后的同批多余记录。
- NetworkByteStream 支持主动及被动连接；超时/取消会关闭底层网络连接以恢复回调，增加握手总期限及单文件会话期限。
- 接入用户主动开启的 Bonjour 发现、监听、IP/端口手动连接、局域网地址检查、单连接限制、配对码过期停止监听。
- 接通人工身份核对和文件接收确认；使用目录描述符与 openat 创建暂存文件，SHA-256 校验后 fsync 并以 RENAME_EXCL 原子落盘，同名添加编号，不覆盖已有文件或符号链接。
- 接入进度、成功历史持久化及旧历史读取、最多 100 个文件的串行队列、取消和剩余文件重试。每个文件重新握手和人工确认，未扩展 v1 线上消息格式。
- 增加正式 app 包清单与本地 release 打包脚本，包含局域网用途说明和 Bonjour 服务类型；支持显式提供 Developer ID 与已有公证凭据后的签名/公证流程，默认只生成 ad-hoc 预览包。
- 验证：`FEIGE_TEST_BONJOUR=1 swift test` 共 40 项全部通过（7 项应用测试、33 项核心测试），覆盖本机 Bonjour 互相发现、真实 TCP 单文件与双文件队列、拒收不落盘、不写成功历史、取消/超时、回执与结束帧异常、同名及符号链接保护。发布构建和预览包签名检查通过。
- 明确未验收：实体双机、Windows 互通、完整原生界面操作、特殊网络/权限/磁盘不足场景、Developer ID 公证和干净机器安装。本机只有 Apple Development / Distribution 证书，没有用于公证分发的 Developer ID 证书。详见[验收清单](AcceptanceChecklist.md)。
- 当时的公共协议仍为草案；此历史记录中的 TXT 差异需与现行 [Public protocol v3](../Public/ProtocolV3.md) 区分，不代表当前兼容状态。

## 2026-10-23：设备名称编辑与设置保存修复

- 为 Swift Package 启动的应用补充前台激活策略，并在设置窗口打开时聚焦设备名称输入框。
- 侧栏、编辑按钮、工具栏及菜单直接打开同一个偏好设置窗口，移除依赖主窗口观察布尔状态的中转。
- 保存设置改为先持久化成功，再更新界面状态；失败在设置窗口内提示，不再依赖全局旧错误判断保存结果。
- 新增 3 项设置测试，覆盖中文名称更新与重新加载、名称长度校验、写入失败后的状态保持与重试；`swift test --filter SettingsTests` 全部通过，应用构建通过。
- 当前未获界面自动化权限，键盘输入和窗口焦点仍需人工验收。

## 2026-10-23：文档治理，公共文档迁至 main

- 删除本分支的《开发总则》，改链 `main` 的权威版本，避免与 Windows 分支各自维护后协议描述分叉。
- 更新根 README 与文档索引，按当前实现修正“Bonjour 已接入/可广播”的过时描述：监听与广播未启用，真实收发不可用。

## 2026-10-23：应用层 TransferService 编排与 SwiftUI 确认接线

- 新增 `TransferService.establishSession`：在已连接传输上串起签名握手 → 本机用户确认对端指纹/SAS → 派生会话。确认回调在签名校验成功后、派生密钥前被等待，用户永远看到已验证的对端再批准。
- 新增 `UserConfirmation`（对端指纹 + 用户输入 SAS），由确认回调返回，服务层不伪造该值。
- `TransferModel.connect(transport:role:sessionCode:)` 用 `TransferService` 替代旧的 `preparePairing`/`confirmPairing` 手工编排；`requestUserConfirmation` 把已验证的对端名、指纹、SAS 发布到 SwiftUI 并通过 continuation 挂起，等待 `confirmPendingPairing`/`rejectPendingPairing`。
- `ContentView` 审批 sheet 改用 `pendingPeerFingerprint != nil` 触发，确认/拒绝分别调用新方法。
- `HandshakeRole`/`HandshakeOffer`/`HandshakeTranscript`/`PendingPeerApproval` 标记 `Sendable`；`TransferService` 的确认回调标 `@Sendable`，以满足 Swift 6 跨隔离传递。
- 网络监听、Bonjour、socket 收发仍关闭；`NetworkByteStream` 尚未接入应用，真实双机互通未测。
- 验证：`cd Project/MacOS && swift test`，21 项全部通过（含 3 项 `TransferServiceTests` 编排层测试）。

## 2026-10-23：端到端集成测试（握手→批准→加密传输全流程）

- 新增 `AuthenticatedHandshakeExchange`：在已连接的 `ByteTransport` 上做四消息签名握手（offer→proof），校验对端签名后只返回待批准状态，不直接派生密钥。
- 抽象 `ByteTransport` 协议；`NetworkByteStream` 实现真实连接，测试提供 `InMemoryDuplexTransport`（内存双工管道）以在无 socket 情况下端到端验证全流程。
- 新增 `EndToEndTransferTests`：两台虚拟设备并发完成签名握手、双方 SAS 确认、方向密钥交叉校验，再以 AES-GCM 记录传输文件（manifest→decision→chunk→finish→SHA-256 校验），12 万字节文件往返一致。
- 新增 `TransferSession`：把方向记录流接到 `TransferCoordinator` 状态机，封装单文件 send/receive；`TransferSessionTests` 验证批准写入与拒绝不写入两条路径。
- 修复 `InMemoryDuplexTransport`：pending reader 记录各自 maximumLength，避免被一次投递过多字节导致 framing 报错；修复 `SecureRecordCipher` 允许 finish 空载荷。
- 网络监听、Bonjour、socket 收发仍关闭；`NetworkByteStream` 尚未接入应用，真实双机互通未测。
- 验证：`cd Project/MacOS && swift test`，18 项全部通过（含端到端集成与会话编排）。

## 2026-10-23：原生 SAS 确认界面与配对状态

- SwiftUI 在有真实 pending transcript/peer offer 时显示审批 sheet：设备名、线下核验用公钥指纹、SAS 和人工输入字段；用户拒绝会清理 pending 状态。
- `PairingApprovalCoordinator.confirm` 要求输入对端指纹和手动重输 SAS；approval token 绑定 transcript digest、role 对应对端指纹、过期时间，并在消费后成为单次 token。
- `TransferModel.preparePairing(with:)` 只能接收真实 `HandshakeOffer` 值，构造/签名 transcript；需要相同一次性会话码。UI 不接受用户自行输入/伪造 peer offer 或指纹来建立“已验证”状态。
- 首页配对码用系统安全 RNG 生成，5 分钟过期；配对码只用于 rendezvous/一致性，不作为密码，不会自动启动网络。
- 网络监听、Bonjour、socket 收发仍关闭；真实 peer offer 需要待实现的 Network.framework 适配层提供，因此实际运行 UI 暂不会自行弹出 SAS sheet。
- 验证：`cd Project/MacOS && swift test`，13 项全部通过。

## 2026-10-23：接收状态机与加密协议原语（仍未联网）

- `TransferCoordinator` 仅在用户批准后向临时 sink 写入受限分块；校验连续 offset、大小和 SHA-256 后才报告完成。它不自行选路径/覆盖文件/打开网络。
- 实现 Keychain RSA 设备身份、RSA-PSS transcript 签名、临时 Curve25519、HKDF 方向密钥、AES-GCM 记录、header AAD 和序列防重放。
- 握手 transcript 仅由 validated factory 创建，绑定角色、身份、nonce、临时密钥与双方一致配对码；握手对象仅单次派生会话。
- 旧 `TransferProtocol.swift` 中缺陷 TLS verifier 和裸文件网络 helper 已删除。
- 验证：Mac Apple Silicon / macOS 27 / Swift 6.4，`swift build` 和 `swift test` 通过（13 项）。
- 安全边界：Network.framework adapter、超时/取消、双机确认与真实文件流尚未实现，广播与监听保持关闭。

## 2026-10-23：Keychain 设备身份

- 新增 `Identity/Sources/DeviceIdentity.swift`，使用 Keychain 持久化 RSA 3072 私钥，并对标准 SPKI DER 公钥计算 SHA-256 指纹。
- 启动时读取或创建设备身份；设置界面可以复制/查看本机指纹。
- XCTest 验证身份跨加载稳定，并在测试结束后清理临时 Keychain 项。

## 2026-10-22：SwiftUI 项目与文档整理

- 建立 Mac SwiftUI、Windows 原生项目入口和开发文档目录。
- 因身份/传输协议未完成，停用实际网络接收和广播。
- Mac SwiftUI 工程可在当前 macOS 环境编译。
