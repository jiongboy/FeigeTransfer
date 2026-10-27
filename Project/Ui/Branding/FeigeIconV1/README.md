# FeigeTransfer Approved Icon · v1

The user approved this icon on 2026-10-23 for both Windows and macOS. It depicts a white dove and folded paper on a blue-to-cyan gradient rounded background. Preserve the transparent margin, proportions and original background; do not add another backing plate.

![FeigeTransfer icon](Feige-master.png)

## Canonical assets

| File | Purpose |
| --- | --- |
| `Feige-master.png` | 1254 × 1254 RGBA source image |
| `Feige.ico` | Windows multi-size icon |
| `Feige.icns` | macOS icon with standard and Retina sizes |
| `generation-prompt.txt` | Original image-generation prompt |
| `SHA256SUMS.txt` | Checksums for canonical assets |

These source assets belong in version control under `Project/Ui/Branding/FeigeIconV1/`. Never use files from `Release/` as build inputs.

## Windows resources

The Windows application uses `Project/Windows/src/Feige.App/Assets/AppIcon.ico` for the application, window and installer. MSIX PNG assets must be derived from the same master image and must not revert to template placeholders.

## macOS packaging

Before signing the macOS app, copy `Feige.icns` into `Contents/Resources/Feige.icns` and set the app Info.plist key `CFBundleIconFile` to `Feige.icns`. Source `Info.plist` and package scripts should retain the same setting and resource.

The canonical paths are relative to the repository root:

```sh
Project/Ui/Branding/FeigeIconV1/Feige.icns
Project/MacOS/Resources/Info.plist
Project/MacOS/scripts/package-app.sh
```

## Verification

Verify the icon in the built app bundle and, on target systems, in Finder, Dock, taskbar, shortcuts and installer. The Windows application icon must hash identically to `Feige.ico`; Windows shell assets should be checked for dimensions and transparency.
