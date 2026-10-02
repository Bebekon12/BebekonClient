using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace Bebekon.Core;

public static class VlessParser
{
    public static Server Parse(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var u) || u.Scheme != "vless") throw new UserError("Ожидается ссылка vless://.");
        if (!Guid.TryParse(u.UserInfo, out var id) || id == Guid.Empty) throw new UserError("В ссылке VLESS указан неверный идентификатор.");
        if (string.IsNullOrWhiteSpace(u.Host) || u.Port is < 1 or > 65535) throw new UserError("В ссылке отсутствует адрес или корректный порт.");
        var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = p.Split('=', 2); var key = Uri.UnescapeDataString(kv[0]);
            if (!q.TryAdd(key, kv.Length == 2 ? Uri.UnescapeDataString(kv[1]) : "")) throw new UserError("Ссылка содержит повторяющиеся параметры.");
        }
        string Get(string key, string fallback = "") => q.GetValueOrDefault(key, fallback);
        var s = new Server { Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.Trim())))[..24], Name = Uri.UnescapeDataString(u.Fragment.TrimStart('#')), Host = u.IdnHost.Trim('[', ']'), Port = u.Port, Uuid = id.ToString(), Security = Get("security", "none").ToLowerInvariant(), Sni = Get("sni", Get("serverName")), PublicKey = Get("pbk"), ShortId = Get("sid"), Fingerprint = Get("fp", "chrome"), Flow = Get("flow"), Transport = Get("type", "tcp").ToLowerInvariant(), ServiceName = Get("serviceName"), Path = Get("path", "/"), TransportHost = Get("host") };
        if (s.Name.Length == 0) s.Name = s.Host;
        if (s.Transport is not ("tcp" or "grpc" or "ws" or "http" or "httpupgrade")) s.UnsupportedReason = "Не поддерживается этой версией: " + s.Transport;
        if (s.Security is not ("none" or "tls" or "reality")) s.UnsupportedReason = "Не поддерживается тип защиты соединения.";
        if (s.Flow is not ("" or "xtls-rprx-vision")) s.UnsupportedReason = "Не поддерживается режим VLESS flow.";
        if (s.Flow.Length > 0 && (s.Security == "none" || s.Transport != "tcp")) s.UnsupportedReason = "Этот режим VLESS требует TCP и TLS / Reality.";
        if (Get("encryption", "none") != "none") s.UnsupportedReason = "Не поддерживается шифрование VLESS.";
        if (Get("headerType", "none") != "none") s.UnsupportedReason = "Не поддерживается маскировка TCP-заголовка.";
        if (Get("allowInsecure", "0") is "1" or "true") s.UnsupportedReason = "Небезопасный TLS в ссылке: проверка сертификата обязательна.";
        if (s.Security == "reality")
        {
            try { var b = s.PublicKey.Replace('-', '+').Replace('_', '/'); if (Convert.FromBase64String(b.PadRight((b.Length + 3) / 4 * 4, '=')).Length != 32) throw new FormatException(); }
            catch (FormatException) { throw new UserError("Неверный публичный ключ Reality."); }
            if (s.ShortId.Length > 16 || s.ShortId.Length % 2 != 0 || s.ShortId.Any(c => !Uri.IsHexDigit(c))) throw new UserError("Неверный короткий идентификатор Reality.");
            if (string.IsNullOrWhiteSpace(s.Sni)) throw new UserError("Для Reality необходимо имя сервера (SNI).");
        }
        if (q.ContainsKey("alpn") || q.ContainsKey("mode") && Get("mode") is not ("" or "gun")) s.UnsupportedReason = "Дополнительные параметры транспорта пока не поддерживаются.";
        return s;
    }
    public static List<Server> ParseSubscription(string content)
    {
        if (content.Length > 4 * 1024 * 1024) throw new UserError("Подписка слишком большая.");
        content = content.Trim().TrimStart('\uFEFF');
        if (!content.StartsWith("vless://", StringComparison.OrdinalIgnoreCase))
        {
            try { var b = string.Concat(content.Where(c => !char.IsWhiteSpace(c))).Replace('-', '+').Replace('_', '/'); content = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(b.PadRight((b.Length + 3) / 4 * 4, '='))); }
            catch (Exception e) when (e is FormatException or DecoderFallbackException) { throw new UserError("Неизвестный формат подписки. Нужны ссылки VLESS или Base64 с ними."); }
        }
        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0 || lines.Length > 5000) throw new UserError("Подписка пустая или содержит слишком много серверов.");
        var servers = new List<Server>();
        for (var i = 0; i < lines.Length; i++)
        {
            try { servers.Add(Parse(lines[i])); }
            catch (UserError e) { throw new UserError($"Строка {i + 1}: {e.Message}"); }
            catch (Exception e) when (e is UriFormatException or ArgumentException) { throw new UserError($"Строка {i + 1}: повреждённая ссылка VLESS."); }
        }
        return servers.DistinctBy(s => s.Id).ToList();
    }
}
public static class SubscriptionLoader
{
    public static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(20) };
    public static async Task<List<Server>> LoadAsync(string source, CancellationToken ct = default)
    {
        if (source.StartsWith("vless://", StringComparison.OrdinalIgnoreCase)) return [VlessParser.Parse(source)];
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || uri.UserInfo.Length > 0) throw new UserError("Укажите ссылку VLESS или URL подписки HTTP/HTTPS.");
        // Never redirect secret subscription paths to another server.
        using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new UserError($"Провайдер вернул HTTP {(int)response.StatusCode}. Проверьте подписку.");
        if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new UserError("Подписка слишком большая.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var output = new MemoryStream(); var buffer = new byte[16384]; int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0) { if (output.Length + n > 4 * 1024 * 1024) throw new UserError("Подписка слишком большая."); await output.WriteAsync(buffer.AsMemory(0, n), ct); }
        return VlessParser.ParseSubscription(Encoding.UTF8.GetString(output.ToArray()));
    }
}
