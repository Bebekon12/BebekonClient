using System.Security.AccessControl;
using System.Security.Principal;

namespace Bebekon.Core;

/// <summary>Reject pre-created writable storage before a privileged helper uses it.</summary>
public static class ProtectedServiceStorage
{
    private static readonly HashSet<string> Trusted = new(StringComparer.Ordinal)
    {
        "S-1-5-18", "S-1-5-32-544",
        "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464"
    };
    public static void Validate(string root)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(root));
        for (var current = directory; current is not null; current = current.Parent)
            if (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Reparse points are forbidden in service storage and its ancestors.");
        if (!directory.Exists) return;
        ValidateItem(directory);
        // Runtime storage contains only our own files. Refuse foreign directories,
        // links and handles obtained through an earlier user-writable file ACL.
        foreach (var item in directory.EnumerateFileSystemInfos())
        {
            if (item is DirectoryInfo || (item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Unexpected entries in service storage. Reinstall into protected storage.");
            ValidateItem(item);
        }
    }
    private static void ValidateItem(FileSystemInfo item)
    {
        FileSystemSecurity acl = item is DirectoryInfo directory ? directory.GetAccessControl() : ((FileInfo)item).GetAccessControl();
        if (acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !Trusted.Contains(owner.Value))
            throw new InvalidOperationException("Service storage has an untrusted owner.");
        const FileSystemRights write = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                (rule.FileSystemRights & write) != 0 && !Trusted.Contains(rule.IdentityReference.Value))
                throw new InvalidOperationException("Service storage is writable by a non-administrator.");
    }
}
