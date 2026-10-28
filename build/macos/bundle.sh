#!/usr/bin/env bash
# Builds "OGS-TS Monitor.app" from a self-contained publish, signs it with a
# Developer ID and notarizes it when the credentials are present, and zips it.
#
# usage: build/macos/bundle.sh <publish-dir> <version> <output.zip>
#
# Signing (all three, else the app is left unsigned):
#   MACOS_CERTIFICATE           base64 of the Developer ID Application .p12
#   MACOS_CERTIFICATE_PASSWORD  its password
# Notarization (all three, and only if signed):
#   APPLE_API_KEY               base64 of the App Store Connect API key (.p8)
#   APPLE_API_KEY_ID, APPLE_API_ISSUER_ID
# NOTARIZE=0 skips notarization even when the key is present.
# REQUIRE_SIGNING=1 fails instead of building unsigned or un-notarized (releases).
set -euo pipefail

publish=$1 version=$2 out=$3
here=$(cd "$(dirname "$0")" && pwd)
repo=$(cd "$here/../.." && pwd)
work=$(mktemp -d)
app="$work/OGS-TS Monitor.app"

# --- bundle layout ---------------------------------------------------------
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp -R "$publish"/. "$app/Contents/MacOS/"
rm -f "$app/Contents/MacOS/"*.pdb
# artifacts don't keep the executable bit
chmod +x "$app/Contents/MacOS/OgsTsMonitor"
sed "s/__VERSION__/$version/g" "$here/Info.plist" > "$app/Contents/Info.plist"

# icon from the OGS-TS mark
iconset="$work/OgsTsMonitor.iconset"
mkdir -p "$iconset"
src="$repo/src/OgsTsMonitor/Assets/telemetry-standard.png"
for s in 16 32 128 256 512; do
  sips -z $s $s "$src" --out "$iconset/icon_${s}x${s}.png" >/dev/null
  d=$((s * 2)); [ $d -le 512 ] && sips -z $d $d "$src" --out "$iconset/icon_${s}x${s}@2x.png" >/dev/null
done
iconutil -c icns "$iconset" -o "$app/Contents/Resources/OgsTsMonitor.icns"

# --- sign --------------------------------------------------------------------
signed=0
if [ -n "${MACOS_CERTIFICATE:-}" ] && [ -n "${MACOS_CERTIFICATE_PASSWORD:-}" ]; then
  keychain="$work/signing.keychain-db"
  kpass=$(openssl rand -hex 24)
  security create-keychain -p "$kpass" "$keychain"
  security set-keychain-settings -lut 3600 "$keychain"
  security unlock-keychain -p "$kpass" "$keychain"
  echo "$MACOS_CERTIFICATE" | base64 --decode > "$work/cert.p12"
  security import "$work/cert.p12" -k "$keychain" -P "$MACOS_CERTIFICATE_PASSWORD" -T /usr/bin/codesign
  security set-key-partition-list -S apple-tool:,apple:,codesign: -s -k "$kpass" "$keychain" >/dev/null
  security list-keychains -d user -s "$keychain" $(security list-keychains -d user | tr -d '"')
  identity=$(security find-identity -v -p codesigning "$keychain" | awk '/Developer ID Application/ { print $2; exit }')
  [ -n "$identity" ] || { echo "no Developer ID Application identity in MACOS_CERTIFICATE" >&2; exit 1; }

  ent="$here/entitlements.plist"
  # inside-out: every Mach-O in the bundle, then the executable, then the app
  find "$app/Contents/MacOS" -type f ! -name OgsTsMonitor | while read -r f; do
    if file "$f" | grep -q 'Mach-O'; then
      codesign --force --timestamp --options runtime --entitlements "$ent" --sign "$identity" "$f"
    fi
  done
  codesign --force --timestamp --options runtime --entitlements "$ent" --sign "$identity" "$app/Contents/MacOS/OgsTsMonitor"
  codesign --force --timestamp --options runtime --entitlements "$ent" --sign "$identity" "$app"
  codesign --verify --strict --verbose=2 "$app"
  signed=1
elif [ "${REQUIRE_SIGNING:-0}" = 1 ]; then
  echo "REQUIRE_SIGNING=1 but MACOS_CERTIFICATE / MACOS_CERTIFICATE_PASSWORD are not set" >&2
  exit 1
else
  echo "MACOS_CERTIFICATE not set: building an unsigned app" >&2
fi

# --- notarize ----------------------------------------------------------------
if [ "${REQUIRE_SIGNING:-0}" = 1 ] && [ "${NOTARIZE:-1}" = 1 ] && \
   { [ -z "${APPLE_API_KEY:-}" ] || [ -z "${APPLE_API_KEY_ID:-}" ] || [ -z "${APPLE_API_ISSUER_ID:-}" ]; }; then
  echo "REQUIRE_SIGNING=1 but APPLE_API_KEY / APPLE_API_KEY_ID / APPLE_API_ISSUER_ID are not all set" >&2
  exit 1
fi
ditto -c -k --keepParent "$app" "$out"
if [ "$signed" = 1 ] && [ "${NOTARIZE:-1}" = 1 ] && [ -n "${APPLE_API_KEY:-}" ] \
   && [ -n "${APPLE_API_KEY_ID:-}" ] && [ -n "${APPLE_API_ISSUER_ID:-}" ]; then
  echo "$APPLE_API_KEY" | base64 --decode > "$work/AuthKey.p8"
  xcrun notarytool submit "$out" --key "$work/AuthKey.p8" --key-id "$APPLE_API_KEY_ID" \
    --issuer "$APPLE_API_ISSUER_ID" --wait --timeout 30m
  xcrun stapler staple "$app"
  spctl --assess --type execute --verbose=2 "$app"
  rm -f "$out"
  ditto -c -k --keepParent "$app" "$out"   # re-zip with the stapled ticket
elif [ "$signed" = 1 ]; then
  echo "notarization skipped (no API key, or NOTARIZE=0)" >&2
fi

[ -n "${keychain:-}" ] && security delete-keychain "$keychain" || true
echo "built $out (signed=$signed)"
