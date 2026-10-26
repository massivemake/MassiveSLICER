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
    <!-- "Open in MassiveSLICER" links from MassiveLAB / MassiveBOARD. -->
    <key>CFBundleURLTypes</key>
    <array><dict>
        <key>CFBundleURLName</key><string>com.massivemake.massiveslicer</string>
        <key>CFBundleURLSchemes</key><array><string>massiveslicer</string></array>
    </dict></array>
</dict></plist>
PLIST
cat > "$APP/Contents/MacOS/MassiveSlicer" <<LAUNCHER
#!/bin/bash
# Apps opened from Finder or a browser link get a bare PATH, not your shell's, so find
# dotnet ourselves. The x64 folder is where the Intel installer lands on Apple Silicon.
for d in /usr/local/share/dotnet /usr/local/share/dotnet/x64 "\$HOME/.dotnet" /opt/homebrew/share/dotnet /opt/homebrew/bin; do
  if [ -x "\$d/dotnet" ]; then export DOTNET_ROOT="\${DOTNET_ROOT:-\$d}"; export PATH="\$d:\$PATH"; break; fi
done
export PATH="\$PATH:/usr/bin:/bin"
export DOTNET_ROLL_FORWARD="\${DOTNET_ROLL_FORWARD:-Major}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
REPO="$REPO"
cd "\$REPO" || exit 1
LOG="\$REPO/MassiveSlicer.app-launch.log"
{ echo "==== \$(date) ===="; git -C "\$REPO" rev-parse --abbrev-ref HEAD; git -C "\$REPO" rev-parse --short HEAD; } >>"\$LOG" 2>&1
exec >>"\$LOG" 2>&1
# Build like run.sh's \`dotnet run\` did, then exec the app in THIS process. \`dotnet run\`
# starts the app as a child, and macOS delivers massiveslicer:// links to the process it
# launched — the child never saw them. UseAppHost=false: see run.sh (noexec share).
dotnet build "\$REPO/src/MassiveSlicer.App/MassiveSlicer.App.csproj" --nologo -v q --property:UseAppHost=false
exec dotnet "\$REPO/src/MassiveSlicer.App/bin/Debug/net8.0/MassiveSlicer.App.dll" "\$@"
LAUNCHER
chmod +x "$APP/Contents/MacOS/MassiveSlicer"
touch "$APP"
# Tell Launch Services this bundle owns massiveslicer:// (otherwise it waits for a rescan).
/System/Library/Frameworks/CoreServices.framework/Frameworks/LaunchServices.framework/Support/lsregister -f "$APP" || true
echo "Done $APP"
