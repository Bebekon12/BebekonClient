using System.Windows.Input;
namespace Bebekon.App;

/// <summary>The user's own GeoSite sets: stored with the encrypted state, expanded before reaching the helper.</summary>
public sealed partial class MainViewModel : IDomainSetLibrary
{
    public IReadOnlyList<DomainSet> DomainSetList => Data.DomainSets;
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
        if (!CanMergeIntoGroup(rules)) throw new UserError(T("В группу можно объединить правила «По суффиксу» и правила с вашими группами.", "Only site rules and rules with your groups can be combined."));
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
    public void DeleteDomainSet(DomainSet set)
    {
        var used = DomainSets.RulesUsing(Data.Profiles, set).Select(rule => rule.Name).Distinct().ToList();
        if (used.Count > 0)
            throw new UserError(T("Группа используется в правилах: ", "The group is used by rules: ") + string.Join(", ", used.Take(5)) + (used.Count > 5 ? "…" : "") +
                T(". Сначала измените или удалите их.", ". Change or delete them first."));
        Data.DomainSets.Remove(set); Save();
    }
}
