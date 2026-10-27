> 历史 v1 规范：以下原文保留至 main e7339d0，含 Windows DNS-SD 约定。当前目标见[通信协议 v3](ProtocolV3.md)。

# 飞鸽快传跨平台通信协议（v1 草案）

> **状态：草案，未冻结。** 本文按 macOS 现有实现（`macOS` 分支 `5a315e9`，2026-10-23）整理，作为 Windows 实现的字段级依据；Windows 已实现握手、记录层、传输与发现（`windows` 分支），两端尚未完成真实互通测试。
>
> 字段细节以本文为准，安全底线以[开发总则](DevelopmentRules.md)为准。任何不兼容修改必须提升协议版本并同步更新本文、两端实现与测试向量；协议冻结前不得用于实际文件传输。
>
> **2026-10-23 复核修订**：本文已由一份不共享两端代码的第三方实现（Node 内置 `crypto`）复算 8.1/8.2 全部测试向量，并逐条比对 `5a315e9` 源码。据此修正 7 节 X25519 的 Windows 选型与 3.8 节确认令牌的绑定描述，详见第 10 节。修订不改变任何 wire 格式。

## 1. 范围与连接模型

本文定义 WinUI 3（Windows）与 SwiftUI（macOS）之间的：

- 四消息签名握手、SAS 人工核对与会话密钥派生；
- AES-GCM 记录层与序列号规则；
- 单文件传输时序（manifest → decision → chunk → finish → receipt）；
- 字段上限、拒绝条件、测试向量与 Windows 实现对照。

不包含：UI 交互、安装包与发布流程。设备发现与端口分配只在「两端要能互相找到」所需的范围内约定（见下文），细节留给各端实现。

**传输假设**

- 使用可靠、有序的字节流（TCP）。当前 macOS 参考实现使用 `Network.framework` 的 TCP 连接；Windows 建议使用 `TcpClient`/`TcpListener` 或 WinRT `StreamSocket`。
- 不使用 TLS；机密性与完整性由本文的握手和记录层提供。
- **一条连接只承载一次握手和一次单文件传输**。完成或失败后关闭连接；传下一个文件需重新握手。
- 握手角色 `initiator` 与 `responder` 只决定密钥方向和 transcript 顺序，不限制谁发送文件。握手完成后任一端都可以发起文件传输。

**发现（DNS-SD，Windows 已实现，macOS 待实现）**

传输层之外用 mDNS/DNS-SD 提供「有哪些设备、地址与端口是什么」。该层**只负责找到可拨号的端点**，不参与认证：
谁都可以在局域网上广播任意服务与任意 TXT，因此设备列表是提示信息，身份一律以第 3 章握手的指纹与 SAS 为准。

| 项 | 约定 |
| --- | --- |
| 服务类型 | `_feige._tcp`，域 `local`（完整名 `_feige._tcp.local`） |
| 实例名 | 左起第一个标签为设备名，按 DNS 标签上限截断到 63 字节；大小写不敏感比较 |
| 端口 | 由监听端决定，绑定 0 时取系统分配的端口，在 SRV 记录中通告；**端口不是固定值**，不得硬编码 |
| TXT | `v=1`（协议版本，与 `offer.version` 同源）、`name=<完整设备名>`、`fp=<SPKI 指纹，去掉空格的大写十六进制>` |
| TTL | 服务 PTR 4500 秒；SRV/TXT/A/AAAA 120 秒。关闭前发 TTL 0 的 goodbye 让对端立即移除 |
| 组播地址 | IPv4 `224.0.0.251:5353`，IPv6 `ff02::fb:5353`，两种地址族都应通告 |

解析顺序即标准的 PTR → SRV → TXT → A/AAAA 三段式：PTR 给出实例名，SRV 给出主机名与端口，地址记录给出可拨号地址。
在拿到 SRV 与至少一条地址记录之前，设备不应出现在「可连接」列表中。

`fp` 只用于设备列表展示与「上次连过的是它吗」这类提示，**不得**据此跳过 SAS 核对，也不得当作信任锚。

发现层与握手是解耦的：主机与端口也可以由用户手工输入（例如跨网段、mDNS 被防火墙拦截），此时跳过本节即可。

