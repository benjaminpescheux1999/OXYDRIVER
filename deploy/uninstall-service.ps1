param(
    [string]$ServiceName = "OXYDRIVERService"
)

$ErrorActionPreference = "Stop"

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -eq $existing) {
    Write-Host "Service '$ServiceName' not found."
    exit 0
}

try {
    Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
} catch {
    # ignore stop issues before delete
}

sc.exe delete $ServiceName | Out-Null
Write-Host "Service '$ServiceName' removed."
