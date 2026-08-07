#!/usr/bin/env bash
set -u
export PATH="/usr/local/share/dotnet:/opt/homebrew/bin:$HOME/.dotnet:$PATH"
if [ -x /usr/local/share/dotnet/dotnet ]; then
  export DOTNET_ROOT="/usr/local/share/dotnet"
elif [ -d "$HOME/.dotnet" ]; then
  export DOTNET_ROOT="$HOME/.dotnet"
fi
export DOTNET_ROLL_FORWARD="${DOTNET_ROLL_FORWARD:-Major}"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
APPHOST="$DIR/src/MassiveSlicer.App/bin/Debug/net8.0/MassiveSlicer.App"
if [ -x "$APPHOST" ]; then
  killall -q "$(basename "$APPHOST")" 2>/dev/null || true
fi
exec dotnet run --project "$DIR/src/MassiveSlicer.App" "$@"
