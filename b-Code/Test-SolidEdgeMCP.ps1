[CmdletBinding()]
param([switch]$Live)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$componentRoot = Join-Path $projectRoot 'b-Code-SolidEdgeMCP'
$pythonExe = Join-Path $componentRoot 'obj\venv\Scripts\python.exe'

if (-not (Test-Path -LiteralPath $pythonExe -PathType Leaf)) {
    throw "Missing $pythonExe. Run b-Code-SolidEdgeMCP\scripts\setup.ps1 -Dev first."
}

Push-Location $componentRoot
try {
    & $pythonExe -m pytest -q
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $pythonExe -m ruff check .
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $pythonExe -m ruff format --check .
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & $pythonExe -m pip check
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    if ($Live) {
        & $pythonExe .\scripts\live_smoke.py
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}
finally {
    Pop-Location
}
