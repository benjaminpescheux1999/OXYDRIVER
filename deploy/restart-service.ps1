param(
    [string]$ServiceName = "OXYDRIVERService"
)

$ErrorActionPreference = "Stop"

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -eq $existing) {
    throw "Service '$ServiceName' not found."
}

Restart-Service -Name $ServiceName -Force
Write-Host "Service '$ServiceName' restarted."
