param([string]$OwnerAccount, [string]$OwnerSid)
$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script once as administrator to register the service.' }
$installRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $installRoot 'Bebekon.Service.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Service executable is missing.' }
$programFilesRoot = [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\') + '\'
if (-not [IO.Path]::GetFullPath($installRoot).StartsWith($programFilesRoot,[StringComparison]::OrdinalIgnoreCase)) {
    # A LocalSystem executable cannot remain in a user-writable portable directory.
    $protectedRoot = Join-Path $env:ProgramFiles 'Bebekon VPN Portable Helper'
    New-Item -ItemType Directory -Path $protectedRoot -Force | Out-Null
    if ((Get-Item -LiteralPath $protectedRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse helper directory is forbidden.' }
    Get-ChildItem -LiteralPath $installRoot | Copy-Item -Destination $protectedRoot -Recurse -Force
    $exe = Join-Path $protectedRoot 'Bebekon.Service.exe'
}
if (-not $OwnerSid) {
    if ($OwnerAccount) { $OwnerSid = ([Security.Principal.NTAccount]::new($OwnerAccount)).Translate([Security.Principal.SecurityIdentifier]).Value }
    else { $OwnerSid = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value }
}
$null = [Security.Principal.SecurityIdentifier]::new($OwnerSid)
New-Item -Path 'HKLM:\SOFTWARE\BebekonVPN' -Force | Out-Null
Set-ItemProperty -Path 'HKLM:\SOFTWARE\BebekonVPN' -Name OwnerSid -Value $OwnerSid
$service = Get-Service -Name BebekonVPN -ErrorAction SilentlyContinue
if ($service) { if ($service.Status -ne 'Stopped') { Stop-Service -Name BebekonVPN; $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(20)) }; & sc.exe config BebekonVPN binPath= ('"' + $exe + '"') start= demand obj= LocalSystem }
else { & sc.exe create BebekonVPN binPath= ('"' + $exe + '"') start= demand obj= LocalSystem DisplayName= 'Bebekon VPN helper' }
if ($LASTEXITCODE -ne 0) { throw 'Service registration failed.' }
& sc.exe description BebekonVPN 'Local protected helper for Bebekon VPN. Starts on demand.'
# UI may query and start the service. Only administrators may change its binary or ACL.
& sc.exe sdset BebekonVPN ('D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;LCRP;;;' + $OwnerSid + ')')
if ($LASTEXITCODE -ne 0) { throw 'Service permissions could not be applied.' }
# Never repeatedly restart a crashing TUN core in the background.
& sc.exe failure BebekonVPN reset= 0 actions= ''
Write-Host 'Bebekon service installed. Launch the UI without administrator privileges.'
