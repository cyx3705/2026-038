$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$pythonExe = Join-Path $projectRoot 'obj\venv\Scripts\python.exe'
if (-not (Test-Path -LiteralPath $pythonExe)) {
    throw 'Run scripts\setup.ps1 first.'
}
$env:PYTHONUTF8 = '1'
& $pythonExe -m solid_edge_mcp --output-dir (Join-Path $projectRoot 'data')
exit $LASTEXITCODE
