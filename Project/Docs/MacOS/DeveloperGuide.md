# macOS developer guide

## Build and test

Run these commands from `Project/MacOS/`:

```sh
swift build
swift test
FEIGE_TEST_BONJOUR=1 swift test --filter DiscoveryTests
swift run
./scripts/package-app.sh
```

## Identity preparation at launch

`TransferModel.start` loads the device identity in the background and calls `DeviceIdentity.validateSigningAccess` for a real sign/verify check. Listening, discovery and connection are enabled only after preparation succeeds. Failure can be retried; shutdown generation checks prevent late results from restarting the listener. Connections reuse the prepared identity and must not call `loadOrCreate` again in `beginConnection`. System authorization should be completed during launch; the application never reads or saves the account password.

Initial connection exchange has a 15-second network timeout. Waiting for the peer's signing proof has a separate five-minute limit. Keep the short timeout regression for older protocol paths; do not turn all waits into unbounded waits.

## Protocol and connection flow

The application implements the shared [Public protocol v3](../Public/ProtocolV3.md), with common interaction requirements in the [UI specification](../Public/UISpecification.md). The public [v3 test vectors](../Public/TestVectors/v3.json) are the cross-platform byte-level oracle. This guide describes macOS-side implementation only; do not treat local implementation coverage as full Windows↔Mac acceptance.

`ConnectionService.establish` verifies signatures, derives keys and gets one approval from the receiving side; it then returns a reusable `FileConnection`. The initiating side does not show an approval prompt.

| Module | macOS implementation responsibility |
| --- | --- |
| `ConnectionService` / `AuthenticatedHandshakeExchange` | v3 signed handshake, system authorization wait and one receiver approval |
| `FileConnection` / `ChatText` | Single read loop, ordered shared write queue, duplex attachments, text and delivery receipts |
| `TemporaryFileSink` | Safe staging, streaming writes, validation and atomic commit without overwrites |
| `TransferModel` / `LANService` | Startup signing preflight and identity cache, Bonjour, one active peer, message/attachment state and local records |
| `ContentView` / `ChatComposer` / `ChatMessageView` | Native conversation UI, IME/newline behavior, background thumbnails, attachment cards and retries |
| `FeigeMacApp` | Foreground activation, single main window, Dock/menu reopen and termination cleanup |

`FileConnection.run` owns the read loop and routes records by file UUID. Both sides can call `sendFile` simultaneously, with writes serialized per direction. Files do not create new connections or trigger per-file approval. Disconnect revokes the approval; reconnect requires approval again.

Each file is read once and streamed while calculating SHA-256. Offsets are UInt64, chunks are about 64 KiB, and encrypted record sequence numbers continue across the connection. Do not restore fixed product limits such as 100 MB or 30 minutes; idle timeouts detect stalled I/O.

Receive settings are read when each manifest arrives. Within an approved connection there is no per-file prompt. Write to a temporary file, validate length and digest at finish, fsync and atomically commit, then send the receipt. Errors must never create false success records.

## Text, images and conversations

`FileConnection.sendText` uses text and receipt record types. `run` sends the receipt only after `didReceiveText` has persisted the message. Text and file writes share an ordered queue, allowing chat during transfers. Text retries reuse the UUID; the receiver deduplicates by device fingerprint and message ID. Native text input preserves IME behavior: Enter sends, Shift+Enter inserts a newline.

Images use `manifest.kind=image` and are sent as originals. ImageIO creates thumbnails with a maximum 640-pixel edge in the background. `messages.json` stores peer fingerprint, direction, type, path and state, retaining up to 1000 finished messages plus active items. Do not trim queued messages during state updates.

## Files and concurrency

`TemporaryFileSink` uses `openat` and `renameatx_np(RENAME_EXCL)` to avoid overwriting files and symlinks. Uncommitted data must be cleaned on disconnect, cancellation, normal exit and errors.

Only one peer connection is active, with one outgoing and one incoming file at a time. Network reads and machine receipts must not depend on UI polling. Network.framework callbacks should recover from actual connection cancellation; continuations waiting for user approval must resume on cancellation/timeout and be bound to a request ID.

The app is not sandboxed. If App Sandbox is introduced, add security-scoped bookmarks for receive directories. Tests use isolated temporary configuration and random-label Keychain identities; auto-approval exists only in tests.

## Packaging and compatibility

The local preview package script outputs to `Release/MacOS/<version>/` and uses canonical icon resources from `Project/Ui/Branding/FeigeIconV1/`. Ad-hoc signing is for local preview only. Developer ID signing and notarization are enabled only when `FEIGE_SIGN_IDENTITY` and `FEIGE_NOTARY_PROFILE` are explicitly supplied.

Protocol v3 is incompatible with v1/v2 clients. Local TCP and model tests do not replace the full physical-device interoperability and native UI acceptance matrix.
