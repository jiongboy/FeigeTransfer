# Communication protocol overview

This is the entry point to the shared FeigeTransfer wire protocol. Wire versions are independent of application versions; incompatible peers must be rejected rather than silently downgraded.

- [Protocol v6 — resumable transfers, folders, and unicast probe](ProtocolV6.md): direct-transfer extension to v5; real Windows↔macOS acceptance pending.
- [Protocol v5 — durable membership cancellation](ProtocolV5.md): authenticated extension to v4; implementation is present on both platforms, with real Windows↔macOS cancellation acceptance pending.
- [Protocol v4 — multi-user conversations](ProtocolV4.md): shared group contract and backward-compatible baseline for clients without V5 cancellation.
- [Protocol v3](ProtocolV3.md): current shared protocol for one connection approval, reusable duplex transfers, text and image messages.
- [Protocol v1 — historical](ProtocolV1-Historical.md): previous handshake, framing, transfer semantics and vectors. Retained for compatibility analysis only.
- [Public v3 test vectors](TestVectors/v3.json): cross-platform byte-level validation fixtures.
- [macOS v2 notes](../MacOS/ConnectionProtocolV2-Historical.md): historical intermediate implementation and compatibility notes.

Protocol v3 remains the fallback one-to-one baseline for older compatible peers. V4 group features are available only when selected by both clients; V5 is required for safe durable cancellation. V6 is required for resumable transfer and folder sending; v5 peers keep ordinary file transfer. Incompatible peers are never silently downgraded after a failed handshake.
