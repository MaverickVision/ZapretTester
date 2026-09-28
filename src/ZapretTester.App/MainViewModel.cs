using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Microsoft.Win32;
using ZapretTester.Core;

namespace ZapretTester.App;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Notify([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}
public sealed class AsyncCommand(Func<Task> action, Func<bool>? enabled = null) : ICommand
{
    private bool running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !running && (enabled?.Invoke() ?? true);
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        running = true; CanExecuteChanged?.Invoke(this, EventArgs.Empty);
        try { await action(); }
        finally { running = false; CanExecuteChanged?.Invoke(this, EventArgs.Empty); }
    }
}
public sealed record EndpointCell(string Text, string Color, string Details);
public sealed class ProfileRow(ProfileView model, IReadOnlyList<TestTarget>? targets = null) : Observable
{
    public ProfileView Model { get; private set; } = model;
    private IReadOnlyList<TestTarget> targets = targets ?? TestTarget.Defaults;
    public string Id => Model.Id;
    public string Name => Model.Name + (Model.IsMain ? "  • текущий" : "");
    public string Status => Model.Error != null ? "Ошибка профиля" : Model.RunState switch
    {
        RunState.Queued => "В очереди", RunState.Testing => "Проверяется…", RunState.Cancelled => "Отменено",
        RunState.Blocked => "Заблокировано",
        _ => Model.IsStale ? "Устарел" : Model.LastResult is not { } r ? "Не проверено" : r.PendingValidation ? "Ожидает подтверждения" : !r.IsReliable ? "Недостоверен"
            : $"{r.Endpoints.Count(e => e.Status == EndpointStatus.Available)}/{r.ExpectedEndpoints}"
    };
    public string Latency => Model.LastResult?.MedianMilliseconds is { } ms ? $"{ms:F0} мс" : "—";
    public string Timestamp => Model.LastResult?.TestedAt.LocalDateTime.ToString("HH:mm:ss") ?? "—";
    public double Opacity => Model.IsStale || Model.RunState is RunState.Queued or RunState.Testing ? 0.5 : 1;
    public EndpointCell Website => CellAt(0);
    public EndpointCell Gateway => CellAt(1);
    public EndpointCell Cdn => CellAt(2);
    public EndpointCell UpdateEndpoint => CellAt(3);
    private EndpointCell CellAt(int index) => index < targets.Count ? Cell(targets[index].Name) : new("—", "#929BA5", "Цель недоступна");
    private EndpointCell Cell(string name)
    {
        var result = Model.LastResult;
        var e = result?.Endpoints.FirstOrDefault(e => e.Name == name);
        if (e == null) return new("—", "#929BA5", "Ещё не проверено");
        var valid = result!.IsReliable && !result.PendingValidation && !Model.IsStale;
        return new(e.HttpCode?.ToString() ?? e.FailureStage?.ToString() ?? "Ошибка",
            !valid ? "#929BA5" : e.Status == EndpointStatus.Available ? "#218559" : e.Status == EndpointStatus.ServerWarning ? "#B77916" : "#C34343",
            $"{name}: {e.Status}\n{e.Duration.TotalMilliseconds:F0} мс; DNS {e.DnsDuration.TotalMilliseconds:F0} мс\n{e.RemoteIp}:{443}, локальный порт {e.LocalPort}\n{e.Error}");
    }
    public string Details => Model.Error ?? Model.RunError ?? (Model.LastResult is not { } r ? "Выберите «Проверить» для получения подробностей."
        : $"{Model.Name} • {r.TestedAt.LocalDateTime:g} • {(r.IsReliable ? "Изолированный тест" : "Результат недостоверен")}\n{r.Error}\n"
            + string.Join("\n", r.Endpoints.Select(e => $"{e.Name}: {e.Status}, HTTP {e.HttpCode?.ToString() ?? "—"}, {e.Duration.TotalMilliseconds:F0} мс, порт {e.LocalPort}; {e.Error}")));
    public void Update(ProfileView value, IReadOnlyList<TestTarget>? updatedTargets = null) { Model = value; targets = updatedTargets ?? targets; Notify(null); }
}

