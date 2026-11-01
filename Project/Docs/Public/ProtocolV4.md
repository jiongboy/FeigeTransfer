# Communication protocol v4 — multi-user conversations

Status: shared V4 group protocol, Windows/macOS implementations, canonical vectors, and cross-platform group text/file interoperability are implemented. V4 remains the group baseline for peers without V5; safe durable cancellation is defined separately in [ProtocolV5.md](ProtocolV5.md). Remaining platform and LAN acceptance gaps are tracked in [MultiUserConversationPlan.md](MultiUserConversationPlan.md).

## 1. Compatibility and discovery

- v4 is wire-incompatible with v3. A v4 connection uses `Offer.version=4`, the `FEIGE-HANDSHAKE-V4` transcript domain, and a v4-specific HKDF label. Both sides must select exactly the same version; a mismatch is rejected.
- New clients advertise Bonjour TXT `version=3` for old-browser visibility and `capabilities=3,4` for new-client capability selection. The TXT record is untrusted discovery metadata and never authorizes a connection.
- A new client chooses v4 when the target advertises capability 4. A target that advertises only v3 gets an explicitly labeled v3 direct conversation. A failed v4 handshake never retries v3 automatically.
- A responder accepts the version in the authenticated initiator offer. v3 connections retain v3 semantics and cannot carry group events. The UI labels v3 sessions and disables group actions there.
- Group creation, membership control, group messages, and group file envelopes require v4 on every participating connection. A v3 member is shown as requiring an upgrade.

## 2. Identity and limits

- The authenticated peer fingerprint is SHA-256 of the peer's complete DER public identity. Names and Bonjour metadata are display hints only.
- A group has a random UUID, a normalized display name, one owner fingerprint, a monotonically increasing membership epoch, and an ordered roster of at most 20 devices including the local device.
- The group owner is the only authority allowed to propose or commit membership changes. The sender fingerprint on a group message must equal the authenticated fingerprint on that peer connection and must occur in the current roster.
- A peer connection may serve a direct conversation and one or more groups. The connection registry is keyed by full fingerprint and owns one reader and one ordered writer per live connection.
- The planned maximum is 20 direct peers plus up to 19 additional group-only peers, for at most 39 remote connections. A peer shared by a direct conversation and a group uses one connection.

## 3. Group lifecycle

All group control messages travel over a live, v4, mutually authenticated, consented peer connection. Only the owner may send owner control events. A receiver checks the authenticated connection fingerprint against the stored owner fingerprint before accepting an owner event.

All group control messages carry `version`, `kind`, `eventID`, `groupID`, `ownerFingerprint`, and `epoch`; event-specific fields use the exact casing shown below. Unknown event kinds, unknown fields, stale epochs, and sender/owner mismatches close the offending v4 connection. A repeated prepare or commit matching the current pending or committed roster is acknowledged again; conflicting contents are rejected.

The control event kinds are:

- `invite`: owner to a target device; carries `targetFingerprint`, `name`, and the proposed full `roster` in addition to common fields. The invitee must explicitly accept or decline in the UI. Connection consent alone does not join a group.
- `inviteReply`: invitee to owner; carries `targetFingerprint` and `accepted` in addition to common fields.
- `membershipPrepare`: owner proposes a full roster snapshot at `epoch = baseEpoch + 1`; contains `groupID`, `eventID`, `baseEpoch`, `epoch`, `targetFingerprint`, and roster. Initial group creation uses epoch 0 without a base epoch. An owner handoff also carries `newOwnerFingerprint`; it must identify a member in the proposed roster, while `ownerFingerprint` remains the current authenticated owner proposing the change.
- `membershipPrepareReply`: current roster member confirms that the proposal is valid and that it is ready to commit; contains `groupID`, `eventID`, `epoch`, and `accepted`.
- `membershipCommit`: owner commits the prepared full roster snapshot at the proposed `epoch`; for an owner handoff it repeats the exact `newOwnerFingerprint` from prepare.
- `membershipCommitReply`: member confirms that the committed snapshot was persisted.
- `membershipAbort`: reserved for compatibility but unsupported on a v4 channel. V4 peers cannot prove that every participant cleared a pending prepare; durable cancellation and its recipient-bound acknowledgment require the authenticated v5 extension in [ProtocolV5.md](ProtocolV5.md). Never send a one-way abort as evidence of cancellation.
- `leave`: a non-owner member requests to leave the current group by sending the request to its authenticated owner; the owner may approve it by preparing a roster without that member.
- `leaveReply`: owner confirms or declines the request after recording the corresponding membership decision; an accepted reply may precede final commit while the durable transaction remains pending.

Membership changes are serialized. The owner may begin a change only when all current members are connected. Group sending is paused while a change is pending. Before any invite or prepare is sent, macOS archives the exact invite/prepare controls and proposed roster; after restart or a required-peer reconnect it replays those controls with the same event IDs, and the sidebar offers explicit retry. Windows also retains its durable preparing transaction and exposes explicit retry. Both platforms persist the commit stage before sending commit and replay that exact commit after reconnect or restart. A decline or disconnect during prepare leaves the transaction pending; V4 itself has no safe cancellation; V5 defines the negotiated durable abort/ack extension in [ProtocolV5.md](ProtocolV5.md). After commit may have started, the transaction is irreversible and the owner must not send abort. Receivers reject group messages while a membership transition is pending or when the message epoch differs from the committed epoch.

