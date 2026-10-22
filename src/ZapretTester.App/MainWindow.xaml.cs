using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace ZapretTester.App;
public partial class MainWindow : Window
{
    private readonly MainViewModel model = new();
    private bool closing;
    private (string Id, int Index, IInputElement Element)? dashboardFocus;
    public MainWindow()
    {
        InitializeComponent(); DataContext = model;
        model.DashboardUpdating += CaptureDashboardFocus;
        model.DashboardUpdated += RestoreDashboardFocus;
        Loaded += async (_, _) =>
        {
            await model.InitializeAsync();
            var arguments = Environment.GetCommandLineArgs();
            var render = Array.IndexOf(arguments, "--render-check");
            if (render >= 0 && render + 1 < arguments.Length)
            {
                await Dispatcher.InvokeAsync(UpdateLayout, DispatcherPriority.Render);
                var image = new RenderTargetBitmap((int)ActualWidth, (int)ActualHeight, 96, 96, PixelFormats.Pbgra32);
                image.Render(this);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
                using (var file = File.Create(Path.GetFullPath(arguments[render + 1]))) encoder.Save(file);
                Close();
            }
        };
        Closing += OnClosing;
    }
    private void CaptureDashboardFocus()
    {
        dashboardFocus = null;
        if (Keyboard.FocusedElement is not FrameworkElement focused || !DashboardList.IsAncestorOf(focused)) return;
        var parent = (DependencyObject)focused;
        while (parent != null)
        {
            if (parent is ContentPresenter { Content: DashboardCardRow card } presenter)
            {
                var elements = VisualDescendants(presenter).OfType<FrameworkElement>().Where(e => e.Focusable).ToArray();
                var index = Array.IndexOf(elements, focused);
                if (index >= 0) dashboardFocus = (card.Id, index, focused);
                return;
            }
            parent = VisualTreeHelper.GetParent(parent);
        }
    }
    private void RestoreDashboardFocus()
    {
        if (dashboardFocus is not { } saved) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (!IsActive) return;
            // Do not steal focus if the user has moved to another live control meanwhile.
            if (Keyboard.FocusedElement is FrameworkElement current && current != saved.Element && current != this
                && PresentationSource.FromVisual(current) != null) return;
            var presenter = VisualDescendants(DashboardList).OfType<ContentPresenter>()
                .FirstOrDefault(p => p.Content is DashboardCardRow row && row.Id == saved.Id);
            if (presenter == null) return;
            var target = VisualDescendants(presenter).OfType<FrameworkElement>().Where(e => e.Focusable).ElementAtOrDefault(saved.Index);
            target?.Focus();
        }, DispatcherPriority.Loaded);
    }
    private static IEnumerable<DependencyObject> VisualDescendants(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            yield return child;
            foreach (var descendant in VisualDescendants(child)) yield return descendant;
        }
    }
    private void OpenHint(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (ReferenceEquals(sender, e.OriginalSource) && sender is FrameworkElement { ToolTip: System.Windows.Controls.ToolTip hint } element)
        { hint.PlacementTarget = element; hint.IsOpen = true; }
    }
    private void OpenCardDetails(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DashboardCardRow row }) model.OpenDetails(row);
    }
    private void OpenAbout(object sender, RoutedEventArgs e) => new AboutWindow { Owner = this }.ShowDialog();
    private void OpenFaq(object sender, RoutedEventArgs e) => new FaqWindow { Owner = this }.ShowDialog();
    private void CloseHint(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (sender is FrameworkElement { ToolTip: System.Windows.Controls.ToolTip hint }) hint.IsOpen = false;
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (closing) return;
        e.Cancel = true;
        try { await model.DisposeAsync(); }
        catch (Exception exception) { MessageBox.Show(exception.Message, "Завершение работы"); }
        finally { closing = true; _ = Dispatcher.BeginInvoke(new Action(Close)); }
    }
}
