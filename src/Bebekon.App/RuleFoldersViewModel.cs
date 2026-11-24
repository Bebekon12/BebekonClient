using System.ComponentModel;
using System.Windows.Input;
using System.Windows.Media;

namespace Bebekon.App;

/// <summary>A folder header in the rules list.</summary>
public sealed class FolderRow(RuleFolder folder) : Observable
{
    public RuleFolder Folder { get; } = folder;
    private string detail = "";
    public string Detail { get => detail; set => Set(ref detail, value); }
    private bool canMoveUp, canMoveDown;
    public bool CanMoveUp { get => canMoveUp; set => Set(ref canMoveUp, value); }
    public bool CanMoveDown { get => canMoveDown; set => Set(ref canMoveDown, value); }
    public Brush Accent => new SolidColorBrush(Dialogs.GroupColors.GetValueOrDefault(Folder.Color, Dialogs.GroupColors["Blue"]));
    public Brush Tint { get { var c = ((SolidColorBrush)Accent).Color; return new SolidColorBrush(Color.FromArgb(0x30, c.R, c.G, c.B)); } }
    public void Restyle() { Notify(nameof(Accent)); Notify(nameof(Tint)); }
}

/// <summary>
/// Rules list with folders. A folder is only a wrapper: its rules keep their own routes and are kept
/// next to each other in the priority order, so numbers inside a folder run consecutively.
/// </summary>
public sealed partial class MainViewModel
{
    private readonly Dictionary<string, FolderRow> folderRows = [];
    private static int RuleGroup(RoutingRule rule) => rule.SystemProtection ? 0 : 1;
    private List<RoutingRule> PriorityOrder() => ActiveProfile.Rules.Select((rule, index) => (rule, index)).OrderBy(x => RuleGroup(x.rule)).ThenBy(x => x.index).Select(x => x.rule).ToList();
    private RuleFolder? FolderOf(RoutingRule rule) => rule.FolderId is { } id ? ActiveProfile.Folders.FirstOrDefault(f => f.Id == id) : null;

    /// <summary>Keeps every folder's rules contiguous at the place of its first rule; clears links to deleted folders.</summary>
    private void CompactFolders()
    {
        var known = ActiveProfile.Folders.Select(f => f.Id).ToHashSet();
        foreach (var rule in ActiveProfile.Rules.Where(r => r.FolderId is { } id && !known.Contains(id))) rule.FolderId = null;
        var order = new List<RoutingRule>(); var placed = new HashSet<string>();
        foreach (var rule in ActiveProfile.Rules)
        {
            if (rule.FolderId is not { } id) { order.Add(rule); continue; }
            if (placed.Add(id)) order.AddRange(ActiveProfile.Rules.Where(r => r.FolderId == id));
        }
        for (var i = 0; i < order.Count; i++)
        {
            var at = ActiveProfile.Rules.IndexOf(order[i]);
            if (at != i) ActiveProfile.Rules.Move(at, i);
        }
    }

    /// <summary>Blocks in priority order: a folder's rules together, or a single rule outside folders.</summary>
    private List<List<RoutingRule>> Units()
    {
        var units = new List<List<RoutingRule>>(); string? current = null;
        foreach (var rule in ActiveProfile.Rules)
        {
            if (rule.FolderId is { } id && id == current) { units[^1].Add(rule); continue; }
            units.Add([rule]); current = rule.FolderId;
        }
        return units;
    }

