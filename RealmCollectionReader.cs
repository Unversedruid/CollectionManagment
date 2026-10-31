using CollectionDowngrader.LazerSchema;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Realms;

namespace CollectionDowngrader;

public sealed record BeatmapData(
    string Hash,
    string Artist,
    string SongName,
    string DifficultyName,
    double StarRating,
    int OnlineSetId = -1,
    string Mapper = "")
{
    public string ArtistText => string.IsNullOrWhiteSpace(Artist) ? "—" : Artist;
    public string SongNameText => string.IsNullOrWhiteSpace(SongName) ? Hash : SongName;
    public string DifficultyNameText => string.IsNullOrWhiteSpace(DifficultyName) ? "—" : DifficultyName;
    public string SongAndDifficulty => string.Join(" - ", new[] { SongName, DifficultyName }.Where(part => part.Length > 0));
    public string SongAndDifficultyText => string.IsNullOrWhiteSpace(SongAndDifficulty) ? Hash : SongAndDifficulty;
    public string StarRatingText => StarRating < 0 ? "—" : $"{StarRating:0.00}★";
    public string DisplayLabel => $"{ArtistText} - {SongAndDifficultyText} ({StarRatingText})";
}

internal sealed class BeatmapSetDownloadEntry : INotifyPropertyChanged
{
    private double progress;
    private string statusText = "Ready";
    private bool isQueued;
    private bool isDownloading;
    private bool isComplete;

    public event PropertyChangedEventHandler? PropertyChanged;

    public int OnlineId { get; }
    public string SongName { get; }
    public string Mapper { get; }
    public string DownloadUrl { get; }
    public bool IsQueued
    {
        get => isQueued;
        set
        {
            if (isQueued == value) return;
            isQueued = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanDownload));
        }
    }
    public bool IsDownloading
    {
        get => isDownloading;
        set
        {
            if (isDownloading == value) return;
            isDownloading = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanDownload));
        }
    }
    public bool IsComplete
    {
        get => isComplete;
        set
        {
            if (isComplete == value) return;
            isComplete = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanDownload));
        }
    }
    public bool CanDownload => !IsQueued && !IsDownloading && !IsComplete;
    public double Progress
    {
        get => progress;
        set
        {
            if (Math.Abs(progress - value) < 0.01)
            {
                return;
            }

            progress = Math.Clamp(value, 0, 100);
            OnPropertyChanged();
        }
    }

    public string StatusText
    {
        get => statusText;
        set
        {
            if (statusText == value)
            {
                return;
            }

            statusText = value;
            OnPropertyChanged();
        }
    }

    public BeatmapSetDownloadEntry(int onlineId, string songName, string mapper, string downloadUrl)
    {
        OnlineId = onlineId;
        SongName = songName;
        Mapper = mapper;
        DownloadUrl = downloadUrl;
    }

    public string SongNameText => string.IsNullOrWhiteSpace(SongName) ? $"Beatmapset {OnlineId}" : SongName;
    public string MapperText => string.IsNullOrWhiteSpace(Mapper) ? "Mapper unknown" : $"Mapped by {Mapper}";
    public string MapperStatusText => $"{MapperText}  ·  {StatusText}";

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

internal sealed record CollectionData(
    Guid Id,
    string Name,
    IReadOnlyList<BeatmapData> Beatmaps,
    DateTimeOffset LastModified,
    bool IsVirtual = false)
{
    public string DisplayText => $"{Name}  ·  {Beatmaps.Count} maps";
}
internal sealed record RealmSnapshot(IReadOnlyList<CollectionData> Collections, IReadOnlyList<BeatmapData> Beatmaps);

internal static class RealmCollectionReader
{
    public static RealmSnapshot Read(string realmFile)
    {
        var config = new RealmConfiguration(realmFile)
        {
            IsReadOnly = true,
            SchemaVersion = CollectionDowngrader.LazerSchemaVersion
        };
        config.Schema = new[]
        {
            typeof(Beatmap),
            typeof(BeatmapCollection),
            typeof(BeatmapDifficulty),
            typeof(BeatmapMetadata),
            typeof(BeatmapSet),
            typeof(BeatmapUserSettings),
            typeof(RealmFile),
            typeof(RealmNamedFileUsage),
            typeof(RealmUser),
            typeof(Ruleset)
        };

        using var realm = Realm.GetInstance(config);
        var realmCollections = realm.All<BeatmapCollection>().ToList();
        var collectionHashes = realmCollections
            .SelectMany(collection => collection.BeatmapMD5Hashes)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var namesByHash = new Dictionary<string, BeatmapData>(StringComparer.OrdinalIgnoreCase);
        var allBeatmaps = new List<BeatmapData>();

        foreach (var beatmap in realm.All<Beatmap>())
        {
            if (string.IsNullOrWhiteSpace(beatmap.MD5Hash))
            {
                continue;
            }

            var title = beatmap.Metadata.Title.Trim();
            var artist = beatmap.Metadata.Artist.Trim();
            var difficulty = beatmap.DifficultyName.Trim();
            var data = new BeatmapData(
                beatmap.MD5Hash,
                artist,
                title,
                difficulty,
                beatmap.StarRating,
                beatmap.BeatmapSet?.OnlineID ?? -1,
                beatmap.Metadata.Author.Username);
            allBeatmaps.Add(data);
            if (collectionHashes.Contains(beatmap.MD5Hash))
            {
                namesByHash[beatmap.MD5Hash] = data;
            }
        }

        var collections = realmCollections
            .Select(collection => new CollectionData(
                collection.ID,
                collection.Name,
                collection.BeatmapMD5Hashes
                    .Select(hash => namesByHash.TryGetValue(hash, out var beatmap)
                        ? beatmap
                        : new BeatmapData(hash, string.Empty, string.Empty, string.Empty, -1))
                    .ToArray(),
                collection.LastModified))
            .ToArray();

        var allBeatmapsCollection = new CollectionData(
            Guid.Empty,
            "All beatmaps",
            allBeatmaps,
            DateTimeOffset.MinValue,
            IsVirtual: true);

        return new RealmSnapshot([allBeatmapsCollection, .. collections], allBeatmaps);
    }
}

