using System.Windows;
using System.Windows.Controls;

namespace ZapretTester.App;

public sealed class PrepareProfileWindow : Window
{
    private readonly ComboBox profiles;
    public string? ProfileId => (profiles.SelectedItem as DashboardCardRow)?.Id;

    public PrepareProfileWindow(IEnumerable<DashboardCardRow> choices)
    {
        Title = "Профиль Flowseal"; Width = 460; SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var content = new StackPanel { Margin = new Thickness(20) };
        content.Children.Add(new TextBlock { Text = "Не удалось однозначно определить исходный профиль. Выберите стратегию для основного Zapret. Перед изменениями появится подтверждение.", TextWrapping = TextWrapping.Wrap });
        profiles = new ComboBox { ItemsSource = choices, DisplayMemberPath = "Name", Margin = new Thickness(0, 16, 0, 16), MinHeight = 32 };
        System.Windows.Automation.AutomationProperties.SetName(profiles, "Исходный профиль Flowseal");
        content.Children.Add(profiles);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var next = new Button { Content = "Продолжить", IsDefault = true, IsEnabled = false };
        next.Click += (_, _) => DialogResult = true;
        profiles.SelectionChanged += (_, _) => next.IsEnabled = ProfileId != null;
        actions.Children.Add(next); actions.Children.Add(new Button { Content = "Отмена", IsCancel = true });
        content.Children.Add(actions); Content = content;
    }
}