## 2. 编码约定

| 项目 | 约定 |
| --- | --- |
| 整数 | 大端序（网络字节序） |
| 字符串 | UTF-8，不带 BOM |
| 长度字段 | 以字节为单位，不包含长度字段自身 |
| JSON 中的二进制 | 标准 Base64（含 `=` 填充，无换行） |
| UUID | 标准字符串形式；解码时大小写不敏感 |
| JSON | 紧凑或带空白均可，字段名必须完全一致；不使用可执行序列化格式 |
| 未知字段 | 忽略；未知版本、类型、超限一律拒绝 |
| 超时（建议） | 握手与传输每次读写 15 秒；用户确认窗口 5 分钟；均为本地策略，不上线 |

## 3. 握手

### 3.1 握手帧

握手阶段独立于记录层，使用：

```text
+----------------+---------------------------+
| length: u32 BE | JSON payload (length 字节) |
+----------------+---------------------------+
```

- `length` 范围 1..16384（16 KiB）；为 0 或超限立即断开。
- 每个方向独立发送；接收端必须按字节流缓冲，处理 TCP 拆包/粘包。

### 3.2 交换顺序（双方对称）

```text
initiator                          responder
    | ---- Offer (JSON) ----------------> |
    | <---------------- Offer (JSON) ---- |
    |  校验、构造 transcript、签名         |
    | ---- Proof (JSON) ----------------> |
    | <---------------- Proof (JSON) ---- |
    |  验签 + 展示指纹/SAS + 用户确认      |
    |  派生会话密钥                        |
```

双方**先写 Offer，再读对端 Offer**；不要等待收到 Offer 后才发送自己的，否则会互相阻塞。收到 Offer 后按 3.4 校验，构造 transcript（3.5），签名后发送 Proof，再读对端 Proof 并验签。

### 3.3 Offer 消息

JSON 外层键为 `offer`：

```json
{
  "offer": {
    "version": 1,
    "role": 1,
    "deviceName": "Mac-A",
    "identityPublicKeyDER": "<Base64 SPKI DER>",
    "ephemeralPublicKey": "<Base64, 32 字节>",
    "nonce": "<Base64, 32 字节>",
    "sessionCode": "123456"
  }
}
```

| 字段 | 类型 | 约束 |
| --- | --- | --- |
| `version` | 数字（u16） | 必须为 `1` |
| `role` | 数字（u8） | `1` = initiator，`2` = responder |
| `deviceName` | 字符串 | 非空，UTF-8 不超过 128 字节 |
| `identityPublicKeyDER` | Base64 字符串 | RSA-3072 公钥的 **SubjectPublicKeyInfo (SPKI) DER**，1..1024 字节 |
| `ephemeralPublicKey` | Base64 字符串 | X25519 公钥，恰好 32 字节 |
| `nonce` | Base64 字符串 | 32 字节安全随机数 |
| `sessionCode` | 字符串 | 恰好 6 个 ASCII 数字 `0-9` |

`identityPublicKeyDER` 必须是 SPKI，而不是 PKCS#1 `RSAPublicKey`。macOS 由 `SecKeyCopyExternalRepresentation` 得到 PKCS#1 后按 rsaEncryption OID 包装为 SPKI；Windows 可用 `RSA.ExportSubjectPublicKeyInfo()` 或 `CryptExportPublicKeyInfo` + `CryptEncodeObjectEx(X509_PUBLIC_KEY_INFO)`。两端指纹都基于这串 SPKI 字节计算，编码必须一致。

### 3.4 Offer 校验

| 条件 | 失败处理 |
| --- | --- |
| `version == 1` | 否则断开，不降级 |
| 双方 `role` 相反（1/2 与 2/1） | 否则断开 |
| 双方 `identityPublicKeyDER` 不同 | 否则断开（禁止与本机自身配对） |
| `sessionCode` 完全一致 | 否则断开（配对码不匹配） |
| `deviceName`、密钥、nonce、配对码满足 3.3 约束 | 否则断开 |

### 3.5 Transcript 编码

