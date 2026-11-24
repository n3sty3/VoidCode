#!/usr/bin/env bash
set -euo pipefail

# Packages VoidCode for macOS (Apple Silicon osx-arm64 or Intel osx-x64)
# Produces:
#   dist/VoidCode-<version>-<rid>.dmg  (+ .sha256)
#   dist/VoidCode-<rid>.zip            (+ .sha256)
#
# Usage:
#   ./scripts/package-mac.sh [osx-arm64|osx-x64]

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
CSPROJ_VER="$(sed -n 's/.*<Version>\([^<]*\)<\/Version>.*/\1/p' "$ROOT_DIR/VoidCode.Mac/VoidCode.Mac.csproj" | head -n 1)"
VERSION="${VOIDCODE_VERSION:-${CSPROJ_VER:-1.1.1}}"

if [[ $# -ge 1 ]]; then
  RID="$1"
else
  ARCH="$(uname -m)"
  if [[ "$ARCH" == "arm64" ]]; then
    RID="osx-arm64"
  else
    RID="osx-x64"
  fi
fi

PUBLISH_DIR="$ROOT_DIR/publish/$RID"
DIST_DIR="$ROOT_DIR/dist"
STAGE_DIR="$DIST_DIR/stage-$RID"
APP_BUNDLE="$STAGE_DIR/VoidCode.app"
DMG_PATH="$DIST_DIR/VoidCode-${VERSION}-${RID}.dmg"
ZIP_PATH="$DIST_DIR/VoidCode-${RID}.zip"

echo "==> Publishing VoidCode.Mac ($RID, v$VERSION)..."
rm -rf "$PUBLISH_DIR" "$STAGE_DIR"
mkdir -p "$DIST_DIR" "$APP_BUNDLE/Contents/MacOS" "$APP_BUNDLE/Contents/Resources/bin"

dotnet publish "$ROOT_DIR/VoidCode.Mac/VoidCode.Mac.csproj" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -p:PublishSingleFile=false \
  -p:Version="$VERSION" \
  -o "$PUBLISH_DIR"

echo "==> Assembling VoidCode.app bundle..."
cp -R "$PUBLISH_DIR"/. "$APP_BUNDLE/Contents/MacOS/"
chmod +x "$APP_BUNDLE/Contents/MacOS/VoidCode"

# Install CLI helper inside bundle: /Applications/VoidCode.app/Contents/Resources/bin/voidcode
cat > "$APP_BUNDLE/Contents/Resources/bin/voidcode" <<'EOF'
#!/usr/bin/env bash
APP_BIN="$(cd "$(dirname "${BASH_SOURCE[0]}")/../../MacOS" && pwd)/VoidCode"
exec "$APP_BIN" "$@"
EOF
chmod +x "$APP_BUNDLE/Contents/Resources/bin/voidcode"

# Generate VoidCode.icns from app.ico when running on macOS (sips + iconutil)
if command -v sips >/dev/null 2>&1 && command -v iconutil >/dev/null 2>&1 && [[ -f "$ROOT_DIR/app.ico" ]]; then
  ICONSET_DIR="$STAGE_DIR/VoidCode.iconset"
  mkdir -p "$ICONSET_DIR"
  BASE_PNG="$STAGE_DIR/icon-base.png"
  if sips -s format png "$ROOT_DIR/app.ico" --out "$BASE_PNG" >/dev/null 2>&1; then
    for sz in 16 32 128 256 512; do
      sips -z "$sz" "$sz" "$BASE_PNG" --out "$ICONSET_DIR/icon_${sz}x${sz}.png" >/dev/null 2>&1 || true
      sz2=$((sz * 2))
      sips -z "$sz2" "$sz2" "$BASE_PNG" --out "$ICONSET_DIR/icon_${sz}x${sz}@2x.png" >/dev/null 2>&1 || true
    done
    iconutil -c icns "$ICONSET_DIR" -o "$APP_BUNDLE/Contents/Resources/VoidCode.icns" || true
  fi
  rm -rf "$ICONSET_DIR" "$BASE_PNG"
fi

cat > "$APP_BUNDLE/Contents/Info.plist" <<EOF
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key>
  <string>VoidCode</string>
  <key>CFBundleDisplayName</key>
  <string>VoidCode</string>
  <key>CFBundleIdentifier</key>
  <string>dev.voidcode.ide</string>
  <key>CFBundleVersion</key>
  <string>${VERSION}</string>
  <key>CFBundleShortVersionString</key>
  <string>${VERSION}</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
  <key>CFBundleExecutable</key>
  <string>VoidCode</string>
  <key>CFBundleIconFile</key>
  <string>VoidCode</string>
  <key>LSMinimumSystemVersion</key>
  <string>12.0</string>
  <key>NSHighResolutionCapable</key>
  <true/>
  <key>NSRequiresAquaSystemAppearance</key>
  <false/>
  <key>CFBundleDocumentTypes</key>
  <array>
    <dict>
      <key>CFBundleTypeName</key>
      <string>Folder</string>
      <key>CFBundleTypeRole</key>
      <string>Editor</string>
      <key>LSItemContentTypes</key>
      <array>
        <string>public.folder</string>
      </array>
    </dict>
  </array>
</dict>
</plist>
EOF

# Ad-hoc (or Developer ID when APPLE_SIGNING_IDENTITY is set) codesign so Apple Silicon runs cleanly
if command -v codesign >/dev/null 2>&1; then
  SIGN_ID="${APPLE_SIGNING_IDENTITY:--}"
  echo "==> Code signing VoidCode.app (identity: $SIGN_ID)..."
  codesign --force --deep --sign "$SIGN_ID" "$APP_BUNDLE"
fi

echo "==> Creating portable archive $ZIP_PATH..."
rm -f "$ZIP_PATH" "${ZIP_PATH}.sha256"
(cd "$STAGE_DIR" && ditto -c -k --sequesterRsrc --keepParent "VoidCode.app" "$ZIP_PATH")
shasum -a 256 "$ZIP_PATH" > "${ZIP_PATH}.sha256"

if command -v hdiutil >/dev/null 2>&1; then
  echo "==> Creating DMG installer $DMG_PATH..."
  DMG_SRC="$STAGE_DIR/dmg-root"
  mkdir -p "$DMG_SRC"
  cp -R "$APP_BUNDLE" "$DMG_SRC/VoidCode.app"
  ln -s /Applications "$DMG_SRC/Applications"
  rm -f "$DMG_PATH" "${DMG_PATH}.sha256"
  hdiutil create -volname "VoidCode" -srcfolder "$DMG_SRC" -ov -format UDZO "$DMG_PATH"
  shasum -a 256 "$DMG_PATH" > "${DMG_PATH}.sha256"
fi

rm -rf "$STAGE_DIR"
echo "==> Done! Artifacts written to $DIST_DIR"
