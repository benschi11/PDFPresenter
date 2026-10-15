#!/usr/bin/env sh
# Lädt die PDFium-Bibliothek (bblanchon/pdfium-binaries, via NuGet) nach prototypes/rust/pdfium/.
# Aufruf aus beliebigem Verzeichnis:  sh prototypes/rust/scripts/fetch-pdfium.sh [linux-x64|linux-arm64]
set -eu
VERSION=157.0.8100
RID=${1:-linux-x64}
DEST="$(cd "$(dirname "$0")/.." && pwd)/pdfium"
TMP=$(mktemp -d)
trap 'rm -rf "$TMP"' EXIT
curl -fsSL -o "$TMP/pdfium.zip" "https://api.nuget.org/v3-flatcontainer/bblanchon.pdfium.linux/$VERSION/bblanchon.pdfium.linux.$VERSION.nupkg"
mkdir -p "$DEST"
unzip -o -j -q "$TMP/pdfium.zip" "runtimes/$RID/native/libpdfium.so" -d "$DEST"
echo "PDFium $VERSION ($RID) -> $DEST/libpdfium.so"
