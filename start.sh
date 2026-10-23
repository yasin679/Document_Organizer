#!/usr/bin/env bash
# Document Organizer — start script for Linux and macOS.
# Optional: DOCORG_ADMIN_PASSWORD=... ./start.sh   (sets the password for user "admin")
set -euo pipefail
cd "$(dirname "$0")"

if ! command -v dotnet >/dev/null 2>&1; then
  echo ".NET 8 is not installed. See https://dotnet.microsoft.com/download/dotnet/8.0"; exit 1
fi

# Another copy (an older version, or this one in another terminal) may already be using port 5080.
if command -v curl >/dev/null 2>&1 && curl -s -o /dev/null --max-time 2 http://localhost:5080/healthz; then
  echo "Port 5080 is already in use, probably by another copy of Document Organizer. Stop it first, then run this again."; exit 1
fi

if [ -f app/DocumentOrganizer.dll ]; then
  echo "Starting on http://localhost:5080 ..."
  cd app && exec dotnet DocumentOrganizer.dll
else
  echo "Building and starting on http://localhost:5080 ..."
  cd src/DocumentOrganizer && exec dotnet run -c Release
fi
