using System.Text;
using System.Text.RegularExpressions;

namespace Bebekon.Core;

/// <summary>A zapret (winws) profile: its file name, the validated arguments and a plain-language summary.</summary>
public sealed record ZapretProfile(string Name, string[] Arguments, string Summary)
{
    /// <summary>"fooling=ts" relies on TCP timestamps, which Windows has off by default.</summary>
    public bool NeedsTcpTimestamps => Arguments.Any(a => a.StartsWith("--dpi-desync-fooling=", StringComparison.Ordinal) && a.Contains("ts", StringComparison.Ordinal));
}
public sealed record ZapretStatus(bool Installed, bool Running, string? Profile, string? Error, string[]? Profiles = null, string[]? Summaries = null, DateTimeOffset? Imported = null);

/// <summary>
/// Reads flowseal/zapret-discord-youtube style general*.bat files without executing them. Only the
/// winws.exe argument list is taken; %BIN%, %LISTS% and the game-filter ports are substituted, and
/// every file argument must stay inside the protected copy.
/// </summary>
public static partial class Zapret
{
    public const string GameFilterOff = "12";
    [GeneratedRegex(@"^general(?: \([\w .-]+\))?\.bat$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    public static partial Regex ProfileName();
    public static readonly string[] UserLists = ["list-general-user.txt", "list-exclude-user.txt", "ipset-exclude-user.txt", "ipset-all.txt"];

    public static ZapretProfile Parse(string name, string batch, string binDirectory, string listsDirectory)
    {
        if (!ProfileName().IsMatch(name)) throw new UserError("Неизвестный профиль zapret.");
        // Join "^" line continuations, then take everything after the winws.exe path on its start line.
        var text = Regex.Replace(batch.Replace("\r", ""), @"\^\s*\n", " ");
        var line = text.Split('\n').FirstOrDefault(l => l.Contains("winws.exe\"", StringComparison.OrdinalIgnoreCase) && l.TrimStart().StartsWith("start ", StringComparison.OrdinalIgnoreCase))
            ?? throw new UserError($"В профиле {name} нет запуска winws.exe.");
        var tail = line[(line.IndexOf("winws.exe\"", StringComparison.OrdinalIgnoreCase) + "winws.exe\"".Length)..];
        var bin = Path.GetFullPath(binDirectory).TrimEnd('\\') + "\\";
        var lists = Path.GetFullPath(listsDirectory).TrimEnd('\\') + "\\";
        var args = new List<string>();
        foreach (var raw in Tokens(tail))
        {
            var token = raw.Replace("%BIN%", bin, StringComparison.OrdinalIgnoreCase).Replace("%LISTS%", lists, StringComparison.OrdinalIgnoreCase)
                .Replace("%GameFilterTCP%", GameFilterOff, StringComparison.OrdinalIgnoreCase).Replace("%GameFilterUDP%", GameFilterOff, StringComparison.OrdinalIgnoreCase)
                .Replace("%GameFilter%", GameFilterOff, StringComparison.OrdinalIgnoreCase);
            if (token.Contains('%') || token.IndexOfAny(['&', '|', '<', '>', '\n']) >= 0 || !token.StartsWith("--", StringComparison.Ordinal))
                throw new UserError($"Профиль {name} содержит неподдерживаемую команду: {raw}");
            // winws runs as SYSTEM: no debug log files, no @argument files, no paths outside the copy.
            if (token.StartsWith("--debug", StringComparison.Ordinal) || token.StartsWith("--dry-run", StringComparison.Ordinal))
                throw new UserError($"Профиль {name} содержит неподдерживаемую команду: {raw}");
            var value = token.Contains('=') ? token[(token.IndexOf('=') + 1)..] : "";
            if (value.StartsWith('@')) throw new UserError($"Профиль {name} содержит неподдерживаемую команду: {raw}");
            if (value.IndexOfAny(['\\', '/', ':']) >= 0)
            {
                // A file argument: only files of the protected copy.
                var full = Path.GetFullPath(value.Replace('/', '\\'));
                if (!full.StartsWith(bin, StringComparison.OrdinalIgnoreCase) && !full.StartsWith(lists, StringComparison.OrdinalIgnoreCase))
                    throw new UserError($"Профиль {name} ссылается на файл вне папки zapret.");
                token = token[..(token.IndexOf('=') + 1)] + full;
            }
            args.Add(token);
        }
        if (!args.Any(a => a.StartsWith("--wf-", StringComparison.Ordinal))) throw new UserError($"В профиле {name} нет фильтра трафика.");
        return new(name, [.. args], Describe(args));
    }

    /// <summary>Splits a cmd argument string, honouring double quotes (which are removed).</summary>
    internal static IEnumerable<string> Tokens(string text)
    {
        var current = new StringBuilder(); var quoted = false; var any = false;
        foreach (var c in text)
        {
            if (c == '"') { quoted = !quoted; any = true; continue; }
            if (char.IsWhiteSpace(c) && !quoted) { if (current.Length > 0 || any) { yield return current.ToString(); current.Clear(); any = false; } continue; }
            current.Append(c);
        }
        if (quoted) throw new UserError("В профиле zapret не закрыта кавычка.");
        if (current.Length > 0) yield return current.ToString();
    }

    /// <summary>What the profile does for YouTube/websites over TCP 443, in plain words.</summary>
    public static string Describe(IReadOnlyList<string> args)
    {
        // The section aimed at list-google (YouTube) or else the first TCP 443 section.
        var sections = Split(args);
        var main = sections.FirstOrDefault(s => s.Any(a => a.StartsWith("--filter-tcp=", StringComparison.Ordinal) && a.Contains("443")) && s.Any(a => a.Contains("list-google", StringComparison.OrdinalIgnoreCase)))
            ?? sections.FirstOrDefault(s => s.Any(a => a.StartsWith("--filter-tcp=", StringComparison.Ordinal) && a.Contains("443")));
        if (main is null) return "Стратегия не распознана";
        string? Value(string key) => main.FirstOrDefault(a => a.StartsWith(key + "=", StringComparison.Ordinal))?[(key.Length + 1)..];
        var desync = (Value("--dpi-desync") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries);
        var parts = new List<string>();
        foreach (var mode in desync)
            parts.Add(mode switch
            {
                "multisplit" => Value("--dpi-desync-split-seqovl") is not null ? "разрезание с подменой начала (seqovl)" : "разрезание на части",
                "multidisorder" => "разрезание и перемешивание частей",
                "fake" => "ложные пакеты",
                "fakedsplit" or "fakeddisorder" => "разрезание с ложными частями",
                "hostfakesplit" => "подмена имени сайта на www.google.com",
                "syndata" => "ложные данные в первом пакете (SYN)",
                _ => mode
            });
        var fooling = Value("--dpi-desync-fooling") switch
        {
            null => null,
            var f when f.Contains("badseq") => "подделки с неверным номером TCP",
            var f when f.Contains("ts") => "подделки с устаревшей меткой времени",
            var f when f.Contains("md5sig") => "подделки с подписью MD5",
            var f => f
        };
        if (fooling is not null) parts.Add(fooling);
        if (Value("--dpi-desync-fake-tls-mod") is { } mod && mod.Contains("rnd")) parts.Add("случайные поля TLS");
        if (Value("--dpi-desync-repeats") is { } repeats) parts.Add("повтор ×" + repeats);
        return parts.Count == 0 ? "Без обхода для сайтов" : string.Join(", ", parts.Distinct());
    }
    private static List<List<string>> Split(IReadOnlyList<string> args)
    {
        var sections = new List<List<string>> { new() };
        foreach (var a in args) { if (a == "--new") sections.Add([]); else sections[^1].Add(a); }
        return sections;
    }

    /// <summary>Validates a user list before the helper writes it into the protected copy.</summary>
    public static string[] ValidateList(string name, IEnumerable<string> lines)
    {
        if (!UserLists.Contains(name)) throw new UserError("Этот список нельзя изменить.");
        var networks = name.StartsWith("ipset-", StringComparison.Ordinal);
        var result = new List<string>();
        foreach (var raw in lines.Select(l => l.Trim()))
        {
            if (raw.Length == 0 || raw.StartsWith('#')) { result.Add(raw); continue; }
            var ok = networks ? System.Net.IPAddress.TryParse(raw.Split('/')[0], out _) && (!raw.Contains('/') || int.TryParse(raw.Split('/')[1], out var p) && p is >= 0 and <= 128)
                : Uri.CheckHostName(raw.TrimStart('^')) == UriHostNameType.Dns;
            if (!ok) throw new UserError("Проверьте запись: " + raw);
            result.Add(raw);
        }
        if (result.Count > 100_000) throw new UserError("Список слишком большой.");
        return [.. result];
    }
}
