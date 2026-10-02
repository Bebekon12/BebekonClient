using System.Windows.Input;

namespace Bebekon.App;
public sealed class Command(Action<object?> action, Func<bool>? can = null) : ICommand
{
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? p) => can?.Invoke() ?? true;
    public void Execute(object? p) => action(p);
}
public sealed class AsyncCommand(Func<object?, Task> action, Action<Exception> onError, Func<bool>? can = null) : ICommand
{
    private bool running;
    public event EventHandler? CanExecuteChanged { add => CommandManager.RequerySuggested += value; remove => CommandManager.RequerySuggested -= value; }
    public bool CanExecute(object? p) => !running && (can?.Invoke() ?? true);
    public async void Execute(object? p)
    {
        if (!CanExecute(p)) return; running = true; CommandManager.InvalidateRequerySuggested();
        try { await action(p); } catch (Exception e) { onError(e); }
        finally { running = false; CommandManager.InvalidateRequerySuggested(); }
    }
}
public abstract record PageModel(MainViewModel Main);
public sealed record HomePage(MainViewModel Main) : PageModel(Main);
public sealed record ServersPage(MainViewModel Main) : PageModel(Main);
public sealed record RulesPage(MainViewModel Main) : PageModel(Main);
public sealed record SubscriptionsPage(MainViewModel Main) : PageModel(Main);
public sealed record SettingsPage(MainViewModel Main) : PageModel(Main);
public sealed record ServerRow(Server Left, Server? Right);
