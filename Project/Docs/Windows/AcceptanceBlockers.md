# Windows 群组邀请验收：问题清单与待办

> 记录时间：2026-11-02
> 状态：**Windows 群组主流程的选定 UI/LAN 路径已通过，整体发布矩阵仍未完成。** Windows 11 ARM64 与 ROG x64 / .NET 10 的 Core 578/578、App 8/8 是本次更新配置解析修复前的全量基线（ROG 使用 commit `ac9a3d8` 隔离副本）。修复后 ROG x64 更新相关用例 62/62 通过；当前 Core 全量 581/581 在 macOS .NET 11 RC major roll-forward 下通过。ROG Windows x64 上 App x64 Release 非打包编译以及 UpdateTool、UpdateProbe Release 编译均 0 警告、0 错误；替换旧的 nullable 测试后，原 Core 测试警告已消失。Mac 群主邀请 Windows、Windows 群主建群与成员增删、以及混合拓扑 20 人群组广播均有实测通过记录。完整双向跨端文件/消息与异常路径、Windows↔Windows/Mac↔Mac 真机矩阵、正式发行包和更新闭环仍待完成。
> **20-peer 范围说明**：通过的 LAN20 用例是 1 个 Parallels Windows owner 加同一 Mac 宿主上的 19 个隔离 `TransferModel` runtime，不是 20 台实体设备；20 台实体设备不是发布验收门槛。
> 本文只写实测能确认的事实；没有验证到的猜测会明确标注为“待确认”。

## 一、当前已完成

| 项 | 证据 |
| --- | --- |
| Windows ARM64 Core 全量基线（commit `ac9a3d8`） | Windows 11 ARM64 / .NET 10.0.402，`Feige.Core.Tests` 578/578 通过 |
| Windows ARM64 App 全量基线（commit `ac9a3d8`） | 同一来宾环境，`Feige.App.Tests` 8/8 通过 |
| macOS 群组传输自动化 | `GroupConversationTransferTests` 6/6 通过 |
| Mac 群主邀请 Windows 的真实 UI/LAN 验收 | `testMacOwnerInvitesWindowsAndWindowsAcceptsFromItsGroupsPage` 1/1 通过；Windows 保存两名成员和群文字并回执 |
| Windows 群主建群、添加及移除成员的 UI/LAN 验收 | `testWindowsUiGroupCreateAddAndRemoveOverLANWhenConfigured` 1/1 通过（138.590 秒） |
| Windows 群主 20 人群组与广播验收 | `testWindowsUiCreatesTwentyMemberGroupAndBroadcastsOverLANWhenConfigured` 1/1 通过（676.361 秒）；19/19 runtime 收到广播，成员表在各 runtime 收敛 |
| 39 条连接容量回归 | 认证 loopback TCP 测试通过；这不是实体 20-peer LAN 测试 |
| LAN20 验收后的 Windows 恢复 | LocalState 逐文件与 manifest 一致；原 TCP/UDP 防火墙规则恢复，临时规则和本轮临时数据清理 |
| Windows 更新配置兼容回归 | ROG x64 / .NET 10 更新相关 Core 用例 62/62；当前 Core 全量 581/581（macOS .NET 11 RC major roll-forward）；App 非打包 Release、UpdateTool 与 UpdateProbe Release 编译均 0 警告、0 错误 |
| Windows App x64 集成回归（解析修复后） | ROG x64 / .NET 10 `Feige.App.Tests` 8/8 通过；`FeigeTestIsolation=true`，未启用 UI profile provisioning |
| Windows 更新签名清单 | Windows .NET 10 UpdateTool 验证 LAN 清单签名和来源 URL，记录 1.2.1 build 1301；ROG 请求 GitHub latest 清单返回 HTTP 404 |

真实跨端（Mac ↔ Parallels Windows）已完成部分 v5 握手、群组邀请与成员状态路径；这不等于完整跨平台文件传输与异常场景矩阵。

## 二、已发现并修复的缺陷

### 缺陷 1：取消读取会关闭整条连接（已修复）

- **位置**：`Project/MacOS/Identity/Sources/NetworkByteStream.swift`
- **现象**：握手完成后连接立即断开，macOS 报 `认证流在完成前结束`，Windows 报 `v4 accept: EndOfStreamException` / `IOException`。
- **根因**：`timed()` 的 `onCancel` 直接调用 `self.close()`。握手 deadline 在任务组返回时 `cancelAll()`，命中了这条路径，把仍然健康的传输关闭。
- **修复**：取消时只恢复被挂起的 Network.framework 回调（`AbandonBox` + `Suspension`），不关闭 socket；真正的超时仍然关闭。
- **回归测试**：`Project/MacOS/Tests/NetworkByteStreamCancellationTests.swift`，取消一个挂起的 `receive` 后再读，断言得到 `timeout` 而非连接失败。
- **验证**：修复后连接从“秒断”变为可稳定保持，直到下一个断点出现。

### 缺陷 2：测试等待窗口短于对端同意超时（已修复于测试侧）

- Windows 同意对话框超时为 5 分钟；验收用例原先只等 90 秒，导致用户尚未完成点击时用例已判定失败。
- 已把等待窗口对齐到 300 秒。

## 三、当前状态与仍待验收项

### 问题 A：Windows 群组页未显示邀请（已修复）

