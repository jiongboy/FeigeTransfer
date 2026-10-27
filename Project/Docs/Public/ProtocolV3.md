# 跨平台通信协议 v3

状态：Windows 与 macOS 的共同实现基线，尚未冻结为完整验收的 Win↔Mac 发布基线。依据 macOS 0.3.3 实现；2026-10-23。v1/v2 与 v3 不互通，拒绝版本不一致，不自动降级。UI 行为见 [UI 与交互同步规范](UISpecification.md)。

## 1. 与 v1 的不兼容差异

| 项目 | 旧 v1 | v3 |
| --- | --- | --- |
| 用户流程 | 配对码、双方 SAS 核对、逐文件批准 | 接收连接的一方同意一次，整条连接内直接互传 |
| Offer | version=1、6 位 sessionCode | version=3、sessionCode 必须为空字符串 |
| transcript / HKDF 域 | V1 / feige-v1 | V3 / feige-v3 |
| GCM nonce | 16 字节 | **12 字节：digest 前 4 字节 + seq 的 8 字节大端整数** |
| 连接 | 单文件即结束 | 多批文件复用、双向同时传输、文字可交错 |
| manifest | 预先带 sha256，1..100 MiB | 无预摘要，size 可为 0，新增 kind，最大为文件系统表示范围 Int64.max |
| decision | 单字节 | 带 transferID 的 JSON |
| chunk | 文件内容 | UUID 原始 16 字节 + offset 8 字节大端 + 内容 |
| finish / receipt | 空 finish、单字节 receipt | 均为带 transferID 与最终 sha256 的 JSON |
| 扩展 | 无 | type=8 连接决定，9 文字，10 文字回执 |

不得把 v1 的 16 字节 nonce、空 finish、65408 字节旧块、100 MiB 或旧测试向量混用于 v3。v3 的 chunk 内容上限是 65512 字节。

## 2. 发现与连接

服务类型 `_feige._tcp`，本地域 `local.`；服务名 `Feige-` + 本机完整指纹去空格后的前 20 个十六进制字符。TXT：`name`（设备名称）、`version`（字符串 `3`），服务记录提供监听端口。应用准备好本机身份后自动监听和发现，过滤自身与版本不符的记录。

与 Windows 已落地的 v1 DNS-SD 约定不同：v1 用设备名实例、TXT `v=1/name/fp`，允许在列表显示版本不符设备；Mac v3 用指纹前缀实例、TXT `version=3/name` 并过滤不符版本。Windows 升级必须发送并解析 `version=3`，只改成 `v=3` 仍不会被当前 Mac 识别。实例命名也需按本节同步，以便 Mac 将已认证历史关联到附近可重连设备。旧 TTL/goodbye 和 PTR→SRV→TXT→A/AAAA 的标准解析仍可复用，不能将旧 TXT 的 fp 当信任锚。旧规范完整保存在 [v1 历史文档](ProtocolV1-Historical.md)。

发现结果不可信：以签名握手得到的完整 SPKI 指纹认证和归组，而不是服务名、名称或 IP。手动连接使用局域网数字 IP/端口，端口 1..65535。当前 Mac 允许 IPv4 私网、环回和链路本地，以及 IPv6 环回、ULA、链路本地与这些地址的 IPv4 映射；拒绝公网目标与普通域名。在 TCP ready 后再次检查实际远端地址，不能只检查输入文字。发现连接仍需解析到合法地址；不允许服务器重定向目标。

当前一次一个活动连接，每方向最多一个发送文件和一个接收文件，文字可与之并行。TCP 建立默认 10 秒。任一协议错误或用户取消可关闭整个连接，清理未提交文件；取消单个附件目前也会断开连接。

## 3. 编码与握手

所有整数为大端，文字 UTF-8；JSON 二进制使用标准 Base64（带填充、无换行），UUID 为标准带连字符字符串。未知 JSON 字段可忽略，但未知版本、类型、非法枚举、长度或状态必须拒绝。

握手帧为 `u32BE(payloadLength) || JSON`，payload 为 1..16384 字节。双方对称：先写自己的 Offer、再读对方 Offer；按完整 transcript 签名；写 Proof、再读对方 Proof 并验签。不能一端等到读取 Offer 才发送自己的。

Offer 外层为 `{"offer": {...}}`，内部字段：

| 字段 | 类型 / 约束 |
| --- | --- |
| version | u16 数字，必须为 3 |
| role | u8 数字，1=initiator，2=responder，双方相反 |
| deviceName | 非空字符串，UTF-8 ≤128 字节；产品设置额外限制 48 字符并拒绝控制字符 |
| identityPublicKeyDER | RSA-3072 公钥 SPKI DER，Base64 解码后 1..1024 字节；不是 PKCS#1 |
| ephemeralPublicKey | X25519 公钥原始 32 字节，Base64 |
| nonce | 新生成的 32 字节安全随机数，Base64 |
| sessionCode | 空字符串，不能省略或发送旧配对码 |