签名与 SAS 都基于同一段二进制 transcript。按 **initiator 在前、responder 在后**（与连接方向无关）：

```text
"FEIGE-HANDSHAKE-V1" || 0x00 || offer(initiator) || offer(responder)
```

单个 offer 的编码（全部大端）：

```text
u16 version
u8  role
u32 deviceNameLen          || deviceName (UTF-8)
u32 identityKeyLen         || identityPublicKeyDER
u32 ephemeralKeyLen (=32)  || ephemeralPublicKey
u32 nonceLen (=32)         || nonce
u32 sessionCodeLen (=6)    || sessionCode (ASCII)
```

`u32` 长度为对应字段的字节数，不含长度字段本身。

### 3.6 签名与指纹

- **签名算法**：RSASSA-PSS，SHA-256，MGF1-SHA256，salt 长度 32 字节。
- **签名对象**：完整 transcript 原始字节（3.5），不是它的 SHA-256 摘要；PSS 内部自行做摘要。
- **密钥**：RSA-3072 长期身份密钥，私钥不出设备；公钥在 Offer 中传输。
- **指纹**：`SHA-256(identityPublicKeyDER)`，输出 32 字节转大写十六进制，每 4 个十六进制字符一组、以单个空格分隔（共 16 组）。比较时去除空格并统一大写，大小写不敏感。
- 两端必须双向互验签名；任一失败立即断开并提示身份校验失败。

> Windows 提示：.NET `RSA.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)` 的 salt 长度等于摘要长度（32），与 macOS 参考实现一致；若用 CNG 原生 API，需显式设置 `BCRYPT_PSS_PADDING_INFO.cbSalt = 32`。首次互通必须做双向验签测试。该参数已用 OpenSSL 对 macOS 生成的 PSS 签名实测通过（`rsa_pss_saltlen:32`）。

### 3.7 Proof 消息

JSON 外层同时包含 Offer 原文与签名：

```json
{
  "offer": { "...与已发送的 Offer 完全一致..." },
  "signature": "<Base64 RSA-PSS 签名>"
}
```

接收端必须校验 `proof.offer` 与之前收到的 Offer 解码后相等，再用该 Offer 的公钥验证 `signature` 对 transcript 的签名。不要信任 Proof 中重新出现的公钥而不做一致性校验。

### 3.8 用户确认（SAS）

握手验签成功后，双方各自向本机用户展示：

- 对端设备名；
- 对端 SPKI 指纹（3.6 格式）；
- 由 transcript 派生的 6 位 SAS（3.10）。

用户必须人工核对指纹/SAS 并确认，接收端才可进入文件传输。**本机确认不代表对端已确认**；每一端只在本机确认后派生密钥，任一端拒绝则直接关闭连接。

**规范要求**：确认结果必须是「一次性令牌」，绑定下列四项，缺任一项都可能让确认结果被挪用到另一次握手或另一个方向：

| 绑定项 | 防的是什么 |
| --- | --- |
| transcript 摘要 | 把确认钉在本轮握手上，防止挪用到另一会话 |
| 对端 SPKI 指纹 | 把确认钉在具体对端身份上 |
| 本地角色 | 防止 initiator/responder 方向错位后复用 |
| 有效期 | 限时失效，避免长期挂起的确认被重新消费 |

**macOS 现状（`5a315e9`）**：`SASApproval` 只携带 `transcriptHash` 与 `peerFingerprint` 两个字段；`localRole` 与 `expiresAt` 由 `PairingApprovalCoordinator` 实例持有，在 `confirm()` 和 `consume()` 时校验；单次消费由协调器状态机（`awaitingUser → confirmed → consumed`）配合握手层 `didDeriveSession` 保证。四项约束实际都成立，区别是**绑定关系分散在令牌与协调器两处**，而非集中在令牌上。

这四项属本地实现策略，**不是 wire 字段**：Windows 可以把四项全部放进令牌对象，只要语义等价即可，两端无需一致。

配对码（Offer 中的 `sessionCode`）只用于让双方进入同一次握手，**不是密码、不是密钥、不能替代 SAS**；它在握手报文中明文传输。SAS 才是防中间人的带外核对值。

