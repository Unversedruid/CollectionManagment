using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;

namespace CollectionDowngrader;

internal sealed class SongPickerWindow : Window
{
    private readonly IReadOnlyList<BeatmapData> allSongs;
    private readonly ObservableCollection<BeatmapData> visibleSongs = new();
    private readonly ListBox songList = new() { SelectionMode = SelectionMode.Multiple };
    private readonly TextBox searchBox = new() { Watermark = "Search artist, song, difficulty, or hash…" };

    public IReadOnlyList<BeatmapData> SelectedSongs { get; private set; } = Array.Empty<BeatmapData>();

    public SongPickerWindow(IReadOnlyList<BeatmapData> songs)
    {
        allSongs = songs;
        Title = "Add songs to collection";
        Width = 720;
        Height = 560;
        MinWidth = 520;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        songList.ItemsSource = visibleSongs;
        songList.ItemTemplate = new FuncDataTemplate<BeatmapData>((song, _) =>
            new TextBlock
            {
                Text = song?.DisplayLabel,
                TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 5)
            });

        var header = new StackPanel { Spacing = 8, Margin = new Thickness(0, 0, 0, 10) };
        header.Children.Add(new TextBlock
        {
            Text = "Choose beatmaps to add",
            FontSize = 18,
            FontWeight = Avalonia.Media.FontWeight.SemiBold
        });
        header.Children.Add(searchBox);

        var addButton = new Button { Content = "Add selected", IsEnabled = false };
        addButton.Classes.Add("accent");
        var cancelButton = new Button { Content = "Cancel" };
        var footer = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 10, 0, 0)
        };
        footer.Children.Add(cancelButton);
        footer.Children.Add(addButton);

        var layout = new DockPanel { Margin = new Thickness(16), LastChildFill = true };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(footer, Dock.Bottom);
        layout.Children.Add(header);
        layout.Children.Add(footer);
        layout.Children.Add(songList);
        Content = layout;

        searchBox.TextChanged += (_, _) => RefreshSongs();
        songList.SelectionChanged += (_, _) => addButton.IsEnabled = songList.SelectedItems?.Count > 0;
        addButton.Click += (_, _) =>
        {
            SelectedSongs = songList.SelectedItems?.Cast<BeatmapData>().ToArray() ?? Array.Empty<BeatmapData>();
            Close(true);
        };
        cancelButton.Click += (_, _) => Close(false);

        RefreshSongs();
    }

    private void RefreshSongs()
    {
        var filter = searchBox.Text?.Trim();
        visibleSongs.Clear();
        foreach (var song in allSongs.Where(song =>
                     string.IsNullOrEmpty(filter)
                     || song.Hash.Contains(filter, StringComparison.OrdinalIgnoreCase)
                     || song.Artist.Contains(filter, StringComparison.OrdinalIgnoreCase)
                     || song.SongAndDifficulty.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            visibleSongs.Add(song);
        }
    }
}
