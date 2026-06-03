using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Tide.App.Services;
using Tide.Core;
using Windows.Storage.Pickers;
using Windows.System;
using Windows.UI.Core;
using WinRT.Interop;

namespace Tide.App;

public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    private const int SwHide = 0;
    private const int SwShow = 5;
    private const int SwRestore = 9;

    private readonly TideRepository _repository;
    private readonly BackgroundRefreshService _backgroundRefresh;
    private readonly INotificationService _notifications;
    private readonly TrayIconService _tray;
    private readonly StartupService _startup;
    private readonly TideLogger _logger;
    private readonly string _launchArguments;
    private readonly DispatcherTimer _searchDebounce = new() { Interval = TimeSpan.FromMilliseconds(180) };
    private Snapshot _snapshot = new();
    private RefreshServiceState? _refreshState;
    private string _filter = "all";
    private string _activeSource = "all";
    private string _pendingQuery = string.Empty;
    private string _query = string.Empty;
    private bool _loaded;
    private bool _allowExit;
    private bool _closeNoticeShown;
    private nint _hwnd;

    public ObservableCollection<SourceRowViewModel> SourceRows { get; } = [];
    public ObservableCollection<StoryCardViewModel> StoryCards { get; } = [];

    public string PageTitle => _activeSource == "all"
        ? FilterTitle()
        : _snapshot.Sources.FirstOrDefault(source => source.Id == _activeSource)?.Title ?? "新着の流れ";
    public string PageEyebrow => _activeSource == "all" ? "CALM UPDATE RADAR" : "WATCHED SOURCE";
    public string TodayText { get; } = DateTimeOffset.Now.ToString("M月d日 dddd");
    public string TotalCount => _snapshot.Stories.Count.ToString();
    public string UnreadCount => _snapshot.Stories.Count(story => !story.IsRead).ToString();
    public string SavedCount => _snapshot.Stories.Count(story => story.IsSaved).ToString();
    public string TodayCount => _snapshot.Stories.Count(story => story.DiscoveredAt.LocalDateTime.Date == DateTime.Today).ToString();
    public string WeekCount => _snapshot.Stories.Count(story => story.DiscoveredAt >= DateTimeOffset.Now.AddDays(-7)).ToString();
    public string FailedSourceCount => _snapshot.Sources.Count(source => source.ConsecutiveFailureCount > 0).ToString();
    public string WatchCount => _snapshot.Sources.Count(source => source.IsEnabled).ToString();
    public string StoriesCaption => $"{StoryCards.Count} STORIES";
    public string UnreadCaption => $"{UnreadCount} UNREAD";
    public string UnreadPadded => _snapshot.Stories.Count(story => !story.IsRead).ToString("00");
    public string LastRefreshText => $"{RefreshStateIcon()}  最終確認 {RelativeTime(_snapshot.LastRefreshedAt)}";
    public string NextRefreshText => _backgroundRefresh.NextRefreshAt is null ? "-" : _backgroundRefresh.NextRefreshAt.Value.LocalDateTime.ToString("t");
    public string RadarStatusText => _refreshState?.Message ??
                                     (_snapshot.Settings.AutoRefreshEnabled
                                         ? $"Tideは{_snapshot.Settings.RefreshIntervalMinutes}分ごとに静かに確認します。"
                                         : "自動取得はオフです。");
    public string EmptyEyebrow => EmptyCopy().Eyebrow;
    public string EmptyTitle => EmptyCopy().Title;
    public string EmptyMessage => EmptyCopy().Message;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow(
        TideRepository repository,
        BackgroundRefreshService backgroundRefresh,
        INotificationService notifications,
        TrayIconService tray,
        StartupService startup,
        TideLogger logger,
        string launchArguments)
    {
        _repository = repository;
        _backgroundRefresh = backgroundRefresh;
        _notifications = notifications;
        _tray = tray;
        _startup = startup;
        _logger = logger;
        _launchArguments = launchArguments;

        InitializeComponent();
        _hwnd = WindowNative.GetWindowHandle(this);
        _tray.Initialize(_hwnd);
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce.Stop();
            _query = _pendingQuery;
            RefreshView();
        };

        AppWindow.Closing += AppWindow_Closing;
        AppWindow.Changed += AppWindow_Changed;
        WireServices();
    }

    public void ActivateFromExternal(string payload)
    {
        var args = ParseArguments(payload);
        if (args.TryGetValue("action", out var action))
        {
            switch (action)
            {
                case "pause1h":
                    _ = PauseNotificationsAsync(TimeSpan.FromHours(1));
                    return;
                case "markAllRead":
                    _ = MarkAllReadFromActivationAsync();
                    return;
                case "openStory" when args.TryGetValue("storyId", out var storyId):
                    ShowAndActivate();
                    _ = OpenStoryAsync(storyId);
                    return;
            }
        }

        ShowAndActivate();
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        _snapshot = await _repository.LoadAsync();
        _backgroundRefresh.Start(_snapshot);
        _tray.Update(_snapshot);
        RefreshView();

        if (!string.IsNullOrWhiteSpace(_snapshot.DataWarning))
        {
            ShowStatus(_snapshot.DataWarning, InfoBarSeverity.Warning);
        }

        if (!_notifications.IsSupported)
        {
            ShowStatus(_notifications.StatusMessage, InfoBarSeverity.Warning);
        }

        if (_launchArguments.Contains("--startup", StringComparison.OrdinalIgnoreCase) &&
            (_snapshot.Settings.StartInTray || _snapshot.Settings.StartMinimized))
        {
            HideToTray(showNotice: false);
        }
    }

    private async void AddSource_Click(object sender, RoutedEventArgs e)
    {
        var input = new TextBox { PlaceholderText = "example.com/journal" };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "気になるサイトを追加",
            Content = input,
            PrimaryButtonText = "プレビュー",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(input.Text))
        {
            return;
        }

        SourcePreview? preview = null;
        await RunBusyAsync(async () =>
        {
            preview = await _repository.PreviewSourceAsync(input.Text);
        }, "サイトを確認できませんでした");
        if (preview is null)
        {
            return;
        }

        var confirm = new StackPanel { Spacing = 8 };
        confirm.Children.Add(new TextBlock { Text = preview.Source.Title, FontSize = 18, TextWrapping = TextWrapping.Wrap });
        confirm.Children.Add(new TextBlock { Text = $"{preview.Kind} / 初期記事 {preview.InitialStories.Count}件", TextWrapping = TextWrapping.Wrap });
        confirm.Children.Add(new TextBlock
        {
            Text = preview.Message ?? "初期取得の記事は通知せず、次回以降の新着から通知します。",
            TextWrapping = TextWrapping.Wrap
        });

        var confirmDialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "このサイトをウォッチしますか",
            Content = confirm,
            PrimaryButtonText = "ウォッチを始める",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            _snapshot = await _repository.AddSourceAsync(_snapshot, input.Text);
            _backgroundRefresh.UpdateSnapshot(_snapshot);
            RefreshView();
            ShowStatus("新しいサイトを追加しました。", InfoBarSeverity.Success);
        }, "サイトを追加できませんでした");
    }

    private async void Settings_Click(object sender, RoutedEventArgs e) => await ShowSettingsDialogAsync();

    private async void SourceManager_Click(object sender, RoutedEventArgs e) => await ShowSourceManagerDialogAsync();

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshNowAsync(manual: true);

    private void AllStories_Click(object sender, RoutedEventArgs e) => ApplyFilter("all");
    private void UnreadStories_Click(object sender, RoutedEventArgs e) => ApplyFilter("unread");
    private void SavedStories_Click(object sender, RoutedEventArgs e) => ApplyFilter("saved");
    private void TodayStories_Click(object sender, RoutedEventArgs e) => ApplyFilter("today");
    private void WeekStories_Click(object sender, RoutedEventArgs e) => ApplyFilter("week");
    private void FailedSources_Click(object sender, RoutedEventArgs e) => ApplyFilter("failed");
    private void NotifiedStories_Click(object sender, RoutedEventArgs e) => ApplyFilter("notified");
    private void MutedSources_Click(object sender, RoutedEventArgs e) => ApplyFilter("muted");

    private void AllSources_Click(object sender, RoutedEventArgs e)
    {
        _activeSource = "all";
        RefreshView();
    }

    private void Source_Click(object sender, RoutedEventArgs e)
    {
        _activeSource = (sender as FrameworkElement)?.Tag as string ?? "all";
        RefreshView();
    }

    private async void RemoveSource_Click(object sender, RoutedEventArgs e)
    {
        var sourceId = (sender as FrameworkElement)?.Tag as string;
        var source = _snapshot.Sources.FirstOrDefault(candidate => candidate.Id == sourceId);
        if (source is null)
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Sourceを削除しますか",
            Content = $"{source.Title} と、このSourceの記事をTideから削除します。",
            PrimaryButtonText = "削除",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        _snapshot = await _repository.RemoveSourceAsync(_snapshot, source.Id);
        _backgroundRefresh.UpdateSnapshot(_snapshot);
        if (_activeSource == source.Id)
        {
            _activeSource = "all";
        }
        RefreshView();
    }

    private async void MarkAllRead_Click(object sender, RoutedEventArgs e) => await MarkAllReadFromActivationAsync();

    private async void ToggleRead_Click(object sender, RoutedEventArgs e)
    {
        var storyId = (sender as FrameworkElement)?.Tag as string;
        if (storyId is null)
        {
            return;
        }

        _snapshot = await _repository.ToggleReadAsync(_snapshot, storyId);
        _backgroundRefresh.UpdateSnapshot(_snapshot);
        RefreshView();
    }

    private async void ToggleSaved_Click(object sender, RoutedEventArgs e)
    {
        var storyId = (sender as FrameworkElement)?.Tag as string;
        if (storyId is null)
        {
            return;
        }

        _snapshot = await _repository.ToggleSavedAsync(_snapshot, storyId);
        _backgroundRefresh.UpdateSnapshot(_snapshot);
        RefreshView();
    }

    private async void OpenStory_Click(object sender, RoutedEventArgs e)
    {
        var storyId = (sender as FrameworkElement)?.Tag as string;
        if (storyId is not null)
        {
            await OpenStoryAsync(storyId);
        }
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _pendingQuery = (sender as TextBox)?.Text ?? string.Empty;
        _searchDebounce.Stop();
        _searchDebounce.Start();
    }

    private async void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);
        if (ctrl && e.Key == VirtualKey.R)
        {
            e.Handled = true;
            await RefreshNowAsync(manual: true);
        }
        else if (ctrl && e.Key == VirtualKey.L)
        {
            e.Handled = true;
            AddSource_Click(this, new RoutedEventArgs());
        }
        else if (ctrl && e.Key == VirtualKey.F)
        {
            e.Handled = true;
            SearchBox.Focus(FocusState.Programmatic);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            SearchBox.Text = string.Empty;
            _pendingQuery = string.Empty;
            _query = string.Empty;
            RefreshView();
        }
        else if (e.Key == VirtualKey.S && StoryCards.Count > 0)
        {
            _snapshot = await _repository.ToggleSavedAsync(_snapshot, StoryCards[0].Id);
            _backgroundRefresh.UpdateSnapshot(_snapshot);
            RefreshView();
        }
        else if (e.Key == VirtualKey.M && StoryCards.Count > 0)
        {
            _snapshot = await _repository.ToggleReadAsync(_snapshot, StoryCards[0].Id);
            _backgroundRefresh.UpdateSnapshot(_snapshot);
            RefreshView();
        }
        else if (e.Key == VirtualKey.Enter && StoryCards.Count > 0)
        {
            await OpenStoryAsync(StoryCards[0].Id);
        }
    }

    private void ApplyFilter(string filter)
    {
        _filter = filter;
        _activeSource = "all";
        RefreshView();
    }

    private async Task RefreshNowAsync(bool manual)
    {
        await RunBusyAsync(async () =>
        {
            var result = await _backgroundRefresh.RefreshNowAsync(manual);
            if (result is null)
            {
                ShowStatus("すでに更新中です。", InfoBarSeverity.Informational);
                return;
            }

            _snapshot = result.Snapshot;
            RefreshView();
            ShowStatus(result.FailedSources.Count == 0
                ? $"{result.CheckedSourcesCount}件のサイトを確認しました。新着 {result.NewStoriesCount}件。"
                : $"{result.FailedSources.Count}件のサイトを更新できませんでした。",
                result.FailedSources.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }, "更新に失敗しました");
    }

    private async Task OpenStoryAsync(string storyId)
    {
        var story = _snapshot.Stories.FirstOrDefault(candidate => candidate.Id == storyId);
        if (story is null)
        {
            return;
        }

        _snapshot = await _repository.MarkReadAsync(_snapshot, story.Id);
        _backgroundRefresh.UpdateSnapshot(_snapshot);
        RefreshView();
        Process.Start(new ProcessStartInfo(story.Url) { UseShellExecute = true });
    }

    private async Task MarkAllReadFromActivationAsync()
    {
        _snapshot = await _repository.MarkAllReadAsync(_snapshot);
        _backgroundRefresh.UpdateSnapshot(_snapshot);
        RefreshView();
        ShowAndActivate();
    }

    private async Task PauseNotificationsAsync(TimeSpan duration)
    {
        await _backgroundRefresh.PauseNotificationsForAsync(duration);
        _snapshot = _backgroundRefresh.Snapshot;
        RefreshView();
        ShowStatus("通知を一時停止しました。", InfoBarSeverity.Informational);
    }

    private void RefreshView()
    {
        SourceRows.Clear();
        foreach (var source in _snapshot.Sources)
        {
            SourceRows.Add(new SourceRowViewModel(source,
                _snapshot.Stories.Count(story => story.SourceId == source.Id && !story.IsRead)));
        }

        var sources = _snapshot.Sources.ToDictionary(source => source.Id);
        var failedSourceIds = _snapshot.Sources
            .Where(source => source.ConsecutiveFailureCount > 0)
            .Select(source => source.Id)
            .ToHashSet(StringComparer.Ordinal);
        var mutedSourceIds = _snapshot.Sources
            .Where(source => !source.IsEnabled || !source.NotificationsEnabled)
            .Select(source => source.Id)
            .ToHashSet(StringComparer.Ordinal);

        var visibleStories = _snapshot.Stories
            .Where(story => _activeSource == "all" || story.SourceId == _activeSource)
            .Where(story => _filter != "unread" || !story.IsRead)
            .Where(story => _filter != "saved" || story.IsSaved)
            .Where(story => _filter != "today" || story.DiscoveredAt.LocalDateTime.Date == DateTime.Today)
            .Where(story => _filter != "week" || story.DiscoveredAt >= DateTimeOffset.Now.AddDays(-7))
            .Where(story => _filter != "failed" || failedSourceIds.Contains(story.SourceId))
            .Where(story => _filter != "notified" || story.WasNotified)
            .Where(story => _filter != "muted" || mutedSourceIds.Contains(story.SourceId))
            .Where(story =>
            {
                var source = sources.GetValueOrDefault(story.SourceId);
                return $"{story.Title} {story.Summary} {source?.Title} {story.Url}"
                    .Contains(_query, StringComparison.OrdinalIgnoreCase);
            })
            .OrderByDescending(story => story.PublishedAt);

        StoryCards.Clear();
        foreach (var story in visibleStories)
        {
            if (sources.TryGetValue(story.SourceId, out var source))
            {
                StoryCards.Add(new StoryCardViewModel(story, source));
            }
        }

        EmptyState.Visibility = _snapshot.Sources.Count == 0 || StoryCards.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
        _tray.Update(_snapshot, _refreshState);
        NotifyAll();
    }

    private async Task ShowSettingsDialogAsync()
    {
        var settings = _snapshot.Settings.Normalize();
        var autoRefresh = Toggle("自動取得", settings.AutoRefreshEnabled);
        var interval = NumberText(settings.RefreshIntervalMinutes.ToString(), "取得間隔(分)");
        var refreshStartup = Toggle("起動直後に更新", settings.RefreshOnStartup);
        var closeToTray = Toggle("閉じたらトレイに入る", settings.CloseToTray);
        var minimizeToTray = Toggle("最小化したらトレイに入る", settings.MinimizeToTray);
        var notifications = Toggle("Windows通知", settings.NotificationsEnabled);
        var quietHours = Toggle("Quiet Hours", settings.QuietHoursEnabled);
        var quietStart = NumberText(settings.QuietHoursStart, "開始 HH:mm");
        var quietEnd = NumberText(settings.QuietHoursEnd, "終了 HH:mm");
        var launchAtStartup = Toggle("Windows起動時に開始", settings.LaunchAtStartup || _startup.IsEnabled);
        var startInTray = Toggle("起動時にトレイへ入る", settings.StartInTray);
        var maxStories = NumberText(settings.MaxStories.ToString(), "保存件数");

        var content = new StackPanel { Spacing = 10, MaxWidth = 560 };
        foreach (var control in new Control[]
                 {
                     autoRefresh, interval, refreshStartup, closeToTray, minimizeToTray, notifications,
                     quietHours, quietStart, quietEnd, launchAtStartup, startInTray, maxStories
                 })
        {
            content.Children.Add(control);
        }

        content.Children.Add(new TextBlock { Text = _notifications.StatusMessage, TextWrapping = TextWrapping.Wrap });
        content.Children.Add(SettingsButton("Dataフォルダを開く", OpenDataFolder));
        content.Children.Add(SettingsButton("ログを開く", OpenLog));
        content.Children.Add(SettingsButton("ログを消す", () =>
        {
            _logger.Clear();
            ShowStatus("ログを消しました。", InfoBarSeverity.Success);
        }));
        content.Children.Add(SettingsButton("データをエクスポート", async () => await ExportDataAsync()));
        content.Children.Add(SettingsButton("データをインポート", async () => await ImportDataAsync()));

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "環境設定",
            Content = new ScrollViewer { Content = content, MaxHeight = 620 },
            PrimaryButtonText = "保存",
            CloseButtonText = "閉じる",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        var updated = (settings with
        {
            AutoRefreshEnabled = autoRefresh.IsOn,
            RefreshIntervalMinutes = ParseInt(interval.Text, settings.RefreshIntervalMinutes),
            RefreshOnStartup = refreshStartup.IsOn,
            CloseToTray = closeToTray.IsOn,
            MinimizeToTray = minimizeToTray.IsOn,
            NotificationsEnabled = notifications.IsOn,
            QuietHoursEnabled = quietHours.IsOn,
            QuietHoursStart = quietStart.Text,
            QuietHoursEnd = quietEnd.Text,
            LaunchAtStartup = launchAtStartup.IsOn,
            StartInTray = startInTray.IsOn,
            MaxStories = ParseInt(maxStories.Text, settings.MaxStories)
        }).Normalize();

        if (!_startup.SetEnabled(updated.LaunchAtStartup))
        {
            ShowStatus($"起動時開始を更新できませんでした: {_startup.LastError}", InfoBarSeverity.Warning);
        }

        _snapshot = await _repository.SaveSettingsAsync(_snapshot, updated);
        _backgroundRefresh.UpdateSnapshot(_snapshot);
        RefreshView();
        ShowStatus("設定を保存しました。", InfoBarSeverity.Success);
    }

    private async Task ShowSourceManagerDialogAsync()
    {
        var rows = new List<SourceDraft>();
        var panel = new StackPanel { Spacing = 12, MaxWidth = 620 };
        foreach (var source in _snapshot.Sources)
        {
            var draft = new SourceDraft(source);
            rows.Add(draft);
            var box = new Border { Padding = new Thickness(10), BorderBrush = ColorBrush.FromHex("#E0E4DE"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(8) };
            var stack = new StackPanel { Spacing = 6 };
            stack.Children.Add(new TextBlock { Text = source.Title, FontSize = 16, TextWrapping = TextWrapping.Wrap });
            stack.Children.Add(new TextBlock
            {
                Text = $"最終成功: {RelativeTime(source.LastSucceededAt)} / 失敗: {source.LastError ?? "-"}",
                TextWrapping = TextWrapping.Wrap
            });
            stack.Children.Add(draft.Enabled);
            stack.Children.Add(draft.Notifications);
            stack.Children.Add(draft.CustomInterval);
            stack.Children.Add(SettingsButton("このサイトだけ更新", async () =>
            {
                var result = await _repository.RefreshSourceAsync(_snapshot, source.Id);
                _snapshot = result.Snapshot;
                _backgroundRefresh.UpdateSnapshot(_snapshot);
                RefreshView();
                ShowStatus($"{source.Title} を確認しました。", InfoBarSeverity.Success);
            }));
            box.Child = stack;
            panel.Children.Add(box);
        }

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Source管理",
            Content = new ScrollViewer { Content = panel, MaxHeight = 620 },
            PrimaryButtonText = "保存",
            CloseButtonText = "閉じる"
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        foreach (var draft in rows)
        {
            _snapshot = await _repository.UpdateSourceAsync(_snapshot, draft.SourceId, source => source with
            {
                IsEnabled = draft.Enabled.IsOn,
                NotificationsEnabled = draft.Notifications.IsOn,
                CustomIntervalMinutes = string.IsNullOrWhiteSpace(draft.CustomInterval.Text)
                    ? null
                    : ParseInt(draft.CustomInterval.Text, source.CustomIntervalMinutes ?? _snapshot.Settings.RefreshIntervalMinutes)
            });
        }

        _backgroundRefresh.UpdateSnapshot(_snapshot);
        RefreshView();
    }

    private async Task ExportDataAsync()
    {
        var picker = new FileSavePicker();
        InitializeWithWindow.Initialize(picker, _hwnd);
        picker.SuggestedFileName = $"tide-backup-{DateTimeOffset.Now:yyyyMMdd-HHmmss}";
        picker.FileTypeChoices.Add("JSON", [".json"]);
        var file = await picker.PickSaveFileAsync();
        if (file is null)
        {
            return;
        }

        await _repository.ExportAsync(file.Path, _snapshot);
        ShowStatus("データをエクスポートしました。", InfoBarSeverity.Success);
    }

    private async Task ImportDataAsync()
    {
        var picker = new FileOpenPicker();
        InitializeWithWindow.Initialize(picker, _hwnd);
        picker.FileTypeFilter.Add(".json");
        var file = await picker.PickSingleFileAsync();
        if (file is null)
        {
            return;
        }

        _snapshot = await _repository.ImportAsync(file.Path);
        _backgroundRefresh.UpdateSnapshot(_snapshot);
        RefreshView();
        ShowStatus("データをインポートしました。", InfoBarSeverity.Success);
    }

    private async Task RunBusyAsync(Func<Task> action, string failureTitle)
    {
        try
        {
            RootGrid.IsHitTestVisible = false;
            RefreshButton.IsEnabled = false;
            await action();
        }
        catch (Exception exception)
        {
            _logger.Warn(failureTitle, exception);
            ShowStatus($"{failureTitle}: {exception.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            RefreshButton.IsEnabled = true;
            RootGrid.IsHitTestVisible = true;
        }
    }

    private void WireServices()
    {
        _backgroundRefresh.StateChanged += (_, state) =>
            DispatcherQueue.TryEnqueue(() =>
            {
                _refreshState = state;
                RefreshView();
            });
        _backgroundRefresh.RefreshCompleted += (_, result) =>
            DispatcherQueue.TryEnqueue(() =>
            {
                _snapshot = result.Snapshot;
                RefreshView();
            });
        _tray.OpenRequested += (_, _) => DispatcherQueue.TryEnqueue(ShowAndActivate);
        _tray.RefreshRequested += (_, _) => DispatcherQueue.TryEnqueue(async () => await RefreshNowAsync(manual: true));
        _tray.PauseNotificationsRequested += (_, _) => DispatcherQueue.TryEnqueue(async () => await PauseNotificationsAsync(TimeSpan.FromHours(1)));
        _tray.PauseUntilNextRequested += (_, _) => DispatcherQueue.TryEnqueue(async () =>
        {
            var duration = _backgroundRefresh.NextRefreshAt is null
                ? TimeSpan.FromMinutes(_snapshot.Settings.RefreshIntervalMinutes)
                : _backgroundRefresh.NextRefreshAt.Value - DateTimeOffset.Now;
            await PauseNotificationsAsync(duration);
        });
        _tray.SettingsRequested += (_, _) => DispatcherQueue.TryEnqueue(async () => await ShowSettingsDialogAsync());
        _tray.QuitRequested += (_, _) => DispatcherQueue.TryEnqueue(QuitFully);
    }

    private void AppWindow_Closing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_allowExit || !_snapshot.Settings.CloseToTray)
        {
            return;
        }

        args.Cancel = true;
        HideToTray(showNotice: !_closeNoticeShown);
        _closeNoticeShown = true;
    }

    private void AppWindow_Changed(AppWindow sender, AppWindowChangedEventArgs args)
    {
        if (!_snapshot.Settings.MinimizeToTray || _allowExit)
        {
            return;
        }

        if (sender.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized })
        {
            HideToTray(showNotice: false);
        }
    }

    private void HideToTray(bool showNotice)
    {
        ShowWindow(_hwnd, SwHide);
        if (showNotice)
        {
            ShowStatus("Tideはバックグラウンドで新着を確認します。完全に終了するにはトレイメニューから終了してください。", InfoBarSeverity.Informational);
        }
    }

    private void ShowAndActivate()
    {
        ShowWindow(_hwnd, SwShow);
        ShowWindow(_hwnd, SwRestore);
        SetForegroundWindow(_hwnd);
        Activate();
    }

    private async void QuitFully()
    {
        _allowExit = true;
        _tray.Dispose();
        await _backgroundRefresh.DisposeAsync();
        Close();
    }

    private void OpenDataFolder()
    {
        PortablePaths.EnsureDataDirectory();
        Process.Start(new ProcessStartInfo(PortablePaths.DataDirectory) { UseShellExecute = true });
    }

    private void OpenLog()
    {
        PortablePaths.EnsureDataDirectory();
        if (!File.Exists(PortablePaths.AppLogPath))
        {
            File.WriteAllText(PortablePaths.AppLogPath, string.Empty);
        }

        Process.Start(new ProcessStartInfo(PortablePaths.AppLogPath) { UseShellExecute = true });
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Message = message;
        StatusBar.Severity = severity;
        StatusBar.IsOpen = true;
    }

    private void NotifyAll()
    {
        foreach (var name in new[]
                 {
                     nameof(PageTitle), nameof(PageEyebrow), nameof(TotalCount), nameof(UnreadCount),
                     nameof(SavedCount), nameof(TodayCount), nameof(WeekCount), nameof(FailedSourceCount),
                     nameof(WatchCount), nameof(StoriesCaption), nameof(UnreadCaption), nameof(UnreadPadded),
                     nameof(LastRefreshText), nameof(NextRefreshText), nameof(RadarStatusText),
                     nameof(EmptyEyebrow), nameof(EmptyTitle), nameof(EmptyMessage)
                 })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
    }

    private string FilterTitle() =>
        _filter switch
        {
            "unread" => "未読の新着",
            "saved" => "保存した記事",
            "today" => "今日の更新",
            "week" => "過去7日の更新",
            "failed" => "失敗中のサイト",
            "notified" => "通知済みの記事",
            "muted" => "ミュート中",
            _ => "新着の流れ"
        };

    private (string Eyebrow, string Title, string Message) EmptyCopy()
    {
        if (_snapshot.Sources.Count == 0)
        {
            return ("A QUIETER INBOX", "Webの新着を、\n静かに逃さない。", "最初のサイトを登録すると、RSSやページ内の記事候補を確認します。");
        }

        if (!string.IsNullOrWhiteSpace(_query))
        {
            return ("NO MATCHES", "検索結果がありません。", "タイトル、概要、Source名、URLを別の言葉で探してみてください。");
        }

        return _filter switch
        {
            "unread" => ("CLEAR", "未読はありません。", "今のところ見逃している新着はありません。"),
            "saved" => ("NO SAVED", "保存済みはありません。", "あとで読みたい記事はカード右下から保存できます。"),
            "failed" => ("NO FAILURES", "失敗中のサイトはありません。", "すべてのSourceが最後の確認で正常に応答しています。"),
            "muted" => ("NO MUTED SOURCES", "ミュート中のSourceはありません。", "Source管理から通知や取得をSourceごとに切り替えられます。"),
            _ => ("QUIET", "表示する記事がありません。", "フィルターを解除するか、別のSourceを選んでください。")
        };
    }

    private string RefreshStateIcon() =>
        _backgroundRefresh.State switch
        {
            RefreshActivityState.Refreshing => "↻",
            RefreshActivityState.Error => "!",
            RefreshActivityState.Paused => "Ⅱ",
            _ => "◷"
        };

    private static ToggleSwitch Toggle(string label, bool value) =>
        new() { Header = label, IsOn = value };

    private static TextBox NumberText(string value, string header) =>
        new() { Header = header, Text = value };

    private static Button SettingsButton(string text, Action action)
    {
        var button = new Button
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)Application.Current.Resources["QuietButtonStyle"]
        };
        button.Click += (_, _) => action();
        return button;
    }

    private static Button SettingsButton(string text, Func<Task> action)
    {
        var button = new Button
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Left,
            Style = (Style)Application.Current.Resources["QuietButtonStyle"]
        };
        button.Click += async (_, _) => await action();
        return button;
    }

    private static int ParseInt(string value, int fallback) =>
        int.TryParse(value, out var parsed) ? parsed : fallback;

    private static Dictionary<string, string> ParseArguments(string payload)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in payload.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pieces = part.Split('=', 2);
            if (pieces.Length == 2)
            {
                result[Uri.UnescapeDataString(pieces[0])] = Uri.UnescapeDataString(pieces[1]);
            }
        }

        return result;
    }

    private static string RelativeTime(DateTimeOffset? value)
    {
        if (value is null)
        {
            return "まだ更新されていません";
        }

        var elapsed = DateTimeOffset.UtcNow - value.Value;
        return elapsed.TotalMinutes switch
        {
            < 1 => "たった今",
            < 60 => $"{(int)elapsed.TotalMinutes}分前",
            < 1440 => $"{(int)elapsed.TotalHours}時間前",
            _ => $"{(int)elapsed.TotalDays}日前"
        };
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(nint hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hWnd);

    private sealed class SourceDraft
    {
        public SourceDraft(Source source)
        {
            SourceId = source.Id;
            Enabled = Toggle("取得する", source.IsEnabled);
            Notifications = Toggle("このSourceの通知", source.NotificationsEnabled);
            CustomInterval = NumberText(source.CustomIntervalMinutes?.ToString() ?? string.Empty, "取得間隔の上書き(分、省略可)");
        }

        public string SourceId { get; }
        public ToggleSwitch Enabled { get; }
        public ToggleSwitch Notifications { get; }
        public TextBox CustomInterval { get; }
    }
}

