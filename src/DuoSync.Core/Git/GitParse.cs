using System.Text;

namespace DuoSync.Core.Git;

public enum StatusKind { Changed, Renamed, Unmerged, Untracked, Ignored }

/// <summary>One entry of `git status --porcelain=v2 -z`.</summary>
public sealed record StatusEntry(StatusKind Kind, string XY, string Path, string? OrigPath);

/// <summary>One conflicted stage entry of `git merge-tree --write-tree -z`.</summary>
public sealed record MergeTreeConflict(string Mode, string Oid, int Stage, string Path);

public sealed record MergeTreeMessage(IReadOnlyList<string> Paths, string Type, string Message);

public sealed record MergeTreeResult(bool Clean, string TreeOid, IReadOnlyList<MergeTreeConflict> Conflicts, IReadOnlyList<MergeTreeMessage> Messages)
{
    public IReadOnlyList<string> ConflictedPaths => Conflicts.Select(c => c.Path).Distinct(StringComparer.Ordinal).ToList();
}

public enum PushRefStatus { Ok, UpToDate, Rejected, Error }

public sealed record PushRefResult(PushRefStatus Status, string Ref, string Summary);

public static class GitParse
{
    /// <summary>Splits NUL-separated output; a trailing NUL does not produce an empty last field.</summary>
    public static List<string> SplitZ(byte[] data)
    {
        var parts = new List<string>();
        int start = 0;
        for (int i = 0; i < data.Length; i++)
        {
            if (data[i] != 0) continue;
            parts.Add(Encoding.UTF8.GetString(data, start, i - start));
            start = i + 1;
        }
        if (start < data.Length) parts.Add(Encoding.UTF8.GetString(data, start, data.Length - start));
        return parts;
    }

    public static List<StatusEntry> ParseStatusV2(byte[] data)
    {
        var fields = SplitZ(data);
        var list = new List<StatusEntry>();
        for (int i = 0; i < fields.Count; i++)
        {
            var f = fields[i];
            if (f.Length == 0 || f[0] == '#') continue;
            switch (f[0])
            {
                case '1':
                {
                    var p = f.Split(' ', 9);
                    list.Add(new StatusEntry(StatusKind.Changed, p[1], p[8], null));
                    break;
                }
                case '2':
                {
                    var p = f.Split(' ', 10);
                    string orig = i + 1 < fields.Count ? fields[++i] : "";
                    list.Add(new StatusEntry(StatusKind.Renamed, p[1], p[9], orig));
                    break;
                }
                case 'u':
                {
                    var p = f.Split(' ', 11);
                    list.Add(new StatusEntry(StatusKind.Unmerged, p[1], p[10], null));
                    break;
                }
                case '?':
                    list.Add(new StatusEntry(StatusKind.Untracked, "??", f.Substring(2), null));
                    break;
                case '!':
                    list.Add(new StatusEntry(StatusKind.Ignored, "!!", f.Substring(2), null));
                    break;
            }
        }
        return list;
    }

    /// <summary>Parses `git merge-tree --write-tree -z` (exit 0 = clean, 1 = conflicts).</summary>
    public static MergeTreeResult ParseMergeTree(GitResult r)
    {
        if (r.ExitCode is not (0 or 1)) throw new GitException(r);
        var fields = SplitZ(r.StdOutBytes);
        if (fields.Count == 0) throw new FormatException("merge-tree printed nothing");
        var tree = fields[0];
        var conflicts = new List<MergeTreeConflict>();
        var messages = new List<MergeTreeMessage>();
        int i = 1;
        // Conflicted file info: "<mode> <oid> <stage>\t<path>", terminated by an empty field.
        for (; i < fields.Count; i++)
        {
            var f = fields[i];
            if (f.Length == 0) { i++; break; }
            int tab = f.IndexOf('\t');
            if (tab < 0) break; // no conflict section: informational messages start here
            var head = f.Substring(0, tab).Split(' ');
            conflicts.Add(new MergeTreeConflict(head[0], head[1], int.Parse(head[2]), f.Substring(tab + 1)));
        }
        // Informational messages: <n>\0<path>...\0<type>\0<message>\0
        while (i < fields.Count)
        {
            if (fields[i].Length == 0) { i++; continue; }
            if (!int.TryParse(fields[i], out var n) || i + n + 1 >= fields.Count) break;
            var paths = fields.GetRange(i + 1, n);
            var type = fields[i + n + 1];
            var msg = i + n + 2 < fields.Count ? fields[i + n + 2] : "";
            messages.Add(new MergeTreeMessage(paths, type, msg));
            i += n + 3;
        }
        return new MergeTreeResult(r.ExitCode == 0, tree, conflicts, messages);
    }

    /// <summary>Parses `git rev-list --left-right --count A...B` ("3\t5").</summary>
    public static (int Left, int Right) ParseLeftRight(string s)
    {
        var p = s.Trim().Split('\t', ' ', StringSplitOptions.RemoveEmptyEntries);
        return (int.Parse(p[0]), int.Parse(p[1]));
    }

    /// <summary>Parses `git ls-remote` output into ref → sha.</summary>
    public static Dictionary<string, string> ParseLsRemote(string s)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var line in s.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.TrimEnd('\r').Split('\t');
            if (p.Length == 2) map[p[1]] = p[0];
        }
        return map;
    }

    /// <summary>Parses `git push --porcelain` ref lines: "&lt;flag&gt;\t&lt;from&gt;:&lt;to&gt;\t&lt;summary&gt;".</summary>
    public static List<PushRefResult> ParsePushPorcelain(string s)
    {
        var list = new List<PushRefResult>();
        foreach (var raw in s.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.Length < 3 || line[1] != '\t') continue;
            var parts = line.Split('\t');
            if (parts.Length < 3) continue;
            var refspec = parts[1];
            var to = refspec.Contains(':') ? refspec[(refspec.IndexOf(':') + 1)..] : refspec;
            var status = line[0] switch
            {
                ' ' or '+' or '*' or '-' => PushRefStatus.Ok,
                '=' => PushRefStatus.UpToDate,
                '!' => PushRefStatus.Rejected,
                _ => PushRefStatus.Error,
            };
            list.Add(new PushRefResult(status, to, parts[2]));
        }
        return list;
    }
}
