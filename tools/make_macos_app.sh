#!/bin/bash
# Builds MassiveSlicer.app — a macOS app bundle so the Dock / Cmd+Tab shows the
# Massive logo and app name instead of a generic executable icon.
#
# The bundle's executable exec's the current Debug build, so rebuilding with
# `dotnet build` keeps the bundle current — no need to regenerate it per build.
#
# Usage:  tools/make_macos_app.sh
set -euo pipefail

REPO="$(cd "$(dirname "$0")/.." && pwd)"
APP="$REPO/MassiveSlicer.app"
ICON_SRC="$REPO/assets/Icons/macos-app-icon.png"          # Massive logo bug (white on black, rounded)
# On macOS the project targets net8.0 (net8.0-windows on Windows) — pick whichever exists.
DLL="$REPO/src/MassiveSlicer.App/bin/Debug/net8.0/MassiveSlicer.App.dll"
[ -f "$DLL" ] || DLL="$REPO/src/MassiveSlicer.App/bin/Debug/net8.0-windows/MassiveSlicer.App.dll"

echo "==> Building .icns from $ICON_SRC"
ICONSET="$(mktemp -d)/AppIcon.iconset"
mkdir -p "$ICONSET"
for SZ in 16 32 64 128 256 512 1024; do
  sips -z $SZ $SZ "$ICON_SRC" --out "$ICONSET/icon_${SZ}x${SZ}.png" >/dev/null
  HALF=$((SZ / 2))
  if [ $HALF -ge 16 ]; then
    cp "$ICONSET/icon_${SZ}x${SZ}.png" "$ICONSET/icon_${HALF}x${HALF}@2x.png"
  fi
done

echo "==> Creating bundle at $APP"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"

cat > "$APP/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>CFBundleName</key>            <string>MassiveSlicer</string>
    <key>CFBundleDisplayName</key>     <string>MassiveSlicer</string>
    <key>CFBundleIdentifier</key>      <string>com.massive.massiveslicer</string>
    <key>CFBundleExecutable</key>      <string>MassiveSlicer</string>
    <key>CFBundleIconFile</key>        <string>AppIcon</string>
    <key>CFBundlePackageType</key>     <string>APPL</string>
    <key>CFBundleShortVersionString</key> <string>1.0</string>
    <key>NSHighResolutionCapable</key> <true/>
</dict>
</plist>
PLIST

cat > "$APP/Contents/MacOS/MassiveSlicer" <<LAUNCHER
#!/bin/bash
# exec keeps this PID, so the dotnet process retains the bundle's Dock identity
# (Massive logo + "MassiveSlicer" name) instead of appearing as generic "dotnet".
export DOTNET_ROOT="\${DOTNET_ROOT:-\$(dirname "\$(readlink -f "\$(which dotnet 2>/dev/null || echo /usr/local/share/dotnet/dotnet)")")}"
exec dotnet "$DLL"
LAUNCHER
chmod +x "$APP/Contents/MacOS/MassiveSlicer"

echo "==> Done: $APP"
echo "    Launch with:  open \"$APP\""
