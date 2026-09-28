using ZapretTester.Core;

namespace ZapretTester.App;

public sealed class DashboardCardRow(DashboardCard card, bool selected, Action<DashboardCardRow> changed) : Observable
{
    private DashboardCard card = card;
    private bool selected = selected;
    public string Id => card.Id;
    public string Name => card.Name;
    public bool IsSelected { get => selected; set { if (selected == value) return; selected = value; Notify(); changed(this); } }
    public IReadOnlyList<DashboardHalf> Services { get; private set; } = [];
    public decimal? Score => DashboardScoring.Average(Services);
    public string ScoreText => DashboardScoring.Format(Score);
    public string ScoreColor => DashboardScoring.Color(Score);
    public string ScoreDetails => Name + "\n" + DashboardScoring.Details(Services);
    public string ScoreAccessibleName => $"{Name}. Доступность: {ScoreText}";
    public double CardWidth => Math.Max(188, 28 + 120 * Services.Count);
    public double TitleWidth => CardWidth - 116;
    public void Update(DashboardCard value, IReadOnlyCollection<TestSuiteId> visible)
    {
        card = value; Services = value.Services.Where(s => visible.Contains(s.Suite)).ToArray(); Notify(null);
    }
}
