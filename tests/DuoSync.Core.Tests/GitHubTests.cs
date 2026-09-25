using System.Text.Json;
using DuoSync.Core.GitHub;
using DuoSync.Core.Ops;
using static DuoSync.Core.Tests.Harness.Sandbox;

namespace DuoSync.Core.Tests;

public class GitHubTests
{
    [Theory]
    [InlineData("https://github.com/team/game.git", "team/game")]
    [InlineData("https://github.com/team/game", "team/game")]
    [InlineData("https://someone@github.com/team/My.Game.git", "team/My.Game")]
    [InlineData("git@github.com:team/game.git", "team/game")]
    [InlineData("ssh://git@github.com/team/game.git", "team/game")]
    [InlineData("https://gitlab.com/team/game.git", null)]
    [InlineData("C:/repos/game", null)]
    public void Repository_name_from_remote_url(string url, string? expected) => Assert.Equal(expected, GitHubClient.FullNameFromUrl(url));

    [Theory]
    [InlineData("team/game", true)]
    [InlineData("https://github.com/team/game.git", true)]
    [InlineData("team/my game", false)]
    [InlineData("game", false)]
    [InlineData("team/..", false)]
    public void Owner_and_name_input(string input, bool ok) => Assert.Equal(ok, GitHubClient.TryParseFullName(input, out _, out _));

    [Fact]
    public void Folder_names_become_repository_names()
    {
        Assert.Equal("My-Game", GitHubClient.SafeRepositoryName("My Game"));
        Assert.Equal("unity-project", GitHubClient.SafeRepositoryName("Игра"));
    }

    [Fact]
    public void Password_line_of_git_credential_output_is_the_token()
    {
        Assert.Equal("tok123", GitHubClient.ParseCredential("protocol=https\r\nhost=github.com\r\nusername=someone\r\npassword=tok123\r\n"));
        Assert.Null(GitHubClient.ParseCredential("protocol=https\nhost=github.com\n"));
    }

    [Fact]
    public void Only_repositories_with_the_topic_are_projects()
    {
        using var doc = JsonDocument.Parse("""
        [
          {"full_name":"team/game","topics":["duosync-project","unity"],"pushed_at":"2026-09-20T10:00:00Z","description":"Unity-проект (DuoSync)","archived":false},
          {"full_name":"team/site","topics":["duosync"],"pushed_at":"2026-09-21T10:00:00Z"},
          {"full_name":"team/old","topics":["duosync-project"],"archived":true},
          {"full_name":"someone/sandbox","topics":["duosync-project"],"pushed_at":null}
        ]
        """);
        var projects = GitHubClient.ParseProjects(doc.RootElement.EnumerateArray()).ToList();

        Assert.Equal(new[] { "team/game", "someone/sandbox" }, projects.Select(p => p.FullName));
        Assert.Equal("game", projects[0].Name);
        Assert.Equal("https://github.com/team/game.git", projects[0].CloneUrl);
    }

    [Fact]
    public void Error_text_keeps_field_errors()
        => Assert.Equal("Repository creation failed.; name already exists on this account", GitHubClient.ErrorMessage(
            """{"message":"Repository creation failed.","errors":[{"resource":"Repository","code":"custom","field":"name","message":"name already exists on this account"}]}"""));

    [Fact]
    public async Task Friend_name_is_the_latest_author_who_is_not_me()
    {
        await using var sb = await CreateAsync();
        sb.Friend.Write("Assets/Scripts/Coin.cs", "class Coin {}\n");
        Assert.Equal(OpStatus.Done, (await sb.Friend.Engine.SendAsync("Монетка")).Status);
        await sb.Owner.Repo.FetchAsync();

        Assert.Equal("Боря", await sb.Owner.Repo.OtherAuthorAsync(sb.Owner.Repo.RemoteBranchRef, "Аня", "аня@example.invalid"));
        Assert.Equal("Seed", await sb.Friend.Repo.OtherAuthorAsync(sb.Friend.Repo.RemoteBranchRef, "Боря", "боря@example.invalid"));
    }
}
