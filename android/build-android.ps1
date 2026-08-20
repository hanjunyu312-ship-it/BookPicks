# ============================================================
# BookPicks Android build (gradle-free bare build)
# Requires: JDK 17 + Android SDK (build-tools 36.1.0 / android-36)
# Pipeline: aapt2 compile/link -> javac -> d8 -> inject classes.dex
#           -> zipalign -> apksigner (only when -Signed)
#
# DEFAULT: builds an UNSIGNED test APK (no keystore required).
# SIGNED:  pass -Signed AND set all four env vars below. The script
#          never stores, generates or prints a keystore password.
#
# Signing env vars (set them before running, e.g. in your shell):
#   BOOKPICKS_KEYSTORE        absolute path to the .keystore file
#   BOOKPICKS_KEY_ALIAS       key alias inside the keystore
#   BOOKPICKS_STORE_PASSWORD  keystore password
#   BOOKPICKS_KEY_PASSWORD    key password
#
# Chinese-path support: all inputs are staged into a pure-ASCII temp
# dir (%TEMP%\bookpicks-android-build), every tool runs there, and
# the final APK is copied back to the project (or $OutputDir).
#
# Usage:
#   unsigned: powershell -ExecutionPolicy Bypass -File android\build-android.ps1
#   signed:   powershell -ExecutionPolicy Bypass -File android\build-android.ps1 -Signed
#             powershell ... -Signed -OutputDir D:\some\ascii\out
#
# NOTE: keep this file pure ASCII (Windows PowerShell 5.1 reads .ps1 as ANSI).
# ============================================================
[CmdletBinding()]
param(
  [switch]$Signed,
  [string]$OutputDir = ""
)
$ErrorActionPreference = 'Stop'

$srcRoot = $PSScriptRoot

# ------------------------------------------------------------
# 0. Resolve Android SDK tooling (from env, no secrets here)
# ------------------------------------------------------------
$sdk = $env:ANDROID_HOME
if (-not $sdk) { $sdk = Join-Path $env:LOCALAPPDATA 'Android\Sdk' }
$bt = Join-Path $sdk 'build-tools\36.1.0'
$platform = Join-Path $sdk 'platforms\android-36'
$androidJar = Join-Path $platform 'android.jar'
$aapt2 = Join-Path $bt 'aapt2.exe'
$d8 = Join-Path $bt 'd8.bat'
$zipalign = Join-Path $bt 'zipalign.exe'
$apksigner = Join-Path $bt 'apksigner.bat'

foreach ($p in @($bt, $platform, $aapt2, $d8, $zipalign, $apksigner)) {
  if (-not (Test-Path $p)) { throw "Missing Android SDK component: $p" }
}

# ------------------------------------------------------------
# 1. Signing config (only when -Signed)
# ------------------------------------------------------------
$keystore = $null; $alias = $null
if ($Signed) {
  $keystore = $env:BOOKPICKS_KEYSTORE
  $alias = $env:BOOKPICKS_KEY_ALIAS
  $missing = @()
  if (-not $keystore) { $missing += 'BOOKPICKS_KEYSTORE' }
  if (-not $alias) { $missing += 'BOOKPICKS_KEY_ALIAS' }
  if (-not $env:BOOKPICKS_STORE_PASSWORD) { $missing += 'BOOKPICKS_STORE_PASSWORD' }
  if (-not $env:BOOKPICKS_KEY_PASSWORD) { $missing += 'BOOKPICKS_KEY_PASSWORD' }
  if ($missing.Count -gt 0) {
    throw "Signed build requires these env vars: $($missing -join ', '). " +
          'Set them before running; do NOT embed passwords in scripts or logs.'
  }
  if (-not (Test-Path $keystore)) { throw "Keystore not found: $keystore" }
}

# ------------------------------------------------------------
# 2. Stage inputs into a pure-ASCII temp dir
# ------------------------------------------------------------
$tmpBase = $env:TEMP
if (-not $tmpBase) { $tmpBase = 'C:\Windows\Temp' }
$work = Join-Path $tmpBase 'bookpicks-android-build'
if (Test-Path $work) { Remove-Item $work -Recurse -Force }
New-Item -ItemType Directory -Force $work | Out-Null

function Stage-Dir([string]$rel) {
  # Copy $srcRoot\<rel> into $work\<rel>, preserving relative layout.
  $srcDir = Join-Path $srcRoot $rel
  if (-not (Test-Path $srcDir)) { return }
  Get-ChildItem $srcDir -Recurse -File | ForEach-Object {
    $inner = $_.FullName.Substring($srcDir.Length + 1)
    $dst = Join-Path $work (Join-Path $rel $inner)
    New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null
    Copy-Item $_.FullName $dst -Force
  }
}

