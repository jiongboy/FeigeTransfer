# Repository and documentation policy

## Project layout

| Location | Contents |
| --- | --- |
| `Project/Windows/` | Windows application, core libraries, tests and scripts |
| `Project/MacOS/` | macOS application, identity/transfer core, tests and scripts |
| `Project/Docs/Public/` | Shared product contracts, protocols and governance |
| `Project/Docs/Windows/` | Windows-specific implementation, release and validation docs |
| `Project/Docs/MacOS/` | macOS-specific implementation and acceptance docs |
| `Project/Ui/` | Shared canonical icon and image resources |
| `Release/` | Local packages, installers, archives and checksums; ignored by Git |

Shared protocol, security and product behavior must have one authoritative document in `Public/`. Platform documentation can link to shared requirements but must not fork or copy their normative text. Keep historical status explicitly dated and distinguish it from current behavior.

## Documentation taxonomy

- **Public**: rules and contracts that apply to both platforms, including protocol, UI behavior, security, onboarding, update behavior, repository collaboration and cross-platform status.
- **Windows**: Windows-specific environment setup, architecture, implementation guidance, update/release processes, acceptance records and development log.
- **MacOS**: macOS-specific architecture, implementation guidance, packaging, acceptance and development log.
- Keep documents directly in these three category directories. Use English filenames and clear topical names. Merge documents when they cover the same subject and have overlapping audiences; retain separate documents when they serve distinct operator tasks or constitute historical evidence.
- The root `Project/Docs/README.md` is the navigation entry point; avoid redundant nested indexes unless a category genuinely needs a long index.

## Release output layout

- Organize deliverables by platform and product version: `Release/Windows/<version>/` and `Release/MacOS/<version>/`.
- Keep all packages for a version directly in its version folder; do not add `EXE/`, `MSIX/` or similar package-type subfolders. Filenames identify architecture and package type.
- Never mix files from different product versions in one version folder. Keep each version folder self-contained with its installers, checksums and release notes.

## Branch and commit workflow

1. Windows, macOS and shared documents use `main` as the only long-lived branch.
2. Do not create long-lived `windows` or `macOS` branches. Short-lived feature branches may be used for isolated work and merged after verification.
3. Update shared public contracts first when changing protocol or security behavior, then synchronize both implementations and public vectors.
4. Obtain the user's explicit approval before every `git commit`; never commit without it.
5. Record release versions, interoperability results, deliverables and known limits. The protocol versions must match across clients.

## Prohibited practices

- Do not maintain duplicate long-lived source trees outside the platform directories.
- Do not restore long-lived platform branches or divergent copies of shared contracts.
- Do not commit build caches or release deliverables. Keep deliverables in repository-adjacent `Release/`.