public sealed class SourceRowViewModel
{
    public SourceRowViewModel(Source source, int unreadCount)
    {
        Id = source.Id;
        Title = source.Title;
        Initial = InitialFor(source.Title);
        UnreadCount = unreadCount == 0 ? string.Empty : unreadCount.ToString();
        StatusText = SourceStatus(source);
        RemoveAutomationName = $"{source.Title}を削除";
        AccentBrush = ColorBrush.FromHex(source.Accent);
    }

    public string Id { get; }
    public string Title { get; }
    public string Initial { get; }
    public string UnreadCount { get; }
    public string StatusText { get; }
    public string RemoveAutomationName { get; }
    public SolidColorBrush AccentBrush { get; }

    private static string SourceStatus(Source source)
    {
        if (!source.IsEnabled)
        {
            return "停止中";
        }

        if (!source.NotificationsEnabled)
        {
            return "通知オフ";
        }

        return source.ConsecutiveFailureCount > 0 ? $"失敗 {source.ConsecutiveFailureCount}: {source.LastError}" : string.Empty;
    }

    private static string InitialFor(string value) =>
        string.IsNullOrWhiteSpace(value) ? "?" : value.Trim()[..1].ToUpperInvariant();
}

public sealed class StoryCardViewModel
{
    public StoryCardViewModel(Story story, Source source)
    {
        Id = story.Id;
        Title = story.Title;
        Summary = story.Summary;
        SourceTitle = source.Title;
        Initial = string.IsNullOrWhiteSpace(source.Title) ? "?" : source.Title.Trim()[..1].ToUpperInvariant();
        Host = new Uri(story.Url).Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase);
        RelativeTime = MainWindowTime.Relative(story.PublishedAt);
        DiscoveredText = $"発見 {MainWindowTime.Relative(story.DiscoveredAt)}";
        NewLabel = story.IsRead ? string.Empty : "NEW";
        SaveGlyph = story.IsSaved ? "◆" : "◇";
        ReadGlyph = story.IsRead ? "○" : "●";
        SaveAutomationName = story.IsSaved ? "保存を解除" : "保存";
        ReadAutomationName = story.IsRead ? "未読に戻す" : "既読にする";
        AccentBrush = ColorBrush.FromHex(source.Accent);
    }

    public string Id { get; }
    public string Title { get; }
    public string Summary { get; }
    public string SourceTitle { get; }
    public string Initial { get; }
    public string Host { get; }
    public string RelativeTime { get; }
    public string DiscoveredText { get; }
    public string NewLabel { get; }
    public string SaveGlyph { get; }
    public string ReadGlyph { get; }
    public string SaveAutomationName { get; }
    public string ReadAutomationName { get; }
    public SolidColorBrush AccentBrush { get; }
}

internal static class MainWindowTime
{
    public static string Relative(DateTimeOffset value)
    {
        var elapsed = DateTimeOffset.UtcNow - value;
        return elapsed.TotalMinutes switch
        {
            < 1 => "たった今",
            < 60 => $"{(int)elapsed.TotalMinutes}分前",
            < 1440 => $"{(int)elapsed.TotalHours}時間前",
            _ => $"{(int)elapsed.TotalDays}日前"
        };
    }
}

internal static class ColorBrush
{
    public static SolidColorBrush FromHex(string value)
    {
        var hex = value.TrimStart('#');
        return new SolidColorBrush(ColorHelper.FromArgb(
            255,
            Convert.ToByte(hex[..2], 16),
            Convert.ToByte(hex.Substring(2, 2), 16),
            Convert.ToByte(hex.Substring(4, 2), 16)));
    }
}
