using System.Text;
using System.Text.RegularExpressions;

namespace Bebekon.App;

public static partial class Dialogs
{
    /// <summary>
    /// Settings for splitting TLS handshakes of direct connections: where it applies, how, the lists,
    /// and a check that finds what the user's provider actually needs for a site.
    /// </summary>
    internal static DirectTrafficPolicy? DirectPolicyEditor(DirectTrafficPolicy? current, string folder, Action<string>? routeViaVpn = null)
    {
        current ??= new();
        var window = Shell(I18n.T("Обход замедления сайтов", "Bypass site throttling"), out var body);
        window.Width = 600;
        body.Children.Add(MutedText(I18n.T(
            "Дробление начала TLS-соединения помогает, когда провайдер замедляет сайт по имени. Оно касается только прямых соединений, VPN не меняется. Каждое раздробленное соединение открывается чуть дольше, поэтому включайте его только там, где оно нужно.",
            "Splitting the start of a TLS connection helps when a provider slows a site down by name. It only affects direct connections; VPN is unchanged. Each split connection opens a little slower, so apply it only where needed.")));

        // Where.
        body.Children.Add(new TextBlock { Text = I18n.T("Где применять", "Where"), FontWeight = FontWeights.SemiBold, Margin = new(0, 4, 0, 6) });
        var scope = new ComboBox { MinHeight = 40 };
        foreach (var (value, label) in new[] {
            (FragmentScope.Auto, I18n.T("Авто · YouTube, Discord, X, Instagram, Facebook, TikTok и ваш список", "Auto · YouTube, Discord, X, Instagram, Facebook, TikTok and your list")),
            (FragmentScope.ListOnly, I18n.T("Только ваш список ниже", "Only your list below")),
            (FragmentScope.AllDirect, I18n.T("Все прямые сайты · может ломать часть сайтов", "All direct sites · may break some sites")) })
            scope.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        scope.SelectedItem = scope.Items.OfType<ComboBoxItem>().First(i => (FragmentScope)i.Tag == current.Scope);
        body.Children.Add(scope);
        body.Children.Add(MutedText(I18n.T("Ваши правила с маршрутом Direct дробятся всегда (кроме исключений). Для сервисов «Авто» QUIC отключается, чтобы браузер шёл по TCP — иначе дробление не сработает.",
            "Your own Direct rules are always split (except exclusions). For Auto services QUIC is refused so the browser uses TCP — otherwise splitting has no effect.")));

        // How.
        body.Children.Add(new TextBlock { Text = I18n.T("Способ", "Method"), FontWeight = FontWeights.SemiBold, Margin = new(0, 4, 0, 6) });
        var method = new ComboBox { MinHeight = 40 };
        foreach (var (value, label) in new[] {
            (FragmentMethod.Records, I18n.T("Записи TLS · быстро, +≈50 мс", "TLS records · fast, +≈50 ms")),
            (FragmentMethod.Split, I18n.T("Сегменты TCP · +≈0,5 с на соединение", "TCP segments · +≈0.5 s per connection")),
            (FragmentMethod.Both, I18n.T("Оба · надёжнее, +≈0,5 с", "Both · most robust, +≈0.5 s")) })
            method.Items.Add(new ComboBoxItem { Content = label, Tag = value });
        method.SelectedItem = method.Items.OfType<ComboBoxItem>().First(i => (FragmentMethod)i.Tag == current.Method);
        body.Children.Add(method);

        // Check a site.
        var checkCard = new Border { CornerRadius = new(12), Padding = new(14), Margin = new(0, 14, 0, 4), BorderThickness = new(1) };
        checkCard.SetResourceReference(Border.BackgroundProperty, "CardHover"); checkCard.SetResourceReference(Border.BorderBrushProperty, "Border");
        var check = new StackPanel(); checkCard.Child = check;
        check.Children.Add(new TextBlock { Text = I18n.T("Проверить сайт на вашем провайдере", "Check a site on your provider"), FontWeight = FontWeights.SemiBold });
        check.Children.Add(MutedText(I18n.T("Bebekon откроет сайт напрямую, в обход VPN, без дробления и каждым способом, и подскажет, что работает. Около 30–60 секунд.",
            "Bebekon opens the site directly, bypassing VPN, without splitting and with each method, then tells you what works. About 30–60 seconds.")));
        var checkRow = new Grid(); checkRow.ColumnDefinitions.Add(new()); checkRow.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        var site = new TextBox { Text = "youtube.com", MinHeight = 40, VerticalContentAlignment = VerticalAlignment.Center, Tag = "youtube.com" };
        var run = new Button { Content = IconText("Bolt", I18n.T("Проверить", "Check")), Style = (Style)Application.Current.Resources["AccentButton"], MinHeight = 40, Margin = new(8, 0, 0, 0), Padding = new(14, 0, 14, 0) };
        Grid.SetColumn(run, 1); checkRow.Children.Add(site); checkRow.Children.Add(run); check.Children.Add(checkRow);
        var table = new StackPanel { Margin = new(0, 10, 0, 0) }; check.Children.Add(table);
        var verdict = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeights.SemiBold, Margin = new(0, 8, 0, 0) }; check.Children.Add(verdict);
        var verdictActions = new WrapPanel { Margin = new(0, 8, 0, 0) }; check.Children.Add(verdictActions);
        body.Children.Add(checkCard);
        CancellationTokenSource? checking = null;
        window.Closed += (_, _) => checking?.Cancel();
        string Label(FragmentMethod? m) => m switch
        {
            null => I18n.T("Без дробления", "No splitting"), FragmentMethod.Records => I18n.T("Записи TLS", "TLS records"),
            FragmentMethod.Split => I18n.T("Сегменты TCP", "TCP segments"), _ => I18n.T("Оба способа", "Both")
        };
        run.Click += async (_, _) =>
        {
            if (checking is not null) { checking.Cancel(); return; }
            checking = new(); var token = checking.Token;
            table.Children.Clear(); verdict.Text = ""; verdictActions.Children.Clear();
            run.Content = IconText("Close", I18n.T("Остановить", "Stop"));
            try
            {
                var uplink = System.Net.IPAddress.TryParse(NetworkAddresses.Read().Address, out var address) ? address : null;
                var host = RuleValidation.SiteHost(site.Text.Trim(), dropWww: false);
                var rows = new Dictionary<FragmentMethod?, TextBlock>();
                foreach (var m in ThrottleProbe.Variants)
                {
                    var line = new TextBlock { Text = Label(m) + " · " + I18n.T("ожидает", "waiting"), FontSize = 12, Margin = new(0, 2, 0, 2) };
                    line.SetResourceReference(TextBlock.ForegroundProperty, "Muted"); rows[m] = line; table.Children.Add(line);
                }
                var results = await Task.Run(() => ThrottleProbe.TestAsync(Path.Combine(AppContext.BaseDirectory, "core", "sing-box.exe"), host, uplink, 3, token,
                    m => Application.Current.Dispatcher.Invoke(() => rows[m].Text = Label(m) + " · " + I18n.T("проверка…", "checking…"))), token);
                foreach (var r in results)
                {
                    rows[r.Method].Text = Label(r.Method) + " · " + (r.Succeeded == 0 ? I18n.T("не открылся", "did not open") : $"{r.Succeeded}/{r.Attempts}, " + r.MedianMs + I18n.T(" мс", " ms"));
                    rows[r.Method].SetResourceReference(TextBlock.ForegroundProperty, r.Works ? "Success" : r.Succeeded == 0 ? "Danger" : "Warning");
                }
                var plain = results.First(r => r.Method is null);
                var best = ThrottleProbe.Recommend(results);
                if (plain.Works)
                {
                    verdict.Text = I18n.T($"{host} открывается напрямую без дробления — для него обход не нужен.", $"{host} opens directly without splitting — no bypass needed.");
                }
                else if (best is { } chosen)
                {
                    verdict.Text = I18n.T($"Работает «{Label(chosen)}». Выберите его и добавьте сайт в список, если он не из «Авто».", $"‘{Label(chosen)}’ works. Use it and add the site to your list unless Auto covers it.");
                    var use = new Button { Content = IconText("Check", I18n.T("Использовать этот способ", "Use this method")), MinHeight = 36, Margin = new(0, 0, 8, 0), Padding = new(12, 0, 12, 0) };
                    use.Click += (_, _) => method.SelectedItem = method.Items.OfType<ComboBoxItem>().First(i => (FragmentMethod)i.Tag == chosen);
                    verdictActions.Children.Add(use);
                }
                else
                {
                    verdict.Text = I18n.T($"{host} у вашего провайдера заблокирован полностью: ни один способ дробления не помог. Откройте его через VPN.",
                        $"{host} is fully blocked by your provider: no splitting method helped. Use VPN for it.");
                    if (routeViaVpn is not null)
                    {
                        var vpn = new Button { Content = IconText("Shield", I18n.T("Добавить правило «через VPN»", "Add a VPN rule")), Style = (Style)Application.Current.Resources["AccentButton"], MinHeight = 36, Padding = new(12, 0, 12, 0) };
                        vpn.Click += (_, _) => { routeViaVpn(host); verdict.Text = I18n.T($"Правило «{host} → VPN» добавлено.", $"Rule ‘{host} → VPN’ added."); vpn.IsEnabled = false; };
                        verdictActions.Children.Add(vpn);
                    }
                }
            }
            catch (OperationCanceledException) { verdict.Text = I18n.T("Проверка остановлена.", "Check stopped."); }
            catch (UserError e) { verdict.Text = e.Message; }
            finally { checking.Dispose(); checking = null; run.Content = IconText("Bolt", I18n.T("Проверить", "Check")); }
        };

