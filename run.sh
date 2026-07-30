#!/usr/bin/env bash
# Close any running MassiveSlicer instance, then build + run the app.
# Usage:  ./run.sh            (from the repo root, or by full path)
set -u

# Prefer a user-installed or system .NET SDK on PATH.
# Linux ARM64 / x64: common install from https://dot.net → $HOME/.dotnet
# macOS Homebrew: /usr/local/share/dotnet
export PATH="$HOME/.dotnet:/usr/local/share/dotnet:$PATH"
export DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"

# Close other slicer instances (app, apphost, or a prior `dotnet run`).
pkill -f "MassiveSlicer.App" 2>/dev/null || true

# Resolve the project relative to this script so the path isn't machine-specific.
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec dotnet run --project "$DIR/src/MassiveSlicer.App" "$@"
