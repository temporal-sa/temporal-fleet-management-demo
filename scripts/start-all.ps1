# Builds everything and opens one window per process (Windows).
# Close a "Worker: ..." window, or run scripts\stop-worker.ps1, to kill a worker during the demo.
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host 'Building the .NET solution...'
dotnet build FleetDemo.sln -c Debug --nologo -v quiet
if ($LASTEXITCODE -ne 0) { throw 'Build failed.' }

if (-not (Test-Path (Join-Path $root 'ui\node_modules'))) {
    Write-Host 'Installing UI packages...'
    Push-Location (Join-Path $root 'ui')
    npm install --no-fund --no-audit
    Pop-Location
}

$shell = if (Get-Command pwsh -ErrorAction SilentlyContinue) { 'pwsh' } else { 'powershell' }

function Test-Port([int]$port) {
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $client.Connect('127.0.0.1', $port)
        $client.Close()
        return $true
    } catch { return $false }
}

function Start-Window([string]$title, [string]$command) {
    $script = "`$host.UI.RawUI.WindowTitle = '$title'; Set-Location '$root'; $command"
    Start-Process $shell -ArgumentList '-NoExit', '-NoProfile', '-Command', $script | Out-Null
    Write-Host "  started: $title"
}

$bin = 'bin\Debug\net8.0'
if (Test-Port 7233) {
    Write-Host '  Temporal is already running on 7233'
} else {
    Start-Window 'Temporal dev server' "& '$PSScriptRoot\start-temporal.ps1'"
    for ($i = 0; $i -lt 30 -and -not (Test-Port 7233); $i++) { Start-Sleep -Seconds 1 }
}

Start-Window 'External systems (mocks)' "dotnet src\Fleet.ExternalSystems\$bin\Fleet.ExternalSystems.dll"
foreach ($q in 'entities', 'dispatch', 'maintenance', 'integrations') {
    Start-Window "Worker: $q" "dotnet src\Fleet.Worker\$bin\Fleet.Worker.dll --queues $q"
}
Start-Window 'Fleet API' "dotnet src\Fleet.Api\$bin\Fleet.Api.dll"
Start-Window 'Fleet UI' "Set-Location ui; npm run dev"

Write-Host ''
Write-Host 'Fleet UI       http://localhost:5173'
Write-Host 'Temporal UI    http://localhost:8233'
Write-Host 'Give it about 20 seconds: the API creates the sync schedule, which starts the truck and driver workflows.'
Write-Host ''
Write-Host 'Kill a worker:     close its window, or scripts\stop-worker.ps1 maintenance'
Write-Host 'Bring it back:     scripts\worker.ps1 maintenance'
Write-Host 'Stop everything:   scripts\stop-all.ps1'
