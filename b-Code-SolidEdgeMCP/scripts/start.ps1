$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$serverExe = Join-Path $projectRoot 'obj\app\SolidEdgeMcp.exe'
if (-not (Test-Path -LiteralPath $serverExe)) {
    throw 'Run scripts\setup.ps1 first.'
}
& $serverExe --output-dir (Join-Path $projectRoot 'data')
exit $LASTEXITCODE
