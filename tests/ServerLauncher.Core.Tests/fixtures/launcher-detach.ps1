# Mimics a real-world launcher script: starts a long-running child process, then
# exits immediately without waiting for it. This is the shape of the Arma 3 server
# scripts, which call $p.Start() and fall off the end of the file.
$pinfo = New-Object System.Diagnostics.ProcessStartInfo
$pinfo.FileName = "ping.exe"
$pinfo.Arguments = "-t 127.0.0.1"
$pinfo.UseShellExecute = $false
$pinfo.CreateNoWindow = $true

$p = New-Object System.Diagnostics.Process
$p.StartInfo = $pinfo
[void]$p.Start()

Write-Output "LAUNCHER: started child pid $($p.Id)"
Write-Output "LAUNCHER: exiting immediately, child keeps running"
