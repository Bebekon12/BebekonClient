using System.Text;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

public class SecurityTests
{
    [Fact]
    public void HelperRejectsPrecreatedUnprivilegedRuntimeStorage()
    {
        var root = Path.Combine(Path.GetTempPath(), "bebekon-storage-security-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var acl = new System.Security.AccessControl.DirectorySecurity();
            var sid = System.Security.Principal.WindowsIdentity.GetCurrent().User!;
            acl.SetOwner(sid); acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new(sid, System.Security.AccessControl.FileSystemRights.FullControl,
                System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                System.Security.AccessControl.PropagationFlags.None, System.Security.AccessControl.AccessControlType.Allow));
            System.IO.FileSystemAclExtensions.SetAccessControl(new DirectoryInfo(root), acl);
            Assert.Throws<InvalidOperationException>(() => ProtectedServiceStorage.Validate(root));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task CleartextSubscriptionSecretsAreRejectedBeforeNetworkRequest()
    {
        var error = await Assert.ThrowsAsync<UserError>(() => SubscriptionLoader.LoadAsync("http://provider.example/private-subscription"));
        Assert.Contains("HTTPS", error.Message);
        Assert.DoesNotContain("private-subscription", error.Message);
    }
    [Theory]
    [InlineData("trojan://secret@vpn.example:443?allowInsecure=true")]
    [InlineData("hy2://secret@vpn.example:443?insecure=1")]
    public void UnsafeTlsImportsCannotBeConnected(string link)
    {
        var server = ProtocolParser.Parse(link);
        Assert.False(server.Supported);
        Assert.Contains("сертификата", server.UnsupportedReason);
        Assert.Throws<UserError>(() => ConfigGenerator.Generate(CoreTests.Spec(node: server)));
    }
    [Fact]
    public void PreviouslySavedUnsafeTlsCannotBypassTheServiceValidation()
    {
        var server = VlessParser.Parse($"vless://{CoreTests.Id}@vpn.example:443?security=tls");
        server.TlsInsecure = true;
        Assert.Throws<UserError>(() => ConfigGenerator.Generate(CoreTests.Spec(node: server)));
    }
    [Theory]
    [InlineData("tls;cert=C:/Windows/private.pem")]
    [InlineData("tls;c\\ert=C:/Windows/private.pem")]
    [InlineData("tls;certRaw=untrusted")]
    [InlineData("host=a;host=b")]
    [InlineData("mux=999999999")]
    public void SubscriptionPluginsCannotReadFilesOrInjectUnboundedOptions(string options)
    {
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-128-gcm:test"));
        Assert.Throws<UserError>(() => ProtocolParser.Parse($"ss://{auth}@vpn.example:443?plugin=" + Uri.EscapeDataString("v2ray-plugin;" + options)));
    }
    [Fact]
    public void SafeBuiltInPluginStillWorks()
    {
        var auth = Convert.ToBase64String(Encoding.UTF8.GetBytes("aes-128-gcm:test"));
        var server = ProtocolParser.Parse($"ss://{auth}@vpn.example:443?plugin=" + Uri.EscapeDataString("v2ray-plugin;tls;host=vpn.example;path=/socket;mode=websocket;mux=1"));
        Assert.True(server.Supported);
        Assert.Contains("v2ray-plugin", ConfigGenerator.Generate(CoreTests.Spec(node: server)));
    }
}