When the owner chooses to leave, the client selects one remaining roster member with the system random source and sends that member as `newOwnerFingerprint`. The old owner is removed from the proposed roster. Every remaining member must prepare and persist the same owner/roster transition before commit. Until all commit acknowledgements arrive, the old owner retains a durable pending commit and retries it after reconnect or restart. If no other member remains, the active group is removed locally; existing local message history is retained. An owner who becomes permanently unavailable without a committed handoff leaves the last committed group read-only; members are never promoted automatically.

New members establish separately consented v4 mesh connections to the remaining roster members after accepting the invite. If any required connection is unavailable or declined, that member remains pending and is not included in a committed roster.

## 4. Group message envelope and acknowledgement

Group control, `groupText`, and `groupTextAck` payloads use canonical UTF-8 JSON: object keys are sorted lexicographically at every level, output is compact with no insignificant whitespace, `/` is not escaped, Unicode characters are emitted as UTF-8, and absent optional values are omitted. Dates are integer milliseconds since 1970-01-01T00:00:00Z. UUID strings use lowercase canonical form; integer fields use base-10 notation. Arrays, including rosters, retain their protocol order. These rules define the payload bytes before encryption and are covered by [v4.json](TestVectors/v4.json). Every group operation is sent independently over each recipient's authenticated connection; recipients do not relay messages. The stable group text message identity is `(groupID, senderFingerprint, id)`.

The Swift model property names below are the wire names and preserve their exact capitalization:

- `groupText` carries required `id`, `groupID`, `senderFingerprint`, `memberRevision`, `date`, and `kind`; text messages additionally carry `text`. The optional `fileMetadata` is used for image/file kinds. `date` is milliseconds since the Unix epoch.
- `groupTextAck` carries required `groupID`, `messageID`, `epoch`, and `accepted`, plus optional `reason` for a rejection. The receiver persists and deduplicates the message before acknowledging it. A retry with the same key never creates a second bubble.
- A receiver requires the sender to match the authenticated connection fingerprint, to be a current group member, and to use the exact committed epoch.
- The sender tracks each recipient independently as `pending`, `delivered`, `unreachable`, `declined`, or `failed`, with a bounded reason for terminal failures. Only a matching acknowledgement records delivery. Group status distinguishes all delivered, partial delivery, and none delivered. Retries target selected failed recipients only.

## 5. Group images and files

A group file uses a stable group message ID and a unique transfer ID for each recipient. Each recipient transfer is independent and uses the existing streaming, SHA-256, temporary-file, atomic-commit, and receipt rules. The current Swift property names below are the exact wire names:

- `groupManifest` (`type 14`) carries `groupID`, `messageID`, `senderFingerprint`, `memberRevision`, nested `metadata` (`transferID`, `fileName`, `size`, `kind`), and `sha256`. `sha256` is a base64 JSON string produced from the 32-byte digest. The authenticated sender and current committed membership revision are checked before showing the file prompt.
- `groupFileDecision` (`type 15`) carries `groupID`, `transferID`, and `accepted`; a declined decision also carries a bounded `reason`. Declining one copy does not close the v4 connection and does not affect other recipients.
- Chunks and finish records are bound to the current per-recipient `metadata.transferID`; the receiver rejects a transfer whose group context is missing, mismatched, or stale.
- `groupFileReceipt` (`type 16`) carries `groupID`, `transferID`, and the verified base64 `sha256`. It is sent only after the received file has passed size/digest checks and was atomically committed.
- The sender displays approval wait, progress, delivery, decline, and failure per member. Manual retries create a new per-recipient transfer ID and do not resend to members already marked delivered.

## 6. V3 isolation

V3 types and close-on-rejection behavior remain exactly as specified in [ProtocolV3.md](ProtocolV3.md). A v3 channel accepts only v3 message types. A v4 channel accepts the v3 direct file/text operations plus the v4 group types defined here. A v3 frame can never be interpreted as a group event, and group capability is never inferred from a peer name or TXT record alone.

## 7. Implementation and verification

- macOS and Windows contain v4 group implementations in this branch. Windows Core/store and app-service loopback tests, shared canonical vectors, and macOS v4 group tests pass. Windows UI member-management click-through, V5 cancellation interoperability over real Windows↔macOS LAN, and the full failure/restart matrix remain pending.
- The independent [v4-handshake.json](TestVectors/v4-handshake.json) fixture pins v4 transcript bytes, SHA-256 digest, and both `feige-v4` HKDF directional outputs using fixed offers and RFC 7748 section 6.1 shared-secret data; the [v4.json](TestVectors/v4.json) fixture pins group control/text/ack JSON bytes. No private keys or signatures are needed for these primitive vectors.
- Verify exact-version handshake rejection, unknown type rejection, owner-only membership changes, random owner handoff and replay after restart, epoch/replay rejection, per-recipient text receipts, independent file decisions, refusal without channel closure in v4, and no accidental v3 fallback.
- Exercise the 20-member / 39-remote-connection limits and verify that one peer failure never interrupts other peers.
