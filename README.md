CollectionDowngrader
=========================

> [!WARNING]
>
> This operation (Importing data from lazer to stable) is not officially supported by osu!, so use this at your own risk and don't forget to backup your data before downgrading!

A very simple and tiny program that converts osu! (lazer) collection data to osu! (stable) collection.db format.

## Usage

Run `CollectionDowngrader.exe` without arguments to open the collection browser. Choose a `client.realm` file to view
its collections and each beatmap's artist, song title, difficulty, and star rating. Use **Add songs…** and **Remove
selected song** to edit a collection. The virtual **All beatmaps** entry shows every beatmap in the file and is not
saved as a collection. Filter displayed beatmaps by minimum and maximum star rating. Changes to regular collections are
written directly to the selected Realm file. Right-click a collection to export it as a `.cmcollection` package. It
contains a standard `collection.db` plus a metadata manifest with beatmap hashes, beatmapset IDs, song and artist names,
difficulty names, mapper names, and star ratings. Use **Import collection…** in the Imports tab to merge a `.cmcollection`
package or standard osu! `collection.db` into the open Realm file; package metadata can also supply download IDs for
beatmaps missing from the local Realm. Right-click a beatmap to add it to another collection or remove it from the
current one. The **Imports** tab has its own collection selector and displays unique beatmapsets whose online IDs are
available in Realm metadata or an imported package. Beatmaps already present in **All beatmaps** are omitted. Use **Download all** to
queue the listed beatmapsets, with up to five downloads running at once. **Download & open** downloads one `.osz`
directly from osu! into
`Downloads/CollectionManager`, validates the archive, and opens it with the system's registered beatmap handler
(configure it for osu!lazer). If osu! requires a signed-in web session, enter the request Cookie header in the masked
Imports field; it is kept in memory for the app session and sent only to `osu.ppy.sh`. You can also use
the system's `.osz` file association to import existing downloads. Close osu!lazer before editing collections and keep
a backup of the database. You can pass a Realm file path to open it on startup:

`CollectionDowngrader.exe <path to osu! (lazer) client.realm>`

The existing command-line export is still available:

> [!NOTE]
>
> You should already know where your osu! installations are located and what you are trying to do.

`CollectionDowngrader.exe <path to osu! (lazer) client.realm> <output osu! (stable) collection.db path>`

### Example

This command reads the collection data from osu! (lazer) at the default installation path and creates a collection.db
 that can be used in osu! (stable).

````shell
CollectionDowngrader.exe %APPDATA%/osu/client.realm collection.db
````

> [!WARNING]
>
> If you already have some collections in your osu! (stable) installation, then you should ***NEVER*** directly overwrite
 the existing collection.db with a new one. Instead, use [CollectionManager][CollectionManager] for merging data in multiple collection databases.

## Downloads

[GitHub Releases](../../releases)

## How to build

Build with the .NET 8 SDK. The app uses Avalonia 11.3.22 for its cross-platform desktop UI and Realm .NET 20.1.0
(Realm Core 20.1.2), targeting osu!lazer Realm schema version 52. For a smaller set of delivery files, publish a
self-contained single-file app for the target operating system and architecture:

```shell
dotnet publish -c Release -r win-x64 --self-contained true
```

Use `linux-x64`, `osx-x64`, or `osx-arm64` in place of `win-x64` for other targets. The executable is written under
`bin/Release/net8.0/<runtime-id>/publish/`. Native libraries are bundled and extracted automatically when launched;
the publish still needs to match the target OS and architecture.

## Credits / See also

* [kabiiQ/BeatmapExporter][BeatmapExporter]: Inspiration of this project, all code for osu! (lazer) realm schema and
 the database parsing.
* [Piotrekol/CollectionManager][CollectionManager]: Code for creating osu! (stable) collection.db files.

[BeatmapExporter]: https://github.com/kabiiQ/BeatmapExporter.git
[CollectionManager]: https://github.com/Piotrekol/CollectionManager.git
