# redeploy.ps1 - Build and redeploy ConnectBoxService to 102.214.69.233
# Usage: .\redeploy.ps1

$ErrorActionPreference = "Stop"

$ServiceName = "Connect Box Service"
$RemoteHost  = "102.214.69.233"
$RemotePort  = "3030"
$RemoteUser  = "Administrator"
$RemotePass  = "AF25ibATXCPm"
$HostKey     = "SHA256:2r326nhfHEngyVloT8oyKVl5PJGvAbJ5aovA3KP1J/4"
$RemotePath  = "C:/Services/ConnectBoxService/"
$ProjectFile = "$PSScriptRoot\ConnectBoxService\ConnectBoxService.csproj"
$PublishOut  = "$PSScriptRoot\ConnectBoxService\publish_out"
$Plink       = "C:\Program Files\PuTTY\plink.exe"
$Pscp        = "C:\Program Files\PuTTY\pscp.exe"

function Remote([string]$cmd) {
    & $Plink -batch -pw $RemotePass -P $RemotePort -hostkey $HostKey "${RemoteUser}@${RemoteHost}" $cmd
}

# ── 1. Publish ────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "==> [1/5] Publishing..." -ForegroundColor Cyan
dotnet publish $ProjectFile -c Release -r win-x64 --self-contained true -o $PublishOut --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Publish failed." }
Write-Host "    Build OK" -ForegroundColor Green

# ── 2. Stop service ───────────────────────────────────────────────────────────
Write-Host ""
Write-Host "==> [2/5] Stopping service..." -ForegroundColor Cyan
Remote "powershell -Command `"Stop-Service -Name 'Connect Box Service' -Force -ErrorAction SilentlyContinue`""

$waited = 0
do {
    Start-Sleep -Seconds 2
    $waited += 2
    $raw   = Remote "powershell -Command `"(Get-Service -Name 'Connect Box Service' -ErrorAction SilentlyContinue).Status`""
    $state = ($raw -join "").Trim()
    Write-Host "    Status: $state ($waited s)"
} while ($state -ne "Stopped" -and $waited -lt 30)

if ($state -ne "Stopped") {
    Write-Warning "Service still running after 30 s - killing process."
    Remote "powershell -Command `"Get-Process ConnectBoxService -ErrorAction SilentlyContinue | Stop-Process -Force`""
    Start-Sleep -Seconds 3
}
Write-Host "    Stopped." -ForegroundColor Green

# ── 3. Copy files ─────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "==> [3/5] Copying files..." -ForegroundColor Cyan
& $Pscp -batch -pw $RemotePass -P $RemotePort -hostkey $HostKey `
    -r "$PublishOut\*" "${RemoteUser}@${RemoteHost}:${RemotePath}" | Out-Null
if ($LASTEXITCODE -ne 0) { throw "File copy failed." }
Write-Host "    Files copied." -ForegroundColor Green

# ── 4. Start service ──────────────────────────────────────────────────────────
Write-Host ""
Write-Host "==> [4/5] Starting service..." -ForegroundColor Cyan
Remote "powershell -Command `"Start-Service -Name 'Connect Box Service'`""
Start-Sleep -Seconds 4

# ── 5. Verify ─────────────────────────────────────────────────────────────────
Write-Host ""
Write-Host "==> [5/5] Verifying..." -ForegroundColor Cyan
$raw    = Remote "powershell -Command `"(Get-Service -Name 'Connect Box Service').Status`""
$status = ($raw -join "").Trim()
Write-Host "    Status: $status"

if ($status -eq "Running") {
    Write-Host ""
    Write-Host "[OK] Connect Box Service is RUNNING on $RemoteHost." -ForegroundColor Green
} else {
    throw "Service did not reach Running state (got: $status). Check event viewer on server."
}
