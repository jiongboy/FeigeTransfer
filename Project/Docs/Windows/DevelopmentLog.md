# Windows 开发日志

> 按时间倒序追加；只记录已实现、已验证与明确未完成项。跨平台摘要见[更新日志](../Public/UpdateLog.md)。

> **路径写法说明（2026-10-27 重命名）**：本日志是历史记录，条目中的路径按当时仓库的布局书写；迁移到统一仓库后对应关系如下，阅读时按新布局理解。
>
> | 日志中的旧写法 | 现在的位置 |
> | --- | --- |
> | `Windows/`、`Windows\scripts\`、`Windows/tests/` | `Project/Windows/` 下的同名子目录 |
> | `Windows/site/` | `Project/Windows/site/` |
> | `Windows/dist/build/`、`Windows/dist/` | `Project/Windows/dist/`（暂存目录，不提交 Git） |
> | `download/`、`download/msix/` | `Release/Windows/<版本号>/`（本地交付目录，不提交 Git） |
> | `Windows/release/`（当时的入库目录） | 已取消入库；交付包改放 `Release/Windows/<版本号>/` |
> | `branding/feige-icon-v1/` | `Project/Ui/Branding/FeigeIconV1/` |
> | `开发文档/` | `Project/Docs/`（分为 `Public/`、`Windows/`、`MacOS/`） |
> | 旧 `windows` / `macOS` / `main` 分支 | 统一为单一 `main` 主线 |

## 2026-10-28 剪贴板图片粘贴

- 聊天输入框现在支持 Ctrl+V（或 Shift+Insert）直接粘贴剪贴板中的截图：剪贴板含文件时仍按文件发送（资源管理器复制图片文件常同时携带两者，文件名更重要）；否则若含位图，则经 `BitmapDecoder` + `BitmapEncoder` 转为 PNG，保存到 `<AppData>\PastedImages\` 后按图片发送。
- 之所以先落盘再发送：剪贴板中的位图流不持久，且 `startSending` 读的是文件路径；落盘还让“重试”能重发完全相同的字节。
- 与 macOS 行为一致（Mac 输入框原本已支持 ⌘V 粘贴图片）。
- **验证范围**：`Feige.Core` 466 项测试在本机 .NET 11 SDK（roll-forward 运行 net10 目标）下全部通过，确认未影响核心逻辑。剪贴板代码单独做了类型检查：用桩类型复刻 Microsoft Learn 的真实 WinRT 签名后编译新增方法体，**据此发现并修复了 `using var` 误用 `BitmapDecoder`/`BitmapEncoder` 的编译错误**（这两个类型不实现 `IDisposable`）。WinUI 工程本身仍无法在 macOS 上编译（需 `net10.0-windows` 目标与 Windows 开发机）；实机粘贴验证待 Windows 环境执行。

## 2026-10-27 正式签名材料重建、ROG 打包机接入、首个正式版发布

- **签名材料重建**：原 pin `799BFE…` 的证书在 ROG 上仍在，但代码签名与清单两套私钥都被创建为不可导出，重装即失效。按用户决策重建：新证书 `CN=Songli`（RSA-3072，至 2036-10-27，pin `7EAEAE46…`）、新清单密钥 `feige-update-release-2026`（`ExportPolicy=AllowExport`），并把加密 PFX 与 PKCS#8 私钥备份到 `C:\Keys\FeigeRelease\`。`UpdateSource.json` 的公钥与 pin 已同步为新值，**旧测试包因此全部失效**。
- **ROG 作为打包机**：Parallels 虚拟机的 CNG 无法创建带 EKU 的自签证书，也无法导入私钥，因此正式打包固定在 ROG（.NET 10 SDK + Inno Setup 6.7.3 + 证书库）。Parallels 仅用于安装验收。
- **新增能力**：`Build-Installer.ps1` 支持 `-SigningPfxPath/-SigningPfxPassword`，用 `signtool /f /p` 直接从 PFX 签名，私钥不进入用户证书库；`Feige.UpdateTool sign` 支持 `--key-file` 读取 PKCS#8 私钥。同时修正 `VerifyPinnedAuthenticode.ps1` 中 `-ne NoError` 的枚举比较错误（PowerShell 把枚举右值当字符串，导致校验恒不通过），并为安装包签名校验加了写盘后的短暂重试。
- **产物传递改走 git LFS**：Tailscale 中继实测 58 KB/s（65 MB 需 20 分钟），git LFS 实测 1.8–2.1 MB/s。当时新增 `Windows/release/` 作为入库目录，`.gitattributes` 用 LFS 跟踪 `*.exe`/`*.zip`；流程与额度说明见 [Windows发布产物传递](ReleaseArtifactTransfer.md)。（该入库方式已取消，现交付包只放 `Release/Windows/<版本号>/`，不提交 Git。）
- **下载官网**：`/feige/` 提供产品介绍与下载，页面读取 `stable` 清单（无正式版时回落到 `preview`），源码迁到 `Windows/site/`（现 `Project/Windows/site/`）纳入版本库。`/updates/` 目录页取消，`/updates` 302 跳转到官网，更新清单与安装包直链保持不变。
- **首个正式版**：`stable` 发布 1.2.0 build 1300，随后发布 1.2.1 build 1301。虚拟机从旧测试版卸载后安装正式版，再由应用内自动更新到 1.2.1，日志记录 `update: completed version=1.2.1 build=1301 stage=installed-awaiting-restart`，下载哈希与签名清单一致。
- **清理**：三台设备共释放约 2.3 GB（ROG 1.7 GB、本机 Mac 0.5 GB、Mac mini 0.14 GB）；Mac mini 现只保留 `stable` 的 b1301，**没有回滚点**。Core 449 项测试通过。

## 2026-10-26 清空历史版本，打包产物改走 Mac mini

- 按用户要求删除全部历史版本：Mac mini 上的 `windows/preview` 与 `windows/stable` 版本目录和清单指针全部清空（80 MB → 0），当时的本地交付目录 `download/` 清空（1.4 GB → 只剩新包；现为 `Release/Windows/<版本号>/`），不再保留 1.1.x、1.2.0 与 b1001～b1016 的任何旧包。
- 发布流程调整：`Build-Installer.ps1` 输出改到 `Windows/dist/build/`（现 `Project/Windows/dist/build/`，被忽略的暂存目录），`New-UpdateRecord.ps1` 与 `Feige.UpdateTool sign` 的输入输出同样落在该目录；只有 `Publish-LanUpdate.ps1` 在完成服务器校验并推进指针后，才把安装包、sidecar 与签名清单归档到仓库的本地交付目录（当时为 `download/`，现为 `Release/Windows/<版本号>/`）。
- 重新打包并发布 `b1100`（1.2.0 x64，含版权 `© Songli`、英文默认接收文件夹 `FeigeTransfer`、侧边栏间距、表情按钮居中、更新弹框与自动重启修复）。服务器 `pointer=200`、`package=200`，上传后 SHA-256 与签名清单一致（`6712A584…`），虚拟机实测可读到 `build=1100`。
- 仍为测试信任域（虚拟机自签证书 + 测试 CNG 密钥），`stable` 未发布；仓库生产公钥/pin 已恢复。Core 449 项测试通过。

## 2026-10-26 更新交接被 PowerShell 执行策略拦截（b1009 → b1011）

- 用户手动点更新后停在下载完成、未安装。日志显示 `handoff exited code=1`，交接脚本的 `ready.error` 标记给出真实原因：`PSSecurityException: 无法加载文件 …VerifyPinnedAuthenticode.ps1，因为在此系统上禁止运行脚本`。该机器的实际执行策略为 `Restricted`（各作用域均 Undefined，Windows 客户端默认值），点源加载随包脚本被拒绝，交接在写 `ready` 之前就退出了。
- 修复：`WindowsUpdateProcess.Command` 统一追加 `-ExecutionPolicy Bypass`。调用方的脚本内容全部由应用生成（数据经 Base64 JSON 传入，不执行发布包里的文本），被点源的校验脚本随安装包签名分发，因此这里只影响进程级策略，不改动系统范围设置。
- 回归：重新构建 `b1011` 并发布到 `preview`，用 UI 自动化从 `b1009` 完整走一遍，结果 `installing` → `installed-awaiting-restart`，应用自动重开，日志记录 `update: completed version=1.2.0 build=1011`，安装结果文件已清除。
- 另：顺手修正发送工具栏“表情/图片/文件”按钮的纵向居中（图标与文字统一 `VerticalAlignment=Center`，emoji 下移 1px 抵消字形基线）。仍为测试信任域与 `preview` 渠道，`stable` 未发布，仓库生产公钥/pin 已恢复；Core 449 项测试通过。

## 2026-10-26 第二轮更新回归（b1007 → b1008）

- 按用户要求再次完整走一遍更新流程。先在 Parallels 中结束残留的多个应用实例（此前手动安装时被安装器检测到“正在运行”即因窗口关闭仅隐藏、进程仍存活），再构建 `b1008` 并发布到 `preview`。
- UI 自动化实测：`b1007` 运行中点顶部“更新”→ 弹框“下载并安装”→ 下载 → 应用退出 → 安装 → 自动重开；结果依次为 `installing` → `installed-awaiting-restart`，随后应用进程恢复运行。
- 日志确认 `update: completed version=1.2.0 build=1008 stage=installed-awaiting-restart`，安装结果文件已确认并清除，可执行文件时间更新为本次构建。
- 仍为测试信任域与 `preview` 渠道，`stable` 未发布；仓库生产公钥/pin 已恢复。Core 449 项测试通过。

## 2026-10-26 更新结果提示与交接稳定性（端到端验证通过）

- 用户反馈更新完成后没有任何状态提示，无法确认是否更新成功。现在应用重新启动后会读取安装结果，在主窗口显示可关闭的成功提示条“更新完成：已更新到 x.y.z（构建 n）”；结果与当前运行版本不符时显示警告提示条“更新未确认”，并在设置页保留对应状态。安装结果确认后删除结果文件，避免重复提示。
- 交接失败的另一原因：交给安装器的 PowerShell 子进程由无控制台的 GUI 进程启动，未重定向的标准句柄可能让子进程提前退出，导致“交接未就绪”。`PrepareAsync` 现在持续排空子进程 stdout/stderr，把退出码与错误写入日志，并在超时前等待 60 秒；交接脚本在启动、验证失败时分别写 `.started` / `.error` 标记，便于定位。
- 端到端验证（Parallels，测试信任域 + preview）：用 UI Automation 真实点击顶部“更新”→ 弹框“下载并安装”→ 下载 67.9 MB → 应用退出 → 安装 → 自动重新启动，日志记录 `update: completed version=1.2.0 build=1007 stage=installed-awaiting-restart`，安装结果文件被确认并清除，应用可执行文件已替换为新构建。
- 仍为测试信任域；`stable` 未发布，仓库生产公钥/pin 已恢复。Core 449 项测试通过。

## 2026-10-26 更新交互修正：直接弹框与安装后自动重启

- 用户实测反馈两点：点击更新提示会跳到设置页；安装完成后应用没有自动重启。前者改为直接在窗口内弹出更新对话框（版本、构建、大小、说明，按钮为“下载并安装 / 查看详情 / 稍后”），选择“下载并安装”后复用现有下载与安装交接，不再强制跳转设置；“查看详情”才进入设置页。顶部提示同时显示下载/安装进度并禁用按钮。
- 重启问题根因来自安装交接脚本：安装成功后启动应用若遇到文件短暂被占用会抛异常，被外层 catch 覆盖为 `handoff-failed`，结果文件因此不再表示“已安装”。现改为：安装前先写入 `installed-awaiting-restart`，启动应用做 10 次重试并以资源管理器打开兜底，安装成功后不再被启动失败覆盖；新增 `installed-restart-failed` 状态，应用仍将其视为安装完成。安装失败则回滚为 `installation-failed` 并尝试恢复旧版本窗口。
- 隔离验证：在 Parallels 中以真实安装交接脚本执行一次完整交接（提交文件存在、旧进程已退出），安装完成后应用被自动拉起（进程存活），结果文件为 `installed-awaiting-restart`。随后把 `b1003` 发布到 `preview`，运行中的 `b1002` 在 1 分钟内自动检查到新版本并写入“发现新版本 1.2.0”，可供界面点击验证。
- 说明：以上仍使用测试信任域（虚拟机自签证书 + 测试 CNG 密钥）与 `preview` 渠道，`stable` 未发布；仓库生产公钥/pin 已恢复。Core 449 项测试通过。

## 2026-10-26 打包与 Parallels 局域网更新联调（测试信任域，未发布 stable）

- 按用户要求打包并在 Parallels Windows 11 安装当前版本，再把新构建上传 Mac mini、验证更新链路。生产签名材料（CNG 清单私钥、与 pin 匹配的代码签名证书）不在这台虚拟机，因此本轮建立**独立测试信任域**：虚拟机内自签 RSA-3072 代码签名证书 + 新建测试 CNG 清单密钥，只用于 `preview` 渠道，未改动生产公钥/pin，`stable` 指针保持为空。
- 构建并签名的测试包：`FeigeSetup-1.2.0-b1001-x64.exe`（安装到 Parallels `songli` 用户）与 `b1002`（发布到 Mac mini `windows/preview`）。安装后应用可正常启动并显示主窗口。
- 联调中发现并修复 6 个真实缺陷：① `Build-Installer.ps1` 的 `+= @('/D…' + $x, …)` 因 PowerShell 逗号优先级高被拼成单个字符串，`FeigeSigningEnabled` 从未生效，Inno 实际一直产出未签名安装包；② 签名工具需暂存到本地临时目录，不能放在 UNC 共享（32 位 Inno 受 System32 重定向影响）；③ x64 包 `ArchitecturesAllowed=x64os` 会拒绝 ARM64 Windows，改为 `x64compatible`；④ `Publish-LanUpdate.ps1` 的 `-WhatIf` 会传播到内部 `Get-FileHash` 导致校验崩溃；⑤ `[Uri]::new(string,string)` 在 Windows PowerShell 绑定到错误重载，指针 URL 丢失 `/updates`；⑥ 配置文件 UTF-8 BOM 会让 `JsonSerializer` 读取失败，配置/清单读取改用文本读取。
- 更新链路验证（测试探针 `Windows/tests/UpdateProbe`，与 App 共用 `UpdateClient` 与 pin 校验脚本）：目标 build 1001 从 Mac mini 取回签名清单，验签后选出 build 1002，下载 67.8 MB 并校验尺寸与 SHA-256，最后通过 pinned Authenticode；把 pin 故意改错后下载被拒绝。
- 未完成：应用内按钮触发的更新检查/下载与安装交接（需先完成两页引导并使用交互桌面）、正式签名材料核对、`stable` 正式发布与端到端安装升级验收。测试证书与测试 CNG 密钥仅存在于该 Parallels 虚拟机。

## 2026-10-26 更新源迁到 Mac mini 局域网（未打包、未发布清单）

- 按用户要求把 Windows EXE 更新源从固定 GitHub 地址改为家庭局域网 `http://192.168.50.229:8080/updates`，并保留 MSIX 走对应发行渠道。客户端改为 provider-neutral 的 `UpdateSource`/`UpdateClient`：base URI + 受信主机，清单仍为 RSA-3072 PSS/SHA-256 签名，下载仍校验尺寸、SHA-256 与 Authenticode publisher pin。
- 安全边界：私网明文 HTTP 仅在配置显式启用 `allowInsecurePrivateNetwork`、主机为受信任私有 IPv4、端口固定 8080 时接受；清单中的下载/说明地址必须与配置源完全同源，不允许改用其他受信主机，且拒绝查询串、fragment、userinfo、`..` 与百分号编码的 base path。重定向只允许留在同一源。
- Mac mini 上执行 `Setup-LanUpdateServer.sh`：更新目录 `/usr/local/var/www/feige-updates/windows/stable/versions`，在既有 nginx `server` 中新增 `location /updates/`（`allow 192.168.50.0/24`、`deny all`、仅 GET/HEAD），`nginx -t` 通过后 reload，配置备份放在 include 目录之外。实测清单 404、写请求 403；未从另一台 LAN 设备实测，nginx 仍监听 `*:8080`。
- 新增 `Publish-LanUpdate.ps1`：校验签名清单与包、上传到临时目录并核对远端哈希、同文件系统原子改名为 `versions/<tag>`、从真实 HTTP 地址复验后再原子替换指针（保留 `feige-windows-update.previous.json`），失败时区分“版本已提升/指针已推进”并支持 `-ResumePointer`。`Build-Installer.ps1` 改为默认必须提供 `-CertThumbprint`，未签名测试包需显式 `-AllowUnsignedTest`；`New-UpdateRecord.ps1` 增加 sidecar 与版本/构建/架构一致性检查。
- 验证：Core **449/449** 通过（本机仅 .NET 11 RC，使用 `DOTNET_ROLL_FORWARD=Major` 执行 net10 测试），UpdateTool 构建通过，脚本仅做静态检查。**未在 Windows .NET 10 环境执行脚本、未重建 WinUI、未核对签名材料、未生成签名包、未发布清单，也未做客户端更新安装验收。** 现状见[更新接入文档](UpdateService.md)。