**根因**：审批卡片确实进入 `PendingApprovals` 并被 `RefreshApprovals` 渲染，但 `DetailPane` 在 `_selected` 为空时保持折叠。未选择现有群组时，邀请卡片因此不可见。

**修复**：`GroupsPage.RefreshDetailPaneVisibility()` 在有待审批项时显示详情面板；没有选中群组时仍禁用群组操作。刷新、选中群组和审批集合变更都会更新面板可见状态。

**Parallels 实测**：

1. Windows 通过 owner 身份校验，审批回调存在，邀请进入待审批集合并渲染。
2. UI Automation 找到并点击“接受”；Windows 日志确认 `accept=True`、邀请回复 `accepted=True`。
3. Windows 群组页出现“Mac-owner invite acceptance”，显示 2 名成员。

本轮完成闭环验收。此前长时间等待的直接原因是验收配置仍使用旧 Windows 指纹 `0102…`，而当前 Windows 隔离运行实例的持久身份已变更；握手本身成功，Mac 已连接到正确的 Windows 应用，但测试一直用旧指纹查询连接。通过 Windows 当前 DPAPI 身份只计算公钥指纹后，将验收配置更新为当前指纹，重新运行即完成连接。独立 `NWConnection` 探测到 `10.211.55.4:59403` 也已成功，路径为 `bridge100`；没有改动系统 VPN 或网络设置。

真实验收 `testMacOwnerInvitesWindowsAndWindowsAcceptsFromItsGroupsPage` 最终 **1/1 通过**。Windows UIA 实际接受邀请，Windows `v4-groups.json` 存有该群的两名成员及 revision 0，群消息也写入持久化存储；Mac 侧收到了群消息确认回执。过程中修正了验收用例的指纹比较：紧凑格式与四位分组格式先统一为紧凑大写再比较；超时辅助函数现在会抛出失败，避免测试在前置断言失败后继续报告后续成功。

### 问题 B：Windows 群组页接受邀请的 UI 点击验收（已打通）

- Parallels 窗口通过 UI Automation 调用按钮，无须依赖本会话桌面输入权限。
- “接受”操作已在真实 Windows UI 中完成并得到应用日志确认。

### 问题 C：仍待完成的设备矩阵与正式发布验收

- **实体设备条件**：Mac↔Mac 暂无可用第二端；Home mini 为 macOS `12.7.6`，低于客户端 macOS 13 目标，且标准 `/Applications/Feige.app` 不存在；Ivy mini `192.168.50.229` SSH/SFTP 超时。Windows↔Windows 暂无第二个已安装当前应用的实体端；ROG 可达但未安装 Feige，按现有约束不在其上安装测试包。
- **最小双端矩阵**：已有 Mac 群主邀请 Windows、Windows 群主建群/加删成员及混合 20 人广播等成功案例；仍需用发布候选包覆盖双向文字、图片/文件、同意/拒绝、取消、断线重连及主要错误路径。若 Windows↔Windows 属于本次发布承诺，使用一台已装当前版本的第二台 Windows 验证即可；不要求 20 台实体设备。
- **容量与负载**：混合拓扑 20 人群组和广播已通过，39 条认证 loopback 连接也有容量回归；持续负载资源采样尚未记录，可用隔离 runtime 完成，不以 20 台实体设备为前提。
- **正式发布**：Windows App 当前源码 x64 Release 非打包编译已通过（0 警告、0 错误）；ROG x64 / .NET 10.0.402 上的更新相关 Core 用例 62/62 通过，当前 Core 全量 581/581 在 macOS .NET 11 RC（major roll-forward）通过；此前 commit `ac9a3d8` 的 Core 578/578、App 8/8 是本次解析修复前的全量基线。UpdateTool 与 UpdateProbe Release 编译也通过（0 警告、0 错误）。Windows .NET 10 UpdateTool 已验证 LAN 清单签名和来源 URL；GitHub latest 清单端点当前返回 HTTP 404。测试未安装或启动应用，也未下载更新包；最终签名包安装/升级/卸载，以及客户端包下载、Authenticode 验证和交接安装仍未闭环。具体状态见 [项目概览](ProjectOverview.md) 与 [更新接入文档](UpdateService.md)。

## 四、下一步计划

1. 冻结首发范围和支持的 Windows/macOS 版本、架构及平台组合；将目录传输、断点续传等文档列出的后续功能明确为本次包含或延期。
2. 用少量可用设备完成发布候选包的双端关键路径；若某个平台组合没有兼容设备，记录成明确的首发限制。无需把 20 台实体 peer 设为门槛。
3. 完成 Windows 签名发行包与干净安装/升级/卸载，发布更新清单并完成客户端端到端安装验收；Core/App 的 x64 自动化回归已通过，仍需候选包与真实客户端运行/更新验收。
4. 完成 macOS Developer ID 签名、公证、stapling 与干净机器 Gatekeeper 安装验证；按支持架构执行更新和安装测试。
5. 汇总已知限制、验收证据与恢复/升级说明，完成状态文档同步。

## 五、历史工作区状态（2026-10-31 记录）

- 截至 2026-10-31 的该轮验收记录：未为该轮生成新的发行包，相关改动未提交/未合并；不代表仓库外已有发布材料或本次文档更新后的工作区状态。
- 临时 `FEIGE_DIAG` / `FEIGE_DIAG_LOG` 源码日志已清理。
- 必须保留的验收测试指纹分组格式变更仍在；群组审批面板可见性修复仍在。
