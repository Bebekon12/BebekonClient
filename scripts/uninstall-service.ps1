param([switch]$RemoveSettings)
$ErrorActionPreference = 'Stop'
$principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run as administrator.' }
. (Join-Path $PSScriptRoot 'helper-path-security.ps1')
$serviceKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\BebekonVPN'
$ownerKey = 'HKLM:\SOFTWARE\BebekonVPN'
$ownerSid = (Get-ItemProperty -LiteralPath $ownerKey -Name OwnerSid -ErrorAction SilentlyContinue).OwnerSid
if ($RemoveSettings -and $ownerSid -and $ownerSid -ne $principal.Identity.User.Value) {
    throw 'Remove settings from the VPN owner account, or use Setup to select the owner profile.'
}
$service = Get-Service BebekonVPN -ErrorAction SilentlyContinue
$portableRoot = Join-Path $env:ProgramFiles 'Bebekon VPN Portable Helper'
$portableExe = Join-Path $portableRoot 'Bebekon.Service.exe'
$packageExe = Join-Path (Split-Path -Parent $PSScriptRoot) 'Bebekon.Service.exe'
$programPrefix = [IO.Path]::GetFullPath($env:ProgramFiles).TrimEnd('\') + '\'
$registeredExe = (Get-ItemProperty -LiteralPath $serviceKey -Name ImagePath -ErrorAction SilentlyContinue).ImagePath
if ($registeredExe) { $registeredExe = $registeredExe.Trim().Trim('"') }
$runtimeRoot = Join-Path ([Environment]::GetFolderPath('CommonApplicationData')) 'BebekonVPN'
$step = 20
$disabled = $false
try {
    if (Get-Process -Name Bebekon.App -ErrorAction SilentlyContinue) { $step = 10; throw 'Exit Bebekon VPN from the tray first.' }
    $helper = $null
    foreach ($candidate in @($packageExe, $registeredExe, $portableExe)) {
        if (!$candidate -or !([IO.Path]::GetFullPath($candidate).StartsWith($programPrefix,[StringComparison]::OrdinalIgnoreCase))) { continue }
        if ([IO.Path]::GetFileName($candidate) -ne 'Bebekon.Service.exe' -or !(Test-Path -LiteralPath $candidate -PathType Leaf)) { continue }
        $directory = Split-Path -Parent $candidate
        Assert-NoHelperReparsePath $candidate
        Assert-TrustedHelperParents $directory $env:ProgramFiles
        Assert-TrustedHelperAcl $directory
        $library = [IO.Path]::ChangeExtension($candidate, '.dll')
        if (!(Test-Path -LiteralPath $library -PathType Leaf)) { continue }
        Assert-TrustedHelperAcl $candidate
        Assert-NoHelperReparsePath $library
        Assert-TrustedHelperAcl $library
        if ([version][Diagnostics.FileVersionInfo]::GetVersionInfo($library).FileVersion -ge [version]'0.1.35') {
            $helper = $candidate
            break
        }
    }
    if (!$helper -and ($service -or $ownerSid -or (Test-Path -LiteralPath $runtimeRoot))) {
        throw 'Restore the current Bebekon Setup to its installation folder and retry.'
    }
    if ($ownerSid -and $helper) {
        $profileKey = 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\' + $ownerSid
        $profilePath = (Get-ItemProperty -LiteralPath $profileKey -ErrorAction SilentlyContinue).ProfileImagePath
        if ($profilePath -and (Test-Path -LiteralPath (Join-Path $profilePath 'AppData\Local\BebekonVPN\proxy-backup.dpapi'))) {
            $step = 12
            if ($ownerSid -ne $principal.Identity.User.Value) { throw 'Restore the system proxy from the VPN owner account.' }
            $ui = Join-Path (Split-Path -Parent $helper) 'Bebekon.App.exe'
            Assert-NoHelperReparsePath $ui
            Assert-TrustedHelperAcl $ui
            if ([version][Diagnostics.FileVersionInfo]::GetVersionInfo([IO.Path]::ChangeExtension($ui, '.dll')).FileVersion -lt [version]'0.1.35') {
                $step = 20
                throw 'Restore the current Setup before uninstalling.'
            }
            $restoreProxy = Start-Process -FilePath $ui -ArgumentList '--restore-owned-proxy' -PassThru -Wait -WindowStyle Hidden
            if ($restoreProxy.ExitCode -ne 0) { throw 'Could not restore the owner proxy settings.' }
        }
    }
    if ($service) {
        $step = 11
        $oldStart = (Get-ItemProperty -LiteralPath $serviceKey -Name Start).Start
        Set-Service -Name BebekonVPN -StartupType Disabled
        $disabled = $true
        $service.Refresh()
        if ($service.Status -ne 'Stopped') {
            if ($service.Status -ne 'StopPending') { Stop-Service -Name BebekonVPN }
            $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(25))
        }
    }
    if ($helper) {
        $step = 20
        & $helper --uninstall-runtime
        if ($LASTEXITCODE -ne 0) {
            if ($LASTEXITCODE -in 21,22) { $step = $LASTEXITCODE }
            throw 'Bebekon helper cleanup failed.'
        }
    }
    if ($service) {
        $step = 23
        $service.Dispose()
        & sc.exe delete BebekonVPN
        if ($LASTEXITCODE -ne 0) { throw 'Could not delete the service.' }
        $disabled = $false
    }
    $step = 24
    if ($ownerSid) {
        $runKey = [Microsoft.Win32.Registry]::Users.OpenSubKey($ownerSid + '\Software\Microsoft\Windows\CurrentVersion\Run', $true)
        if ($runKey) { try { $runKey.DeleteValue('BebekonVPN', $false) } finally { $runKey.Dispose() } }
    }
    if (($registeredExe -eq $portableExe -or (Test-Path -LiteralPath $portableExe) -or (!$service -and $ownerSid)) -and (Test-Path -LiteralPath $portableRoot)) {
        Assert-NoHelperReparsePath $portableRoot
        Assert-TrustedHelperAcl $portableRoot
        Get-ChildItem -LiteralPath $portableRoot -Recurse -Force | ForEach-Object {
            if ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Unexpected link in the portable helper.' }
            Assert-TrustedHelperAcl $_.FullName
        }
        Remove-Item -LiteralPath $portableRoot -Recurse -Force
    }
    if (Test-Path -LiteralPath $ownerKey) { Remove-Item -LiteralPath $ownerKey -Force }
}
catch {
    [Console]::Error.WriteLine($_.Exception.Message)
    if ($disabled) {
        $restore = @{2='Automatic'; 3='Manual'; 4='Disabled'}
        Set-Service -Name BebekonVPN -StartupType $restore[[int]$oldStart] -ErrorAction Continue
    }
    exit $step
}
if ($RemoveSettings) {
    $target = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA 'BebekonVPN'))
    $expected = [IO.Path]::GetFullPath($env:LOCALAPPDATA).TrimEnd('\') + '\BebekonVPN'
    if ($target -ne $expected) { throw 'Unsafe settings target.' }
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
}
