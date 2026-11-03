# Feige Protocol v6 — resumable transfers and folders

Protocol v6 extends [Protocol v5](ProtocolV5.md) for direct (one-to-one) file transfer. It also defines the unauthenticated unicast probe used for discovery across subnets, which is independent of the protocol version. Product behaviour is described in the [collaboration enhancement plan](CollaborationEnhancementPlan.md).

Status: implemented on both platforms; real Windows↔macOS LAN acceptance is pending.

## 1. Negotiation

- Discovery advertises `caps=3,4,5,6`. The `v` value stays unchanged, so older clients keep their existing selection and ignore capability 6.
- An initiator selects v6 only when the peer advertises it. A failed v6 handshake is never silently retried with another version.
- The handshake is the v5 handshake with `Offer.version = 6`, transcript domain `FEIGE-HANDSHAKE-V6\0`, and HKDF labels `feige-v6 initiator-to-responder` and `feige-v6 responder-to-initiator`. See the [shared vector](TestVectors/v6-handshake.json).
- Every v4 and v5 record keeps its meaning on a v6 channel. Features gated on "v5" mean "v5 or later".

## 2. Manifest (record type 1)

A v6 manifest may add two optional fields. Absent fields mean the v5 behaviour.

```json
{
  "transferID": "UUID",
  "fileName": "a.jpg",
  "size": 1024,
  "kind": "file",
  "resumeID": "UUID",
  "folder": {
    "rootName": "Photos",
    "relativePath": "2026/a.jpg",
    "folderID": "UUID",
    "fileCount": 12,
    "totalBytes": 123456
  }
}
```

- `resumeID` is chosen by the sender and must stay the same while the file's path, size, and modification time stay the same, and change otherwise. It must not reveal the path; implementations hash it.
- `folderID` is stable across retries of the same local folder and must not reveal the path.
- All files of one folder send share one `transferID`, are sent strictly in sequence, and share `rootName`, `folderID`, `fileCount`, and `totalBytes`.
- `relativePath` uses `/`, is at most 1024 UTF-8 bytes and 32 components, and its last component equals `fileName`. Every component passes the `fileName` rules: not empty, not `.` or `..`, no `/`, `\`, or control characters, at most 255 bytes. `rootName` passes the same rules.
- `fileCount` is 1–10000. The receiver counts files and bytes it accepts per `folderID` on this connection and closes the connection when a manifest exceeds the declared `fileCount` or `totalBytes`, or changes any shared field.

## 3. Decision (record type 2)

```json
{ "transferID": "UUID", "accepted": true, "resumeOffset": 4096, "alreadyComplete": false }
```

- `resumeOffset` defaults to 0 and must not exceed `size`. A larger value is a protocol error.
- `alreadyComplete: true` is valid only with a `folder` manifest: the receiver already has this exact file from an earlier attempt. The sender records it as done and sends no chunk, finish, or receipt for it.
- The receiver asks the user once per `folderID`, on its first file. Later manifests of that folder on the same connection are accepted without another prompt, within the declared limits. A rejected folder ends the connection, as a rejected file does.

## 4. Chunks, finish, and receipt

- The first chunk offset equals `resumeOffset`; offsets stay strictly contiguous. When `resumeOffset == size`, no chunk is sent.
- The sender reads and hashes the first `resumeOffset` bytes without sending them. `finish` always carries the SHA-256 of the whole file.
- The receiver hashes its retained prefix before accepting chunks. On a digest mismatch it deletes the partial file and closes the connection; the next attempt starts at 0.
- A receipt means the complete file was verified and durably moved to its final name, as in v3.

## 5. Receiver storage

- Partial data for a manifest with `resumeID` lives in the receive directory as `.feige-resume-<key>.part`, where `<key>` is the first 32 lowercase hex characters of SHA-256 over the normalized peer fingerprint, a NUL byte, and the lowercase `resumeID`. The peer's authenticated identity is part of the key, so another device cannot extend the file.
- A partial file longer than `size`, or one that is not a regular file, is discarded. Partial files are kept across disconnects and removed at startup after 7 days.
- Folder progress lives beside it in `.feige-folder-<key>.state`, keyed the same way with `folderID`. The first line is the created root directory name; each later line is the `resumeID` of a completed file. It is removed when the folder completes or after 7 days.
- A new folder creates a new root directory (`rootName`, then `rootName (1)`, …) and never merges into an existing one. Intermediate directories are created below that root and symbolic links or reparse points are never followed. Final names never overwrite existing files.
- Windows maps components that are invalid on Windows to writable names. Collisions after mapping use the numbered suffix.

## 6. Unicast probe (UDP 52526)

The probe finds a Feige device at a user-configured address. It is unauthenticated: replies are treated exactly like mDNS TXT data and never authorize anything.

Each datagram is one UTF-8 JSON object. Requests are padded with trailing NUL bytes to exactly 512 bytes. Replies are at most 512 bytes and are dropped rather than truncated if larger.

```json
{ "feige": "probe", "v": 1, "nonce": "<16 random bytes, base64>" }
```

```json
{ "feige": "reply", "v": 1, "nonce": "<echoed>", "name": "Mac-A", "fp": "<64 hex>",
  "caps": "3,4,5,6", "port": 52000, "avatar": "dove" }
```

- `port` is the TCP port for v4 and later. Probe peers always speak v4 or later, so there is no v3 port. `avatar` is optional.
- A receiver ignores malformed datagrams, its own `fp`, replies whose nonce it did not send, and any datagram whose source is not a private address.
- A responder answers only requests of exactly 512 bytes from private sources, at most 100 per second overall.

## 7. Verification

- Match the shared v6 vector on both platforms and keep the v3, v4, and v5 vectors unchanged.
- Test resume from a partial file, digest mismatch recovery, oversized partials, `alreadyComplete`, declared limit violations, path traversal, symbolic links, and non-overwrite on both platforms.
- Run the Windows↔macOS LAN acceptance matrix before claiming v6 interoperability.
