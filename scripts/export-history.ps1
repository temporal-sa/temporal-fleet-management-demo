# Saves a workflow's event history for the replay test (tests\Fleet.Tests\ReplayTests.cs).
# Usage: scripts\export-history.ps1 maint-41207-001
param([Parameter(Mandatory = $true)][string]$WorkflowId)
$root = Split-Path -Parent $PSScriptRoot
$out = Join-Path $root "tests\Fleet.Tests\histories\$WorkflowId.json"
temporal workflow show --workflow-id $WorkflowId --output json | Out-File -FilePath $out -Encoding utf8
Write-Host "Saved $out. Run: dotnet test"
