param(
    [string]$ServiceName = "OXYDRIVERService",
    [string]$DisplayName = "OXYDRIVER Service",
    [string]$ExePath = "C:\Program Files\OXYDRIVER\OXYDRIVER.exe",
    [string]$StartMode = "auto"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $ExePath)) {
    throw "Executable not found: $ExePath"
}

$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
if ($null -ne $existing) {
    throw "Service '$ServiceName' already exists. Use uninstall-service.ps1 first."
}

$binPath = "`"$ExePath`" --service"
sc.exe create $ServiceName binPath= $binPath start= $StartMode DisplayName= $DisplayName | Out-Null
sc.exe description $ServiceName "OXYDRIVER background runtime (gateway, tunnel, API sync)." | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/10000/restart/20000 | Out-Null
sc.exe start $ServiceName | Out-Null

Write-Host "Service '$ServiceName' installed and started."
