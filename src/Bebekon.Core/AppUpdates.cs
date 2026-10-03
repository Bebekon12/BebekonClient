using System.Net;
using System.Security.Cryptography;
using System.Text.Json;

namespace Bebekon.Core;

public sealed record UpdateRelease(string Version, string Installer, long Size, string Sha256, string Notes);
public sealed record SignedUpdate(string Payload, string Signature);
public sealed record AvailableUpdate(UpdateRelease Release, string Source);

/// <summary>The feed is untrusted until verified against the public key shipped with the app.</summary>
public sealed class AppUpdates(string publicKey) : IDisposable
{
    private const int MaxManifest = 64 * 1024;
    private const long MaxInstaller = 512L * 1024 * 1024;
    private readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.None }) { Timeout = Timeout.InfiniteTimeSpan };

    public static string NormalizeSource(string source)
    {
        source = source.Trim();
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo)) return uri.AbsoluteUri;
        if (Path.IsPathFullyQualified(source) && !source.StartsWith(@"\\") && Path.GetExtension(source).Equals(".json", StringComparison.OrdinalIgnoreCase)) return Path.GetFullPath(source);
        throw new UserError("Укажите HTTPS-адрес update.json или локальный JSON-файл обновлений.");
    }

    public UpdateRelease Verify(byte[] envelope)
    {
        try
        {
            if (envelope.Length > MaxManifest) throw new JsonException();
            var signed = JsonSerializer.Deserialize<SignedUpdate>(envelope, Json.Options) ?? throw new JsonException();
            var bytes = Convert.FromBase64String(signed.Payload);
            using var rsa = RSA.Create(); rsa.FromXmlString(publicKey);
            if (!rsa.VerifyData(bytes, Convert.FromBase64String(signed.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) throw new CryptographicException();
            var release = JsonSerializer.Deserialize<UpdateRelease>(bytes, Json.Options) ?? throw new JsonException();
            if (!System.Version.TryParse(release.Version, out var version) || version.Major < 0 || release.Size is <= 0 or > MaxInstaller ||
                release.Sha256 is null || release.Sha256.Length != 64 || !release.Sha256.All(Uri.IsHexDigit) ||
                string.IsNullOrWhiteSpace(release.Installer) || release.Notes is null || release.Notes.Length > 8000) throw new JsonException();
            return release;
        }
        catch (Exception e) when (e is JsonException or FormatException or CryptographicException or ArgumentException)
        { throw new UserError("Подпись или формат обновления не прошли проверку. Файл не будет запущен."); }
    }

    public async Task<AvailableUpdate?> CheckAsync(string source, Version current, CancellationToken token = default)
    {
        source = NormalizeSource(source);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        await using var stream = await OpenAsync(source, deadline.Token);
        using var data = new MemoryStream();
        await CopyLimitedAsync(stream, data, MaxManifest, null, deadline.Token);
        var release = Verify(data.ToArray());
        // Normalize missing version components so 0.1.8 and 0.1.8.0 compare equally.
        static Version Full(Version v) => new(v.Major, v.Minor, Math.Max(0, v.Build), Math.Max(0, v.Revision));
        if (Full(Version.Parse(release.Version)) <= Full(current)) return null;
        return new(release, ResolveInstaller(source, release.Installer));
    }

    internal static string ResolveInstaller(string feed, string installer)
    {
        if (Uri.TryCreate(installer, UriKind.Absolute, out var uri) && uri.Scheme == "https" && string.IsNullOrEmpty(uri.UserInfo)) return uri.AbsoluteUri;
        // Relative packages must be a sibling filename, never a command, share or traversal.
        if (installer.Length > 160 || installer.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '-' and not '_') || !installer.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new UserError("Некорректный адрес пакета обновления.");
        return feed.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? new Uri(new Uri(feed), installer).AbsoluteUri : Path.Combine(Path.GetDirectoryName(feed)!, installer);
    }

    public async Task<string> DownloadAsync(AvailableUpdate update, string directory, IProgress<int>? progress = null, CancellationToken token = default)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Bebekon-update-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromMinutes(5));
            await using (var input = await OpenAsync(update.Source, deadline.Token))
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920, true))
            {
                await CopyLimitedAsync(input, output, update.Release.Size, progress, deadline.Token);
                output.Position = 0; await VerifyInstallerAsync(output, update.Release, deadline.Token);
            }
            return path;
        }
        catch { if (File.Exists(path)) File.Delete(path); throw; }
    }

    public static async Task VerifyInstallerAsync(Stream stream, UpdateRelease release, CancellationToken token = default)
    {
        if (stream.Length != release.Size || !Convert.ToHexString(await SHA256.HashDataAsync(stream, token)).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
            throw new UserError("Файл обновления повреждён или подменён. Загрузите его заново.");
    }

    private async Task<Stream> OpenAsync(string source, CancellationToken token)
    {
        if (!source.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        for (var redirect = 0; redirect < 6; redirect++)
        {
            var response = await http.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, token);
            try
            {
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    var next = new Uri(new Uri(source), response.Headers.Location ?? throw new UserError("Источник вернул пустое перенаправление."));
                    if (next.Scheme != "https" || next.UserInfo.Length != 0) throw new UserError("Небезопасное перенаправление обновления заблокировано.");
                    source = next.AbsoluteUri; response.Dispose(); continue;
                }
                response.EnsureSuccessStatusCode(); return new ResponseStream(await response.Content.ReadAsStreamAsync(token), response);
            }
            catch { response.Dispose(); throw; }
        }
        throw new UserError("Слишком много перенаправлений источника обновления.");
    }

    private static async Task CopyLimitedAsync(Stream input, Stream output, long limit, IProgress<int>? progress, CancellationToken token)
    {
        var buffer = new byte[81920]; long total = 0; int count, last = -1;
        while ((count = await input.ReadAsync(buffer, token)) != 0)
        {
            total += count; if (total > limit) throw new UserError("Размер обновления превышает заявленный.");
            await output.WriteAsync(buffer.AsMemory(0, count), token);
            var percent = (int)(total * 100 / limit); if (percent != last) { progress?.Report(percent); last = percent; }
        }
    }

    private sealed class ResponseStream(Stream inner, HttpResponseMessage response) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default) => inner.ReadAsync(buffer, token);
        protected override void Dispose(bool disposing) { if (disposing) { inner.Dispose(); response.Dispose(); } base.Dispose(disposing); }
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
    public void Dispose() => http.Dispose();
}
