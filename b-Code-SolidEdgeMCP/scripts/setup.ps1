param([switch]$Dev)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$venvRoot = Join-Path $projectRoot 'obj\venv'
$pythonExe = Join-Path $venvRoot 'Scripts\python.exe'
if (-not (Test-Path -LiteralPath $pythonExe)) {
    & py -3.12 -m venv $venvRoot
    if ($LASTEXITCODE -ne 0) { throw 'Python 3.12 is required by this setup script.' }
}
$dependencyFile = Join-Path $projectRoot 'requirements.lock.txt'
if ($Dev) { $dependencyFile = Join-Path $projectRoot 'requirements-dev.lock.txt' }
& $pythonExe -m pip install -r $dependencyFile
if ($LASTEXITCODE -ne 0) { throw 'Dependency installation failed.' }
& $pythonExe -m pip install --no-deps -e $projectRoot
if ($LASTEXITCODE -ne 0) { throw 'Project installation failed.' }
& $pythonExe (Join-Path $PSScriptRoot 'write_client_config.py')
if ($LASTEXITCODE -ne 0) { throw 'Client configuration generation failed.' }
& $pythonExe -m solid_edge_mcp --doctor
if ($LASTEXITCODE -ne 0) { throw 'Solid Edge COM registration was not found.' }
