using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;

namespace ZapretTester.App;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0];
        VersionLabel.Text = $"Версия {version ?? "—"}";
    }

    private void OpenRepository(object sender, RequestNavigateEventArgs e)
    {
        e.Handled = true;
        try { Process.Start(new ProcessStartInfo("https://github.com/MaverickVision/ZapretTester") { UseShellExecute = true }); }
        catch (System.ComponentModel.Win32Exception error) { MessageBox.Show(this, error.Message, "Не удалось открыть браузер", MessageBoxButton.OK, MessageBoxImage.Information); }
    }
}
