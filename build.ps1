# Build & publish script (ASCII only: Windows PowerShell 5.1 reads this as ANSI)
# Usage:  powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot

Write-Host "==> dotnet publish (Release / win-x64 / self-contained / single-file) ..."
# PublishSingleFile: bundle all managed DLLs into one BookPicks.exe
# (www static assets stay as external files; no compression for fast startup)
dotnet publish "$root\BookPicks.csproj" -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o "$root\publish"
if ($LASTEXITCODE -ne 0) { throw "Publish failed. See errors above." }

# Copy readme (*.txt) next to the exe
Get-ChildItem "$root\*.txt" | ForEach-Object { Copy-Item $_.FullName "$root\publish\$($_.Name)" -Force }

Write-Host ""
Write-Host "Publish OK  ->  $root\publish\BookPicks.exe"
Write-Host "Double-click BookPicks.exe to run (self-contained single file)."
Write-Host "Self test: BookPicks.exe --selftest  (report at %LOCALAPPDATA%\BookPicks\selftest.txt)"