public sealed class MainViewModel : Observable, IAsyncDisposable
{
    private readonly string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZapretTester");
    private HelperClient client;
    private ApplicationSettings settings = new();
    private string folder = "", message = "Выберите папку Flowseal, затем стратегию.", mode = "Не подключён";
    private ProfileRow? selected;
    private int selectedTab;
    private int viewGeneration;
    private bool isTesting;
    private IReadOnlyList<TestTarget> currentTargets = TestTarget.Defaults;
    public TestSuiteId SelectedSuite => selectedTab switch { 1 => TestSuiteId.LeagueOfLegends, 2 => TestSuiteId.YouTube, _ => TestSuiteId.Discord };
    public bool IsDashboard => selectedTab == 3;
    public Visibility DashboardVisibility => IsDashboard ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TestsVisibility => IsDashboard ? Visibility.Collapsed : Visibility.Visible;
    public ObservableCollection<DashboardCardRow> DashboardCards { get; } = [];
    public ListCollectionView DashboardCardsView { get; }
    public event Action? DashboardUpdating;
    public event Action? DashboardUpdated;
    public int DashboardSortIndex
    {
        get => (int)dashboardPreferences.SortMode;
        set
        {
            if (!Enum.IsDefined((DashboardSortMode)value) || value == DashboardSortIndex) return;
            dashboardPreferences.SortMode = (DashboardSortMode)value;
            SaveDashboardPreferences(); Notify();
            DashboardUpdating?.Invoke();
            try { RefreshDashboardOrder(true); }
            finally { DashboardUpdated?.Invoke(); }
        }
    }
    private IReadOnlyList<SuiteView> suiteViews = [];
    private DashboardPreferences dashboardPreferences = new();
    private bool bulkSelection;
    private HelperState? dashboardSnapshot;
    public bool ShowDiscord { get => Visible(TestSuiteId.Discord); set => SetVisible(TestSuiteId.Discord, value); }
    public bool ShowLeague { get => Visible(TestSuiteId.LeagueOfLegends); set => SetVisible(TestSuiteId.LeagueOfLegends, value); }
    public bool ShowYouTube { get => Visible(TestSuiteId.YouTube); set => SetVisible(TestSuiteId.YouTube, value); }
    public bool HasVisibleServices => dashboardPreferences.VisibleSuites.Length > 0;
    public Visibility CardsVisibility => HasVisibleServices ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyServicesVisibility => HasVisibleServices ? Visibility.Collapsed : Visibility.Visible;
    public string SelectionSummary => $"{DashboardCards.Count(c => c.IsSelected)}/{DashboardCards.Count} стратегий";
    public string SeriesSummary => dashboardSnapshot?.Series is { } series
        ? $"{series.Completed}/{series.Total} проверок сервисов" + (isTesting ? " • выполняется" : $" • {series.ElapsedSeconds:F1} с") : "";
    private bool Visible(TestSuiteId suite) => dashboardPreferences.VisibleSuites.Contains(suite);
    private void SetVisible(TestSuiteId suite, bool value)
    {
        dashboardPreferences.VisibleSuites = value ? dashboardPreferences.VisibleSuites.Append(suite).Distinct().Order().ToArray()
            : dashboardPreferences.VisibleSuites.Where(s => s != suite).ToArray();
        SaveDashboardPreferences(); ApplySuites(suiteViews, dashboardSnapshot, forceOrder: true);
        Notify(nameof(HasVisibleServices)); Notify(nameof(CardsVisibility)); Notify(nameof(EmptyServicesVisibility));
        Notify(nameof(ShowDiscord)); Notify(nameof(ShowLeague)); Notify(nameof(ShowYouTube)); RefreshCommands();
    }
    private void SaveDashboardPreferences()
    {
        try { JsonFiles.Save(Path.Combine(data, "dashboard.json"), dashboardPreferences); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { MessageText = e.Message; }
    }
    private void SelectionChanged(DashboardCardRow row)
    {
        dashboardPreferences.Select(Folder, row.Id, row.IsSelected);
        if (!bulkSelection) SaveDashboardPreferences();
        Notify(nameof(SelectionSummary)); RefreshCommands();
    }
    private Task SelectCards(bool selected)
    {
        bulkSelection = true;
        try { foreach (var row in DashboardCards) row.IsSelected = selected; }
        finally { bulkSelection = false; }
        SaveDashboardPreferences(); return Task.CompletedTask;
    }
    private void ApplySuites(IReadOnlyList<SuiteView> suites, HelperState? snapshot = null, bool forceOrder = false)
    {
        DashboardUpdating?.Invoke();
        try
        {
            suiteViews = suites; dashboardSnapshot = snapshot;
            var cards = DashboardProjection.Cards(suites, participants: snapshot?.IsTesting == true ? snapshot.Series?.Participants : null);
            var existing = DashboardCards.ToDictionary(c => c.Id);
            for (var i = 0; i < cards.Count; i++)
            {
                var card = cards[i];
                if (!existing.TryGetValue(card.Id, out var row))
                {
                    row = new DashboardCardRow(card, dashboardPreferences.IsSelected(Folder, card.Id), SelectionChanged);
                    DashboardCards.Insert(i, row);
                }
                else if (DashboardCards.IndexOf(row) != i) DashboardCards.Move(DashboardCards.IndexOf(row), i);
                row.Update(card, dashboardPreferences.VisibleSuites);
                row.IsSelected = dashboardPreferences.IsSelected(Folder, card.Id);
            }
            while (DashboardCards.Count > cards.Count) DashboardCards.RemoveAt(DashboardCards.Count - 1);
            RefreshDashboardOrder(forceOrder);
            Notify(nameof(SelectionSummary)); Notify(nameof(SeriesSummary)); RefreshCommands();
            }
        finally { DashboardUpdated?.Invoke(); }
    }
    private void RefreshDashboardOrder(bool force = false)
    {
        // Freeze the existing ranks while provisional results arrive. Explicit user choices still apply immediately.
        if (dashboardSnapshot?.IsTesting == true && !force) return;
        var ordered = dashboardPreferences.SortMode == DashboardSortMode.BestFirst
            ? DashboardCards.OrderByDescending(c => c.Score.HasValue).ThenByDescending(c => c.Score).ToArray()
            : DashboardCards.ToArray();
        if (DashboardCardsView.Cast<DashboardCardRow>().Select(c => c.Id).SequenceEqual(ordered.Select(c => c.Id))) return;
        var ranks = ordered.Select((c, i) => (c.Id, Index: i)).ToDictionary(p => p.Id, p => p.Index);
        DashboardCardsView.CustomSort = Comparer<DashboardCardRow>.Create((a, b) =>
            ranks.GetValueOrDefault(a.Id, int.MaxValue).CompareTo(ranks.GetValueOrDefault(b.Id, int.MaxValue)));
    }
    private async Task TestBatchAsync()
    {
        var ids = DashboardCards.Where(c => c.IsSelected).Select(c => c.Id).ToArray();
        var suites = dashboardPreferences.VisibleSuites.Select(s => new SuiteSelection(s, s == TestSuiteId.LeagueOfLegends ? settings.LeagueRegion : null)).ToArray();
        await EnsureConnectedAsync();
        await client.SendAsync("TestBatch", profileIds: ids, testSuites: suites);
    }
    public int SelectedTab
    {
        get => selectedTab;
        set { if (selectedTab == value) return; selectedTab = value; viewGeneration++; Profiles.Clear(); Selected = null; SetTargets(value == 0 ? TestTarget.Defaults : []); Notify(); Notify(nameof(SuiteNotice)); Notify(nameof(IsDashboard)); Notify(nameof(DashboardVisibility)); Notify(nameof(TestsVisibility)); RefreshCommands(); _ = SwitchSuiteAsync(); }
    }
    public string SuiteNotice => IsDashboard ? "" : SelectedSuite == TestSuiteId.YouTube ? "Проверена доступность HTTPS-служб YouTube. Воспроизведение и скорость видео не проверялись." : SelectedSuite == TestSuiteId.Discord
        ? "HTTPS Gateway не является проверкой голоса или WebSocket."
        : "Проверена доступность служб клиента. Вход в аккаунт и подключение к матчу не проверялись.";
    public string Header0 => currentTargets.ElementAtOrDefault(0)?.Name ?? "Авторизация Riot";
    public string Header1 => currentTargets.ElementAtOrDefault(1)?.Name ?? "Конфигурация клиента";
    public string Header2 => currentTargets.ElementAtOrDefault(2)?.Name ?? "Платформа";
    public string Header3 => currentTargets.ElementAtOrDefault(3)?.Name ?? "CDN обновлений";
    private void SetTargets(IReadOnlyList<TestTarget> targets)
    { currentTargets = targets; Notify(nameof(Header0)); Notify(nameof(Header1)); Notify(nameof(Header2)); Notify(nameof(Header3)); }
    private async Task SwitchSuiteAsync()
    {
        try { if (client.Connected) { if (IsDashboard) await client.SendAsync("State"); else await client.SendAsync("SelectSuite", suite: SelectedSuite, region: settings.LeagueRegion); } else await RefreshAsync(); }
        catch (Exception e) { MessageText = e.Message; }
    }
    public ObservableCollection<ProfileRow> Profiles { get; } = [];
    public string Folder { get => folder; private set { folder = value; Notify(); } }
    public string MessageText { get => message; private set { message = value; Notify(); } }
    public string Mode { get => mode; private set { mode = value; Notify(); } }
    public ProfileRow? Selected { get => selected; set { selected = value; Notify(); Notify(nameof(Details)); } }
    public string Details => selected?.Details ?? "Выберите стратегию, чтобы увидеть подробности проверок.";
    public ICommand ChooseFolderCommand { get; }
    public ICommand TestAllCommand { get; }
    public ICommand TestBatchCommand { get; }
    public ICommand SelectAllCardsCommand { get; }
    public ICommand ClearCardsCommand { get; }
    public ICommand TestOneCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand MainCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand StopMainCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand DiagnosticsCommand { get; }
    public ICommand LogsCommand { get; }
    private HelperState? state;

    public MainViewModel()
    {
        DashboardCardsView = new ListCollectionView(DashboardCards);
        client = NewClient();
        TestBatchCommand = Command(TestBatchAsync, () => !isTesting && HasVisibleServices && DashboardCards.Any(c => c.IsSelected));
        SelectAllCardsCommand = Command(() => SelectCards(true));
        ClearCardsCommand = Command(() => SelectCards(false));
        ChooseFolderCommand = Command(ChooseFolderAsync);
        TestAllCommand = Command(() => SendAsync("Test"), () => !isTesting && !IsDashboard);
        TestOneCommand = Command(() => selected == null ? throw new InvalidOperationException("Выберите стратегию.") : SendAsync("Test", selected.Id), () => !isTesting && !IsDashboard);
        CancelCommand = Command(() => client.Connected ? client.SendAsync("Cancel") : Task.CompletedTask, () => isTesting);
        RefreshCommand = Command(RefreshAsync, () => !isTesting);
        MainCommand = Command(MakeMainAsync, () => !IsDashboard);
        StopMainCommand = Command(async () =>
        {
            if (MessageBox.Show("Остановить основной Zapret? Исходная служба останется отключённой до команды восстановления.", "Остановить Zapret", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                await SendAsync("StopMain");
        });
        RestoreCommand = Command(async () =>
        {
            if (MessageBox.Show("Вернуть исходную службу Flowseal и её автозапуск? Возможен краткий разрыв соединений.", "Восстановить Flowseal", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                await SendAsync("RestoreExternal");
        });
        SettingsCommand = Command(EditSettingsAsync);
        DiagnosticsCommand = Command(async () =>
        {
            await EnsureConnectedAsync();
            await client.SendAsync("State");
            MessageBox.Show(state?.Diagnostics ?? "Диагностика загружается; повторите после подключения.", "Диагностика");
        });
        LogsCommand = Command(() =>
        {
            var path = Path.Combine(data, "logs"); Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { path } });
            return Task.CompletedTask;
        });
    }
    private void RefreshCommands()
    {
        foreach (var command in new[] { TestAllCommand, TestOneCommand, RefreshCommand, CancelCommand, MainCommand, TestBatchCommand })
            ((AsyncCommand)command).Refresh();
    }
    private AsyncCommand Command(Func<Task> action, Func<bool>? enabled = null) => new(async () =>
    {
        try { await action(); }
        catch (Exception e) { MessageText = e is Win32Exception { NativeErrorCode: 1223 } ? "Повышение прав отменено." : e.Message; }
    }, enabled);
    private HelperClient NewClient()
    {
        var value = new HelperClient();
        value.Message += m => Application.Current.Dispatcher.InvokeAsync(() => Apply(m));
        return value;
    }
    public async Task InitializeAsync()
    {
        try
        {
            var path = Path.Combine(data, "settings.json");
            if (File.Exists(path)) settings = JsonSerializer.Deserialize<ApplicationSettings>(File.ReadAllText(path), JsonFiles.Options) ?? new();
            if (Environment.GetCommandLineArgs().Contains("--league")) { selectedTab = 1; Notify(nameof(SelectedTab)); Notify(nameof(SuiteNotice)); }
            if (Environment.GetCommandLineArgs().Contains("--youtube")) { selectedTab = 2; Notify(nameof(SelectedTab)); Notify(nameof(SuiteNotice)); }
            if (Environment.GetCommandLineArgs().Contains("--dashboard")) { selectedTab = 3; Notify(nameof(SelectedTab)); Notify(nameof(IsDashboard)); Notify(nameof(DashboardVisibility)); Notify(nameof(TestsVisibility)); Notify(nameof(SuiteNotice)); RefreshCommands(); }
            try
            {
                var preferencesPath = Path.Combine(data, "dashboard.json");
                if (File.Exists(preferencesPath)) dashboardPreferences = JsonSerializer.Deserialize<DashboardPreferences>(File.ReadAllText(preferencesPath), JsonFiles.Options) ?? new();
                dashboardPreferences.VisibleSuites = dashboardPreferences.VisibleSuites.Where(s => Enum.IsDefined(s)).Distinct().Order().ToArray();
                if (!Enum.IsDefined(dashboardPreferences.SortMode)) dashboardPreferences.SortMode = DashboardSortMode.BestFirst;
            }
            catch (Exception e) when (e is IOException or JsonException) { dashboardPreferences = new(); }
            Notify(nameof(ShowDiscord)); Notify(nameof(ShowLeague)); Notify(nameof(ShowYouTube)); Notify(nameof(CardsVisibility)); Notify(nameof(EmptyServicesVisibility));
            Notify(nameof(DashboardSortIndex));
            Folder = settings.FlowsealFolder;
            if (Directory.Exists(Folder))
            {
                await RefreshAsync();
                if (!IsDashboard && !Environment.GetCommandLineArgs().Contains("--render-check"))
                {
                    if (settings.ManagedEnabled) await EnsureConnectedAsync();
                    if (settings.AutoTest) await SendAsync("Test");
                }
            }
        }
        catch (Exception e) { MessageText = e.Message; }
    }
    private async Task ChooseFolderAsync()
    {
        if (state?.Mode == "Managed") throw new InvalidOperationException("Сначала восстановите исходный Flowseal перед сменой каталога.");
        var dialog = new OpenFolderDialog { Title = "Папка zapret-discord-youtube" };
        if (dialog.ShowDialog() != true) return;
        if (client.Connected) await client.SendAsync("Shutdown");
        await client.DisposeAsync(); client = NewClient(); state = null;
        Folder = dialog.FolderName;
        settings = settings with { FlowsealFolder = Folder, ManagedEnabled = false, MainProfileId = null };
        JsonFiles.Save(Path.Combine(data, "settings.json"), settings);
        await RefreshAsync();
    }
    private async Task EnsureConnectedAsync()
    {
        if (!Directory.Exists(Folder)) throw new InvalidOperationException("Укажите существующую папку Flowseal.");
        if (!client.Connected) { MessageText = "Подключение Helper… Подтвердите UAC при запросе Windows."; await client.ConnectAsync(Folder); if (!IsDashboard) await client.SendAsync("SelectSuite", suite: SelectedSuite, region: settings.LeagueRegion); }
    }
    private async Task SendAsync(string command, string? profileId = null)
    {
        if (IsDashboard && command is "Test" or "StartManaged") throw new InvalidOperationException("Откройте отдельную вкладку приложения.");
        var suite = SelectedSuite; var region = settings.LeagueRegion;
        await EnsureConnectedAsync();
        await client.SendAsync(command, profileId, suite: suite, region: region);
    }
    private async Task MakeMainAsync()
    {
        await EnsureConnectedAsync();
        if (selected == null) throw new InvalidOperationException("Выберите основную стратегию.");
        if (MessageBox.Show($"Сделать {selected.Model.Name}{(SelectedSuite == TestSuiteId.LeagueOfLegends ? " + LoL (" + settings.LeagueRegion + ")" : SelectedSuite == TestSuiteId.YouTube ? " • YouTube" : " • Discord")} основной?\n\nПри первом переходе служба Flowseal будет остановлена, её автозапуск отключён. Zapret будет запускаться через это приложение. Возможен краткий разрыв соединений.",
            "Управление Zapret", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        MessageText = "Запуск основной стратегии…";
        await SendAsync("StartManaged", selected.Id);
    }
    private async Task RefreshAsync()
    {
        if (client.Connected)
        {
            await client.SendAsync("Refresh");
            if (!IsDashboard) await client.SendAsync("SelectSuite", suite: SelectedSuite, region: settings.LeagueRegion);
            return;
        }
        var generation = ++viewGeneration;
        if (!Directory.Exists(Folder)) { DashboardCards.Clear(); Profiles.Clear(); return; }
        var capturedFolder = Folder; var capturedSettings = settings;
        IReadOnlyList<SuiteView> suites;
        try { suites = await Task.Run(() => SuiteProjection.Load(capturedFolder, capturedSettings, Path.Combine(data, "results.json"))); }
        catch
        {
            if (generation == viewGeneration)
            {
                if (suiteViews.Count > 0) ApplySuites(suiteViews.Select(s => s with {
                    Profiles = s.Profiles.Select(p => p with { Error = "Не удалось перечитать сохранённые результаты. Откройте журнал или повторите обновление." }).ToArray() }).ToArray());
                else DashboardCards.Clear();
            }
            throw;
        }
        if (generation != viewGeneration) return;
        ApplySuites(suites);
        var suite = suites.Single(s => s.Suite == SelectedSuite);
        SetTargets(suite.Targets);
        Profiles.Clear();
        foreach (var row in suite.Profiles) Profiles.Add(new(row, suite.Targets));
        Selected = Profiles.FirstOrDefault(p => p.Id == settings.MainProfileId) ?? Profiles.FirstOrDefault();
        MessageText = IsDashboard ? "" : suite.Error ?? $"Найдено профилей: {Profiles.Count}. {SelectedSuite}";
    }
    private async Task EditSettingsAsync()
    {
        var dialog = new SettingsWindow(settings) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true) return;
        var updated = dialog.Value with { FlowsealFolder = Folder };
        if (client.Connected) await client.SendAsync("Settings", settings: updated);
        else { settings = updated; JsonFiles.Save(Path.Combine(data, "settings.json"), settings); await RefreshAsync(); }
    }
    private void Apply(HelperMessage message)
    {
        if (message.State is { } snapshot)
        {
            state = snapshot; settings = snapshot.Settings;
            isTesting = snapshot.IsTesting;
            RefreshCommands();
            if (snapshot.Suites != null) ApplySuites(snapshot.Suites, snapshot);
            Mode = snapshot.Mode + (snapshot.MainProfileId != null ? " • основной запущен" : " • внешний / не запущен");
            if (snapshot.MainProfileId != null) Mode += $" • {snapshot.Settings.MainSuite} {snapshot.Settings.MainRegion}";
            if (snapshot.IsTesting) MessageText = SeriesSummary;
            if (IsDashboard) { MessageText = snapshot.Error ?? ""; return; }
            if (snapshot.Suite != SelectedSuite) return;
            SetTargets(snapshot.Targets ?? TestTarget.Defaults);
            var selectedId = selected?.Id;
            foreach (var row in snapshot.Profiles)
            {
                var existing = Profiles.FirstOrDefault(p => p.Id == row.Id);
                if (existing == null) Profiles.Add(new(row, currentTargets)); else existing.Update(row, currentTargets);
            }
            foreach (var old in Profiles.Where(p => snapshot.Profiles.All(r => r.Id != p.Id)).ToArray()) Profiles.Remove(old);
            Selected = Profiles.FirstOrDefault(p => p.Id == selectedId) ?? Profiles.FirstOrDefault();
            Notify(nameof(Details));
            MessageText = snapshot.Error ?? (snapshot.IsTesting ? SeriesSummary : "Готово.");
            if (snapshot.Mode != "Managed" && snapshot.ExternalProfileMatches is { Count: > 0 } matches)
                MessageText += matches.Count == 1
                    ? " Исходная служба: " + snapshot.Profiles.Single(p => p.Id == matches[0]).Name + "."
                    : " Несколько профилей совпадают со службой; выберите основной вручную.";
        }
        if (message.Error != null) MessageText = message.Error;
        if (message.Kind == "Disconnected")
        {
            Mode = "Helper отключён"; isTesting = false;
            // Keep pending cards neutral until a reconnect or disk refresh establishes their outcome.
            RefreshCommands();
        }
    }
    public async ValueTask DisposeAsync()
    {
        if (client.Connected)
        {
            try { await client.SendAsync(settings.KeepMainRunning ? "Cancel" : "StopMain").WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception e) when (e is IOException or TimeoutException or OperationCanceledException) { }
        }
        await client.DisposeAsync();
    }
}
