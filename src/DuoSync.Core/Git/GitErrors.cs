using System.Text.RegularExpressions;

namespace DuoSync.Core.Git;

public enum GitErrorKind { None, Network, Auth, NonFastForward, WouldOverwrite, FileBusy, LfsMissing, LfsQuota, TooLarge, DiskFull, Timeout, Other }

/// <summary>Maps git's C-locale error texts to one kind and a short Russian explanation (§3.12).</summary>
public static class GitErrors
{
    static readonly (GitErrorKind Kind, Regex Pattern)[] Table =
    {
        (GitErrorKind.Auth, new Regex(@"Authentication failed|could not read Username|HTTP 403|returned error: 403|Permission to .* denied|Repository not found", RegexOptions.IgnoreCase)),
        (GitErrorKind.NonFastForward, new Regex(@"non-fast-forward|fetch first|\[rejected\]", RegexOptions.IgnoreCase)),
        (GitErrorKind.WouldOverwrite, new Regex(@"would be overwritten|Please move or remove", RegexOptions.IgnoreCase)),
        (GitErrorKind.FileBusy, new Regex(@"unable to unlink|Permission denied|used by another process|unable to create file|Invalid argument", RegexOptions.IgnoreCase)),
        (GitErrorKind.LfsMissing, new Regex(@"smudge filter lfs failed|Object does not exist on the server", RegexOptions.IgnoreCase)),
        (GitErrorKind.LfsQuota, new Regex(@"over its data quota|exceeded .*(quota|budget)", RegexOptions.IgnoreCase)),
        (GitErrorKind.TooLarge, new Regex(@"exceeds GitHub's file size limit|GH001", RegexOptions.IgnoreCase)),
        (GitErrorKind.DiskFull, new Regex(@"No space left", RegexOptions.IgnoreCase)),
        (GitErrorKind.Network, new Regex(@"Could not resolve host|Failed to connect|timed out|early EOF|RPC failed|HTTP 5\d\d|returned error: 5\d\d|Connection (reset|refused|was reset)|remote end hung up|unable to access|SSL", RegexOptions.IgnoreCase)),
    };

    public static GitErrorKind Classify(GitResult r)
    {
        if (r.Ok) return GitErrorKind.None;
        if (r.TimedOut) return GitErrorKind.Timeout;
        var text = r.StdErr + "\n" + r.StdOut;
        foreach (var (kind, pattern) in Table)
            if (pattern.IsMatch(text)) return kind;
        return GitErrorKind.Other;
    }

    public static string Explain(GitResult r) => Classify(r) switch
    {
        GitErrorKind.None => "готово",
        GitErrorKind.Network => "нет связи с GitHub, работа сохранена у тебя",
        GitErrorKind.Timeout => "git не ответил вовремя, работа сохранена у тебя",
        GitErrorKind.Auth => "GitHub не пускает, нужно войти в GitHub",
        GitErrorKind.NonFastForward => "друг успел отправить раньше",
        GitErrorKind.WouldOverwrite => "файлы менялись во время операции, ничего не применено",
        GitErrorKind.FileBusy => "файл занят (Unity или антивирус)",
        GitErrorKind.LfsMissing => "не докачались файлы LFS",
        GitErrorKind.LfsQuota => "у владельца репозитория кончилась квота LFS",
        GitErrorKind.TooLarge => "файл больше 100 МБ, его надо перевести в LFS",
        GitErrorKind.DiskFull => "нет места на диске",
        _ => "git: " + FirstLine(r.StdErr),
    };

    static string FirstLine(string s)
    {
        foreach (var line in s.Split('\n'))
        {
            var t = line.Trim();
            if (t.Length > 0 && !t.StartsWith("hint:", StringComparison.Ordinal)) return t;
        }
        return "неизвестная ошибка";
    }
}