### 3.9 会话密钥派生

1. 用本机 X25519 私钥与对端 `ephemeralPublicKey` 计算 32 字节共享密钥 `shared`；全零结果视为失败。
2. 对 `shared` 做 HKDF-SHA256，`salt = SHA-256(transcript)`（3.5 的摘要），输出 32 字节：

```text
i2r = HKDF-SHA256(ikm=shared, salt=digest, info="feige-v1 initiator-to-responder", L=32)
r2i = HKDF-SHA256(ikm=shared, salt=digest, info="feige-v1 responder-to-initiator", L=32)
```

3. 按角色选择方向：

| 角色 | 发送密钥 | 接收密钥 | 发送 nonce 前缀 | 接收 nonce 前缀 |
| --- | --- | --- | --- | --- |
| initiator | `i2r` | `r2i` | `digest[0..4] \|\| "INIT"` | `digest[0..4] \|\| "RESP"` |
| responder | `r2i` | `i2r` | `digest[0..4] \|\| "RESP"` | `digest[0..4] \|\| "INIT"` |

`"INIT"` 的字节为 `49 4E 49 54`，`"RESP"` 为 `52 45 53 50`。每个方向、每次握手都必须使用全新密钥；同一会话密钥不得跨连接复用。

### 3.10 SAS 与配对码

**SAS**（6 位数字，双方应显示为相同值）：

```text
n   = digest[0..4] 按大端解释为 u32
sas = n mod 1_000_000
显示 = 6 位十进制，左侧补零
```

**配对码**：恰好 6 个 ASCII 数字，由一端生成后通过带外方式告知另一端（UI 显示、口头等），双方必须完全一致。macOS UI 的有效期为 5 分钟；这是本地策略，不是 wire 字段。

## 4. 记录层

握手完成并派生会话后，所有应用数据都封装为 AES-256-GCM 记录。

### 4.1 记录帧

```text
+----------------+--------+------------------+----------------+----------------+---------+
| length: u32 BE | type:u8| sequence: u64 BE | ctLen: u32 BE  | ciphertext     | tag:16B |
+----------------+--------+------------------+----------------+----------------+---------+
                  \___________________ 13 字节 header (AAD) ______________/
```

- `length` = 13 + `ctLen`，即整个记录（不含最外层 4 字节长度）的字节数。
- `ctLen` = 明文长度 + 16（GCM tag）。
- `ciphertext` 长度等于明文长度，随后是 16 字节 tag。
- `header`（type + sequence + ctLen，共 13 字节）作为 AEAD 的 **AAD**；篡改类型、序列号或长度都会认证失败。

**Nonce**：**16 字节**，`prefix(8) || sequence(8, BE)`；`prefix` 按 3.9 选择，`sequence` 从 0 开始、每记录加 1。

> 注意这是 16 字节，不是 AES-GCM 常见的 12 字节：8 字节前缀与 8 字节大端序列号各占一半，§8.2 的向量也按 16 字节给出。两边必须一致，否则认证标签对不上。`.NET` 的 `System.Security.Cryptography.AesGcm` 只接受 12 字节 nonce（传入 16 字节直接抛 `ArgumentException`），Windows 侧需改用支持任意 nonce 长度的实现（BouncyCastle `GcmBlockCipher`）。

**加密**：AES-256-GCM，key 为对应方向的 32 字节会话密钥，nonce 如上，AAD 为 13 字节 header，tag 16 字节。

### 4.2 记录约束

| 项目 | 限制 |
| --- | --- |
| 明文长度 | 1..65536 字节；仅 `finish`（类型 7）允许 0 字节 |
| `ctLen` | 16..65552 |
| 记录长度 | 29..65565（13 + 16 到 13 + 65552） |
| 序列号 | 每方向独立，从 0 开始严格递增；重复、跳号、乱序立即断开 |
| 类型 | 1..7；未知类型拒绝 |
| 完成标记 | 发出或收到 `finish` 后该方向不得再发/再收应用记录；`finish` 后出现多余字节视为错误 |

读取时必须先校验长度上限，再分配/读取正文；不得根据未校验的长度预分配大缓冲区。

### 4.3 消息类型

