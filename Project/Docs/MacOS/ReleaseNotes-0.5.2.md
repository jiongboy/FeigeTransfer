# macOS 0.5.2 发布说明

状态：**已发布**（2026-11-03，build 18，已公证）。含文件大小与 SHA-256 的定稿在 `Release/MacOS/0.5.2/ReleaseNotes-0.5.2.md`，公开页面为 [GitHub macos-v0.5.2](https://github.com/jiongboy/FeigeTransfer/releases/tag/macos-v0.5.2)。逐轮实现与验证见 [macOS 开发日志](DevelopmentLog.md)，跨平台摘要见[更新日志](../Public/UpdateLog.md)。

## 修复

- **对方离线时弹框**：正在对话的设备离线或断开时，会弹出“飞鸽快传”提示框，需要点掉。现在只更新会话状态，未完成的消息标为“连接已断开”；发文件时断开也只在该条消息上显示失败。文件读取失败、对方拒收等其他错误仍照常提示。

## 兼容性

- 最低系统版本 macOS 13.0；协议版本不变（v6），与 Windows 1.3.0 及以上版本可使用全部功能。

## 交付文件

| 文件 | 用途 |
| --- | --- |
| `飞鸽快传.app` | 已签名并公证的应用 |
| `FeigeMac-0.5.2.zip` | 分发与应用内更新用压缩包 |

## 发布前检查

- [x] 从已提交的代码构建（`4ad7245`）
- [x] Developer ID 签名、公证并 staple
- [ ] 对方断开时不再弹框
- [ ] 从 0.5.1 升级
