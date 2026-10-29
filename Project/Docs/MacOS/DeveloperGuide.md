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

`TransferModel.start` loads the device identity in the background and calls `DeviceIdentity.validateSigningAccess` for a real sign/verify check. Listening, discovery and connection are enabled only after preparation succeeds. Failure can be retried; shutdown generation checks prevent late results from restarting the listener. Connections reuse the prepared identity and must not call `loadOrCreate` again in `beginConnection`.

Identity storage is `EncryptedIdentityStore` (`Identity/Sources/EncryptedIdentityStore.swift`): the RSA-3072 private key is kept as PKCS#1 DER encrypted with AES-256-GCM in `Application Support/FeigeTransfer/Identity/identity.pkcs1.bin` (mode 600), and the 32-byte wrapping key is a Keychain *generic-password* item (`kSecClassGenericPassword`, `AfterFirstUnlockThisDeviceOnly`).

**Do not store the private key as `kSecClassKey` again, and do not "fix" this by adding ACL prompts.** Keychain key items are governed by an access control list; unless the process has a stable Apple-signed identity macOS raises the "wants to use your confidential information stored in Keychain" password prompt, and an ad-hoc signature changes identity on every rebuild, so the prompt returns after each update. Generic-password items use the data-protection path and do not prompt. Trade-off: the key is exportable by processes running as the same user, which is acceptable because the device identity only authenticates LAN peers to the user's own devices — it never signs software or protects data at rest. Release signing and update signing use separate certificates.

`EncryptedIdentityStore.migrateLegacyKeyIfNeeded()` runs once before the first `loadOrCreate` on an upgraded install: it adopts the old Keychain key so the fingerprint stays the same, then deletes the old item. Never regenerate the identity on a read failure — throw, keep the encrypted file, and let the user reset deliberately.

`scripts/package-app.sh` selects a stable signing certificate automatically (`Developer ID Application`, then `Apple Distribution`, or `FEIGE_SIGN_IDENTITY`) and only falls back to ad-hoc with a loud warning, because ad-hoc signing makes the app a "different program" after each update.

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

## Settings, guidance and notifications (0.4.0)

`SetupView` serves both the two-page first-run guide and the preferences window. The preferences window is a scroll of `Card`s mirroring the Windows settings page: device name, receive folder, local connection info, version and updates, notifications, local records, hidden conversations, identity and application run. `SettingsError`/persistence rules are unchanged: never publish a value before it is written.

`DeviceSettings` declares an explicit `init(from:)`. Swift's synthesized decoding requires every stored property, so adding a property without a decoder would make every pre-existing `settings.json` fail to load and silently fall back to defaults. Keep new preferences optional or defaulted through `decodeIfPresent`.

`GuidancePanel` renders closable tips equivalent to the Windows InfoBars (authorization, legacy data, first connection, first transfer, notification invitation, background run, update and result, errors). `CloseObserver` forwards `windowWillClose` only for a user-visible close; update handoff and ⌘Q must not record the "still running" tip.

`TransferNotifier` wraps UserNotifications. `UNUserNotificationCenter.current()` raises an Objective-C exception outside an application bundle (XCTest, plain command-line binaries), so `TransferNotifier.isSupported` guards every call. Notifications are optional: a refusal or unavailable environment must only change status text. Tests construct models with `makeUpdates: false` when they do not exercise the updater.

## Application updates (0.4.0)

`UpdateController` owns the whole flow: silent automatic checks (at most every 24 hours, failures count toward the interval), user-confirmed download, size and SHA-256 verification, user-confirmed install, waiting for active transfers (`TransferModel.hasActiveWork`; an idle connection never blocks), code-signature verification and same-directory bundle replacement. Automatic checks never download, install or open a dialog.

- Manifest: `<base>/macos/<channel>/feige-macos-update.json`, envelope `format/keyId/payload/signature`, signed bytes `Feige.Update.v1\n<keyId>\n` + payload. The envelope is shared with Windows; the P-256/Ed25519 verification (`UpdateSignature`, CryptoKit) and the package format (`.zip` app archive) are macOS specific. Releases carry `platform=macos`, `installation=macos`.
- Trust root: `Resources/Updates/UpdateSource.json` inside the signed bundle. Keep it in `Project/MacOS/Updates/`; the repository file is an unconfigured placeholder, so the client stays offline until a key and origin are set. Production origins must be HTTPS; plain HTTP is accepted only for `127.0.0.1`/`localhost` for local rehearsal.
- State: `Updates/update-state.json` (interval, highest seen build, prompt and skip state, pending release). Downloads are stored under `Updates/Downloads/`. A release restored at launch is re-verified by SHA-256 before it counts as installable.
- Install: verify the current and new bundles with `SecStaticCodeCheckValidity`, stage with `ditto`, then rename in place with rollback. A non-`.app` build refuses to install. `scripts/sign-macos-manifest.py` signs payloads; the private key never enters the repository or `Release/`.

Do not weaken these checks to make a rehearsal pass; use loopback origins instead.

## Packaging and compatibility

The local preview package script outputs to `Release/MacOS/<version>/` and uses canonical icon resources from `Project/Ui/Branding/FeigeIconV1/`. Ad-hoc signing is for local preview only. Developer ID signing and notarization are enabled only when `FEIGE_SIGN_IDENTITY` and `FEIGE_NOTARY_PROFILE` are explicitly supplied.

Protocol v3 is incompatible with v1/v2 clients. Local TCP and model tests do not replace the full physical-device interoperability and native UI acceptance matrix.
