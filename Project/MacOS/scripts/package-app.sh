#!/bin/bash
set -euo pipefail

# Local preview by default. Signing/notarization credentials are supplied explicitly
# through the operator's environment and are never stored in the repository.
project_dir="$(cd "$(dirname "$0")/.." && pwd)"
repo_root="$(cd "$project_dir/../.." && pwd)"
cd "$project_dir"
swift build -c release --product FeigeMac
binary_dir="$(swift build -c release --show-bin-path)"
staging_dir="$(mktemp -d "$repo_root/Release/.MacOS-staging.XXXXXX")"
trap 'rm -rf "$staging_dir"' EXIT
app="$staging_dir/飞鸽快传.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$binary_dir/FeigeMac" "$app/Contents/MacOS/FeigeMac"
cp Resources/Info.plist "$app/Contents/Info.plist"
cp "$repo_root/Project/Ui/Branding/FeigeIconV1/Feige.icns" "$app/Contents/Resources/Feige.icns"
plutil -lint "$app/Contents/Info.plist"
app_version="$(/usr/libexec/PlistBuddy -c 'Print :CFBundleShortVersionString' "$app/Contents/Info.plist")"
output_dir="$repo_root/Release/MacOS/$app_version"
mkdir -p "$output_dir"

if [[ -n "${FEIGE_SIGN_IDENTITY:-}" ]]; then
    codesign --force --options runtime --timestamp --sign "$FEIGE_SIGN_IDENTITY" "$app"
else
    codesign --force --sign - "$app"
fi
codesign --verify --deep --strict "$app"

if [[ -n "${FEIGE_NOTARY_PROFILE:-}" ]]; then
    if [[ -z "${FEIGE_SIGN_IDENTITY:-}" ]]; then
        echo 'Notarization requires FEIGE_SIGN_IDENTITY (Developer ID Application).' >&2
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
