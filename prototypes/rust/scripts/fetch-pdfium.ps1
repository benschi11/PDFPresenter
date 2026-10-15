# Lädt die PDFium-Bibliothek (bblanchon/pdfium-binaries, via NuGet) nach prototypes\rust\pdfium\.
# Aufruf:  powershell -ExecutionPolicy Bypass -File prototypes\rust\scripts\fetch-pdfium.ps1 [win-x64|win-arm64]
param([string]$Rid = "win-x64")
$ErrorActionPreference = "Stop"
$Version = "157.0.8100"
$Dest = Join-Path (Split-Path $PSScriptRoot -Parent) "pdfium"
$Tmp = Join-Path ([IO.Path]::GetTempPath()) ("pdfium-" + [Guid]::NewGuid())
New-Item -ItemType Directory -Force -Path $Tmp, $Dest | Out-Null
try {
    $Zip = Join-Path $Tmp "pdfium.zip"
    Invoke-WebRequest -Uri "https://api.nuget.org/v3-flatcontainer/bblanchon.pdfium.win32/$Version/bblanchon.pdfium.win32.$Version.nupkg" -OutFile $Zip
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $Archive = [IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        $Entry = $Archive.GetEntry("runtimes/$Rid/native/pdfium.dll")
        if (-not $Entry) { throw "pdfium.dll für $Rid nicht im Paket gefunden" }
        [IO.Compression.ZipFileExtensions]::ExtractToFile($Entry, (Join-Path $Dest "pdfium.dll"), $true)
    } finally { $Archive.Dispose() }
    Write-Host "PDFium $Version ($Rid) -> $Dest\pdfium.dll"
} finally {
    Remove-Item -Recurse -Force $Tmp
}
