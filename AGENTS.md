# Project Rules

## Changes, Tests, and Packaging

- For changes, complete code edits and necessary build/test/runtime verification first, then report results for the user to review. Do not generate install packages or other delivery artifacts by default.
- Only create release packages after the user confirms the changes are acceptable and explicitly asks for packaging. Temporary builds needed for verification are allowed.
- Every Git commit must be explicitly approved by the user beforehand. Summarize the intended commit contents and wait for approval before running `git commit`.

## Release Output

- Store all Windows and macOS release deliverables under the repository-adjacent `Release/` directory, at the same level as `Project/`.
- Organize deliverables by platform, then product version: `Release/Windows/<version>/` and `Release/MacOS/<version>/`.
- Do not add extra package-type folders such as `EXE/` or `MSIX/` under a version; put all packages for that version directly in its version folder. Installer filenames already identify architecture and package type.
- Resolve `Release/` from the repository root in scripts; do not depend on the command's current working directory.
- This applies to application bundles, installers, archives, checksums, release notes, and dependencies. Every deliverable goes into its own product version folder; never mix files from different versions in one folder. Installer filenames identify architecture.
- Build caches and temporary outputs may remain in their existing platform build directories.
- `Release/` is local-only and must never be committed to Git.

## Application Branding

- Both platforms use the approved Feige v1 icon (white dove, folded paper, blue/cyan gradient background). Canonical icon inputs live under `Project/Ui/Branding/FeigeIconV1/`, never in `Release/`.
- Windows app, window, and installer use `Project/Windows/src/Feige.App/Assets/AppIcon.ico`. MSIX PNG assets must be derived from the same master image, not replaced by template placeholders.
- Before signing a macOS app, copy `Project/Ui/Branding/FeigeIconV1/Feige.icns` to `Contents/Resources/Feige.icns` and set `CFBundleIconFile` to `Feige.icns`.
- See `Project/Ui/Branding/FeigeIconV1/README.md` for resource mapping and verification. Changing the approved design requires user confirmation.

## Directory Naming

- Platform/project folders use PascalCase: `Windows/`, `MacOS/`, `Docs/`, `Ui/`.
- Follow this convention for all newly created project folders. Existing upstream/tool-generated names required by build tools may remain as-is when renaming would break workflows.
