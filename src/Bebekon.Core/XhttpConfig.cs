using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Bebekon.Core;

/// <summary>Whitelist transport tuning only. Raw Xray configs, paths, commands and dialer settings never cross IPC.</summary>
internal static class XhttpConfig
{
    internal static JsonObject Options(Server s)
    {
        if (s.XhttpMode is not ("auto" or "packet-up" or "stream-up" or "stream-one")) throw new UserError("Неизвестный режим XHTTP.");
        var result = new JsonObject { ["host"] = s.TransportHost, ["path"] = s.Path, ["mode"] = s.XhttpMode };
        if (s.XhttpExtra.Length == 0) return result;
        if (s.XhttpExtra.Length > 16384) throw Invalid();
        try
        {
            using var doc = JsonDocument.Parse(s.XhttpExtra, new() { MaxDepth = 4 });
            var source = doc.RootElement; if (source.ValueKind != JsonValueKind.Object) throw Invalid();
            var keys = new HashSet<string>();
            foreach (var p in source.EnumerateObject())
            {
                if (!keys.Add(p.Name)) throw Invalid();
                switch (p.Name)
                {
                    case "noGRPCHeader": case "noSSEHeader": case "xPaddingObfsMode":
                        if (p.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw Invalid(); break;
                    case "xPaddingBytes": case "scMaxEachPostBytes": case "scMinPostsIntervalMs": case "scStreamUpServerSecs": case "uplinkChunkSize":
                        Range(p.Value); break;
                    case "scMaxBufferedPosts": case "serverMaxHeaderBytes":
                        if (!p.Value.TryGetInt32(out var number) || number is < 0 or > 16_777_216) throw Invalid(); break;
                    case "xPaddingKey": case "xPaddingHeader": case "xPaddingPlacement": case "xPaddingMethod": case "uplinkHTTPMethod": case "sessionPlacement": case "sessionKey": case "seqPlacement": case "seqKey": case "uplinkDataPlacement": case "uplinkDataKey":
                        Text(p.Value, 256); break;
                    case "headers":
                        if (p.Value.ValueKind != JsonValueKind.Object || p.Value.EnumerateObject().Count() > 32) throw Invalid();
                        var headers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var h in p.Value.EnumerateObject()) { if (!headers.Add(h.Name) || h.Name.Equals("Host", StringComparison.OrdinalIgnoreCase) || h.Name.Length > 128 || h.Name.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-')) throw Invalid(); Text(h.Value, 2048); } break;
                    case "xmux":
                        if (p.Value.ValueKind != JsonValueKind.Object) throw Invalid();
                        var xmux = new HashSet<string>();
                        foreach (var x in p.Value.EnumerateObject()) { if (!xmux.Add(x.Name) || x.Name is not ("maxConcurrency" or "maxConnections" or "cMaxReuseTimes" or "hMaxRequestTimes" or "hMaxReusableSecs" or "hKeepAlivePeriod")) throw Invalid(); Range(x.Value); } break;
                    default: throw Invalid();
                }
                result[p.Name] = JsonNode.Parse(p.Value.GetRawText());
            }
            return result;
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException) { throw Invalid(); }
    }
    private static void Text(JsonElement v, int max) { if (v.ValueKind != JsonValueKind.String || v.GetString()!.Length > max || v.GetString()!.Any(char.IsControl)) throw Invalid(); }
    private static void Range(JsonElement v)
    {
        if (v.ValueKind == JsonValueKind.Number) { if (!v.TryGetInt32(out var n) || n is < 0 or > 16_777_216) throw Invalid(); return; }
        Text(v, 32); var parts = v.GetString()!.Split('-');
        if (parts.Length is < 1 or > 2 || parts.Any(p => !int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) || n is < 0 or > 16_777_216) || parts.Length == 2 && int.Parse(parts[0]) > int.Parse(parts[1])) throw Invalid();
    }
    private static UserError Invalid() => new("Некорректные или неподдерживаемые дополнительные параметры XHTTP.");
}