internal static class RealmCollectionEditor
{
    public static void Delete(string realmFile, Guid collectionId)
    {
        var config = new RealmConfiguration(realmFile)
        {
            SchemaVersion = CollectionDowngrader.LazerSchemaVersion,
        };
        config.Schema = new[] { typeof(BeatmapCollection) };

        using var realm = Realm.GetInstance(config);
        var collection = realm.Find<BeatmapCollection>(collectionId);
        if (collection is null)
        {
            return;
        }

        realm.Write(() => realm.Remove(collection));
    }

    public static void Add(string realmFile, Guid collectionId, IEnumerable<string> hashes)
    {
        Update(realmFile, collectionId, collection =>
        {
            var existing = collection.BeatmapMD5Hashes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var hash in hashes)
            {
                if (existing.Add(hash))
                {
                    collection.BeatmapMD5Hashes.Add(hash);
                }
            }
        });
    }

    public static void Remove(string realmFile, Guid collectionId, IEnumerable<string> hashes)
    {
        var toRemove = hashes.ToHashSet(StringComparer.OrdinalIgnoreCase);
        Update(realmFile, collectionId, collection =>
        {
            for (var index = collection.BeatmapMD5Hashes.Count - 1; index >= 0; index--)
            {
                if (toRemove.Contains(collection.BeatmapMD5Hashes[index]))
                {
                    collection.BeatmapMD5Hashes.RemoveAt(index);
                }
            }
        });
    }

    private static void Update(string realmFile, Guid collectionId, Action<BeatmapCollection> update)
    {
        var config = new RealmConfiguration(realmFile)
        {
            SchemaVersion = CollectionDowngrader.LazerSchemaVersion,
            Schema = new[] { typeof(BeatmapCollection) }
        };

        using var realm = Realm.GetInstance(config);
        realm.Write(() =>
        {
            var collection = realm.Find<BeatmapCollection>(collectionId)
                             ?? throw new InvalidOperationException("The selected collection no longer exists.");
            var previousCount = collection.BeatmapMD5Hashes.Count;
            update(collection);
            if (collection.BeatmapMD5Hashes.Count != previousCount)
            {
                collection.LastModified = DateTimeOffset.UtcNow;
            }
        });
    }
}

internal static class RealmCollectionImporter
{
    public static void Import(string realmFile, IEnumerable<LegacyCollection> importedCollections)
    {
        var config = new RealmConfiguration(realmFile)
        {
            SchemaVersion = CollectionDowngrader.LazerSchemaVersion,
        };
        config.Schema = new[] { typeof(BeatmapCollection) };

        using var realm = Realm.GetInstance(config);
        var existingByName = realm.All<BeatmapCollection>().ToList()
            .GroupBy(collection => collection.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        realm.Write(() =>
        {
            foreach (var imported in importedCollections)
            {
                if (string.IsNullOrWhiteSpace(imported.Name))
                {
                    continue;
                }

                if (!existingByName.TryGetValue(imported.Name, out var collection))
                {
                    collection = realm.Add(new BeatmapCollection
                    {
                        ID = Guid.NewGuid(),
                        Name = imported.Name,
                        LastModified = DateTimeOffset.UtcNow
                    });
                    existingByName.Add(collection.Name, collection);
                }

                var hashes = collection.BeatmapMD5Hashes.ToHashSet(StringComparer.OrdinalIgnoreCase);
                var changed = false;
                foreach (var hash in imported.Hashes)
                {
                    if (!string.IsNullOrWhiteSpace(hash) && hashes.Add(hash))
                    {
                        collection.BeatmapMD5Hashes.Add(hash);
                        changed = true;
                    }
                }

                if (changed)
                {
                    collection.LastModified = DateTimeOffset.UtcNow;
                }
            }
        });
    }
}