Stage-Dir 'res'
Stage-Dir 'src'
Stage-Dir 'assets'        # includes assets/www/* (copied, never modified)
Copy-Item (Join-Path $srcRoot 'AndroidManifest.xml') $work -Force
Copy-Item (Join-Path $srcRoot 'make-icon.ps1') $work -Force

# Build outputs live inside the temp work dir.
$build = Join-Path $work 'build'
New-Item -ItemType Directory -Force $build | Out-Null

# Debug: how many files were staged per input dir (helps diagnose CI gaps).
Write-Host ("STAGED res    files: " + (Get-ChildItem (Join-Path $work 'res') -Recurse -File).Count)
Write-Host ("STAGED src    files: " + (Get-ChildItem (Join-Path $work 'src') -Recurse -File).Count)
Write-Host ("STAGED assets files: " + (Get-ChildItem (Join-Path $work 'assets') -Recurse -File).Count)
# Diagnostics for CI asset-path issue: dump source + staged asset trees verbatim.
Write-Host "DIAG srcRoot=[$srcRoot]"
Write-Host "DIAG work=[$work]"
Write-Host "DIAG source assets tree (after staging):"
Get-ChildItem (Join-Path $srcRoot 'assets') -Recurse -Force | ForEach-Object { Write-Host ("  " + $_.FullName) }
Write-Host "DIAG staged assets tree (after staging):"
Get-ChildItem (Join-Path $work 'assets') -Recurse -Force | ForEach-Object { Write-Host ("  " + $_.FullName) }