双方身份公钥必须不同。Proof 为 `{"offer": {原 Offer}, "signature": "Base64"}`；回显的 Offer 解码值必须与先前完全一致。签名算法为 RSA-PSS / SHA-256 / MGF1-SHA256 / saltLength=32，签的是下面完整 transcript 原始字节，不是预先摘要后再次 SignData。

```text
UTF8("FEIGE-HANDSHAKE-V3") || 00 || encode(initiator) || encode(responder)
encode(offer) =
  u16BE(version) || u8(role)
  || u32BE(deviceName UTF8长度) || deviceName UTF8
  || u32BE(SPKI长度)           || SPKI
  || u32BE(32)                 || ephemeralPublicKey
  || u32BE(32)                 || nonce
  || u32BE(0)                  // sessionCode 空字符串，长度字段仍保留
```

`digest = SHA256(transcript)`；指纹是 `SHA256(SPKI)` 的大写十六进制，每 4 个字符一组，空格分隔。比较可去空格并统一大小写。双方必须验签，proof.offer 不符或验签失败直接断开。

X25519 共享结果必须拒绝非法/全零值。HKDF-SHA256 的 IKM 为 shared，salt 为 digest，L=32：

```text
i2r = HKDF(shared, digest, UTF8("feige-v3 initiator-to-responder"), 32)
r2i = HKDF(shared, digest, UTF8("feige-v3 responder-to-initiator"), 32)
```

initiator 发 i2r、收 r2i；responder 反之。每条新连接生成新的 X25519 临时密钥，不跨连接复用密钥、nonce 或序列号。

v3 不要求用户核对 SAS；签名验证的是密钥持有及握手完整性，不等于带外人工确认对方真实身份。不得向用户宣称具备旧 SAS 核对的身份保证。

## 4. 连接授权与记录层

验签并派生密钥后，只有 responder 弹出一次连接同意。responder 的首条发送记录为 type=8，明文恰好一个字节：`00` 拒绝，`01` 同意。initiator 在读到并严格验证 `01` 前不得处理文件或文字；responder 同意前也不得创建接收暂存文件。拒绝或超时即关闭。

该授权只覆盖当前连接的双向文字、图片和文件；重连重新询问，不建立永久自动接收名单。type=8 只能在此阶段出现，已建立会话中再次收到该类型属于错误。

加密记录格式：

```text
u32BE(recordLength)
|| type:u8 || sequence:u64BE || ciphertextAndTagLength:u32BE
|| ciphertext || tag:16B
```

AAD 为 `type || sequence || ciphertextAndTagLength` 共 13 字节。`ctLen = plaintext.count + 16`，`recordLength = 13 + ctLen`；外层长度不包含自身 4 字节。AES-256-GCM 使用对应方向 key，nonce 为 **digest[0..<4] || sequence:u64BE**，恰好 12 字节，两个方向密钥不同。

| 约束 | 数值 |
| --- | --- |
| 单记录明文 | ≤65536 字节；v3 实际消息均非空，空 finish 虽可能通过通用密文层长度检查，但必须在 v3 JSON/状态校验拒绝 |
| ctLen | 16..65552；实际载荷还需符合类型约束 |
| recordLength | 29..65565 |
| 序列号 | 每方向从 0 开始，严格递增；重复/跳号/乱序拒绝，在同一连接上不得因下一个文件而重置 |
| 记录类型 | v3 可用 1..4、7..10；5/6 保留，收到即断开 |

连接决定占用 responder 方向 seq=0，之后该方向从 seq=1 继续；initiator 首条应用记录使用 seq=0。finish 只结束一个文件，不关闭记录流。读端必须处理拆包/粘包并保留跨握手/记录层的剩余字节。写端必须把编码和真实网络发送一起串行化，不能先并发分配序列号再乱序发包。

## 5. 消息与文件传输

| type | 名称 | 明文载荷 |
| --- | --- | --- |
| 1 | manifest | JSON：transferID(UUID)、fileName(String)、size(非负整数)、kind(String) |
| 2 | decision | JSON：transferID、accepted(Boolean)；接收器准备文件成功的机器回执，无额外用户弹窗 |
| 3 | chunk | UUID 原始 16 字节 + offset:u64BE + 文件内容（1..65512 字节） |
| 4 | receipt | JSON：transferID、sha256(Base64，32 字节) |
| 7 | finish | JSON：transferID、sha256(Base64，32 字节) |
| 8 | connectionDecision | 恰好 1 字节 00/01，仅连接阶段 |
| 9 | text | JSON：id(UUID)、text(String) |
| 10 | textReceipt | **JSON 字符串 UUID 本身**，例如 `"00112233-4455-6677-8899-AABBCCDDEEFF"`；不是 `{id:...}` |

### manifest

```json
{"transferID":"00112233-4455-6677-8899-AABBCCDDEEFF","fileName":"photo.png","size":12345,"kind":"image"}
```

