using GhProjectsBoards.Core.Projects;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace GhProjectsBoards.App;

internal sealed partial class EditingGrid
{
    private ContentDialog? dailyProgressDialog;
    private AppBarButton? boardsDailyProgress;
    private bool boardsDailyProgressActive;
    internal string? DailyProjectFieldId { get; set; }
    private void UpdateBoardsDailyProgress()
    {
        if (boardsDailyProgress is null) return;
        var row = active && currentRow < rows.Length ? rows[currentRow] : null;
        boardsDailyProgress.IsEnabled = IsLoaded && CanRefresh && !boardsDailyProgressActive && dailyProgressDialog is null
            && session.Workspace.Planning(projectId) is not null && row is not null
            && (row.IsLocal || registration.Snapshot.Items.Any(item => item.Id.NodeId == row.ItemId
                && item.Kind == ProjectItemKind.Issue && item.ContentId is not null));
    }
    private async Task OpenBoardsDailyProgressAsync()
    {
        if (boardsDailyProgress?.IsEnabled != true) return;
        boardsDailyProgressActive = true; UpdateBoardsDailyProgress();
        try { await DailyProgressDialogAsync(); }
        finally { boardsDailyProgressActive = false; if (IsLoaded) UpdateBoardsDailyProgress(); }
    }
    private async Task DailyProgressDialogAsync()
    {
        if (!CanRefresh || !active || !IsLoaded || dailyProgressDialog is not null) return;
        var request = generation;
        if (!await prepareLocalRows() || request != generation || !IsLoaded) return;
        var work = session.Workspace;
        var plan = work.Planning(projectId);
        if (plan is null) { await ShowPlanningSettingsAsync(); return; }
        var row = rows[currentRow]; var taskId = work.TaskId(registration, row.ItemId);
        var task = plan.Tasks.SingleOrDefault(t => t.Id == taskId)
            ?? (plan.Version >= 3 ? EditingWorkspace.WithObservedAssignment(registration, new(taskId)) : new(taskId));
        var expected = work.Revision;
        EditCell? Cell(string role) => plan.Fields.SingleOrDefault(f => f.Role == role) is { } binding
            ? row.Cells.SingleOrDefault(c => c.Key?.FieldId == binding.FieldId) : null;
        var actualCell = Cell("Actual"); var remainingCell = Cell("Remaining");
        var initialActualBuffer = actualCell is null ? null : work.Buffer(actualCell);
        var initialRemainingBuffer = remainingCell is null ? null : work.Buffer(remainingCell);
        var actualValue = actualCell is null ? task.Actuals is { Length: > 0 } knownReports
            ? PlanningContract.CanonicalHours(knownReports.Sum(r => r.Hours)) : "" : work.Value(actualCell) ?? "";
        var remainingValue = remainingCell is null ? "" : work.Value(remainingCell) ?? "";
        ActualInputContext? actualContext = null; string? actualProblem = null;
        try { actualContext = work.ActualInput(registration, row.ItemId); }
        catch (InvalidOperationException error) { actualProblem = error.Message; }
        actualProblem ??= actualContext?.Problem;

        var surface = new Grid { RowSpacing = 8, Width = Math.Min(540, XamlRoot.Size.Width - 96), MaxHeight = Math.Max(240, XamlRoot.Size.Height - 220) };
        foreach (var height in new[] { GridLength.Auto, GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto })
            surface.RowDefinitions.Add(new() { Height = height });
        var identity = new TextBlock { Text = $"{RowIdentity(row)}  {work.Value(row.Cells[0]) ?? row.Cells[0].Display}", TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(identity, "DailyTaskIdentity");
        var heading = new Grid { ColumnSpacing = 8 };
        heading.ColumnDefinitions.Add(new()); heading.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        heading.Children.Add(identity);
        var help = PlanningHelpButton("DailyProgressHelp", "実績・進捗のヘルプ",
            "実績は報告対象最終日までの累計人時、残時間はこれから必要な工数です。実績だけでは進捗を変更しません。\n計画上の進捗は日程計算に使う工数を決めます。Projectの項目は連動せず、この画面で選んだ変更だけを一緒に確定します。\n日時を指定したタスクは進捗を変更しても自動計算に切り替わりません。");
        SetColumn(help, 1); heading.Children.Add(help); surface.Children.Add(heading);
        var adopted = work.PlanFor(registration).Tasks.SingleOrDefault(t => t.Id == taskId);
        var schedule = new TextBlock { Text = adopted is null ? "採用日程：未設定"
            : $"採用日程：{DateText(adopted.Start)} → {DateText(adopted.Finish)}"
                + (adopted.Resolved ? "" : "\n" + (adopted.Problem ?? "日程を決める条件を確認してください。")), TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(schedule, "DailyAdoptedSchedule"); SetRow(schedule, 1); surface.Children.Add(schedule);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(status, "DailyProgressStatus"); AutomationProperties.SetLiveSetting(status, AutomationLiveSetting.Polite);
        SetRow(status, 2); surface.Children.Add(status);
        var content = new StackPanel { Spacing = 8 };
        var scroll = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Disabled };
        SetRow(scroll, 3); surface.Children.Add(scroll);
        var amounts = new Grid { ColumnSpacing = 12 };
        amounts.ColumnDefinitions.Add(new()); amounts.ColumnDefinitions.Add(new()); content.Children.Add(amounts);
        amounts.RowDefinitions.Add(new() { Height = GridLength.Auto }); amounts.RowDefinitions.Add(new() { Height = GridLength.Auto });
        TextBox Amount(string label, string id, string text, int column)
        {
            var input = new TextBox { Header = label, Text = text };
            AutomationProperties.SetAutomationId(input, id); TrackContextInput(input); SetColumn(input, column); amounts.Children.Add(input); return input;
        }
        var actual = Amount("累積実績（人時）", "DailyActual", initialActualBuffer ?? actualValue, 0);
        var remaining = Amount("残時間（人時）", "DailyRemaining", initialRemainingBuffer ?? remainingValue, 1);
        TextBlock PendingAmount(string id, string confirmedValue, int column)
        {
            var pending = new TextBlock { Text = "入力途中 · 確定済み " + (confirmedValue.Length == 0 ? "未設定" : confirmedValue),
                TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
            AutomationProperties.SetAutomationId(pending, id); SetRow(pending, 1); SetColumn(pending, column); amounts.Children.Add(pending); return pending;
        }
        var actualPending = PendingAmount("DailyActualPending", actualValue, 0);
        var remainingPending = PendingAmount("DailyRemainingPending", remainingValue, 1);
        void UpdateAmountPending()
        {
            actualPending.Visibility = actualCell is not null && work.Buffer(actualCell) is not null ? Visibility.Visible : Visibility.Collapsed;
            remainingPending.Visibility = remainingCell is not null && work.Buffer(remainingCell) is not null ? Visibility.Visible : Visibility.Collapsed;
        }
        UpdateAmountPending();
        actual.IsReadOnly = actualContext is null || actualContext.MultipleReports;
        remaining.IsReadOnly = remainingCell is not { Editable: true };
        if (actualProblem is not null || actualContext?.MultipleReports == true)
            content.Children.Add(new TextBlock { Text = actualProblem ?? "複数人の実績は「タスクの詳細」で更新", TextWrapping = TextWrapping.Wrap });
        var report = new Grid { ColumnSpacing = 12, Visibility = actualContext is null or { MultipleReports: true } ? Visibility.Collapsed : Visibility.Visible };
        AutomationProperties.SetAutomationId(report, "DailyReportContext");
        report.ColumnDefinitions.Add(new()); report.ColumnDefinitions.Add(new()); content.Children.Add(report);
        var people = actualContext is { Historical: true }
            ? new[] { new ActualWorkerChoice(actualContext.PersonId, PersonName(actualContext.PersonId)) }
            : plan.People.Select(p => new ActualWorkerChoice(p.Id, p.Name))
                .Concat((registration.Snapshot.Issues.GetValueOrDefault(new(work.Scope, taskId))?.Native?.Assignees ?? []).Select(p => new ActualWorkerChoice(p.Id.NodeId, p.Login)))
                .DistinctBy(p => p.Id).Append(new(null, "担当者未割当")).ToArray();
        string PersonName(string? id) => id is null ? "担当者未割当" : plan.People.FirstOrDefault(p => p.Id == id)?.Name ?? id;
        var worker = new FormComboBox { Header = actualContext?.Historical == true ? "実績の担当者（保持）" : "実績の担当者", ItemsSource = people,
            DisplayMemberPath = nameof(ActualWorkerChoice.Label), HorizontalAlignment = HorizontalAlignment.Stretch,
            IsEnabled = !actual.IsReadOnly && actualContext?.Historical != true };
        worker.SelectedItem = actualContext?.HasPerson == true ? people.FirstOrDefault(p => p.Id == actualContext.PersonId) : null;
        var initialWorker = worker.SelectedItem as ActualWorkerChoice;
        AutomationProperties.SetAutomationId(worker, "DailyActualWorker"); report.Children.Add(worker);
        var proposedDay = actualContext is null or { MultipleReports: true } ? null
            : actualContext.ReportedThrough ?? confirmedActualThrough ?? (plan.Cutoff is { } cutoff ? DateOnly.FromDateTime(cutoff) : (DateOnly?)null);
        var through = new CalendarDatePicker { Header = "報告対象最終日", Date = proposedDay is { } day ? new DateTimeOffset(day.ToDateTime(TimeOnly.MinValue), TimeSpan.FromHours(9)) : null,
            IsEnabled = !actual.IsReadOnly, HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(through, "DailyReportedThrough"); SetColumn(through, 1); report.Children.Add(through);
        var progress = new FormComboBox { Header = "計画上の進捗（日程計算）", HorizontalAlignment = HorizontalAlignment.Stretch,
            ItemsSource = new[] { "未着手", "進行中", "完了", "再開" }, SelectedIndex = (int)task.Progress };
        AutomationProperties.SetAutomationId(progress, "DailyProgress"); content.Children.Add(progress);
        var effect = new TextBlock { TextWrapping = TextWrapping.Wrap }; AutomationProperties.SetAutomationId(effect, "DailyProgressEffect"); content.Children.Add(effect);
        var confirmRemaining = new CheckBox { Content = "再開時の残時間を確認" }; AutomationProperties.SetAutomationId(confirmRemaining, "DailyConfirmRemaining"); content.Children.Add(confirmRemaining);
        var start = new MinuteEditor("実績開始（日本時間）", "DailyActualStart", DateText(task.ActualStart));
        var finish = new MinuteEditor("実績終了（日本時間）", "DailyActualFinish", DateText(task.ActualFinish));
        TrackContextInput(start.Input); TrackContextInput(finish.Input); content.Children.Add(start); content.Children.Add(finish);
        void Explain()
        {
            var value = (PlanningProgress)progress.SelectedIndex;
            confirmRemaining.Visibility = value == PlanningProgress.Reopened ? Visibility.Visible : Visibility.Collapsed;
            start.Visibility = value != PlanningProgress.Unstarted || task.ActualStart is not null ? Visibility.Visible : Visibility.Collapsed;
            finish.Visibility = value == PlanningProgress.Completed || task.ActualFinish is not null ? Visibility.Visible : Visibility.Collapsed;
            effect.Text = value == PlanningProgress.Completed ? "完了には残時間0と実績開始・終了を入力してください。"
                : task.Mode == PlanningMode.Manual ? "指定した日程を保持"
                : task.Mode == PlanningMode.Unplanned ? "日程未設定"
                : value is PlanningProgress.InProgress or PlanningProgress.Reopened ? $"残時間で計算 · 基準 {DateText(plan.Cutoff)}"
                : "見積で計算";
        }
        progress.SelectionChanged += (_, _) => Explain(); Explain();
        var fieldChoice = new Grid { ColumnSpacing = 8 };
        fieldChoice.ColumnDefinitions.Add(new()); fieldChoice.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        content.Children.Add(fieldChoice);
        var projectField = new FormComboBox { Header = "一緒に変更するProject項目（任意）", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(projectField, "DailyProjectField"); fieldChoice.Children.Add(projectField);
        projectField.Items.Add(new ComboBoxItem { Content = "選択しない" });
        var availableFields = registration.Snapshot.Fields.Where(f => f.DataType == "SINGLE_SELECT" && f.ValueOwner == FieldOwner.ProjectItem).ToArray();
        foreach (var field in availableFields)
            projectField.Items.Add(new ComboBoxItem { Content = field.Name
                + (availableFields.Count(other => other.Name == field.Name) > 1 ? " [" + field.Id.NodeId + "]" : ""), Tag = field.Id.NodeId });
        if (DailyProjectFieldId is { } retained && !projectField.Items.Cast<ComboBoxItem>().Any(i => (string?)i.Tag == retained))
            projectField.Items.Add(new ComboBoxItem { Content = "確認できない項目 [" + retained + "]", Tag = retained });
        projectField.SelectedItem = projectField.Items.Cast<ComboBoxItem>().Single(i => (string?)i.Tag == DailyProjectFieldId);
        var fieldIdentity = new TextBlock { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, MaxWidth = 440 };
        AutomationProperties.SetAutomationId(fieldIdentity, "DailyProjectFieldIdentity");
        var fieldDetails = new Button { Content = "項目のID", VerticalAlignment = VerticalAlignment.Bottom,
            Flyout = new Flyout { Content = fieldIdentity } };
        AutomationProperties.SetAutomationId(fieldDetails, "DailyProjectFieldDetails"); SetColumn(fieldDetails, 1); fieldChoice.Children.Add(fieldDetails);
        var projectOption = new FormComboBox { Header = "値", HorizontalAlignment = HorizontalAlignment.Stretch };
        AutomationProperties.SetAutomationId(projectOption, "DailyProjectOption"); content.Children.Add(projectOption);
        var fieldHint = new TextBlock { TextWrapping = TextWrapping.Wrap };
        AutomationProperties.SetAutomationId(fieldHint, "DailyProjectFieldHint"); content.Children.Add(fieldHint);
        var resetField = new Button { Content = "項目の変更を取り消す", Visibility = Visibility.Collapsed };
        AutomationProperties.SetAutomationId(resetField, "DailyProjectFieldReset"); content.Children.Add(resetField);
        string? selectedFieldId = null, initialOption = null, fieldProblem = null;
        string initialOptionLabel = "";
        bool loadingOption = false;
        bool ProjectFieldDirty() => projectOption.IsEnabled && projectOption.SelectedItem is ComboBoxItem chosen && (string?)chosen.Tag != initialOption;
        void UpdateFieldCandidate()
        {
            var dirty = ProjectFieldDirty();
            // Keep one visible candidate. Switching the field cannot silently
            // abandon an already selected option for another field.
            projectField.IsEnabled = !dirty; resetField.Visibility = dirty ? Visibility.Visible : Visibility.Collapsed;
            fieldHint.Text = fieldProblem is not null ? fieldProblem + " 変更せずに日程計算の入力を確定できます。"
                : dirty ? $"変更候補 · 確定済み: {initialOptionLabel}"
                : "確定済みのローカル値";
        }
        void LoadProjectField()
        {
            loadingOption = true;
            selectedFieldId = (string?)((ComboBoxItem)projectField.SelectedItem).Tag;
            var definition = registration.Snapshot.Fields.SingleOrDefault(f => f.Id.NodeId == selectedFieldId);
            var cell = selectedFieldId is null ? null : work.Open(registration).Single(r => r.ItemId == row.ItemId).Cells
                .SingleOrDefault(c => c.Key is { Kind: "Select" or "LocalSelect" } key && key.FieldId == selectedFieldId);
            var shown = selectedFieldId is not null;
            fieldDetails.Visibility = projectOption.Visibility = fieldHint.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
            projectOption.Items.Clear(); projectOption.IsEnabled = false;
            fieldProblem = null;
            if (shown)
            {
                fieldIdentity.Text = $"ProjectフィールドID: {selectedFieldId}";
                AutomationProperties.SetName(projectOption, $"{definition?.Name ?? "確認できない項目"} [{selectedFieldId}] の値");
                initialOption = cell is null ? null : work.Value(cell);
                projectOption.Items.Add(new ComboBoxItem { Content = "未選択", Tag = null });
                foreach (var option in definition?.Options ?? []) projectOption.Items.Add(new ComboBoxItem { Content = option.Name
                    + (definition!.Options.Count(other => other.Name == option.Name) > 1 ? " [" + option.Id + "]" : ""), Tag = option.Id });
                if (initialOption is not null && !projectOption.Items.Cast<ComboBoxItem>().Any(i => (string?)i.Tag == initialOption))
                    projectOption.Items.Add(new ComboBoxItem { Content = "確認できない選択肢 · " + initialOption, Tag = initialOption, IsEnabled = false });
                projectOption.SelectedItem = projectOption.Items.Cast<ComboBoxItem>().Single(i => (string?)i.Tag == initialOption);
                initialOptionLabel = (string)((ComboBoxItem)projectOption.SelectedItem).Content;
                fieldProblem = definition?.Availability != ValueAvailability.Present || cell is null ? "この項目を確認できません。"
                    : !cell.Editable ? cell.Reason ?? "この項目は参照専用です。"
                    : work.Buffer(cell) is not null ? "この項目には入力途中の値があります。表で確定・取消してください。"
                    : work.Field(cell)?.Conflict == true ? "この項目は競合しています。表で採用値を確認してください。"
                    : work.Field(cell)?.Observation?.Reason;
                projectOption.IsEnabled = fieldProblem is null && cell?.Availability is ValueAvailability.Present or ValueAvailability.Empty;
            }
            loadingOption = false; UpdateFieldCandidate();
        }
        projectField.SelectionChanged += (_, _) => { DailyProjectFieldId = (string?)((ComboBoxItem)projectField.SelectedItem).Tag; LoadProjectField(); };
        projectOption.SelectionChanged += (_, _) => { if (!loadingOption) UpdateFieldCandidate(); };
        resetField.Click += (_, _) => { projectOption.SelectedItem = projectOption.Items.Cast<ComboBoxItem>().Single(i => (string?)i.Tag == initialOption); };
        LoadProjectField();
        var details = new Button { Content = "タスクの詳細" }; AutomationProperties.SetAutomationId(details, "DailyTaskDetails"); content.Children.Add(details);
        var leaving = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        leaving.Children.Add(new TextBlock { Text = "この画面の未確定の変更を破棄して、詳細を開きますか。", TextWrapping = TextWrapping.Wrap });
        var leaveActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var discard = new Button { Content = "破棄して詳細へ" }; var keep = new Button { Content = "編集を続ける" };
        AutomationProperties.SetAutomationId(discard, "DailyDiscardToDetails"); AutomationProperties.SetAutomationId(keep, "DailyKeepEditing");
        leaveActions.Children.Add(discard); leaveActions.Children.Add(keep); leaving.Children.Add(leaveActions); SetRow(leaving, 4); surface.Children.Add(leaving);
        var cancelHint = new TextBlock { Text = "取消では、この画面での変更を取り消します。開く前の入力途中は残ります。",
            TextWrapping = TextWrapping.Wrap, Visibility = initialActualBuffer is not null || initialRemainingBuffer is not null
                ? Visibility.Visible : Visibility.Collapsed };
        AutomationProperties.SetAutomationId(cancelHint, "DailyCancelHint"); SetRow(cancelHint, 5); surface.Children.Add(cancelHint);
        var dialog = new ContentDialog { XamlRoot = XamlRoot, Title = "実績・進捗", PrimaryButtonText = "確定", CloseButtonText = "取消", Content = surface };
        AutomationProperties.SetAutomationId(dialog, "DailyProgressDialog");
        bool confirmed = false, scheduleChanged = false, openDetails = false;
        bool Current() => IsLoaded && request == generation && ReferenceEquals(session.Workspace, work) && work.Revision == expected;
        void ShowError(string text) { status.Text = text; status.Visibility = Visibility.Visible; }
        void Buffer(TextBox input, EditCell? cell, bool typed)
        {
            if (input.IsReadOnly || cell is null) return;
            if (!Current()) { ShowError("入力対象が変わりました。現在の値を確認して開き直してください。"); return; }
            if (typed) work.SetPlanningBuffer(cell, input.Text); else work.SetBuffer(cell, input.Text);
            expected = work.Revision;
            UpdateAmountPending();
            _ = FlushDraftsAsync("daily-progress-input");
        }
        // Confirm can follow the final keystroke before deferred TextChanged.
        // Use the same synchronous raw-input boundary as the sheet's native cells.
        actual.TextChanging += (_, _) => Buffer(actual, actualCell, true);
        remaining.TextChanging += (_, _) => Buffer(remaining, remainingCell, false);
        bool PlanningDirty() => actual.Text != (initialActualBuffer ?? actualValue) || remaining.Text != (initialRemainingBuffer ?? remainingValue)
            || progress.SelectedIndex != (int)task.Progress || start.Text != DateText(task.ActualStart) || finish.Text != DateText(task.ActualFinish)
            || !ReferenceEquals(worker.SelectedItem, initialWorker) || through.Date?.DateTime.Date != proposedDay?.ToDateTime(TimeOnly.MinValue) || confirmRemaining.IsChecked == true;
        bool Dirty() => PlanningDirty() || ProjectFieldDirty();
        details.Click += (_, _) => {
            if (!CanRefresh) return;
            if (Dirty()) { leaving.Visibility = Visibility.Visible; dialog.IsPrimaryButtonEnabled = false; keep.Focus(FocusState.Programmatic); }
            else { openDetails = true; dialog.Hide(); }
        };
        discard.Click += (_, _) => { if (CanRefresh) { openDetails = true; dialog.Hide(); } };
        keep.Click += (_, _) => { leaving.Visibility = Visibility.Collapsed; dialog.IsPrimaryButtonEnabled = true; progress.Focus(FocusState.Programmatic); };
        dialog.CloseButtonClick += (_, args) => { if (!CanRefresh) { ShowError("IME入力を確定・取消してから閉じてください。"); args.Cancel = true; } };
        dialog.PrimaryButtonClick += (_, args) => {
            Control? invalid = null;
            try
            {
                if (!CanRefresh || !Current()) throw new InvalidOperationException("入力対象または入力状態が変わりました。現在の値を確認してください。");
                var ownsActual = !actual.IsReadOnly && (work.Buffer(actualCell!) is not null || actual.Text != actualValue
                    || !ReferenceEquals(worker.SelectedItem, initialWorker) || through.Date?.DateTime.Date != proposedDay?.ToDateTime(TimeOnly.MinValue));
                var ownsRemaining = !remaining.IsReadOnly && (work.Buffer(remainingCell!) is not null || remaining.Text != remainingValue || confirmRemaining.IsChecked == true);
                // An unchanged form is only a dismissal. A same-value buffer,
                // including text returned to its original value here, still needs confirmation.
                if (!ownsActual && !ownsRemaining && !Dirty()) { confirmed = true; return; }
                var changesPlanning = ownsActual || ownsRemaining || PlanningDirty();
                var nextPlan = plan;
                if (changesPlanning)
                {
                    var reports = task.Actuals;
                    if (ownsActual)
                    {
                        invalid = actual; var hours = PlanningContract.ParseHours(actual.Text);
                        invalid = worker; var person = worker.SelectedItem as ActualWorkerChoice ?? throw new InvalidOperationException("実績の担当者または担当者未割当を選んでください。");
                        invalid = through; var reportDay = through.Date is { } date ? DateOnly.FromDateTime(date.DateTime) : throw new InvalidOperationException("報告対象最終日を選んでください。");
                        reports = [new(person.Id, hours, reportDay)];
                    }
                    invalid = remaining;
                    var remainingHours = remaining.Text.Length == 0 ? (decimal?)null : PlanningContract.ParseHours(remaining.Text);
                    invalid = start.Input; var actualStart = PlanningDate(start.Text);
                    invalid = finish.Input; var actualFinish = PlanningDate(finish.Text);
                    var selectedProgress = (PlanningProgress)progress.SelectedIndex;
                    if (actualFinish < actualStart) throw new InvalidOperationException("実績終了は実績開始以降の日時を入力してください。");
                    if (selectedProgress == PlanningProgress.Completed)
                    {
                        invalid = remaining; if (remainingHours != 0) throw new InvalidOperationException("完了にするには残時間0を入力してください。");
                        invalid = start.Input; if (actualStart is null) throw new InvalidOperationException("完了にするには実績開始を入力してください。");
                        invalid = finish.Input; if (actualFinish is null) throw new InvalidOperationException("完了にするには実績終了を入力してください。");
                    }
                    invalid = progress;
                    var nextTask = task with { Progress = selectedProgress, Actuals = reports, ActualStart = actualStart, ActualFinish = actualFinish };
                    nextPlan = plan with { Tasks = plan.Tasks.Where(t => t.Id != taskId).Append(nextTask).ToArray() };
                }
                var confirmedInputs = new List<FieldKey>();
                if (ownsActual) confirmedInputs.Add(actualCell!.Key!);
                if (ownsRemaining) confirmedInputs.Add(remainingCell!.Key!);
                work.CommitPlanning(registration, nextPlan, expected,
                    values: ownsRemaining ? [new(row.ItemId, "Remaining", remaining.Text.Length == 0 ? null : remaining.Text)] : [],
                    consumeBuffers: confirmedInputs.ToArray(), projectFields: ProjectFieldDirty()
                        ? [new(row.ItemId, selectedFieldId!, (string?)((ComboBoxItem)projectOption.SelectedItem).Tag)] : []);
                expected = work.Revision; confirmed = true;
                var adoptedAfter = work.PlanFor(registration).Tasks.SingleOrDefault(t => t.Id == taskId);
                scheduleChanged = adoptedAfter?.Start != adopted?.Start || adoptedAfter?.Finish != adopted?.Finish;
                if (ownsActual && through.Date is { } savedDay) confirmedActualThrough = DateOnly.FromDateTime(savedDay.DateTime);
                Update("daily-progress");
                gantt?.ShowChangedSchedule(scheduleChanged ? row.ItemId : null);
            }
            catch (Exception error) when (error is InvalidOperationException or InvalidDataException)
            {
                args.Cancel = true; ShowError(error.Message);
                if (invalid?.Parent is MinuteEditor minute) minute.RevealInput();
                surface.UpdateLayout();
                invalid?.Focus(FocusState.Programmatic); invalid?.StartBringIntoView(new BringIntoViewOptions { AnimationDesired = false });
            }
        };
        void CloseOnUnload(object sender, RoutedEventArgs args) => dialog.Hide();
        dailyProgressDialog = dialog; Unloaded += CloseOnUnload;
        try { await dialog.ShowAsync(); }
        finally { Unloaded -= CloseOnUnload; if (ReferenceEquals(dailyProgressDialog, dialog)) dailyProgressDialog = null; }
        if (!confirmed && Current())
        {
            // Cancel restores only this editor's raw input, including any buffer
            // that existed before entry. Other cells and adopted values stay intact.
            if (!actual.IsReadOnly && actualCell is not null) work.SetPlanningBuffer(actualCell, initialActualBuffer);
            if (!remaining.IsReadOnly && remainingCell is not null) work.SetBuffer(remainingCell, initialRemainingBuffer);
            expected = work.Revision; Update("daily-progress-cancel");
        }
        await FlushDraftsAsync("daily-progress");
        if (!IsLoaded || request != generation) return;
        if (confirmed) UpdateGantt();
        else if (openDetails) await PlanningDialogAsync(false);
    }
}
