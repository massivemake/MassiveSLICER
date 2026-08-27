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
if [ -f "$APPHOST" ]; then
  killall -q "$(basename "$APPHOST")" 2>/dev/null || true
fi
# MassiveFILES is noexec SMB. `dotnet run` launches the native apphost from
# bin/Debug and execve() returns Permission denied. UseAppHost=false hosts
# the DLL with the local `dotnet` binary. launchSettings workingDirectory
# (repo root) is still honored. Do not ./run.sh on this share — use bash.
exec dotnet run --project "$DIR/src/MassiveSlicer.App" --property:UseAppHost=false "$@"