| type | 名称 | 方向 | 载荷 | 状态 |
| --- | --- | --- | --- | --- |
| 1 | `manifest` | 发送方 → 接收方 | UTF-8 JSON，见 4.4 | 已实现 |
| 2 | `decision` | 接收方 → 发送方 | 1 字节：`0x00` 拒绝，`0x01` 接受 | 已实现 |
| 3 | `chunk` | 发送方 → 接收方 | 原始文件字节，1..65408 字节 | 已实现 |
| 4 | `receipt` | 接收方 → 发送方 | 1 字节 `0x01`：摘要校验通过 | 已实现 |
| 5 | `cancel` | 双向 | 未定义 | 保留，v1 不应发送 |
| 6 | `error` | 双向 | 未定义 | 保留，v1 不应发送 |
| 7 | `finish` | 发送方 → 接收方 | 空载荷 | 已实现 |

### 4.4 manifest

明文为 UTF-8 JSON：

```json
{
  "transferID": "E621E1F8-C36C-495A-93FC-0C247A3E6E5F",
  "fileName": "photo.bin",
  "size": 120000,
  "sha256": "<Base64, 32 字节>"
}
```

| 字段 | 约束 |
| --- | --- |
| `transferID` | 每次传输新生成的 UUID；用于把接收确认绑定到本次传输 |
| `fileName` | 非空，UTF-8 ≤255 字节；不得为 `.`/`..`；不得含 `/`、`\` 或控制字符 |
| `size` | 1..104857600（100 MiB） |
| `sha256` | Base64 解码后恰好 32 字节，为完整文件 SHA-256 |

接收端在展示确认界面前必须校验这些约束；超限或非法文件名直接拒绝，且不得写入磁盘。

### 4.5 传输消息语义

- `decision`：接收端只有在用户确认后才发送；发送端收到 `0x00` 必须以“对方拒绝”结束，不得继续发送 chunk。
- `chunk`：有序文件片段，第 1 个 chunk 对应 offset 0；接收端要求累计偏移连续、每个 chunk 1..65408 字节、总长度不超过 manifest.size。macOS 发送端使用 chunk 上限 `65408 = 65536 - 128`，Windows 必须不超过该值。
- `finish`：最后一个 chunk 之后发送，空载荷；表示发送方数据发送完毕。
- `receipt`：接收端在完整校验 SHA-256 通过后发送；发送端收到 receipt 才算成功，超时/断开视为失败。落盘与原子重命名是接收端本地步骤，规范要求先完成再发 receipt（当前 macOS 实现的差异见 5.3）。
- 摘要失败、写盘失败或用户取消时，不发送 receipt，关闭连接。

### 4.6 终止与错误

- v1 没有 `close_notify`、`cancel` 或 `error` 的线上格式；任何协议错误、认证失败、超时或 EOF 都直接关闭连接。
- 接收端在摘要校验完成前断开，必须删除临时文件并记为失败，不得留下“已完成”记录。
- 发送端在收到 receipt 前断开，同样记为失败。

## 5. 传输时序

### 5.1 接受并完成

```text
sender                                  receiver
  | -- manifest (plaintext JSON) ----------> |
  |                                         | 校验 manifest，展示用户确认
  | <------------------------ decision 0x01 | 创建临时 sink
  | -- chunk 0..N (<=65408 each) ----------> | 连续偏移写盘 + 累计 SHA-256
  | -- finish (empty) ---------------------> |
  |                                         | 校验 size/SHA-256，原子重命名
  | <---------------------------- receipt 0x01
  | 关闭连接                                 | 关闭连接
```

### 5.2 拒绝

```text
sender                                  receiver
  | -- manifest ---------------------------> |
  | <------------------------ decision 0x00 |
  | 以“对方拒绝”结束，不发 chunk              | 清理临时状态并关闭
