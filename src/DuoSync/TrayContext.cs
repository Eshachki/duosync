using System.Text.Json;
using DuoSync.Core;
using DuoSync.Core.Git;
using DuoSync.Core.GitHub;
using DuoSync.Core.Ops;
using DuoSync.Core.Setup;
using DuoSync.Core.Unity;

namespace DuoSync;

/// <summary>The tray part of the app (§10.2): icon colour, menu, background check and one notification per friend's commit.</summary>
sealed class TrayContext : ApplicationContext
{
    public AppSettings Settings { get; }
    public List<ProjectController> Controllers { get; } = new();
    /// <summary>DuoSync projects on GitHub that are not on this computer yet.</summary>
    public IReadOnlyList<RemoteProject> Available { get; private set; } = Array.Empty<RemoteProject>();
    public event Action? AvailableChanged;

    static readonly TimeSpan DiscoveryInterval = TimeSpan.FromMinutes(10);

    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _timer;
    MainForm? _form;
    bool _polling;
    bool _discovering;
    DateTime _nextDiscovery = DateTime.MinValue;
    GitHubClient? _github;
    /// <summary>What a click on the current balloon does.</summary>
    Action _balloonClick;

    public TrayContext(bool showWindow, string? snapshotPath = null)
    {
        Settings = AppSettings.Load();
        EnsureIdentity();
        AutoStart.Apply(Settings.AutoStart);
        foreach (var p in Settings.Projects.Where(p => Directory.Exists(p.Path)))
            Controllers.Add(new ProjectController(p, Settings));

        _balloonClick = () => ShowWindow();
        _tray = new NotifyIcon { Icon = Icons.For(SyncState.Busy), Text = "DuoSync", Visible = true, ContextMenuStrip = new ContextMenuStrip() };
        _tray.DoubleClick += (_, _) => ShowWindow();
        _tray.BalloonTipClicked += (_, _) => _balloonClick();
        RebuildMenu();
        SingleInstance.Listen(SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext(), () => ShowWindow(), ExitThread);

        _timer = new System.Windows.Forms.Timer { Interval = Math.Max(15, Settings.PollSeconds) * 1000 };
        _timer.Tick += async (_, _) => await PollAsync();
        _timer.Start();

        if (showWindow || Controllers.Count == 0 || snapshotPath != null) ShowWindow();
        _ = snapshotPath == null ? PollAsync() : SnapshotAndExitAsync(snapshotPath);
    }

    /// <summary>The friend in messages, always in the nominative («{имя} отправил», «прислал {имя}»).</summary>
    public string Friend => Settings.FriendDisplay;

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

    /// <summary>No questions on start: the name comes from git (or Windows), the friend's name from their first commit.</summary>
    void EnsureIdentity()
    {
        if (string.IsNullOrWhiteSpace(Settings.MeName))
            Settings.MeName = GlobalGit("user.name") ?? Environment.UserName;
        if (string.IsNullOrWhiteSpace(Settings.MeEmail))
            Settings.MeEmail = GlobalGit("user.email") is { } email && email.Contains('@')
                ? email
                : $"{Environment.UserName.ToLowerInvariant()}@users.noreply.duosync.invalid";
        Settings.Save();
    }

    static string? GlobalGit(string key)
    {
        try
        {
            // Task.Run: blocking on the UI thread must not wait for a continuation queued to that same thread.
            var r = Task.Run(() => new GitRunner(Path.GetTempPath(), "x", "x").RunAsync("config", "--global", key)).GetAwaiter().GetResult();
            return r.Ok && r.StdOutTrimmed.Trim().Length > 0 ? r.StdOutTrimmed.Trim() : null;
        }
        catch (Exception) { return null; }
    }

    public MainForm ShowWindow()
    {
        if (_form == null || _form.IsDisposed) _form = new MainForm(this);
        _form.Show();
        if (_form.WindowState == FormWindowState.Minimized) _form.WindowState = FormWindowState.Normal;
        _form.Activate();
        return _form;
    }

    void Notify(string title, string text, ToolTipIcon icon = ToolTipIcon.Info, Action? onClick = null, int milliseconds = 10_000)
    {
        _balloonClick = onClick ?? (() => ShowWindow());
        _tray.ShowBalloonTip(milliseconds, title, text, icon);
    }

