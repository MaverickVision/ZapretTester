using ZapretTester.Core;

namespace ZapretTester.App;

public sealed class DashboardCardRow(DashboardCard card, bool selected, Action<DashboardCardRow> changed) : Observable
{
    private DashboardCard card = card;
    private bool selected = selected;
    public bool IsPreliminary { get; private set; }
    public System.Windows.Visibility PreliminaryVisibility => IsPreliminary ? System.Windows.Visibility.Visible : System.Windows.Visibility.Collapsed;
    public string Id => card.Id;
    public string Name => card.Name;
    public bool IsSelected { get => selected; set { if (selected == value) return; selected = value; Notify(); changed(this); } }
    public IReadOnlyList<DashboardHalf> Services { get; private set; } = [];
    public DashboardMetrics Metrics => DashboardScoring.Summarize(Services);
    public decimal? AvailabilityScore => IsPreliminary ? null : Metrics.AvailabilityScore;
    public decimal? ResponseScore => IsPreliminary ? null : Metrics.ResponseScore;
    public string AvailabilityText => DashboardScoring.FormatAvailability(Metrics);
    public string ResponseText => DashboardScoring.Format(Metrics.ResponseScore);
    public string AvailabilityColor => DashboardScoring.Color(AvailabilityScore);
    public string ResponseColor => ResponseScore.HasValue ? "#314255" : "#8994A3";
    private string PreliminaryNote => IsPreliminary ? "Предварительно: достоверность серии ещё не подтверждена.\n" : "";
    public string AvailabilityDetails => Name + "\n" + PreliminaryNote + DashboardScoring.Details(Services);
    public string ResponseDetails => Name + "\n" + PreliminaryNote + DashboardScoring.ResponseDetails(Services);
    public string AvailabilityAccessibleName => $"{Name}. Доступность HTTPS-служб: {AvailabilityText}";
    public string ResponseAccessibleName => $"{Name}. Оценка отклика HTTPS: {ResponseText}";
    public double CardWidth => Services.Count switch { 0 or 1 => 188, 2 => 268, _ => 388 };
    public double CardHeight => Services.Count > 3 ? 394 : 245;
    public double ServicesWidth => Math.Min(3, Math.Max(1, Services.Count)) * 120;
    public int ServiceColumns => Math.Min(3, Math.Max(1, Services.Count));
    public double TitleWidth => CardWidth - 58;
    public void Update(DashboardCard value, IReadOnlyCollection<TestSuiteId> visible, bool isPreliminary = false)
    {
        card = value; IsPreliminary = isPreliminary; Services = value.Services.Where(s => visible.Contains(s.Suite)).ToArray(); Notify(null);
    }
}