```

### 5.3 实现差异与待办（重要）

- macOS 的 `TransferSession.receive` 在收满 `size` 后直接校验并发送 receipt，**没有显式消费 `finish` 记录**；`EndToEndTransferTests` 则消费了 finish 并检查流结束。
- v1 规范要求发送方必须发送 `finish`；接收方应当读取并确认其后无多余记录。Mac 应用层在接入真实网络前需要统一这两条路径。
- macOS 在摘要校验后立即发送 receipt，临时文件的原子重命名由调用方在其返回后执行。v1 要求先原子落盘再发 receipt；接入真实网络前需要调整，否则重命名失败会让发送端误判成功。
- 尚无 chunk 级 ACK；成功回执只有最终 `receipt`。《开发总则》早期列出的 `ack`、`hello`、`pair_challenge` 等消息未在 v1 实现，不得按旧描述编码。

## 6. 安全要求

1. 没有完成签名握手 + 双方人工 SAS 确认，不得派生密钥或传输文件。
2. 记录层必须认证加密；不得明文发送文件名、大小或内容。
3. 严格校验所有长度、类型、角色、版本和序列号；失败即断开，不尝试恢复。
4. 每次连接使用新的 X25519 临时密钥和新的序列号空间；禁止复用 nonce/key。
5. 接收端未经用户确认不得写盘；确认前只允许读取 manifest。
6. 默认不覆盖已有文件；先写临时文件，校验通过后原子重命名。
7. 日志不得记录配对码、SAS、文件内容、密钥材料或敏感完整路径。
8. 发现结果与任何网络输入都不可信；不因收到重定向/代理要求而改变目标地址。

## 7. Windows 实现对照

| 能力 | macOS 参考实现 | Windows 建议 |
| --- | --- | --- |
| CSPRNG | `SecRandomCopyBytes` | `RandomNumberGenerator.Fill` / `BCryptGenRandom` |
| RSA-3072 身份 | Security.framework，Keychain 持久化 | `RSA.Create(3072)`；私钥存 DPAPI/CNG，不导出 |
| SPKI 导出 | 手工包装 PKCS#1 为 SPKI | `RSA.ExportSubjectPublicKeyInfo()` |
| RSA-PSS 签名 | `SecKeyAlgorithm.rsaSignatureMessagePSSSHA256` | `RSA.SignData(..., SHA256, RSASignaturePadding.Pss)`；CNG 需 `cbSalt=32` |
| X25519 | CryptoKit `Curve25519.KeyAgreement` | **已定：`BouncyCastle.Cryptography` 2.7.0**（纯托管，2026-10-23 用 RFC 7748 向量验证通过）；备选 libsodium `crypto_scalarmult_curve25519`。.NET 与 CNG 都不可用 |
| HKDF-SHA256 | CryptoKit | `HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, info)` |
| AES-256-GCM | CryptoKit `AES.GCM` | **不能用 `System.Security.Cryptography.AesGcm`**（只接受 12 字节 nonce，协议要求 16 字节）：用 BouncyCastle `GcmBlockCipher` + `AeadParameters`，128 位 tag、header 作 AAD |
| SHA-256 | CryptoKit | `SHA256.HashData` |
| JSON | Foundation `JSONEncoder`/`Decoder` | `System.Text.Json`；字段名逐字匹配，二进制转 Base64，角色/版本用数字 |
| TCP | `NWConnection` (.tcp) | `TcpClient`/`StreamSocket`，按字节流自行分帧 |

**为什么不能用 CNG / OpenSSL 顶替**：Windows CNG 的 ECDH 算法族只有 NIST 曲线（P-256 / P-384 / P-521），不含 Curve25519；`System.Security.Cryptography` 在 .NET 10 也没有 X25519 类型。原生的 `X25519DiffieHellman` 计划在 .NET 11 提供，而 .NET 11 是 STS（短期支持）版本，不能作为本项目的 LTS 基线。

Windows 侧已定稿：`Feige.Core` 用 BouncyCastle 同时承担 X25519 与 AES-GCM（记录层因 16 字节 nonce 同样无法用 .NET 内置实现），RFC 7748 A.5 / 6.1 向量与「小阶对端必须拒绝」均已通过，并与不共享代码的 Node 实现交叉验证 `shared` / `i2r` / `r2i` 逐字节一致。

**易错点**

1. 所有整数（含长度、序列号、nonce 中的序列号）都是大端；.NET 需手动 `BinaryPrimitives.WriteUInt64BigEndian`。
2. 签名输入是 transcript 原字节；不要先做 SHA-256 再签，也不要签 JSON 文本。
3. 指纹基于 SPKI DER；若用 PKCS#1 计算会与 macOS 不一致。
4. PSS salt 固定 32；用 CNG 时忘记设置 `cbSalt` 会导致两端验签失败。
5. Nonce 不是随机数，而是 `8 字节前缀 + 8 字节大端序列号`，**共 16 字节**（不是 AES-GCM 常见的 12 字节）；前缀来自 transcript 摘要。用 .NET 的 `AesGcm` 会因长度不符直接抛异常，必须换支持任意 nonce 长度的实现。
6. AAD 是包含 `ctLen` 的 13 字节 header；漏掉长度字段会认证失败。
7. 最外层记录长度包含 13 字节 header；`ctLen` 包含 16 字节 tag。
8. 每方向序列号独立从 0 开始；不要共用序列号。
9. chunk 上限 65408，不是 65536；`finish` 必须为空载荷。
10. 一条连接只传一个文件；完成后重新握手。
11. 不要试图用 `ECDiffieHellman` 或 CNG 顶替 X25519：曲线不匹配，.NET 10 也没有 X25519 类型，必须引入托管库或原生库。

## 8. 测试向量

以下向量用于校验编码/加密实现，**密钥与公钥均为测试值，不得用于真实通信**。8.1 已用 macOS 包内测试代码复算通过，8.2 与 8.3 已用 Node `crypto` 独立解密验证。

8.1 到 8.3 共 22 项，已由一份**不共享两端代码**的独立实现完整复算通过（`windows` 分支 `Windows/scripts/verify-protocol-vectors.js`，仅用 Node 内置 `crypto`）。Windows 实现应把它当作回归 oracle：先跑通该脚本，再对齐 C# 单测，避免实现与单测一起出错。

### 8.1 Transcript / SAS

固定输入：

- initiator：`deviceName="Mac-A"`，role=1，公钥=`0xAA` × 8，ephemeral=`0x11` × 32，nonce=`0x22` × 32，code=`123456`
- responder：`deviceName="Win-B"`，role=2，公钥=`0xBB` × 8，ephemeral=`0x33` × 32，nonce=`0x44` × 32，code=`123456`

（公钥字节仅用于编码向量，真实 Offer 必须是合法 RSA-3072 SPKI。）

```text
offer(initiator) =
000101000000054d61632d4100000008aaaaaaaaaaaaaaaa00000020111111111111111111111111111111111111111111111111111111111111111100000020222222222222222222222222222222222222222222222222222222222222222200000006313233343536

