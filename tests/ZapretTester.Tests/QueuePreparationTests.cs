using ZapretTester.Core;
using Xunit;

namespace ZapretTester.Tests;

public sealed class QueuePreparationTests
{
    [Fact]
    public void EightWorkersAreAllowedButDefaultAndSavedFiveArePreserved()
    {
        Assert.Equal(4, new ApplicationSettings().MaxParallelTests);
        (new ApplicationSettings { MaxParallelTests = 5 }).Validate();
        (new ApplicationSettings { MaxParallelTests = 8 }).Validate();
        Assert.Throws<ArgumentException>(() => (new ApplicationSettings { MaxParallelTests = 9 }).Validate());
    }

    [Fact]
    public void ReservationFreezesSelectionRegionAndSettingsBeforePreparation()
    {
        var ids = new[] { "A", "B" };
        var suites = new[] { new SuiteSelection(TestSuiteId.Discord), new SuiteSelection(TestSuiteId.LeagueOfLegends) };
        var settings = new ApplicationSettings { LeagueRegion = "RU", MaxParallelTests = 5 };
        var request = SeriesRequest.Capture(ids, suites, ids, settings);
        ids[0] = "OTHER"; suites[0] = new(TestSuiteId.YouTube);
        settings = settings with { LeagueRegion = "EUW", MaxParallelTests = 8 };
        Assert.Equal(new[] { "A", "B" }, request.ProfileIds);
        Assert.Equal(TestSuiteId.Discord, request.Selections[0].Suite);
        Assert.Equal("RU", request.Selections[1].Region);
        Assert.Equal(5, request.Settings.MaxParallelTests);
        Assert.Equal(4, request.Participants.Count);
        Assert.Throws<ArgumentException>(() => SeriesRequest.Capture(["missing"], suites, ["A"], settings));
        Assert.Throws<ArgumentException>(() => SeriesRequest.Capture(["A", "A"], suites, ["A"], settings));
    }

    [Fact]
    public async Task SharedSnapshotHasIndependentFiltersAndLivesUntilWholeSeriesFinishes()
    {
        var root = Path.Combine(Path.GetTempPath(), "ZapretTester-queue-" + Guid.NewGuid().ToString("N"));
        var workspace = new SeriesWorkspace(root);
        var profile = new ZapretProfile("A", "A", "A.bat", "winws.exe", "bin", [], "hash");
        var creations = 0;
        SnapshotProfile Factory(ZapretProfile p, string path, CancellationToken token)
        {
            Interlocked.Increment(ref creations);
            Directory.CreateDirectory(path);
            File.WriteAllText(Path.Combine(path, "resource.txt"), "preserved");
            return new(p, "winws.exe", path, [], "frozen");
        }
        var snapshots = await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
            workspace.Snapshot(profile, Factory, CancellationToken.None))));
        Assert.Equal(1, creations);
        Assert.All(snapshots, s => Assert.Same(snapshots[0], s));
        var jobs = Enumerable.Range(0, 2).Select(_ => workspace.CreateJobDirectory()).ToArray();
        Assert.NotEqual(jobs[0], jobs[1]);
        File.WriteAllText(Path.Combine(jobs[0], "test.filter"), "port A");
        File.WriteAllText(Path.Combine(jobs[1], "test.filter"), "port B");
        Assert.Equal("port A", File.ReadAllText(Path.Combine(jobs[0], "test.filter")));
        Assert.Equal("preserved", File.ReadAllText(Path.Combine(snapshots[0].WorkingDirectory, "resource.txt")));
        workspace.Dispose();
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void CancelledPreparationDoesNotCreateASnapshotAndStillCleansUp()
    {
        var root = Path.Combine(Path.GetTempPath(), "ZapretTester-queue-" + Guid.NewGuid().ToString("N"));
        var workspace = new SeriesWorkspace(root);
        var profile = new ZapretProfile("A", "A", "A.bat", "winws.exe", "bin", [], "hash");
        Assert.Throws<OperationCanceledException>(() => workspace.Snapshot(profile,
            (_, _, _) => throw new InvalidOperationException("Factory must not run"), new CancellationToken(true)));
        workspace.Dispose();
        Assert.False(Directory.Exists(root));
    }

    [Fact]
    public void FailedSnapshotPreparationCleansPartialFilesWithoutDeletingExistingSource()
    {
        var parent = Path.Combine(Path.GetTempPath(), "ZapretTester-queue-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(parent);
        var untouched = Path.Combine(parent, "source.txt"); File.WriteAllText(untouched, "original");
        var root = Path.Combine(parent, "series");
        using (var workspace = new SeriesWorkspace(root))
        {
            var profile = new ZapretProfile("A", "A", "A.bat", "winws.exe", "bin", [], "hash");
            Assert.Throws<IOException>(() => workspace.Snapshot(profile, (_, destination, _) =>
            {
                Directory.CreateDirectory(destination);
                File.WriteAllText(Path.Combine(destination, "partial.txt"), "partial");
                throw new IOException("Interrupted copy");
            }, CancellationToken.None));
        }
        Assert.False(Directory.Exists(root));
        Assert.Equal("original", File.ReadAllText(untouched));
        Assert.Throws<IOException>(() => new SeriesWorkspace(parent));
        File.Delete(untouched); Directory.Delete(parent);
    }
}
