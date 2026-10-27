# Windows 更新服务器（Mac mini 局域网）

更新至 2026-10-26。Windows EXE 客户端已配置为家庭/办公室局域网中的 `http://192.168.50.229:8080/updates`。Mac mini 上 nginx 静态服务已接入，当前尚未发布清单和安装包；MSIX 仍须通过对应发行渠道更新。完成正式签名发布和 Windows 客户端端到端安装验收前，不能称为更新发布闭环已上线。

## 当前实现与边界

客户端从 `/windows/<channel>/feige-windows-update.json` 读取签名清单，版本包位于 `/windows/<channel>/versions/<tag>/<file>.exe`，说明位于 `/windows/<channel>/versions/<tag>/notes`。当前 stable URL 是 `http://192.168.50.229:8080/updates/windows/stable/...`。真实清单 URL 由客户端用 base directory URI 追加 `windows/<channel>/...`，发布记录/发布脚本也必须保留 `/updates` base path。

LAN 地址使用明文 HTTP，只有在应用配置显式启用 `allowInsecurePrivateNetwork` 且主机是受信任的私有 IPv4 时才可接受；Host 固定为 `192.168.50.229`，HTTP 端口固定 `8080`，公共/非私网地址、其他 Host/端口、HTTPS/HTTP scheme 变更、查询串及不在更新渠道路径内的地址都会被拒绝。该例外仅适合信任的本地网络；不适用于公共 Wi-Fi、未知设备可接入的网络或互联网分发。外网设备不能访问此更新源。

仍保留全部内容级信任校验：RSA-3072 公钥验签 RSA-PSS/SHA-256，清单严格 JSON、平台/渠道/架构/build 兼容检查，下载大小及 SHA-256，Windows Authenticode 发布证书 SHA-256 pin。服务端只提供静态 GET/HEAD，不提供 HTTP 上传。网络可观察/拦截流量，因此此 LAN HTTP 例外需要服务端与发布设备可信；不降低清单和安装包验证。

## Mac mini 准备

Mac mini 上已执行 `Project/Windows/scripts/Setup-LanUpdateServer.sh`：创建 `/usr/local/var/www/feige-updates/windows/stable/versions`，在现有 nginx `server` 中加入 `location /updates/`，经 `nginx -t` 验证后 reload。缺失清单返回 HTTP 404，写请求返回 403；配置会备份到 `/usr/local/etc/nginx/`（不在 nginx `servers/*` include 目录中）。nginx 进程仍监听 `*:8080`，更新 location 由 `allow 192.168.50.0/24` / `deny all` 限制访问；发布 SSH 用户需要能写更新目录。另一台 LAN 客户端尚未实测。

健康检查可访问：

- `http://192.168.50.229:8080/updates/windows/stable/feige-windows-update.json`（尚无发布时应返回 404）
- 局域网另一台客户端也须能通过上述 URL 访问

Mac mini DHCP 地址需保持固定；nginx 由现有 Homebrew LaunchAgent 管理。该主机目前 macOS 12.7.6 / Intel x86_64，不能作为 Windows 安装包构建/签名环境。

## 常规发布流程

默认不生成交付包；需要明确授权发布/打包后才执行。构建产物先落在被忽略的 `Project/Windows/dist/build/`，只有 `Publish-LanUpdate.ps1` 把校验通过的安装包与清单归档到 `Release/Windows/<version>/`（不提交 Git）。发布目标只有 Mac mini 局域网：每次发布都会替换服务器上的 `versions/` 与清单指针，旧的版本目录需在发布时清理。签名私钥只在 Windows 发布用户下的 CNG；代码签名证书私钥仍在该发布环境，绝不能复制到 Mac mini。

1. 在已授权的 Windows 发布环境构建并完成安装器 Authenticode 签名。`Build-Installer.ps1` 输出到 `Project/Windows/dist/build/`，默认要求传入 `-CertThumbprint`；签名证书 DER SHA-256 必须与 `UpdateSource.json` publisher pin 一致。只有显式加 `-AllowUnsignedTest` 才会生成未签名测试包，发布脚本会拒绝该包。
2. 运行 `New-UpdateRecord.ps1`（输入与输出同样位于 `Project/Windows/dist/build/`），其 notes/download URL 使用固定 LAN base URL。核对安装包 SHA-256、大小、版本与 build。
3. 使用 `Feige.UpdateTool sign` 为原始发布记录签名，输出到 `Project/Windows/dist/build/feige-windows-update.json`；签名私钥不得离开 CNG 发布环境。
4. 使用 `Publish-LanUpdate.ps1 -ManifestPath <Project\\Windows\\dist\\build\\feige-windows-update.json> -WhatIf` 预览。真实发布需确认（`ConfirmImpact=High`）。脚本先把版本包上传到服务器临时目录，校验 SHA-256 后用同文件系统的目录重命名一次性提升为 `versions/<tag>`（不可覆盖），再从真实 HTTP 地址重新下载校验，最后原子替换清单指针并在成功后将安装包与清单归档到 `Release/Windows/<version>/`；替换前保留上一份指针为 `feige-windows-update.previous.json` 供手工回滚。若上次运行已在提升版本目录后失败，核对服务器后使用 `-ResumePointer` 复验现有文件并仅推进指针。`RemoteRoot` 固定为 `/usr/local/var/www/feige-updates`，`SshTarget` 只能是简单主机别名；Windows 发布用户需在 `~/.ssh/config` 中自备 `songli-macmini` 别名（MCP 的服务器配置不会自动同步到该机器）。
5. 发布后从另一台局域网设备读取清单与包，按签名清单再次校验尺寸与 SHA-256，并在普通 Windows 用户账户验收更新检查/下载/安装、签名错误拒绝、网络中断及升级结果。

## 更安全的远程访问

如需外网用户、公共网络或无法完全信任的 LAN，先规划稳定域名 + 正式 HTTPS（或经认证 VPN），移除私网 HTTP 例外，并更新固定信任 Host/证书配置；不可把 `allowInsecurePrivateNetwork` 改成全局宽松策略。当前流程只支持 Mac mini 局域网，不宣称公共更新已上线。

## 清单协议

外层严格 JSON：`format`（整数 1）、`keyId`、Base64 原始 UTF-8 `payload`、Base64 RSA-PSS/SHA-256 `signature`。签名原文为 UTF-8 `Feige.Windows.Update.v1\n` + `keyId` + LF + payload 原始字节。payload 包含 `schemaVersion: 1` 和最多 64 个 release 条目。每项包含 platform、architecture、channel、installation、version、build、publishedAt、minimumSystemVersion、summary、notesUrl、downloadUrl、size、sha256 和 protocolVersion；同平台/架构/渠道/安装方式/build 不可重复。

版本按数值比较，不允许版本降级；同版本只接受更高 build。客户端下载内容必须通过已固定发布者 pin 的 Authenticode 验证，只有完整验证成功的包可进入安装交接。

## 当前签名信任

RSA 更新清单公钥和发布者证书 pin 固定在客户端配置 `Project/Windows/src/Feige.App/Updates/UpdateSource.json`。配置中的公钥及 pin 仅供验证；私钥材料不写入 Git、清单、Mac mini 或交付目录。密钥不可用/过期/泄露时停止发布，必须先发布包含新公钥/新 pin 的可信客户端，再切换签名密钥。
