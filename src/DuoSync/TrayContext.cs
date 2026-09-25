using DuoSync.Core;
using DuoSync.Core.Git;
using DuoSync.Core.Ops;

namespace DuoSync;

/// <summary>The tray part of the app (§10.2): icon colour, menu, background check and one notification per friend's commit.</summary>
sealed class TrayContext : ApplicationContext
{
    public AppSettings Settings { get; }
    public List<ProjectController> Controllers { get; } = new();

    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _timer;
    MainForm? _form;
    bool _polling;

    public TrayContext(bool showWindow, string? snapshotPath = null)
    {
        Settings = AppSettings.Load();
        EnsureIdentity();
        AutoStart.Apply(Settings.AutoStart);
        foreach (var p in Settings.Projects.Where(p => Directory.Exists(p.Path)))
            Controllers.Add(new ProjectController(p, Settings));

        _tray = new NotifyIcon { Icon = Icons.For(SyncState.Busy), Text = "DuoSync", Visible = true, ContextMenuStrip = new ContextMenuStrip() };
        _tray.DoubleClick += (_, _) => ShowWindow();
        _tray.BalloonTipClicked += (_, _) => ShowWindow();
        RebuildMenu();
        SingleInstance.Listen(SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext(), ShowWindow, ExitThread);

        _timer = new System.Windows.Forms.Timer { Interval = Math.Max(15, Settings.PollSeconds) * 1000 };
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();

        if (showWindow || Controllers.Count == 0 || snapshotPath != null) ShowWindow();
        _ = snapshotPath == null ? PollAsync() : SnapshotAndExitAsync(snapshotPath);
    }

