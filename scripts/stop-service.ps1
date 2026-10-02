$ErrorActionPreference = 'Stop'
$service = Get-Service -Name BebekonVPN -ErrorAction SilentlyContinue
if ($service -and $service.Status -ne 'Stopped') { Stop-Service -Name BebekonVPN; $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(25)) }
