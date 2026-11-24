using System.Windows.Media;

namespace Bebekon.App;

public static partial class Dialogs
{
    /// <summary>
    /// zapret settings: import a folder once (UAC), choose a profile with a plain description, or let
    /// Bebekon try every profile and keep the fastest one that opens the test sites.
    /// </summary>
    internal static void ZapretSettings(MainViewModel vm)
    {
        var w = Shell("Zapret · " + I18n.T("обход блокировок без VPN", "bypass without VPN"), out var body); w.Width = 640;
        body.Children.Add(MutedText(I18n.T(
            "zapret (winws + WinDivert) подменяет начало соединений на уровне сетевой карты, поэтому YouTube и Discord открываются напрямую, без VPN. Bebekon запускает его через свою службу, сам исключает адреса ваших VPN-серверов и выключает собственное «Обход замедления», чтобы они не мешали друг другу. Сайты, которые должны работать через zapret, оставьте в правилах «Direct».",
            "zapret (winws + WinDivert) rewrites the start of connections at the network adapter, so YouTube and Discord open directly without VPN. Bebekon runs it through its helper, excludes your VPN servers itself and turns off its own splitting so they don't interfere. Keep the sites zapret should handle on Direct in your rules.")));
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new(0, 0, 0, 8) };
        body.Children.Add(status);

        // Source folder.
        var import = new Button { Content = IconText("Folder", I18n.T("Импортировать папку zapret…", "Import zapret folder…")), MinHeight = 40 };
        body.Children.Add(import);
        body.Children.Add(MutedText(I18n.T("Нужна папка с general*.bat, bin\\winws.exe и lists — например, flowseal/zapret-discord-youtube. Windows один раз спросит права администратора: Bebekon копирует zapret в защищённую папку службы и запускает только эту копию.",
            "A folder with general*.bat, bin\\winws.exe and lists — e.g. flowseal/zapret-discord-youtube. Windows asks for administrator rights once: Bebekon copies zapret into the helper's protected folder and runs only that copy.")));

        // Profile.
        body.Children.Add(new TextBlock { Text = I18n.T("Профиль", "Profile"), FontWeight = FontWeights.SemiBold, Margin = new(0, 6, 0, 6) });
        var profiles = new ComboBox { MinHeight = 40 };
        var summary = MutedText(""); summary.Margin = new(0, 6, 0, 0);
        body.Children.Add(profiles); body.Children.Add(summary);