## 2026-10-24 文档整理、虚拟机桥接与旧测试实例清理

- 统一项目入口、Windows 使用说明、文档索引、项目状态、开发手册和 1.2.0 交付说明中的范围：现有安装包仍是 392 项测试的交付基线；当前源码已包含名称同步、刷新反馈及签名链路改进，核心测试 405/405、双架构构建通过，尚未重新打包。保留各轮历史验证记录，明确原生双机名称/刷新界面仍待复验。
- 自动发现排查发现 Parallels 采用共享网络，Windows 虚拟机为 `10.211.55.4`，与 ROG 的 `192.168.50.202` 不处于同一发现链路。按用户要求改为桥接 `Wi-Fi (en0)`，重新连接虚拟网卡并更新 DHCP 后取得 `192.168.50.121/24`，网关 `192.168.50.1`。实测虚拟机到 ROG `51851`、Mac 到虚拟机安装验收实例 `64813` 的 TCP 端口连通，设备广播出现在 Wi-Fi 链路；没有执行完整双机握手/收发验收。
- 用户随后明确要求关闭两个不用的测试实例。核对端口、进程及程序路径后，停止旧 AppX 开发实例（当时 PID 3740，端口 `51842`）和安装验收实例（当时 PID 480，端口 `64813`）。停止后再次检查，两个旧端口已无监听；保留“Mac虚拟机”实例（当时 PID 11472，端口 `63616`）。未删除身份、配置、聊天记录、接收文件或旧程序目录；发现缓存可能在 TTL 到期后移除旧条目。
- 此次按用户要求整理文档并提交源码；本机网络调整和进程清理只是环境操作，不会被提交或随安装自动执行。当时的本地交付目录 `download/`（现 `Release/Windows/<版本号>/`）的生成内容继续忽略，未生成新安装包。

## 2026-10-24 刷新反馈与重复测试实例排查（未打包）

- 手动刷新附近设备时，按钮改为进度环并禁止重复点击，设备区显示“正在查找设备…”。重建发现服务后等待 2 秒接收首批异步响应，再显示“已刷新 · 时间”或“暂未发现设备 · 时间”；后台周期发现继续运行，设备数量仍随响应更新。失败显示“刷新未完成”及原有错误/连接帮助，未就绪时禁用刷新；保留现有 TCP 会话与传输。
- 截图底部三个条目均来自同一台 Windows 虚拟机 `192.168.50.121`。本次排查时，`63616` 为用户已改名“Mac虚拟机”的 `C:\Tools\FeigeUIReview` 实例；`51842` 为 `C:\Tools\FeigeV3` 的旧 AppX 开发实例；`64813` 为 `FeigeInstallerTest-…\app` 中的安装验收实例。后两份仍使用系统默认设备名 `SONGLI-M5MAX-WI`。它们有独立身份和监听端口，不能仅按同一 IP 合并为一个已认证设备；本轮只说明来源，未关闭运行中的实例。
- 在独立 `C:\Tools\FeigeRefreshVerify20261024` 构建副本中，Windows ARM64 / x64 应用均构建通过，**0 警告、0 错误**，包含进度环与状态行的 XAML 编译。此轮只修改应用层反馈，核心 405 项回归结果沿用上一轮，未新增低影响 UI 状态测试；原生点击/动画与窄窗口展示仍待界面复验。未替换运行版本，未生成安装包。

## 2026-10-24 安装反馈更正：管理员方式可完成安装

- 用户补充实测：此前出现 Error 4551 的另一台 Windows 电脑，以“管理员身份运行”现有安装器后安装成功。该事实修正了此前对必须取得可信签名才能处理本次安装的过度判断；本次无需先为安装购买签名证书。
- 仅确认用户报告的安装成功，未新增应用启动、双机收发、升级/卸载或策略根因验收。尚无目标电脑日志，不能据此确认是目录权限、具体应用策略或其他执行环境差异，也不把管理员方式推广为所有应用控制问题的通用解决方法。
- 同步 Windows 使用说明、1.2.0 交付说明及随包说明，优先呈现此次实测结果；签名链路改进保留为后续发行准备。未修改安装器默认权限、未关闭系统保护、未重新打包。本轮仅整理文档，检查差异格式，不重复执行代码测试；其他会话改名等在途修改保持原样。

## 2026-10-24 设备改名同步到已有会话（未打包）

- 用户将 Mac 中的 Windows 虚拟机从“界面验证设备”改为“Mac虚拟机”，另一台 Windows 的已连接会话列表/聊天标题仍显示旧名。实测 mDNS TXT 已广播 `name=Mac虚拟机`，故障在接收端已有会话仍使用连接时缓存的名称；刷新附近设备没有同步会话名称，聊天标题也未响应名称属性变化。
- 将新发现的名称应用到已有会话，并让当前聊天标题、头像和输入框提示同步刷新；在发现设备之后才创建的会话也读取当前名称。v3 实例名按既有指纹前缀与完整服务名匹配，显示名限制 128 UTF-8 字节且拒绝空白/控制字符。广播名称仅作显示提示，不作为认证依据，不替换完整设备指纹、已建立连接的握手资料或历史消息名称；不创建、恢复隐藏会话，也不主动重连。离线期间保留本次运行中最后显示的名称，重新启动后再次发现时更新。
- 修复另一个改名路径：监听原先在等待 TCP 连接前便捕获本机名称；现于 TCP 接入后读取最新设备资料，避免等待期间改名后，下一次入站握手仍发送旧名。保持 v3 线协议及原有一次连接同意流程。
- 新增 13 项回归检查：TXT 单独改名、重复广播、goodbye/刷新后重新发现、先发现后创建会话、错误服务/域名/设备、非法显示名，以及真实 TCP 等待期间改名后握手的新名称和同一指纹。macOS（SDK/运行时 11 预览版，net10 测试向前兼容运行）与 Windows ARM64（SDK 10.0.402）核心测试均 **405/405** 通过；隔离 Windows 构建副本中 ARM64 / x64 应用构建均 **0 警告、0 错误**。
- 未替换当前运行的测试应用或用户资料，未生成安装包。原生窗口未复验（电脑控制权限不可用；随后额外的模型探针也未能建立 Parallels 来宾会话，未计入通过项）；ROG 的实际显示同步需安装修复版后双机确认。

