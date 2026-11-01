# Communication protocol v5 — durable membership cancellation

Status: implemented in the Windows and macOS branches as an authenticated extension to the v4 group protocol. The v4 offer, transcript bytes, HKDF labels, group control types, and existing v4 vectors remain unchanged; v5 has a separately versioned handshake vector. Cross-platform v5 group cancellation over a real Windows↔macOS LAN still requires acceptance testing.

## 1. Version negotiation

- A v5 connection uses `Offer.version=5`, the `FEIGE-HANDSHAKE-V5\0` transcript domain, and v5-specific directional HKDF labels. The authenticated transcript and derived keys therefore bind the negotiated version.
- Bonjour capability values may advertise `3,4,5`. They are discovery hints only; the authenticated offer and handshake determine the connection version.
- When both peers advertise v5, clients prefer v5. V4 remains available for group conversations with older clients. A failed v5 handshake does not silently retry v4 or v3.
- V4 transcript bytes and the `feige-v4` key derivation are frozen by the existing v4 handshake vector. V5-only controls are rejected on v4 connections.

## 2. Durable pre-commit cancellation

Cancellation is available only while a membership operation is in `Preparing`, before the owner persists or sends any commit. Once commit may have started, the operation is irrevocable and cancellation is refused.

The owner performs cancellation as follows:

1. Build one recipient-bound `membershipAbort` control for every original prepare target. Each control repeats that target's prepare `eventID`, `groupID`, owner, `baseEpoch`, and proposed `epoch`, and includes the target's full fingerprint.
2. Persist the owner abort intent and the complete target list before sending any abort.
3. Send only over authenticated v5 channels. Persist each positive `membershipAbortReply` before treating that target as acknowledged.
4. Retry unacknowledged targets after timeout, reconnection, and application restart. Remove the pending operation only after every original target has durably accepted the abort.

A v4 channel cannot acknowledge cancellation. The owner keeps the intent pending until the original targets can reconnect over v5.

## 3. Receiver decision and replay

- A v5 receiver authenticates that the sender is the stated owner and that the control targets the local fingerprint.
- Before acknowledging acceptance, the receiver writes a tombstone keyed by the event ID and clears only state matching that exact transaction. Failure to persist produces no reply.
- A duplicate abort replays the same durable decision. An accepted tombstone rejects a later prepare or commit for that transaction.
- If the receiver has already committed the proposed epoch or a later epoch, it persists and returns `accepted=false`; it never rolls committed membership back.
- The owner records a positive reply durably before waking the cancellation task. Per-target event IDs are preserved, including initial invitations where each recipient has its own event ID.
- A receiver that has durably stored a prepare retains it when the mesh is temporarily unavailable and responds negatively. The prepare remains recoverable so that the owner can safely initiate cancellation or retry later.

## 4. Compatibility boundaries

V4 remains the group conversation contract for clients without durable cancellation. V4 peers may prepare and commit membership, but they cannot participate in the v5 abort handshake. V5 does not change group text, file, or acknowledgement payloads. Unknown v4 controls continue to be rejected as required by [ProtocolV4.md](ProtocolV4.md).

## 5. Verification

- Keep the exact V4 transcript and directional HKDF vector unchanged, and match the [shared V5 transcript and directional HKDF vector](TestVectors/v5-handshake.json) in both implementations.
- Test v5 authenticated negotiation, v4 refusal of abort controls, durable owner intent and recipient acknowledgements, receiver tombstone replay, abort-after-commit refusal, prepare-after-abort rejection, and restart recovery.
- Run the cross-platform v5 LAN acceptance matrix before claiming Windows↔macOS cancellation interoperability.