    /// <summary>Debug aid: poll once, render the window to PNG and quit.</summary>
    async Task SnapshotAndExitAsync(string path)
    {
        await PollAsync();
        await Task.Delay(800);
        if (_form != null)
        {
            using var bmp = new Bitmap(_form.Width, _form.Height);
            _form.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
            bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        ExitThread();
    }

    void EnsureIdentity()
    {
        if (string.IsNullOrWhiteSpace(Settings.MeName))
            Settings.MeName = Prompt.Ask(null, "DuoSync", "Как тебя зовут? Это имя для уведомлений и коммитов, не логин.") ?? Environment.UserName;
        if (string.IsNullOrWhiteSpace(Settings.FriendName))
            Settings.FriendName = Prompt.Ask(null, "DuoSync", "Как зовут друга, с которым вы делаете проект?") ?? "друг";
        if (string.IsNullOrWhiteSpace(Settings.MeEmail))
            Settings.MeEmail = GlobalGitEmail() ?? $"{Environment.UserName.ToLowerInvariant()}@users.noreply.duosync.invalid";
        Settings.Save();
    }

    static string? GlobalGitEmail()
    {
        try
        {
            var r = new GitRunner(Environment.CurrentDirectory, "x", "x").RunAsync("config", "--global", "user.email").GetAwaiter().GetResult();
            return r.Ok && r.StdOutTrimmed.Contains('@') ? r.StdOutTrimmed : null;
        }
        catch (Exception) { return null; }
    }

    public void ShowWindow()
    {
        if (_form == null || _form.IsDisposed) _form = new MainForm(this);
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
    }

    async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            foreach (var c in Controllers.ToList())
            {
                await c.RefreshAsync();
                MaybeNotify(c);
            }
            UpdateTray();
        }
        finally { _polling = false; }
    }

    void MaybeNotify(ProjectController c)
    {
        var st = c.Status;
        if (st is not { State: SyncState.Incoming or SyncState.Both } || st.RemoteSha == null || st.RemoteSha == c.Entry.NotifiedSha)
            return;
        c.Entry.NotifiedSha = st.RemoteSha;
        Settings.Save();
        var subject = st.IncomingSubjects.FirstOrDefault();
        var text = $"{Settings.FriendName} отправил{(subject != null ? $" «{subject}»" : " обнову")}: {Ru.Files(st.IncomingFiles.Count)}. " +
                   (st.Overlap.Count > 0 ? $"Пересекается с твоим неотправленным {st.Overlap[0]}." : "С твоими не пересекается.") +
                   " Нажми, чтобы открыть.";
        _tray.ShowBalloonTip(10_000, $"DuoSync — {c.Entry.Name}", text, ToolTipIcon.Info);
    }

    void UpdateTray()
    {
        var states = Controllers.Select(c => c.Status?.State ?? SyncState.Busy).ToList();
        var worst = states.Count == 0 ? SyncState.InSync
            : states.Contains(SyncState.AuthFailed) ? SyncState.AuthFailed
            : states.Contains(SyncState.Rewritten) ? SyncState.Rewritten
            : states.Any(s => s is SyncState.Incoming or SyncState.Both) ? SyncState.Incoming
            : states.Any(s => s is SyncState.Unsent or SyncState.NotPrepared) ? SyncState.Unsent
            : states.All(s => s is SyncState.Offline) ? SyncState.Offline
            : SyncState.InSync;
        _tray.Icon = Icons.For(worst);
        var tip = "DuoSync — " + (worst switch
        {
            SyncState.Incoming => $"обнова от {Settings.FriendName}",
            SyncState.NotPrepared => "проект не подготовлен",
            SyncState.Unsent => "есть неотправленное",
            SyncState.Offline => "нет связи с GitHub",
            SyncState.AuthFailed => "GitHub не пускает",
            SyncState.Rewritten => "история переписана",
            _ => "синхронно",
        });
        _tray.Text = tip.Length > 63 ? tip[..63] : tip;
        RebuildMenu();
    }

    void RebuildMenu()
    {
        var menu = _tray.ContextMenuStrip!;
        menu.Items.Clear();
        foreach (var c in Controllers)
        {
            var st = c.Status;
            var line = $"{c.Entry.Name} — " + (st?.State switch
            {
                SyncState.InSync => "синхронно ✓",
                SyncState.Incoming => $"↓{Math.Max(1, st.IncomingCommits)}",
                SyncState.Unsent => $"↑{st.UnsentFiles.Count}",
                SyncState.Both => $"↓{Math.Max(1, st.IncomingCommits)}  ↑{st.UnsentFiles.Count}",
                SyncState.Offline => "нет связи",
                SyncState.AuthFailed => "не пускает GitHub",
                SyncState.Rewritten => "история переписана",
                SyncState.NotPrepared => "не подготовлен",
                _ => "…",
            });
            menu.Items.Add(new ToolStripMenuItem(line) { Enabled = false });
        }
        if (Controllers.Count > 0) menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Открыть окно", null, (_, _) => ShowWindow());
        menu.Items.Add("Проверить сейчас", null, async (_, _) => await PollAsync());
        if (AutoStart.IsInstalledCopy)
        {
            var auto = new ToolStripMenuItem("Запускать вместе с Windows") { Checked = Settings.AutoStart, CheckOnClick = true };
            auto.CheckedChanged += (_, _) =>
            {
                Settings.AutoStart = auto.Checked;
                Settings.Save();
                AutoStart.Apply(Settings.AutoStart);
            };
            menu.Items.Add(auto);
        }
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, (_, _) => ExitThread());
    }

    // ------------------------------------------------------------------ Добавление проекта

    public void AddProjectInteractive(IWin32Window owner)
    {
        var connect = new TaskDialogButton("Подключить папку");
        var clone = new TaskDialogButton("Скачать по ссылке");
        var page = new TaskDialogPage
        {
            Caption = "DuoSync",
            Heading = "Добавить проект",
            Text = "Подключить — если проект уже лежит у тебя в папке и связан с GitHub.\nСкачать — если проекта у тебя ещё нет, а на GitHub он есть.",
            Buttons = { connect, clone, TaskDialogButton.Cancel },
        };
        var pressed = TaskDialog.ShowDialog(owner, page);
        if (pressed == connect) ConnectFolder(owner);
        else if (pressed == clone) _ = CloneAsync(owner);
    }

    void ConnectFolder(IWin32Window owner)
    {
        using var dlg = new FolderBrowserDialog { Description = "Папка проекта Unity (с папкой .git)", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(owner) != DialogResult.OK) return;
        var dir = dlg.SelectedPath;
        if (!Directory.Exists(Path.Combine(dir, ".git")))
        {
            MessageBox.Show(owner, "В этой папке нет git-репозитория. Выбери папку проекта, скачанного с GitHub, или используй «Скачать по ссылке».",
                "DuoSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        AddProject(dir);
    }

    async Task CloneAsync(IWin32Window owner)
    {
        var url = Prompt.Ask(owner, "Скачать проект", "Ссылка на репозиторий GitHub (https://github.com/…):");
        if (url == null) return;
        using var dlg = new FolderBrowserDialog { Description = "Куда положить проект (будет создана папка с его именем)", UseDescriptionForTitle = true };
        if (dlg.ShowDialog(owner) != DialogResult.OK) return;
        var name = Path.GetFileNameWithoutExtension(url.TrimEnd('/'));
        var target = Path.Combine(dlg.SelectedPath, name);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            MessageBox.Show(owner, $"Папка {target} уже есть и не пустая.", "DuoSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var git = new GitRunner(dlg.SelectedPath, Settings.MeName, Settings.MeEmail);
        _tray.ShowBalloonTip(5_000, "DuoSync", $"Скачиваю «{name}»… Большой проект может качаться долго.", ToolTipIcon.Info);
        var r = await git.RunAsync(new[] { "clone", url, target }, new GitRunOptions { Timeout = TimeSpan.FromMinutes(60) });
        if (!r.Ok)
        {
            MessageBox.Show(owner, "Не получилось скачать: " + GitErrors.Explain(r), "DuoSync", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }
        AddProject(target);
    }

    void AddProject(string dir)
    {
        if (Settings.Projects.Any(p => string.Equals(p.Path, dir, StringComparison.OrdinalIgnoreCase))) { ShowWindow(); return; }
        var entry = new ProjectEntry { Path = dir, Name = Path.GetFileName(dir.TrimEnd('\\', '/')) };
        Settings.Projects.Add(entry);
        Settings.Save();
        Controllers.Add(new ProjectController(entry, Settings));
        _form?.ReloadProjects();
        _ = PollAsync();
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _form?.Dispose();
        base.ExitThreadCore();
    }
}
