#!/bin/bash
# On Windows (Git Bash), deletes stale test output; see tests/README.md#test-output-retention.
# In Claude Code cloud sessions (Linux), prepares the checkout for Core logic work.
# The app, UI integration and E2E tests need Windows and run on the owner's PC instead.
set -euo pipefail

case "$(uname -s)" in
  MINGW*|MSYS*|CYGWIN*)
    # A cleanup failure must not stop the session; the test scripts run the same cleanup.
    pwsh -NoProfile -File "$CLAUDE_PROJECT_DIR/scripts/Clear-TestResults.ps1" || echo 'TestResults cleanup failed.' >&2
    exit 0
    ;;
esac

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
