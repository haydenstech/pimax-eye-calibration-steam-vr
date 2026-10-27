#!/usr/bin/env bash
# Builds the release package: dist/EyeTrackingGuide-OpenVR/ and dist/EyeTrackingGuide-OpenVR-<version>.zip
# Needs only the .NET SDK (8+); no game files are required to build.
set -euo pipefail
cd "$(dirname "$0")"
version="${1:-dev}"
out=dist/EyeTrackingGuide-OpenVR

dotnet build src/OpenVRPatch -c Release
dotnet build src/Installer -c Release -f net48

rm -rf dist && mkdir -p "$out"
cp src/OpenVRPatch/bin/Release/OpenVRPatch.dll "$out/"
cp src/Installer/bin/Release/net48/{Install.exe,Install.exe.config,Mono.Cecil.dll} "$out/"
cp OpenVRPatch.ini LICENSE THIRD-PARTY-NOTICES.txt "$out/"
cp docs/INSTALL.txt "$out/README.txt"

if command -v zip >/dev/null; then
  (cd dist && zip -qr "EyeTrackingGuide-OpenVR-$version.zip" EyeTrackingGuide-OpenVR)
  echo "Built dist/EyeTrackingGuide-OpenVR-$version.zip"
else
  echo "zip not installed; package left unzipped in $out"
fi
