# FeigeTransfer public development rules

This document is the canonical source for shared product behavior, security requirements and collaboration rules. Windows, macOS and public documentation are maintained together on the `main` branch. Historical milestones and test records remain explicitly labeled as historical; they do not imply current acceptance.

## 1. Platforms and project documentation

| Platform | Source directory | Technology | Documentation |
| --- | --- | --- | --- |
| macOS | `Project/MacOS/` on `main` | SwiftUI / Network.framework | [macOS project](../MacOS/ProjectOverview.md), [development log](../MacOS/DevelopmentLog.md) |
| Windows | `Project/Windows/` on `main` | WinUI 3 / Windows App SDK | [Windows project](../Windows/ProjectOverview.md), [development log](../Windows/DevelopmentLog.md) |

The current shared protocol target is [protocol v3](ProtocolV3.md), with interaction requirements in [the UI specification](UISpecification.md). Protocol v1 documents and vectors are historical references. macOS and Windows source both use v3; users have reported basic Windows↔Mac interoperability, but full device/system acceptance is not complete. Do not describe it as fully accepted before that matrix passes.

## 2. Product and consent

- Prepare the local device identity and validate signing access before enabling discovery, listening or connections. If preparation fails, provide retry without blocking the UI.
- The receiver explicitly approves one incoming connection. That approval authorizes text, images and files in that live connection. Disconnect revokes approval; reconnect requires a new approval.
- Do not create receive files or process application messages before consent. Machine-level readiness acknowledgements are not extra user approval.
- There is no pairing password, SAS re-entry or per-file approval flow. Without an out-of-band SAS confirmation, do not claim resistance to a malicious device impersonating a displayed name.
- Do not impose product-level single-file size, batch-count or total-transfer-time limits. Support empty files; retain filesystem, disk, frame memory, concurrency and idle-I/O bounds. An 8192-byte text limit is a message-frame limit, not an attachment limit.
- System credential authorization is distinct from peer consent. Never store login passwords, export private identity keys or bypass operating-system security controls.

## 3. Communication and security

- Protocol v3 uses `Offer.version=3` and an empty session code. RSA-PSS signatures authenticate the full transcript. X25519 ephemeral keys and HKDF-SHA256 derive directional AES-256-GCM keys.
- GCM nonces are 12 bytes. Length prefixes, 13-byte AAD, strict directional sequence numbers, UUID/offset/digest fields and type payloads must follow [the v3 protocol](ProtocolV3.md). Reject incompatible versions; never silently downgrade.
- Reuse an approved connection for multiple files and text messages. Use one read loop and ordered writes. A transfer finish ends a file, not the live connection. A new connection creates new ephemeral keys and sequence spaces.
- Strictly validate versions, types, lengths, enums, paths, identifiers, offsets, digests and receipts. Invalid messages close the connection and clean uncommitted data; do not attempt permissive recovery.
- Preserve bytes across TCP fragmentation/coalescing and handshake/record boundaries. Do not allocate large buffers based on unvalidated lengths.
- Discovery data is untrusted. Validate the actual target as an allowed LAN address; reject public redirects and arbitrary destinations.
- Update shared specifications first when changing protocol fields; increment incompatible protocol versions and synchronize both clients and public vectors.

## 4. Files, messages and local records

- Stream each file once for reading, sending, receiving, writing and SHA-256 calculation. Do not preload entire files. Preserve original images; generate bounded thumbnails in the background.
- Prevent path traversal, symlink attacks and platform-invalid paths. Never overwrite an existing target. Acknowledge and record success only after digest/size validation, flush and atomic commit.
- Mark text delivered only after the receiver persists it and returns a matching UUID receipt; delivery is not read status. Deduplicate text retries. File retries are not resumable and do not provide exactly-once guarantees.
- Disconnect, failure and cancellation must never create false success. Keep committed files; remove uncommitted temporary files. If cancelling an attachment closes the live connection, present that consistently on both clients.
- Group conversations by full identity fingerprint, not displayed name. Only one active peer is supported; viewing another conversation must not misroute a message. Retention must keep active queued items.
- Logs must not contain login passwords, message/file contents, private key material or sensitive full paths. Local conversation data and diagnostic logs have distinct retention/protection needs. There is no cloud sync or offline delivery.

## 5. UI and window behavior

The desired first-run path is in [the onboarding plan](FirstRunOnboarding.md): persist local settings first, then prepare identity and networking. Let users defer; do not require a second device or notification permission to enter the main screen. Closing before onboarding completes stops initialization; upgrades must preserve existing identity, history and receive location.

Use device conversations: peer list/discovery on the left and text/image/file messages plus composer on the right. Use a white/light-blue selected sidebar in light mode and follow system appearance in dark mode. Native UI must preserve IME behavior, copy/paste, Enter to send and Shift+Enter for newline.

Closing the main window keeps the app running with a taskbar/Dock reopen affordance. Reopen should reuse the main window; quitting ends connections. Persist settings before publishing them to the UI and allow retry after write errors. See the [UI specification](UISpecification.md) for shared state and acceptance requirements. Mock progress, speed and success displays must not be presented as real transport capabilities.

## 6. Verification, packaging and release

The shared update behavior is defined in [the update policy](UpdatePolicy.md); it does not imply in-app updating is already implemented. For changes, complete code and necessary build/test/runtime verification first, then report results for user review. Only create release packages after the user confirms and explicitly requests packaging.

All Windows and macOS deliverables go to repository-adjacent `Release/`, which is excluded from Git. Scripts locate it from the repository root. Build caches may stay in platform build folders. Canonical icon sources live in `Project/Ui/Branding/FeigeIconV1/`.

| Area | Required coverage |
| --- | --- |
| Public vectors | Both implementations independently reproduce v3 transcript, signatures, HKDF, GCM, UUIDs and digests; retain v1 vectors separately. |
| Interoperability | Mac→Windows and Windows→Mac discovery, one-time consent, text, original images, empty/large files and multiple attachments. |
| Concurrency/reconnect | Simultaneous duplex transfers with chat; reconnect requires consent and creates new ephemeral keys/sequences. |
| Failure behavior | Reject, cancel, disconnect, timeout, malformed IDs/offsets/digests, disk-full, revoked permissions, duplicate names and traversal. |
| Native UI | Credential prompts, IME, drag/drop, preview/open, conversation isolation, appearance, DPI and window reopen. |
| Special environments | Firewall, LAN permissions, multiple interfaces, VPN/proxy, isolated Wi-Fi and sleep/resume. |
| Distribution | macOS Developer ID notarization / Windows trusted-signed EXE; separately verify MSIX if retained; clean-machine install/upgrade/uninstall. |

Ad-hoc macOS preview packages are not notarized releases. Local TCP/model/window tests do not replace physical-device interoperability and native UI acceptance. Keep verified results, user feedback and pending acceptance clearly separated; see the [shared update log](UpdateLog.md).

## 7. Repository and commit responsibilities

- Windows, macOS and shared documents use this repository's `main` branch; platform sources live in `Project/Windows/` and `Project/MacOS/`.
- Do not create long-lived Windows/macOS branches. Large isolated changes may use short-lived feature branches and return to `main` after verification.
- Before every `git commit`, summarize the intended contents and obtain the user's explicit approval. Never commit without approval.
