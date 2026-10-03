param([string]$OwnerAccount, [string]$OwnerSid, [switch]$EnableStartup)
$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this script once as administrator to register the service.' }
function Set-ProtectedHelperAcl([IO.FileSystemInfo]$Item) {
    $admins = [Security.Principal.SecurityIdentifier]::new('S-1-5-32-544')
    if ($Item.PSIsContainer) {
        $acl = [Security.AccessControl.DirectorySecurity]::new()
        $inherit = [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'
    } else {
        $acl = [Security.AccessControl.FileSecurity]::new()
        $inherit = [Security.AccessControl.InheritanceFlags]::None
    }
    $acl.SetAccessRuleProtection($true, $false)
    $acl.SetOwner($admins)
    foreach ($sid in @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-32-545')) {
        $rights = if ($sid -eq 'S-1-5-32-545') { [Security.AccessControl.FileSystemRights]::ReadAndExecute } else { [Security.AccessControl.FileSystemRights]::FullControl }
        $rule = [Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new($sid), $rights, $inherit, [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)
        $acl.AddAccessRule($rule)
    }
    Set-Acl -LiteralPath $Item.FullName -AclObject $acl
}
$installRoot = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $installRoot 'Bebekon.Service.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'Service executable is missing.' }
$programFilesRoot = [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\') + '\'
if (-not [IO.Path]::GetFullPath($installRoot).StartsWith($programFilesRoot,[StringComparison]::OrdinalIgnoreCase)) {
    # A LocalSystem executable cannot remain in a user-writable portable directory.
    $protectedRoot = Join-Path $env:ProgramFiles 'Bebekon VPN Portable Helper'
    if (-not [IO.Path]::GetFullPath($protectedRoot).StartsWith($programFilesRoot,[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid helper destination.' }
    New-Item -ItemType Directory -Path $protectedRoot -Force | Out-Null
    if ((Get-Item -LiteralPath $protectedRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse helper directory is forbidden.' }
    foreach ($root in @($installRoot, $protectedRoot)) {
        if ((Get-Item -LiteralPath $root).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse package directory is forbidden.' }
        if (Get-ChildItem -LiteralPath $root -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Reparse package files are forbidden.' }
    }
    Set-ProtectedHelperAcl (Get-Item -LiteralPath $protectedRoot)
    # Release loaded executable/runtime files before replacing a portable helper.
    $existingHelper = Get-Service -Name BebekonVPN -ErrorAction SilentlyContinue
    if ($existingHelper -and $existingHelper.Status -ne 'Stopped') {
        Stop-Service -Name BebekonVPN
        $existingHelper.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(20))
    }
    Get-ChildItem -LiteralPath $installRoot | Copy-Item -Destination $protectedRoot -Recurse -Force
    Get-ChildItem -LiteralPath $protectedRoot -Recurse -Force | ForEach-Object { Set-ProtectedHelperAcl $_ }
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
if ($service) {
    if ($service.Status -ne 'Stopped') { Stop-Service -Name BebekonVPN; $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(20)) }
    $win32Service = Get-CimInstance Win32_Service -Filter "Name='BebekonVPN'"
    $result = Invoke-CimMethod -InputObject $win32Service -MethodName Change -Arguments @{PathName=('"' + $exe + '"');StartMode='Manual';StartName='LocalSystem'}
    if ($result.ReturnValue -ne 0) { throw 'Service update failed.' }
}
else { New-Service -Name BebekonVPN -BinaryPathName ('"' + $exe + '"') -StartupType Manual -DisplayName 'Bebekon VPN helper' | Out-Null }
Set-Service -Name BebekonVPN -Description 'Local protected helper for Bebekon VPN. Starts on demand.'
# UI may query and start the service. Only administrators may change its binary or ACL.
& sc.exe sdset BebekonVPN ('D:(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;SY)(A;;CCDCLCSWRPWPDTLOCRSDRCWDWO;;;BA)(A;;LCRP;;;' + $OwnerSid + ')')
if ($LASTEXITCODE -ne 0) { throw 'Service permissions could not be applied.' }
if ($EnableStartup) {
    $ownerHive = [Microsoft.Win32.Registry]::Users.OpenSubKey($OwnerSid, $true)
    if (-not $ownerHive) { throw 'The selected user must be logged in to configure startup.' }
    try { $runKey = $ownerHive.CreateSubKey('Software\Microsoft\Windows\CurrentVersion\Run'); try { $ui = Join-Path $installRoot 'Bebekon.App.exe'; $runKey.SetValue('BebekonVPN', ('"' + $ui + '" --tray')) } finally { $runKey.Dispose() } } finally { $ownerHive.Dispose() }
}
# Never repeatedly restart a crashing TUN core in the background.
Write-Host 'Bebekon service installed. Launch the UI without administrator privileges.'
