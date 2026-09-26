using DuoSync.Core;
using DuoSync.Core.Ops;

namespace DuoSync;

/// <summary>Main window (§10.1): project, status lines, «Получить» / «Отправить», feed.</summary>
sealed class MainForm : Form
{
    readonly TrayContext _app;
    readonly ComboBox _projects = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 260 };
    readonly Button _add = new() { Text = "Добавить проект…", AutoSize = true };
    /// <summary>Projects on GitHub that are not on this computer: one «Скачать» button each.</summary>
    readonly FlowLayoutPanel _available = new() { Dock = DockStyle.Fill, AutoSize = true, Visible = false };
    readonly Label _incoming = new() { AutoSize = true, Font = new Font("Segoe UI", 10.5f, FontStyle.Bold) };
    readonly Label _unsent = new() { AutoSize = true };
    /// <summary>A merge request: the friend's one waiting here (integrator) or own one waiting there.</summary>
    readonly Label _request = new() { AutoSize = true, ForeColor = Color.FromArgb(170, 90, 0) };
    /// <summary>The other side's latest status report (§6а); a click shows it whole.</summary>
    readonly LinkLabel _peer = new() { AutoSize = true, LinkBehavior = LinkBehavior.HoverUnderline, Visible = false };
    readonly Button _merge = new() { Text = "Слить с Claude", AutoSize = true, Height = 36, Visible = false, Font = new Font("Segoe UI", 10f, FontStyle.Bold) };
    readonly Button _receive = new() { Text = "Получить", Width = 150, Height = 36 };
    readonly Button _send = new() { Text = "Отправить", Width = 150, Height = 36 };
    readonly Button _undo = new() { Text = "Откатить получение", AutoSize = true, Height = 36 };
    readonly Button _check = new() { Text = "Проверить сейчас", AutoSize = true, Height = 36 };
    readonly Button _todo = new() { Text = "Черновик", AutoSize = true, Height = 36 };
    readonly Button _report = new() { Text = "Отправить отчёт", AutoSize = true, Height = 36 };
    readonly Button _prepare = new() { Text = "Подготовить проект", AutoSize = true, Height = 36, Visible = false };
    readonly TextBox _message = new() { PlaceholderText = "Что сделал — можно не писать, программа подпишет сама по файлам", Dock = DockStyle.Fill };
    readonly ListBox _feed = new() { Dock = DockStyle.Fill, IntegralHeight = false, HorizontalScrollbar = true };
    readonly Label _result = new() { AutoSize = true, MaximumSize = new Size(760, 0) };

    public MainForm(TrayContext app)
    {
        _app = app;
        Text = $"DuoSync {UpdateGuard.Current.ToString(3)}";
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(800, 560);
        MinimumSize = new Size(640, 420);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = Icons.For(SyncState.InSync);

        var top = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false };
        top.Controls.AddRange(new Control[] { new Label { Text = "Проект:", AutoSize = true, Padding = new Padding(0, 6, 0, 0) }, _projects, _add });

        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        buttons.Controls.AddRange(new Control[] { _prepare, _merge, _receive, _send, _undo, _check, _todo, _report });

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(12) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.Controls.Add(top);
        layout.Controls.Add(_available);
        layout.Controls.Add(_incoming);
        layout.Controls.Add(_unsent);
        layout.Controls.Add(_request);
        layout.Controls.Add(_peer);
        layout.Controls.Add(buttons);
        layout.Controls.Add(_message);
        layout.Controls.Add(_result);
        layout.Controls.Add(new Label { Text = "Лента проекта", AutoSize = true, ForeColor = Color.DimGray });
        layout.Controls.Add(_feed);
        for (int i = 0; i < 10; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Controls.Add(layout);

        _projects.SelectedIndexChanged += async (_, _) => await ShowProjectAsync();
        _add.Click += (_, _) => _app.Guard(() => _app.AddProjectInteractiveAsync(this));
        _app.AvailableChanged += RenderAvailable;
        Disposed += (_, _) => _app.AvailableChanged -= RenderAvailable;
        _receive.Click += async (_, _) => await RunAsync(e => e.ReceiveAsync());
        _send.Click += async (_, _) =>
        {
            var text = _message.Text;
            var r = await RunAsync(e => e.SendAsync(text));
            if (r?.Status == OpStatus.Done) _message.Clear();
        };
        _undo.Click += async (_, _) =>
        {
            if (MessageBox.Show(this, "Вернуть файлы, как они были до последнего «Получить»? Твои правки после получения останутся.",
                    "Откатить получение", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) == DialogResult.OK)
                await RunAsync(e => e.UndoReceiveAsync());
        };
        _check.Click += async (_, _) => { if (Current != null) await Current.RefreshAsync(); };
        _prepare.Click += async (_, _) => await PrepareAsync();
        _merge.Click += async (_, _) =>
        {
            var c = Current;
            if (c == null) return;
            // Own conflict first (main is needed before a request can be merged), otherwise the friend's request.
            var request = c.LastResult?.Status == OpStatus.Conflict ? null : c.Status?.Requests.FirstOrDefault();
            await RunAsync(e => e.MergeWithClaudeAsync(request, question => Task.FromResult(AskDialog.Ask(this, question))));
        };
        _todo.Click += (_, _) => { if (Current is { } c) _app.ShowTodo(c); };
        _report.Click += async (_, _) => await SendReportAsync();
        _peer.LinkClicked += (_, _) =>
        {
            if (Current?.Status?.Peer is { } peer)
                ReportDialog.Show(this, $"Отчёт программы: {peer.Name}", "Это состояние программы на другом компьютере, как она его прислала. Файлов проекта в отчёте нет.", peer.ToText());
        };
        Resize += (_, _) => FitLabels();
        FitLabels();
        FormClosing += (_, e) =>
        {
            if (e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); }
        };
        ReloadProjects();
        RenderAvailable();
    }

    void RenderAvailable()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action(RenderAvailable)); return; }
        _available.SuspendLayout();
        _available.Controls.Clear();
        var list = _app.Available;
        if (list.Count > 0)
        {
            _available.Controls.Add(new Label
            {
                Text = list.Count == 1 ? "На GitHub есть проект, которого у тебя нет:" : "На GitHub есть проекты, которых у тебя нет:",
                AutoSize = true, Padding = new Padding(0, 9, 0, 0),
            });
            foreach (var p in list.Take(4))
            {
                var project = p;
                var button = new Button { Text = $"Скачать «{project.Name}»", AutoSize = true, Height = 34 };
                button.Click += (_, _) => _app.Guard(() => _app.DownloadAsync(this, project));
                _available.Controls.Add(button);
            }
        }
        _available.Visible = list.Count > 0;
        _available.ResumeLayout();
        if (Current == null) Render();
    }

    public void SelectProject(ProjectController controller)
    {
        for (int i = 0; i < _projects.Items.Count; i++)
            if (_projects.Items[i] is ProjectItem item && item.Controller == controller) _projects.SelectedIndex = i;
    }

    /// <summary>A line under the buttons for things that are not an operation of the current project (downloads).</summary>
    public void ShowNote(string text, bool error = false)
    {
        _result.Text = text;
        _result.ForeColor = error ? Color.FromArgb(170, 30, 30) : Color.DimGray;
    }

    void FitLabels()
    {
        var width = Math.Max(300, ClientSize.Width - 40);
        _incoming.MaximumSize = _unsent.MaximumSize = _request.MaximumSize = _peer.MaximumSize = _result.MaximumSize = new Size(width, 0);
    }

    async Task PrepareAsync()
    {
        var c = Current;
        if (c == null || c.Busy) return;
        var setup = new DuoSync.Core.Setup.ProjectSetup(c.Engine.Repo, _app.Settings.MeName, _app.Settings.FriendDisplay);
        var plan = await setup.PlanAsync();
        var nl = Environment.NewLine;
        var text = "Что изменится (одним коммитом у тебя, на GitHub уйдёт при «Отправить»):" + nl + nl +
                   string.Join(nl, plan.Changes.Select(ch => $"• {ch.Path} — {ch.Description}")) +
                   (plan.Warnings.Count > 0 ? nl + nl + "Проверь в Unity:" + nl + string.Join(nl, plan.Warnings.Select(w => "• " + w)) : "");
        if (MessageBox.Show(this, text, "Подготовить проект", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        await RunAsync(_ => setup.ApplyAsync());
    }

    /// <summary>«Отправить отчёт»: the person sees everything that goes out, then it goes to their own status branch.</summary>
    async Task SendReportAsync()
    {
        var c = Current;
        if (c == null) return;
        _report.Enabled = false;
        try
        {
            var report = await c.BuildReportAsync();
            if (!ReportDialog.Show(this, "Отправить отчёт", $"Это увидит {_app.Friend}. Отчёт уходит на GitHub в служебную ветку и заменяет прошлый. " +
                    "Файлов проекта в нём нет, адреса почты, токены и имя пользователя Windows вычищены.", report.ToText(), "Отправить")) return;
            var r = await c.PublishReportAsync(interactive: true, report);
            ShowNote(r is { Ok: true } ? $"Отчёт отправлен, {_app.Friend} увидит его в окне программы." : "Отчёт не ушёл: нет связи с GitHub. Работа при этом не затронута.", r is not { Ok: true });
        }
        finally { _report.Enabled = true; }
    }

    ProjectController? Current => _projects.SelectedItem is ProjectItem item ? item.Controller : null;

    sealed record ProjectItem(ProjectController Controller)
    {
        public override string ToString() => Controller.Entry.Name;
    }

    public void ReloadProjects()
    {
        var selected = Current?.Entry.Path;
        _projects.Items.Clear();
        foreach (var c in _app.Controllers)
        {
            _projects.Items.Add(new ProjectItem(c));
            c.Changed -= OnControllerChanged;
            c.Changed += OnControllerChanged;
        }
        var index = _app.Controllers.FindIndex(c => c.Entry.Path == selected);
        _projects.SelectedIndex = _projects.Items.Count == 0 ? -1 : Math.Max(0, index);
        if (_projects.Items.Count == 0) Render();
    }

    void OnControllerChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action(OnControllerChanged)); return; }
        _ = ShowProjectAsync();
    }

    async Task<OpResult?> RunAsync(Func<SyncEngine, Task<OpResult>> op)
    {
        var c = Current;
        if (c == null || c.Busy) return null;
        var r = await c.RunAsync(op);
        _result.Text = r.Message;
        _result.ForeColor = r.Succeeded ? Color.FromArgb(30, 110, 50)
            : r.Status is OpStatus.Offline ? Color.DimGray
            : r.Status is OpStatus.Requested or OpStatus.Conflict ? Color.FromArgb(170, 90, 0)
            : Color.FromArgb(170, 30, 30);
        return r;
    }

    async Task ShowProjectAsync()
    {
        Render();
        var c = Current;
        if (c == null) return;
        var feed = await c.FeedAsync(200);
        _feed.BeginUpdate();
        _feed.Items.Clear();
        foreach (var e in feed.Reverse()) _feed.Items.Add($"{e.Utc.ToLocalTime():dd.MM HH:mm}  {e.Text}");
        _feed.EndUpdate();
    }

    void Render()
    {
        var c = Current;
        bool enabled = c != null && !c.Busy;
        bool notPrepared = c?.Status?.State == SyncState.NotPrepared;
        var outdated = !notPrepared && c != null && DuoSync.Core.Setup.ProjectSetup.IgnoreRulesOutdated(c.Entry.Path);
        _prepare.Visible = notPrepared || outdated;
        _prepare.Text = outdated ? "Обновить подготовку" : "Подготовить проект";
        _receive.Enabled = _send.Enabled = _undo.Enabled = enabled && !notPrepared;
        _check.Enabled = _prepare.Enabled = enabled;
        _todo.Enabled = _report.Enabled = c != null;
        _todo.Text = c?.TodoOpen is > 0 ? $"Черновик ({c.TodoOpen})" : "Черновик";
        if (c == null)
        {
            _incoming.Text = _app.Available.Count > 0
                ? "Проектов на этом компьютере пока нет. Скачай проект кнопкой выше."
                : "Проектов пока нет. Нажми «Добавить проект…».";
            _unsent.Text = _request.Text = "";
            _merge.Visible = _peer.Visible = false;
            return;
        }
        var st = c.Status;
        var request = st?.Requests.FirstOrDefault();
        _merge.Visible = c.Options.CanResolve && (c.LastResult?.Status == OpStatus.Conflict || request != null);
        _merge.Enabled = enabled;
        _request.Text = request != null
            ? $"{_app.Friend} просит слить «{request.Subject}»: {Ru.Files(request.Files.Count)} (с {request.At.ToLocalTime():dd.MM HH:mm}). Нажми «Слить с Claude»."
            : st?.RequestWaitingSince is { } since
                ? $"Твоя работа ждёт слияния с {since.ToLocalTime():dd.MM HH:mm}, сольёт {_app.Friend}. Работать можно дальше, лучше в других файлах."
                : "";
        var friend = _app.Friend;
        _peer.Visible = st?.Peer != null;
        if (st?.Peer is { } peer)
        {
            var what = peer.LastOp is { } op ? op.Message : "работает без ошибок";
            if (what.Length > 160) what = what[..157] + "…";
            _peer.Text = $"{peer.Name}: {what} (отчёт от {peer.Utc.ToLocalTime():dd.MM HH:mm}) — подробнее";
            _peer.LinkArea = new LinkArea(_peer.Text.Length - "подробнее".Length, "подробнее".Length);
            _peer.ForeColor = peer.HasProblem ? Color.FromArgb(170, 30, 30) : Color.DimGray;
        }
        _incoming.ForeColor = Icons.ColorOf(st?.State ?? SyncState.Busy);
        _incoming.Text = c.Busy ? "Идёт операция… " + (c.Progress ?? "") : st?.State switch
        {
            null => "Проверяю…",
            SyncState.Offline => "Нет связи с GitHub. Работа сохранена у тебя.",
            SyncState.AuthFailed => "GitHub не пускает: нужно войти в GitHub (git сам откроет окно входа при «Получить»).",
            SyncState.Rewritten => "История на GitHub переписана — обмен остановлен.",
            SyncState.Busy => "Проверяю…",
            SyncState.NotPrepared => "Проект ещё не подготовлен для DuoSync. Нажми «Подготовить проект» — это один раз.",
            SyncState.Incoming or SyncState.Both =>
                $"{friend} отправил {Quote(st.IncomingSubjects)}{Ru.Files(st.IncomingFiles.Count)}." +
                (st.Overlap.Count > 0 ? $" Пересекается с твоим неотправленным: {string.Join(", ", st.Overlap.Take(3))}." : " С твоими не пересекается."),
            _ => "Синхронно: у тебя то же, что на GitHub.",
        };
        _unsent.Text = st is { UnsentFiles.Count: > 0 }
            ? $"У тебя не отправлено: {Ru.Files(st.UnsentFiles.Count)} ({string.Join(", ", st.UnsentFiles.Take(3))}{(st.UnsentFiles.Count > 3 ? " и ещё " + (st.UnsentFiles.Count - 3) : "")})"
            : "";
        _receive.Text = st is { State: SyncState.Incoming or SyncState.Both } ? "Получить ↓" : "Получить";
        Icon = Icons.For(st?.State ?? SyncState.Busy);
    }

    static string Quote(IReadOnlyList<string> subjects) => subjects.Count == 0 ? "обнову: " : $"«{subjects[0]}»: ";
}