offer(responder) =
0001020000000557696e2d4200000008bbbbbbbbbbbbbbbb00000020333333333333333333333333333333333333333333333333333333333333333300000020444444444444444444444444444444444444444444444444444444444444444400000006313233343536

transcript = "FEIGE-HANDSHAKE-V1" || 00 || offer(initiator) || offer(responder)
digest     = 779855ca63ad32499d9c50fa4cd1c0764862f7bdbc717e22f1ccd81712ced273
SAS        = 472138
nonce 前缀 (initiator) = 779855ca494e4954
nonce 前缀 (responder) = 779855ca52455350
fingerprint(0xAA×8) = 6893 A08C 9C4C C97C 2279 9A6A A7AF 6F8B 8BC2 2D54 1A5E 4253 B180 BBC9 74C9 D497
fingerprint(0xBB×8) = 8C7D 2C6F 7DEF 9A50 D4AD 3023 702D F661 BE86 637F 2854 EFF9 4DE6 0367 6A81 3341
```

### 8.2 AES-GCM 记录

固定测试密钥（非派生值）：

```text
key       = 1111111111111111111111111111111111111111111111111111111111111111
nonce     = 779855ca494e4954 0000000000000000   (prefix || seq=0)
header    = 01 0000000000000000 00000021        (type=1, seq=0, ctLen=33)
plaintext = 66656967652d7265636f72642d74657374   ("feige-record-test", 17 字节)
ciphertext= 02eff278f8692682ae1bec1a6c54adc0da
tag       = f7e2e9d4e1b6bfbac728898115a6db64
frame     = 0000002e 01000000000000000000000021 02eff278f8692682ae1bec1a6c54adc0da f7e2e9d4e1b6bfbac728898115a6db64
```

`frame` 可直接用于验证“长度前缀 + header + 密文 + tag”的拼装与解密。

### 8.3 记录序列

同一固定密钥（`key = 0x11 × 32`、`prefix = 779855ca494e4954`）连续三条记录，用于验证序列号推进与 `finish` 的空载荷：

```text
seq=0  type=1  "feige-record-test" (17B)
  0000002e 01000000000000000000000021 02eff278f8692682ae1bec1a6c54adc0da f7e2e9d4e1b6bfbac728898115a6db64

