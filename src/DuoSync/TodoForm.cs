using DuoSync.Core.GitHub;

namespace DuoSync;

/// <summary>
/// «Черновик» of one project: the two people's shared task list (kept in the repository's Issues). A tab per status;
/// a task moves between tabs when its status changes. Own tasks and the friend's are told apart by colour.
/// While the window is open it asks GitHub every 3 seconds with cheap conditional requests, so the friend's changes
/// appear by themselves. A change shows here at once and is sent right after; if GitHub refuses, the list returns
/// to what GitHub has.
/// </summary>
sealed class TodoForm : Form
{
    static readonly Color MineBack = Color.FromArgb(222, 235, 255);
    static readonly Color FriendBack = Color.FromArgb(255, 232, 204);
    static readonly TimeSpan PendingFor = TimeSpan.FromSeconds(20);

    readonly TrayContext _app;
    readonly ProjectController _project;
    readonly TextBox _input = new() { PlaceholderText = "Новая задача: напиши и нажми Enter", Dock = DockStyle.Fill };
    readonly ComboBox _who = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 120 };
    readonly Button _add = new() { Text = "Добавить", AutoSize = true };
    readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    readonly Dictionary<TodoStatus, (TabPage Page, ListView List, string Title)> _views = new();
    readonly Button _toTodo = new() { Text = "→ Надо сделать", AutoSize = true };
    readonly Button _toDoing = new() { Text = "→ В работе", AutoSize = true };
    readonly Button _toDone = new() { Text = "→ Готово", AutoSize = true };
    readonly Button _delete = new() { Text = "Удалить", AutoSize = true };
    readonly Label _state = new() { AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(0, 6, 0, 0) };
    readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };
    readonly SemaphoreSlim _gate = new(1, 1);

    string? _repo, _me;
    string? _openTag, _closedTag;
    IReadOnlyList<TodoItem> _open = Array.Empty<TodoItem>(), _closed = Array.Empty<TodoItem>();
    /// <summary>What the window shows: GitHub's lists plus changes made here that GitHub has not confirmed yet.</summary>
    List<TodoItem> _items = new();

    /// <summary>
    /// A change sent from here. For a second or two after a change GitHub still lists the old state; until the list
    /// shows it (or 20 s pass) the window keeps the change instead of flickering back. Expected null: deleted.
    /// </summary>
    sealed record Pending(DateTime At, int Number, TodoItem? Expected);
    readonly List<Pending> _pending = new();

    /// <summary>Who can be given a task: null login means me.</summary>
    sealed record Who(string? Login, string Text)
    {
        public override string ToString() => Text;
    }
    /// <summary>Everyone besides me who can be given a task in the repository (the friend, and whoever else is in the organization).</summary>
    IReadOnlyList<string>? _people;

    public TodoForm(TrayContext app, ProjectController project)
    {
        _app = app;
        _project = project;
        Text = $"Черновик — {project.Entry.Name}";
        Font = new Font("Segoe UI", 10f);
        ClientSize = new Size(620, 540);
        MinimumSize = new Size(460, 340);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = Icons.For(Core.Ops.SyncState.InSync);

        var top = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 4 };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 3; i++) top.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        top.Controls.Add(_input, 0, 0);
        top.Controls.Add(new Label { Text = "Делает:", AutoSize = true, Padding = new Padding(8, 6, 0, 0) }, 1, 0);
        top.Controls.Add(_who, 2, 0);
        top.Controls.Add(_add, 3, 0);

        foreach (var (status, title) in new[] { (TodoStatus.Todo, "Надо сделать"), (TodoStatus.Doing, "В работе"), (TodoStatus.Done, "Готово") })
        {
            var list = new ListView
            {
                View = View.Details, FullRowSelect = true, Dock = DockStyle.Fill, MultiSelect = false, HideSelection = false,
                ShowItemToolTips = true, HeaderStyle = ColumnHeaderStyle.Nonclickable,
            };
            list.Columns.Add("Задача", 400);
            list.Columns.Add("Делает", 110);
            list.Resize += (_, _) => FitColumns(list);
            list.SelectedIndexChanged += (_, _) => UpdateButtons();
            list.DoubleClick += async (_, _) => await RenameAsync();
            list.KeyDown += async (_, e) =>
            {
                if (e.KeyCode == Keys.Delete) await DeleteAsync();
                else if (e.KeyCode == Keys.F2) await RenameAsync();
            };
            list.ContextMenuStrip = BuildMenu();
            var page = new TabPage(title);
            page.Controls.Add(list);
            _tabs.TabPages.Add(page);
            _views[status] = (page, list, title);
        }
        _tabs.SelectedIndexChanged += (_, _) => UpdateButtons();

        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        bottom.Controls.AddRange(new Control[] { _toTodo, _toDoing, _toDone, _delete, Legend("мои", MineBack), Legend(_app.Friend, FriendBack) });

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, Padding = new Padding(10) };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(top);
        layout.Controls.Add(_tabs);
        layout.Controls.Add(bottom);
        layout.Controls.Add(_state);
        Controls.Add(layout);

        _who.Items.Add(new Who(null, "я"));
        _who.SelectedIndex = 0;
        AcceptButton = _add;
        _add.Click += async (_, _) => await AddAsync();
        _toTodo.Click += async (_, _) => await SetStatusAsync(TodoStatus.Todo);
        _toDoing.Click += async (_, _) => await SetStatusAsync(TodoStatus.Doing);
        _toDone.Click += async (_, _) => await SetStatusAsync(TodoStatus.Done);
        _delete.Click += async (_, _) => await DeleteAsync();
        _timer.Tick += async (_, _) => await RefreshAsync();
        Shown += async (_, _) =>
        {
            _input.Focus();
            _timer.Start();
            await RefreshAsync();
        };
        Activated += async (_, _) => await RefreshAsync();
        FormClosed += (_, _) =>
        {
            _timer.Stop();
            _timer.Dispose();
        };
        Render();
        State("Загружаю задачи…", false);
    }

    static Label Legend(string text, Color color) => new()
    {
        Text = "  " + text + "  ", AutoSize = true, BackColor = color, Margin = new Padding(12, 9, 0, 0), Padding = new Padding(2),
    };

    ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Надо сделать", null, async (_, _) => await SetStatusAsync(TodoStatus.Todo));
        menu.Items.Add("В работе", null, async (_, _) => await SetStatusAsync(TodoStatus.Doing));
        menu.Items.Add("Готово", null, async (_, _) => await SetStatusAsync(TodoStatus.Done));
        menu.Items.Add(new ToolStripSeparator());
        var who = new ToolStripMenuItem("Делает");
        menu.Items.Add(who);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Изменить текст…", null, async (_, _) => await RenameAsync());
        menu.Items.Add("Удалить", null, async (_, _) => await DeleteAsync());
        menu.Opening += (_, e) =>
        {
            var t = Selected;
            if (t is not { Number: > 0 }) { e.Cancel = true; return; }
            who.DropDownItems.Clear();
            foreach (var person in People())
            {
                var login = person.Login ?? _me;
                var item = new ToolStripMenuItem(person.Text)
                {
                    Checked = string.Equals(t.Owner, login, StringComparison.OrdinalIgnoreCase) || (person.Login == null && IsMine(t)),
                };
                item.Click += async (_, _) => await ReassignAsync(login);
                who.DropDownItems.Add(item);
            }
        };
        return menu;
    }

    /// <summary>Me, the friend (by name once the login is known), then anyone else who can be given a task.</summary>
    IEnumerable<Who> People()
    {
        yield return new Who(null, "я");
        if (FriendLogin != null) yield return new Who(FriendLogin, _app.Friend);
        foreach (var login in _people ?? Array.Empty<string>())
            if (!string.Equals(login, FriendLogin, StringComparison.OrdinalIgnoreCase)) yield return new Who(login, login);
    }

    void FillWho()
    {
        var chosen = (_who.SelectedItem as Who)?.Login;
        _who.BeginUpdate();
        _who.Items.Clear();
        foreach (var person in People()) _who.Items.Add(person);
        _who.SelectedIndex = Math.Max(0, _who.Items.Cast<Who>().ToList().FindIndex(p => p.Login == chosen));
        _who.EndUpdate();
    }

    string? FriendLogin => _project.Entry.FriendLogin;

    /// <summary>Until the window is shown the tab control has no selected tab yet: the first one counts.</summary>
    ListView CurrentList => _views.Values.FirstOrDefault(v => v.Page == _tabs.SelectedTab).List ?? _views[TodoStatus.Todo].List;

    TodoItem? Selected => CurrentList.SelectedItems.Count > 0 ? CurrentList.SelectedItems[0].Tag as TodoItem : null;

    bool IsMine(TodoItem t) => t.Owner.Length == 0 || string.Equals(t.Owner, _me, StringComparison.OrdinalIgnoreCase);

    string OwnerName(TodoItem t) => IsMine(t) ? "я"
        : string.Equals(t.Owner, FriendLogin, StringComparison.OrdinalIgnoreCase) ? _app.Friend
        : t.Owner;

    // ------------------------------------------------------------------ Чтение

    async Task RefreshAsync(bool force = false)
    {
        if (IsDisposed || !await _gate.WaitAsync(0)) return;
        try
        {
            var github = await _app.GitHubAsync();
            if (github == null)
            {
                State("Нет входа в GitHub. Программа возьмёт его у git, как только git войдёт в GitHub.", true);
                return;
            }
            _repo ??= await _app.RepositoryOfAsync(_project);
            if (_repo == null)
            {
                State("Проект не связан с репозиторием на GitHub, задачам негде храниться.", true);
                return;
            }
            _me ??= await github.LoginAsync();
            if (force) _openTag = _closedTag = null;
            var open = await github.OpenTodosAsync(_repo, _openTag);
            var closed = await github.ClosedTodosAsync(_repo, _closedTag);
            if (open != null) { _open = open.Items; _openTag = open.ETag; }
            if (closed != null) { _closed = closed.Items; _closedTag = closed.ETag; }
            if (FriendLogin == null && (open != null || closed != null)) await LearnFriendAsync(github);
            if (_people == null)
            {
                try
                {
                    _people = await github.PeopleAsync(_repo);
                    FillWho();
                }
                catch (GitHubException) { } // next refresh tries again
            }
            if (open != null || closed != null || force) Rebuild();
            State(_items.Count == 0 ? "Задач пока нет: напиши первую в поле сверху." : $"Обновлено в {DateTime.Now:HH:mm:ss}, изменения друга появляются сами.", false);
        }
        catch (GitHubException e) when (e.Kind == GitHubError.Gone && _repo != null)
        {
            var github = await _app.GitHubAsync();
            if (github != null && await github.EnableIssuesAsync(_repo))
            {
                State("Включил задачи у репозитория на GitHub.", false);
                _openTag = _closedTag = null;
            }
            else State($"На GitHub у проекта выключены задачи (Issues). Включить их может только создатель репозитория: пусть {_app.Friend} откроет «Черновик» у себя.", true);
        }
        catch (GitHubException e)
        {
            State(e.Kind == GitHubError.Network ? "Нет связи с GitHub, повторю через 3 с." : e.Message, true);
        }
        finally { _gate.Release(); }
    }

    /// <summary>The friend's GitHub login, for colours and for giving them a task. Remembered in the settings.</summary>
    async Task LearnFriendAsync(GitHubClient github)
    {
        try
        {
            if (await github.FriendLoginAsync(_repo!, _open.Concat(_closed)) is not { } login) return;
            _project.Entry.FriendLogin = login;
            _app.Settings.Save();
            FillWho();
        }
        catch (GitHubException) { } // next refresh tries again
    }

    /// <summary>Rebuilds the lists from GitHub's two lists (open tasks, recently finished ones) and own unconfirmed changes.</summary>
    void Rebuild()
    {
        var items = _open.Concat(_closed.Where(t => t.Status == TodoStatus.Done)).ToList();
        _pending.RemoveAll(p => DateTime.UtcNow - p.At > PendingFor);
        foreach (var p in _pending.ToList())
        {
            var i = items.FindIndex(t => t.Number == p.Number);
            if (p.Expected == null)
            {
                if (i < 0) _pending.Remove(p);
                else items.RemoveAt(i);
            }
            else if (i >= 0 && items[i].Status == p.Expected.Status && items[i].Title == p.Expected.Title && items[i].Owner == p.Expected.Owner)
                _pending.Remove(p);
            else if (i >= 0) items[i] = p.Expected;
            else items.Add(p.Expected);
        }
        _items = items;
        Render();
    }

    void Render()
    {
        if (IsDisposed) return;
        var recentlyDone = _pending.Where(p => p.Expected?.Status == TodoStatus.Done).Select(p => p.Number).ToHashSet();
        foreach (var (status, view) in _views)
        {
            var items = _items.Where(t => t.Status == status);
            // Unfinished in the order they were added; finished with the latest on top, as GitHub lists them.
            items = status == TodoStatus.Done
                ? items.OrderByDescending(t => recentlyDone.Contains(t.Number))
                : items.OrderBy(t => t.Number == 0 ? int.MaxValue : t.Number);
            var shown = items.ToList();
            var selected = view.List.SelectedItems.Count > 0 ? (view.List.SelectedItems[0].Tag as TodoItem)?.Number : null;
            view.List.BeginUpdate();
            view.List.Items.Clear();
            foreach (var t in shown)
            {
                var row = new ListViewItem(t.Title)
                {
                    Tag = t,
                    BackColor = IsMine(t) ? MineBack : FriendBack,
                    ForeColor = t.Number == 0 ? Color.Gray : status == TodoStatus.Done ? Color.DimGray : SystemColors.WindowText,
                    ToolTipText = t.Number == 0 ? "отправляю на GitHub…" : $"№{t.Number}" + (t.Author.Length > 0 ? $", написал {t.Author}" : ""),
                };
                row.SubItems.Add(OwnerName(t));
                view.List.Items.Add(row);
                if (selected != null && t.Number == selected) row.Selected = true;
            }
            view.List.EndUpdate();
            view.Page.Text = $"{view.Title} ({shown.Count})";
            FitColumns(view.List);
        }
        UpdateButtons();
        _project.SetTodoOpen(_items.Count(t => t.Status != TodoStatus.Done));
    }

    static void FitColumns(ListView list)
    {
        if (list.Columns.Count < 2) return;
        list.Columns[1].Width = 110;
        list.Columns[0].Width = Math.Max(120, list.ClientSize.Width - list.Columns[1].Width - 6);
    }

    void UpdateButtons()
    {
        var t = Selected;
        var usable = t is { Number: > 0 };
        _toTodo.Enabled = usable && t!.Status != TodoStatus.Todo;
        _toDoing.Enabled = usable && t!.Status != TodoStatus.Doing;
        _toDone.Enabled = usable && t!.Status != TodoStatus.Done;
        _delete.Enabled = usable;
    }

    void State(string text, bool problem)
    {
        if (IsDisposed) return;
        _state.Text = text;
        _state.ForeColor = problem ? Color.FromArgb(170, 30, 30) : Color.DimGray;
    }

    // ------------------------------------------------------------------ Изменения

    /// <summary>Shows the change at once, then sends it; afterwards the list is read again from GitHub.</summary>
    async Task ChangeAsync(Action show, Func<GitHubClient, string, Task<Pending>> send)
    {
        await _gate.WaitAsync();
        try
        {
            var github = await _app.GitHubAsync(interactive: true);
            _repo ??= await _app.RepositoryOfAsync(_project);
            if (github == null || _repo == null)
            {
                State(github == null ? "Нет входа в GitHub, изменение не сохранено." : "Проект не связан с репозиторием на GitHub.", true);
                return;
            }
            _me ??= await github.LoginAsync();
            show();
            Render();
            try { _pending.Add(await send(github, _repo)); }
            catch (GitHubException e) { State("Не сохранилось на GitHub: " + e.Message, true); }
            _openTag = _closedTag = null;
        }
        finally { _gate.Release(); }
        await RefreshAsync(force: true);
    }

    async Task AddAsync()
    {
        var title = _input.Text.Trim();
        if (title.Length == 0) return;
        _input.Clear();
        var assignee = (_who.SelectedItem as Who)?.Login;
        var pending = new TodoItem(0, title, TodoStatus.Todo, new[] { GitHubClient.TodoLabel }, "", DateTimeOffset.Now, assignee);
        _tabs.SelectedTab = _views[TodoStatus.Todo].Page;
        await ChangeAsync(() => _items.Add(pending), async (github, repo) =>
        {
            var created = await github.AddTodoAsync(repo, title, assignee);
            return new Pending(DateTime.UtcNow, created.Number, created);
        });
    }

    async Task SetStatusAsync(TodoStatus status)
    {
        if (Selected is not { Number: > 0 } t || t.Status == status) return;
        await ChangeAsync(() => Replace(t, t with { Status = status }), async (github, repo) =>
        {
            await github.UpdateTodoAsync(repo, t, status: status);
            return new Pending(DateTime.UtcNow, t.Number, t with { Status = status });
        });
    }

    async Task ReassignAsync(string? login)
    {
        if (Selected is not { Number: > 0 } t || login == null || string.Equals(t.Owner, login, StringComparison.OrdinalIgnoreCase)) return;
        await ChangeAsync(() => Replace(t, t with { Assignee = login }), async (github, repo) =>
        {
            await github.UpdateTodoAsync(repo, t, assignee: login);
            return new Pending(DateTime.UtcNow, t.Number, t with { Assignee = login });
        });
    }

    async Task DeleteAsync()
    {
        if (Selected is not { Number: > 0 } t) return;
        await ChangeAsync(() => _items.Remove(t), async (github, repo) =>
        {
            await github.UpdateTodoAsync(repo, t, delete: true);
            return new Pending(DateTime.UtcNow, t.Number, null);
        });
    }

    async Task RenameAsync()
    {
        if (Selected is not { Number: > 0 } t) return;
        var title = Prompt.Ask(this, "Черновик", "Задача:", t.Title);
        if (title == null || title == t.Title) return;
        await ChangeAsync(() => Replace(t, t with { Title = title }), async (github, repo) =>
        {
            await github.UpdateTodoAsync(repo, t, title: title);
            return new Pending(DateTime.UtcNow, t.Number, t with { Title = title });
        });
    }

    void Replace(TodoItem old, TodoItem now)
    {
        var i = _items.IndexOf(old);
        if (i >= 0) _items[i] = now;
    }
}
