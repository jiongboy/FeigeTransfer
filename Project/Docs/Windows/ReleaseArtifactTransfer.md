# Windows release artifact transfer (historical workflow)

This document records the older ROG-to-Mac-mini release transfer workflow. The current local artifact destination is `Release/`, which is outside `Project/` and excluded from Git. Never stage or commit release packages without explicit user authorization; project policy requires user approval before every Git commit.

| Channel | Historical 65 MB transfer time | Notes |
| --- | --- | --- |
| Tailscale relay | About 20 minutes | Symmetric NAT; DERP relay required |
| Jianguoyun WebDAV | Upload 41 seconds / download 9 seconds | Historical primary transfer channel |
| Git LFS | About 35 seconds | Historical fallback; not the current local release-storage policy |

## Directory policy

| Path | Purpose | Tracked |
| --- | --- | --- |
| `Project/Windows/dist/build/` | Temporary Windows build outputs | No |
| `Release/Windows/<version>/` | Windows installers, MSIX packages, manifests and checksums for one app version | No |
| `Release/MacOS/<version>/` | macOS application bundle, archive and checksums for one app version | No |
| `Project/Ui/Branding/FeigeIconV1/` | Canonical image/icon build inputs | Yes |

## Historical commands

The commands in this section describe a previous operational workflow only. They reference a legacy checkout path and release directory layout and should not be used verbatim after the repository restructuring.

```powershell
# Legacy checkout example only
cd C:\Work\XH_Transfer
# Legacy scripts used Windows\scripts and Windows\dist\build.
```

The current packaging scripts locate the repository root from their own paths, build under `Project/Windows/`, and store final outputs beneath the sibling `Release/` folder. Verify a script's `-WhatIf` preview and current instructions before any real publication. Never put signing private keys, PFX passwords, or credentials in the repository.
