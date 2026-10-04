using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Bebekon.Core;

public static class VlessParser
{
    public static Server Parse(string input)
    {
        if (!Uri.TryCreate(input.Trim(), UriKind.Absolute, out var u) || u.Scheme != "vless") throw new UserError("Ожидается ссылка vless://.");
        if (!Guid.TryParse(u.UserInfo, out var id)) throw new UserError("В ссылке VLESS указан неверный идентификатор.");
        if (string.IsNullOrWhiteSpace(u.Host) || u.Port is < 1 or > 65535) throw new UserError("В ссылке отсутствует адрес или корректный порт.");
        var q = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in u.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = p.Split('=', 2); var key = Uri.UnescapeDataString(kv[0]);
            if (!q.TryAdd(key, kv.Length == 2 ? Uri.UnescapeDataString(kv[1]) : "")) throw new UserError("Ссылка содержит повторяющиеся параметры.");
        }
        string Get(string key, string fallback = "") => q.GetValueOrDefault(key, fallback);
        var s = new Server { Id = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(input.Trim())))[..24], Name = Uri.UnescapeDataString(u.Fragment.TrimStart('#')), Host = u.IdnHost.Trim('[', ']'), Port = u.Port, Uuid = id.ToString(), Security = Get("security", "none").ToLowerInvariant(), Sni = Get("sni", Get("serverName")), PublicKey = Get("pbk"), ShortId = Get("sid"), Fingerprint = Get("fp", "chrome"), Flow = Get("flow"), Transport = Get("type", "tcp").ToLowerInvariant(), ServiceName = Get("serviceName"), Path = Get("path", "/"), TransportHost = Get("host") };
        if (s.Transport == "splithttp") s.Transport = "xhttp";
        s.XhttpMode = Get("mode", "auto"); s.XhttpExtra = Get("extra"); s.CertificatePin = Get("pcs").Replace(":", "").ToLowerInvariant(); s.VerifyCertificateName = Get("vcn");
        if (s.Transport == "xhttp") { XhttpConfig.Options(s); if (s.CertificatePin.Length > 0 && (s.CertificatePin.Length != 64 || s.CertificatePin.Any(c => !Uri.IsHexDigit(c)))) throw new UserError("Некорректный отпечаток TLS-сертификата XHTTP."); }
        else if (s.CertificatePin.Length > 0 || s.VerifyCertificateName.Length > 0) s.UnsupportedReason = "Отпечаток сертификата / vcn поддерживается только для XHTTP.";
        s.PacketEncoding = Get("packetEncoding", "xudp").ToLowerInvariant();
        s.UdpEnabled = Get("udp", "1") is "1" or "true";
        if (s.Transport == "xhttp" && s.PacketEncoding == "packetaddr") s.UnsupportedReason = "Кодирование packetaddr не поддерживается ядром XHTTP; используйте XUDP.";
        if (s.PacketEncoding is not ("none" or "xudp" or "packetaddr")) s.UnsupportedReason = "Не поддерживается кодирование UDP-пакетов VLESS.";
        if (Get("udp", "1") is not ("0" or "1" or "false" or "true")) s.UnsupportedReason = "Некорректный параметр UDP.";
        if (IPAddress.TryParse(s.Host, out var address) && (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any)))
            s.UnsupportedReason = "Провайдер вернул запись без рабочего адреса сервера. Проверьте подписку и привязку HWID.";
        if (s.Name.Length == 0) s.Name = s.Host;
        s.Alpn = Get("alpn").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        if (s.Transport is not ("tcp" or "grpc" or "ws" or "http" or "httpupgrade" or "xhttp" or "splithttp")) s.UnsupportedReason = "Не поддерживается этой версией: " + s.Transport;
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
        if (s.Transport != "xhttp" && q.ContainsKey("mode") && Get("mode") is not ("" or "gun") || s.Transport == "ws" && s.Path.Contains("?ed=", StringComparison.Ordinal)) s.UnsupportedReason = "Дополнительные параметры транспорта пока не поддерживаются.";
        return s;
    }
    public static List<Server> ParseSubscription(string content)
    {
        if (content.Length > 4 * 1024 * 1024) throw new UserError("Подписка слишком большая.");
        content = content.Trim().TrimStart('\uFEFF').TrimStart();
        if (content.StartsWith('<')) throw new UserError("Провайдер вернул веб-страницу вместо подписки. Нужна прямая ссылка на список серверов.");
        if (!ProtocolParser.IsLink(content) && !IsJson(content) && !IsYaml(content))
        {
            try { var b = string.Concat(content.Where(c => !char.IsWhiteSpace(c))).Replace('-', '+').Replace('_', '/'); content = new UTF8Encoding(false, true).GetString(Convert.FromBase64String(b.PadRight((b.Length + 3) / 4 * 4, '='))); }
            catch (Exception e) when (e is FormatException or DecoderFallbackException) { throw new UserError("Неизвестный формат подписки. Поддерживаются VPN-ссылки, Base64, JSON Xray и Clash/Mihomo YAML или JSON."); }
        }
        content = content.Trim().TrimStart('\uFEFF').TrimStart();
        if (content.StartsWith('<')) throw new UserError("Провайдер вернул веб-страницу вместо подписки. Нужна прямая ссылка на список серверов.");
        if (IsJson(content))
        {
            try
            {
                using var document = JsonDocument.Parse(content, new() { MaxDepth = 32, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("proxies", out _))
                    return ClashSubscriptionParser.Parse(JsonSerializer.Serialize(document.RootElement));
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty("outbounds", out var outbounds) && outbounds.ValueKind == JsonValueKind.Array && outbounds.EnumerateArray().Any(node => node.ValueKind == JsonValueKind.Object && node.TryGetProperty("type", out _))) return SingBoxSubscriptionParser.Parse(document.RootElement);
            }
            catch (JsonException)
            {
                if (IsYaml(content)) return ClashSubscriptionParser.Parse(content);
                throw new UserError("Повреждённая JSON-подписка.");
            }
            return XraySubscriptionParser.Parse(content);
        }
        if (IsYaml(content)) return ClashSubscriptionParser.Parse(content);
        var lines = content.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (lines.Length == 0 || lines.Length > 5000) throw new UserError("Подписка пустая или содержит слишком много серверов.");
        var servers = new List<Server>();
        for (var i = 0; i < lines.Length; i++)
        {
            try { servers.Add(ProtocolParser.Parse(lines[i])); }
            catch (UserError e) { throw new UserError($"Строка {i + 1}: {e.Message}"); }
            catch (Exception e) when (e is UriFormatException or ArgumentException) { throw new UserError($"Строка {i + 1}: повреждённая ссылка VPN."); }
        }
        return servers.DistinctBy(s => s.Id).ToList();
    }
    private static bool IsYaml(string content) => Regex.IsMatch(content, """(?:^|[\r\n,{])\s*['"]?(?:proxies|proxy-providers)['"]?\s*:""", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static bool IsJson(string content) => content.StartsWith('{') || content.StartsWith('[');
}
public static class SubscriptionLoader
{
    public static readonly HttpClient Http = new(new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, PooledConnectionLifetime = TimeSpan.FromMinutes(5) }) { Timeout = TimeSpan.FromSeconds(20) };
    public static async Task<List<Server>> LoadAsync(string source, CancellationToken ct = default)
    {
        source = source.Trim().TrimStart('\uFEFF').TrimStart();
        if (!source.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && !source.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            return VlessParser.ParseSubscription(source);
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0) throw new UserError("Укажите URL подписки HTTP/HTTPS без логина в адресе.");
        // Cover headers AND the streamed body. HttpClient's header timeout alone does not bound downloads.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(20));
        try
        {
            // Providers often choose their subscription format using the client User-Agent.
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("clash.meta BebekonVPN/" + (typeof(SubscriptionLoader).Assembly.GetName().Version?.ToString(3) ?? "1.0"));
            if (uri.Scheme == "https" || uri.IsLoopback)
            {
                request.Headers.Add("x-hwid", SubscriptionDeviceIdentity.Get());
                request.Headers.Add("x-device-os", "Windows");
                request.Headers.Add("x-ver-os", Environment.OSVersion.Version.ToString());
            }
            request.Headers.Accept.ParseAdd("text/yaml, application/yaml, application/json, text/plain, */*");
            // Never redirect secret subscription paths to another server.
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            bool HeaderFlag(string key) => response.Headers.TryGetValues(key, out var values) && values.Any(value => value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");
            if (HeaderFlag("x-hwid-max-devices-reached") || HeaderFlag("x-hwid-limit"))
                throw new UserError("Достигнут лимит устройств подписки. Удалите старое устройство в кабинете провайдера или обратитесь в поддержку.");
            if (HeaderFlag("x-hwid-not-supported"))
                throw new UserError("Провайдер не принял идентификатор устройства (HWID). Проверьте поддержку клиента у провайдера.");
            if (!response.IsSuccessStatusCode) throw new UserError($"Провайдер вернул HTTP {(int)response.StatusCode}. Проверьте подписку.");
            if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new UserError("Подписка слишком большая.");
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var output = new MemoryStream(); var buffer = new byte[16384]; int n;
            while ((n = await stream.ReadAsync(buffer, deadline.Token)) > 0)
            {
                if (output.Length + n > 4 * 1024 * 1024) throw new UserError("Подписка слишком большая.");
                await output.WriteAsync(buffer.AsMemory(0, n), deadline.Token);
            }
            try
            {
                var servers = VlessParser.ParseSubscription(new UTF8Encoding(false, true).GetString(output.ToArray()));
                if (servers.All(server => IPAddress.TryParse(server.Host, out var address) && (address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))))
                    throw new UserError("Провайдер вернул информационные записи вместо рабочих серверов. Проверьте HWID, лимит устройств и статус подписки в кабинете провайдера.");
                return servers;
            }
            catch (DecoderFallbackException) { throw new UserError("Подписка содержит повреждённый текст UTF-8."); }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new UserError("Провайдер не ответил за 20 секунд. Попробуйте обновить подписку позже.");
        }
    }
}
