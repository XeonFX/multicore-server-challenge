#!/usr/bin/env bash
# Builds the server -> bin/conduit.dll (Release).
set -euo pipefail
cd "$(dirname "$0")"
export DOTNET_ROOT="${DOTNET_ROOT:-/usr/local/dotnet}" DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1
DOTNET="$DOTNET_ROOT/dotnet"
[ -x "$DOTNET" ] || DOTNET=dotnet
"$DOTNET" publish Conduit.csproj -c Release -o bin -v quiet -nologo
