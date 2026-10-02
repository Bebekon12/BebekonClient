param([switch]$RemoveSettings)
$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run as administrator.' }
$service = Get-Service BebekonVPN -ErrorAction SilentlyContinue
$imagePath = (Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\BebekonVPN' -Name ImagePath -ErrorAction SilentlyContinue).ImagePath
if ($service) { if ($service.Status -ne 'Stopped') { Stop-Service BebekonVPN; $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(20)) }; & sc.exe delete BebekonVPN; if ($LASTEXITCODE -ne 0) { throw 'Could not delete the service.' } }
Remove-Item -LiteralPath 'HKLM:\SOFTWARE\BebekonVPN' -Force -ErrorAction SilentlyContinue
$portableHelper = [IO.Path]::GetFullPath((Join-Path $env:ProgramFiles 'Bebekon VPN Portable Helper'))
if ($imagePath -and $imagePath.Trim('"') -eq (Join-Path $portableHelper 'Bebekon.Service.exe')) {
    if (-not $portableHelper.StartsWith([IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\') + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Unsafe helper target.' }
    Remove-Item -LiteralPath $portableHelper -Recurse -Force
}
if ($RemoveSettings) {
    $target = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'BebekonVPN'))
    $expected = [IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\') + '\BebekonVPN'
    if ($target -ne $expected) { throw 'Unsafe settings target.' }
    Remove-Item -LiteralPath $target -Recurse -Force -ErrorAction SilentlyContinue
}
