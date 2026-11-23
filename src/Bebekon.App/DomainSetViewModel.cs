using System.Net.Http;
using System.Collections.ObjectModel;
using System.Windows.Input;
namespace Bebekon.App;

/// <summary>The user's own GeoSite sets: stored with the encrypted state, expanded before reaching the helper.</summary>
public sealed partial class MainViewModel : IDomainSetLibrary
{
    public IReadOnlyList<DomainSet> DomainSetList => Data.DomainSets;
    public IReadOnlyList<RoutingRule> CurrentRules => ActiveProfile.Rules;
    public ICommand OpenGroups => openGroups ??= new Command(_ => { Dialogs.DomainGroups(); RefreshRules(); });
    private ICommand? openGroups;
    internal void AttachDomainSets()
    {
        RuleValidation.CustomSetExists = name => Data.DomainSets.Any(set => set.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        Dialogs.Library = this;
    }
    public void SaveDomainSet(DomainSet? old, DomainSet set)
    {
        if (Data.DomainSets.Any(existing => !ReferenceEquals(existing, old) && existing.Name.Equals(set.Name, StringComparison.OrdinalIgnoreCase)))
            throw new UserError(T($"Группа «{set.Name}» уже есть. Выберите другое название.", $"Group ‘{set.Name}’ already exists. Choose another name."));
        var affected = old is null ? [] : DomainSets.RulesUsing(Data.Profiles, old).ToList();
        if (old is not null && !old.Name.Equals(set.Name, StringComparison.Ordinal))
            foreach (var rule in affected)
            {
                // Rules refer to sets by name; a rename keeps them pointing at the same set.
                rule.Values = rule.Values.Select(v => DomainSets.IsReference(v) && DomainSets.NameOf(v).Equals(old.Name, StringComparison.OrdinalIgnoreCase) ? DomainSets.Reference(set) : v).ToList();
                rule.Notify(nameof(RoutingRule.Description));
            }
        if (old is null || Data.DomainSets.IndexOf(old) is var index && index < 0) Data.DomainSets.Add(set);
        else Data.DomainSets[index] = set;
        Save();
        if (affected.Any(rule => rule.Enabled && ActiveProfile.Rules.Contains(rule))) QueueApply();
    }
    public int RulesUsingCount(DomainSet set) => DomainSets.RulesUsing(Data.Profiles, set).Count();
    public void AddGroupRule(DomainSet set, RouteTarget target)
    {
        var rule = new RoutingRule
        {
            CreatedAt = DateTimeOffset.UtcNow, Name = set.Name, Kind = RuleKind.GeoSite, Values = [DomainSets.Reference(set)],
            UseVpn = target != RouteTarget.Direct, Destination = target is RouteTarget.Direct or RouteTarget.Vpn ? null : target
        };
        RuleValidation.Validate(rule);
        ActiveProfile.Rules.Add(rule); RulesChanged();
    }
    /// <summary>Domains a rule contributes to a group: its sites, or the domains of its own groups.</summary>
    private IEnumerable<string>? GroupableDomains(RoutingRule rule) => rule.Kind switch
    {
        RuleKind.Site => rule.Values,
        RuleKind.Contains => rule.Values.Select(v => DomainSets.KeywordPrefix + v),
        RuleKind.Network => rule.Values,
        RuleKind.GeoSite when rule.Values.All(DomainSets.IsReference) => rule.Values.SelectMany(v => DomainSets.Find(Data.DomainSets, v)?.Domains ?? []),
        _ => null
    };
    internal bool CanMergeIntoGroup(IReadOnlyCollection<RoutingRule> rules) => rules.Count > 0 && rules.All(rule => GroupableDomains(rule) is not null);
    /// <summary>
    /// Puts the domains of the selected site rules into one new group. When the rules route the same
    /// way, offers to replace them with a single group rule at the highest selected position.
    /// </summary>
    internal void MergeIntoGroup(IReadOnlyCollection<RoutingRule> rules)
    {
        if (!CanMergeIntoGroup(rules)) throw new UserError(T("В группу можно объединить правила с сайтами, словами «Адрес содержит», IP-адресами и вашими группами. Программы и встроенные наборы — нельзя.", "Site, keyword, IP and your-group rules can be combined. Applications and bundled sets cannot."));
        var ordered = rules.OrderBy(rule => ActiveProfile.Rules.IndexOf(rule)).ToList();
        var domains = ordered.SelectMany(rule => GroupableDomains(rule)!).ToList();
        var suggested = new string(ordered[0].Name.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.').Take(40).ToArray());
        var made = Dialogs.DomainSetEditor(null, domains.Distinct(StringComparer.Ordinal), suggested.Length > 0 && Data.DomainSets.All(s => !s.Name.Equals(suggested, StringComparison.OrdinalIgnoreCase)) ? suggested : "");
        if (made is null) return;
        string Shape(RoutingRule rule) => System.Text.Json.JsonSerializer.Serialize(new { rule.Target, rule.ServerId, rule.Port, rule.Transport, rule.SystemProtection, rule.AllowedRoutes, rule.ForbiddenRoutes, rule.FallbackServerIds, rule.UnavailableRoute });
        if (ordered.Select(Shape).Distinct().Count() == 1 && ordered.All(rule => ActiveProfile.Rules.Contains(rule)) &&
            Dialogs.Confirm(T("Заменить правила группой?", "Replace rules with the group?"),
                T($"Выбранные правила ({ordered.Count}) ведут одинаково — {RouteChoice.NameOf(ordered[0].Target)}. Заменить их одним правилом «{made.Name}» на месте первого из них? Приоритет не изменится.",
                  $"The selected rules ({ordered.Count}) route the same way — {RouteChoice.NameOf(ordered[0].Target)}. Replace them with one rule ‘{made.Name}’ at the first one's place? Priority stays the same.")))
        {
            var first = ordered[0];
            var replacement = System.Text.Json.JsonSerializer.Deserialize<RoutingRule>(System.Text.Json.JsonSerializer.Serialize(first, Json.Options), Json.Options)!;
            replacement.Id = Guid.NewGuid().ToString("N"); replacement.CreatedAt = DateTimeOffset.UtcNow;
            replacement.Name = made.Name; replacement.Kind = RuleKind.GeoSite; replacement.Values = [DomainSets.Reference(made)];
            RuleValidation.Validate(replacement);
            var index = ActiveProfile.Rules.IndexOf(first);
            foreach (var rule in ordered) { rule.PropertyChanged -= OnRuleChanged; ActiveProfile.Rules.Remove(rule); }
            ActiveProfile.Rules.Insert(Math.Min(index, ActiveProfile.Rules.Count), replacement);
            RulesChanged();
            Banner = T($"Группа «{made.Name}» создана и заменила {ordered.Count} правил.", $"Group ‘{made.Name}’ created and replaced {ordered.Count} rules.");
        }
        else Banner = T($"Группа «{made.Name}» создана. Её можно выбрать в правиле GeoSite.", $"Group ‘{made.Name}’ created. Choose it in a GeoSite rule.");
    }
    // ---- Recent domains, lookup, links and followed URLs ----
    private RecentDestination[] recentDomains = [];
    public IReadOnlyList<RecentDestination> RecentDomains => recentDomains;
    public ObservableCollection<RecentRow> RecentRows { get; } = [];
    private string recentSearch = "";
    public string RecentSearch { get => recentSearch; set { if (Set(ref recentSearch, value)) ShowRecent(); } }
    public string RecentSummary => !Connected ? T("Подключите VPN — здесь появятся домены, к которым обращаются приложения.", "Connect VPN to see the domains your apps reach.")
        : recentDomains.Length == 0 ? T("Пока пусто. Откройте нужный сайт или программу и нажмите «Обновить».", "Nothing yet. Open the site or app, then press Refresh.")
        : T($"Последние 15 минут · {recentDomains.Length}. Выделите домены и нажмите «В группу».", $"Last 15 minutes · {recentDomains.Length}. Select domains and press Add to group.");
    public ICommand RefreshRecent => refreshRecent ??= Async(_ => RefreshRecentDomainsAsync());
    private ICommand? refreshRecent;
    /// <summary>Asks the helper for domains the running tunnel carried recently (memory only, cleared on disconnect).</summary>
    public async Task RefreshRecentDomainsAsync()
    {
        if (testProbe is null && Connected)
        {
            try { var reply = await service.SendAsync(new("RecentDestinations"), ct: lifetime.Token); if (reply.Ok) recentDomains = reply.Recent ?? []; }
            catch (Exception e) when (e is not OperationCanceledException || !lifetime.IsCancellationRequested) { }
        }
        else if (!Connected) recentDomains = [];
        ShowRecent();
    }
    internal void SetRecentDomainsForTest(RecentDestination[] value) { recentDomains = value; ShowRecent(); }
    private void ShowRecent()
    {
        RecentRows.Clear();
        foreach (var entry in recentDomains.Where(e => recentSearch.Length == 0 || e.Host.Contains(recentSearch, StringComparison.OrdinalIgnoreCase) || (e.Process ?? "").Contains(recentSearch, StringComparison.OrdinalIgnoreCase)))
        {
            var groups = GroupsContaining(entry.Host).Select(set => set.Name).ToArray();
            var ago = DateTimeOffset.UtcNow - entry.LastSeen;
            var when = ago < TimeSpan.FromMinutes(1) ? T("только что", "just now") : T($"{(int)ago.TotalMinutes} мин назад", $"{(int)ago.TotalMinutes} min ago");
            RecentRows.Add(new(entry, entry.Route switch { "VPN" => "Shield", "NetBird" => "Network", "Block" => "Close", _ => "Servers" },
                string.Join(" · ", new[] { entry.Process, entry.Port > 0 ? T("порт ", "port ") + entry.Port : null, when }.OfType<string>()),
                groups.Length == 0 ? null : "★ " + string.Join(", ", groups)));
        }
        Notify(nameof(RecentSummary));
    }
    private static bool Covers(string domain, string host) => host.Equals(domain, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + domain, StringComparison.OrdinalIgnoreCase);
    internal IEnumerable<DomainSet> GroupsContaining(string host) => Data.DomainSets.Where(set => set.Domains.Any(d => DomainSets.KindOf(d) switch
    {
        RuleKind.Site => Covers(d, host),
        RuleKind.Contains => host.Contains(DomainSets.ValueOf(d), StringComparison.OrdinalIgnoreCase),
        _ => false
    }));
    /// <summary>Registrable-looking root (last two labels; three for short second-level zones like co.uk).</summary>
    internal static string Root(string host)
    {
        var labels = host.ToLowerInvariant().Split('.', StringSplitOptions.RemoveEmptyEntries);
        var take = labels.Length >= 3 && labels[^2].Length <= 3 && labels[^1].Length == 2 ? 3 : 2;
        return string.Join('.', labels.TakeLast(Math.Min(take, labels.Length)));
    }
    /// <summary>Recent domains related to <paramref name="domains"/> (same root or same app) that the list does not cover yet.</summary>
    public IReadOnlyList<string> SuggestRelated(IReadOnlyCollection<string> domains, IEnumerable<string>? processes = null)
    {
        var roots = domains.Where(d => DomainSets.KindOf(d) == RuleKind.Site).Select(Root).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var apps = (processes ?? []).Where(p => p.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return recentDomains.Where(e => (roots.Contains(Root(e.Host)) || e.Process is { } p && apps.Contains(p)) && !domains.Any(d => DomainSets.KindOf(d) == RuleKind.Site && Covers(d, e.Host)))
            .Select(e => e.Host).Distinct(StringComparer.OrdinalIgnoreCase).Take(24).ToList();
    }
    /// <summary>"git.hai.example → ★ hai → rule #3 → NetBird": which groups and which rule decide this domain.</summary>
    public string DescribeDomain(string text)
    {
        string host;
        try { host = RuleValidation.SiteHost(text.Trim(), dropWww: false).ToLowerInvariant(); } catch (UserError) { return ""; }
        if (!host.Contains('.') || host.Contains(' ')) return "";
        var groups = GroupsContaining(host).Select(set => "★ " + set.Name).ToArray();
        RouteAnalysis decision;
        try { decision = RoutingPolicy.Analyze(ExpandedProfile, new RouteQuery(Domain: host)); } catch (UserError e) { return e.Message; }
        var rule = decision.Rule is { } matched ? ActiveProfile.Rules.FirstOrDefault(r => matched.Id == r.Id || matched.Id.StartsWith(r.Id + "-", StringComparison.Ordinal)) : null;
        var parts = new List<string> { host };
        parts.Add(groups.Length == 0 ? T("не входит в ваши группы", "not in your groups") : T("в группах ", "in groups ") + string.Join(", ", groups));
        parts.Add(rule is null ? T("правило не найдено — «Остальной трафик»", "no rule — Other traffic") : T($"правило №{rule.Priority} «{rule.Name}»", $"rule #{rule.Priority} ‘{rule.Name}’"));
        parts.Add(RouteChoice.NameOf(decision.Direction));
        return string.Join(" → ", parts);
    }
    public async Task<DomainSet> SubscribeDomainSetAsync(string name, string url)
    {
        using var http = new HttpClient();
        var domains = await DomainSets.FetchAsync(url, http, lifetime.Token);
        var set = DomainSets.Normalize(name, domains, style: new() { SourceUrl = url.Trim(), Updated = DateTimeOffset.Now, Icon = "Link", Color = "Cyan" });
        SaveDomainSet(null, set); return set;
    }
    public async Task RefreshDomainSetAsync(DomainSet set)
    {
        if (set.SourceUrl is not { } url) return;
        using var http = new HttpClient();
        var domains = await DomainSets.FetchAsync(url, http, lifetime.Token);
        var current = Data.DomainSets.FirstOrDefault(s => s.Id == set.Id) ?? set;
        SaveDomainSet(current, DomainSets.Normalize(current.Name, domains, current.Id, new() { SourceUrl = url, Updated = DateTimeOffset.Now, Icon = current.Icon, Color = current.Color }));
    }
    /// <summary>Groups that follow a URL refresh at start and every six hours; a failure keeps the old domains.</summary>
    private async Task AutoRefreshDomainSetsAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(30), lifetime.Token);
            while (!lifetime.IsCancellationRequested)
            {
                foreach (var set in Data.DomainSets.Where(s => s.SourceUrl is not null && (s.Updated is null || DateTimeOffset.Now - s.Updated > TimeSpan.FromHours(6))).ToArray())
                    try { await RefreshDomainSetAsync(set); log.Write("Domain group refreshed."); }
                    catch (UserError) { log.Write("Domain group refresh failed."); }
                await Task.Delay(TimeSpan.FromMinutes(30), lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
    }
    /// <summary>From Connections: puts the chosen domains into an existing or a new group.</summary>
    internal void AddDomainsToGroup(IReadOnlyList<RecentDestination> picked)
    {
        if (picked.Count == 0) { Banner = T("Выделите домены в списке.", "Select domains in the list."); return; }
        var hosts = picked.Select(p => p.Host).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var related = SuggestRelated(hosts, picked.Select(p => p.Process ?? ""));
        if (Dialogs.AddToGroup(hosts, related) is { } group) { Banner = T($"Добавлено в группу «{group.Name}».", $"Added to group ‘{group.Name}’."); ShowRecent(); }
    }
    public void DeleteDomainSet(DomainSet set)
    {
        var used = DomainSets.RulesUsing(Data.Profiles, set).Select(rule => rule.Name).Distinct().ToList();
        if (used.Count > 0)
            throw new UserError(T("Группа используется в правилах: ", "The group is used by rules: ") + string.Join(", ", used.Take(5)) + (used.Count > 5 ? "…" : "") +
                T(". Сначала измените или удалите их.", ". Change or delete them first."));
        Data.DomainSets.Remove(set); Save();
    }
}

public sealed record RecentRow(RecentDestination Entry, string Icon, string Detail, string? Groups)
{
    public string Host => Entry.Host;
    public string Route => Entry.Route;
    public bool InGroup => Groups is not null;
}
