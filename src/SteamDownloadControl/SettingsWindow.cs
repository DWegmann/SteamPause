namespace SteamDownloadControl;

internal sealed class SettingsWindow : Form
{
    private readonly ListView list = new() { View = View.Details, FullRowSelect = true, MultiSelect = false, Dock = DockStyle.Fill };
    private readonly CheckBox automation = new() { Text = "Automatik aktiv", AutoSize = true };
    private readonly CheckBox startup = new() { Text = "Bei der Windows-Anmeldung starten", AutoSize = true };
    private readonly CheckBox scheduledUpdates = new()
    {
        Text = "Geplante Steam-Updates bei freier Warteschlange vorziehen", AutoSize = true
    };
    private readonly Label status = new() { Text = "Bereit.", Dock = DockStyle.Fill, AutoEllipsis = true };
    private readonly Action<Settings> save;
    private Settings settings = new();
    private bool refreshing;

    public SettingsWindow(Action<Settings> save)
    {
        this.save = save;
        Text = Program.AppName;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(770, 440);
        MinimumSize = new Size(610, 370);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = SystemFonts.MessageBoxFont;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 1, RowCount = 6 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        Controls.Add(layout);
        layout.Controls.Add(new Label { AutoSize = true, Margin = new Padding(0, 0, 0, 12),
            Text = "Steam-Downloads pausieren oder begrenzen, solange ein hinterlegtes Programm läuft." }, 0, 0);
        list.Columns.Add("Programm", 135);
        list.Columns.Add("Aktion", 95);
        list.Columns.Add("EXE-Pfad", 485);
        list.DoubleClick += (_, _) => EditRule();
        layout.Controls.Add(list, 0, 1);

        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false };
        buttons.Controls.Add(Button("EXE hinzufügen", ChooseFile));
        buttons.Controls.Add(Button("Laufendes Programm …", ChooseRunning));
        var edit = Button("Bearbeiten", EditRule);
        var remove = Button("Entfernen", RemoveRule);
        edit.Enabled = remove.Enabled = false;
        list.SelectedIndexChanged += (_, _) => edit.Enabled = remove.Enabled = list.SelectedItems.Count == 1;
        buttons.Controls.Add(edit);
        buttons.Controls.Add(remove);
        layout.Controls.Add(buttons, 0, 2);

