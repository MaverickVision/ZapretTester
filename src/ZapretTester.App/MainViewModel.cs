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
        running = true; Refresh();
        try { await action(); } finally { running = false; Refresh(); }
    }
}
public sealed record EndpointDetail(string Name, string Address, string Status, string Measurements, string Error);
public sealed record ServiceDetail(string Name, string Status, string Limitation, IReadOnlyList<EndpointDetail> Endpoints);

public sealed class MainViewModel : Observable, IAsyncDisposable
{
    private readonly string data = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ZapretTester");
    private HelperClient client;
    private ApplicationSettings settings = new();
    private DashboardPreferences preferences = new();
    private IReadOnlyList<SuiteView> suiteViews = [];
    private HelperState? state;
    private string folder = "", message = "Выберите папку Flowseal, сервисы и стратегии.", mode = "Не подключён";
    private bool isTesting, startingTest, bulkSelection, forceNextViewOrder;
    private int viewGeneration;
    private DashboardCardRow? selected;
    public ObservableCollection<DashboardCardRow> DashboardCards { get; } = [];
    public ListCollectionView DashboardCardsView { get; }
    public event Action? DashboardUpdating;
    public event Action? DashboardUpdated;
    public string Folder { get => folder; private set { folder = value; Notify(); } }
    private UserFacingMessage userMessage = new("");
    public string MessageText
    {
        get => message;
        private set
        {
            userMessage = UserFacingMessage.Create(value); message = userMessage.Text; Notify();
            Notify(nameof(RecoveryVisibility)); Notify(nameof(MessageFooterVisibility)); Notify(nameof(MessageTechnicalDetails));
        }
    }
    public Visibility RecoveryVisibility => userMessage.OfferPrepareFlowseal ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MessageFooterVisibility => userMessage.OfferPrepareFlowseal ? Visibility.Collapsed : Visibility.Visible;
    public string? MessageTechnicalDetails => userMessage.TechnicalDetails;
    public string Mode { get => mode; private set { mode = value; Notify(); } }
    public bool ShowDiscord { get => Visible(TestSuiteId.Discord); set => SetVisible(TestSuiteId.Discord, value); }
    public bool ShowLeague { get => Visible(TestSuiteId.LeagueOfLegends); set => SetVisible(TestSuiteId.LeagueOfLegends, value); }
    public bool ShowYouTube { get => Visible(TestSuiteId.YouTube); set => SetVisible(TestSuiteId.YouTube, value); }
    public bool ShowSteam { get => Visible(TestSuiteId.Steam); set => SetVisible(TestSuiteId.Steam, value); }
    public bool ShowTwitch { get => Visible(TestSuiteId.Twitch); set => SetVisible(TestSuiteId.Twitch, value); }
    public bool ShowCloudflare { get => Visible(TestSuiteId.Cloudflare); set => SetVisible(TestSuiteId.Cloudflare, value); }
    public bool HasVisibleServices => preferences.VisibleSuites.Length > 0;
    public bool IsWorking => isTesting || startingTest;
    public double MinimumCardWidth => preferences.VisibleSuites.Length switch { 0 or 1 => 188, 2 => 268, _ => 388 };
    public Visibility CardsVisibility => HasVisibleServices ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EmptyServicesVisibility => HasVisibleServices ? Visibility.Collapsed : Visibility.Visible;
    public Visibility DetailsVisibility => selected == null ? Visibility.Collapsed : Visibility.Visible;
    public double DetailsWidth => selected == null ? 0 : 360;
    public string DetailTitle => selected?.Name ?? "Стратегия";
    public string DetailSelection => DescribeVariant(CurrentVariant);
    public IReadOnlyList<ServiceDetail> DetailServices { get; private set; } = [];
    public string SelectionSummary => $"{DashboardCards.Count(c => c.IsSelected)}/{DashboardCards.Count} стратегий";
    public string MainSummary => state?.MainProfileId is { } id
        ? $"Основной: {DashboardCards.FirstOrDefault(c => c.Id == id)?.Name ?? id} • {DescribeVariant(state.MainVariant)}{(state.MainVariantVerified ? "" : " • не проверен") }"
        : settings.ManagedEnabled && settings.MainProfileId is { } saved
            ? $"Сохранён основной: {saved} • {DescribeVariant(settings.MainVariant)} • состояние Helper не проверено"
            : "Основной: внешний Flowseal или не запущен";
    public string SeriesSummary => state?.Series is not { } series ? "" : isTesting
        ? series.Phase switch {
            SeriesPhase.Preparing => $"Подготовка • {series.Total} проверок сервисов",
            SeriesPhase.Finalizing => $"{series.Completed}/{series.Total} • подтверждение и сохранение",
            _ => $"{series.Completed}/{series.Total} проверок сервисов • выполняется" }
        : $"{series.Completed}/{series.Total} проверок сервисов • {series.ElapsedSeconds:F1} с";
    public string ActiveVariantSummary => isTesting ? "Текущая серия: " + DescribeVariant(state?.ActiveVariant) : "";
    private VariantDescriptor? CurrentVariant => HasVisibleServices ? VariantDescriptor.Create(preferences.VisibleSuites.Select(s => new SuiteSelection(s,
        s == TestSuiteId.LeagueOfLegends ? settings.LeagueRegion : null)), settings.LeagueRegion) : null;
    private static string DescribeVariant(VariantDescriptor? variant) => variant == null ? "исходный вариант"
        : string.Join(" + ", variant.Suites.Select(s => ServiceCatalog.Get(s).ShortName + (s == TestSuiteId.LeagueOfLegends ? " (" + variant.Region + ")" : "")));
    public int RequiredPorts => DashboardCards.Count(c => c.IsSelected) * preferences.VisibleSuites.Length * 4 * settings.Attempts;
    public int AllRequiredPorts => DashboardCards.Count * preferences.VisibleSuites.Length * 4 * settings.Attempts;
    private int AvailablePorts => settings.LastPort - settings.FirstPort + 1;
    public bool PortBudgetFits => RequiredPorts <= AvailablePorts;
    public bool AllPortBudgetFits => AllRequiredPorts <= AvailablePorts;
    public Visibility PortBudgetVisibility => PortBudgetFits && AllPortBudgetFits ? Visibility.Collapsed : Visibility.Visible;
    public string PortBudgetMessage => !PortBudgetFits
        ? $"Для выбранной серии нужно {RequiredPorts} уникальных портов, в пуле {AvailablePorts}."
        : $"Для проверки всех стратегий нужно {AllRequiredPorts} уникальных портов, в пуле {AvailablePorts}. Выбранные стратегии можно проверить отдельно.";
    public string MainEligibility => CanApplyMain ? "Можно применить этот проверенный общий вариант. Ошибки отдельных служб сохраняются в результатах."
        : "Для применения завершите проверку выбранного набора сервисов. Нужны актуальные достоверные результаты одной серии; доступность всех служб не обязательна.";
    private bool CanApplyMain
    {
        get
        {
            if (isTesting || startingTest || selected == null || CurrentVariant is not { } variant || suiteViews.Count != variant.Suites.Count) return false;
            var rows = suiteViews.Select(s => s.Profiles.FirstOrDefault(p => p.Id == selected.Id)).ToArray();
            if (rows.Any(p => p == null || p.Error != null || p.IsStale || p.RunState is RunState.Queued or RunState.Testing or RunState.Cancelled or RunState.Blocked
                || p.LastResult is not { IsReliable: true, PendingValidation: false, Error: null } r
                || r.VariantId != variant.Id || string.IsNullOrEmpty(r.SeriesId) || r.ExpectedEndpoints != 4 || r.Endpoints.Count != 4)) return false;
            return rows.Select(p => p!.LastResult!.SeriesId).Distinct().Count() == 1;
        }
    }
    public int DashboardSortIndex
    {
        get => (int)preferences.SortMode;
        set
        {
            if (!Enum.IsDefined((DashboardSortMode)value) || value == DashboardSortIndex) return;
            preferences.SortMode = (DashboardSortMode)value; SavePreferences(); Notify();
            DashboardUpdating?.Invoke();
            try { RefreshDashboardOrder(true); } finally { DashboardUpdated?.Invoke(); }
        }
    }
    public ICommand ChooseFolderCommand { get; }
    public ICommand TestBatchCommand { get; }
    public ICommand TestAllCommand { get; }
    public ICommand SelectAllCardsCommand { get; }
    public ICommand ClearCardsCommand { get; }
    public ICommand TestOneCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand MainCommand { get; }
    public ICommand ResumeMainCommand { get; }
    public ICommand PrepareCommand { get; }
    public ICommand RestoreCommand { get; }
    public ICommand StopMainCommand { get; }
    public ICommand SettingsCommand { get; }
    public ICommand DiagnosticsCommand { get; }
    public ICommand LogsCommand { get; }
    public ICommand CloseDetailsCommand { get; }
    public ICommand ExpandPoolCommand { get; }
    public MainViewModel()
    {
        DashboardCardsView = new ListCollectionView(DashboardCards); client = NewClient();
        TestBatchCommand = Command(() => TestAsync(DashboardCards.Where(c => c.IsSelected).Select(c => c.Id).ToArray()),
            () => !isTesting && !startingTest && HasVisibleServices && PortBudgetFits && DashboardCards.Any(c => c.IsSelected));
        TestAllCommand = Command(() => TestAsync(DashboardCards.Select(c => c.Id).ToArray()),
            () => !isTesting && !startingTest && HasVisibleServices && AllPortBudgetFits && DashboardCards.Count > 0);
        TestOneCommand = Command(() => TestAsync(selected == null ? [] : [selected.Id]), () => !isTesting && !startingTest && selected != null && HasVisibleServices);
        SelectAllCardsCommand = Command(() => SelectCards(true)); ClearCardsCommand = Command(() => SelectCards(false));
        ChooseFolderCommand = Command(ChooseFolderAsync, () => !isTesting && !startingTest);
        CancelCommand = Command(() => client.Connected ? client.SendAsync("Cancel") : Task.CompletedTask, () => isTesting);
        RefreshCommand = Command(RefreshAsync, () => !isTesting); MainCommand = Command(MakeMainAsync, () => CanApplyMain);
        ResumeMainCommand = Command(ResumeMainAsync, () => !isTesting && settings.MainProfileId != null
            && (!client.Connected || state is not { Mode: "Managed", MainProfileId: not null }));
        PrepareCommand = Command(PrepareFlowsealAsync, () => !isTesting && !startingTest && DashboardCards.Count > 0);
        ExpandPoolCommand = Command(ExpandPoolAsync, () => !isTesting && (!PortBudgetFits || !AllPortBudgetFits));
        StopMainCommand = Command(async () => {
            if (MessageBox.Show("Остановить основной Zapret? Исходная служба останется отключённой до восстановления.", "Остановить Zapret", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) await SendAsync("StopMain");
        }, () => !isTesting);
        RestoreCommand = Command(async () => {
            if (MessageBox.Show("Вернуть исходную службу Flowseal и её автозапуск? Возможен краткий разрыв соединений.", "Восстановить Flowseal", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) await SendAsync("RestoreExternal");
        }, () => !isTesting);
        SettingsCommand = Command(EditSettingsAsync);
        DiagnosticsCommand = Command(async () => {
            await EnsureConnectedAsync(); await client.SendAsync("State"); MessageBox.Show(state?.Diagnostics ?? "Диагностика загружается.", "Диагностика");
        });
        LogsCommand = Command(() => {
            var path = Path.Combine(data, "logs"); Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = true, ArgumentList = { path } }); return Task.CompletedTask;
        });
        CloseDetailsCommand = Command(() => { selected = null; UpdateDetails(); return Task.CompletedTask; });
    }
    public void OpenDetails(DashboardCardRow row) { selected = row; UpdateDetails(); }
    private void UpdateDetails()
    {
        DetailServices = selected == null ? [] : suiteViews.Where(s => Visible(s.Suite)).OrderBy(s => s.Suite).Select(s => {
            var row = s.Profiles.FirstOrDefault(p => p.Id == selected.Id);
            var half = selected.Services.FirstOrDefault(h => h.Suite == s.Suite); var result = row?.LastResult;
            return new ServiceDetail(ServiceCatalog.Get(s.Suite).Name + (s.Suite == TestSuiteId.LeagueOfLegends ? " • " + s.Region : ""),
                half?.Status.Description ?? "Ещё не проверено", ServiceCatalog.Get(s.Suite).Limitation,
                s.Targets.Select(target => {
                    var endpoint = result?.Endpoints.FirstOrDefault(e => e.Name == target.Name);
                    return new EndpointDetail(target.Name, target.Method + " " + target.Url.AbsoluteUri,
                        endpoint == null ? "Ещё не проверено" : $"{EndpointStatusText(endpoint.Status)} • HTTP {endpoint.HttpCode?.ToString() ?? "—"}",
                        endpoint == null ? "" : $"HTTPS {endpoint.Duration.TotalMilliseconds:F0} мс • DNS {endpoint.DnsDuration.TotalMilliseconds:F0} мс\nIP {endpoint.RemoteIp ?? "—"} • порт {endpoint.LocalPort}\n{result!.TestedAt.LocalDateTime:g}",
                        row?.Error ?? row?.RunError ?? endpoint?.Error ?? result?.Error ?? "");
                }).ToArray());
        }).ToArray();
        foreach (var name in new[] { nameof(DetailsVisibility), nameof(DetailsWidth), nameof(DetailTitle), nameof(DetailSelection), nameof(DetailServices), nameof(MainEligibility) }) Notify(name);
        RefreshCommands();
    }
    private static string EndpointStatusText(EndpointStatus status) => status switch
    {
        EndpointStatus.Available => "Доступно", EndpointStatus.ServerWarning => "Предупреждение сервера", _ => "Ошибка проверки"
    };
    private bool Visible(TestSuiteId suite) => preferences.VisibleSuites.Contains(suite);
    private void SetVisible(TestSuiteId suite, bool value)
    {
        if (Visible(suite) == value) return;
        preferences.VisibleSuites = value ? preferences.VisibleSuites.Append(suite).Distinct().Order().ToArray() : preferences.VisibleSuites.Where(s => s != suite).ToArray();
        forceNextViewOrder = true;
        SavePreferences(); NotifySelections();
        // Every service set is a different effective process. Do not recycle badges from another variant.
        ApplySuites(suiteViews.Select(s => s with { Profiles = s.Profiles.Select(p => p with { LastResult = null, RunState = RunState.Idle }).ToArray() }).ToArray(), true);
        _ = RefreshViewSafelyAsync();
    }
    private void NotifySelections()
    {
        Notify(nameof(MinimumCardWidth));
        foreach (var name in new[] { nameof(ShowDiscord), nameof(ShowLeague), nameof(ShowYouTube), nameof(ShowSteam), nameof(ShowTwitch), nameof(ShowCloudflare), nameof(HasVisibleServices), nameof(CardsVisibility), nameof(EmptyServicesVisibility), nameof(SelectionSummary), nameof(PortBudgetVisibility), nameof(PortBudgetMessage) }) Notify(name);
        RefreshCommands();
    }
    private async Task RefreshViewSafelyAsync() { try { await RefreshViewAsync(); } catch (Exception e) { MessageText = e.Message; } }
    private void SavePreferences()
    {
        try { JsonFiles.Save(Path.Combine(data, "dashboard.json"), preferences); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { MessageText = e.Message; }
    }
    private void SelectionChanged(DashboardCardRow row)
    {
        preferences.Select(Folder, row.Id, row.IsSelected); if (!bulkSelection) SavePreferences(); NotifySelections();
    }
    private Task SelectCards(bool value)
    {
        bulkSelection = true; try { foreach (var row in DashboardCards) row.IsSelected = value; } finally { bulkSelection = false; }
        SavePreferences(); return Task.CompletedTask;
    }
    private void ApplySuites(IReadOnlyList<SuiteView> suites, bool forceOrder = false)
    {
        DashboardUpdating?.Invoke();
        try
        {
            suiteViews = suites;
            var updates = LiveDashboardProjection.Project(suites, state?.IsTesting == true ? state.Series : null, state?.Error == null);
            var cards = updates.Select(u => u.Card).ToArray();
            var ids = cards.Select(c => c.Id).ToHashSet();
            foreach (var removed in DashboardCards.Where(c => !ids.Contains(c.Id)).ToArray()) DashboardCards.Remove(removed);
            for (var i = 0; i < cards.Length; i++)
            {
                var card = cards[i]; var row = DashboardCards.FirstOrDefault(c => c.Id == card.Id);
                if (row == null) { row = new(card, preferences.IsSelected(Folder, card.Id), SelectionChanged); DashboardCards.Insert(i, row); }
                else if (DashboardCards.IndexOf(row) != i) DashboardCards.Move(DashboardCards.IndexOf(row), i);
                row.Update(card, preferences.VisibleSuites, updates[i].IsPreliminary); row.IsSelected = preferences.IsSelected(Folder, card.Id);
            }
            if (selected != null && !ids.Contains(selected.Id)) selected = null;
            RefreshDashboardOrder(forceOrder); UpdateDetails(); NotifySelections();
            Notify(nameof(SeriesSummary)); Notify(nameof(ActiveVariantSummary)); Notify(nameof(MainSummary));
        }
        finally { DashboardUpdated?.Invoke(); }
    }
    private void RefreshDashboardOrder(bool force = false)
    {
        if (isTesting && !force) return;
        var ordered = preferences.SortMode == DashboardSortMode.BestFirst
            ? DashboardCards.OrderByDescending(c => c.AvailabilityScore.HasValue).ThenByDescending(c => c.AvailabilityScore).ThenByDescending(c => c.ResponseScore.HasValue).ThenByDescending(c => c.ResponseScore).ToArray()
            : DashboardCards.ToArray();
        if (DashboardCardsView.Cast<DashboardCardRow>().Select(c => c.Id).SequenceEqual(ordered.Select(c => c.Id))) return;
        var ranks = ordered.Select((c, i) => (c.Id, Index: i)).ToDictionary(p => p.Id, p => p.Index);
        DashboardCardsView.CustomSort = Comparer<DashboardCardRow>.Create((a, b) => ranks.GetValueOrDefault(a.Id, int.MaxValue).CompareTo(ranks.GetValueOrDefault(b.Id, int.MaxValue)));
    }
    private void RefreshCommands()
    {
        Notify(nameof(IsWorking));
        foreach (var command in new[] { TestBatchCommand, TestAllCommand, TestOneCommand, RefreshCommand, CancelCommand, MainCommand, ResumeMainCommand, PrepareCommand, ExpandPoolCommand, ChooseFolderCommand, StopMainCommand, RestoreCommand }) (command as AsyncCommand)?.Refresh();
    }
    private AsyncCommand Command(Func<Task> action, Func<bool>? enabled = null) => new(async () => {
        try { await action(); } catch (Exception e) { MessageText = e is Win32Exception { NativeErrorCode: 1223 } ? "Повышение прав отменено." : e.Message; }
    }, enabled);
    private HelperClient NewClient()
    {
        var value = new HelperClient(); value.Message += m => Application.Current.Dispatcher.InvokeAsync(() => { if (ReferenceEquals(value, client)) Apply(m); }); return value;
    }
    public async Task InitializeAsync()
    {
        try
        {
            var path = Path.Combine(data, "settings.json");
            if (File.Exists(path)) settings = JsonSerializer.Deserialize<ApplicationSettings>(File.ReadAllText(path), JsonFiles.Options) ?? new();
            var prefPath = Path.Combine(data, "dashboard.json"); var leagueAvailable = false;
            try
            {
                var league = LeagueConfiguration.Load(settings.LeagueFolder, settings.LeagueRegion);
                settings = settings with { LeagueFolder = league.Folder, LeagueRegion = league.SelectedRegion };
                leagueAvailable = true;
            }
            catch (Exception e) when (e is IOException or ArgumentException or UnauthorizedAccessException) { }
            try
            {
                preferences = File.Exists(prefPath) ? JsonSerializer.Deserialize<DashboardPreferences>(File.ReadAllText(prefPath), JsonFiles.Options) ?? new() : DashboardPreferences.New(leagueAvailable);
                preferences.VisibleSuites = preferences.VisibleSuites.Where(s => Enum.IsDefined(s)).Distinct().Order().ToArray();
                if (!Enum.IsDefined(preferences.SortMode)) preferences.SortMode = DashboardSortMode.BestFirst;
            }
            catch (Exception e) when (e is IOException or JsonException) { preferences = new(); }
            Folder = settings.FlowsealFolder; NotifySelections(); Notify(nameof(DashboardSortIndex)); Notify(nameof(MainSummary));
            if (!Directory.Exists(Folder)) return;
            await RefreshAsync();
            if (settings.AutoTest && !Environment.GetCommandLineArgs().Contains("--render-check") && TestBatchCommand.CanExecute(null)) TestBatchCommand.Execute(null);
        }
        catch (Exception e) { MessageText = e.Message; }
    }
    private async Task ChooseFolderAsync()
    {
        if (state?.Mode == "Managed" || settings.ManagedEnabled) throw new InvalidOperationException("Сначала восстановите исходный Flowseal перед сменой каталога.");
        var dialog = new OpenFolderDialog { Title = "Папка zapret-discord-youtube" }; if (dialog.ShowDialog() != true) return;
        viewGeneration++; if (client.Connected) await client.SendAsync("Shutdown");
        await client.DisposeAsync(); client = NewClient(); state = null; isTesting = false;
        Folder = dialog.FolderName; Mode = "Не подключён"; selected = null;
        settings = settings with { FlowsealFolder = Folder, ManagedEnabled = false, MainProfileId = null };
        JsonFiles.Save(Path.Combine(data, "settings.json"), settings); await RefreshAsync();
    }
    private async Task EnsureConnectedAsync()
    {
        if (!Directory.Exists(Folder)) throw new InvalidOperationException("Укажите существующую папку Flowseal.");
        if (!client.Connected)
        {
            MessageText = "Подключение Helper… Подтвердите UAC при запросе Windows.";
            await client.ConnectAsync(Folder);
            // The live state now owns publication, even if this connection later closes before an older disk load finishes.
            viewGeneration++;
            if (CurrentVariant is { } variant) await client.SendAsync("SelectVariant", variant: variant);
        }
    }
    private async Task SendAsync(string command) { await EnsureConnectedAsync(); await client.SendAsync(command); }
    private async Task TestAsync(IReadOnlyList<string> ids)
    {
        var variant = CurrentVariant;
        if (ids.Count == 0 || variant == null) throw new InvalidOperationException("Выберите сервисы и хотя бы одну стратегию.");
        if (ids.Count * variant.Suites.Count * 4 * settings.Attempts > AvailablePorts) throw new InvalidOperationException("Недостаточно портов. Расширьте диапазон в настройках или уменьшите выбор.");
        if (startingTest) return;
        startingTest = true; RefreshCommands();
        try
        {
            await EnsureConnectedAsync();
            if (CurrentVariant is { } viewVariant) await client.SendAsync("SelectVariant", variant: viewVariant);
            await client.SendAsync("TestBatch", profileIds: ids, testSuites: variant.Selections, variant: variant);
        }
        finally { startingTest = false; RefreshCommands(); }
    }
    private async Task MakeMainAsync()
    {
        if (!CanApplyMain || selected == null || CurrentVariant is not { } variant) throw new InvalidOperationException(MainEligibility);
        var id = selected.Id; var name = selected.Name;
        if (MessageBox.Show($"Сделать «{name}» основным?\n{DescribeVariant(variant)}\n\nБудет запущен именно проверенный общий вариант. Возможен краткий разрыв соединений. Исходная служба Flowseal будет остановлена, её автозапуск отключён.", "Основная стратегия", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await EnsureConnectedAsync(); await client.SendAsync("StartManaged", id, variant: variant);
    }
    private async Task ResumeMainAsync()
    {
        if (settings.MainProfileId == null) throw new InvalidOperationException("Сохранённая основная стратегия отсутствует.");
        await EnsureConnectedAsync();
        await client.SendAsync("State");
        // Starting Helper may already have resumed its saved main. Check before asking it to resume again.
        // No view variant is sent: the saved main is independent of the dashboard checkboxes.
        if (state is not { Mode: "Managed", MainProfileId: not null }) await client.SendAsync("ResumeManaged");
    }
    private async Task PrepareFlowsealAsync()
    {
        await EnsureConnectedAsync(); await client.SendAsync("State"); var matches = state?.ExternalProfileMatches ?? [];
        var id = matches.Count == 1 ? matches[0] : selected?.Id;
        if (id == null)
        {
            var choice = new PrepareProfileWindow(DashboardCards) { Owner = Application.Current.MainWindow };
            if (choice.ShowDialog() != true) return;
            id = choice.ProfileId!;
        }
        var name = DashboardCards.FirstOrDefault(c => c.Id == id)?.Name ?? id;
        if (MessageBox.Show($"Подготовить Flowseal к проверке с исходным профилем «{name}»?\n\nСлужба Flowseal будет остановлена и отключена. Приложение запустит исходный профиль с исключением тестовых портов. Этот вариант ещё не проверен. Возможен краткий разрыв соединений.", "Подготовка Flowseal", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await client.SendAsync("PrepareFlowseal", id);
    }
    private async Task ExpandPoolAsync()
    {
        var required = Math.Max(RequiredPorts, AllRequiredPorts);
        var desiredSize = Math.Max(2000, (int)Math.Ceiling(required / 1000d) * 1000);
        var last = Math.Min(65535, settings.FirstPort + desiredSize - 1);
        if (last - settings.FirstPort + 1 < required) throw new InvalidOperationException("Недостаточно места в диапазоне. Измените начало пула в настройках.");
        if ((state?.MainProfileId != null || settings.ManagedEnabled) && MessageBox.Show($"Расширить пул до {settings.FirstPort}–{last} и перезапустить основной Zapret? При ошибке будет восстановлена прежняя конфигурация.", "Тестовый пул", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        var updated = settings with { LastPort = last };
        if (client.Connected || settings.ManagedEnabled) { await EnsureConnectedAsync(); await client.SendAsync("ExpandPortPool", settings: updated); }
        else { settings = updated; JsonFiles.Save(Path.Combine(data, "settings.json"), settings); NotifySelections(); }
    }
    private async Task RefreshAsync()
    {
        if (client.Connected) await client.SendAsync("Refresh"); await RefreshViewAsync();
    }
    private async Task RefreshViewAsync()
    {
        var generation = ++viewGeneration; var variant = CurrentVariant;
        if (variant == null) { UpdateDetails(); return; }
        if (client.Connected) { await client.SendAsync("SelectVariant", variant: variant); return; }
        if (!Directory.Exists(Folder)) { DashboardCards.Clear(); suiteViews = []; selected = null; UpdateDetails(); return; }
        var capturedFolder = Folder; var capturedSettings = settings;
        IReadOnlyList<SuiteView> suites;
        try { suites = await Task.Run(() => SuiteProjection.Load(capturedFolder, capturedSettings, Path.Combine(data, "results.json"), variant)); }
        catch
        {
            if (client.Connected || generation != viewGeneration || capturedFolder != Folder || variant.Id != CurrentVariant?.Id) return;
            // A failed refresh cannot leave a seemingly current green summary on screen.
            ApplySuites(suiteViews.Select(s => s with { Profiles = s.Profiles.Select(p => p with { Error = "Не удалось перечитать конфигурацию или результаты." }).ToArray() }).ToArray());
            throw;
        }
        if (client.Connected || generation != viewGeneration || capturedFolder != Folder || variant.Id != CurrentVariant?.Id) return;
        forceNextViewOrder = false; ApplySuites(suites, true);
        MessageText = suites.FirstOrDefault(s => s.Error != null)?.Error ?? "Сохранённые результаты. Просмотр не запускает сетевых проверок.";
    }
    private async Task EditSettingsAsync()
    {
        var dialog = new SettingsWindow(settings) { Owner = Application.Current.MainWindow }; if (dialog.ShowDialog() != true) return;
        var updated = dialog.Value with { FlowsealFolder = Folder }; var poolChanged = updated.FirstPort != settings.FirstPort || updated.LastPort != settings.LastPort;
        if (poolChanged && (state?.MainProfileId != null || settings.ManagedEnabled)
            && MessageBox.Show("Изменение диапазона требует перезапуска основного Zapret. Применить с откатом при ошибке?", "Тестовый пул", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!client.Connected && poolChanged && settings.ManagedEnabled) await EnsureConnectedAsync();
        if (client.Connected)
        {
            if (poolChanged) await client.SendAsync("ExpandPortPool", settings: updated);
            await client.SendAsync("Settings", settings: updated); settings = updated; await RefreshViewAsync();
        }
        else { settings = updated; JsonFiles.Save(Path.Combine(data, "settings.json"), settings); await RefreshViewAsync(); }
        NotifySelections();
    }
    private void Apply(HelperMessage value)
    {
        if (value.State is { } snapshot)
        {
            if (!snapshot.FlowsealFolder.Equals(Folder, StringComparison.OrdinalIgnoreCase)) return;
            state = snapshot; settings = snapshot.Settings; isTesting = snapshot.IsTesting;
            Mode = snapshot.Mode + (snapshot.MainProfileId != null ? " • основной запущен" : " • внешний / не запущен");
            if (snapshot.Suites != null && snapshot.ViewVariant?.Id == CurrentVariant?.Id)
            {
                var forceOrder = forceNextViewOrder; forceNextViewOrder = false;
                ApplySuites(snapshot.Suites, forceOrder);
            }
            else { UpdateDetails(); NotifySelections(); }
            Notify(nameof(SeriesSummary)); Notify(nameof(ActiveVariantSummary)); Notify(nameof(MainSummary));
            MessageText = snapshot.Error ?? (isTesting ? SeriesSummary : "Готово.");
        }
        if (value.Error != null) MessageText = value.Error;
        if (value.Kind == "Disconnected") { Mode = "Helper отключён"; isTesting = false; RefreshCommands(); Notify(nameof(MainSummary)); }
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
