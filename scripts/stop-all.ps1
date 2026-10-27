# Stops every demo process. The dev server is in-memory, so the next start is a clean demo.
$root = Split-Path -Parent $PSScriptRoot
Get-CimInstance Win32_Process |
    Where-Object {
        $_.CommandLine -and (
            $_.CommandLine -match 'Fleet\.(Worker|Api|ExternalSystems)\.dll' -or
            ($_.CommandLine -like "*$root*" -and $_.CommandLine -like '*vite*') -or
            $_.CommandLine -match 'temporal(\.exe)?"?\s+server\s+start-dev')
    } |
    ForEach-Object {
        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
        Write-Host "stopped $($_.ProcessId): $($_.Name)"
    }
