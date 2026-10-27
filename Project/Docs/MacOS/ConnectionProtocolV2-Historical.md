# 连接与传输协议 v2（历史版本）

当前应用已升级为 [Public protocol v3](../Public/ProtocolV3.md)，本文保留 0.2.0 的历史协议记录。

2026-10-23，根据用户要求“询问连接、对方同意即可发送，不限制文件大小”替换原 v1 操作流程。本文记录当时的 macOS 实现，不能作为当前跨平台规范；旧 v1/v2 客户端不能与当前 v3 应用连接。共同规范见 [Public protocol v3](../Public/ProtocolV3.md)。

## 连接与同意

- Bonjour 类型仍为 `_feige._tcp`；TXT 为 `name`、`version=2`。应用启动后发现和监听，不包含配对码。
- Offer 的 `version=2`，`sessionCode` 为空字符串，其他字段沿用长度前缀 JSON 的 Offer/Proof 签名握手。
- Transcript 域改为 `FEIGE-HANDSHAKE-V2\0`，其后仍按 initiator/responder 顺序编码双方完整 offer；拒绝版本不一致，不自动降级。
- 双方验签并协商加密密钥；HKDF info 为 `feige-v2 initiator-to-responder` / `feige-v2 responder-to-initiator`。
- v2 nonce 为 12 字节：transcript SHA-256 的前 4 字节 + 本方向递增的 8 字节大端序列号。两方向使用不同密钥，整条连接的序列号不得重置。
- 仅 responder 展示一次“是否同意连接”，initiator 发起请求即表达本机连接意愿。
- responder 发送加密的 type=8 `connectionDecision`：载荷恰好 1 字节，0 拒绝、1 同意。拒绝或超时关闭连接；只有同意后才暴露文件通道、处理文件或创建暂存文件。
- 同意授权本次连接期间双向、多文件传输，直到断开。重连需重新同意，不保存永久自动接受名单。
- v2 不要求人工核对指纹或 SAS。设备名称由对端提供；签名握手验证密钥持有和握手内容，不声称具有人工带外身份核验。

## 记录与文件

沿用 AES-GCM 的长度前缀、13 字节 AAD header、16 字节 tag、每记录最多 65536 字节明文。限制每帧和内存缓冲，不限制文件的产品大小或总传输时长。

| type | 名称 | v2 载荷 |
| --- | --- | --- |
| 1 | manifest | JSON：`transferID`（UUID）、`fileName`、`size`（非负 64 位大小） |
| 2 | decision | JSON：`transferID`、`accepted`（布尔）；是机器侧文件准备回执，不再弹窗询问用户 |
| 3 | chunk | UUID 原始 16 字节 + offset 的 8 字节大端整数 + 文件字节；总载荷不超过 65536 字节 |
| 4 | receipt | JSON：`transferID`、`sha256`（完整文件摘要，Base64 32 字节） |
| 5 / 6 | 保留 | 不发送；收到则终止连接 |
| 7 | finish | JSON：`transferID`、`sha256`（Base64 32 字节）；结束本文件，不结束连接或方向记录流 |
| 8 | connectionDecision | responder 发给 initiator，1 字节 0/1，仅在连接建立阶段使用 |

- 每个方向同时只发送一个文件，两个方向可以并发传输。只有一个读循环消费底层连接，以 transferID 路由回执与文件；所有写入串行编码并实际发送，保证序列号与网络顺序一致。
- 文件名非空、UTF-8 最多 255 字节，不含路径分隔符、控制字符，不能是 `.` 或 `..`。文件大小可为 0；使用 UInt64 跟踪偏移，磁盘接口使用非负 Int64 表示文件大小，无 100 MB 等人为阈值。
- manifest 不预先携带摘要；发送方一遍读取、一遍发送、一遍累计 SHA-256，finish 再提交最终摘要。
- 接收方检查 ID、连续 offset、块大小和声明长度；finish 后验证总长度与 SHA-256，fsync 并原子落盘，成功后发送 receipt。
- receipt 的 ID 和摘要必须与本次发送匹配；回执前中断不记发送成功。任何文件协议错误、磁盘写入失败或取消均断开当前连接、清理未提交暂存文件。
- 空文件不发送 chunk，直接 finish，校验空内容摘要后正常落盘。

## 时间与资源

连接超时 10 秒，握手总计最多 15 秒，连接同意等待最多 5 分钟。已同意且空闲的连接不因 5 分钟/15 秒而自动失效。文件数据读写保留空闲超时，等待文件准备回执 30 秒、最终落盘回执 5 分钟；没有“文件超过 30 分钟即失败”的总时长限制。

传输文件受真实磁盘容量、文件系统单文件能力和网络状态约束。应用未启用断点续传；取消后重新发送未完成文件。
