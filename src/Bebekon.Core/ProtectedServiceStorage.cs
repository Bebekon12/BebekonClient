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
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current.FullName); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0 || (attributes & FileAttributes.Directory) == 0)
                throw new InvalidOperationException("Reparse points are forbidden in service storage and its ancestors.");
        }
        if (!directory.Exists) return;
        ValidateItem(directory);
        foreach (var item in EnumerateEntries(directory))
            ValidateItem(item);
    }

    // Yield each directory for the ACL check before descending into it.
    internal static IEnumerable<FileSystemInfo> EnumerateEntries(DirectoryInfo directory, int depth = 0)
    {
        foreach (var item in directory.EnumerateFileSystemInfos())
        {
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("Unexpected entries in service storage. Reinstall into protected storage.");
            if (item is DirectoryInfo && !(depth switch {
                0 => item.Name.ToLowerInvariant() is "zapret" or "zapret.new" or "zapret.old",
                1 => item.Name.ToLowerInvariant() is "bin" or "lists" or "profiles",
                _ => false
            }))
                throw new InvalidOperationException("Unexpected directory in service storage: " + item.Name);
            yield return item;
            if (item is DirectoryInfo child)
                foreach (var descendant in EnumerateEntries(child, depth + 1))
                    yield return descendant;
        }
    }
    public static void RemoveRuntime(string path)
    {
        var expected = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "BebekonVPN");
        if (!string.Equals(Path.GetFullPath(path), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Unexpected service storage path.");
        DeleteValidatedTree(new DirectoryInfo(path));
    }

    internal static void DeleteValidatedTree(DirectoryInfo root)
    {
        Validate(root.FullName);
        if (!root.Exists) return;
        // Older imports may have inherited a read-only attribute from the source archive.
        foreach (var file in EnumerateEntries(root).OfType<FileInfo>())
            if (file.IsReadOnly) file.IsReadOnly = false;
        root.Delete(true);
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
