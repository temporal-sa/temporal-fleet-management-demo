# Hard-kills the worker process for one task queue, like a node dying. Workflows pause; nothing is lost.
param([Parameter(Mandatory = $true)][string]$Queue)
$killed = 0
Get-CimInstance Win32_Process |
    Where-Object { $_.CommandLine -and $_.CommandLine -like '*Fleet.Worker.dll*' -and $_.CommandLine -like "*--queues $Queue*" } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue; $killed++ }
if ($killed -gt 0) { Write-Host "Killed the $Queue worker. Watch its pill turn red in the fleet UI." }
else { Write-Host "No $Queue worker is running." }