        var options = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, WrapContents = false,
            Margin = new Padding(0, 5, 0, 0) };
        options.Controls.Add(automation);
        options.Controls.Add(startup);
        layout.Controls.Add(options, 0, 3);
        scheduledUpdates.Margin = new Padding(3, 3, 0, 10);
        layout.Controls.Add(scheduledUpdates, 0, 4);
        layout.Controls.Add(status, 0, 5);
        automation.CheckedChanged += (_, _) =>
        {
            if (!refreshing)
                save(settings with { Enabled = automation.Checked });
        };
        scheduledUpdates.CheckedChanged += (_, _) =>
        {
            if (!refreshing)
                save(settings with { StartScheduledUpdates = scheduledUpdates.Checked });
        };
        try { startup.Checked = Startup.Initialize(); }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException
            or IOException)
        {
            startup.Enabled = false;
        }
        startup.CheckedChanged += (_, _) => ChangeStartup();
    }

    public void RefreshSettings(Settings value)
    {
        settings = value;
        refreshing = true;
        automation.Checked = value.Enabled;
        scheduledUpdates.Checked = value.StartScheduledUpdates;
        scheduledUpdates.Enabled = value.Enabled;
        list.BeginUpdate();
        list.Items.Clear();
        foreach (var rule in value.Rules)
            list.Items.Add(new ListViewItem([rule.Name, rule.Action, rule.Path]) { Tag = rule });
        list.EndUpdate();
        refreshing = false;
    }

    public void ShowStatus(MonitorResult result)
    {
        status.Text = result.Detail;
        status.ForeColor = result.Attention ? Color.DarkRed : SystemColors.ControlText;
    }

    private void ChangeStartup()
    {
        if (refreshing)
            return;
        try { Startup.SetEnabled(startup.Checked); }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException
            or IOException or InvalidOperationException)
        {
            refreshing = true;
            startup.Checked = !startup.Checked;
            refreshing = false;
            MessageBox.Show(this, error.Message, "Autostart", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void ChooseFile()
    {
        using var picker = new OpenFileDialog { Filter = "Windows-Programme (*.exe)|*.exe", CheckFileExists = true };
        if (picker.ShowDialog(this) == DialogResult.OK)
            ConfigureRule(picker.FileName);
    }

    private void ChooseRunning()
    {
        var iconSize = LogicalToDeviceUnits(16);
        using var icons = new ImageList { ColorDepth = ColorDepth.Depth32Bit, ImageSize = new Size(iconSize, iconSize) };
        icons.Images.Add(SystemIcons.Application);
        var programs = Processes.AvailablePaths()
            .OrderBy(path => Path.GetFileName(path), Processes.PathComparer)
            .Select(path => (Path: path, Name: Path.GetFileName(path), Icon: AddProgramIcon(icons, path)))
            .ToArray();

        using var picker = Dialog("Laufendes Programm wählen", new Size(680, 380));
        picker.MinimumSize = new Size(410, 255);
        var search = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "Nach Programmname oder Pfad suchen …",
            AccessibleName = "Programm suchen", TabIndex = 0 };
        var paths = new ListView { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false,
            SmallImageList = icons, ShowItemToolTips = true, HeaderStyle = ColumnHeaderStyle.Nonclickable, TabIndex = 1 };
        paths.Columns.Add("Programm", 200);
        paths.Columns.Add("EXE-Pfad", 430);
        var matches = new Label { AutoSize = true, ForeColor = SystemColors.GrayText };

        var choose = Button("Auswählen", () => picker.DialogResult = DialogResult.OK);
        choose.Enabled = false;
        paths.SelectedIndexChanged += (_, _) => choose.Enabled = paths.SelectedItems.Count == 1;
        paths.MouseDoubleClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left && paths.GetItemAt(e.X, e.Y) is not null)
                choose.PerformClick();
        };
        search.TextChanged += (_, _) => FilterPrograms();
        search.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Down || paths.Items.Count == 0)
                return;

            paths.Items[0].Selected = paths.Items[0].Focused = true;
            paths.Items[0].EnsureVisible();
            paths.Focus();
            e.SuppressKeyPress = true;
        };

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(8) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(search, 0, 0);
        layout.Controls.Add(paths, 0, 1);
        layout.Controls.Add(matches, 0, 2);
        picker.Controls.Add(layout);
        AddDialogButtons(picker, choose);
        picker.ActiveControl = search;
        FilterPrograms();

        if (picker.ShowDialog(this) == DialogResult.OK && paths.SelectedItems.Count == 1 &&
            paths.SelectedItems[0].Tag is string path)
            ConfigureRule(path);

        void FilterPrograms()
        {
            var query = search.Text.Trim();
            paths.BeginUpdate();
            paths.Items.Clear();
            foreach (var program in programs.Where(program => program.Path.Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                paths.Items.Add(new ListViewItem([program.Name, program.Path], program.Icon)
                {
                    Tag = program.Path,
                    ToolTipText = program.Path
                });
            }
            paths.EndUpdate();
            choose.Enabled = false;
            matches.Text = paths.Items.Count == 0 ? "Keine Programme gefunden." : $"{paths.Items.Count} Treffer";
        }
    }

    private static int AddProgramIcon(ImageList icons, string path)
    {
        try
        {
            using var icon = Icon.ExtractIcon(path, 0, icons.ImageSize.Width);
            if (icon is not null)
            {
                var index = icons.Images.Count;
                icons.Images.Add(icon);
                return index;
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The program may have exited or its executable may be inaccessible.
        }
        return 0;
    }

    private void EditRule()
    {
        if (SelectedRule() is { } rule)
            ConfigureRule(rule.Path);
    }

    private void RemoveRule()
    {
        if (SelectedRule() is { } rule)
            save(settings with { Rules = settings.Rules.Where(item => item != rule).ToList() });
    }

    private Rule? SelectedRule() => list.SelectedItems.Count == 1 ? list.SelectedItems[0].Tag as Rule : null;

    private void ConfigureRule(string path)
    {
        path = Path.GetFullPath(path);
        var existing = settings.Rules.Find(rule => Processes.PathComparer.Equals(rule.Path, path));
        using var editor = Dialog(Path.GetFileName(path), new Size(445, 145));
        editor.FormBorderStyle = FormBorderStyle.FixedDialog;
        var pause = new RadioButton { Text = "Steam-Downloads pausieren", AutoSize = true, Checked = existing is { KilobitsPerSecond: null } };
        var limit = new RadioButton { Text = "Begrenzen auf", AutoSize = true, Checked = !pause.Checked };
        var rate = new NumericUpDown { DecimalPlaces = 3, Minimum = 0.001m, Maximum = 1000,
            Increment = 0.1m, Width = 115, Value = existing?.KilobitsPerSecond / 8000m ?? 1m, Enabled = limit.Checked };
        limit.CheckedChanged += (_, _) => rate.Enabled = limit.Checked;
        var body = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 2,
            Padding = new Padding(8) };
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        body.RowStyles.Add(new RowStyle(SizeType.Absolute, 35));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        body.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        body.Controls.Add(pause, 0, 0);
        body.SetColumnSpan(pause, 3);
        body.Controls.Add(limit, 0, 1);
        body.Controls.Add(rate, 1, 1);
        body.Controls.Add(new Label { Text = "MB/s", AutoSize = true, Margin = new Padding(4, 6, 0, 0) }, 2, 1);
        editor.Controls.Add(body);
        AddDialogButtons(editor, Button("Speichern", () => editor.DialogResult = DialogResult.OK));
        if (editor.ShowDialog(this) != DialogResult.OK)
            return;

        var replacement = new Rule(path, pause.Checked ? null : (int)Math.Round(rate.Value * 8000m));
        var rules = settings.Rules.ToList();
        if (existing is null)
            rules.Add(replacement);
        else
            rules[rules.IndexOf(existing)] = replacement;
        save(settings with { Rules = rules });
    }

    private static Form Dialog(string title, Size size) => new()
    {
        Text = title, ClientSize = size, StartPosition = FormStartPosition.CenterParent,
        MinimizeBox = false, MaximizeBox = false, ShowInTaskbar = false, AutoScaleMode = AutoScaleMode.Dpi,
        Font = SystemFonts.MessageBoxFont
    };

    private static Button Button(string title, Action click)
    {
        var button = new Button { Text = title, AutoSize = true };
        button.Click += (_, _) => click();
        return button;
    }

    private static void AddDialogButtons(Form form, Button accept)
    {
        var row = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Bottom, FlowDirection = FlowDirection.RightToLeft };
        var cancel = new Button { Text = "Abbrechen", DialogResult = DialogResult.Cancel, AutoSize = true };
        row.Controls.Add(cancel);
        row.Controls.Add(accept);
        form.Controls.Add(row);
        form.CancelButton = cancel;
        form.AcceptButton = accept;
    }
}
