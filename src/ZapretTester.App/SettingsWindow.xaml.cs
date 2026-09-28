using System.Windows;
using ZapretTester.Core;

namespace ZapretTester.App;
public partial class SettingsWindow : Window
{
    public ApplicationSettings Value { get; private set; }
    public SettingsWindow(ApplicationSettings value)
    {
        InitializeComponent(); Value = value;
        Parallel.Text = value.MaxParallelTests.ToString(); Attempts.Text = value.Attempts.ToString(); Timeout.Text = value.TimeoutSeconds.ToString();
        LeagueFolder.Text = value.LeagueFolder;
        LoadLeague(value.LeagueRegion);
        First.Text = value.FirstPort.ToString(); Last.Text = value.LastPort.ToString(); Auto.IsChecked = value.AutoTest; Keep.IsChecked = value.KeepMainRunning;
    }
    private void LoadLeague(string? region = null)
    {
        try
        {
            var install = LeagueConfiguration.Load(LeagueFolder.Text, region);
            LeagueFolder.Text = install.Folder;
            LeagueRegion.ItemsSource = install.Regions.Keys.Order().ToArray();
            LeagueRegion.SelectedItem = install.SelectedRegion;
        }
        catch (Exception e) { ErrorText.Text = e.Message; }
    }
    private void ChooseLeague(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFolderDialog { Title = "Папка League of Legends (с system.yaml)" };
        if (picker.ShowDialog() == true) { LeagueFolder.Text = picker.FolderName; LoadLeague(); }
    }
    private void Save(object sender, RoutedEventArgs e)
    {
        try
        {
            var next = Value with { LeagueFolder = LeagueFolder.Text, LeagueRegion = LeagueRegion.SelectedItem as string, MaxParallelTests = int.Parse(Parallel.Text), Attempts = int.Parse(Attempts.Text), TimeoutSeconds = int.Parse(Timeout.Text),
                FirstPort = int.Parse(First.Text), LastPort = int.Parse(Last.Text), AutoTest = Auto.IsChecked == true, KeepMainRunning = Keep.IsChecked == true };
            next.Validate(); Value = next; DialogResult = true;
        }
        catch (Exception exception) when (exception is FormatException or OverflowException or ArgumentException) { ErrorText.Text = "Проверьте числа и размер диапазона портов."; }
    }
}
