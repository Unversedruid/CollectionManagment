using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO.Compression;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;

namespace CollectionDowngrader;

public partial class MainWindow : Window
{
    private static readonly HttpClient beatmapDownloadClient = new(new HttpClientHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false
    })
    {
        Timeout = TimeSpan.FromMinutes(10)
    };
    private readonly SemaphoreSlim downloadSemaphore = new(5, 5);

    private enum SortColumn
    {
        Artist,
        SongName,
        StarRating
    }

    private readonly ObservableCollection<CollectionData> visibleCollections = new();
    private readonly ObservableCollection<BeatmapData> visibleBeatmaps = new();
    private readonly ObservableCollection<CollectionData> downloadCollections = new();
    private readonly ObservableCollection<BeatmapSetDownloadEntry> downloadSetEntries = new();
    private readonly Dictionary<string, BeatmapData> importedBeatmapMetadata = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<CollectionData> collections = Array.Empty<CollectionData>();
    private IReadOnlyList<BeatmapData> allBeatmaps = Array.Empty<BeatmapData>();
    private CollectionData? selectedCollection;
    private CollectionData? downloadSelectedCollection;
    private CollectionData? contextCollection;
    private BeatmapData? contextBeatmap;
    private string? loadedRealmPath;
    private bool isLoading;
    private bool isDownloadQueueRunning;
    private bool isRefreshingDownloadCollections;
    private SortColumn? sortColumn;
    private bool sortAscending = true;

    public MainWindow()
        : this(null)
    {
    }

    public MainWindow(string? initialRealmPath)
    {
        InitializeComponent();
        CollectionList.ItemsSource = visibleCollections;
        HashList.ItemsSource = visibleBeatmaps;
        DownloadCollectionComboBox.ItemsSource = downloadCollections;
        DownloadSetList.ItemsSource = downloadSetEntries;

        OpenButton.Click += async (_, _) => await OpenRealmAsync();
        ImportButton.Click += async (_, _) => await ImportCollectionDatabaseAsync();
        DownloadAllButton.Click += async (_, _) => await DownloadAllBeatmapsetsAsync();
        Closed += (_, _) => OsuSessionCookieBox.Text = string.Empty;
        CollectionSearch.TextChanged += (_, _) => RefreshCollectionList();
        HashSearch.TextChanged += (_, _) => RefreshHashList();
        CollectionList.SelectionChanged += (_, _) => SelectCollection();
        DownloadCollectionComboBox.SelectionChanged += (_, _) =>
        {
            if (isRefreshingDownloadCollections)
            {
                return;
            }

            downloadSelectedCollection = DownloadCollectionComboBox.SelectedItem as CollectionData;
            RefreshDownloadsPanel();
        };
        HashList.SelectionChanged += (_, _) => RemoveSongButton.IsEnabled = selectedCollection is not null
                                                                              && !selectedCollection.IsVirtual
                                                                              && HashList.SelectedItem is BeatmapData
                                                                              && !isLoading;
        StarRatingRange.RangeChanged += (_, _) =>
        {
            StarRatingRangeLabel.Text = StarRatingRange.IsFiltering
                ? $"{StarRatingRange.LowerValue:0.0}–{StarRatingRange.UpperValue:0.0}★"
                : "All ratings";
            RefreshHashList();
        };
        ClearStarRatingButton.Click += (_, _) =>
        {
            StarRatingRange.ClearRange();
        };
        AddSongsButton.Click += async (_, _) => await AddSongsAsync();
        RemoveSongButton.Click += async (_, _) => await RemoveSelectedSongAsync();
        ArtistSortButton.Click += (_, _) => SortBy(SortColumn.Artist);
        SongSortButton.Click += (_, _) => SortBy(SortColumn.SongName);
        RatingSortButton.Click += (_, _) => SortBy(SortColumn.StarRating);

        if (!string.IsNullOrWhiteSpace(initialRealmPath))
        {
            _ = LoadRealmAsync(initialRealmPath);
        }
    }

    private async Task OpenRealmAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open osu!lazer client.realm",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Realm database") { Patterns = ["*.realm"] },
                new FilePickerFileType("All files") { Patterns = ["*"] }
            ]
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            await LoadRealmAsync(path);
        }
    }

    private void CollectionContextMenu_Opening(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            contextCollection = null;
            return;
        }

        contextCollection = (menu.PlacementTarget as Control)?.DataContext as CollectionData
                           ?? menu.DataContext as CollectionData
                           ?? CollectionList.SelectedItem as CollectionData;
        if (contextCollection is null)
        {
            e.Cancel = true;
            return;
        }

        var filtered = collections.Where(collection =>
            string.IsNullOrEmpty(CollectionSearch.Text?.Trim())
            || collection.Name.Contains(CollectionSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
        CollectionList.SelectedIndex = filtered.FindIndex(collection => collection.Id == contextCollection.Id);
        var deleteItem = menu.Items.OfType<MenuItem>()
            .FirstOrDefault(item => Equals(item.Header, "Delete collection…"));
        if (deleteItem is not null)
        {
            deleteItem.IsEnabled = !isLoading && !contextCollection.IsVirtual;
        }
    }

    private async void DeleteCollection_Click(object? sender, RoutedEventArgs e)
    {
        var collection = contextCollection;
        var realmPath = loadedRealmPath;
        if (collection is null || collection.IsVirtual || realmPath is null || isLoading)
        {
            return;
        }

        var confirmed = await ConfirmCollectionDeletionAsync(collection.Name);
        if (!confirmed || isLoading)
        {
            return;
        }

        isLoading = true;
        ImportButton.IsEnabled = false;
        AddSongsButton.IsEnabled = false;
        RemoveSongButton.IsEnabled = false;
        StatusLabel.Foreground = null;
        StatusLabel.Text = $"Deleting {collection.Name}…";
        try
        {
            await Task.Run(() => RealmCollectionEditor.Delete(realmPath, collection.Id));
            if (await LoadRealmAsync(realmPath))
            {
                StatusLabel.Text = $"Deleted collection {collection.Name}.";
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Foreground = Avalonia.Media.Brushes.IndianRed;
            StatusLabel.Text = ex.Message;
        }
        finally
        {
            isLoading = false;
            ImportButton.IsEnabled = loadedRealmPath is not null;
            AddSongsButton.IsEnabled = selectedCollection is not null && !selectedCollection.IsVirtual;
            RemoveSongButton.IsEnabled = selectedCollection is not null && !selectedCollection.IsVirtual
                                         && HashList.SelectedItem is BeatmapData;
        }
    }

    private async Task<bool> ConfirmCollectionDeletionAsync(string collectionName)
    {
        var cancelButton = new Button { Content = "Cancel" };
        var deleteButton = new Button { Content = "Delete", IsDefault = true };
        deleteButton.Classes.Add("accent");
        var dialog = new Window
        {
            Title = "Delete collection",
            Width = 420,
            Height = 180,
            MinWidth = 420,
            MinHeight = 180,
            MaxWidth = 420,
            MaxHeight = 180,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Avalonia.Thickness(20),
                Spacing = 18,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"Delete ‘{collectionName}’? This removes the collection from the selected client.realm file.",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap
                    },
                    new StackPanel
                    {
                        Orientation = Avalonia.Layout.Orientation.Horizontal,
                        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
                        Spacing = 8,
                        Children =
                        {
                            cancelButton,
                            deleteButton
                        }
                    }
                }
            }
        };

        cancelButton.Click += (_, _) => dialog.Close(false);
        deleteButton.Click += (_, _) => dialog.Close(true);
        return await dialog.ShowDialog<bool>(this);
    }

    private async void ExportCollection_Click(object? sender, RoutedEventArgs e)
    {
        var collection = contextCollection;
        if (collection is null || isLoading)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export collection",
            SuggestedFileName = $"{MakeSafeFileName(collection.Name)}.cmcollection",
            DefaultExtension = "cmcollection",
            FileTypeChoices = [new FilePickerFileType("Collection Manager package") { Patterns = ["*.cmcollection"] }]
        });

        if (file?.TryGetLocalPath() is not { } path)
        {
            return;
        }

        try
        {
            var exported = new LegacyCollection(
                collection.Name,
                collection.Beatmaps.Select(beatmap => beatmap.Hash).ToArray(),
                collection.Beatmaps);
            await Task.Run(() => CollectionDbCodec.WritePackage(path, exported, overwrite: true));
            StatusLabel.Foreground = null;
            StatusLabel.Text = $"Exported {collection.Name} with download metadata to {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            StatusLabel.Foreground = Avalonia.Media.Brushes.IndianRed;
            StatusLabel.Text = ex.Message;
        }
    }

    private async Task ImportCollectionDatabaseAsync()
    {
        var realmPath = loadedRealmPath;
        if (realmPath is null || isLoading)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import osu! collection",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Collection Manager package or osu! collection database")
                {
                    Patterns = ["*.cmcollection", "*.db"]
                }
            ]
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } importPath)
        {
            return;
        }

        var preferredCollectionId = selectedCollection?.Id;
        isLoading = true;
        ImportButton.IsEnabled = false;
        AddSongsButton.IsEnabled = false;
        RemoveSongButton.IsEnabled = false;
        StatusLabel.Foreground = null;
        StatusLabel.Text = "Importing collections…";
        try
        {
            var imported = await Task.Run(() => CollectionDbCodec.Read(importPath));
            await Task.Run(() => RealmCollectionImporter.Import(realmPath, imported));
            foreach (var beatmap in imported.SelectMany(collection => collection.Beatmaps ?? Array.Empty<BeatmapData>()))
            {
                importedBeatmapMetadata[beatmap.Hash] = beatmap;
            }

            if (await LoadRealmAsync(realmPath, preferredCollectionId))
            {
                var importedCollectionName = imported.FirstOrDefault()?.Name;
                if (!string.IsNullOrWhiteSpace(importedCollectionName))
                {
                    SelectCollectionByName(importedCollectionName);
                    StatusLabel.Text = $"Imported {imported.Count} collection(s) from {Path.GetFileName(importPath)}. Selected {importedCollectionName} to review missing beatmaps in the Imports tab.";
                }
                else
                {
                    StatusLabel.Text = $"Imported {imported.Count} collection(s) from {Path.GetFileName(importPath)}.";
                }
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Foreground = Avalonia.Media.Brushes.IndianRed;
            StatusLabel.Text = ex.Message;
        }
        finally
        {
            isLoading = false;
            ImportButton.IsEnabled = loadedRealmPath is not null;
            AddSongsButton.IsEnabled = selectedCollection is not null && !selectedCollection.IsVirtual;
            RemoveSongButton.IsEnabled = selectedCollection is not null && !selectedCollection.IsVirtual
                                         && HashList.SelectedItem is BeatmapData;
        }
    }

    private static string MakeSafeFileName(string name)
    {
        var invalidCharacters = Path.GetInvalidFileNameChars().ToHashSet();
        var safeName = new string(name.Select(character => invalidCharacters.Contains(character) ? '_' : character).ToArray());
        return string.IsNullOrWhiteSpace(safeName) ? "collection" : safeName;
    }

    private void SelectCollectionByName(string name)
    {
        var collection = collections.FirstOrDefault(candidate =>
            !candidate.IsVirtual && string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase));
        if (collection is null)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(CollectionSearch.Text))
        {
            CollectionSearch.Text = string.Empty;
        }

        CollectionList.SelectedIndex = visibleCollections.ToList()
            .FindIndex(candidate => candidate.Id == collection.Id);
    }

    private static BeatmapData MergeBeatmapMetadata(BeatmapData local, BeatmapData imported) => new(
        local.Hash,
        string.IsNullOrWhiteSpace(local.Artist) ? imported.Artist : local.Artist,
        string.IsNullOrWhiteSpace(local.SongName) ? imported.SongName : local.SongName,
        string.IsNullOrWhiteSpace(local.DifficultyName) ? imported.DifficultyName : local.DifficultyName,
        local.StarRating < 0 ? imported.StarRating : local.StarRating,
        local.OnlineSetId > 0 ? local.OnlineSetId : imported.OnlineSetId,
        string.IsNullOrWhiteSpace(local.Mapper) ? imported.Mapper : local.Mapper);

    private async Task<bool> LoadRealmAsync(string path, Guid? preferredCollectionId = null)
    {
        isLoading = true;
        var loaded = false;
        OpenButton.IsEnabled = false;
        ImportButton.IsEnabled = false;
        AddSongsButton.IsEnabled = false;
        RemoveSongButton.IsEnabled = false;
        StatusLabel.Foreground = null;
        StatusLabel.Text = "Reading collections…";
        PathLabel.Text = path;
        selectedCollection = null;
        collections = Array.Empty<CollectionData>();
        RefreshCollectionList();
        RefreshHashList();
        SelectedCollectionLabel.Text = "Select a collection";

        try
        {
            var snapshot = await Task.Run(() => RealmCollectionReader.Read(path));
            loaded = true;
            loadedRealmPath = path;
            allBeatmaps = snapshot.Beatmaps;
            collections = snapshot.Collections.Select(collection => collection.IsVirtual
                ? collection
                : collection with
                {
                    Beatmaps = collection.Beatmaps.Select(beatmap =>
                        importedBeatmapMetadata.TryGetValue(beatmap.Hash, out var metadata)
                            ? MergeBeatmapMetadata(beatmap, metadata)
                            : beatmap).ToArray()
                }).ToArray();
            StatusLabel.Text = $"Loaded {collections.Count} collections.";
            RefreshCollectionList();
            if (visibleCollections.Count > 0)
            {
                var selectedIndex = preferredCollectionId is Guid id
                    ? collections
                        .Where(c => string.IsNullOrEmpty(CollectionSearch.Text?.Trim())
                                    || c.Name.Contains(CollectionSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase))
                        .ToList()
                        .FindIndex(c => c.Id == id)
                    : -1;
                CollectionList.SelectedIndex = selectedIndex >= 0 ? selectedIndex : 0;
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Foreground = Avalonia.Media.Brushes.IndianRed;
            StatusLabel.Text = ex.Message;
        }
        finally
        {
            isLoading = false;
            OpenButton.IsEnabled = true;
            ImportButton.IsEnabled = loadedRealmPath is not null;
            AddSongsButton.IsEnabled = selectedCollection is not null && !selectedCollection.IsVirtual;
            RemoveSongButton.IsEnabled = selectedCollection is not null && !selectedCollection.IsVirtual
                                         && HashList.SelectedItem is BeatmapData;
        }

        return loaded;
    }

    private void RefreshCollectionList()
    {
        var filter = CollectionSearch.Text?.Trim();
        var selectedId = selectedCollection?.Id;
        visibleCollections.Clear();
        foreach (var collection in collections.Where(c =>
                     string.IsNullOrEmpty(filter) || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            visibleCollections.Add(collection);
        }

        if (selectedId is Guid id)
        {
            var index = collections
                .Where(c => string.IsNullOrEmpty(filter) || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
                .ToList()
                .FindIndex(c => c.Id == id);
            CollectionList.SelectedIndex = index;
        }
    }

    private void SelectCollection()
    {
        var filter = CollectionSearch.Text?.Trim();
        var filtered = collections
            .Where(c => string.IsNullOrEmpty(filter) || c.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var index = CollectionList.SelectedIndex;
        selectedCollection = index >= 0 && index < filtered.Count ? filtered[index] : null;
        SelectedCollectionLabel.Text = selectedCollection is null
            ? "Select a collection"
            : $"{selectedCollection.Name}  ·  {selectedCollection.Beatmaps.Count} maps";
        HashSearch.IsEnabled = selectedCollection is not null;
        StarRatingRange.IsEnabled = selectedCollection is not null;
        ClearStarRatingButton.IsEnabled = selectedCollection is not null;
        AddSongsButton.IsEnabled = selectedCollection is not null && !selectedCollection.IsVirtual && !isLoading;
        RemoveSongButton.IsEnabled = selectedCollection is not null && !selectedCollection.IsVirtual
                                     && HashList.SelectedItem is BeatmapData && !isLoading;
        RefreshHashList();
        RefreshDownloadCollectionPicker(selectedCollection);
    }

    private void RefreshDownloadCollectionPicker(CollectionData? preferredCollection = null)
    {
        isRefreshingDownloadCollections = true;
        downloadCollections.Clear();
        foreach (var collection in collections)
        {
            downloadCollections.Add(collection);
        }

        var target = preferredCollection ?? downloadSelectedCollection;
        var index = target is null
            ? (downloadCollections.Count > 0 ? 0 : -1)
            : downloadCollections.ToList().FindIndex(collection => collection.Id == target.Id);
        if (index < 0 && downloadCollections.Count > 0)
        {
            index = 0;
        }

        DownloadCollectionComboBox.SelectedIndex = index;
        downloadSelectedCollection = DownloadCollectionComboBox.SelectedItem as CollectionData;
        isRefreshingDownloadCollections = false;
        RefreshDownloadsPanel();
    }

    private void RefreshDownloadsPanel()
    {
        downloadSetEntries.Clear();
        if (downloadSelectedCollection is null)
        {
            DownloadSummaryLabel.Text = "No collection selected.";
            DownloadAllButton.IsEnabled = false;
            DownloadsStatusLabel.Text = string.Empty;
            return;
        }

        var maps = downloadSelectedCollection.Beatmaps;
        var installedHashes = allBeatmaps
            .Select(beatmap => beatmap.Hash)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mapsToDownload = maps
            .Where(beatmap => !installedHashes.Contains(beatmap.Hash))
            .ToArray();
        var beatmapsets = mapsToDownload.Where(beatmap => beatmap.OnlineSetId > 0)
            .GroupBy(beatmap => beatmap.OnlineSetId)
            .Select(group => new { OnlineId = group.Key, Beatmap = group.First() })
            .OrderBy(group => group.Beatmap.SongName, StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
        var alreadyInstalledCount = maps.Count - mapsToDownload.Length;
        var unresolvedCount = mapsToDownload.Count(beatmap => beatmap.OnlineSetId <= 0);
        DownloadSummaryLabel.Text = $"{downloadSelectedCollection.Name}: {mapsToDownload.Length} beatmap hash(es) not in All beatmaps, {beatmapsets.Length} unique beatmapset(s) to download, {alreadyInstalledCount} already present, {unresolvedCount} unresolved.";
        DownloadAllButton.IsEnabled = beatmapsets.Length > 0 && !isDownloadQueueRunning;
        DownloadsStatusLabel.Text = beatmapsets.Length > 0
            ? "Select a beatmapset below to download and import it through osu!'s .osz handler."
            : mapsToDownload.Length == 0
                ? "All beatmaps in this collection are already present in osu!."
                : "No beatmapset IDs are available for the remaining beatmaps.";

        foreach (var beatmapset in beatmapsets)
        {
            downloadSetEntries.Add(new BeatmapSetDownloadEntry(
                beatmapset.OnlineId,
                beatmapset.Beatmap.SongName,
                beatmapset.Beatmap.Mapper,
                $"https://osu.ppy.sh/beatmapsets/{beatmapset.OnlineId}/download"));
        }
    }

    private async void DownloadAndOpenBeatmapset_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: BeatmapSetDownloadEntry entry })
        {
            return;
        }

        await DownloadBeatmapsetAsync(entry);
    }

    private async Task DownloadAllBeatmapsetsAsync()
    {
        if (isDownloadQueueRunning)
        {
            return;
        }

        var queue = downloadSetEntries
            .Where(entry => !entry.IsComplete && !entry.IsDownloading && !entry.IsQueued)
            .ToArray();
        if (queue.Length == 0)
        {
            return;
        }

        isDownloadQueueRunning = true;
        DownloadAllButton.IsEnabled = false;
        foreach (var entry in queue)
        {
            entry.IsQueued = true;
            entry.StatusText = "Queued";
        }

        DownloadsStatusLabel.Text = $"Queued {queue.Length} beatmapset(s); downloading up to five at a time…";
        try
        {
            await Task.WhenAll(queue.Select(entry => DownloadBeatmapsetAsync(entry, alreadyQueued: true)));
            var downloadedCount = queue.Count(entry => entry.IsComplete);
            var failedCount = queue.Length - downloadedCount;
            DownloadsStatusLabel.Text = failedCount == 0
                ? $"Download queue complete: {downloadedCount} beatmapset(s) downloaded. Check each row for its import status."
                : $"Download queue complete: {downloadedCount} downloaded; {failedCount} failed. Failed rows can be retried.";
        }
        finally
        {
            isDownloadQueueRunning = false;
            DownloadAllButton.IsEnabled = downloadSetEntries.Any(entry => !entry.IsComplete);
        }
    }

    private async Task DownloadBeatmapsetAsync(BeatmapSetDownloadEntry entry, bool alreadyQueued = false)
    {
        if ((!alreadyQueued && (entry.IsQueued || entry.IsDownloading || entry.IsComplete))
            || !Uri.TryCreate(entry.DownloadUrl, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || uri.Host != "osu.ppy.sh")
        {
            return;
        }

        entry.IsQueued = true;
        if (!alreadyQueued)
        {
            entry.StatusText = "Queued";
        }

        var hasSemaphore = false;
        var partialPath = string.Empty;
        entry.IsComplete = false;
        entry.Progress = 0;
        try
        {
            await downloadSemaphore.WaitAsync();
            hasSemaphore = true;
            entry.IsQueued = false;
            entry.IsDownloading = true;
            entry.StatusText = "Starting download…";
            var sessionCookie = NormalizeSessionCookie(OsuSessionCookieBox.Text);
            if (!string.IsNullOrEmpty(sessionCookie)
                && (sessionCookie.Contains('\r') || sessionCookie.Contains('\n')))
            {
                throw new InvalidDataException("The osu! Cookie must be a single line.");
            }

            var downloadsFolder = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads",
                "CollectionManager");
            Directory.CreateDirectory(downloadsFolder);
            var fileName = $"{entry.OnlineId} - {MakeSafeFileName(entry.SongNameText)}.osz";
            var outputPath = GetAvailableDownloadPath(Path.Combine(downloadsFolder, fileName));
            partialPath = $"{outputPath}.{Guid.NewGuid():N}.part";

            if (!isDownloadQueueRunning)
            {
                DownloadsStatusLabel.Text = $"Downloading beatmapset {entry.OnlineId}…";
            }
            using var response = await GetBeatmapDownloadResponseAsync(uri, entry.OnlineId, sessionCookie);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            {
                throw new InvalidDataException("osu! rejected the download request. Sign in to osu.ppy.sh, copy a fresh Cookie header, and try again.");
            }

            response.EnsureSuccessStatusCode();
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is "text/html" or "application/json")
            {
                throw new InvalidDataException("osu! returned a sign-in or verification page instead of a beatmap archive. Check the Cookie header or complete the verification in your browser.");
            }

            if (response.Content.Headers.ContentLength is > 2_147_483_648)
            {
                throw new InvalidDataException("The beatmap archive exceeds the 2 GiB download limit.");
            }

            var expectedBytes = response.Content.Headers.ContentLength;
            entry.StatusText = expectedBytes is > 0 ? "Downloading 0%" : "Downloading…";

            await using (var responseStream = await response.Content.ReadAsStreamAsync())
            await using (var output = new FileStream(
                             partialPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             81920,
                             FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                var buffer = new byte[81920];
                long totalBytes = 0;
                long lastReportedBytes = 0;
                int bytesRead;
                while ((bytesRead = await responseStream.ReadAsync(buffer)) > 0)
                {
                    totalBytes += bytesRead;
                    if (totalBytes > 2_147_483_648)
                    {
                        throw new InvalidDataException("The beatmap archive exceeds the 2 GiB download limit.");
                    }

                    await output.WriteAsync(buffer.AsMemory(0, bytesRead));
                    if (expectedBytes is > 0)
                    {
                        var percent = Math.Min(99, totalBytes * 100d / expectedBytes.Value);
                        if (percent - entry.Progress >= 0.5 || totalBytes == expectedBytes.Value)
                        {
                            entry.Progress = percent;
                            entry.StatusText = $"Downloading {percent:0}%";
                        }
                    }
                    else if (totalBytes - lastReportedBytes >= 512 * 1024)
                    {
                        lastReportedBytes = totalBytes;
                        entry.StatusText = $"Downloading {totalBytes / (1024d * 1024d):0.0} MB";
                    }
                }
            }

            entry.StatusText = "Checking archive…";
            try
            {
                ValidateBeatmapArchive(partialPath, entry.OnlineId);
            }
            catch (InvalidDataException ex)
            {
                throw new InvalidDataException("osu! did not return a valid .osz archive. It may have returned an authentication or verification page; check the Cookie header and try again.", ex);
            }
            File.Move(partialPath, outputPath);
            partialPath = string.Empty;
            entry.Progress = 100;
            entry.IsComplete = true;
            entry.StatusText = "Downloaded";
            try
            {
                Process.Start(new ProcessStartInfo(outputPath) { UseShellExecute = true });
                entry.StatusText = "Opened with .osz handler";
                if (!isDownloadQueueRunning)
                {
                    DownloadsStatusLabel.Text = $"Downloaded and opened {Path.GetFileName(outputPath)}. osu!lazer should import it through the registered .osz handler.";
                }
            }
            catch (Exception ex)
            {
                entry.StatusText = "Downloaded; open failed";
                if (!isDownloadQueueRunning)
                {
                    DownloadsStatusLabel.Text = $"Downloaded to {outputPath}, but couldn't open it: {ex.Message}";
                }
            }
        }
        catch (Exception ex)
        {
            entry.Progress = 0;
            entry.StatusText = "Failed";
            if (!isDownloadQueueRunning)
            {
                DownloadsStatusLabel.Text = ex.Message;
            }
        }
        finally
        {
            if (!string.IsNullOrEmpty(partialPath))
            {
                try
                {
                    File.Delete(partialPath);
                }
                catch
                {
                    // Keep the original download error visible; a leftover partial file is harmless.
                }
            }

            if (hasSemaphore)
            {
                downloadSemaphore.Release();
            }

            entry.IsQueued = false;
            entry.IsDownloading = false;
            DownloadAllButton.IsEnabled = !isDownloadQueueRunning
                                          && downloadSetEntries.Any(item => !item.IsComplete);
        }
    }

    private static async Task<HttpResponseMessage> GetBeatmapDownloadResponseAsync(Uri uri, int beatmapsetId, string? sessionCookie)
    {
        for (var redirectCount = 0; redirectCount <= 5; redirectCount++)
        {
            if (uri.Scheme != Uri.UriSchemeHttps
                || !(uri.Host.Equals("osu.ppy.sh", StringComparison.OrdinalIgnoreCase)
                     || uri.Host.EndsWith(".ppy.sh", StringComparison.OrdinalIgnoreCase)))
            {
                throw new InvalidDataException("The download redirected to an untrusted host.");
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.UserAgent.ParseAdd("CollectionManager/1.0");
            request.Headers.Accept.ParseAdd("application/octet-stream");
            request.Headers.Accept.ParseAdd("application/zip");
            request.Headers.Accept.ParseAdd("*/*");
            if (uri.Host.Equals("osu.ppy.sh", StringComparison.OrdinalIgnoreCase))
            {
                request.Headers.Referrer = new Uri($"https://osu.ppy.sh/beatmapsets/{beatmapsetId}");
                request.Headers.TryAddWithoutValidation("Origin", "https://osu.ppy.sh");
                if (!string.IsNullOrWhiteSpace(sessionCookie))
                {
                    request.Headers.TryAddWithoutValidation("Cookie", sessionCookie);
                }
            }

            var response = await beatmapDownloadClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if ((int)response.StatusCode is >= 300 and < 400)
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location is null || redirectCount == 5)
                {
                    throw new HttpRequestException("The beatmap download returned an invalid redirect.");
                }

                uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                continue;
            }

            return response;
        }

        throw new HttpRequestException("Too many redirects while downloading the beatmap.");
    }

    private static void ValidateBeatmapArchive(string archivePath, int expectedSetId)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count == 0 || archive.Entries.Count > 20_000)
        {
            throw new InvalidDataException("The downloaded file is not a valid beatmap archive.");
        }

        long uncompressedBytes = 0;
        var hasBeatmapFile = false;
        foreach (var archiveEntry in archive.Entries)
        {
            uncompressedBytes += archiveEntry.Length;
            if (uncompressedBytes > 4_294_967_296)
            {
                throw new InvalidDataException("The beatmap archive expands beyond the 4 GiB safety limit.");
            }

            if (!archiveEntry.FullName.EndsWith(".osu", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            hasBeatmapFile = true;
            using var entryStream = archiveEntry.Open();
            using var reader = new StreamReader(entryStream);
            while (reader.ReadLine() is { } line && !line.StartsWith("[HitObjects]", StringComparison.Ordinal))
            {
                const string field = "BeatmapSetID:";
                if (line.StartsWith(field, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(line[field.Length..].Trim(), out var setId)
                    && setId > 0
                    && setId != expectedSetId)
                {
                    throw new InvalidDataException("The downloaded archive contains a different beatmapset ID.");
                }
            }
        }

        if (!hasBeatmapFile)
        {
            throw new InvalidDataException("The downloaded archive contains no .osu beatmap files.");
        }
    }

    private static string GetAvailableDownloadPath(string desiredPath)
    {
        if (!File.Exists(desiredPath))
        {
            return desiredPath;
        }

        var directory = Path.GetDirectoryName(desiredPath)!;
        var name = Path.GetFileNameWithoutExtension(desiredPath);
        var extension = Path.GetExtension(desiredPath);
        for (var suffix = 2; ; suffix++)
        {
            var candidate = Path.Combine(directory, $"{name} ({suffix}){extension}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static string? NormalizeSessionCookie(string? input)
    {
        var value = input?.Trim();
        if (string.IsNullOrEmpty(value))
        {
            return null;
        }

        if (value.StartsWith("Cookie:", StringComparison.OrdinalIgnoreCase))
        {
            value = value["Cookie:".Length..].Trim();
        }

        return value.StartsWith("osu_session=", StringComparison.OrdinalIgnoreCase)
               || value.Contains(';')
            ? value
            : $"osu_session={value}";
    }

    private async Task AddSongsAsync()
    {
        if (loadedRealmPath is null || selectedCollection is null || isLoading)
        {
            return;
        }

        var collection = selectedCollection;
        var existingHashes = collection.Beatmaps.Select(map => map.Hash).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var available = allBeatmaps.Where(map => !existingHashes.Contains(map.Hash)).ToArray();
        if (available.Length == 0)
        {
            StatusLabel.Text = "There are no additional beatmaps to add.";
            return;
        }

        var picker = new SongPickerWindow(available);
        if (!await picker.ShowDialog<bool>(this) || picker.SelectedSongs.Count == 0)
        {
            return;
        }

        await EditCollectionAsync(
            loadedRealmPath,
            collection,
            () => RealmCollectionEditor.Add(loadedRealmPath, collection.Id, picker.SelectedSongs.Select(song => song.Hash)),
            $"Added {picker.SelectedSongs.Count} beatmap(s).");
    }

    private async Task RemoveSelectedSongAsync()
    {
        if (loadedRealmPath is null || selectedCollection is null || HashList.SelectedItem is not BeatmapData beatmap || isLoading)
        {
            return;
        }

        var collection = selectedCollection;
        await EditCollectionAsync(
            loadedRealmPath,
            collection,
            () => RealmCollectionEditor.Remove(loadedRealmPath, collection.Id, [beatmap.Hash]),
            $"Removed {beatmap.SongAndDifficultyText}.");
    }

    private void BeatmapContextMenu_Opening(object? sender, CancelEventArgs e)
    {
        if (sender is not ContextMenu menu)
        {
            contextBeatmap = null;
            return;
        }

        var beatmap = (menu.PlacementTarget as Control)?.DataContext as BeatmapData
                      ?? menu.DataContext as BeatmapData
                      ?? HashList.SelectedItem as BeatmapData;
        if (beatmap is null)
        {
            contextBeatmap = null;
            return;
        }

        contextBeatmap = beatmap;
        HashList.SelectedItem = beatmap;
        var menuItems = menu.Items.OfType<MenuItem>().ToArray();
        if (menuItems.Length >= 2)
        {
            var addToCollectionMenu = menuItems[0];
            addToCollectionMenu.Items.Clear();
            var sourceCollection = selectedCollection;
            var realmPath = loadedRealmPath;
            if (realmPath is not null && !isLoading)
            {
                var destinations = collections
                    .Where(collection => !collection.IsVirtual)
                    .ToArray();
                foreach (var destination in destinations)
                {
                    var alreadyContainsBeatmap = destination.Beatmaps.Any(map =>
                        string.Equals(map.Hash, beatmap.Hash, StringComparison.OrdinalIgnoreCase));
                    var destinationItem = new MenuItem
                    {
                        Header = alreadyContainsBeatmap ? $"{destination.Name} (already added)" : destination.Name,
                        IsEnabled = !alreadyContainsBeatmap
                    };
                    if (!alreadyContainsBeatmap)
                    {
                        destinationItem.Click += async (_, _) =>
                            await AddBeatmapToCollectionAsync(realmPath, sourceCollection, beatmap, destination);
                    }
                    addToCollectionMenu.Items.Add(destinationItem);
                }
            }

            if (addToCollectionMenu.Items.Count == 0)
            {
                addToCollectionMenu.Items.Add(new MenuItem
                {
                    Header = "No collections available",
                    IsEnabled = false
                });
            }

            addToCollectionMenu.IsEnabled = true;
            menuItems[1].IsEnabled = selectedCollection is not null
                                     && !selectedCollection.IsVirtual
                                     && !isLoading
                                     && selectedCollection.Beatmaps.Any(map =>
                                         string.Equals(map.Hash, beatmap.Hash, StringComparison.OrdinalIgnoreCase));
        }
    }

    private async Task AddBeatmapToCollectionAsync(
        string realmPath,
        CollectionData? sourceCollection,
        BeatmapData beatmap,
        CollectionData destination)
    {
        if (isLoading)
        {
            return;
        }

        await EditCollectionAsync(
            realmPath,
            sourceCollection is { IsVirtual: false } ? sourceCollection : destination,
            () => RealmCollectionEditor.Add(realmPath, destination.Id, [beatmap.Hash]),
            $"Added {beatmap.SongAndDifficultyText} to {destination.Name}.");
    }

    private async void RemoveBeatmapFromCollection_Click(object? sender, RoutedEventArgs e)
    {
        var realmPath = loadedRealmPath;
        var collection = selectedCollection;
        var beatmap = contextBeatmap;
        if (realmPath is null || collection is null || collection.IsVirtual || beatmap is null || isLoading)
        {
            return;
        }

        await EditCollectionAsync(
            realmPath,
            collection,
            () => RealmCollectionEditor.Remove(realmPath, collection.Id, [beatmap.Hash]),
            $"Removed {beatmap.SongAndDifficultyText} from {collection.Name}.");
    }

    private async Task EditCollectionAsync(string realmPath, CollectionData collection, Action edit, string successMessage)
    {
        isLoading = true;
        AddSongsButton.IsEnabled = false;
        RemoveSongButton.IsEnabled = false;
        StatusLabel.Foreground = null;
        StatusLabel.Text = "Saving collection…";
        try
        {
            await Task.Run(edit);
            if (await LoadRealmAsync(realmPath, collection.Id))
            {
                StatusLabel.Text = successMessage;
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Foreground = Avalonia.Media.Brushes.IndianRed;
            StatusLabel.Text = ex.Message;
        }
        finally
        {
            isLoading = false;
            AddSongsButton.IsEnabled = selectedCollection is not null && !selectedCollection.IsVirtual;
            RemoveSongButton.IsEnabled = selectedCollection is not null && !selectedCollection.IsVirtual
                                         && HashList.SelectedItem is BeatmapData;
        }
    }

    private void RefreshHashList()
    {
        visibleBeatmaps.Clear();
        if (selectedCollection is null)
        {
            return;
        }

        var filter = HashSearch.Text?.Trim();
        var filtered = selectedCollection.Beatmaps.Where(map =>
            string.IsNullOrEmpty(filter)
            || map.Hash.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || map.Artist.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || map.SongName.Contains(filter, StringComparison.OrdinalIgnoreCase)
            || map.DifficultyName.Contains(filter, StringComparison.OrdinalIgnoreCase));

        if (StarRatingRange.IsFiltering)
        {
            filtered = filtered.Where(map => map.StarRating >= 0
                                             && map.StarRating >= StarRatingRange.LowerValue
                                             && map.StarRating <= StarRatingRange.UpperValue);
        }

        if (sortColumn is SortColumn column)
        {
            IComparer<string> textComparer = sortAscending
                ? StringComparer.CurrentCultureIgnoreCase
                : DescendingStringComparer.Instance;
            filtered = column switch
            {
                SortColumn.Artist => filtered.OrderBy(map => string.IsNullOrWhiteSpace(map.Artist))
                    .ThenBy(map => map.Artist, textComparer),
                SortColumn.SongName => filtered.OrderBy(map => string.IsNullOrWhiteSpace(map.SongName))
                    .ThenBy(map => map.SongName, textComparer),
                SortColumn.StarRating => filtered.OrderBy(map => map.StarRating < 0)
                    .ThenBy(map => map.StarRating, sortAscending ? Comparer<double>.Default : Comparer<double>.Create((a, b) => b.CompareTo(a))),
                _ => filtered
            };
        }

        foreach (var beatmap in filtered)
        {
            visibleBeatmaps.Add(beatmap);
        }
    }

    private void SortBy(SortColumn column)
    {
        if (sortColumn == column)
        {
            sortAscending = !sortAscending;
        }
        else
        {
            sortColumn = column;
            sortAscending = true;
        }

        ArtistSortButton.Content = SortHeader("Artist", SortColumn.Artist);
        SongSortButton.Content = SortHeader("Song name", SortColumn.SongName);
        RatingSortButton.Content = SortHeader("Star rating", SortColumn.StarRating);
        RefreshHashList();
    }

    private string SortHeader(string title, SortColumn column) => sortColumn == column
        ? $"{title} {(sortAscending ? "↑" : "↓")}"
        : title;

    private sealed class DescendingStringComparer : IComparer<string>
    {
        public static readonly DescendingStringComparer Instance = new();
        public int Compare(string? x, string? y) => StringComparer.CurrentCultureIgnoreCase.Compare(y, x);
    }
}