try {
  # --------------------------------------------------------
  # 3. Icon resource (only if missing in the staged res)
  # --------------------------------------------------------
  if (-not (Test-Path (Join-Path $work 'res\mipmap-xxxhdpi\ic_launcher.png'))) {
    & powershell -ExecutionPolicy Bypass -File (Join-Path $work 'make-icon.ps1')
  }

  # --------------------------------------------------------
  # 4. aapt2 compile resources
  # --------------------------------------------------------
  $flats = @()
  Get-ChildItem (Join-Path $work 'res') -Recurse -Filter *.png | ForEach-Object {
    $before = @(Get-ChildItem "$build\*.flat" -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Name)
    & $aapt2 compile $_.FullName -o $build
    if ($LASTEXITCODE -ne 0) { throw "aapt2 compile failed: $($_.Name)" }
    $after = @(Get-ChildItem "$build\*.flat" | Select-Object -ExpandProperty Name)
    foreach ($n in ($after | Where-Object { $before -notcontains $_ })) {
      $flats += (Join-Path $build $n)
    }
  }

  # --------------------------------------------------------
  # 5. aapt2 link: manifest + resources -> unsigned apk
  # NOTE: assets are NOT packed at link time (aapt2 on Windows writes
  # backslash asset paths that Android AssetManager can't resolve).
  # assets are injected below with forward-slash paths.
  # --------------------------------------------------------
  $apkUnsigned = Join-Path $build 'app-unsigned.apk'
  if (Test-Path $apkUnsigned) { Remove-Item $apkUnsigned -Force }
  & $aapt2 link -o $apkUnsigned -I $androidJar `
    --manifest (Join-Path $work 'AndroidManifest.xml') `
    --min-sdk-version 24 --target-sdk-version 36 `
    $flats
  if ($LASTEXITCODE -ne 0) { throw 'aapt2 link failed' }

  # --------------------------------------------------------
  # 6. javac compile (fresh dir so mtime-based incremental
  #    checks never skip recompiling)
  # --------------------------------------------------------
  $classes = Join-Path $build 'classes'
  if (Test-Path $classes) { Remove-Item $classes -Recurse -Force }
  New-Item -ItemType Directory -Force $classes | Out-Null
  $srcs = Get-ChildItem (Join-Path $work 'src') -Recurse -Filter *.java | Select-Object -ExpandProperty FullName
  & javac -encoding UTF-8 -source 11 -target 11 -classpath $androidJar `
    -d $classes $srcs
  if ($LASTEXITCODE -ne 0) { throw 'javac failed' }

  # --------------------------------------------------------
  # 7. d8 -> classes.dex
  # --------------------------------------------------------
  $dexOut = Join-Path $build 'dexout'
  if (Test-Path $dexOut) { Remove-Item $dexOut -Recurse -Force }
  New-Item -ItemType Directory -Force $dexOut | Out-Null
  $cls = Get-ChildItem (Join-Path $classes 'com\hjy\bookpicks') -Filter *.class | Select-Object -ExpandProperty FullName
  & cmd /c "`"$d8`" --release --lib `"$androidJar`" --min-api 24 --output `"$dexOut`" $($cls -join ' ')"
  if ($LASTEXITCODE -ne 0) { throw 'd8 failed' }
  $dex = Join-Path $dexOut 'classes.dex'
  if (-not (Test-Path $dex)) { throw 'classes.dex not produced' }

  # --------------------------------------------------------
  # 8. Inject classes.dex + assets (forward-slash paths)
  # --------------------------------------------------------
  Add-Type -AssemblyName System.IO.Compression
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $zip = [System.IO.Compression.ZipFile]::Open($apkUnsigned, [System.IO.Compression.ZipArchiveMode]::Update)
  try {
    $entry = $zip.CreateEntry('classes.dex', [System.IO.Compression.CompressionLevel]::Optimal)
    $es = $entry.Open()
    $bytes = [System.IO.File]::ReadAllBytes($dex)
    $es.Write($bytes, 0, $bytes.Length)
    $es.Close()
    Get-ChildItem (Join-Path $work 'assets') -Recurse -File | ForEach-Object {
      $rel = $_.FullName.Substring((Join-Path $work 'assets').Length + 1).Replace('\', '/')
      Write-Host ("DIAG inject file=[{0}] rel=[{1}]" -f $_.FullName, $rel)
      $e2 = $zip.CreateEntry("assets/$rel", [System.IO.Compression.CompressionLevel]::Optimal)
      $s2 = $e2.Open()
      $b2 = [System.IO.File]::ReadAllBytes($_.FullName)
      $s2.Write($b2, 0, $b2.Length)
      $s2.Close()
    }
    # Sanity check: every staged asset must be inside the apk.
    $injected = @($zip.Entries | ForEach-Object { $_.FullName })
    $stagedAssets = @(Get-ChildItem (Join-Path $work 'assets') -Recurse -File | ForEach-Object {
      $_.FullName.Substring((Join-Path $work 'assets').Length + 1).Replace('\', '/')
    })
    $missing = @($stagedAssets | Where-Object { $injected -notcontains "assets/$_" })
    if ($missing.Count -gt 0) {
      throw "Asset injection incomplete. Missing: $($missing -join ', '). APK entries: $($injected -join ', ')"
    }
  } finally {
    $zip.Dispose()
  }

  # --------------------------------------------------------
  # 9. zipalign
  # --------------------------------------------------------
  $aligned = Join-Path $build 'app-aligned.apk'
  & $zipalign -f -p 4 $apkUnsigned $aligned
  if ($LASTEXITCODE -ne 0) { throw 'zipalign failed' }

  # --------------------------------------------------------
  # 10. Sign (only when -Signed) or keep unsigned
  #     Passwords are passed to apksigner via env: references,
  #     so they never appear on the command line or in logs.
  # --------------------------------------------------------
  $apkName = 'BookPicks-unsigned.apk'
  if ($Signed) {
    $apkFinal = Join-Path $build 'BookPicks.apk'
    # Build the full command line in a variable first (avoids cmd /c
    # argument-mode pitfalls with concatenated strings).
    $signCmd = '"' + $apksigner + '" sign --ks "' + $keystore + '" --ks-key-alias "' +
      $alias + '" --ks-pass env:BOOKPICKS_STORE_PASSWORD --key-pass env:BOOKPICKS_KEY_PASSWORD' +
      ' --out "' + $apkFinal + '" "' + $aligned + '"'
    & cmd /c $signCmd
    if ($LASTEXITCODE -ne 0) { throw 'apksigner failed' }
    $apkName = 'BookPicks.apk'
  } else {
    $apkFinal = Join-Path $build $apkName
    Copy-Item $aligned $apkFinal -Force
  }

  # --------------------------------------------------------
  # 11. Copy the final APK back to the project / output dir
  # --------------------------------------------------------
  $destDir = $srcRoot
  if ($OutputDir -ne '') { $destDir = $OutputDir }
  New-Item -ItemType Directory -Force $destDir | Out-Null
  $outPath = Join-Path $destDir $apkName
  Copy-Item $apkFinal $outPath -Force

  Write-Host ''
  Write-Host "APK done -> $outPath"
  if ($Signed) { Write-Host 'Signed release APK (keystore read from BOOKPICKS_KEYSTORE; passwords kept out of logs).' }
  else { Write-Host 'Unsigned test APK (use -Signed to produce a release build).' }
} finally {
  # --------------------------------------------------------
  # 12. Always remove the staging temp dir (incl. any staged
  #     keystore) so nothing sensitive is left behind.
  # --------------------------------------------------------
  if (Test-Path $work) { Remove-Item $work -Recurse -Force }
}