        // Lists.
        var lists = new Expander { Header = I18n.T("Ваш список, исключения и QUIC", "Your list, exclusions and QUIC"), Margin = new(0, 12, 0, 0), IsExpanded = current.Include.Length + current.Exclude.Length > 0, Style = (Style)Application.Current.Resources["DetailsPanel"] };
        var listBody = new StackPanel(); lists.Content = listBody; body.Children.Add(lists);
        TextBox ListField(string label, string[] entries)
        {
            var field = Field(listBody, label, string.Join(Environment.NewLine, entries));
            field.AcceptsReturn = true; field.MinHeight = 70; field.MaxHeight = 130; field.MaxLength = 512 * 1024;
            field.VerticalContentAlignment = VerticalAlignment.Top; field.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            return field;
        }
        var include = ListField(I18n.T("Ваш список: домены и IP/CIDR · ^имя — только точное имя", "Your list: domains and IP/CIDR · ^name — exact name only"), current.Include ?? []);
        var exclude = ListField(I18n.T("Исключения · никогда не дробить, имеют приоритет", "Exclusions · never split, take priority"), current.Exclude ?? []);
        var preferTcp = new CheckBox { Content = I18n.T("Отключать QUIC и для вашего списка (браузер перейдёт на TCP)", "Also refuse QUIC for your list (browsers switch to TCP)"), IsChecked = current.PreferTcp, Margin = new(0, 10, 0, 3) };
        listBody.Children.Add(preferTcp);
        var import = new Button { Content = IconText("Folder", I18n.T("Добавить списки из Obhod…", "Import Obhod lists…")), Margin = new(0, 10, 0, 0) };
        listBody.Children.Add(import);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0, 5, 0, 0) }; error.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
        body.Children.Add(error);
        import.Click += (_, _) =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = I18n.T("Папка Obhod или lists", "Obhod or lists folder") };
            if (Path.IsPathFullyQualified(folder ?? "") && Directory.Exists(folder)) picker.InitialDirectory = folder;
            if (picker.ShowDialog(window) != true) return;
            try
            {
                var root = picker.FolderName;
                if (Directory.Exists(Path.Combine(root, "lists"))) root = Path.Combine(root, "lists");
                if (!File.Exists(Path.Combine(root, "list-general.txt")) && !File.Exists(Path.Combine(root, "list-general-user.txt")))
                    throw new UserError(I18n.T("В этой папке нет списков Obhod.", "No Obhod site lists in this folder."));
                string Load(string existing, params string[] names)
                {
                    var lines = existing.Split('\n').AsEnumerable();
                    foreach (var name in names)
                    {
                        var file = new FileInfo(Path.Combine(root, name));
                        if (!file.Exists) continue;
                        if (file.Length > 4 * 1024 * 1024) throw new UserError(I18n.T("Слишком большой список.", "List is too large."));
                        if (name == "ipset-all.txt" && file.Length == 0)
                            lines = lines.Concat(new[] { "0.0.0.0/0", "::/0" });
                        else lines = lines.Concat(File.ReadAllLines(file.FullName));
                    }
                    return string.Join(Environment.NewLine, DirectTrafficPolicy.ParseList(lines).Entries);
                }
                var included = Load(include.Text, "list-general.txt", "list-general-user.txt", "list-google.txt", "ipset-all.txt");
                var excluded = Load(exclude.Text, "list-exclude.txt", "list-exclude-user.txt", "ipset-exclude.txt", "ipset-exclude-user.txt");
                include.Text = included; exclude.Text = excluded;
                error.Text = I18n.T("Списки добавлены. Проверьте их и нажмите «Сохранить».", "Lists imported. Review and save.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or UserError)
            { error.Text = e is UserError ? e.Message : I18n.T("Не удалось прочитать списки.", "Could not read the lists."); }
        };
        DirectTrafficPolicy? result = null;
        Submit(body, I18n.T("Сохранить", "Save"), () =>
        {
            try
            {
                result = new()
                {
                    Include = DirectTrafficPolicy.ParseList(include.Text.Split('\n')).Entries,
                    Exclude = DirectTrafficPolicy.ParseList(exclude.Text.Split('\n')).Entries, PreferTcp = preferTcp.IsChecked == true,
                    Scope = (FragmentScope)((ComboBoxItem)scope.SelectedItem).Tag, Method = (FragmentMethod)((ComboBoxItem)method.SelectedItem).Tag
                };
                window.DialogResult = true;
            }
            catch (UserError e) { error.Text = e.Message; }
        });
        return window.ShowDialog() == true ? result : null;
    }

    internal static void ManageObhod(Settings settings, Action save)
    {
        var window = Shell("Obhod · WinDivert", out var body);
        var hint = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = I18n.T(
            "Выберите свою папку Obhod с general.bat и bin\\winws.exe. Профиль использует WinDivert для системного трафика, включая TCP и UDP. Запуск выполняется отдельно от подключения VPN.",
            "Choose your Obhod folder containing general.bat and bin\\winws.exe. Profiles use WinDivert for system TCP and UDP traffic. Start a profile independently of the VPN.") };
        body.Children.Add(hint);
        var folderLabel = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0,12,0,4) };
        var folder = settings.ObhodFolder;
        body.Children.Add(folderLabel);
        var folderButton = new Button { Content = I18n.T("Выбрать папку…", "Choose folder…") };
        body.Children.Add(folderButton);
        var profiles = new ComboBox { Margin = new(0,10,0,6) };
        body.Children.Add(profiles);
        var actions = new WrapPanel { IsEnabled = false }; body.Children.Add(actions);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0,6,0,0) };
        error.SetResourceReference(TextBlock.ForegroundProperty, "Warning"); body.Children.Add(error);
        void Act(Action action)
        {
            try { error.Text = ""; action(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or UserError or System.ComponentModel.Win32Exception or ArgumentException)
            { error.Text = e is UserError ? e.Message : I18n.T("Не удалось открыть или изменить профиль. Проверьте папку и доступ к файлам.", "Could not open or change the profile. Check the folder and permissions."); }
        }
        void Button(string label, Action click)
        {
            var b = new Button { Content = label, Margin = new(0,4,6,0) }; b.Click += (_,_) => Act(click); actions.Children.Add(b);
        }
        void Refresh()
        {
            var names = ObhodProfiles(folder);
            folderLabel.Text = string.IsNullOrEmpty(folder) ? I18n.T("Папка не выбрана", "No folder selected") : folder;
            profiles.ItemsSource = names;
            profiles.SelectedItem = names.Contains(settings.ObhodProfile) ? settings.ObhodProfile : names.FirstOrDefault();
            actions.IsEnabled = names.Length > 0;
        }
        folderButton.Click += (_,_) => Act(() =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = I18n.T("Папка Obhod", "Obhod folder") };
            if (picker.ShowDialog(window) != true) return;
            if (ObhodProfiles(picker.FolderName).Length == 0) throw new UserError(I18n.T("Нужны general*.bat, bin\\winws.exe и папка lists.", "Expected general*.bat, bin\\winws.exe and a lists folder."));
            settings.ObhodFolder = folder = picker.FolderName; Refresh(); save();
        });
        profiles.SelectionChanged += (_,_) => { if (profiles.SelectedItem is string name && settings.ObhodProfile != name) { settings.ObhodProfile = name; save(); } };
        void Launch(string file)
        {
            if (!File.Exists(file)) throw new UserError(I18n.T("Файл отсутствует в выбранной папке.", "File missing from selected folder."));
            if (Confirm(I18n.T("Запустить внешний файл?", "Run external file?"), file))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file) { UseShellExecute = true, WorkingDirectory = folder });
        }
        Button(I18n.T("Запустить профиль", "Start profile"), () =>
        {
            if (profiles.SelectedItem is string name && ObhodProfiles(folder).Contains(name)) Launch(Path.Combine(folder,name));
        });
        Button(I18n.T("Управление службой", "Service controls"), () => Launch(Path.Combine(folder,"service.bat")));
        foreach (var (file, label) in new[] {
            ("list-general-user.txt", I18n.T("Мои сайты","My sites")), ("list-exclude-user.txt", I18n.T("Исключения сайтов","Excluded sites")),
            ("ipset-all.txt", I18n.T("Сети профиля","Profile networks")), ("ipset-exclude-user.txt", I18n.T("Исключения IP","Excluded IPs")) })
            Button(label, () => EditObhodList(Path.Combine(folder, "lists", file), label));
        Button(I18n.T("Отвязать папку", "Forget folder"), () =>
        {
            settings.ObhodFolder = folder = ""; settings.ObhodProfile = ""; Refresh(); save();
        });
        Act(Refresh);
        var close = new Button { Content = I18n.T("Готово","Done"), HorizontalAlignment = HorizontalAlignment.Right, Margin = new(0,16,0,0) };
        close.Click += (_,_) => window.Close(); body.Children.Add(close);
        window.ShowDialog();
    }

    internal static string[] ObhodProfiles(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder) || folder.StartsWith(@"\\") ||
            !File.Exists(Path.Combine(folder,"bin","winws.exe")) || !Directory.Exists(Path.Combine(folder,"lists"))) return [];
        return Directory.GetFiles(folder,"general*.bat").Select(Path.GetFileName).OfType<string>()
            .Where(name => Regex.IsMatch(name, @"^general(?: \([\w .-]+\))?\.bat$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant))
            .OrderBy(name => name.Length).ThenBy(name => name, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void EditObhodList(string file, string label)
    {
        var info = new FileInfo(file);
        if (info.Exists && info.Length > 4*1024*1024) throw new UserError(I18n.T("Список слишком велик для редактора.", "List is too large for this editor."));
        if (info.Directory!.Attributes.HasFlag(FileAttributes.ReparsePoint) || (info.Exists && info.Attributes.HasFlag(FileAttributes.ReparsePoint)))
            throw new UserError(I18n.T("Выберите обычный файл списка в папке Obhod.", "Choose a regular list file in the Obhod folder."));
        var original = info.Exists ? File.ReadAllText(file) : null;
        var networks = info.Name.StartsWith("ipset-", StringComparison.Ordinal);
        var description = info.Name == "ipset-all.txt"
            ? I18n.T("Пустой ipset-all.txt в Obhod означает все IP. Изменения применятся при следующем запуске профиля.", "An empty ipset-all.txt means all IPs in Obhod. Changes apply on the next profile start.")
            : I18n.T("По одной записи в строке. Изменения вступят в силу при следующем запуске профиля.",
            "One entry per line. Changes take effect when the profile is next started.");
        var entries = Lines(label, description, (original ?? "").Replace("\r","").Split('\n'), lines =>
        {
            foreach (var line in lines.Select(s => s.Trim()).Where(s => s.Length > 0 && !s.StartsWith('#')))
                if (networks ? !(System.Net.IPAddress.TryParse(line, out _) || System.Net.IPNetwork.TryParse(line, out _)) :
                    Uri.CheckHostName(line.StartsWith('^') ? line[1..] : line) != UriHostNameType.Dns)
                    throw new UserError(I18n.T("Проверьте запись: ", "Check entry: ") + line);
            if (info.Name != "ipset-all.txt" && !lines.Any(s => s.Trim().Length > 0 && !s.TrimStart().StartsWith('#')))
                return [.. lines, networks ? "203.0.113.113/32" : "empty.invalid"];
            return lines;
        }, 4*1024*1024);
        if (entries is null) return;
        if ((File.Exists(file) ? File.ReadAllText(file) : null) != original)
            throw new UserError(I18n.T("Список изменился на диске. Откройте его заново.", "The file changed on disk. Reopen it."));
        var temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, string.Join(Environment.NewLine,entries), new UTF8Encoding(false)); File.Move(temporary,file,true); }
        finally { File.Delete(temporary); }
    }
}
