#!/bin/bash
set -euo pipefail

# Release build. Signing/notarization credentials come from the operator's environment or the
# login keychain and are never stored in the repository.
#
# Why signing identity matters: an ad-hoc signature has no certificate and no Team ID, so macOS
# identifies the app by its code hash. Every rebuild or update changes that hash, and the Keychain
# then treats the new version as a different program and asks for the login password again
# (and adds a slow, uncached access check before each signature). A Developer ID signature gives
# the app a stable identity, so Keychain access survives updates.
project_dir="$(cd "$(dirname "$0")/.." && pwd)"
repo_root="$(cd "$project_dir/../.." && pwd)"
cd "$project_dir"
swift build -c release --product FeigeMac
binary_dir="$(swift build -c release --show-bin-path)"
staging_dir="$(mktemp -d "$repo_root/Release/.MacOS-staging.XXXXXX")"
trap 'rm -rf "$staging_dir"' EXIT
app="$staging_dir/飞鸽快传.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources/Updates"
cp "$binary_dir/FeigeMac" "$app/Contents/MacOS/FeigeMac"
cp Resources/Info.plist "$app/Contents/Info.plist"
cp "$repo_root/Project/Ui/Branding/FeigeIconV1/Feige.icns" "$app/Contents/Resources/Feige.icns"
# The update trust root ships inside the signed bundle; it is never downloaded separately.
cp Updates/UpdateSource.json "$app/Contents/Resources/Updates/UpdateSource.json"
plutil -lint "$app/Contents/Info.plist"
app_version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$app/Contents/Info.plist")"
output_dir="$repo_root/Release/MacOS/$app_version"
mkdir -p "$output_dir"

# Pick a stable signing identity when one is installed. FEIGE_SIGN_IDENTITY wins; otherwise the
# first Developer ID Application certificate is used, then an Apple Distribution certificate.
# Ad-hoc signing stays available only when no certificate exists, because it is the only option
# on a machine without one, but it is announced loudly and never silently shipped.
find_signing_identity() {
    if [[ -n "${FEIGE_SIGN_IDENTITY:-}" ]]; then
        printf '%s' "$FEIGE_SIGN_IDENTITY"
        return
    fi
    local identities
    identities="$(security find-identity -v -p codesigning 2>/dev/null || true)"
    local match
    match="$(printf '%s\n' "$identities" | grep -o '"Developer ID Application: [^"]*"' | head -1 | tr -d '"')"
    if [[ -z "$match" ]]; then
        match="$(printf '%s\n' "$identities" | grep -o '"Apple Distribution: [^"]*"' | head -1 | tr -d '"')"
    fi
    printf '%s' "$match"
}

signing_identity="$(find_signing_identity)"
if [[ -n "$signing_identity" ]]; then
    codesign --force --options runtime --timestamp --sign "$signing_identity" "$app"
    echo "Signed with: $signing_identity"
    if [[ "$signing_identity" != Developer\ ID\ Application:* ]]; then
        echo 'Warning: an "Apple Distribution" certificate is trusted by Apple but, without notarization,' >&2
        echo '         Gatekeeper still blocks first launch after download. Export the app as a zip and use' >&2
        echo '         "Developer ID Application" plus notarization for public distribution.' >&2
    fi
else
    codesign --force --sign - "$app"
    echo 'WARNING: no code-signing certificate found; falling back to an ad-hoc signature.' >&2
    echo '         Every rebuild changes the code hash, so macOS will treat each new version as a' >&2
    echo '         different program and the Keychain will ask for your login password again.' >&2
    echo '         Install an "Apple Developer" / "Developer ID Application" certificate to avoid this.' >&2
fi
codesign --verify --deep --strict "$app"

# Report the resulting identity so a release build can be audited at a glance.
if command -v codesign >/dev/null 2>&1; then
    codesign -dvv "$app" 2>&1 | grep -E '^(Authority|TeamIdentifier|Signature)' || true
fi

if [[ -n "${FEIGE_NOTARY_PROFILE:-}" ]]; then
    if [[ -z "$signing_identity" ]]; then
        echo 'Notarization requires a signing certificate (Developer ID Application).' >&2
        exit 1
    fi
    ditto -c -k --keepParent "$app" "$staging_dir/notarize.zip"
    xcrun notarytool submit "$staging_dir/notarize.zip" --keychain-profile "$FEIGE_NOTARY_PROFILE" --wait
    xcrun stapler staple "$app"
    xcrun stapler validate "$app"
fi

rm -rf -- "$output_dir/飞鸽快传.app"
ditto "$app" "$output_dir/飞鸽快传.app"
ditto -c -k --keepParent "$app" "$output_dir/FeigeMac-$app_version.zip"
echo "App: $output_dir/飞鸽快传.app"
echo "Archive: $output_dir/FeigeMac-$app_version.zip"
if [[ -z "${FEIGE_NOTARY_PROFILE:-}" ]]; then
    echo 'Local build only; not notarized for public distribution.'
fi
