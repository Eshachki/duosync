using DuoSync.Core.Setup;

namespace DuoSync.Core.Tests;

public class BridgeInstallerTests : IDisposable
{
    readonly string _root = Path.Combine(Path.GetTempPath(), "duosync-tests", "bridge-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { }
    }

    static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name)).Replace("\r\n", "\n");

    [Fact]
    public void Packages_lock_entry_is_written_exactly_like_unity_does()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Packages"));
        var lockPath = Path.Combine(_root, "Packages", "packages-lock.json");
        File.WriteAllText(lockPath, Fixture("packages-lock.before.json"));

        Assert.True(BridgeInstaller.AddToPackagesLock(_root));

        Assert.Equal(Fixture("packages-lock.unity-after.json"), File.ReadAllText(lockPath));
        Assert.False(BridgeInstaller.AddToPackagesLock(_root)); // idempotent
    }

    [Fact]
    public void Entry_goes_last_with_a_comma_on_the_previous_one()
    {
        Directory.CreateDirectory(Path.Combine(_root, "Packages"));
        var lockPath = Path.Combine(_root, "Packages", "packages-lock.json");
        File.WriteAllText(lockPath, "{\n  \"dependencies\": {\n    \"com.alpha\": {\n      \"version\": \"1.0.0\",\n      \"depth\": 0,\n      \"source\": \"registry\",\n      \"dependencies\": {}\n    }\n  }\n}\n");

        BridgeInstaller.AddToPackagesLock(_root);

        var text = File.ReadAllText(lockPath);
        Assert.Contains("    },\n    \"com.duosync.bridge\": {", text);
        Assert.EndsWith("      \"dependencies\": {}\n    }\n  }\n}\n", text);
    }

    [Fact]
    public void Embedded_bridge_contains_every_file_including_script_metas()
    {
        var files = BridgeInstaller.EmbeddedFiles();
        Assert.Contains("package.json", files);
        Assert.Contains("package.json.meta", files);
        Assert.Contains("Editor.meta", files);
        Assert.Contains("Editor/DuoSyncBridge.cs", files);
        Assert.Contains("Editor/DuoSyncBridge.cs.meta", files); // "X.cs.meta" must not be taken for a Czech satellite resource
        Assert.All(files.Where(f => !f.EndsWith(".meta")), f => Assert.Contains(f + ".meta", files));
    }
}
