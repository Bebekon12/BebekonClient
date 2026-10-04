param([int]$Seconds = 10)
$ErrorActionPreference='Stop'
$names=@('Bebekon.App','Bebekon.Service','sing-box','xray')
$initial=@{}
Get-Process -Name $names -ErrorAction SilentlyContinue | ForEach-Object { $initial[$_.Id]=$_.TotalProcessorTime.TotalMilliseconds }
Start-Sleep -Seconds $Seconds
$rows=Get-Process -Name $names -ErrorAction SilentlyContinue | ForEach-Object {
    $_.Refresh()
    [pscustomobject]@{Process=$_.ProcessName;PID=$_.Id;WorkingSetMB=[math]::Round($_.WorkingSet64/1MB,1);PrivateMB=[math]::Round($_.PrivateMemorySize64/1MB,1);CpuPercent=if($initial.ContainsKey($_.Id)){[math]::Round(($_.TotalProcessorTime.TotalMilliseconds-$initial[$_.Id])/($Seconds*1000)/[Environment]::ProcessorCount*100,3)}else{$null}}
}
$rows | Format-Table -AutoSize
[pscustomobject]@{TotalWorkingSetMB=($rows | Measure-Object WorkingSetMB -Sum).Sum;SampleSeconds=$Seconds} | Format-List