## 2026-10-24 安装 Error 4551：签名链路补齐（未重新打包）

- 用户提供另一台 Windows 电脑的安装报错截图：安装器无法在临时目录执行文件，Error 4551“应用程序控制策略已阻止此文件”。当前 1.2.0 交付为未签名包；具体由智能应用控制或哪条单位策略阻止，仍需目标电脑的 CodeIntegrity/Operational 等日志确认。当前 Parallels 虚拟机的智能应用控制状态为 0，近期无对应 3077/3033 拦截记录，不能作为目标电脑策略验收。
- 发现旧 `-CertThumbprint` 分支只签主 EXE 与最终安装器外壳，遗漏 Inno 的临时运行副本/卸载器，也未覆盖应用 DLL。修正为编译时传入命名 `SignTool`、启用 `SignedUninstaller`，使 Inno 为临时引擎和卸载器签名；自包含目录的未签名 EXE/DLL 逐项签名并 `/pa` 验证，保留且校验已有厂商签名，失败中止。SDK 签名工具暂存到构建目录，避免 32 位编译器访问 SYSTEM 的 System32 缓存时遭文件系统重定向。无证书的构建输出明确提示 4551 风险。
- 隔离 Inno 实编译 **9/9** 检查通过：未签名分支、签名分支、内部临时引擎与外壳签名、签名回调非零退出时中止，以及 Windows PowerShell 脚本解析。测试使用临时自签证书，覆盖含空格/美元符号路径，捕获实际签名的 PE 文件验证所选证书；证书和私钥在 finally 中删除，未导入信任存储、修改策略或安装应用。这些检查不等同于可信发行签名、全应用依赖签名或目标电脑的智能应用控制验收。
- 用户确认暂时没有可信代码签名证书。已更新 Windows 使用说明、1.2.0 交付说明与随包说明，说明目标电脑日志查看、可信提供者签名及管理员批准的处理路径；不建议关闭保护，不声称自签 MSIX、ZIP 或重新生成未签名包可以解决拦截。现有安装包及 SHA-256 保留原样，未生成新的交付包。
- 依据：[Inno SignedUninstaller（临时副本也签名）](https://jrsoftware.org/is6help/topic_setup_signeduninstaller.htm)、[微软智能应用控制签名要求](https://learn.microsoft.com/en-us/windows/apps/develop/smart-app-control/code-signing-for-smart-app-control)、[应用控制拦截事件](https://learn.microsoft.com/en-us/windows/security/application-security/application-control/app-control-for-business/operations/event-id-explanations)。

## 2026-10-24 Windows 1.2.0 文档整理、双架构交付与提交

- 用户明确要求整理近期文档、打包并提交 Git。汇总自 1.1.0 以来的窗口缩放、v1 图标、三栏布局、设置入口、附件气泡/预览、会话管理、首次引导与网络韧性/失败排查，新增 [Windows 1.2.0 交付说明](ReleaseNotes-1.2.0.md)，同步根 README、Windows 使用说明、文档索引、项目状态及开发手册；此前标注“未打包”的条目保留为当时记录。
- 主程序版本更新为 1.2.0、MSIX 清单为 1.2.0.0。本轮使用干净的 Windows 构建副本，核心 Release 测试 **392/392** 通过；ARM64 / x64 自包含发布及 Inno EXE 编译成功，生成当时的 `download/FeigeSetup-1.2.0-{arm64,x64}.exe`（现对应 `Release/Windows/1.2.0/`）、各自 JSON、合并 SHA-256 与随包说明。旧包保留，交付产物不提交 Git；本轮不生成 macOS 或 MSIX 包。
- 打包脚本增加 `SetupPage.xbf` 和 `AttachmentPreviewDialog.xbf` 必需项检查；安装验收脚本通过测试进程的 UI 自动化完成两页首次引导，再判断监听与身份就绪，避免新用户停在欢迎页导致旧验收脚本超时。该辅助流程在隔离原生窗口中验证通过，未操作正式资料。
- 交付检查 **32/32** 通过：应用 PE 架构、应用与托管文件版本、已确认 v1 图标、引导/预览 XAML 和 PRI、自包含运行库、安装器版本、JSON 大小/SHA-256/签名记录、脚本语法。复制回共享仓库后再次核验两包 SHA-256；没有代码签名证书，本轮交付明确为未签名测试安装包。
- 未覆盖运行中的应用或用户资料，未执行最终 1.2.0 的干净账户安装/重装/卸载。此前各轮原生界面检查与双系统核心回归见下文；实际双机、Windows 10、Intel/AMD、真实 TUN/透明代理/双层路由、中文输入法/拖放完整矩阵及可信签名发行仍待验收。

## 2026-10-24 连接失败时的 VPN/TUN/透明代理提示（未打包）

- 发现列表和手动连接共用连接失败处理：保留原始错误，遇到超时、不可达、被拒绝/重置等可排查网络失败时追加条件提示，明确“若正在使用 VPN、TUN 或透明代理”，以及“仅凭此次失败无法确认是否由代理造成”。不通过网卡名称猜测安装了什么软件，不自动更改任何系统配置。
- 失败栏原有动作按状态切换为“连接排查”，打开原生专用帮助：检查局域网直连/绕过规则、真实局域网地址及当前端口、应用防火墙许可和路由/NAT 隔离。保留设置/普通使用帮助入口；没有增加自动弹窗或第二次连接审批。开始新连接或其他状态提示时清除旧建议。
- `NetworkFailureAdvice` 对错误分类，主动取消、对方拒绝、输入格式错误、身份/协议校验失败、本机存储失败不附加代理建议，避免误归因。新增 18 项分类检查，核心测试总数 392。
- 验证与范围：macOS 与 Windows 核心测试均 392/392 通过，Windows ARM64 / x64 源码构建均为 0 警告、0 错误，原生界面检查 10/10 通过，覆盖真实本机 TCP 拒绝连接、接受 TCP 但不响应产生的握手超时、主动取消、坏协议与浅深色/窄窗口展示。真实 TUN/透明代理软件及双机矩阵仍待实际网络验收，未打包。


## 2026-10-24 代理、多网卡与多层路由排查（未打包）

- 代码检查确认：v3 收发为直接 TCP 与本机 UDP mDNS，没有 HTTP/PAC/SOCKS 代理调用。HTTP 代理环境与 VPN/TUN/透明代理分开处理；后者仍受系统路由和过滤影响。mDNS 具有本地链路范围，不能把多层 NAT/子网隔离归因于客户端代理设置，也不能保证仅输入 IP 即可穿越路由器。
- 修复发现只保留第一个地址的问题：每个实例最多提供 16 个当前有效的局域网候选，优先已公布且实际发来发现包的地址、再本机同前缀地址，保留其他候选。已显示设备的地址、端口和名称可更新，处理地址 TTL 0/超时、SRV 撤回和主机变化。UI 回到分发线程后读取最新快照，避免 IPv4/IPv6 回调乱序恢复旧地址或删除已更新设备。
- TCP 候选按 200 ms 间隔竞争建连，共用 10 秒预算；关闭所有未胜出套接字后只启动一次真实 v3 身份握手与接收方同意。拒绝、无效身份和坏协议不触发其他地址的再次审批。公网/域名、协议版本及认证规则未放宽；跨子网的 RFC1918 私网数字 IP 仍可手动连接，不强制同一子网，也不保证实际路由可达。
- 修复 IPv6 链路本地范围：UDP 接收包信息提供接收网卡，IPv4 入站网卡映射到相应 IPv6 网卡编号，不能把发送方编号用于本机连接。IPv6 多播目标显式附加发出接口。发现对两个地址族独立启动，IPv4 网卡属性异常不会拖垮其他网卡；传输启动串行化，先准备套接字再开启接收，避免并发发送创建重复实例。默认 TCP 监听在系统不支持 IPv6 时使用 IPv4。
- 地址变化防抖后重建发现服务；手动刷新和更名也重新获取当前接口/地址。刷新保留监听端口、身份和仍有效的 TCP 会话，不中断收发或重新询问同意；底层连接已断开时仍按正式流程重连。稍后模式和退出不会被网络变化回调重新启动。自动发现故障时保留实际可用的 TCP 监听和手动直连，主界面给出单独警告，不伪装为已发现设备。
- 设置/帮助增加普通代理与 TUN 的区别、AP/桥接、不同子网、双层 NAT、下级网络主动连接上级及隔离限制说明；手动连接使用当前端口，IPv6 范围须以发起端网卡为准。细化连接拒绝、无路由、系统阻止及地址变化提示，修正传输记录页“不需端口许可”的含混说明。未修改操作系统代理、防火墙、网络类型、VPN 或路由表，未增加公网转发、扫描或自动端口映射。
- 新增网络/代理测试 **24 项**：候选排序/更新/撤回/过期、范围/地址策略、真实 TCP 失败回退与文字回执、取消清理、拒绝/坏协议不重试，以及 HTTP/SOCKS 环境变量下直连与假代理未收到请求。旧发现测试适配“新增或更新”事件语义，断言单一实例及最终完整元数据，事件记录使用线程安全队列。macOS ARM64 / Windows ARM64 .NET 10 核心测试均 **374/374** 通过；正式源码 ARM64 / x64 非增量构建通过，0 警告、0 错误。
- Windows 11 ARM64 隔离原生窗口网络专项 **16/16** 检查通过：注入 UDP 发现失败仍能进入主界面并真实连接、继承测试代理变量后真实文字回执、发现失败/恢复刷新不丢连接、模拟地址变化回调保持端口/身份/回执、文字/文件/原图收发、帮助说明、稍后模式不自动启动。该故障及回调钩子仅在独立测试副本中，不进入正式源码；没有改变真实网卡或代理配置。首次引导回归 **52/52** 通过，浅深色截图复核。
- 真实双路由器/双 NAT、特定代理软件的 TUN/透明代理、重叠网段、受管网络隔离、真实 Wi-Fi 断切及 Windows 10 / Intel/AMD 仍待验收。本机 TCP 与受控回调/故障注入不能替代实际路由环境。仍未打包，未修改 download 中既有交付物。
- 依据：[mDNS 的链路范围与多网卡要求（RFC 6762）](https://datatracker.ietf.org/doc/html/rfc6762)、[传统 NAT 的行为与限制（RFC 3022）](https://www.rfc-editor.org/info/rfc3022/)、[.NET 接收 UDP 包信息](https://learn.microsoft.com/en-us/dotnet/api/system.net.sockets.socket.receivemessagefromasync?view=net-10.0)。


## 2026-10-24 首次安装引导（未打包）

- 先阅读 main 工作区中的《首次安装引导方案》1.0 和《应用更新规则》，再按“两页引导 + 主界面首次提示”接入；Windows 分支不复制公共文档形成第二个权威版本。首次仅加载必要配置，欢迎页不生成身份、不监听/广播、不初始化通知或请求更新。
- 第一页提供设备名、接收位置、自动检查更新偏好；新用户通过系统下载目录 API 取得“下载/飞鸽快传”，已有用户原目录保留。名称校验 48 个 UTF-16 代码单元、128 UTF-8 字节及控制字符；系统默认名按文本元素截断。选择器取消保留原值，目录检查用独立临时探针，配置与进度一起提交后才进入第二页。失败保留输入，不标记完成。
- 第二页分别显示身份签名校验、目录检查和实际网络启动状态，提供返回、重试、连接设置指引、稍后设置。准备由启动信号量串行化，使用视图取消令牌与独立网络生命周期；返回和稍后先等待本轮停止，晚到身份结果不启动服务。未结束首次引导前不处理连接批准；已有身份复用，不因读取/解密错误重建。
- 引导的版本、步骤、结束/稍后与各首次提示分别持久化，不缓存权限成功。有效旧配置或身份/历史痕迹视为已有用户并提示核对设置，明确未完成的新状态优先。未知/损坏设置保留原件并提供修复/重读入口；旧记录损坏/未知版本不冒充空资料继续写入。EXE 沿用原子 settings.json，MSIX 新设置使用单个复合值提交并兼容原有独立键，不跨渠道扫描资料。
- 本机记录读取与身份/网络分开，稍后模式及身份异常仍可查看有效历史；尚未就绪禁用新连接与收发。设置“使用帮助”只展示复用说明，不重置历史或在活动收发中再次初始化。首次引导关闭退出；进入主界面后关闭保持后台，已启用通知时可显示一次后台提示，否则重开窗口后显示一次可关闭说明。
- 首次连接后显示可关闭发送提示；只在真实对端回执或完整接收落盘后显示首次成功/可选通知邀请。通知偏好保存后才注册，拒绝或跳过不影响收发且不重复邀请。在本条目对应的 1.2.0 交付实现中，更新服务尚未接入：只保存自动检查偏好，不发起更新网络请求、下载或安装。后续交付后源码和 Mac mini 接入现状见[Windows 更新接入文档](UpdateService.md)。
- 验证：新增引导/兼容/校验/保存测试 **27 项**，macOS ARM64 与 Windows ARM64 .NET 10 核心测试均 **350/350** 通过；正式 WinUI 源码 ARM64 / x64 非增量构建通过，0 警告、0 错误。Windows 11 ARM64 的隔离原生 UI **52/52** 项检查通过，覆盖两页、重启与关闭、保存失败重试、旧资料、损坏/未知资料、身份失败、稍后恢复、浅深色和窄窗口。测试专用慢身份钩子验证准备中关闭/稍后不会被晚到结果重启服务，该钩子不在正式源码中。
- 真实本机 TCP 回环验证正式的一次连接批准、UI 发文字及真实回执、接收文字/文件/原图、通知邀请跳过后不重现、稍后模式重启仍载入会话且无监听。隔离实例使用独立标识、资料目录与仅用于测试的本机对端，不向其他设备发送消息。检查通过后复核截图并修复“成功后残留初始错误提示”，不以自动断言代替视觉检查。
- 未执行新版交付包安装、MSIX 运行、Windows 10、Intel/AMD 实机 UI、真实防火墙拒绝/受管策略、真实磁盘耗尽、系统大字体/读屏/输入法以及实体跨设备首次收发矩阵，不能把回环验证标为双机权限验收。未生成交付包或修改既有 download 内容。
- 系统参考：[Windows 已知文件夹 API](https://learn.microsoft.com/windows/win32/shell/known-folders)、[允许应用通过防火墙的说明](https://support.microsoft.com/en-gb/windows/security/firewall/risks-of-allowing-apps-through-windows-firewall)。


## 2026-10-24 附件预览与会话右键管理（未打包）

- 已完成附件增加“预览”，图片缩略图可点击打开；按文件格式识别，普通文件入口发送的图片也能预览。原生窗口支持图片缩放、代码行号/基础着色/换行/复制、文本与 Markdown 源码、PDF 翻页及缩放、DOCX 正文文本。DOCX 明确说明不保留图片、图表和完整排版，旧版 Office 等格式继续外部打开。格式及大小限制见 Windows README。
- 读取全部在本机进行，不执行源码或文档脚本。文本支持 UTF-8、UTF-16 BOM 和常见中文编码，限制读取字节、字符和行数；DOCX 限制压缩包及正文大小、禁止 DTD/外部实体，图片按最长边限制解码尺寸。关闭预览时取消文本读取，图片/PDF 异步结果检查窗口关闭状态。
- 会话列表右键支持打开、隐藏、删除记录，活动连接可先右键断开。隐藏状态持久化，设置可逐项或全部恢复；新增收到的消息、重新连接会恢复显示，已有消息更新或回执不触发恢复。删除需确认，仅删除指定认证指纹的本机消息和隐藏状态，保留磁盘文件及独立传输记录；活动连接/未结束传输不可删除。
- `conversations.json` 仍为 version=1，新增可选 `hiddenFingerprints`，旧记录可直接读取。消息与隐藏状态一起原子提交，失败不改变内存快照。删除后的 UI 更新保留删除后新写入的消息；删除空会话先移除行再清空，避免短暂重新排序。行内操作从实际列表项容器取对象，排除已移除项目，并关闭相关列表的行过渡动画，避免连续操作命中旧项目。
- 验证：macOS ARM64 与 Windows ARM64 的 .NET 10 核心测试均 **323/323** 通过（新增预览 21 项、会话管理 12 项）。WinUI ARM64 / x64 正式源码均全量重编译成功，0 警告、0 错误；验证使用显式非增量构建，避免共享目录时间戳导致缓存误判。Windows 11 ARM64、250% 显示缩放下，浅色与深色各 **39/39** 项界面检查通过：预览 22 项、会话管理 17 项，包含重启持久化、恢复单项/全部、取消及确认删除、文件保留、删除选中项、全部隐藏空态、窄窗口、PDF 翻页及异常文件。实际主界面、菜单、设置、空态、代码/PDF/DOCX 预览截图已检查。
- 本轮未执行新安装包安装或完整双机实时收发矩阵，Intel/AMD 实机 UI 仍待验证。预览使用独立单实例标识与资料目录，示例和测试脚本在忽略目录；未改动真实用户记录或既有交付包，结束后清理本轮预览进程及任务。仍按用户要求不打包。
- API 参考：[WinUI 富文本](https://learn.microsoft.com/en-us/windows/apps/develop/ui/controls/rich-text-block)、[Windows PDF 页面渲染](https://learn.microsoft.com/en-us/uwp/api/windows.data.pdf.pdfpage.rendertostreamasync?view=winrt-26100)。

## 2026-10-24 传输完成气泡优化（未打包）

- 附件气泡改为紧凑文件卡片：文件/图片图标、最多两行的文件名、格式和大小集中展示；完整名称可悬停查看。底部分隔后放置完成勾选、“已发送 / 已接收”和轻量打开/显示位置入口，移除附件气泡外重复的状态文字。文字消息的气泡和送达提示保留。
- 仅收到成功回执或完成接收的附件显示完成标记与打开入口；传输中保留进度，失败显示原因与重试。补齐状态更新时完成标记、按钮、元信息等绑定通知。完成色适配浅色、深色及高对比度资源。
- 图片保留卡片内预览与打开图片入口。修复延迟创建图片时数据上下文尚未就绪而漏载的问题，在上下文更新后重新加载；读取本机图片流并限制解码宽度 640 像素，异步结果检查绑定对象，文件缺失时保留附件信息及操作入口。
- 验证：正式源码 ARM64 / x64 构建成功，0 警告、0 错误。Windows 11 ARM64、250% 显示缩放下，浅色、深色各 **12/12** 项针对性检查通过，覆盖收发完成状态、文件类型/大小、完成标记与操作入口、长文件名两行限制、图片预览和打开图片、失败原因/重试且不显示完成标记、窄窗口按钮边界；实际主界面、图片、失败及窄窗口截图已检查。本次未重新执行双机实时传输矩阵。
- 示例记录、文件与预览配置均在临时隔离目录，未进入正式源码或用户资料。本轮未打包，未改动既有交付包；验证后清理本轮预览进程及任务。

## 2026-10-24 退出入口移入设置（未打包）

- 根据用户要求，移除左侧“退出应用”，仅保留消息、传输记录和设置。在设置页底部增加“应用运行”卡片，包含后台运行说明及带图标的退出按钮；移除本机连接卡片中旧的左侧退出提示。
- 退出流程集中到主窗口 `ExitAsync`，沿用停止会话、关闭通知、关闭窗口及结束应用的顺序；重复点击不会重复执行退出。关闭窗口隐藏到后台的行为保留。
- 验证：ARM64 / x64 正式源码构建均成功，0 警告、0 错误。Windows 11 ARM64 独立预览中，浅色、深色各 **6/6** 项针对性检查通过：侧栏无退出入口、设置连接信息可访问、页面底部退出按钮可见、关闭窗口保留后台进程、再次启动恢复窗口、点击设置退出后进程结束。实际浅色和深色设置页截图已检查。
- 本轮仅临时构建及隔离资料验证，未生成安装包，也未退出用户原有应用。

## 2026-10-24 导航对齐与本机信息位置调整（未打包）

- 根据用户反馈，移除自定义导航项额外的左右边距，使消息、传输记录、退出应用与系统设置项的图标、文字起点一致；浅色和深色实际截图均已核对。
- 移除会话栏底部本机设备信息及浮层，在设置页增加“本机连接信息”，显示就绪状态、本机地址、端口、手动连接说明及后台运行说明。页面打开时跟随会话状态刷新连接信息，不覆盖用户正在编辑的设备名称。
- 验证：正式源码 ARM64 / x64 构建均成功，0 警告、0 错误。Windows 11 ARM64、250% 显示缩放下，浅色与深色各 **27/27** 项界面检查通过，包含导航左边界、会话栏移除本机信息、设置连接状态/地址/端口、设备名称保存以及原有宽窄窗口交互；实际主界面与设置页截图已检查。导航子文本未单独暴露给 UI 自动化，文字及图标起点通过实际截图核对。
- 继续遵循先确认、后打包的流程；本轮仅临时构建和独立资料预览验证，未生成或修改交付安装包。验证结束后关闭本轮预览并移除临时验证任务，保留用户原有应用。

## 2026-10-24 Windows 三栏会话界面优化（未打包）

- 根据用户参考图，调整为浅灰导航、独立会话列表和聊天面板。会话采用首字头像、两行摘要与活动时间，按最近消息排序；移动已有会话对象并保留选择和草稿。按用户后续要求，不添加搜索功能或搜索框。
- 聊天区独立显示设备和连接状态，区分收发气泡，保留附件进度、图片预览、打开文件、显示位置与重试入口。合并输入框、图片/文件工具和发送按钮；日常就绪信息收进本机设备面板，连接错误仍显示提示。
- 导航在较窄窗口收成图标栏，会话页宽度不足 700 个逻辑像素时采用列表/聊天切换；返回列表后点击同一选中项仍可打开聊天。启动尺寸按 DPI 换算，并限制在屏幕工作区内，保留前轮 10 个逻辑像素的缩放感应区。
- 设置和传输记录统一卡片、字号和留白。滚动内容显式绑定视口宽度，修复高 DPI 下限宽内容被居中挤出视口的问题；设备名称保存按钮保持完整可见。原生标题栏按钮随浅/深色更新，使用 Windows App SDK 的 `ThemeSettings` 处理对比度设置，避免依赖 UWP 窗口事件。
- 验证：正式源码 ARM64 与 x64 均构建成功，0 警告、0 错误。Windows 11 ARM64、250% 显示缩放的独立预览副本中，浅色与深色各 **24/24** 项界面检查通过，覆盖最近会话选择/排序、离线禁用、消息与附件卡片、手动连接及输入校验、本机信息面板、窄窗口返回与同项重开、恢复宽窗口、记录空态、设置保存与返回后选择保留；实际截图已检查，包括深色标题栏和设置页边界。
- 预览使用独立单实例标识和资料目录；示例会话、测试脚本及截图仅在被忽略的临时目录，不进入正式应用源码。未覆盖或退出用户原有应用，未改动协议核心；本轮未重做双机收发、IME/拖放、安装升级或 Intel/AMD 实机验收。
- 遵循用户“先优化和验证、确认后再打包”的要求，没有生成或替换安装包；版本号仍为 1.1.1，当时的 `download/`（现 `Release/Windows/<版本号>/`）中现有安装包不包含本轮界面优化。

## 2026-10-23 Windows 1.1.1 加宽窗口缩放感应区域

- 用户确认主窗口可以调整大小，但原生边缘感应范围太窄。使用 Windows App SDK `InputNonClientPointerSource` 将四边扩展为 10 个逻辑像素，交叠处由系统提供四角斜向缩放；保留原生鼠标样式和系统缩放交互。
- 感应区域按 `XamlRoot.RasterizationScale` 换算为物理像素，随窗口、显示缩放和非客户区布局变化更新。标题栏控件会在布局时重设部分区域，因此合并排队到布局后更新，并比较现有区域，避免自身变更触发反复刷新。无区域时 SDK 可返回 null，读取已兼容；最大化/最小化时清除扩展区域，还原后恢复，退出时停止更新。
- Windows 11 ARM64、250% 显示缩放的独立测试副本：距客户区边缘 6 个逻辑像素处，四边和四角均返回正确的 8 种缩放命中；右下角连续实际拖动成功，单次宽度 +200、高度 +120 物理像素；最大化后禁用扩展命中，还原后 8 向命中恢复。关闭、最大化、最小化、导航按钮与标题栏移动区域命中保持正常；最大化静置 0.5 秒，采样 CPU 增量为 0。
- 版本更新为 1.1.1，MSIX 清单同步为 1.1.1.0。ARM64 与 x64 自包含应用均构建成功，EXE 安装包及 JSON 校验文件交付到当时的仓库根目录 `download/`（现 `Release/Windows/1.1.1/`），同时包含已确认的 v1 图标。构建未覆盖运行中的应用；本轮未重做安装/升级验收、Intel/AMD 实机交互或其他 DPI 的实机验证。

## 2026-10-23 飞鸽快传 v1 图标定稿与打包资源接入

- 用户确认保存白鸽、纸张折面与蓝青渐变底的 v1 图标，后续 Windows/macOS 打包统一使用。母图、ICO、ICNS、生成提示词及校验信息保存到 `branding/feige-icon-v1/`（现 `Project/Ui/Branding/FeigeIconV1/`），避免正式资源仅存在于被忽略的交付目录。
- 替换 Windows `Assets/AppIcon.ico`，现有程序、窗口与 EXE 安装器引用自动生效；同步替换全部 8 项 MSIX PNG，宽磁贴和启动画面使用透明画布居中等比布局。
- 根 `AGENTS.md` 新增图标规则；图标规范记录资源映射及 macOS 签名前复制 ICNS、设置 plist 的接入方式。记录当时 macOS 仍在独立分支，未在那一轮改动；后续统一仓库迁移已将规则和正式资源纳入共同主线，并接入 macOS 打包脚本及本地交付目录输出。
- 本次未重新构建 Windows/macOS 应用或安装包；已有交付包保持原版本。格式与资源检查不替代实际安装、任务栏、Dock 验收。

## 2026-10-23 项目目录、交付产物与文档整理

- 根目录新增 `AGENTS.md`，规定 macOS、Windows 所有交付包及随包内容统一使用小写 `download/`，允许按平台、版本和架构分子目录；编译缓存与临时构建文件保持独立。该规则现由 `Release/<平台>/<版本号>/` 取代。
- 已将 macOS 0.1.0、0.2.0、0.3.0、0.3.1、0.3.2、0.3.3 压缩包、macOS 应用，以及 Windows 1.1.0 的 ARM64/x64 EXE 和各自 JSON 校验信息迁入当时的根目录 `download/`（现对应 `Release/MacOS/<版本号>/`、`Release/Windows/<版本号>/`），共 11 项。迁移前后逐文件校验 SHA-256 一致；清理迁移后的空旧产物目录，保留构建缓存。
- EXE 打包脚本改为输出到当时的根目录 `download/`（现为 `Release/Windows/<版本号>/`）；MSIX 打包及默认侧载验收目录当时统一为 `download/msix/`，现同样放在 `Release/Windows/<版本号>/`。路径根据脚本所在位置计算，不依赖执行时的工作目录。产物、构建缓存及本地签名材料不提交 Git。
- 根 README 补充目录职责，文档索引按使用场景组织；Windows README 优先展示当前 1.1.0/v3 状态，将旧 v1 测试与 MSIX 结果明确标为历史基线，并校正项目计划和打包路径。
- 本轮验证：macOS ARM64，使用既有独立 .NET SDK 10.0.402 / Runtime 10.0.13，核心测试 **290 通过、0 失败、0 跳过**；Node 协议向量全部通过；本地 Markdown 文件链接和差异格式检查通过。系统默认的 .NET 11 预览环境缺少 .NET 10 Runtime，不能直接运行本项目测试，须选择 .NET 10 环境。
- 本轮未重新执行 Windows UI 构建、EXE/MSIX 打包或安装验收；先前打包与安装验证的范围及待办保持如下记录。

## 2026-10-23 Mac 互通用户确认，新增自包含 EXE 安装流程

- 用户启用开发者模式并重启后开发版启动成功，明确反馈 Windows↔Mac 能够互通。此前 AppX 部署队列阻塞为历史状态；用户反馈未替代完整双机、UI 与网络异常矩阵验收。
- 增加 `FeigeInstallerBuild=true` 独立发布模式，捆绑 .NET 10 与 Windows App SDK，保留现有 MSIX 模式。补齐应用 PRI、根及 Pages 下 XBF、图标等资源；首轮发布缺少 XAML 资源导致 0xC000027B，修正后从非安装目录启动正常。脚本对全部应用 XAML 资源执行缺失检查。
- EXE 版使用 `%LOCALAPPDATA%\Songli\FeigeTransfer`，名称和接收目录经验证并原子保存；DPAPI 身份生成也采用临时文件和原子提交。EXE 与 MSIX 资料独立，不自动迁移旧历史；EXE 重装和卸载保留资料。新增 5 项设置存储回归，macOS ARM64 和 Windows ARM64 **均 290 项通过、0 失败、0 跳过**。
- 新增 `installer/Feige.iss`、`scripts/Build-Installer.ps1` 与 `Test-ExeInstaller.ps1`，生成 1.1.0 x64 / ARM64 按用户安装的 EXE，开始菜单及可选桌面快捷方式，包含上游许可文本。支持通过证书指纹为主程序、安装包签名和时间戳；本轮没有正式证书，产物为未签名内测包，未修改证书信任或开发者模式。
- Windows 11 ARM64 虚拟机中：ARM64 **安装 → 从临时工作目录启动 → v3 TCP 监听 → 覆盖重装 → 身份哈希一致 → 卸载 → 程序与安装登记移除、身份保留**通过。验收未生成模拟聊天记录；已有资料文件按哈希检查，新配置下未存在的聊天/设置文件不算作真实记录升级验收。
- x64 自包含发布成功，x64 EXE 在 ARM64 Windows 兼容模式启动，日志 architecture=X64 且本进程拥有 v3 TCP 监听；**尚未在 Intel/AMD 实机完成安装验收**。保留的 MSIX 模式再次 ARM64 构建通过，0 警告、0 错误。
- 未打断用户正在运行的 MSIX 应用，仅停止本轮测试自己启动的进程；安装验收拒绝覆盖已有 EXE 安装。程序关闭会隐藏窗口，安装前需从“退出应用”结束旧程序。
- 通知：自包含 EXE 的 Windows App SDK 2.5.1 注册出现缺失 Insights.Resource.dll，符合[上游问题 #6774](https://github.com/microsoft/WindowsAppSDK/issues/6774)。本轮保留窗口内收发状态、隐藏技术异常于日志；系统通知仍待上游修复或另行适配，不能标记通过。
- 最后仅调整通知不可用的中文提示后，两个架构重新生成安装包。ARM64 最终复测首次启动与监听通过；覆盖重装期间 Parallels 的 `prl_cc.exe` 重新启动测试目录内的应用，运行库被占用导致安装退出码 5。未终止该窗口；用户明确选择“先保留窗口，稍后测试”，故最终覆盖重装/卸载复测暂缓。此前首轮完整验收已通过，不将本次中断标成成功。
- 使用和构建方法见 [Windows README](README.md#exe-安装包给普通用户)。仍待可信签名发行、实际消息/附件资料升级、Intel/AMD 实机和完整 UI/防火墙/多网卡验证。

## 2026-10-23 WinUI 接入 v3，消息存储落地，双架构构建与双系统 285 项回归通过

- `AppSession` 从旧 v1 单文件流程切换到 v3，监听/发现显式 version=3，接收方同意一次连接；连接槽防止多台同时连接，唯一读循环处理收件和回执。移除应用中的配对码、SAS 与逐文件确认入口，保留 v1 核心和回归。
- `SendPage` 接入设备会话、附近设备、消息时间线、多行输入、图片/文件选择、多批附件与文件拖放；显示进度/状态、打开文件/显示位置和失败重试。历史按认证指纹区分，同名不合并；选中离线会话禁用输入，草稿按会话保留。
- 新增严格原子 `ConversationHistory`（conversations.json）：保留最近 1000 条结束消息与全部活动项；收件文字先保存再回执，保存失败不改变内存，未知/损坏文件不覆盖。重启未完成项和断线活动项转失败，已送达保留。文字重试复用 UUID，附件重试创建新 ID。旧文件历史独立入口仍保留。
- `V3Connection.FileReceiving` 在发 decision 前让应用保存活动附件卡片，记录失败断开并清理暂存；补充两项真实 TCP 时序/失败回归。新增 11 项消息存储回归覆盖往返、同名隔离、重试、重启、裁剪、写失败、坏数据、快照和断线状态。
- 接入后台系统图片缩略图（请求 640 像素）、Enter/Shift+Enter 和 IME 组合输入标记；异常图片/文件移走保留附件信息并提示。此处为代码与编译状态，未完成输入法、预览、拖放实际操作验收。
- 新增后台身份随机挑战签名预检；成功前不开网络，失败可重试，退出后的晚到结果释放。设置名称/目录先持久化再更新对象，名称限制 1–48 字符、128 UTF-8 字节且无控制字符，目录先测试写入。
- 实现 AppInstance 单实例重定向、关闭窗口隐藏、再次启动恢复/还原、侧栏退出时取消网络。没有托盘入口；窗口流程待运行验证，不能从编译成功推断通过。
- Windows 回归发现双方取消时的竞态：一端取消关闭 socket 后，另一端偶尔先报 EOF。EstablishAsync 现以调用者取消令牌为准，归一化为 OperationCanceledException；内部时间预算取消映射 TimeoutException。原取消测试断言保留，修正后双系统通过。
- 依赖固定为 Windows App SDK 2.5.1 / SDK BuildTools 10.0.28000.2705 / WinApp 0.7.1，替代通配符。
- 最终验证：.NET SDK 10.0.402 / Runtime 10.0.13，macOS ARM64 与 Windows ARM64 **均 285 通过、0 失败、0 跳过**（原 189 + 新增 96，本轮新增 13）；Windows ARM64 虚拟机上 **ARM64、x64 WinUI 构建均 0 警告、0 错误**。x64 仅完成编译，未运行 x64 应用。
- 运行验收阻塞：当前虚拟机 AppX Add/Register 部署排队后停滞，日志只有开始事件 603，AppXSvc 正在运行且不可停止。框架依赖版和单次 SDK 自包含构建均通过，但开发注册未成功；部署客户端已取消，未强制中断系统服务或重启虚拟机。电脑控制工具未获授权，未做图形界面验收。
- 下一步先恢复测试虚拟机 AppX 部署，再验证启动/窗口重开、UI 文字与双向附件、IME/拖放/图片/主题/高 DPI，以及 Mac↔Win 双机和签名 MSIX 安装升级。本轮未产出已验收的新版发布包。

## 2026-10-23 v3 会话核心首轮完成，272 项核心测试通过

- 在已有未接入应用的 v3 草稿上完善 `Conversations/V3Wire`、`V3Connection`，新增 `V3TextInbox` 与 `Net/V3PeerConnector` / `V3PeerListener`。旧 WinUI 应用仍使用 v1，本轮未改为广播 v3。
- 公共夹具取自 `Transfer/main` 的 `80d355f0469fa03495727cb71824213a0a2bb4d8`，原样保存于 `tests/Feige.Core.Tests/Fixtures/v3.json`。transcript、摘要、两端 RSA-PSS 签名、HKDF、双向 GCM 全部记录、UUID 网络字节序及文件摘要逐项通过。
- 同意由接收方执行一次，发起方只等待连接决定；增加必填 JSON 字段检查，保留类型 5/6 立即拒绝。握手和应用记录按精确帧长读取，三字节拆分回环验证无数据丢失；每方向序列号跨文件和文字持续递增，编码与网络写入在同一队列串行化。
- 真实 TCP 验证同一连接多批文件、两个方向同时传文件且交错文字、原图 kind、0 字节和 105906177 字节（101 MiB + 1）文件；SHA-256 一致，原子提交后才发 receipt，重名不覆盖。
- 文字记录完成后才发回执；记录失败或未配置处理器不报送达。去重按认证指纹和 UUID，支持同连接重试、跨连接重试与历史初始化；断线后的晚到本地记录仍纳入窗口。取消连接会唤醒待收回执，未提交文件清理；连接同意回调不响应取消时也能结束等待，晚到同意不会重启连接。
- 发现通过 `version: 3` 显式启用指纹实例名及 TXT `version=3/name`，过滤旧版本和自身（同名设备不互相隐藏）；不再把发现 TXT 当认证身份。TXT 版本变更或 TTL=0 时撤下已报告的设备，PTR goodbye 行为保留。
- TCP 新入口只接受私网、环回与链路本地数字 IP，同时核对真实远端；覆盖 IPv4、IPv6 ULA、链路本地、IPv4 映射 IPv6、公网/域名/端口拒绝。一次坏握手后监听器仍可接受正常 v3 连接。
- v3 安全落盘增加 Windows 设备名/非法字符/尾随点空格校验，合法长名称保留；保留 v1 原有名称整理和大小限制。累计大小比较使用减法避免 Int64 加法溢出。
- 验证环境：macOS ARM64，临时独立安装 .NET SDK 10.0.402 / Runtime 10.0.13；`dotnet test Windows/tests/Feige.Core.Tests/Feige.Core.Tests.csproj` **272 通过，0 失败，0 跳过**，其中原 v1 189 项 + 新增 v3 83 项。非法 ID/偏移/越界/摘要、空/缺字段 finish、重复 manifest、未知回执、重复授权及 GCM 篡改均断开并清理暂存文件。
- 下一阶段：WinUI 接入一次同意、设备会话列表、文字/附件时间线和持久化；图片预览、草稿、重试与离线禁用；身份签名预检、设置/窗口生命周期。仍需 Windows 实机复跑、源文件变化/磁盘不足/权限撤销等负例、Windows↔macOS 双机和新版签名 MSIX 验收。本轮不宣称完成这些验收。

## 2026-10-23 开发目录与当前状态文档校正

- 开发目录统一使用项目根目录，直接检出 `windows` 分支；`Windows/`（现 `Project/Windows/`）为源码目录。临时 worktree 已归档，不再作为开发入口。
- 修正根 README 的“尚无 WinUI 可构建源码”和“X25519 选型未定”，补充现有 `2c37f61` v1 基线与当前 v3 升级的区别。
- 当前 macOS 0.3.3 使用 v3；Windows 需升级协议、接入会话式文字/图片/文件界面并重新验收，不再等待 Mac 实现 v1。
- v3 会话核心已开始开发，尚未接入 WinUI，也未完成 v3 测试和双机互通。189 项测试及 MSIX 侧载结论属于此前 v1 基线。
- 下方历史条目保留当时状态；新增开发以 main 的 v3 协议和 UI 与交互同步规范为准。

## 2026-10-23 系统通知与传输记录落地，顺带修掉一个「失败后静默停服」的严重 bug

- **传输记录**：`Feige.Core.Storage.TransferHistory`（`LocalFolder\history.json`，最多 100 条，新的在前）。下沉到 Core 是因为它只依赖文件系统——格式解析、坏行跳过、裁剪、原子写都能用纯 .NET 测试覆盖，应用层只提供目录。写入走临时文件 + `File.Move(overwrite)`；读取逐行校验，缺字段/负长度/未知方向的行丢弃而不是整份失败，文件损坏当空历史——历史问题绝不能阻断启动。接收页改名「接收记录」并加「清空记录」（带二次确认，只删记录不动文件），发送页底部加可折叠的「最近发送」。
- **系统通知**：用 `Windows.UI.Notifications.ToastNotificationManager.CreateToastNotifier()`，**放弃**文档推荐的 `Microsoft.Windows.AppNotifications.AppNotificationManager`——后者 `Register()` 在本机稳定抛 `0x80070490 (Element not found)`，即使清单按文档写全了 `com:ExeServer` + `windows.toastNotificationActivation`（另有两个 schema 坑：`com:Class/@Id` 与 `ToastActivatorCLSID` 都不能带花括号）。新 API 相对旧 API 多给的只有「点击通知激活应用」，而传输通知没有可激活的东西，所以换依赖而不是绕过：有包标识时旧 API 无需注册、无需 CLSID、无需清单声明。清单里那套 COM/CLSID 声明已全部撤掉。
- **修掉的严重 bug（RPC_E_WRONG_THREAD 打死接收循环）**：`Failed`/`FileReceived`/`FileSent` 原本从线程池直接 `Invoke`，页面处理器里改 `InfoBar.Severity` 抛「应用程序调用一个已为另一线程整理的接口」，而它是在接收循环自己的 catch 块里抛的 → 异常逃出循环 → `Task.Run` 的返回值没人 await → **接收服务静默死亡**。表现：第一次接收失败之后再也收不到任何文件，新连接全堆在 accept 队列里（`netstat` 可见 `CloseWait`）。全部事件改为经 `Post()` marshal；接收循环加最外层 `try/catch`（跑飞时记 `accept: loop aborted` 并提示用户）；`OperationCanceledException` 只在自己那个 token 被取消时才 `break`，单连接超时取消不该终止服务。
- **对端中断归一化**：`StreamHandshakeDuplex` 把握手阶段的 `IOException`/`SocketException` 转成 `ProtocolException(UnexpectedEndOfStream)`。之前裸 socket 异常直接冒到界面（`SocketException 10053 你的主机中的软件中止了一个已建立的连接`），既不可读也不是协议语义。新增测试用零 linger 的 RST 复现。
- **诊断日志**：`LocalFolder\app.log`（`Services/AppLog`，只留最后 256 KB）。记录通知初始化结果、监听端口、发现层是否起来、接收循环起止、每次收发失败的类型；逐连接失败只写「类型: 消息」不写堆栈，启动失败与循环跑飞才写完整堆栈。不写密钥、内容与完整路径。`Test-MsixPackage.ps1` 断言失败时会把这份日志自动打出来。
- **验收脚本两个坑**：①`Get-NetUdpEndpoint` 在端口被多进程 `SO_REUSEADDR` 共享时只报其中一个（Windows 自带 mDNS 响应器常驻 svchost 也占 5353），导致「明明绑上了却判失败」——改用 `netstat -ano -p UDP` 解析，稳定看到本进程的绑定。②`Add-AppxPackage` 的部署进度条会把每帧重绘写进重定向日志（几千行噪音），设 `$ProgressPreference = "SilentlyContinue"`。
- **实测证据**（均来自 `%LOCALAPPDATA%\Packages\FeigeTransfer.Windows_fmc2zrmw36x2p\LocalState\app.log`）：启动日志 `notifications=True`；连打三次坏连接，三次都被记录且循环存活（`UnexpectedEndOfStream`、`InvalidHandshakeFrame`、`UnexpectedEndOfStream`）；注册表 `HKCU\...\Notifications\Settings\FeigeTransfer.Windows_fmc2zrmw36x2p!App` 的 `LastNotificationAddedTime` 与最后一次坏连接时刻一致（`17:25:26`），证明通知真的投递到了通知平台；预置 3 条 `history.json` 重启后日志显示 `history=3`，证明读取链路通。
- 测试 178 → 189（历史存储 10 项 + 握手中断映射 1 项）。签名 MSIX 重新打包，侧载安装/启动/端口/卸载全链路仍为 PASS。
- 未完成：与 macOS 的双机互通验收（依赖 macOS 侧先实现同名服务与协议 v1）；ARM64 真机验证。

## 2026-10-23 签名 MSIX 打通，侧载安装 / 启动 / 卸载全链路验收通过

- 包身份定稿：`Name=FeigeTransfer.Windows`、`Publisher=CN=Songli`、`PublisherDisplayName=Songli`、显示名「飞鸽快传」、版本 `1.0.0.0`。去掉模板遗留的 `systemAIModels` 与 `systemai` 命名空间，能力保留 `privateNetworkClientServer` + `runFullTrust`。
- 新增 `scripts/Build-Msix.ps1`：`dotnet publish -p:AppxBundle=Never -p:UapAppxPackageBuildMode=SideloadOnly -p:AppxPackageSigningEnabled=false` → `signtool sign /fd SHA256`。支持 `-NewDevCert`（生成 `CN=Songli` 自签名证书并导入）、`-CertThumbprint`（指定证书）、`-TrustDevCert`（顺便导入信任存储）。**签名前强制校验证书 Subject 与清单 Publisher 一致**——不一致的典型症状是 `0x800B0109` / `0x80073CF3`，而 MSBuild 自己的报错指不到这一层。
- **关掉模板的 Release 裁剪**：`PublishTrimmed` 改为恒 `false`。WinUI 3 的 XAML 类型解析与 WinRT 投影靠反射元数据，裁剪器看不见；包能构建出来，装上启动即崩。产物（未裁剪）约 81 MB。
- 新增 `scripts/Test-MsixPackage.ps1`：导入证书 → 卸载旧包 → 干净安装 → `shell:AppsFolder\<PFN>!App` 启动 → 轮询等待「进程 + TCP 监听 + UDP 5353」三条件 → 杀进程 → 卸载 → 断言无残留。退出码 0 为通过。
- 实测验收输出（两次连跑均通过）：`PASS: package registered` / `Publisher: CN=Songli` / `Process appeared: Feige.App (PID 33492)` / `PASS: TCP listener is open (port 55837)` / `PASS: mDNS socket 5353 is bound` / `PASS: package removed` / `PASS: no process left` / `Sideload acceptance passed.`
- **自签名证书的校验坑**：`signtool verify /pa /r` 报 `Signing Cert does not chain to the specified Root Cert`——自签名证书自己就是根，但 signtool 要求根与签名证书分离校验。改为「verify 失败则回退 `Get-AuthenticodeSignature` 比对签名者指纹，一致仅告警」，最终以能否安装成功为准；这也是为什么验收脚本保留真实的 `Add-AppxPackage` 步骤而不是只做静态检查。
- **StrictMode 两个坑**：①函数里 `return @(...)` 在结果为空时返回空而**不是空数组**，调用方 `$x.Count` 直接抛 `PropertyNotFoundException`，报错位置离真因很远，改用 `return ,$items` 阻止展开；②`Add-AppxPackage` 的部署进度条会把每帧重绘写进重定向日志（几千行噪音），开头 `$ProgressPreference = "SilentlyContinue"`。
- 记一件事：`LocalMachine\TrustedPeople` 导入需要提权，脚本按是否提权自动选 `LocalMachine` / `CurrentUser` 存储。
- 未完成：系统通知、接收历史持久化、与 macOS 的双机互通验收（依赖 macOS 侧先实现同名服务与协议 v1）。

## 2026-10-23 WinUI 3 应用落地，本机跑通「发现 → 配对 → 传输」全链路

- 代码：`src/Feige.App`（TFM `net10.0-windows10.0.26100.0`）——`Pages/SendPage`（设备列表、刷新、手动 IP:端口、选文件→配对码→发送）、`Pages/ReceivePage`（大字配对码、监听端口、接收文件夹、已接收列表）、`Pages/SettingsPage`（设备名、接收文件夹、本机指纹）；`Services/AppSession`（身份/监听器/发现/接收循环，事件统一回 UI 线程）、`Services/UiPromptService`（SAS 与文件确认弹窗）、`Services/IdentityStore`（DPAPI）、`Services/AppSettings`、`Services/ErrorText`。
- **同步确认的桥接**：握手与接收循环都在线程池上推进，`approve` 是同步委托，因此可以阻塞等待 `ContentDialog`；`UiPromptService` 用 `DispatcherQueue` + `TaskCompletionSource` 取回结果，并**主动拒绝在 UI 线程上调用**（会死锁）。
- **固定「拨号方发送、监听方接收」**：协议 §1 允许任一端发送但没有协商记录，与其猜，不如在应用层定死。
- 身份用 DPAPI（`CurrentUser`）加密 PKCS#8 存在 LocalFolder；首次生成 RSA-3072 放 `Task.Run`，界面显示准备中。
- **清单能力补 `privateNetworkClientServer`**：打包应用缺这一项时局域网入站与组播会被静默拦截，表现为「能发现别人、别人连不上」。同时把显示名改成「飞鸽快传」、去掉模板里的 `systemAIModels`。
- 启动自检（实测）：`dotnet run --project src/Feige.App` 以 MSIX 调试身份启动，截图确认窗口与空状态提示正常；`netstat` 同时看到随机 TCP 端口（双栈）与 UDP 5353 绑定，说明监听器与 mDNS 都起来了；进程运行 60 秒无异常退出。
- 修掉一个模板继承的编译坑：`IdentityStore` 里把属性命名为 `Path`，遮蔽了 `System.IO.Path`，`Path.Combine` 直接编译失败。
- 测试仍为 178 项全绿（应用层为 UI 编排，协议/连接/发现逻辑都在 `Feige.Core` 内已被覆盖）。
- 未完成：系统通知、接收历史持久化、签名 MSIX（`Publisher` 待定）、与 macOS 的双机互通验收。
- 附注：跑 `dotnet test` 时 Windows 防火墙弹出「testhost」的网络访问提示，那是测试进程绑定 socket 触发的；打包应用走清单能力，不使用该规则。

## 2026-10-23 mDNS/DNS-SD 发现层落地，协议 §1 的留白有了具体定义

- 背景：Windows 没有 Bonjour，WinRT `Dnssd` 只能注册不能浏览，所以自己实现 DNS-SD，`Discovery/` 零第三方依赖。
- 代码：`DnsName`、`MdnsMessage`（PTR/SRV/TXT/A/AAAA 编解码，**解析支持压缩指针**）、`UdpMdnsTransport`（5353 双栈、逐网卡入组）、`MdnsAdvertiser`（应答 + 三连公告 + TTL=0 告别）、`PeerBrowser`（PTR→SRV→TXT→A 分段解析、TTL 过期）、`LanDiscovery` 门面。
- **服务定义定稿**：`_feige._tcp.local`；实例名=设备名（截断 63 字节）；SRV 端口来自 `PeerListener`（绑定 0 时系统分配后通告）；TXT `v=1`/`name`/`fp`（指纹去空格）；TTL PTR 4500 秒、其余 120 秒。已回写 `main` 协议 §1。
- **安全边界写进文档**：mDNS 无认证，TXT 的 `fp` 只做展示，指纹与 SAS 一律以握手为准。
- **两个坑**：①`DnsReader` 是 ref struct，跨函数必须 `ref` 传，否则读取位置被复制，表现为 additional 里重复 answer、后面 A 记录全丢（不报错，极难反推）；②`GetIPv6Properties()` 在无 IPv6 协议栈的网卡上抛 `NetworkInformationException` 而不是返回 null，必须逐网卡 catch，否则一块虚拟网卡就能让整个发现层起不来。
- 测试 169 → 178：编解码往返、手写字节的压缩指针（含自引用指针必须被拒）、畸形报文返回 null、内存介质上的 advertise→browse→goodbye（自我过滤、指纹、端口）、QU 单播应答、TTL 到期移除、真实 socket 绑定/入组/收包。
- 未完成：`Feige.App`（WinUI 3 界面）、身份持久化（DPAPI）、签名 MSIX、与 macOS 的真实双机发现互通（需 macOS 侧先实现同名服务）。

## 2026-10-23 连接装配层落地，真实 socket 上跑通双向传输

- 代码：`Net/PeerListener`（双栈监听）、`Net/PeerConnector`（发起方）、`Net/PeerSession`（已认证会话 + 一连接一次传输）、`Net/TimeoutStream`、`Net/ConnectionOptions`、`Net/LocalDevice`、`Net/PeerTarget`、`Net/PeerHandshakeRunner`。
- **一连接一次传输写进 API**：`PeerSession` 用 `Interlocked` 抢占，第二次调用直接 `InvalidOperationException`，不靠调用方自觉（§1）。
- **超时只算空闲不算总时长**：`TimeoutStream` 每次读写各给预算，长传输不会被掐；`ProtocolError.Timeout` 是新增的本地策略错误码，不在 wire 上。用户确认弹窗期间没有读写在等，所以不会被 15 秒握手超时打断。
- **配对码从「可选」改成「必填」**：原来传 null 时两端各自随机生成，必然 `PairingCodeMismatch`（写测试时踩到）。现在非 6 位数字直接 `ArgumentException`。
- **又一个 Windows 坑**：`TcpClient.ConnectAsync(endpoint, cancellationToken)` 在「连接被拒」时抛 `OperationCanceledException` 并吞掉 `SocketException`，UI 会把「端口没开」显示成「超时」。改为 `ConnectAsync(endpoint)` + `WaitAsync(timeout)` 并观察被放弃的任务，异常类型才对。本机实测 loopback 拒绝约 2 秒（SYN 重试），`ConnectTimeout` 默认 5 秒。
- 测试 154 → 169 项：真实 loopback 双向传输（发起方发/应答方发）、receipt 在落盘之后、二次传输被拒、配对码不一致两端同时报错、用户拒绝、静默对端超时后监听器仍可用（可继续接受新连接）、取消 accept、连接被拒的错误类型、`TimeoutStream` 超时映射、`LocalDevice`/`PeerTarget` 参数校验。连跑 3 轮全绿。
- 未完成：发现层（mDNS/DNS-SD）、`Feige.App`（WinUI 3 界面）、身份持久化（DPAPI）、签名 MSIX。

## 2026-10-23 传输状态机与安全落盘落地，跑通完整栈端点

- 代码：`TransferManifest`（§4.4 JSON + 文件名/大小/摘要校验 + `ForFileAsync`）、`TransferFileSink`（临时文件流式写、增量 SHA-256、校验通过才原子重命名、不覆盖）、`SafeFileName`（Windows 非法字符与保留名）、`IRecordChannel`/`StreamRecordChannel`、`TransferSender`、`TransferReceiver`、`ReceivedFile`。
- **§5.3 的 macOS 缺陷在本实现里无法表达**：`CommitAsync` 把「字节数 → 摘要 → 原子重命名」封在一次调用里，接收方拿不到最终路径就发不出 receipt；macOS 那种「先发 receipt 再重命名」在这里没有对应的 API 组合。
- **完整栈端点验证**：真实 `TcpListener` + 真实握手（RSA-3072 / PSS / X25519）+ 120000 字节 AES-GCM 传输 + 落盘摘要比对，30 秒超时保护，通过。
- **发现并修掉一个间歇性互通 bug**：握手最后一条消息与第一条记录可能落在同一次 `Read`。`HandshakeFrameReader` 之前会把多读的字节留在内部缓冲里然后随对象丢弃，导致接收端永远等不到首条记录。新增 `TakeRemainder()`，`StreamRecordChannel` 增加 `initialBytes` 参数承接；配了专门的回归测试。这类问题在真机上表现为「偶发卡住」，很难复现，所以在文档里也写明了用法。
- 负例：接收方拒绝（发送端得 `PeerRejected`、目录都不创建）、摘要不符（不留文件、不发 receipt）、chunk 超过 65408（`InvalidLength`）、累计越界（`SizeMismatch`）、manifest 之前出现记录（`UnexpectedMessage`）、源文件比 manifest 短（`SizeMismatch`）。
- 测试 111 → 154 项通过。
- 未完成：`Feige.App`（WinUI 3 界面）、发现层（mDNS/Bonjour）、传输超时与取消的 UI 反馈、身份持久化（DPAPI）、签名 MSIX。

## 2026-10-23 签名握手落地，PSS 与 OpenSSL 双向互通

- 代码：`HandshakeOffer`（§3.3 校验 + 结构相等）、`HandshakeTranscript`（§3.5 编码、§3.4 交叉校验、摘要/SAS）、`DeviceFingerprint`、`IdentityKey`（RSA-3072 + SPKI + PSS）、`HandshakeMessages`（JSON + 分帧）、`HandshakeFrameReader`、`LocalHandshake`（四消息时序 + 确认 + 派生）、`StreamHandshakeDuplex`。
- **§8.1 全对齐**：两个 offer 的 transcript 编码逐字节相同，摘要 `779855ca…`，SAS `472138`，两个 SPKI 指纹一致。期望值仍来自 `scripts/verify-protocol-vectors.js`。
- **PSS 双向互验**（这层最容易和各平台实现错位，所以两个方向都做了）：
  1. OpenSSL 3.5.7 `dgst -sha256 -sign -sigopt rsa_padding_mode:pss -sigopt rsa_pss_saltlen:32` 签出的 §8.1 transcript，`.NET` 验签通过 → 已固化为单测向量（只含公钥与签名，无私钥）。
  2. `.NET` `RSASignaturePadding.Pss` 签出的同名 transcript，OpenSSL 判 `Verified OK`。
  结论：.NET 的默认 PSS salt 长度就是 32，与 macOS `rsaSignatureMessagePSSSHA256` 一致，不需要手工调参。
- **握手时序用真实 TCP 回环验证**：两个 `LocalHandshake` 各持一端，`TcpListener` + `TcpClient`，20 秒超时保护。证明「双方先写 offer 再读」不会死锁，也验证了 `StreamHandshakeDuplex` 的分帧重组。
- 负例：配对码不一致（两端均 `PairingCodeMismatch`）、用户拒绝（`UserRejected` 且不派生密钥）、篡改 proof 签名（`SignatureFailed`）、proof 回显不同 offer（`InvalidTranscript`）、小阶临时公钥（`InvalidOffer`）、destructive 偏移截断的 SPKI（验签返回 false）。
- 测试 51 → 111 项通过。`ProtocolError` 新增握手相关错误码（`InvalidHandshakeFrame`/`InvalidOffer`/`InvalidTranscript`/`PairingCodeMismatch`/`SignatureFailed`/`FingerprintMismatch`/`UserRejected`）。
- 一个实现细节：`HandshakeOffer` 不能用 `record`，因为 record 的默认相等对 `byte[]` 只比引用；§3.7 要求回显 offer 逐字段相等，所以手写了 `Equals`/`GetHashCode`（`HashCode.AddBytes`）。
- 未完成：传输状态机与落盘、manifest 校验、`Feige.App`、发现层、身份持久化（当前 `IdentityKey` 只在内存，`ExportPkcs8` 已提供，DPAPI 封装留给 App 层）。

## 2026-10-23 AES-GCM 记录层落地，协议补 §8.3 记录序列向量

- 代码：`Crypto/RecordCipher`（单方向 AES-256-GCM 状态机，16 字节 nonce、13 字节 header 作 AAD、序列号严格递增、`Seal` 返回 `header||ct||tag` 不含长度前缀）与 `Protocol/AuthenticatedRecordStream`（`u32` 长度前缀、任意 TCP 分片重组、`finish` 后拒收、`EndOfStream` 校验）；配套 `TransferMessageType`、`OpenedRecord`、`ProtocolError` + `ProtocolException`。
- 测试：51 项通过，记录层新增 34 项。正向用 §8.2 与新增 §8.3 向量逐字节比对；负向覆盖重放、跳号、篡改 type、篡改序列号（证明序列号在 AAD 内）、篡改密文、截断 tag、`ctLen` 不一致、未定义类型、空载荷仅 `finish` 允许、65536/65537 边界、1 字节分片重组、`finish` 后多余字节、`EndOfStream` 无 `finish`。
- **协议文档新增 §8.3**（已推 `main` `77fbcb9`）：同一固定 key/prefix 下 seq 0..2 的三条完整帧（`manifest`/`chunk`/空 `finish`），并写明「按序解码得 1,3,7 且序列号 0,1,2」「改写序列号不改 tag 必须认证失败」两条验收。§8 复算项数 15 → 22，§9.5 验收条同步。
- `scripts/verify-protocol-vectors.js` 扩展同名向量，仍是**不共享两端代码**的独立 oracle；C# 单测里的期望值全部来自该脚本。
- BouncyCastle 2.7.0 的坑：`AesEngine.NewInstance()` 不存在（`CS0117`），要用 `new AesEngine()`；`GcmBlockCipher` 每次使用前必须重新 `Init`，不能跨记录复用实例。
- 本层刻意**未**做：业务层 chunk 上限 65408 校验、消息语义（`decision`/`receipt` 的取值）、超时。这些属于传输状态机，避免记录层耦合业务规则。
- 未完成：握手帧编解码与 transcript/SAS、RSA-PSS 身份与指纹、传输状态机与落盘、`Feige.App`、发现层。

## 2026-10-23 X25519 定稿 BouncyCastle，`Feige.Core` 密钥派生落地

- 决策：X25519 用 `BouncyCastle.Cryptography` 2.7.0（纯托管），不选 libsodium 原生库；理由、版本锁定与使用边界见[开发手册](DeveloperGuide.md)「加密依赖」。
- 验证：RFC 7748 A.5 两组标量乘、6.1 的 alice/bob 公钥与共享密钥全部一致；小阶对端（全零公钥）抛 `CryptographicException`；固定输入下 `shared` / `i2r` / `r2i` 与不共享代码的 Node 实现**逐字节相同**。
- **又发现一处文档错误**：§4.1 写「Nonce：12 字节」，但同句的 `prefix(8) || sequence(8, BE)` 是 **16 字节**，macOS `SecureRecordCipher.makeNonce` 与 §8.2 向量也都是 16 字节。更关键的是 **.NET `AesGcm` 只接受 12 字节 nonce**，实测抛 `ArgumentException: The specified nonce is not a valid size for this algorithm`，所以记录层同样必须走 BouncyCastle `GcmBlockCipher`（已用 §8.2 帧实测解密成功，篡改 header 被拒）。已回写 `main`（`17fd041`），wire 格式未变。
- 代码：新建 `Feige.Windows.sln`（`--format sln`，否则 .NET 10 默认产出 `.slnx`）与 `src/Feige.Core`（`net10.0`，不依赖 Windows API 以便跨机跑测试）：`Crypto/X25519KeyAgreement`、`Crypto/SessionKeySchedule`、`Crypto/SessionKeys`、`Protocol/HandshakeRole`；`tests/Feige.Core.Tests` 17 项全部通过。
- 注意：`HKDF.DeriveKey` 的 `ikm` 形参只接受 `byte[]`，传 `ReadOnlySpan<byte>` 会编译失败（CS1503），需先 `ToArray()`。
- 未完成：AES-GCM 记录流与序列号状态机、握手帧编解码、RSA-PSS 与指纹、`Feige.App`、发现层。

## 2026-10-23 协议文档复核：测试向量独立复算通过，发现 3 处需回写 main

- 用 Node 独立实现（不复用两端代码）复算[通信协议总览](../Public/ProtocolOverview.md)第 8 章全部向量：两个 offer 编码、transcript 摘要 `779855ca…`、SAS `472138`、两个 nonce 前缀、两个 SPKI 指纹、AES-GCM 记录帧解密与长度/ctLen 一致性，**15 项全部通过**；另验证篡改 header（AAD）会被拒绝。Windows 可直接拿这些向量做实现期回归。
- 逐条比对 macOS 源码（`5a315e9`）与文档，确认字段级描述准确：握手帧 `u32 BE 长度 + JSON`（上限 16 KiB）、`OfferEnvelope`/`ProofEnvelope` 结构、transcript 编码顺序、PSS-SHA256 签名输入为 transcript 原字节、HKDF salt = transcript 摘要、记录层 13 字节 header 作 AAD、`length = 13 + ctLen`、`ctLen` 含 16 字节 tag、记录长度 29..65565、chunk 上限 65408、`FileManifest` 的 100 MiB 与文件名规则，均与代码一致。
- 文档 5.3 列出的两处 macOS 实现差异经源码确认属实：`TransferSession.receive` 收满 `size` 后未消费 `finish` 记录（与 `TransferCoordinator.finish()` 自身注释「Call only after authenticated finish record」矛盾，且 `endOfStream()` 从未被调用）；receipt 在调用方执行原子重命名之前发出。**Windows 必须按文档实现（先落盘再发 receipt、消费 finish 并校验无尾随字节），不跟随 macOS 现状。**
- 发现文档需修正的两点：①§7 把 X25519 的 .NET 方案写成「libsodium 或 OpenSSL/CNG」，但**CNG 不支持 Curve25519**，且 .NET 10 的 `System.Security.Cryptography` 无 X25519 类型（微软跨平台加密文档确认；原生 `X25519DiffieHellman` 要到 .NET 11 才有，而 .NET 11 是 STS）。Windows 现实选项是 BouncyCastle.Cryptography 或 libsodium（原生 P/Invoke）。②§3.8 要求确认令牌绑定「摘要、指纹、本地角色、有效期」，而 macOS `SASApproval` 只绑定前两项，单次消费靠 `didDeriveSession` 间接实现——属本地策略差异，不影响互通。
- 复核脚本已入库：`Windows/scripts/verify-protocol-vectors.js`（现 `Project/Windows/scripts/verify-protocol-vectors.js`，只用 Node 内置 `crypto`，无第三方依赖），作为不依赖两端代码的独立 oracle，后续 C# 实现可拿它交叉验证。
- **两点已回写 `main`（`2126453`）**：§7 X25519 选型改为 `BouncyCastle.Cryptography` 2.7.0（推荐）或 libsodium，并说明 CNG 无 Curve25519、.NET 10 无内置类型、.NET 11 为 STS；§3.8 的确认令牌要求拆为「规范要求」四项链与「macOS 现状」。本次修订**未改变 wire 格式**，macOS 实现无需改动。
- Windows 侧对应补充：`开发文档/Windows开发手册.md`（现 `Project/Docs/Windows/DeveloperGuide.md`）新增「加密依赖选型（待定）」，把 X25519 定为 `Feige.Core` 的第一个待决外部依赖，并明确 RFC 7748 向量 + 与 macOS 交叉验证两道关卡。
- 未完成：X25519 依赖库未定稿（阻塞握手实现）；协议仍未冻结；Windows 无任何源码。

## 2026-10-23 Windows 开发环境就绪并通过端到端验收

- 开发机由 Windows 空环境补齐：.NET SDK 10.0.402、Visual Studio Community 2026 18.11（WinUI 应用程序开发工作负载）、WinApp CLI 0.7.1；Windows App Runtime 2.5.1 原本已存在。
- 系统设置：开启开发者模式与长路径支持，WLAN 配置文件由「公用」改为「专用」（公用网络下入站 mDNS 与传输连接会被防火墙丢弃）。
- 验证结论：`dotnet new winui` 生成的工程 TFM 为 `net10.0-windows10.0.26100.0`，`dotnet build` 0 警告 0 错误，`dotnet run` 以 MSIX 打包身份启动并创建真实顶层窗口。
- **不需要独立 Windows SDK**：C# 工程的 WinRT 投影来自 `Microsoft.Windows.SDK.NET.Ref`，`makeappx`/`signtool` 来自 NuGet `Microsoft.Windows.SDK.BuildTools`（实测 10.0.28000.2705）；只有 C++/WinRT 才需要装独立 SDK。
- 新增 `Windows/scripts/`（现 `Project/Windows/scripts/`）：`Check-DevEnv.ps1`（17 项只读体检）、`Install-DevEnv.ps1`（幂等安装）、`Test-Toolchain.ps1`（建工程→编译→MSIX 身份启动→验窗口→自动清理）。三者均在实机跑通。
- 新增《Windows 环境搭建》：版本基线、安装步骤、系统设置与回滚、本项目特有的网络问题（Windows 无 Bonjour 服务、入站端口、公用 Wi-Fi 隔离、多网卡/代理、休眠唤醒 socket 僵死、x64/ARM64）。
- 记录两个会复现的环境陷阱：①进程环境同时存在 `http_proxy` 与 `HTTP_PROXY` 会让 VS 引导器崩溃（退出码 5002）并使 `Start-Process` 抛重复键异常；②PowerShell 5.1 按 ANSI 解码无 BOM 的 UTF-8 脚本会导致 ParserError，脚本已统一转存为 UTF-8 带 BOM。
- 未完成：代码签名证书（须等 `Package.appxmanifest` 的 `Publisher` 确定）、防火墙规则、ARM64 验证、任何 Windows 源码。

## 2026-10-23 通信协议 v1 草案发布

- `main` 新增[通信协议总览](../Public/ProtocolOverview.md)：四消息签名握手、transcript/SAS、RSA-PSS、X25519/HKDF、AES-256-GCM 记录层、单文件时序、字段上限、Windows 实现对照与测试向量。
- Windows 后续实现以该文档为准：Offer/Proof JSON、SPKI 指纹、PSS salt=32、nonce 前缀 + 序列号、chunk ≤65408、finish 空载荷。
- 尚无 Windows 代码；协议未冻结，需在双端互通测试后回写结论。

## 2026-10-23 文档治理：公共文档迁至 `main`

- 删除本分支的《开发总则》，改为链接 `main` 中的权威版本，避免与 macOS 分支的协议描述继续分叉。
- 新增本日志；里程碑摘要记入 `main` 的跨平台更新日志，本日志只写 Windows 实现细节。

## 2026-10-23 拆分 Windows 工程到独立分支（该做法已废止）

- `Windows/README.md` 作为 WinUI 3 工程入口与规划；`开发文档/Windows项目.md`、`Windows开发手册.md` 描述目标架构与约束（上述路径均已迁移，现分别对应 `Project/Windows/README.md`、`Project/Docs/Windows/ProjectOverview.md`、`Project/Docs/Windows/DeveloperGuide.md`）。
- 工程需在 Windows 10/11 + Visual Studio 2022 环境创建、编译、签名和验证（**该表述已过期**，实际要求 Visual Studio 2026，见上方最新条目）。
- 未实现：solution 与项目骨架、设备发现、协议编解码、传输状态机、原生 UI、MSIX 打包与签名。
