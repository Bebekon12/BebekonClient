using System.ComponentModel;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Bebekon.App;

public partial class UpdateOverlay : UserControl
{
    private MainViewModel? vm;
    private Window? owner;
    public UpdateOverlay()
    {
        InitializeComponent();
        Loaded += (_, _) => { vm = DataContext as MainViewModel; if (vm is not null) { vm.PropertyChanged += Changed; DownloadProgress.Value = vm.UpdatePercent; } owner = Window.GetWindow(this); if (owner is not null) owner.StateChanged += OnWindowState; ThemeManager.Changed += Animate; Animate(); };
        Unloaded += (_, _) => { if (vm is not null) vm.PropertyChanged -= Changed; if (owner is not null) owner.StateChanged -= OnWindowState; ThemeManager.Changed -= Animate; Stop(); vm = null; owner = null; };
        IsVisibleChanged += (_, _) => { if (IsVisible) { Motion.Reveal(UpdateCard); Dispatcher.BeginInvoke(new Action(() => { if (IsVisible && UpdateButton.IsVisible) UpdateButton.Focus(); })); } Animate(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { vm?.DeferUpdate.Execute(null); e.Handled = true; } };
    }
    private void OnWindowState(object? sender, EventArgs e) => Animate();
    private void Changed(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.UpdatePercent))
        {
            var from = DownloadProgress.Value; DownloadProgress.BeginAnimation(RangeBase.ValueProperty, null); DownloadProgress.Value = vm!.UpdatePercent;
            if (IsVisible && Motion.Enabled) DownloadProgress.BeginAnimation(RangeBase.ValueProperty, new DoubleAnimation(from, vm.UpdatePercent, TimeSpan.FromMilliseconds(220)) { FillBehavior = FillBehavior.Stop, EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        }
        if (e.PropertyName is nameof(MainViewModel.UpdateBusy) or nameof(MainViewModel.UpdateStage)) Animate();
    }
    private void Stop() { SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, null); DownloadProgress.BeginAnimation(RangeBase.ValueProperty, null); }
    private void Animate()
    {
        SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        if (!IsVisible || owner?.WindowState == WindowState.Minimized || !Motion.Enabled) DownloadProgress.BeginAnimation(RangeBase.ValueProperty, null);
        if (IsVisible && owner?.WindowState != WindowState.Minimized && Motion.Enabled && vm?.UpdateBusy == true)
            SpinnerRotation.BeginAnimation(RotateTransform.AngleProperty, new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.4)) { RepeatBehavior = RepeatBehavior.Forever });
    }
}
