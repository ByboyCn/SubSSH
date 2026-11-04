$ErrorActionPreference = 'Continue'
$dir = 'D:\source\SubSSHMCP\smoke_webapi'
New-Item -ItemType Directory -Force $dir | Out-Null
$exe = 'D:\source\SubSSHMCP\src\core\SubSsh.Host\bin\Debug\net10.0\SubSsh.Host.dll'

# 确认 8080 未被占用（残留进程已清理）
$conn = Get-NetTCPConnection -LocalPort 8080 -State Listen -ErrorAction SilentlyContinue
if ($conn) { "PORT8080 BUSY pid=$($conn.OwningProcess)" | Set-Content "$dir\result.txt"; exit 1 }

$psi = [System.Diagnostics.ProcessStartInfo]::new()
$psi.FileName = 'dotnet'
$psi.Arguments = "`"$exe`""
$psi.WorkingDirectory = 'D:\source\SubSSHMCP'
$psi.RedirectStandardInput = $true
$psi.RedirectStandardOutput = $true
$psi.RedirectStandardError = $true
$psi.UseShellExecute = $false
$p = [System.Diagnostics.Process]::Start($psi)
$p.Id | Set-Content "$dir\pid.txt"

# 启动输出采集
$outTask = $p.StandardOutput.ReadToEndAsync()
$errTask = $p.StandardError.ReadToEndAsync()

Start-Sleep -Seconds 3
$p.StandardInput.WriteLine(':load *')
$p.StandardInput.Flush()

Start-Sleep -Seconds 10
$log = "== stdout ==" + "`n" + $outTask.Result + "`n== stderr ==" + "`n" + $errTask.Result

$r = @()
try { $h = Invoke-WebRequest -Uri 'http://127.0.0.1:8080/healthz' -UseBasicParsing -TimeoutSec 5; $r += "healthz: $($h.StatusCode) $($h.Content)" } catch { $r += "healthz: FAIL $($_.Exception.Message)" }
try { $hs = Invoke-WebRequest -Uri 'http://127.0.0.1:8080/api/v1/hosts' -UseBasicParsing -TimeoutSec 5; $r += "hosts: $($hs.StatusCode) $($hs.Content)" } catch { $r += "hosts: FAIL $($_.Exception.Message)" }
try { $k = Invoke-WebRequest -Uri 'http://127.0.0.1:8080/api/v1/keys' -UseBasicParsing -TimeoutSec 5; $r += "keys: $($k.StatusCode) $($k.Content)" } catch { $r += "keys: FAIL $($_.Exception.Message)" }

( $log + "`n== smoke ==" + "`n" + ($r -join "`n") ) | Set-Content "$dir\result.txt"
Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
