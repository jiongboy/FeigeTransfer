# Communication protocol overview

This is the entry point to the shared FeigeTransfer wire protocol. Wire versions are independent of application versions; incompatible peers must be rejected rather than silently downgraded.

- [Protocol v3](ProtocolV3.md): current shared protocol for one connection approval, reusable duplex transfers, text and image messages.
- [Protocol v1 — historical](ProtocolV1-Historical.md): previous handshake, framing, transfer semantics and vectors. Retained for compatibility analysis only.
- [Public v3 test vectors](TestVectors/v3.json): cross-platform byte-level validation fixtures.
- [macOS v2 notes](../MacOS/ConnectionProtocolV2-Historical.md): historical intermediate implementation and compatibility notes.

Protocol v3 is the intended Windows/macOS common baseline. Basic interoperability feedback has been reported, but the full device and system acceptance matrix is still pending.
