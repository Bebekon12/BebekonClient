using System.Collections.ObjectModel;
using System.Windows.Input;

namespace Bebekon.App;

/// <summary>Connections → recent domains: what the running tunnel carried lately, ready to become a rule.</summary>
public sealed partial class MainViewModel
{
    /// <summary>Domain groups were replaced by folders; the helper and the app no longer accept my: references.</summary>
    internal void AttachDomainSets() => RuleValidation.CustomSetExists = null;

    private RecentDestination[] recentDomains = [];
    public IReadOnlyList<RecentDestination> RecentDomains => recentDomains;
    public ObservableCollection<RecentRow> RecentRows { get; } = [];
    private string recentSearch = "";
    public string RecentSearch { get => recentSearch; set { if (Set(ref recentSearch, value)) ShowRecent(); } }
    public string RecentSummary => !Connected ? T("Подключите VPN — здесь появятся домены, к которым обращаются приложения.", "Connect VPN to see the domains your apps reach.")
        : recentDomains.Length == 0 ? T("Пока пусто. Откройте нужный сайт или программу и нажмите «Обновить».", "Nothing yet. Open the site or app, then press Refresh.")
        : T($"Последние 15 минут · {recentDomains.Length}. Выделите домены и нажмите «Создать правило».", $"Last 15 minutes · {recentDomains.Length}. Select domains and press Create rule.");
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
        var profile = ActiveProfile;
        foreach (var entry in recentDomains.Where(e => recentSearch.Length == 0 || e.Host.Contains(recentSearch, StringComparison.OrdinalIgnoreCase) || (e.Process ?? "").Contains(recentSearch, StringComparison.OrdinalIgnoreCase)))
        {
            var ago = DateTimeOffset.UtcNow - entry.LastSeen;
            var when = ago < TimeSpan.FromMinutes(1) ? T("только что", "just now") : T($"{(int)ago.TotalMinutes} мин назад", $"{(int)ago.TotalMinutes} min ago");
            RoutingRule? rule = null;
            try { rule = RoutingPolicy.Analyze(profile, new RouteQuery(Domain: entry.Host)).Rule; } catch (UserError) { }
            var match = rule is null ? null : profile.Rules.FirstOrDefault(r => r.Id == rule.Id);
            RecentRows.Add(new(entry, entry.Route switch { "VPN" => "Shield", "NetBird" => "Network", "Block" => "Close", _ => "Servers" },
                string.Join(" · ", new[] { entry.Process, entry.Port > 0 ? T("порт ", "port ") + entry.Port : null, when }.OfType<string>()),
                match is null ? null : T("правило ", "rule ") + "«" + match.Name + "»"));
        }
        Notify(nameof(RecentSummary));
    }
    /// <summary>From Connections: opens the usual rule editor with the chosen domains, so route and folder are picked as for any rule.</summary>
    internal void AddRuleForDomains(IReadOnlyList<RecentDestination> picked)
    {
        if (picked.Count == 0) { Banner = T("Выделите домены в списке.", "Select domains in the list."); return; }
        var hosts = picked.Select(p => p.Host).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var template = new RoutingRule { Kind = RuleKind.Site, Values = hosts, Name = hosts.Count == 1 ? hosts[0] : hosts[0] + " +" + (hosts.Count - 1), CreatedAt = DateTimeOffset.UtcNow, UseVpn = true };
        var rule = Dialogs.Rule(template, Data.Servers, draft => Dialogs.RouteCheck(this, draft), NetBirdEnabled, ActiveProfile.Folders, create: true);
        if (rule is null) return;
        InsertRule(rule); ShowRecent();
        Banner = T($"Правило «{rule.Name}» добавлено.", $"Rule ‘{rule.Name}’ added.");
    }
}

public sealed record RecentRow(RecentDestination Entry, string Icon, string Detail, string? Groups)
{
    public string Host => Entry.Host;
    public string Route => Entry.Route;
    public bool InGroup => Groups is not null;
}
