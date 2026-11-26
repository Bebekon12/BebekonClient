using System.Security.AccessControl;
using System.Security.Principal;
using Bebekon.Core;
using Bebekon.Service;
using Xunit;

namespace Bebekon.Tests;

public sealed class ElevatedImportFactAttribute : FactAttribute
{
    public ElevatedImportFactAttribute()
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            Skip = "Creating the production Administrators-owned ACL requires elevation.";
    }
}

public sealed class ZapretImportTests
{
    [ElevatedImportFact]
    public void ImportSurvivesRestartFailedReplacementAndInterruptedSwap()
    {
        var fixture = Directory.CreateTempSubdirectory("bebekon-import-");
        try
        {
            var admin = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);
            var acl = new DirectorySecurity();
            acl.SetOwner(admin);
            acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new(admin, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            var runtime = new DirectoryInfo(Path.Combine(fixture.FullName, "runtime"));
            runtime.Create(acl);
            var source = fixture.CreateSubdirectory("source");
            var bin = source.CreateSubdirectory("bin");
            source.CreateSubdirectory("lists");
            foreach (var file in new[] { "winws.exe", "WinDivert.dll", "WinDivert64.sys" })
                File.WriteAllText(Path.Combine(bin.FullName, file), "inert fixture");
            var profile = Path.Combine(source.FullName, "general.bat");
            File.WriteAllText(profile, "start \"\" \"%BIN%winws.exe\" --wf-tcp=443");
            ZapretRunner.Import(runtime.FullName, source.FullName);
            ProtectedServiceStorage.Validate(runtime.FullName);
            var home = Path.Combine(runtime.FullName, "zapret");
            using (ZapretRunner.LockImport(runtime.FullName))
                Assert.Throws<IOException>(() => ZapretRunner.Import(runtime.FullName, source.FullName));
            File.Delete(profile);
            Assert.Throws<UserError>(() => ZapretRunner.Import(runtime.FullName, source.FullName));
            Assert.False(Directory.Exists(home + ".new"));
            Directory.Move(home, home + ".old");
            ZapretRunner.RecoverImport(runtime.FullName);
            ProtectedServiceStorage.Validate(runtime.FullName);
            Assert.True(File.Exists(Path.Combine(home, "profiles", "general.bat")));
            using (ZapretRunner.LockImport(runtime.FullName))
                ProtectedServiceStorage.DeleteValidatedTree(runtime);
            ProtectedServiceStorage.DeleteValidatedTree(new DirectoryInfo(runtime.FullName));
            Assert.False(Directory.Exists(runtime.FullName));
            Assert.True(File.Exists(Path.Combine(bin.FullName, "winws.exe")));
        }
        finally { fixture.Delete(true); }
    }
}
