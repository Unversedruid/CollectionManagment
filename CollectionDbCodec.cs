using System.Text;
using System.IO.Compression;
using System.Text.Json;

namespace CollectionDowngrader;

internal sealed record LegacyCollection(
    string Name,
    IReadOnlyList<string> Hashes,
    IReadOnlyList<BeatmapData>? Beatmaps = null);

internal static class CollectionDbCodec
{
    private const int DatabaseVersion = 20150203;
    private const int MaxCollectionCount = 1_000_000;
    private const int MaxBeatmapCount = 10_000_000;
    private const string DatabaseEntryName = "collection.db";
    private const string MetadataEntryName = "metadata.json";

    public static IReadOnlyList<LegacyCollection> Read(string path)
    {
        if (!string.Equals(Path.GetExtension(path), ".cmcollection", StringComparison.OrdinalIgnoreCase))
        {
            using var stream = File.OpenRead(path);
            return ReadDatabase(stream);
        }

        using var archive = ZipFile.OpenRead(path);
        var databaseEntry = archive.GetEntry(DatabaseEntryName)
            ?? throw new InvalidDataException("The collection package does not contain collection.db.");
        using var databaseStream = databaseEntry.Open();
        var collections = ReadDatabase(databaseStream).ToArray();
        var metadataEntry = archive.GetEntry(MetadataEntryName)
            ?? throw new InvalidDataException("The collection package does not contain beatmap metadata.");
        using var metadataStream = metadataEntry.Open();
        var metadata = JsonSerializer.Deserialize<CollectionPackageMetadata>(metadataStream)
            ?? throw new InvalidDataException("The collection package metadata is invalid.");
        if (metadata.Version != 1 || collections.Length != 1
            || !string.Equals(collections[0].Name, metadata.CollectionName, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The collection package metadata does not match its collection.db.");
        }

        var metadataByHash = metadata.Beatmaps
            .Where(beatmap => !string.IsNullOrWhiteSpace(beatmap.Hash))
            .GroupBy(beatmap => beatmap.Hash, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        return [collections[0] with
        {
            Beatmaps = collections[0].Hashes
                .Where(metadataByHash.ContainsKey)
                .Select(hash => metadataByHash[hash])
                .ToArray()
        }];
    }

    private static IReadOnlyList<LegacyCollection> ReadDatabase(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var version = reader.ReadInt32();
        if (version <= 0)
        {
            throw new InvalidDataException("The selected file is not a valid osu! collection.db file.");
        }

        var collectionCount = ReadCount(reader, MaxCollectionCount, "collection");
        var collections = new List<LegacyCollection>(collectionCount);
        for (var collectionIndex = 0; collectionIndex < collectionCount; collectionIndex++)
        {
            var name = ReadOsuString(reader);
            var beatmapCount = ReadCount(reader, MaxBeatmapCount, "beatmap");
            var hashes = new List<string>(beatmapCount);
            for (var beatmapIndex = 0; beatmapIndex < beatmapCount; beatmapIndex++)
            {
                hashes.Add(ReadOsuString(reader));
            }

            collections.Add(new LegacyCollection(name, hashes));
        }

        if (stream.CanSeek && stream.Position != stream.Length)
        {
            throw new InvalidDataException("The selected collection.db contains unexpected trailing data.");
        }

        return collections;
    }

    public static void WritePackage(string path, LegacyCollection collection, bool overwrite = false)
    {
        using var stream = new FileStream(
            path,
            overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: false);
        var databaseEntry = archive.CreateEntry(DatabaseEntryName, CompressionLevel.Optimal);
        using (var databaseStream = databaseEntry.Open())
        {
            WriteDatabase(databaseStream, [collection]);
        }

        var metadataEntry = archive.CreateEntry(MetadataEntryName, CompressionLevel.Optimal);
        using var metadataStream = metadataEntry.Open();
        JsonSerializer.Serialize(metadataStream, new CollectionPackageMetadata(
            1,
            collection.Name,
            collection.Beatmaps ?? Array.Empty<BeatmapData>()));
    }

    public static void Write(string path, IEnumerable<LegacyCollection> source, bool overwrite = false)
    {
        using var stream = new FileStream(
            path,
            overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None);
        WriteDatabase(stream, source);
    }

    private static void WriteDatabase(Stream stream, IEnumerable<LegacyCollection> source)
    {
        var collections = source.ToArray();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: false);
        writer.Write(DatabaseVersion);
        writer.Write(collections.Length);
        foreach (var collection in collections)
        {
            WriteOsuString(writer, collection.Name);
            writer.Write(collection.Hashes.Count);
            foreach (var hash in collection.Hashes)
            {
                WriteOsuString(writer, hash);
            }
        }
    }

    private sealed record CollectionPackageMetadata(int Version, string CollectionName, IReadOnlyList<BeatmapData> Beatmaps);

    private static int ReadCount(BinaryReader reader, int max, string itemName)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > max)
        {
            throw new InvalidDataException($"The collection.db has an invalid {itemName} count.");
        }

        return count;
    }

    private static string ReadOsuString(BinaryReader reader)
    {
        var marker = reader.ReadByte();
        return marker switch
        {
            0x00 => string.Empty,
            0x0b => reader.ReadString(),
            _ => throw new InvalidDataException("The selected file is not a valid osu! collection.db file.")
        };
    }

    private static void WriteOsuString(BinaryWriter writer, string value)
    {
        writer.Write((byte)0x0b);
        writer.Write(value);
    }
}
