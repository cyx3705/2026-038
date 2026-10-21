param([switch]$Dev)
$ErrorActionPreference = 'Stop'
$projectRoot = Split-Path -Parent $PSScriptRoot
$appRoot = Join-Path $projectRoot 'obj\app'
$serverExe = Join-Path $appRoot 'SolidEdgeMcp.exe'

# Framework-dependent publish into obj\app; the generated client configs point at this exe.
& dotnet publish (Join-Path $projectRoot 'src\SolidEdgeMcp') -c Release -o $appRoot --nologo
if ($LASTEXITCODE -ne 0) { throw 'Server publish failed. Close MCP clients that hold obj\app\SolidEdgeMcp.exe, then retry.' }
if ($Dev) {
    & dotnet build (Join-Path $projectRoot 'SolidEdgeMcp.sln') --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Solution build failed.' }
}

# Generate client snippets for this checkout; never edit global client settings.
$configRoot = Join-Path $projectRoot 'config'
New-Item -ItemType Directory -Force -Path $configRoot | Out-Null
$command = $serverExe.Replace('\', '/')
$output = (Join-Path $projectRoot 'data').Replace('\', '/')
$arguments = @('--output-dir', $output)
$config = [ordered]@{ mcpServers = [ordered]@{ 'solid-edge' = [ordered]@{ command = $command; args = $arguments } } }
$utf8 = New-Object System.Text.UTF8Encoding($false)
[IO.File]::WriteAllText((Join-Path $configRoot 'mcp.json'), ($config | ConvertTo-Json -Depth 5) + "`n", $utf8)
$toml = @(
    '# Generated for this checkout. Regenerate after moving the project.'
    '# Merge this section into your Codex config.toml.'
    '[mcp_servers.solid-edge]'
    "command = `"$command`""
    "args = [`"--output-dir`", `"$output`"]"
    'startup_timeout_sec = 30'
    'tool_timeout_sec = 150'
    ''
)
[IO.File]::WriteAllText((Join-Path $configRoot 'codex.toml'), ($toml -join "`n"), $utf8)
Write-Output "Client configuration written to $configRoot"

& $serverExe --doctor
if ($LASTEXITCODE -ne 0) { throw 'Solid Edge COM registration was not found.' }
