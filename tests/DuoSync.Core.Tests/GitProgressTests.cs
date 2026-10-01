using DuoSync.Core.Git;
using DuoSync.Core.Ops;
using static DuoSync.Core.Tests.Harness.Sandbox;

namespace DuoSync.Core.Tests;

public class GitProgressTests
{
    [Theory]
    [InlineData("Writing objects:  45% (123/456), 10.00 MiB | 1.20 MiB/s", "Отправляю проект: 45%, 10.00 МБ, 1.20 МБ/с")]
    [InlineData("Receiving objects:  30% (3/10)", "Скачиваю проект: 30%")]
    [InlineData("remote: Compressing objects: 100% (5/5), done.", "Готовлю отправку: 100%")]
    [InlineData("Filtering content:  50% (2/4), 3.00 MiB | 1.00 MiB/s", "Скачиваю файлы (картинки, модели, звуки): 50%, 3.00 МБ, 1.00 МБ/с")]
    public void Git_lines_become_short_russian_lines(string line, string expected)
        => Assert.Equal(expected, GitProgress.DescribeGit(line, out _));

    [Fact]
    public void Other_lines_are_not_progress()
        => Assert.Null(GitProgress.DescribeGit("fatal: repository not found", out _));

    [Fact]
    public void Lfs_progress_file_lines()
    {
        Assert.Equal("Выгружаю файлы (картинки, модели, звуки): 450 из 1019", GitProgress.DescribeLfs("upload 450/1019 2048/4096 Assets/Art/a.png"));
        Assert.Equal("Скачиваю файлы (картинки, модели, звуки): 3 из 7", GitProgress.DescribeLfs("download 3/7 10/10 Assets/b.wav"));
    }

    [Fact]
    public async Task Send_reports_progress_and_errors_stay_clean()
    {
        await using var sb = await CreateAsync();
        for (int i = 0; i < 200; i++) sb.Owner.Write($"Assets/Data/f{i}.txt", new string('x', 5000) + i);
        var r = await sb.Owner.Engine.SendAsync("Много файлов");
        Assert.Equal(OpStatus.Done, r.Status);
        Assert.Contains(sb.Owner.ProgressLines, l => l.StartsWith("Отправляю проект: 100%", StringComparison.Ordinal));
    }
}
