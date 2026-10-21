[CmdletBinding()]
param([switch]$Live)

$ErrorActionPreference = 'Stop'
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$componentRoot = Join-Path $projectRoot 'b-Code-SolidEdgeMCP'
$solution = Join-Path $componentRoot 'SolidEdgeMcp.sln'

Push-Location $componentRoot
try {
    & dotnet build $solution --nologo
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    & dotnet test $solution --nologo --no-build
    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    if ($Live) {
        & dotnet run --project .\scripts\LiveSmoke --no-build
        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
    }
}
finally {
    Pop-Location
}
