using System.Text;
using System.Text.RegularExpressions;

namespace Bebekon.App;

public static partial class Dialogs
{
    internal static DirectTrafficPolicy? DirectPolicyEditor(DirectTrafficPolicy? current, string folder)
    {
        current ??= new();
        var window = Shell(I18n.T("Прямой трафик", "Direct traffic"), out var body);
        TextBox ListField(string label, string[] entries)
        {
            var field = Field(body, label, string.Join(Environment.NewLine, entries));
            field.AcceptsReturn = true; field.MinHeight = 70; field.MaxHeight = 130; field.MaxLength = 512 * 1024;
            field.VerticalContentAlignment = VerticalAlignment.Top; field.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            return field;
        }
        var include = ListField(I18n.T("Применять к доменам и IP/CIDR · пусто — все прямые маршруты", "Apply to domains and IP/CIDR · empty includes all direct routes"), current.Include ?? []);
        var exclude = ListField(I18n.T("Исключения · имеют приоритет", "Exclusions · take priority"), current.Exclude ?? []);
        var preferTcp = new CheckBox { Content = I18n.T("Предпочитать TCP для HTTPS", "Prefer TCP for HTTPS"), IsChecked = current.PreferTcp, Margin = new(0,10,0,3) };
        body.Children.Add(preferTcp);
        body.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Style = (Style)Application.Current.Resources["Subtitle"],
            Text = I18n.T("Для выбранных адресов Bebekon отклоняет QUIC на UDP/443. Браузер с поддержкой TCP может повторить запрос по HTTPS. Эти настройки действуют при включённом обходе замедления.",
                "Bebekon rejects QUIC on UDP/443 for the selected addresses. Browsers supporting TCP can retry over HTTPS. Enable the direct TLS option to apply these settings.") });
        var import = new Button { Content = I18n.T("Добавить списки из Obhod…", "Import Obhod lists…"), Margin = new(0,10,0,0) };
        body.Children.Add(import);
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new(0,5,0,0) }; error.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
        body.Children.Add(error);
        import.Click += (_,_) =>
        {
            var picker = new Microsoft.Win32.OpenFolderDialog { Title = I18n.T("Папка Obhod или lists", "Obhod or lists folder") };
            if (Path.IsPathFullyQualified(folder ?? "") && Directory.Exists(folder)) picker.InitialDirectory = folder;
            if (picker.ShowDialog(window) != true) return;
            try
            {
                var root = picker.FolderName;
                if (Directory.Exists(Path.Combine(root,"lists"))) root = Path.Combine(root,"lists");
                if (!File.Exists(Path.Combine(root,"list-general.txt")) && !File.Exists(Path.Combine(root,"list-general-user.txt")))
                    throw new UserError(I18n.T("В этой папке нет списков Obhod.", "No Obhod site lists in this folder."));
                string Load(string existing, params string[] names)
                {
                    var lines = existing.Split('\n').AsEnumerable();
                    foreach (var name in names)
                    {
                        var file = new FileInfo(Path.Combine(root,name));
                        if (!file.Exists) continue;
                        if (file.Length > 4*1024*1024) throw new UserError(I18n.T("Слишком большой список.", "List is too large."));
                        if (name == "ipset-all.txt" && file.Length == 0)
                            lines = lines.Concat(new[] { "0.0.0.0/0", "::/0" });
                        else lines = lines.Concat(File.ReadAllLines(file.FullName));
                    }
                    return string.Join(Environment.NewLine,DirectTrafficPolicy.ParseList(lines).Entries);
                }
                var included = Load(include.Text, "list-general.txt","list-general-user.txt","list-google.txt","ipset-all.txt");
                var excluded = Load(exclude.Text, "list-exclude.txt","list-exclude-user.txt","ipset-exclude.txt","ipset-exclude-user.txt");
                include.Text = included; exclude.Text = excluded;
                error.Text = I18n.T("Списки добавлены. Проверьте их и нажмите «Сохранить».", "Lists imported. Review and save.");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or UserError)
            { error.Text = e is UserError ? e.Message : I18n.T("Не удалось прочитать списки.", "Could not read the lists."); }
        };
        DirectTrafficPolicy? result = null;
        Submit(body,I18n.T("Сохранить","Save"),() =>
        {
            try
            {
                result = new() { Include = DirectTrafficPolicy.ParseList(include.Text.Split('\n')).Entries,
                    Exclude = DirectTrafficPolicy.ParseList(exclude.Text.Split('\n')).Entries, PreferTcp = preferTcp.IsChecked == true };
                window.DialogResult = true;
            } catch (UserError e) { error.Text = e.Message; }
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