    private void RefreshRules()
    {
        var ordered = PriorityOrder();
        var units = Units();
        for (var i = 0; i < ordered.Count; i++)
        {
            var rule = ordered[i];
            rule.Priority = i + 1;
            if (rule.FolderId is { } folder)
            {
                var siblings = ordered.Where(r => r.FolderId == folder && RuleGroup(r) == RuleGroup(rule)).ToList();
                var at = siblings.IndexOf(rule);
                rule.CanMoveUp = at > 0; rule.CanMoveDown = at >= 0 && at < siblings.Count - 1;
            }
            else
            {
                var at = units.FindIndex(u => u.Count == 1 && ReferenceEquals(u[0], rule) && u[0].FolderId is null);
                bool Movable(int index) => index >= 0 && index < units.Count && (units[index][0].FolderId is not null || RuleGroup(units[index][0]) == RuleGroup(rule));
                rule.CanMoveUp = Movable(at - 1); rule.CanMoveDown = Movable(at + 1);
            }
        }
        var search = RuleSearch.Trim();
        bool Match(RoutingRule r) => search.Length == 0 || r.Name.Contains(search, StringComparison.OrdinalIgnoreCase) || r.Description.Contains(search, StringComparison.OrdinalIgnoreCase);
        var items = new List<object>();
        if (RuleSortIndex == 0)
            foreach (var rule in ActiveProfile.Rules.OrderByDescending(r => r.CreatedAt).Where(Match)) { rule.Nested = false; items.Add(rule); }
        else
        {
            var folders = ActiveProfile.Folders.ToDictionary(f => f.Id);
            foreach (var id in folderRows.Keys.Where(id => !folders.ContainsKey(id)).ToArray()) folderRows.Remove(id);
            FolderRow Row(RuleFolder folder, List<RoutingRule> kids)
            {
                if (!folderRows.TryGetValue(folder.Id, out var row)) folderRows[folder.Id] = row = new(folder);
                var routes = kids.GroupBy(k => k.Target).Select(g => RouteChoice.NameOf(g.Key) + " " + g.Count());
                row.Detail = kids.Count == 0 ? T("пустая — нажмите «+», чтобы добавить правило", "empty — press + to add a rule")
                    : RulesWord(kids.Count) + " · " + string.Join(" · ", routes);
                var at = units.FindIndex(u => u[0].FolderId == folder.Id);
                row.CanMoveUp = at > 0; row.CanMoveDown = at >= 0 && at < units.Count - 1;
                return row;
            }
            // Empty folders first, so a new folder is visible straight away.
            foreach (var folder in ActiveProfile.Folders.Where(f => ActiveProfile.Rules.All(r => r.FolderId != f.Id)))
                if (search.Length == 0 || folder.Name.Contains(search, StringComparison.OrdinalIgnoreCase)) items.Add(Row(folder, []));
            var emitted = new HashSet<string>();
            foreach (var rule in ordered)
            {
                if (rule.FolderId is { } id && folders.TryGetValue(id, out var folder))
                {
                    if (!emitted.Add(id)) continue;
                    var kids = ordered.Where(r => r.FolderId == id).ToList();
                    var shown = kids.Where(Match).ToList();
                    if (search.Length > 0 && shown.Count == 0 && !folder.Name.Contains(search, StringComparison.OrdinalIgnoreCase)) continue;
                    items.Add(Row(folder, kids));
                    // Searching opens folders that contain matches.
                    if (!folder.Collapsed || search.Length > 0) foreach (var kid in search.Length > 0 && shown.Count > 0 ? shown : kids) { kid.Nested = true; items.Add(kid); }
                    continue;
                }
                rule.Nested = false;
                if (Match(rule)) items.Add(rule);
            }
        }
        // Move rows in place instead of rebuilding the list, so a reorder keeps the scroll position.
        for (var i = 0; i < items.Count; i++)
        {
            if (i < VisibleRules.Count && ReferenceEquals(VisibleRules[i], items[i])) continue;
            var existing = VisibleRules.IndexOf(items[i]);
            if (existing >= 0) VisibleRules.Move(existing, i); else VisibleRules.Insert(i, items[i]);
        }
        while (VisibleRules.Count > items.Count) VisibleRules.RemoveAt(VisibleRules.Count - 1);
        Notify(nameof(RuleCount)); Notify(nameof(NoRules));
    }
    private string RulesWord(int n) => I18n.English ? n + (n == 1 ? " rule" : " rules")
        : n + (n % 10 == 1 && n % 100 != 11 ? " правило" : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? " правила" : " правил");

