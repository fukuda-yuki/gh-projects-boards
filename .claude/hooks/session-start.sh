#!/bin/bash
# Prepares Claude Code cloud sessions (Linux) for Core logic work.
# The app, UI integration and E2E tests need Windows and run on the owner's PC instead.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

# The dotnet-install script host is outside the cloud network allowance; the Ubuntu archive is not.
if ! command -v dotnet >/dev/null 2>&1 || ! dotnet --list-sdks | grep -q '^10\.'; then
  apt-get update -qq
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq dotnet-sdk-10.0 >/dev/null
fi

# The test project targets net10.0-windows, which NUnit skips entirely on Linux.
cd "$CLAUDE_PROJECT_DIR"
dotnet restore tests/GhProjectsBoards.Tests/GhProjectsBoards.Tests.csproj -p:TargetFramework=net10.0 --verbosity quiet
