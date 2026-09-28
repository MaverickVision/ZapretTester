namespace ZapretTester.Core;

public enum DashboardSortMode { BestFirst, Name }

public sealed class DashboardPreferences
{
    public DashboardSortMode SortMode { get; set; } = DashboardSortMode.BestFirst;
    public TestSuiteId[] VisibleSuites { get; set; } = Enum.GetValues<TestSuiteId>();
    public Dictionary<string, HashSet<string>> ExcludedProfiles { get; set; } = [];
    private static string FolderKey(string folder) => Path.GetFullPath(folder).ToUpperInvariant();
    public bool IsSelected(string folder, string id) => !ExcludedProfiles.TryGetValue(FolderKey(folder), out var excluded) || !excluded.Contains(id);
    public void Select(string folder, string id, bool selected)
    {
        var key = FolderKey(folder);
        if (!ExcludedProfiles.TryGetValue(key, out var excluded)) ExcludedProfiles[key] = excluded = [];
        if (selected) excluded.Remove(id); else excluded.Add(id);
    }
}