    /// <summary>Moves a rule one step: inside its folder, or past the neighbouring rule or whole folder.</summary>
    internal void MoveRule(RoutingRule rule, int direction)
    {
        var step = Math.Sign(direction);
        if (rule.FolderId is { } folder)
        {
            var siblings = PriorityOrder().Where(r => r.FolderId == folder && RuleGroup(r) == RuleGroup(rule)).ToList();
            var at = siblings.IndexOf(rule); var to = at + step;
            if (at < 0 || to < 0 || to >= siblings.Count) return;
            ActiveProfile.Rules.Move(ActiveProfile.Rules.IndexOf(rule), ActiveProfile.Rules.IndexOf(siblings[to]));
        }
        else
        {
            var units = Units();
            var at = units.FindIndex(u => u.Count == 1 && ReferenceEquals(u[0], rule) && rule.FolderId is null);
            var to = at + step;
            if (at < 0 || to < 0 || to >= units.Count || units[to][0].FolderId is null && RuleGroup(units[to][0]) != RuleGroup(rule)) return;
            var target = step < 0 ? ActiveProfile.Rules.IndexOf(units[to][0]) : ActiveProfile.Rules.IndexOf(units[to][^1]);
            ActiveProfile.Rules.Move(ActiveProfile.Rules.IndexOf(rule), target);
        }
        RulesChanged();
    }
    /// <summary>Moves a whole folder past its neighbouring rule or folder.</summary>
    internal void MoveFolder(RuleFolder folder, int direction)
    {
        var units = Units();
        var at = units.FindIndex(u => u[0].FolderId == folder.Id); var to = at + Math.Sign(direction);
        if (at < 0 || to < 0 || to >= units.Count) return;
        var block = units[at]; var neighbour = units[to];
        var order = units.ToList(); order[at] = neighbour; order[to] = block;
        var flat = order.SelectMany(u => u).ToList();
        for (var i = 0; i < flat.Count; i++) { var current = ActiveProfile.Rules.IndexOf(flat[i]); if (current != i) ActiveProfile.Rules.Move(current, i); }
        RulesChanged();
    }
    internal bool CanDrop(RoutingRule source, RoutingRule target) => PriorityView && !ReferenceEquals(source, target) && RuleGroup(source) == RuleGroup(target);
    /// <summary>Places <paramref name="source"/> right before or after <paramref name="target"/>, joining the target's folder.</summary>
    public void Reorder(RoutingRule source, RoutingRule target, bool after = false)
    {
        if (!CanDrop(source, target) || !ActiveProfile.Rules.Contains(source) || !ActiveProfile.Rules.Contains(target)) return;
        source.FolderId = target.FolderId;
        var from = ActiveProfile.Rules.IndexOf(source);
        var to = ActiveProfile.Rules.IndexOf(target) + (after ? 1 : 0);
        if (to > from) to--;
        if (to != from) ActiveProfile.Rules.Move(from, to);
        CompactFolders(); RulesChanged();
    }
    /// <summary>Puts a rule into a folder (at its end) or, with null, takes it out to just below the folder.</summary>
    internal void MoveToFolder(RoutingRule rule, RuleFolder? folder)
    {
        if (!ActiveProfile.Rules.Contains(rule) || rule.FolderId == folder?.Id) return;
        var previous = FolderOf(rule);
        rule.FolderId = folder?.Id;
        var anchor = folder is not null
            ? ActiveProfile.Rules.LastOrDefault(r => r.FolderId == folder.Id && !ReferenceEquals(r, rule))
            : previous is not null ? ActiveProfile.Rules.LastOrDefault(r => r.FolderId == previous.Id) : null;
        if (anchor is not null)
        {
            var from = ActiveProfile.Rules.IndexOf(rule); var to = ActiveProfile.Rules.IndexOf(anchor);
            if (from > to) to++;
            if (from != to) ActiveProfile.Rules.Move(from, to);
        }
        if (folder is not null) folder.Collapsed = false;
        CompactFolders(); RulesChanged();
    }
    internal RuleFolder CreateFolder(string name, string color, string icon, IEnumerable<RoutingRule>? rules = null)
    {
        var folder = new RuleFolder { Name = RuleFolders.NormalizeName(name), Color = color, Icon = icon };
        ActiveProfile.Folders.Add(folder);
        foreach (var rule in rules ?? []) if (ActiveProfile.Rules.Contains(rule)) rule.FolderId = folder.Id;
        CompactFolders(); RulesChanged();
        return folder;
    }

