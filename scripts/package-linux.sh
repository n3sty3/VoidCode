#!/usr/bin/env bash
set -euo pipefail

# Packages VoidCode for Linux (x64) using the cross-platform Photino/WebKitGTK host.
# Produces:
#   dist/VoidCode-linux-x64.tar.gz (+ .sha256)

ROOT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
VERSION="${VOIDCODE_VERSION:-1.1.1}"
RID="${1:-linux-x64}"
PUBLISH_DIR="$ROOT_DIR/publish/$RID"
DIST_DIR="$ROOT_DIR/dist"
STAGE_DIR="$DIST_DIR/stage-$RID/voidcode"
ARCHIVE_PATH="$DIST_DIR/VoidCode-${RID}.tar.gz"

echo "==> Publishing VoidCode ($RID, v$VERSION)..."
rm -rf "$PUBLISH_DIR" "$DIST_DIR/stage-$RID"
mkdir -p "$DIST_DIR" "$STAGE_DIR"

dotnet publish "$ROOT_DIR/VoidCode.Mac/VoidCode.Mac.csproj" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -p:PublishSingleFile=false \
  -p:Version="$VERSION" \
  -o "$PUBLISH_DIR"

cp -R "$PUBLISH_DIR"/. "$STAGE_DIR/"
chmod +x "$STAGE_DIR/VoidCode"

cat > "$STAGE_DIR/voidcode.desktop" <<EOF
[Desktop Entry]
Name=VoidCode
Comment=Local-First AI-Native IDE powered by VoidAgent Core
Exec=VoidCode %F
Terminal=false
Type=Application
Categories=Development;IDE;
EOF

rm -f "$ARCHIVE_PATH" "${ARCHIVE_PATH}.sha256"
tar -C "$DIST_DIR/stage-$RID" -czf "$ARCHIVE_PATH" voidcode
sha256sum "$ARCHIVE_PATH" > "${ARCHIVE_PATH}.sha256"
rm -rf "$DIST_DIR/stage-$RID"
echo "==> Created $ARCHIVE_PATH"
