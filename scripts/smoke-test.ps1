param([Parameter(Mandatory=$true)][string]$Installer)
$ErrorActionPreference = 'Stop'
$installLog = Join-Path $env:RUNNER_TEMP 'mggx-install.log'
Start-Process $Installer -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART',('/LOG="' + $installLog + '"') -Wait
$service = Get-Service -Name 'MGGXPCAgent'
if ($service.Status -ne 'Running') { throw "Service is not running: $($service.Status)" }
$deadline = (Get-Date).AddSeconds(30)
do { try { $health = Invoke-RestMethod 'http://127.0.0.1:8766/health' -TimeoutSec 2; break } catch { Start-Sleep -Milliseconds 500 } } while ((Get-Date) -lt $deadline)
if (-not $health.ok) { throw 'Health endpoint failed' }
$tokenPath = Join-Path $env:ProgramData 'MGGX\PC-Agent\agent-token.bin'
$entropy = [Text.Encoding]::UTF8.GetBytes('MGGX.PCAgent.Token.v1')
$plain = [Security.Cryptography.ProtectedData]::Unprotect([IO.File]::ReadAllBytes($tokenPath), $entropy, [Security.Cryptography.DataProtectionScope]::LocalMachine)
$token = [Text.Encoding]::UTF8.GetString($plain)
$status = Invoke-RestMethod 'http://127.0.0.1:8766/api/v1/status' -Headers @{ Authorization = "Bearer $token" } -TimeoutSec 3
if (-not $status.ok) { throw 'Authorized status endpoint failed' }
Stop-Service 'MGGXPCAgent'
Start-Service 'MGGXPCAgent'
if ((Get-Service 'MGGXPCAgent').Status -ne 'Running') { throw 'Service restart smoke test failed' }
$uninstaller = Join-Path ${env:ProgramFiles} 'MGGX\PC Agent\unins000.exe'
Start-Process $uninstaller -ArgumentList '/VERYSILENT','/SUPPRESSMSGBOXES','/NORESTART' -Wait
if (Get-Service 'MGGXPCAgent' -ErrorAction SilentlyContinue) { throw 'Uninstall did not remove service' }
