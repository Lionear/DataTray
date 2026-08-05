using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DataTray.Sdk.Ui;
using Microsoft.Data.SqlClient;

namespace DataTray.Providers.MsSql;

/// <summary>
/// The Schedules page of <see cref="AgentJobPropertiesView"/> (SE-235): the schedules attached to a job, and
/// the recurrence editor behind them.
/// </summary>
/// <remarks>
/// SSMS splits the choice in two — a "schedule type" and then a frequency — which means picking "Recurring"
/// before you can say "weekly". One combo carries both here; the set of things you can express is the same.
///
/// A schedule in msdb is a shared object that jobs are attached to, not a property of the job, so removing
/// one detaches it and lets <c>sp_detach_schedule @delete_unused_schedule = 1</c> clean it up only when no
/// other job still uses it. Fields that do not apply to the chosen frequency are hidden rather than disabled,
/// because a greyed-out weekday picker on a monthly schedule is noise.
/// </remarks>
internal sealed class AgentJobSchedulesPage
{
    private static readonly (int Value, string Label)[] Frequencies =
    [
        (AgentScheduleText.Daily, "Daily"),
        (AgentScheduleText.Weekly, "Weekly"),
        (AgentScheduleText.Monthly, "Monthly, on a day of the month"),
        (AgentScheduleText.MonthlyRelative, "Monthly, on a relative day"),
        (AgentScheduleText.Once, "One time"),
        (AgentScheduleText.OnAgentStart, "When SQL Server Agent starts"),
        (AgentScheduleText.OnIdle, "When the CPUs become idle")
    ];

    private static readonly (int Value, string Label)[] SubdayTypes =
    [
        (1, "Once, at"),
        (8, "Every … hours"),
        (4, "Every … minutes"),
        (2, "Every … seconds")
    ];

    private static readonly (int Bit, string Name)[] WeekDays =
    [
        (1, "Sun"), (2, "Mon"), (4, "Tue"), (8, "Wed"), (16, "Thu"), (32, "Fri"), (64, "Sat")
    ];

    private static readonly (int Value, string Label)[] Ordinals =
    [
        (1, "First"), (2, "Second"), (4, "Third"), (8, "Fourth"), (16, "Last")
    ];

    private static readonly (int Value, string Label)[] RelativeDays =
    [
        (1, "Sunday"), (2, "Monday"), (3, "Tuesday"), (4, "Wednesday"), (5, "Thursday"), (6, "Friday"),
        (7, "Saturday"), (8, "day"), (9, "weekday"), (10, "weekend day")
    ];

    private readonly NodeInfoContext _context;
    private readonly string _job;

    private readonly ListBox _list = new() { Height = 120 };
    private readonly TextBlock _status = new() { Opacity = 0.75, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, FontWeight = FontWeight.SemiBold };

    private readonly TextBox _name = new();
    private readonly CheckBox _enabled = new() { Content = "Enabled" };
    private readonly ComboBox _frequency = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly NumericUpDown _recurrence = new() { Minimum = 1, Maximum = 999, Width = 100, Value = 1 };
    private readonly TextBlock _recurrenceUnit = new() { VerticalAlignment = VerticalAlignment.Bottom, Opacity = 0.65 };
    private readonly List<CheckBox> _weekDays = [];
    private readonly NumericUpDown _dayOfMonth = new() { Minimum = 1, Maximum = 31, Width = 100, Value = 1 };
    private readonly ComboBox _ordinal = new() { Width = 120 };
    private readonly ComboBox _relativeDay = new() { Width = 150 };
    private readonly ComboBox _subdayType = new() { Width = 170 };
    private readonly NumericUpDown _subdayInterval = new() { Minimum = 1, Maximum = 999, Width = 90, Value = 1 };
    private readonly TextBox _startTime = new() { Width = 110, Text = "00:00:00" };
    private readonly TextBox _endTime = new() { Width = 110, Text = "23:59:59" };
    private readonly TextBox _startDate = new() { Width = 130 };
    private readonly TextBox _endDate = new() { Width = 130 };

