#!/bin/bash
set -euo pipefail
REPO="$(cd "$(dirname "$0")/.." && pwd)"
APP="$REPO/MassiveSlicer.app"
ICON_ICNS="$REPO/assets/Icons/icon.icns"
ICON_PNG="$REPO/assets/Icons/macos-app-icon.png"
rm -rf "$APP"
mkdir -p "$APP/Contents/MacOS" "$APP/Contents/Resources"
if [ -f "$ICON_ICNS" ]; then cp "$ICON_ICNS" "$APP/Contents/Resources/AppIcon.icns"
elif [ -f "$ICON_PNG" ]; then
  ICONSET="$(mktemp -d)/AppIcon.iconset"; mkdir -p "$ICONSET"
  for SZ in 16 32 64 128 256 512 1024; do
    sips -z $SZ $SZ "$ICON_PNG" --out "$ICONSET/icon_${SZ}x${SZ}.png" >/dev/null
    HALF=$((SZ/2)); [ $HALF -ge 16 ] && cp "$ICONSET/icon_${SZ}x${SZ}.png" "$ICONSET/icon_${HALF}x${HALF}@2x.png"
  done
  iconutil -c icns "$ICONSET" -o "$APP/Contents/Resources/AppIcon.icns"
fi
cat > "$APP/Contents/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0"><dict>
    <key>CFBundleName</key><string>MassiveSlicer</string>
    <key>CFBundleDisplayName</key><string>MassiveSlicer</string>
    <key>CFBundleIdentifier</key><string>com.massivemake.massiveslicer</string>
    <key>CFBundleExecutable</key><string>MassiveSlicer</string>
    <key>CFBundleIconFile</key><string>AppIcon</string>
    <key>CFBundlePackageType</key><string>APPL</string>
    <key>CFBundleShortVersionString</key><string>1.0</string>
    <key>NSHighResolutionCapable</key><true/>
</dict></plist>
PLIST
cat > "$APP/Contents/MacOS/MassiveSlicer" <<LAUNCHER
#!/bin/bash
export PATH="/usr/local/share/dotnet:/opt/homebrew/bin:\$HOME/.dotnet:/usr/bin:/bin:\$PATH"
export DOTNET_ROOT="/usr/local/share/dotnet"
export DOTNET_ROLL_FORWARD="\${DOTNET_ROLL_FORWARD:-Major}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
REPO="$REPO"
cd "\$REPO" || exit 1
LOG="\$REPO/MassiveSlicer.app-launch.log"
{ echo "==== \$(date) ===="; git -C "\$REPO" rev-parse --abbrev-ref HEAD; git -C "\$REPO" rev-parse --short HEAD; } >>"\$LOG" 2>&1
exec >>"\$LOG" 2>&1
exec "\$REPO/run.sh" "\$@"
LAUNCHER
chmod +x "$APP/Contents/MacOS/MassiveSlicer"
touch "$APP"
echo "Done $APP"
