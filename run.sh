#!/usr/bin/env bash
# Close any running MassiveSlicer instance, then build + run the app.
# Usage:  ./run.sh            (from the repo root, or by full path)
set -u

# The Homebrew dotnet@8 (8.0.127) can't compile this project's .slnx / C#12 syntax,
# so put the newer .NET SDK first on PATH.
export PATH="/usr/local/share/dotnet:$PATH"

# Close other slicer instances (app, apphost, or a prior `dotnet run`).
pkill -f "MassiveSlicer.App" 2>/dev/null || true

# Resolve the project relative to this script so the path isn't machine-specific.
DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
exec dotnet run --project "$DIR/src/MassiveSlicer.App" "$@"
