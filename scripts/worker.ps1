# Starts one worker for one task queue in a new window: entities | dispatch | maintenance | integrations | all
param([string]$Queue = 'all')
$root = Split-Path -Parent $PSScriptRoot
$dll = Join-Path $root 'src\Fleet.Worker\bin\Debug\net8.0\Fleet.Worker.dll'
if (-not (Test-Path $dll)) { dotnet build (Join-Path $root 'src\Fleet.Worker') -c Debug --nologo -v quiet }
$shell = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }
$script = "`$host.UI.RawUI.WindowTitle = 'Worker: $Queue'; Set-Location '$root'; dotnet '$dll' --queues $Queue"
Start-Process $shell -ArgumentList '-NoExit', '-NoProfile', '-Command', $script | Out-Null
Write-Host "Worker for '$Queue' started in a new window."
