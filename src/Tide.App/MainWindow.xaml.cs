using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tide.Core;

namespace Tide.App;

public sealed partial class MainWindow : Window, INotifyPropertyChanged
{
    private readonly TideRepository _repository = new();
    private Snapshot _snapshot = new();
    private string _filter = "all";
    private string _activeSource = "all";
    private string _query = string.Empty;

    public ObservableCollection<SourceRowViewModel> SourceRows { get; } = [];
    public ObservableCollection<StoryCardViewModel> StoryCards { get; } = [];

    public string PageTitle => _activeSource == "all"
        ? "新着の流れ"
        : _snapshot.Sources.FirstOrDefault(source => source.Id == _activeSource)?.Title ?? "新着の流れ";
    public string PageEyebrow => _activeSource == "all" ? "PERSONAL SIGNALS" : "WATCHED SOURCE";
    public string TodayText { get; } = DateTimeOffset.Now.ToString("M月d日 dddd");
    public string TotalCount => _snapshot.Stories.Count.ToString();
    public string UnreadCount => _snapshot.Stories.Count(story => !story.IsRead).ToString();
    public string SavedCount => _snapshot.Stories.Count(story => story.IsSaved).ToString();
    public string WatchCount => _snapshot.Sources.Count.ToString();
    public string StoriesCaption => $"{StoryCards.Count} STORIES";
    public string UnreadCaption => $"{UnreadCount} UNREAD";
    public string UnreadPadded => _snapshot.Stories.Count(story => !story.IsRead).ToString("00");
    public string LastRefreshText => $"◷  最終確認 {RelativeTime(_snapshot.LastRefreshedAt)}";

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainWindow()
    {
        InitializeComponent();
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        _snapshot = await _repository.LoadAsync();
        RefreshView();
    }