    // ---- Commands for folder rows and rules ----
    public ICommand NewFolder => newFolder ??= new Command(_ =>
    {
        if (Dialogs.FolderEditor(null, ActiveProfile.Rules.ToList()) is not { } result) return;
        if (result.Imported is { } imported) { ImportFolder(imported.Folder, imported.Rules); return; }
        CreateFolder(result.Name, result.Color, result.Icon, result.Rules);
    });
    private ICommand? newFolder;
    internal void ImportFolder(RuleFolder folder, List<RoutingRule> rules)
    {
        while (ActiveProfile.Folders.Any(f => f.Name.Equals(folder.Name, StringComparison.OrdinalIgnoreCase))) folder.Name += " (2)";
        ActiveProfile.Folders.Add(folder);
        foreach (var rule in rules) ActiveProfile.Rules.Add(rule);
        CompactFolders(); RulesChanged();
        Banner = T($"Папка «{folder.Name}» добавлена: {RulesWord(rules.Count)}. Они в конце списка — поднимите папку стрелками, если нужен более высокий приоритет.",
            $"Folder ‘{folder.Name}’ added: {RulesWord(rules.Count)}. It is at the end — raise it with the arrows for higher priority.");
    }
    public ICommand ToggleFolder => toggleFolder ??= new Command(p => { if (p is FolderRow row) { row.Folder.Collapsed = !row.Folder.Collapsed; Save(); RefreshRules(); } });
    private ICommand? toggleFolder;
    public ICommand AddRuleToFolder => addRuleToFolder ??= new Command(p =>
    {
        if (p is not FolderRow row) return;
        var rule = Dialogs.Rule(null, Data.Servers, draft => Dialogs.RouteCheck(this, draft), NetBirdEnabled, ActiveProfile.Folders, row.Folder.Id);
        if (rule is null) return;
        InsertRule(rule);
    });
    private ICommand? addRuleToFolder;
    /// <summary>Adds a new rule: inside its folder at the folder's end, otherwise at the end of the list.</summary>
    internal void InsertRule(RoutingRule rule)
    {
        var anchor = rule.FolderId is { } id ? ActiveProfile.Rules.LastOrDefault(r => r.FolderId == id) : null;
        if (anchor is not null) ActiveProfile.Rules.Insert(ActiveProfile.Rules.IndexOf(anchor) + 1, rule); else ActiveProfile.Rules.Add(rule);
        if (FolderOf(rule) is { } folder) folder.Collapsed = false;
        CompactFolders(); RulesChanged();
    }
    public ICommand ShareFolder => shareFolder ??= new Command(p => { if (p is FolderRow row) Dialogs.FolderShare(row.Folder, RuleFolders.RulesIn(ActiveProfile, row.Folder).ToList()); });
    private ICommand? shareFolder;
    public ICommand EditFolder => editFolder ??= new Command(p =>
    {
        if (p is not FolderRow row || Dialogs.FolderEditor(row.Folder, ActiveProfile.Rules.ToList()) is not { } result) return;
        row.Folder.Name = RuleFolders.NormalizeName(result.Name); row.Folder.Color = result.Color; row.Folder.Icon = result.Icon; row.Restyle();
        var chosen = result.Rules.ToHashSet();
        foreach (var rule in ActiveProfile.Rules)
            if (chosen.Contains(rule)) rule.FolderId = row.Folder.Id;
            else if (rule.FolderId == row.Folder.Id) rule.FolderId = null;
        CompactFolders(); RulesChanged();
    });
    private ICommand? editFolder;
    public ICommand DeleteFolder => deleteFolder ??= new Command(p =>
    {
        if (p is not FolderRow row) return;
        var rules = RuleFolders.RulesIn(ActiveProfile, row.Folder).ToList();
        var choice = rules.Count == 0 ? (Dialogs.Confirm(T("Удалить папку?", "Delete folder?"), row.Folder.Name) ? Dialogs.FolderDeletion.KeepRules : Dialogs.FolderDeletion.Cancel)
            : Dialogs.DeleteFolderChoice(row.Folder.Name, rules.Count);
        if (choice != Dialogs.FolderDeletion.Cancel) RemoveFolder(row.Folder, choice == Dialogs.FolderDeletion.DeleteRules);
    });
    /// <summary>Removes a folder; its rules either stay in place outside folders or are deleted too.</summary>
    internal void RemoveFolder(RuleFolder folder, bool deleteRules)
    {
        foreach (var rule in RuleFolders.RulesIn(ActiveProfile, folder).ToList())
            if (deleteRules) { rule.PropertyChanged -= OnRuleChanged; ActiveProfile.Rules.Remove(rule); }
            else rule.FolderId = null;
        ActiveProfile.Folders.Remove(folder);
        CompactFolders(); RulesChanged();
    }
    private ICommand? deleteFolder;
    public ICommand MoveFolderUp => moveFolderUp ??= new Command(p => { if (p is FolderRow row) MoveFolder(row.Folder, -1); });
    private ICommand? moveFolderUp;
    public ICommand MoveFolderDown => moveFolderDown ??= new Command(p => { if (p is FolderRow row) MoveFolder(row.Folder, 1); });
    private ICommand? moveFolderDown;
    internal IReadOnlyList<RuleFolder> Folders => ActiveProfile.Folders;
    private void AttachProfile() { foreach (var r in ActiveProfile.Rules) { r.PropertyChanged -= OnRuleChanged; r.PropertyChanged += OnRuleChanged; } }
    private void DetachProfile(Profile p) { foreach (var r in p.Rules) r.PropertyChanged -= OnRuleChanged; }
    private void OnRuleChanged(object? sender, PropertyChangedEventArgs e) { if (e.PropertyName is nameof(RoutingRule.Target) or nameof(RoutingRule.Enabled)) { Notify(nameof(ProtectedRuleCount)); Save(); QueueApply(); } }
    internal void RulesChanged() { AttachProfile(); RefreshRules(); Notify(nameof(NoRules)); Notify(nameof(ProtectedRuleCount)); Save(); QueueApply(); }
}
