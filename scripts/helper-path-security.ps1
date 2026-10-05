function Assert-NoHelperReparsePath([string]$Path) {
    $item = Get-Item -LiteralPath ([IO.Path]::GetFullPath($Path)) -Force
    while ($item) {
        if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Reparse points are forbidden in the helper path.' }
        $item = if ($item -is [IO.DirectoryInfo]) { $item.Parent } else { $item.Directory }
    }
}
function Assert-TrustedHelperParents([string]$Path, [string]$ProgramFilesRoot) {
    $boundary = [IO.Path]::GetFullPath($ProgramFilesRoot).TrimEnd('\')
    $parent = [IO.DirectoryInfo]::new([IO.Path]::GetFullPath($Path)).Parent
    while ($parent -and $parent.FullName -ne $boundary) {
        if (-not $parent.FullName.StartsWith($boundary + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Helper must remain under Program Files.' }
        Assert-TrustedHelperAcl $parent.FullName
        $parent = $parent.Parent
    }
    if (-not $parent) { throw 'Helper must remain under Program Files.' }
}
function Assert-TrustedHelperAcl([string]$Path) {
    $trusted = @('S-1-5-18', 'S-1-5-32-544', 'S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464')
    $writeRights = [Security.AccessControl.FileSystemRights]'Write, Delete, DeleteSubdirectoriesAndFiles, ChangePermissions, TakeOwnership'
    $acl = Get-Acl -LiteralPath $Path
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -notin $trusted) { throw 'A helper file or directory has an untrusted owner. Reinstall into a protected folder.' }
    foreach ($rule in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
            -not ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -and
            ($rule.FileSystemRights -band $writeRights) -and $rule.IdentityReference.Value -notin $trusted) {
            throw 'A helper file or directory is writable by a non-administrator. Reinstall into a protected folder.'
        }
    }
}
