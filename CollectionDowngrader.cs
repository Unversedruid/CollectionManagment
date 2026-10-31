using System.Text.RegularExpressions;
using Realms;
using Realms.Exceptions;
using CollectionDowngrader.LazerSchema;

namespace CollectionDowngrader
{
    class CollectionDowngrader
    {
        // osu!lazer Realm schema version current as of 2026-10.
        internal const int LazerSchemaVersion = 52;

        internal static int Run(string[] args)
        {
            String realmFile, outputFile;
            Realm db;

            if (args.Length != 2)
            {
                Console.Error.WriteLine("Usage: CollectionDowngrader <path to osu! (lazer) client.realm> " +
                                        "<output osu! (stable) collection.db path>");

                return 1;
            }

            realmFile = args[0];
            outputFile = args[1];

            if (File.Exists(realmFile)) {
                Console.WriteLine("Found realm file.");
            } else {
                Console.Error.WriteLine("Realm file does not exist, stop.");

                return 1;
            }

            if (File.Exists(outputFile))
            {
                Console.Error.WriteLine("Output file already exists, aborting.");

                return 1;
            }

            RealmConfiguration config = new(realmFile)
            {
                IsReadOnly = true,
                SchemaVersion = LazerSchemaVersion
            };

            // Keep the Realm schema limited to the only model this tool reads.
            // This avoids requiring unrelated lazer models to mirror every schema change.
            config.Schema = new[] { typeof(BeatmapCollection) };

            try
            {
                db = Realm.GetInstance(config);
            }
            catch (RealmException re)
            {
                Console.Error.WriteLine($"Error opening database:\n\n{re.Message}");

                // example msg: "Provided schema version A does not equal last set version B."
                // example msg2: "Provided schema version A is less than last set version B."
                
                if (re.Message.Contains("less than last set version"))
                {
                    Console.Error.WriteLine("It seemed like the specified osu! (lazer) database is in a new format " +
                                            "which is not compatible with this version of CollectionDowngrader.");
                    Console.Error.WriteLine("\nYou can go check the project page to see if there's a new release, " +
                                            "or file an Issue on GitHub to let me know it needs updating.");
                }
                else
                {
                    Regex regex = new Regex(@"Provided schema version (\d+) does not equal last set version (\d+).");
                    
                    Match match = regex.Match(re.Message);

                    if (match.Success && match.Groups.Count == 3)
                    {
                        int providedVersion = int.Parse(match.Groups[1].Value);
                        int lastSetVersion = int.Parse(match.Groups[2].Value);
                        
                        // if provided version is smaller than the last set version, it means CollectionDowngrader is too old
                        // ask the user to update in this case
                        
                        if (providedVersion < lastSetVersion)
                        {
                            Console.Error.WriteLine("It seemed like the specified osu! (lazer) database is in a new format " +
                                                    "which is not compatible with this version of CollectionDowngrader.");
                            Console.Error.WriteLine("\nYou can go check the project page to see if there's a new release, " +
                                                    "or file an Issue on GitHub to let me know it needs updating.");
                        }
                        else
                        {
                            // otherwise, user installed CollectionDowngrader is too new for the database
                            // ask the user to update osu! (lazer) client or downgrade CollectionDowngrader in this case
                            
                            Console.Error.WriteLine("It seemed like the specified osu! (lazer) database is in an old format " +
                                                    "which is not compatible with this version of CollectionDowngrader.");
                            Console.Error.WriteLine("\nYou can try to update your osu! (lazer) client, or downgrade " +
                                                    "CollectionDowngrader to an older version.");
                        }
                    }
                }

                return 1;
            }

            Console.WriteLine("The specified osu! (lazer) database is loaded successfully.");

            List<BeatmapCollection> collections = db.All<BeatmapCollection>().ToList();
            int collectionCount = collections.Count;

            Console.WriteLine($"Found {collectionCount} collections in the database.");

            try
            {
                CollectionDbCodec.Write(outputFile, collections.Select(collection =>
                    new LegacyCollection(collection.Name, collection.BeatmapMD5Hashes.ToArray())));
            }
            catch (IOException ioe)
            {
                Console.Error.WriteLine($"Cannot write collection database: {ioe.Message}");
                db.Dispose();
                return 1;
            }

            db.Dispose();

            Console.WriteLine("Everything is OK.");

            return 0;
        }
    }
}