        // Automatic search.
        var card = new Border { CornerRadius = new(12), Padding = new(14), Margin = new(0, 12, 0, 4), BorderThickness = new(1) };
        card.SetResourceReference(Border.BackgroundProperty, "CardHover"); card.SetResourceReference(Border.BorderBrushProperty, "Border");
        var search = new StackPanel(); card.Child = search; body.Children.Add(card);
        search.Children.Add(new TextBlock { Text = I18n.T("Подобрать рабочий профиль автоматически", "Find a working profile automatically"), FontWeight = FontWeights.SemiBold });
        search.Children.Add(MutedText(I18n.T("Bebekon по очереди включит каждый профиль и откроет сайты напрямую. Выберет самый быстрый из тех, что открыли все сайты. Обычно 2–4 минуты. Голос Discord так не проверить — только сайты.",
            "Bebekon turns on each profile in turn and opens the sites directly, then keeps the fastest that opened them all. Usually 2–4 minutes. Discord voice can't be checked this way — sites only.")));
        var sitesRow = new Grid(); sitesRow.ColumnDefinitions.Add(new()); sitesRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var sites = new TextBox { Text = vm.Settings.ZapretSites, MinHeight = 40, VerticalContentAlignment = VerticalAlignment.Center };
        var run = new Button { Content = IconText("Bolt", I18n.T("Подобрать", "Find")), Style = (Style)Application.Current.Resources["AccentButton"], MinHeight = 40, Margin = new(8, 0, 0, 0), Padding = new(14, 0, 14, 0) };
        Grid.SetColumn(run, 1); sitesRow.Children.Add(sites); sitesRow.Children.Add(run); search.Children.Add(sitesRow);
        var results = new StackPanel { Margin = new(0, 10, 0, 0) };
        search.Children.Add(new ScrollViewer { Content = results, MaxHeight = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var verdict = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new(0, 8, 0, 0) }; search.Children.Add(verdict);

        // Lists.
        var lists = new WrapPanel { Margin = new(0, 10, 0, 0) }; body.Children.Add(lists);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 6, 0, 0) }; error.SetResourceReference(TextBlock.ForegroundProperty, "Danger"); body.Children.Add(error);

        void Show()
        {
            var state = vm.ZapretState;
            status.Text = vm.ZapretSummary;
            status.SetResourceReference(TextBlock.ForegroundProperty, state is { Running: true } ? "Success" : state?.Error is not null ? "Warning" : "Text");
            var names = state?.Profiles ?? []; var notes = state?.Summaries ?? [];
            profiles.Items.Clear();
            for (var i = 0; i < names.Length; i++) profiles.Items.Add(new ComboBoxItem { Content = names[i], Tag = i < notes.Length ? notes[i] : "" });
            profiles.SelectedItem = profiles.Items.OfType<ComboBoxItem>().FirstOrDefault(i => (string)i.Content == vm.Settings.ZapretProfile) ?? profiles.Items.OfType<ComboBoxItem>().FirstOrDefault();
            profiles.IsEnabled = run.IsEnabled = names.Length > 0;
            lists.IsEnabled = state?.Installed == true;
            import.Content = IconText("Folder", state?.Installed == true ? I18n.T("Обновить из папки zapret…", "Update from zapret folder…") : I18n.T("Импортировать папку zapret…", "Import zapret folder…"));
        }
        profiles.SelectionChanged += async (_, _) =>
        {
            if (profiles.SelectedItem is not ComboBoxItem item) return;
            summary.Text = I18n.T("Как работает: ", "How it works: ") + item.Tag;
            var name = (string)item.Content;
            if (name == vm.Settings.ZapretProfile) return;
            vm.Settings.ZapretProfile = name;
            try { error.Text = ""; if (vm.ZapretEnabled) await vm.ApplyZapretAsync(); Show(); }
            catch (UserError e) { error.Text = e.Message; }
        };
        import.Click += async (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = I18n.T("Папка zapret", "zapret folder") };
            if (Path.IsPathFullyQualified(vm.Settings.ObhodFolder ?? "") && Directory.Exists(vm.Settings.ObhodFolder)) picker.InitialDirectory = vm.Settings.ObhodFolder;
            if (picker.ShowDialog(w) != true) return;
            try { error.Text = ""; import.IsEnabled = false; await vm.ImportZapretAsync(picker.FolderName); Show(); }
            catch (UserError e) { error.Text = e.Message; }
            finally { import.IsEnabled = true; }
        };
        CancellationTokenSource? searching = null;
        w.Closed += (_, _) => searching?.Cancel();
        run.Click += async (_, _) =>
        {
            if (searching is not null) { searching.Cancel(); return; }
            vm.Settings.ZapretSites = sites.Text.Trim();
            searching = new(); results.Children.Clear(); verdict.Text = ""; error.Text = "";
            run.Content = IconText("Close", I18n.T("Остановить", "Stop"));
            var rows = new Dictionary<string, TextBlock>();
            try
            {
                var trials = await vm.FindZapretProfileAsync((profile, trial) => Application.Current.Dispatcher.Invoke(() =>
                {
                    if (!rows.TryGetValue(profile, out var line)) { line = new TextBlock { FontSize = 12, Margin = new(0, 2, 0, 2), TextTrimming = TextTrimming.CharacterEllipsis }; rows[profile] = line; results.Children.Add(line); line.BringIntoView(); }
                    line.Text = profile + " · " + (trial is null ? I18n.T("проверка…", "checking…") : trial.Works ? trial.MedianMs + I18n.T(" мс", " ms") : I18n.T("не открыл ", "failed ") + trial.Failed);
                    line.SetResourceReference(TextBlock.ForegroundProperty, trial is null ? "Muted" : trial.Works ? "Success" : "Danger");
                }), searching.Token);
                var best = trials.Where(t => t.Works).OrderBy(t => t.MedianMs).FirstOrDefault();
                verdict.Text = best is not null
                    ? I18n.T($"Лучший: {best.Profile} · {best.MedianMs} мс. Выбран{(vm.ZapretEnabled ? " и включён" : "; включите переключатель zapret")}.", $"Best: {best.Profile} · {best.MedianMs} ms. Selected{(vm.ZapretEnabled ? " and running" : "; turn the zapret switch on")}.")
                    : I18n.T("Ни один профиль не открыл все сайты. Обновите zapret (новые версии flowseal часто добавляют рабочие альты) или используйте для этих сайтов VPN.",
                             "No profile opened every site. Update zapret (new flowseal releases often add working profiles) or use VPN for these sites.");
            }
            catch (OperationCanceledException) { verdict.Text = I18n.T("Подбор остановлен. Сохранён лучший из проверенных профилей.", "Search stopped. The best profile tried so far is kept."); }
            catch (UserError e) { error.Text = e.Message; }
            finally { searching.Dispose(); searching = null; run.Content = IconText("Bolt", I18n.T("Подобрать", "Find")); Show(); }
        };
        foreach (var (file, label) in new[] {
            ("list-general-user.txt", I18n.T("Мои сайты", "My sites")), ("list-exclude-user.txt", I18n.T("Исключения сайтов", "Excluded sites")),
            ("ipset-all.txt", I18n.T("Сети профиля", "Profile networks")), ("ipset-exclude-user.txt", I18n.T("Исключения IP", "Excluded IPs")) })
        {
            var b = new Button { Content = IconText("List", label), Margin = new(0, 0, 6, 6), MinHeight = 36 };
            b.Click += async (_, _) =>
            {
                try
                {
                    error.Text = "";
                    var current = await vm.ReadZapretListAsync(file);
                    var description = file == "ipset-all.txt"
                        ? I18n.T("Пустой ipset-all.txt означает «все IP». Адреса ваших VPN-серверов Bebekon исключает сам.", "An empty ipset-all.txt means all IPs. Bebekon excludes your VPN servers itself.")
                        : I18n.T("По одной записи в строке. Изменения применятся сразу.", "One entry per line. Changes apply right away.");
                    var edited = Lines(label, description, current, entries => Zapret.ValidateList(file, entries), 4 * 1024 * 1024,
                        file.StartsWith("list-", StringComparison.Ordinal) ? () => PickFromRules(keywords: false)?.Where(e => DomainSets.KindOf(e) == RuleKind.Site).ToList() : null);
                    if (edited is not null) await vm.SaveZapretListAsync(file, edited);
                }
                catch (UserError e) { error.Text = e.Message; }
            };
            lists.Children.Add(b);
        }
        Show();
        var done = new Button { Content = I18n.T("Готово", "Done"), Style = (Style)Application.Current.Resources["AccentButton"], MinHeight = 44, Margin = new(0, 16, 0, 0), IsDefault = true, HorizontalAlignment = HorizontalAlignment.Right, MinWidth = 160 };
        done.Click += (_, _) => w.Close(); body.Children.Add(done);
        w.ShowDialog();
    }
}
