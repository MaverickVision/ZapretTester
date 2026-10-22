using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ZapretTester.App;

public sealed class ActivityLogo : Image
{
    // Six seconds at 20 fps; see scripts/Build-ActivityLogo.ps1. All frames retain full alpha.
    public const int FrameCount = 120;
    private static readonly Lazy<BitmapSource[]> Frames = new(LoadFrames);
    private readonly DispatcherTimer timer = new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
    private Window? host;
    public static readonly DependencyProperty IsRunningProperty = DependencyProperty.Register(nameof(IsRunning), typeof(bool), typeof(ActivityLogo),
        new PropertyMetadata(false, (d, _) => ((ActivityLogo)d).UpdatePlayback()));
    public bool IsRunning { get => (bool)GetValue(IsRunningProperty); set => SetValue(IsRunningProperty, value); }
    public bool IsAnimating => timer.IsEnabled;
    public int FrameIndex { get; private set; }

    public ActivityLogo()
    {
        Source = Frames.Value[0]; Width = Height = 104;
        timer.Tick += (_, _) => NextFrame();
        Loaded += (_, _) => { host = Window.GetWindow(this); if (host != null) host.StateChanged += HostChanged; UpdatePlayback(); };
        Unloaded += (_, _) => { timer.Stop(); if (host != null) host.StateChanged -= HostChanged; host = null; };
        IsVisibleChanged += (_, _) => UpdatePlayback();
    }
    private void HostChanged(object? sender, EventArgs e) => UpdatePlayback();
    private void UpdatePlayback()
    {
        if (!IsRunning || !IsLoaded || !IsVisible || host?.WindowState == WindowState.Minimized)
        {
            timer.Stop(); FrameIndex = 0; Source = Frames.Value[0]; return;
        }
        if (!timer.IsEnabled) { FrameIndex = 0; Source = Frames.Value[0]; }
        if (SystemParameters.ClientAreaAnimation) timer.Start(); else timer.Stop();
    }
    private void NextFrame()
    {
        FrameIndex = (FrameIndex + 1) % FrameCount;
        Source = Frames.Value[FrameIndex];
    }
    private static BitmapImage Load(string name)
    {
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad;
        image.UriSource = new Uri("pack://application:,,,/ZapretTester;component/Assets/" + name);
        image.EndInit(); image.Freeze(); return image;
    }
    private static BitmapSource[] LoadFrames()
    {
        var atlas = Load("activity-logo.png");
        return Enumerable.Range(0, FrameCount).Select(index => {
            var frame = new CroppedBitmap(atlas, new Int32Rect(index % 10 * 192, index / 10 * 192, 192, 192));
            frame.Freeze(); return (BitmapSource)frame;
        }).ToArray();
    }
}
