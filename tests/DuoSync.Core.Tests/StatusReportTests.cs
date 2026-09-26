using DuoSync.Core.Ops;
using static DuoSync.Core.Tests.Harness.Sandbox;

namespace DuoSync.Core.Tests;

/// <summary>§6а: each side publishes its own status report to a service branch, the other side only reads it.</summary>
public class StatusReportTests
{
    [Fact]
    public async Task Friends_report_reaches_the_integrator_and_main_stays_untouched()
    {
        await using var sb = await CreateAsync();
        sb.Owner.Options.CanResolve = true;
        var mainBefore = await sb.RemoteHeadAsync();

        var failed = OpResult.Blocked("Git сейчас занят другой программой. Повтори через минуту.");
        var report = await StatusReports.BuildAsync(sb.Friend.Repo, "Боря", "0.3.7", integrator: false, failed, DateTime.UtcNow,
            new[] { "10:00:00 start 0.3.7" });
        var push = await sb.Friend.Engine.PublishStatusAsync(report, interactive: false);
        Assert.True(push.Ok, push.StdErr);

        // The owner publishes too: its own report is never shown as the other side's.
        var own = await StatusReports.BuildAsync(sb.Owner.Repo, "Аня", "0.3.7", integrator: true, null, null, Array.Empty<string>());
        Assert.True((await sb.Owner.Engine.PublishStatusAsync(own, interactive: false)).Ok);

        var status = await sb.Owner.Engine.CheckStatusAsync();
        Assert.NotNull(status.Peer);
        Assert.Equal("Боря", status.Peer!.Name);
        Assert.Equal("друг", status.Peer.Role);
        Assert.True(status.Peer.HasProblem);
        Assert.Contains("занят другой программой", status.Peer.LastOp!.Message);
        Assert.Contains("start 0.3.7", status.Peer.ToText());

        var friendSide = await sb.Friend.Engine.CheckStatusAsync();
        Assert.Equal("Аня", friendSide.Peer?.Name);

        Assert.Equal(mainBefore, await sb.RemoteHeadAsync());
        Assert.Equal("", await sb.Friend.OutAsync("status", "--porcelain"));
    }

    [Fact]
    public async Task A_new_report_replaces_the_old_one()
    {
        await using var sb = await CreateAsync();
        for (int i = 1; i <= 2; i++)
        {
            var r = await StatusReports.BuildAsync(sb.Friend.Repo, "Боря", "0.3.7", false, new OpResult(OpStatus.Done, $"операция {i}"), DateTime.UtcNow, Array.Empty<string>());
            Assert.True((await sb.Friend.Engine.PublishStatusAsync(r, false)).Ok);
        }
        var bare = new Git.GitRunner(sb.RemoteDir, "t", "t@example.invalid");
        var branch = StatusReports.BranchRef("Боря");
        Assert.Equal("1", await bare.OutAsync("rev-list", "--count", branch));

        var status = await sb.Owner.Engine.CheckStatusAsync();
        Assert.Equal("операция 2", status.Peer?.LastOp?.Message);
        Assert.False(status.Peer!.HasProblem);
    }

    [Fact]
    public async Task No_report_means_no_peer_line()
    {
        await using var sb = await CreateAsync();
        Assert.Null((await sb.Owner.Engine.CheckStatusAsync()).Peer);
    }

    [Theory]
    [InlineData("вход: ghp_abcdefghijklmnopqrstuvwxyz0123", "ghp_")]
    [InlineData("почта someone@example.com в логе", "someone@")]
    [InlineData("token=abc123secret", "abc123secret")]
    [InlineData("C:\\Users\\SomeUser\\AppData\\Roaming", "SomeUser")]
    [InlineData("https://name:pass@github.com/x/y.git", "pass@")]
    public void Clean_hides_secrets_addresses_and_the_user_name(string line, string hidden)
    {
        var clean = StatusReports.Clean(line, "SomeUser");
        Assert.DoesNotContain(hidden, clean);
        Assert.Contains("[", clean);
    }

    [Fact]
    public void A_huge_log_is_cut_to_the_size_limit()
    {
        var report = new StatusReport
        {
            Name = "Боря",
            Log = Enumerable.Range(0, 20_000).Select(i => $"строка журнала номер {i} " + new string('x', 40)).ToList(),
        };
        var bytes = StatusReports.Serialize(report);
        Assert.True(bytes.Length <= 256 * 1024);
        Assert.Contains("номер 19999", System.Text.Encoding.UTF8.GetString(bytes));
    }
}