    private async void AddSource_Click(object sender, RoutedEventArgs e)
    {
        var input = new TextBox { PlaceholderText = "example.com/journal" };
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "気になるサイトを追加",
            Content = input,
            PrimaryButtonText = "ウォッチを始める",
            CloseButtonText = "キャンセル",
            DefaultButton = ContentDialogButton.Primary
        };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary || string.IsNullOrWhiteSpace(input.Text))
        {
            return;
        }

        await RunBusyAsync(async () =>
        {
            _snapshot = await _repository.AddSourceAsync(_snapshot, input.Text);
            RefreshView();
            ShowStatus("新しいサイトを追加しました。", InfoBarSeverity.Success);
        }, "サイトを追加できませんでした");
    }

    private async void Settings_Click(object sender, RoutedEventArgs e)
    {
        var openFolder = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            Content = "保存フォルダを開く",
            Style = (Style)Application.Current.Resources["QuietButtonStyle"]
        };
        openFolder.Click += (_, _) =>
        {
            PortablePaths.MigrateLegacyData();
            PortablePaths.EnsureDataDirectory();
            Process.Start(new ProcessStartInfo(PortablePaths.DataDirectory) { UseShellExecute = true });
        };

        var location = new TextBox
        {
            IsReadOnly = true,
            Text = PortablePaths.DataDirectory,
            TextWrapping = TextWrapping.Wrap
        };
        var content = new StackPanel { Spacing = 10 };
        content.Children.Add(new TextBlock
        {
            Text = "ポータブルモード",
            FontSize = 18,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold
        });
        content.Children.Add(new TextBlock
        {
            Text = "設定と取得済みの新着情報は、アプリ本体と同じフォルダ内の Data に保存します。",
            TextWrapping = TextWrapping.Wrap
        });
        content.Children.Add(location);
        content.Children.Add(openFolder);

        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "環境設定",
            Content = content,
            CloseButtonText = "閉じる"
        };
        await dialog.ShowAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await RunBusyAsync(async () =>
        {
            var result = await _repository.RefreshAsync(_snapshot);
            _snapshot = result.Snapshot;
            RefreshView();
            ShowStatus(result.FailedSources.Count == 0
                ? "新着情報を確認しました。"
                : $"{result.FailedSources.Count}件のサイトを更新できませんでした。",
                result.FailedSources.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        }, "更新に失敗しました");
    }

    private void AllStories_Click(object sender, RoutedEventArgs e) => ApplyFilter("all");
    private void UnreadStories_Click(object sender, RoutedEventArgs e) => ApplyFilter("unread");
    private void SavedStories_Click(object sender, RoutedEventArgs e) => ApplyFilter("saved");

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
        if (sourceId is null)
        {
            return;
        }

        _snapshot = await _repository.RemoveSourceAsync(_snapshot, sourceId);
        if (_activeSource == sourceId)
        {
            _activeSource = "all";
        }
        RefreshView();
    }

    private async void MarkAllRead_Click(object sender, RoutedEventArgs e)
    {
        _snapshot = await _repository.MarkAllReadAsync(_snapshot);
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
        RefreshView();
    }

    private async void OpenStory_Click(object sender, RoutedEventArgs e)
    {
        var storyId = (sender as FrameworkElement)?.Tag as string;
        var story = _snapshot.Stories.FirstOrDefault(candidate => candidate.Id == storyId);
        if (story is null)
        {
            return;
        }

        _snapshot = await _repository.MarkReadAsync(_snapshot, story.Id);
        RefreshView();
        Process.Start(new ProcessStartInfo(story.Url) { UseShellExecute = true });
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _query = (sender as TextBox)?.Text ?? string.Empty;
        RefreshView();
    }

    private void ApplyFilter(string filter)
    {
        _filter = filter;
        RefreshView();
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
        var visibleStories = _snapshot.Stories
            .Where(story => _activeSource == "all" || story.SourceId == _activeSource)
            .Where(story => _filter != "unread" || !story.IsRead)
            .Where(story => _filter != "saved" || story.IsSaved)
            .Where(story =>
            {
                var source = sources.GetValueOrDefault(story.SourceId);
                return $"{story.Title} {story.Summary} {source?.Title}"
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
        NotifyAll();
    }

    private async Task RunBusyAsync(Func<Task> action, string failureTitle)
    {
        try
        {
            RootGrid.IsHitTestVisible = false;
            await action();
        }
        catch (Exception exception)
        {
            ShowStatus($"{failureTitle}: {exception.Message}", InfoBarSeverity.Error);
        }
        finally
        {
            RootGrid.IsHitTestVisible = true;
        }
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
                     nameof(SavedCount), nameof(WatchCount), nameof(StoriesCaption), nameof(UnreadCaption),
                     nameof(UnreadPadded), nameof(LastRefreshText)
                 })
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        }
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
}

public sealed class SourceRowViewModel
{
    public SourceRowViewModel(Source source, int unreadCount)
    {
        Id = source.Id;
        Title = source.Title;
        Initial = source.Title[..1].ToUpperInvariant();
        UnreadCount = unreadCount == 0 ? string.Empty : unreadCount.ToString();
        AccentBrush = ColorBrush.FromHex(source.Accent);
    }

    public string Id { get; }
    public string Title { get; }
    public string Initial { get; }
    public string UnreadCount { get; }
    public SolidColorBrush AccentBrush { get; }
}

public sealed class StoryCardViewModel
{
    public StoryCardViewModel(Story story, Source source)
    {
        Id = story.Id;
        Title = story.Title;
        Summary = story.Summary;
        SourceTitle = source.Title;
        Initial = source.Title[..1].ToUpperInvariant();
        Host = new Uri(story.Url).Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase);
        RelativeTime = MainWindowTime.Relative(story.PublishedAt);
        NewLabel = story.IsRead ? string.Empty : "NEW";
        SaveGlyph = story.IsSaved ? "◆" : "◇";
        AccentBrush = ColorBrush.FromHex(source.Accent);
    }

    public string Id { get; }
    public string Title { get; }
    public string Summary { get; }
    public string SourceTitle { get; }
    public string Initial { get; }
    public string Host { get; }
    public string RelativeTime { get; }
    public string NewLabel { get; }
    public string SaveGlyph { get; }
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