kind 为 `file` / `image`，缺省按 file，未知值拒绝。fileName 非空，UTF-8 ≤255 字节，不为 `.`/`..`，不含 `/`、`\` 或控制字符；接收端还需拒绝自己平台不支持的文件名/设备名等，并保证路径不会逸出接收根目录。

size 使用 64 位整数，范围 0..9223372036854775807；Windows 不得用 Int32 或 JavaScript 浮点解析该值。无产品大小阈值，实际文件系统与可用磁盘容量仍是约束。不要预读完整文件或在发送前扫描全部摘要。

### 时序

发送 manifest → 接收端校验并安全创建暂存文件 → 发 decision(true) → 发连续 chunks → 发 finish(最终摘要) → 接收端确认大小、摘要、刷盘及原子提交 → 发 receipt(同 ID、同摘要)。只有 receipt 匹配才记发送成功。空文件不发 chunk，直接 finish，摘要为 SHA256(empty)。

UUID 的 16 字节按字符串去连字符后的十六进制顺序编码，例如 `00112233-4455-6677-8899-AABBCCDDEEFF` 对应 `00 11 22 33 44 55 66 77 88 99 AA BB CC DD EE FF`。**不能直接使用 .NET Guid.ToByteArray() 的默认混合端序布局。**

offset 必须等于该文件已收到的总字节数，ID 必须匹配当前接收文件，不允许越界、重复块或乱序。每方向最多一个文件，另一方向的文件、文字与回执可以穿插。单一读循环按消息类型和 ID 路由，不能让多个接收协程争读同一 TCP 连接。

manifest 不带预摘要。每端边读写边累计 SHA-256；finish/receipt 的摘要都必须是 32 字节。接收方必须显式消费 finish 后才校验/提交，不能收到声明字节数即提前成功。暂存文件需防链接/路径穿越，原子改名不得覆盖已有文件或符号链接，重名生成编号。Mac 以 openat / fsync / RENAME_EXCL 实现，Windows 提供等价保证，不照抄 Unix 调用。

### 文字与图片

text.text 保留原始 Unicode/换行/空格，全空白或 UTF-8 超过 8192 字节拒绝。接收方严格验证并成功记录本机消息后发 type=10；发送方只有匹配回执后才显示“已送达”，并非已读。回执等待项必须在发送前注册，连接关闭要唤醒全部等待者。未知/不匹配回执断开。

手动重试文字复用 UUID，接收端在本机保留窗口内按设备指纹/接收方向/UUID 去重。文件没有相同的 exactly-once 语义；重试附件可能产生重名编号的新文件。图片 kind=image 仍走同一分块与完整性流程，原始内容不压缩；缩略图是本机 UI 行为。

## 6. 时间预算和失败

| 阶段 | Mac 0.3.3 默认 |
| --- | --- |
| TCP 建立 | 10 秒 |
| 初始握手网络交换 | 每次读写 15 秒 |
| 等待签名证明（可能有系统授权） | 5 分钟；总握手预算 5 分钟 + 两段 15 秒网络预算 |
| 连接同意 | 独立最多 5 分钟 |
| 已建立且无活动接收文件 | 不因空闲自动关闭 |
| 活动文件接收数据 / 每次发送 | 15 秒空闲超时 |
| 文件 decision | 30 秒 |
| 文件最终 receipt | 5 分钟 |
| 文字 receipt | 30 秒 |

这些是本地策略，不是线上协商字段。没有固定文件总传输时长上限。不能把等待系统密码误算为普通的 15 秒握手总期限，也不能把所有网络超时取消。

EOF、错误偏移/长度/ID、坏摘要、坏签名、坏 GCM tag、磁盘写入/提交失败、超时或取消均关闭连接并清理未提交文件。已成功提交的文件保留；丢失回执时发送方可能显示未完成而接收方已有文件，不能声称 exactly-once。

## 7. Windows 升级与验证

保留现有 v1 测试，新增 v3 测试，不全局改掉旧向量的版本或 nonce 后冒充兼容。12 字节 nonce 可适配 .NET 原生 AesGcm，但是否替换库由 Windows 实现决定；X25519 与 PSS 继续沿用经验证的能力。

1. 先对齐 [v3 公共向量](TestVectors/v3.json)：transcript、digest、HKDF、双向 GCM、UUID 字节与最终摘要；这是文档夹具，不包含真实私钥。
2. 验证一次同意后多文件、双向文件与文字交错，连接决定后的 seq 不重置。
3. 负例覆盖未知版本/类型、空 finish、错误 kind、路径/ID/偏移/长度/摘要、过期或错误回执、源文件缩短、重放/篡改、取消和磁盘不足。
4. 本机回环及大于 100 MiB/空文件通过后，再跑 Mac→Win / Win→Mac、同名保护和权限/网络异常矩阵。

当前 Mac 有 58 项自动回归，Windows 2c37f61 已有 v1 核心、发现、DPAPI、WinUI 收发/设置/通知/记录及签名 MSIX；当前仍未支持本节 v3 会话协议；v3 双平台互通、共同向量的 Windows 复算和实体双机矩阵仍待完成。本文不会把 Mac 单平台测试当作跨平台验收结果。
