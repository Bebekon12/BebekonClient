using System.Diagnostics;
using System.Security.Principal;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

public class ServiceInstallerTests
{
    [Fact]
    public void ElevationKeepsOriginalOwnerAndSeparatesLiteralPackagePath()
    {
        var root = Path.Combine(Path.GetTempPath(), "BebekonTests", Guid.NewGuid().ToString("N"), "package & literal $name");
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "scripts")); Directory.CreateDirectory(Path.Combine(root, "core"));
            File.WriteAllText(Path.Combine(root, "scripts", "install-service.ps1"), "");
            File.WriteAllBytes(Path.Combine(root, "Bebekon.Service.exe"), []);
            File.WriteAllBytes(Path.Combine(root, "core", "sing-box.exe"), []);
            var owner = WindowsIdentity.GetCurrent().User!.Value;
            var plan = ServiceInstaller.CreateStartInfo(root, owner);
            Assert.True(plan.UseShellExecute); Assert.Equal("runas", plan.Verb); Assert.Equal(ProcessWindowStyle.Hidden, plan.WindowStyle);
            Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"), plan.FileName);
            Assert.Equal(["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Path.Combine(root, "scripts", "install-service.ps1"), "-OwnerSid", owner], plan.ArgumentList.ToArray());
            Assert.Empty(plan.Arguments); Assert.DoesNotContain("-Command", plan.ArgumentList);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void PartialPackageFailsBeforeRequestingElevation() => Assert.Throws<UserError>(() =>
        ServiceInstaller.CreateStartInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")), WindowsIdentity.GetCurrent().User!.Value));

    [Fact]
    public void OwnerArgumentMustBeAWindowsSid() => Assert.Throws<ArgumentException>(() =>
        ServiceInstaller.CreateStartInfo(Path.GetTempPath(), "user -Command arbitrary"));
}