seq=1  type=3  00..1f (32B)
  0000003d 03000000000000000100000030
  d28326309f5a2dc13c837b11a21cdf3ea86263a9c58cc4f0467f4db33f722945
  47b6443017390c042da902933d1e4f52

seq=2  type=7  "" (0B)
  0000001d 07000000000000000200000010 bcc19843d8716dbafb740136ca263574
```

把这三条按顺序拼成一个字节流喂给解码器，必须依次产出 `type=1/3/7`、`sequence=0/1/2` 的记录，且 `finish` 的明文长度为 0；随后再收到任何字节都属于错误（4.2）。把 `seq=0` 那条 header 里的序列号改成 `1` 而不重新计算 tag 必须认证失败——序列号在 AAD 里。

## 9. 互通验收清单

在两处真实设备上至少完成：

1. Offer/Proof JSON 双端互解，字段名与 Base64 一致。
2. 两端 transcript 摘要相同，SAS 均为 `472138`（用 8.1 向量时）。
3. RSA-PSS 签名双向互验通过；错误签名被拒。
4. X25519 + HKDF 派生结果一致：A 的发送密钥 = B 的接收密钥，反向亦然。
5. AES-GCM 记录双向互解；8.2 向量在本机解密成功；8.3 的三条记录按序解码且 `finish` 后多一字节被拒。
6. 120,000 字节文件双向传输后 SHA-256 一致；接收端原子落盘。
7. 拒绝、配对码不一致、错误指纹/SAS、重复序列号、篡改 header、篡改密文、超限 chunk 均被拒。
8. 断开、超时、磁盘写入失败不产生完成记录。

当前阻塞项：Windows 无实现、macOS 网络适配未接入应用、发现层未实现、`finish` 消费路径待统一。完成以上验收后，v1 才能从草案冻结。

## 10. 修订记录

| 日期 | 变更 |
| --- | --- |
| 2026-10-23 | 首版：按 `5a315e9` 整理字段级规范、Windows 实现对照与测试向量 |
| 2026-10-23 | 复核修订：7 节 X25519 的 Windows 选型由「libsodium 或 OpenSSL/CNG」改为 BouncyCastle / libsodium（CNG 无 Curve25519，.NET 10 无内置类型，.NET 11 为 STS 不可用）；3.8 节把确认令牌的绑定要求拆为「规范要求」与「macOS 现状」；8 节补记第三方独立实现复算结论；易错点补第 11 条 |
| 2026-10-23 | 记录层修订：4.1 节 nonce 由「12 字节」更正为 **16 字节**（`prefix(8) + sequence(8, BE)` 本就是 16 字节，与 §8.2 向量、macOS `makeNonce` 一致）；7 节 AES-GCM 与易错点第 5 条同步说明 `.NET AesGcm` 只接受 12 字节、需改用 BouncyCastle `GcmBlockCipher`；7 节 X25519 标注 Windows 已定稿 BouncyCastle 并已用 RFC 7748 验证 |
| 2026-10-23 | 新增 8.3 记录序列向量（seq 0..2 覆盖 `manifest`/`chunk`/空 `finish`、按序解码、序列号参与 AAD 的负例）；8 章补充第三方复算项数与 9.5 验收条 |

> 上述修订只纠正描述与实现建议，**未改变任何 wire 格式**，两端现有实现无需改动。协议版本仍为 v1 草案。
