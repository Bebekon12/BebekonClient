using System.Security.Cryptography;
using System.Text.Json;
using Bebekon.Core;
using Xunit;

namespace Bebekon.Tests;

public sealed class AppUpdateTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "BebekonUpdates", Guid.NewGuid().ToString("N"));
    private readonly RSA key = RSA.Create(2048);
    private readonly byte[] package = "synthetic update package, never executed"u8.ToArray();
    public AppUpdateTests() => Directory.CreateDirectory(root);
    private UpdateRelease Release(string version = "0.1.9") => new(version, "Bebekon-0.1.9.exe", package.Length, Convert.ToHexString(SHA256.HashData(package)), "Release notes");
    private byte[] Sign(UpdateRelease release)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(release, Json.Options);
        return JsonSerializer.SerializeToUtf8Bytes(new SignedUpdate(Convert.ToBase64String(data), Convert.ToBase64String(key.SignData(data, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))), Json.Options);
    }
    private string Feed(UpdateRelease release)
    {
        var path = Path.Combine(root, "update.json"); File.WriteAllBytes(path, Sign(release)); File.WriteAllBytes(Path.Combine(root, "Bebekon-0.1.9.exe"), package); return path;
    }
    [Fact] public async Task SignedLocalFeedDownloadsVerifiedPackageWithoutTouchingUserData()
    {
        using var client = new AppUpdates(key.ToXmlString(false)); var feed = Feed(Release());
        var state = Path.Combine(root, "state.dpapi"); File.WriteAllBytes(state, [1, 2, 3]);
        var update = await client.CheckAsync(feed, new(0, 1, 8, 0)); Assert.NotNull(update);
        var path = await client.DownloadAsync(update, Path.Combine(root, "download"));
        Assert.Equal(package, File.ReadAllBytes(path)); Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(state));
        await using var file = File.OpenRead(path); await AppUpdates.VerifyInstallerAsync(file, update.Release);
    }
    [Theory] [InlineData("0.1.8")] [InlineData("0.1.8.0")] [InlineData("0.1.7")]
    public async Task NeverOffersSameOrOlderVersion(string version)
    { using var client = new AppUpdates(key.ToXmlString(false)); Assert.Null(await client.CheckAsync(Feed(Release(version)), new(0, 1, 8, 0))); }
    [Fact] public void WrongSigningKeyAndTamperedPayloadAreRejected()
    {
        using var other = RSA.Create(2048); using var wrong = new AppUpdates(other.ToXmlString(false));
        Assert.Throws<UserError>(() => wrong.Verify(Sign(Release())));
        var envelope = JsonSerializer.Deserialize<SignedUpdate>(Sign(Release()), Json.Options)!;
        using var client = new AppUpdates(key.ToXmlString(false));
        var modified = envelope with { Payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(Release("9.0.0"), Json.Options)) };
        Assert.Throws<UserError>(() => client.Verify(JsonSerializer.SerializeToUtf8Bytes(modified, Json.Options)));
        Assert.Throws<UserError>(() => client.Verify("{}"u8.ToArray()));
    }
    [Theory] [InlineData(0)] [InlineData(-1)] [InlineData(1)]
    public async Task TruncatedOverlongOrCorruptDownloadIsDeleted(int change)
    {
        using var client = new AppUpdates(key.ToXmlString(false)); var feed = Feed(Release());
        var update = (await client.CheckAsync(feed, new(0, 1, 8)))!;
        var bad = change == -1 ? package[..^1] : change == 1 ? package.Concat(new byte[] { 0 }).ToArray() : package.Select(b => (byte)(b ^ 1)).ToArray();
        File.WriteAllBytes(update.Source, bad); var folder = Path.Combine(root, "download");
        await Assert.ThrowsAsync<UserError>(() => client.DownloadAsync(update, folder)); Assert.Empty(Directory.GetFiles(folder));
    }
    [Fact] public async Task CancellationLeavesNoPartialInstaller()
    {
        using var client = new AppUpdates(key.ToXmlString(false)); var update = (await client.CheckAsync(Feed(Release()), new(0, 1, 8)))!;
        using var cancel = new CancellationTokenSource(); cancel.Cancel(); var folder = Path.Combine(root, "download");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.DownloadAsync(update, folder, token: cancel.Token)); Assert.Empty(Directory.GetFiles(folder));
    }
    [Theory] [InlineData("../bad.exe")] [InlineData("http://host/setup.exe")] [InlineData("C:\\setup.exe")] [InlineData("\\\\host\\setup.exe")] [InlineData("setup.exe /silent")]
    public async Task SignedButUnsafeInstallerLocationIsRejected(string location)
    {
        using var client = new AppUpdates(key.ToXmlString(false));
        await Assert.ThrowsAsync<UserError>(() => client.CheckAsync(Feed(Release() with { Installer = location }), new(0, 1, 8)));
    }
    [Fact] public void RemoteSiblingStaysHttpsAndInvalidFeedSourcesAreRejected()
    {
        Assert.Equal("https://releases.example/versions/setup.exe", AppUpdates.ResolveInstaller("https://releases.example/versions/update.json", "setup.exe"));
        Assert.Throws<UserError>(() => AppUpdates.NormalizeSource("http://releases.example/update.json"));
        Assert.Throws<UserError>(() => AppUpdates.NormalizeSource(@"\\server\update.json"));
        Assert.Throws<UserError>(() => AppUpdates.NormalizeSource("update.json"));
    }
    public void Dispose() { key.Dispose(); Directory.Delete(root, true); }
}
