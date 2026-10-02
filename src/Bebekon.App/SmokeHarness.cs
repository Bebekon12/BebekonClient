using System.Diagnostics;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Bebekon.App;
/// <summary>Explicit developer-only render harness; synthetic fixtures never enter the user's settings.</summary>
internal static class SmokeHarness
{
    private static readonly string Root = FindRoot();
    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++, dir = dir.Parent) if (File.Exists(Path.Combine(dir.FullName, "Bebekon.sln"))) return Path.Combine(dir.FullName, "artifacts", "ui-smoke");
        return Path.Combine(Path.GetTempPath(), "Bebekon-QA", "ui-smoke");
    }
    public static MainViewModel CreateViewModel()
    {
        Directory.CreateDirectory(Root);
        var store = new StateStore(Path.Combine(Root, "test-user"));
        var data = new AppState();
        foreach (var name in new[] { "Estonia", "Poland", "Hungary", "Bulgaria", "Austria", "Romania", "Norway", "Albania", "USA · New York", "Russia, Saint Petersburg", "Russia, Novosibirsk", "Brazil", "France", "Germany", "Italy", "United Kingdom" }) data.Servers.Add(VlessParser.Parse("vless://" + Guid.NewGuid() + "@127.0.0.1:9?security=none&type=tcp#" + Uri.EscapeDataString(name)));
        data.Subscriptions.Add(new() { Name = "UI test fixture", Source = "https://example.invalid/private-token", ServerCount = data.Servers.Count });
        data.SelectedServerId = data.Servers[0].Id;
        data.Profiles[0].Rules.Add(new() { Name = "OpenAI / ChatGPT", Values = ["openai.com", "chatgpt.com", "oaistatic.com", "oaiusercontent.com"] });
        data.Profiles[0].Rules.Add(new() { Name = "Claude / Anthropic", Values = ["claude.ai", "anthropic.com"] });
        data.Profiles[0].Rules.Add(new() { Name = "Telegram", Kind = RuleKind.Application, Values = ["Telegram.exe"] });
        data.Settings.TunnelMode = TunnelMode.Proxy;
        store.Save(data);
        var vm = new MainViewModel(store);
        if (vm.Settings.TunnelMode != TunnelMode.Tun) throw new InvalidOperationException("Startup must select TUN even after a prior proxy session.");
        return vm;
    }
    public static async Task RunAsync(MainWindow window, MainViewModel vm)
    {
        await Task.Delay(400); var report = new List<string> { "Startup selects TUN after a saved proxy session." };
        foreach (var page in new[] { "Home", "Servers", "Rules", "Subscriptions", "Settings" })
        {
            vm.Go(page); await Task.Delay(300); window.UpdateLayout();
            foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75 })
            {
                var bitmap = new RenderTargetBitmap((int)(window.ActualWidth * scale), (int)(window.ActualHeight * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32); bitmap.Render(window);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var output = File.Create(Path.Combine(Root, page + "-" + (int)(scale * 100) + ".png")); encoder.Save(output);
            }
            report.Add(page + " rendered at 100/125/150/175%.");
        }
        vm.Go("Rules"); for (var i = 0; i < 500; i++) vm.ActiveProfile.Rules.Add(new() { Name = "Rule " + i, Values = [$"site{i}.example.com"] }); vm.RuleSearch = ""; vm.RuleSearch = "Rule"; await Task.Delay(200); window.UpdateLayout();
        vm.IsWholePc = true; window.UpdateLayout(); if (Descendants(window).OfType<RadioButton>().Count(r => r.IsChecked == true) != 1) throw new InvalidOperationException("Routing mode selection is inconsistent.");
        vm.IsWholePc = false; window.UpdateLayout(); if (Descendants(window).OfType<RadioButton>().Count(r => r.IsChecked == true) != 1) throw new InvalidOperationException("Selective mode selection is inconsistent.");
        report.Add("Mode switches: one selection in Entire PC and By rules.");
        var boxes = Descendants(window).OfType<ListBox>().ToArray(); var realized = boxes.Sum(b => Descendants(b).OfType<ListBoxItem>().Count()); report.Add($"503 rules, realized list containers: {realized} (virtualization).");
        vm.Go("Home"); await Task.Delay(1000); using var process = Process.GetCurrentProcess(); process.Refresh(); var before = process.TotalProcessorTime; await Task.Delay(3000); process.Refresh(); report.Add($"After rendering (not an idle benchmark): Working Set {process.WorkingSet64 / 1048576.0:F1} MB; private {process.PrivateMemorySize64 / 1048576.0:F1} MB; 3-second CPU {(process.TotalProcessorTime - before).TotalMilliseconds / 3000 / Environment.ProcessorCount * 100:F3}%.");
        File.WriteAllLines(Path.Combine(Root, "report.txt"), report);
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject d) { for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++) { var child = VisualTreeHelper.GetChild(d, i); yield return child; foreach (var x in Descendants(child)) yield return x; } }
}
