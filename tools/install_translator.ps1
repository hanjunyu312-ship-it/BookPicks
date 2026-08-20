# BookPicks offline translator installer (ASCII only: Windows PowerShell 5.1 reads this as ANSI)
# Installs: uv venv + torch (CPU) + transformers + Helsinki-NLP/opus-mt-en-zh model (~1.2GB total).
# Run:      powershell -ExecutionPolicy Bypass -File install_translator.ps1
# Idempotent: safe to re-run; already-installed parts are skipped.
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$base = Join-Path $env:LOCALAPPDATA 'BookPicks'
$venv = Join-Path $base 'translator\venv'
$venvPy = Join-Path $venv 'Scripts\python.exe'
$modelDir = Join-Path $base 'models\opus-mt-en-zh'
$marker = Join-Path $base 'translator\installed.json'

Write-Host '=================================================='
Write-Host ' BookPicks offline translator installer'
Write-Host ' Torch(CPU) + transformers + opus-mt-en-zh model'
Write-Host '=================================================='
Write-Host ''

if (-not (Get-Command uv -ErrorAction SilentlyContinue)) {
  Write-Host 'ERROR: "uv" not found on PATH.'
  Write-Host 'Install it first: https://docs.astral.sh/uv/  (winget install astral-sh.uv)'
  exit 1
}

New-Item -ItemType Directory -Force (Join-Path $base 'translator') | Out-Null
New-Item -ItemType Directory -Force (Join-Path $base 'models') | Out-Null

Write-Host '==> [1/4] Python venv ...'
if (-not (Test-Path $venvPy)) {
  uv venv $venv --python 3.14
  if ($LASTEXITCODE -ne 0) {
    Write-Host 'ERROR: venv creation failed. Try: uv python install 3.14'
    exit 1
  }
} else {
  Write-Host '    (already exists, skip)'
}

Write-Host '==> [2/4] torch (CPU-only, ~200MB) ...'
uv pip install --python $venvPy torch --index-url https://download.pytorch.org/whl/cpu
if ($LASTEXITCODE -ne 0) {
  Write-Host '    CPU wheel unavailable, falling back to default PyPI torch (larger)...'
  uv pip install --python $venvPy torch
  if ($LASTEXITCODE -ne 0) {
    Write-Host 'ERROR: torch install failed.'
    exit 1
  }
}

Write-Host '==> [3/4] transformers + sentencepiece + huggingface_hub ...'
uv pip install --python $venvPy transformers sentencepiece huggingface_hub
if ($LASTEXITCODE -ne 0) {
  Write-Host 'ERROR: dependencies install failed.'
  exit 1
}

Write-Host '==> [4/4] model download (opus-mt-en-zh, ~300MB) ...'
& $venvPy "$root\download_model.py" --out $modelDir
if ($LASTEXITCODE -ne 0) {
  Write-Host '    Direct download failed, retrying via hf-mirror.com ...'
  & $venvPy "$root\download_model.py" --out $modelDir --mirror
  if ($LASTEXITCODE -ne 0) {
    Write-Host 'ERROR: model download failed. Check network and re-run this script.'
    exit 1
  }
}

@{ installed = $true; model = $modelDir; date = (Get-Date).ToString('o') } |
  ConvertTo-Json | Set-Content $marker -Encoding utf8

Write-Host ''
Write-Host 'OK. Offline translator installed.'
Write-Host "    venv : $venv"
Write-Host "    model: $modelDir"
Write-Host 'Restart BookPicks to use the local engine.'
Write-Host ''