    private Control _weeklyRow = null!;
    private Control _monthlyRow = null!;
    private Control _relativeRow = null!;
    private Control _recurrenceRow = null!;
    private Control _timeRow = null!;
    private Control _startRow = null!;
    private Control _windowRow = null!;

    private List<Schedule> _schedules = [];
    private bool _loading;

    public AgentJobSchedulesPage(NodeInfoContext context)
    {
        _context = context;
        _job = context.Node.Name;
        Control = Build();
        _ = ReloadAsync(select: 0);
    }

    public Control Control { get; }

    private sealed record Schedule(
        int Id, string Name, bool Enabled, int FreqType, int FreqInterval, int SubdayType, int SubdayInterval,
        int RelativeInterval, int RecurrenceFactor, int StartDate, int EndDate, int StartTime, int EndTime);

    private Control Build()
    {
        _list.SelectionChanged += (_, _) => ShowSelected();
        _frequency.ItemsSource = Frequencies.Select(f => f.Label).ToList();
        _subdayType.ItemsSource = SubdayTypes.Select(s => s.Label).ToList();
        _ordinal.ItemsSource = Ordinals.Select(o => o.Label).ToList();
        _relativeDay.ItemsSource = RelativeDays.Select(d => d.Label).ToList();
        _frequency.SelectionChanged += (_, _) => SyncVisibility();
        _subdayType.SelectionChanged += (_, _) => SyncVisibility();

        foreach (var (_, day) in WeekDays)
        {
            _weekDays.Add(new CheckBox { Content = day });
        }

        var add = new Button { Content = "New" };
        var remove = new Button { Content = "Remove" };
        var save = new Button { Content = "Save schedule", HorizontalAlignment = HorizontalAlignment.Right };

        add.Click += (_, _) => NewSchedule();
        remove.Click += async (_, _) => await GuardAsync(remove, RemoveSelectedAsync);
        save.Click += async (_, _) => await GuardAsync(save, SaveSelectedAsync);

        var dayBoxes = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        foreach (var box in _weekDays)
        {
            dayBoxes.Children.Add(box);
        }

        _recurrenceRow = Row("Recurs every", _recurrence, _recurrenceUnit);
        _weeklyRow = new StackPanel
        {
            Spacing = 2,
            Children = { new TextBlock { Text = "On these days", Opacity = 0.65 }, dayBoxes }
        };
        _monthlyRow = Row("Day of the month", _dayOfMonth);
        _relativeRow = Row("On the", _ordinal, _relativeDay);
        // Each control sits in exactly one row: the start time is always shown when the schedule has a time
        // at all, and the end time only when it repeats inside a window.
        _timeRow = Row("Occurs", _subdayType, _subdayInterval);
        _startRow = Row("At / from", _startTime);
        _windowRow = Row("Until", _endTime);

        var form = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                Labelled("Name", _name),
                _enabled,
                Labelled("Frequency", _frequency),
                _recurrenceRow,
                _weeklyRow,
                _monthlyRow,
                _relativeRow,
                _timeRow,
                _startRow,
                _windowRow,
                Row("Starts / ends (yyyy-mm-dd)", _startDate, _endDate),
                _summary,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8, Children = { _status, save }
                }
            }
        };

        var page = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                _list,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { add, remove } },
                form
            }
        };

        return new ScrollViewer { Content = page, Padding = new Thickness(12) };
    }

    private static Control Labelled(string label, Control editor) => new StackPanel
    {
        Spacing = 2,
        Children = { new TextBlock { Text = label, Opacity = 0.65 }, editor }
    };

    private static Control Row(string label, params Control[] controls)
    {
        var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var control in controls)
        {
            line.Children.Add(control);
        }

        return new StackPanel
        {
            Spacing = 2,
            Children = { new TextBlock { Text = label, Opacity = 0.65 }, line }
        };
    }

    private async Task GuardAsync(Button button, Func<Task> action)
    {
        button.IsEnabled = false;
        try
        {
            await action();
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
        finally
        {
            button.IsEnabled = true;
        }
    }

    // ── Load ─────────────────────────────────────────────────────────────────────────────────────────

    private async Task ReloadAsync(int select)
    {
        try
        {
            await using var connection = await OpenAsync();
            await using var command = new SqlCommand(
                """
                SELECT s.schedule_id, s.name, s.enabled, s.freq_type, s.freq_interval, s.freq_subday_type,
                       s.freq_subday_interval, s.freq_relative_interval, s.freq_recurrence_factor,
                       s.active_start_date, s.active_end_date, s.active_start_time, s.active_end_time
                FROM msdb.dbo.sysschedules s
                JOIN msdb.dbo.sysjobschedules js ON js.schedule_id = s.schedule_id
                JOIN msdb.dbo.sysjobs j ON j.job_id = js.job_id
                WHERE j.name = @name
                ORDER BY s.name
                """, connection);
            command.Parameters.AddWithValue("name", _job);

            var schedules = new List<Schedule>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    schedules.Add(new Schedule(
                        reader.GetInt32(0), reader.GetString(1), reader.GetByte(2) == 1, reader.GetInt32(3),
                        reader.GetInt32(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt32(7),
                        reader.GetInt32(8), reader.GetInt32(9), reader.GetInt32(10), reader.GetInt32(11),
                        reader.GetInt32(12)));
                }
            }

            Dispatcher.UIThread.Post(() =>
            {
                _schedules = schedules;
                _list.ItemsSource = schedules.Select(Line).ToList();
                _list.SelectedIndex = schedules.Count == 0 ? -1 : Math.Clamp(select, 0, schedules.Count - 1);
                _status.Text = schedules.Count == 0 ? "This job has no schedules." : "";
                if (schedules.Count == 0)
                {
                    _summary.Text = "";
                }
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => _status.Text = ex.Message);
        }
    }

    private static string Line(Schedule s) =>
        $"{s.Name}{(s.Enabled ? "" : "  (disabled)")} — {Describe(s)}";

    private static string Describe(Schedule s) => AgentScheduleText.Describe(
        s.FreqType, s.FreqInterval, s.SubdayType, s.SubdayInterval, s.RelativeInterval, s.RecurrenceFactor,
        s.StartDate, s.StartTime, s.EndTime);

    private void ShowSelected()
    {
        if (_list.SelectedIndex < 0 || _list.SelectedIndex >= _schedules.Count)
        {
            return;
        }

        _loading = true;
        var s = _schedules[_list.SelectedIndex];
        _name.Text = s.Name;
        _enabled.IsChecked = s.Enabled;
        _frequency.SelectedIndex = Math.Max(0, Array.FindIndex(Frequencies, f => f.Value == s.FreqType));
        _recurrence.Value = Math.Max(1, s.RecurrenceFactor);
        for (var i = 0; i < _weekDays.Count; i++)
        {
            _weekDays[i].IsChecked = s.FreqType == AgentScheduleText.Weekly && (s.FreqInterval & WeekDays[i].Bit) != 0;
        }

        _dayOfMonth.Value = s.FreqType == AgentScheduleText.Monthly ? Math.Clamp(s.FreqInterval, 1, 31) : 1;
        _ordinal.SelectedIndex = Math.Max(0, Array.FindIndex(Ordinals, o => o.Value == s.RelativeInterval));
        _relativeDay.SelectedIndex = Math.Max(0, Array.FindIndex(RelativeDays, d => d.Value == s.FreqInterval));
        _subdayType.SelectedIndex = Math.Max(0, Array.FindIndex(SubdayTypes, t => t.Value == s.SubdayType));
        _subdayInterval.Value = Math.Max(1, s.SubdayInterval);
        _startTime.Text = Clock(s.StartTime);
        _endTime.Text = Clock(s.EndTime);
        _startDate.Text = Stamp(s.StartDate);
        _endDate.Text = s.EndDate is 0 or 99991231 ? "" : Stamp(s.EndDate);
        _loading = false;

        SyncVisibility();
    }

    private void NewSchedule()
    {
        _loading = true;
        _list.SelectedIndex = -1;
        _name.Text = $"{_job} schedule";
        _enabled.IsChecked = true;
        _frequency.SelectedIndex = 0;
        _recurrence.Value = 1;
        foreach (var box in _weekDays)
        {
            box.IsChecked = false;
        }

        _dayOfMonth.Value = 1;
        _ordinal.SelectedIndex = 0;
        _relativeDay.SelectedIndex = 0;
        _subdayType.SelectedIndex = 0;
        _subdayInterval.Value = 1;
        _startTime.Text = "00:00:00";
        _endTime.Text = "23:59:59";
        _startDate.Text = "";
        _endDate.Text = "";
        _loading = false;

        SyncVisibility();
        _status.Text = "New schedule — Save schedule creates it and attaches it to this job.";
    }

    // Only show what the chosen frequency actually uses; a greyed-out weekday picker on a monthly schedule
    // is noise, and a visible one that does nothing is a lie.
    private void SyncVisibility()
    {
        if (_loading)
        {
            return;
        }

        var freq = Pick(_frequency, Frequencies);
        var repeats = freq is AgentScheduleText.Daily or AgentScheduleText.Weekly
            or AgentScheduleText.Monthly or AgentScheduleText.MonthlyRelative;
        var timed = freq != AgentScheduleText.OnAgentStart && freq != AgentScheduleText.OnIdle;
        var window = timed && Pick(_subdayType, SubdayTypes) != 1;

        _recurrenceRow.IsVisible = repeats;
        _recurrenceUnit.Text = freq switch
        {
            AgentScheduleText.Daily => "day(s)",
            AgentScheduleText.Weekly => "week(s)",
            _ => "month(s)"
        };
        _weeklyRow.IsVisible = freq == AgentScheduleText.Weekly;
        _monthlyRow.IsVisible = freq == AgentScheduleText.Monthly;
        _relativeRow.IsVisible = freq == AgentScheduleText.MonthlyRelative;
        _timeRow.IsVisible = timed;
        _subdayInterval.IsVisible = window;
        _startRow.IsVisible = timed;
        _windowRow.IsVisible = window;

        _summary.Text = AgentScheduleText.Describe(
            freq, Interval(freq), Pick(_subdayType, SubdayTypes), (int)(_subdayInterval.Value ?? 1),
            Pick(_ordinal, Ordinals), (int)(_recurrence.Value ?? 1), Packed(_startDate.Text, 20260101),
            PackedTime(_startTime.Text), PackedTime(_endTime.Text));
    }

    // ── Write ────────────────────────────────────────────────────────────────────────────────────────

    private async Task SaveSelectedAsync()
    {
        if (string.IsNullOrWhiteSpace(_name.Text))
        {
            _status.Text = "A schedule needs a name.";
            return;
        }

        var freq = Pick(_frequency, Frequencies);
        if (freq == AgentScheduleText.Weekly && Interval(freq) == 0)
        {
            _status.Text = "A weekly schedule needs at least one day.";
            return;
        }

        var isNew = _list.SelectedIndex < 0;
        var settings =
            $"@enabled = {(_enabled.IsChecked == true ? 1 : 0)}" +
            $", @freq_type = {freq}" +
            $", @freq_interval = {Interval(freq)}" +
            $", @freq_subday_type = {Pick(_subdayType, SubdayTypes)}" +
            $", @freq_subday_interval = {(int)(_subdayInterval.Value ?? 1)}" +
            $", @freq_relative_interval = {(freq == AgentScheduleText.MonthlyRelative ? Pick(_ordinal, Ordinals) : 0)}" +
            $", @freq_recurrence_factor = {(int)(_recurrence.Value ?? 1)}" +
            $", @active_start_date = {Packed(_startDate.Text, Today())}" +
            $", @active_end_date = {Packed(_endDate.Text, 99991231)}" +
            $", @active_start_time = {PackedTime(_startTime.Text)}" +
            $", @active_end_time = {PackedTime(_endTime.Text)}";

        if (isNew)
        {
            // Create then attach: a schedule is a shared object, and sp_add_schedule alone leaves it dangling.
            await ExecuteAsync(
                $"EXEC msdb.dbo.sp_add_schedule @schedule_name = N'{Escape(_name.Text)}', {settings};\n"
                + $"EXEC msdb.dbo.sp_attach_schedule @job_name = N'{Escape(_job)}', "
                + $"@schedule_name = N'{Escape(_name.Text)}';");
            _status.Text = "Schedule created and attached.";
        }
        else
        {
            var current = _schedules[_list.SelectedIndex];
            await ExecuteAsync($"EXEC msdb.dbo.sp_update_schedule @schedule_id = {current.Id}"
                               + $", @new_name = N'{Escape(_name.Text)}', {settings}");
            _status.Text = "Schedule saved.";
        }

        await ReloadAsync(select: isNew ? _schedules.Count : _list.SelectedIndex);
    }

    private async Task RemoveSelectedAsync()
    {
        if (_list.SelectedIndex < 0)
        {
            return;
        }

        // Detach, and let Agent drop the schedule itself only when no other job still points at it.
        var index = _list.SelectedIndex;
        await ExecuteAsync($"EXEC msdb.dbo.sp_detach_schedule @job_name = N'{Escape(_job)}', "
                           + $"@schedule_id = {_schedules[index].Id}, @delete_unused_schedule = 1");
        _status.Text = "Schedule removed.";
        await ReloadAsync(select: Math.Max(0, index - 1));
    }

    // ── Encoding ─────────────────────────────────────────────────────────────────────────────────────

    /// <summary>freq_interval means something different per frequency: a weekday bitmask, a day of the
    /// month, a relative weekday, or nothing at all.</summary>
    private int Interval(int freq) => freq switch
    {
        AgentScheduleText.Weekly => Enumerable.Range(0, _weekDays.Count)
            .Where(i => _weekDays[i].IsChecked == true).Sum(i => WeekDays[i].Bit),
        AgentScheduleText.Monthly => (int)(_dayOfMonth.Value ?? 1),
        AgentScheduleText.MonthlyRelative => Pick(_relativeDay, RelativeDays),
        AgentScheduleText.Daily => 1,
        _ => 0
    };

    private static int Pick(ComboBox box, (int Value, string Label)[] options) =>
        options[Math.Clamp(box.SelectedIndex, 0, options.Length - 1)].Value;

    private static int Today() => int.Parse(DateTime.Now.ToString("yyyyMMdd"));

    private static string Clock(int hhmmss) =>
        $"{hhmmss / 10000:D2}:{hhmmss / 100 % 100:D2}:{hhmmss % 100:D2}";

    private static string Stamp(int yyyymmdd) => yyyymmdd == 0
        ? ""
        : $"{yyyymmdd / 10000:D4}-{yyyymmdd / 100 % 100:D2}-{yyyymmdd % 100:D2}";

    /// <summary>A typed date back into Agent's packed int; anything unparseable falls back rather than
    /// throwing, since this runs on every keystroke to refresh the summary.</summary>
    private static int Packed(string? text, int fallback) =>
        DateTime.TryParse(text, out var date) ? int.Parse(date.ToString("yyyyMMdd")) : fallback;

    private static int PackedTime(string? text) =>
        TimeSpan.TryParse(text, out var time)
            ? time.Hours * 10000 + time.Minutes * 100 + time.Seconds
            : 0;

    private Task ExecuteAsync(string sql) =>
        _context.Provider.ExecuteDdlAsync(_context.Profile, sql, CancellationToken.None);

    private async Task<SqlConnection> OpenAsync()
    {
        var connection = new SqlConnection(_context.Profile.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static string Escape(string value) => value.Replace("'", "''");
}