    /// <summary>Runs an action started by a click; an unexpected error becomes a message instead of vanishing.</summary>
    public async void Guard(Func<Task> action)
    {
        try { await action(); }
        catch (GitHubException e) when (e.Kind == GitHubError.Unauthorized)
        {
            var dead = _github;
            _github = null;
            if (dead != null) await dead.ForgetCredentialAsync();
            MessageBox.Show(VisibleForm, "GitHub не принял сохранённый вход. Повтори действие — git попросит войти заново.",
                "DuoSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        catch (Exception e)
        {
            MessageBox.Show(VisibleForm, "Ошибка: " + e.Message, "DuoSync", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    IWin32Window? VisibleForm => _form is { IsDisposed: false, Visible: true } f ? f : null;

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
            await LearnFriendNameAsync();
        }
        finally { _polling = false; }

        if (DateTime.UtcNow >= _nextDiscovery)
        {
            _nextDiscovery = DateTime.UtcNow + DiscoveryInterval;
            await DiscoverAsync();
        }
    }

    void MaybeNotify(ProjectController c)
    {
        var st = c.Status;
        if (st is not { State: SyncState.Incoming or SyncState.Both } || st.RemoteSha == null || st.RemoteSha == c.Entry.NotifiedSha)
            return;
        c.Entry.NotifiedSha = st.RemoteSha;
        Settings.Save();
        var subject = st.IncomingSubjects.FirstOrDefault();
        var text = $"{Friend} отправил{(subject != null ? $" «{subject}»" : " обнову")}: {Ru.Files(st.IncomingFiles.Count)}. " +
                   (st.Overlap.Count > 0 ? $"Пересекается с твоим неотправленным {st.Overlap[0]}." : "С твоими не пересекается.") +
                   " Нажми, чтобы открыть.";
        Notify($"DuoSync — {c.Entry.Name}", text, onClick: () => ShowWindow().SelectProject(c));
    }

    /// <summary>Nobody typed the friend's name: take it from the latest commit on GitHub that is not ours.</summary>
    async Task LearnFriendNameAsync()
    {
        if (!string.IsNullOrWhiteSpace(Settings.FriendName)) return;
        foreach (var c in Controllers.ToList())
        {
            var repo = c.Engine.Repo;
            var name = await repo.OtherAuthorAsync(repo.RemoteBranchRef, Settings.MeName, Settings.MeEmail);
            if (string.IsNullOrWhiteSpace(name)) continue;
            Settings.FriendName = name;
            Settings.Save();
            foreach (var other in Controllers) other.Options.FriendName = name;
            UpdateTray();
            return;
        }
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
            SyncState.Incoming => $"обнова, прислал {Friend}",
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
        foreach (var p in Available.Take(5))
        {
            var project = p;
            menu.Items.Add($"Скачать «{project.Name}» с GitHub", null, (_, _) => Guard(() => DownloadAsync(ShowWindow(), project)));
        }
        if (Controllers.Count > 0 || Available.Count > 0) menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Открыть окно", null, (_, _) => ShowWindow());
        menu.Items.Add("Проверить сейчас", null, async (_, _) =>
        {
            _nextDiscovery = DateTime.MinValue;
            await PollAsync();
        });
        menu.Items.Add("Имена…", null, (_, _) => EditNames());
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

    /// <summary>Names are filled automatically; this is only for fixing them.</summary>
    void EditNames()
    {
        if (Controllers.Any(c => c.Busy))
        {
            MessageBox.Show(VisibleForm, "Сейчас идёт операция. Поменяй имена, когда она закончится.", "DuoSync", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }
        var me = Prompt.Ask(VisibleForm, "Имена", "Твоё имя (так подписаны твои отправки):", Settings.MeName);
        if (me == null) return;
        var friend = Prompt.Ask(VisibleForm, "Имена", "Как называть друга в уведомлениях:", Settings.FriendName);
        if (friend == null) return;
        Settings.MeName = me;
        Settings.FriendName = friend;
        Settings.Save();
        Controllers.Clear();
        foreach (var p in Settings.Projects.Where(p => Directory.Exists(p.Path)))
            Controllers.Add(new ProjectController(p, Settings));
        _form?.ReloadProjects();
        _ = PollAsync();
    }

    // ------------------------------------------------------------------ Проекты на GitHub

    /// <summary>
    /// Looks for DuoSync projects on GitHub (topic <c>duosync-project</c>) that are not on this computer and announces new ones.
    /// Background by default: no sign-in windows, silent when offline — the next round tries again.
    /// </summary>
    public async Task DiscoverAsync(bool interactive = false)
    {
        if (_discovering) return;
        _discovering = true;
        try
        {
            _github ??= await GitHubClient.ConnectAsync(interactive);
            if (_github == null) return;
            IReadOnlyList<RemoteProject> all;
            try { all = await _github.ProjectsAsync(); }
            catch (GitHubException e) when (e.Kind == GitHubError.Unauthorized) { _github = null; return; }
            var local = await LocalRepositoriesAsync();
            Available = all.Where(p => !local.Contains(p.FullName)).ToList();
            AvailableChanged?.Invoke();
            RebuildMenu();
            AnnounceNew();
        }
        catch (GitHubException) { }
        finally { _discovering = false; }
    }

    void AnnounceNew()
    {
        var fresh = Available.Where(p => !Settings.SeenProjects.Contains(p.FullName, StringComparer.OrdinalIgnoreCase)).ToList();
        if (fresh.Count == 0) return;
        Settings.SeenProjects.AddRange(fresh.Select(p => p.FullName));
        Settings.Save();
        if (fresh.Count == 1)
            Notify("DuoSync", $"На GitHub новый проект «{fresh[0].Name}». Нажми, чтобы скачать.",
                onClick: () => Guard(() => DownloadAsync(ShowWindow(), fresh[0])));
        else
            Notify("DuoSync", $"На GitHub новые проекты: {string.Join(", ", fresh.Select(p => $"«{p.Name}»"))}. Нажми, чтобы выбрать.");
    }

    /// <summary>"owner/name" of the projects already on this computer.</summary>
    async Task<HashSet<string>> LocalRepositoriesAsync()
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var changed = false;
        foreach (var c in Controllers.ToList())
        {
            if (c.Entry.GitHub == null && await RemoteRepositoryAsync(c.Engine.Repo.Git) is { } name)
            {
                c.Entry.GitHub = name;
                changed = true;
            }
            if (c.Entry.GitHub != null) set.Add(c.Entry.GitHub);
        }
        if (changed) Settings.Save();
        return set;
    }

    static async Task<string?> RemoteRepositoryAsync(GitRunner git)
    {
        var r = await git.RunAsync("remote", "get-url", "origin");
        return r.Ok ? GitHubClient.FullNameFromUrl(r.StdOutTrimmed) : null;
    }

    /// <summary>
    /// Marks a connected project's repository with the DuoSync topic so the friend's DuoSync finds it.
    /// Best effort: without admin rights on the repository it stays as is and its creator's DuoSync marks it.
    /// </summary>
    async Task MarkAsProjectAsync(ProjectController c)
    {
        try
        {
            c.Entry.GitHub ??= await RemoteRepositoryAsync(c.Engine.Repo.Git);
            if (c.Entry.GitHub == null) return;
            Settings.Save();
            _github ??= await GitHubClient.ConnectAsync(interactive: false);
            if (_github != null) await _github.MarkAsProjectAsync(c.Entry.GitHub);
        }
        catch (GitHubException) { }
    }

    /// <summary>Where new projects go: the last download folder, next to existing projects, Unity Hub's folder, Documents.</summary>
    string DefaultProjectsFolder()
    {
        if (Settings.ProjectsFolder.Length > 0 && Directory.Exists(Settings.ProjectsFolder)) return Settings.ProjectsFolder;
        foreach (var p in Enumerable.Reverse(Settings.Projects))
            if (Path.GetDirectoryName(p.Path.TrimEnd('\\', '/')) is { Length: > 0 } parent && Directory.Exists(parent))
                return parent;
        return UnityHubProjectsFolder() ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    /// <summary>Unity Hub's default location for new projects (%APPDATA%\UnityHub\projectDir.json).</summary>
    static string? UnityHubProjectsFolder()
    {
        try
        {
            var file = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "UnityHub", "projectDir.json");
            if (!File.Exists(file)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(file));
            return doc.RootElement.TryGetProperty("directoryPath", out var d) && d.GetString() is { Length: > 0 } dir && Directory.Exists(dir) ? dir : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException) { return null; }
    }

    // ------------------------------------------------------------------ Добавление проекта

    public async Task AddProjectInteractiveAsync(IWin32Window owner)
    {
        // Fresh list first: the friend may have created the project a minute ago.
        if (owner is Control control) control.UseWaitCursor = true;
        try { await DiscoverAsync(); }
        finally { if (owner is Control c) c.UseWaitCursor = false; }

        var page = new TaskDialogPage { Caption = "DuoSync", Heading = "Добавить проект", AllowCancel = true };
        var downloads = new Dictionary<TaskDialogButton, RemoteProject>();
        foreach (var p in Available.Take(6))
        {
            var button = new TaskDialogCommandLinkButton($"Скачать «{p.Name}»", $"{p.FullName}, обновлён {p.PushedAt.ToLocalTime():dd.MM.yyyy HH:mm}");
            downloads[button] = p;
            page.Buttons.Add(button);
        }
        var create = new TaskDialogCommandLinkButton("Новый проект",
            "Unity-проект, которого ещё нет на GitHub: программа создаст приватный репозиторий и отправит проект. У друга он появится сам.");
        var connect = new TaskDialogCommandLinkButton("Подключить папку", "Проект уже лежит у тебя и связан с GitHub.");
        var clone = new TaskDialogCommandLinkButton("Скачать по ссылке", "Если нужного проекта нет в списке.");
        page.Buttons.Add(create);
        page.Buttons.Add(connect);
        page.Buttons.Add(clone);
        page.Buttons.Add(TaskDialogButton.Cancel);

        var pressed = TaskDialog.ShowDialog(owner, page);
        if (downloads.TryGetValue(pressed, out var project)) await DownloadAsync(owner, project);
        else if (pressed == create) await CreateProjectAsync(owner);
        else if (pressed == connect) ConnectFolder(owner);
        else if (pressed == clone) await CloneByLinkAsync(owner);
    }

    void ConnectFolder(IWin32Window owner)
    {
        using var dlg = new FolderBrowserDialog { Description = "Папка проекта Unity (с папкой .git)", UseDescriptionForTitle = true, InitialDirectory = DefaultProjectsFolder() };
        if (dlg.ShowDialog(owner) != DialogResult.OK) return;
        var dir = dlg.SelectedPath;
        if (!Directory.Exists(Path.Combine(dir, ".git")))
        {
            MessageBox.Show(owner, "В этой папке нет git-репозитория. Если проекта ещё нет на GitHub — выбери «Новый проект», если есть — скачай его.",
                "DuoSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var controller = AddProject(dir);
        _ = MarkAsProjectAsync(controller);
    }

    /// <summary>Unity folder without git → private GitHub repository marked as a DuoSync project, setup and the first send.</summary>
    async Task CreateProjectAsync(IWin32Window owner)
    {
        using var dlg = new FolderBrowserDialog { Description = "Папка Unity-проекта (где лежат Assets и Packages)", UseDescriptionForTitle = true, InitialDirectory = DefaultProjectsFolder() };
        if (dlg.ShowDialog(owner) != DialogResult.OK) return;
        var dir = dlg.SelectedPath;
        if (!BridgeInstaller.IsUnityProject(dir))
        {
            MessageBox.Show(owner, "Это не папка Unity-проекта: в ней нет Assets и Packages.", "DuoSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        if (Directory.Exists(Path.Combine(dir, ".git")))
        {
            _ = MarkAsProjectAsync(AddProject(dir));
            return;
        }
        if (UnityDetector.IsProjectOpen(dir))
        {
            MessageBox.Show(owner, "Этот проект сейчас открыт в Unity. Закрой Unity на время создания репозитория и повтори.",
                "DuoSync", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return;
        }

        _github ??= await GitHubClient.ConnectAsync(interactive: true);
        string url, name;
        if (_github != null)
        {
            var suggested = $"{await DefaultOwnerAsync(_github)}/{GitHubClient.SafeRepositoryName(Path.GetFileName(dir.TrimEnd('\\', '/')))}";
            var input = Prompt.Ask(owner, "Новый проект", "Репозиторий на GitHub (владелец/имя), будет приватным:", suggested);
            if (input == null) return;
            if (!GitHubClient.TryParseFullName(input, out var repoOwner, out name))
            {
                MessageBox.Show(owner, "Нужно в виде «организация/имя», латиницей, без пробелов.", "DuoSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            var fullName = $"{repoOwner}/{name}";
            try { await _github.CreateRepositoryAsync(repoOwner, name); }
            catch (GitHubException e) when (e.Kind == GitHubError.AlreadyExists)
            {
                if (!await _github.IsEmptyAsync(fullName))
                {
                    MessageBox.Show(owner, $"На GitHub уже есть репозиторий {fullName} с файлами. Выбери другое имя.", "DuoSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            try { await _github.MarkAsProjectAsync(fullName); } catch (GitHubException) { }
            Settings.GitHubOwner = repoOwner;
            Settings.Save();
            url = $"https://github.com/{fullName}.git";
        }
        else
        {
            var input = Prompt.Ask(owner, "Новый проект", "Не получилось войти в GitHub. Создай пустой приватный репозиторий на github.com и вставь ссылку:");
            if (input == null) return;
            url = NormalizeUrl(input);
            name = Path.GetFileNameWithoutExtension(url.TrimEnd('/'));
        }

        var git = new GitRunner(dir, Settings.MeName, Settings.MeEmail);
        await git.RunCheckedAsync("init", "-q", "-b", "main");
        await git.RunCheckedAsync("remote", "add", "origin", url);

        var controller = AddProject(dir);
        ShowWindow().SelectProject(controller);
        Notify("DuoSync", $"Готовлю «{name}» и отправляю на GitHub. Большой проект может отправляться долго.", milliseconds: 5_000);
        var result = await controller.RunAsync(async engine =>
        {
            var prepared = await new ProjectSetup(engine.Repo, Settings.MeName, Settings.FriendDisplay).ApplyAsync();
            if (!prepared.Succeeded) return prepared;
            return await engine.SendAsync("Новый проект");
        });
        Notify("DuoSync", result.Succeeded
            ? $"«{name}» на GitHub. {Friend} увидит его у себя в DuoSync и скачает одной кнопкой."
            : result.Message, result.Succeeded ? ToolTipIcon.Info : ToolTipIcon.Warning);
    }

    /// <summary>Where a new repository goes by default: the remembered owner, the owner of existing projects, the organization, the account.</summary>
    async Task<string> DefaultOwnerAsync(GitHubClient github)
    {
        if (!string.IsNullOrWhiteSpace(Settings.GitHubOwner)) return Settings.GitHubOwner;
        var known = Controllers.Select(c => c.Entry.GitHub).Concat(Available.Select(p => p.FullName)).FirstOrDefault(n => n != null);
        if (known != null) return known[..known.IndexOf('/')];
        try
        {
            var orgs = await github.OrganizationsAsync();
            return orgs.Count > 0 ? orgs[0] : await github.LoginAsync();
        }
        catch (GitHubException) { return ""; }
    }

    /// <summary>A project found on GitHub: confirm the folder and download it.</summary>
    public async Task DownloadAsync(IWin32Window? owner, RemoteProject project)
    {
        var parent = DefaultProjectsFolder();
        var target = await FreeFolderAsync(parent, project);
        var download = new TaskDialogButton("Скачать");
        var other = new TaskDialogButton("Другая папка…");
        var page = new TaskDialogPage
        {
            Caption = "DuoSync",
            Heading = $"Скачать «{project.Name}»",
            Text = $"Проект будет в папке:\n{target}",
            Buttons = { download, other, TaskDialogButton.Cancel },
            DefaultButton = download,
            AllowCancel = true,
        };
        var pressed = owner != null ? TaskDialog.ShowDialog(owner, page) : TaskDialog.ShowDialog(page);
        if (pressed == other)
        {
            using var dlg = new FolderBrowserDialog { Description = "Куда положить проект (внутри будет папка с его именем)", UseDescriptionForTitle = true, InitialDirectory = parent };
            if (dlg.ShowDialog(owner) != DialogResult.OK) return;
            target = await FreeFolderAsync(dlg.SelectedPath, project);
        }
        else if (pressed != download) return;
        await CloneIntoAsync(owner, project.CloneUrl, target);
    }

    /// <summary>parent\Name, or Name-2, Name-3… when the folder is taken by something else. A folder with this very repository is fine.</summary>
    static async Task<string> FreeFolderAsync(string parent, RemoteProject project)
    {
        for (int i = 1; ; i++)
        {
            var candidate = Path.Combine(parent, i == 1 ? project.Name : $"{project.Name}-{i}");
            if (!Directory.Exists(candidate) || !Directory.EnumerateFileSystemEntries(candidate).Any()) return candidate;
            if (await IsCloneOfAsync(candidate, project.FullName)) return candidate;
        }
    }

    static async Task<bool> IsCloneOfAsync(string dir, string fullName)
    {
        if (!Directory.Exists(Path.Combine(dir, ".git"))) return false;
        var name = await RemoteRepositoryAsync(new GitRunner(dir, "x", "x"));
        return string.Equals(name, fullName, StringComparison.OrdinalIgnoreCase);
    }

    async Task CloneByLinkAsync(IWin32Window owner)
    {
        var input = Prompt.Ask(owner, "Скачать проект", "Ссылка на репозиторий GitHub или «организация/имя»:");
        if (input == null) return;
        var url = NormalizeUrl(input);
        using var dlg = new FolderBrowserDialog { Description = "Куда положить проект (будет создана папка с его именем)", UseDescriptionForTitle = true, InitialDirectory = DefaultProjectsFolder() };
        if (dlg.ShowDialog(owner) != DialogResult.OK) return;
        await CloneIntoAsync(owner, url, Path.Combine(dlg.SelectedPath, Path.GetFileNameWithoutExtension(url.TrimEnd('/'))));
    }

    async Task CloneIntoAsync(IWin32Window? owner, string url, string target)
    {
        var name = Path.GetFileName(target);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any())
        {
            if (GitHubClient.FullNameFromUrl(url) is { } fullName && await IsCloneOfAsync(target, fullName))
            {
                ShowWindow().SelectProject(AddProject(target));
                return;
            }
            MessageBox.Show(owner, $"Папка {target} уже есть и не пустая. Выбери другую.", "DuoSync", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        var parent = Path.GetDirectoryName(target)!;
        Directory.CreateDirectory(parent);
        Settings.ProjectsFolder = parent;
        Settings.Save();

        var form = ShowWindow();
        form.ShowNote($"Скачиваю «{name}» в {target}… Большой проект может качаться долго.");
        Notify("DuoSync", $"Скачиваю «{name}»… Большой проект может качаться долго.", milliseconds: 5_000);
        var git = new GitRunner(parent, Settings.MeName, Settings.MeEmail);
        var options = new GitRunOptions { Timeout = TimeSpan.FromMinutes(90) };
        GitResult r = await git.RunAsync(new[] { "clone", url, target }, options);
        for (int attempt = 0; !r.Ok && attempt < 2 && GitErrors.Classify(r) is GitErrorKind.Network or GitErrorKind.Timeout; attempt++)
        {
            form.ShowNote($"Связь с GitHub оборвалась, повторяю скачивание «{name}»…");
            await Task.Delay(TimeSpan.FromSeconds(15));
            if (Directory.Exists(target)) { try { Directory.Delete(target, true); } catch (IOException) { } }
            r = await git.RunAsync(new[] { "clone", url, target }, options);
        }
        if (!r.Ok)
        {
            form.ShowNote("Не получилось скачать: " + GitErrors.Explain(r), error: true);
            return;
        }
        var controller = AddProject(target);
        form.SelectProject(controller);
        var unity = UnityVersion(target);
        var text = $"«{name}» скачан в {target}. Открой его в Unity{(unity != null ? " " + unity : "")} через Unity Hub → Add → Add project from disk.";
        form.ShowNote(text);
        Notify("DuoSync", text);
    }

    /// <summary>Editor version from ProjectSettings/ProjectVersion.txt: the friend must open the project in the same one.</summary>
    static string? UnityVersion(string projectDir)
    {
        try
        {
            var file = Path.Combine(projectDir, "ProjectSettings", "ProjectVersion.txt");
            if (!File.Exists(file)) return null;
            foreach (var line in File.ReadLines(file))
                if (line.StartsWith("m_EditorVersion:", StringComparison.Ordinal))
                    return line["m_EditorVersion:".Length..].Trim();
        }
        catch (IOException) { }
        return null;
    }

    static string NormalizeUrl(string input)
    {
        var s = input.Trim();
        if (System.Text.RegularExpressions.Regex.IsMatch(s, @"^[A-Za-z0-9._-]+/[A-Za-z0-9._-]+$")) return $"https://github.com/{s}.git";
        return s;
    }

    ProjectController AddProject(string dir)
    {
        var existing = Controllers.FirstOrDefault(c => string.Equals(c.Entry.Path, dir, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { ShowWindow(); return existing; }
        var entry = new ProjectEntry { Path = dir, Name = Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, '/')) };
        Settings.Projects.Add(entry);
        Settings.Save();
        var controller = new ProjectController(entry, Settings);
        Controllers.Add(controller);
        _form?.ReloadProjects();
        _nextDiscovery = DateTime.MinValue;
        _ = PollAsync();
        return controller;
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
