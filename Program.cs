﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.IO;
using System.Threading.Tasks;
using System.Net;
using System.Data.SQLite;
using System.Reflection;
using System.Collections.Concurrent;
using System.Data.Common;
using System.Data;

namespace TileCutter
{
    class Program
    {
        static void Main(string[] args)
        {
            string localCacheDirectory = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            int minz = 7;
            int maxz = 10;
            double minx = -95.844727;
            double miny = 35.978006;
            double maxx = -88.989258;
            double maxy = 40.563895;
            string mapServiceUrl = "http://sampleserver1.arcgisonline.com/ArcGIS/rest/services/Demographics/ESRI_Census_USA/MapServer";
            int maxDegreeOfParallelism = 10;
            bool replaceExistingCacheDB = true;
            bool showHelp = false;
            bool verbose = true;
            string mapServiceType = "osm";
            string settings = string.Empty;
            ITileUrlSource tileSource = new OSMTileUrlSource()
            {
                MapServiceUrl = TileHelper.OSM_BASE_URL_TEMPLATE,
            };

            var options = new OptionSet()
            {
                {"h|help=", "Show this message and exits", h => showHelp = h != null},
                {"v|verbose=", "Display verbose information logs while running", v => verbose = v != null},
                {"t|type=", "Type of the map service to be cached", t => mapServiceType = t.ToLower()},
                {"m|mapservice=", "Url of the Map Service to be cached", m => mapServiceUrl = m},
                {"s|settings=", "Extra settings needed by the type of map service being used", s => settings = s},
                {"o|output=", "Location on disk where the tile cache will be stored", o => localCacheDirectory = o},
                {"z|minz=", "Minimum zoom scale at which to begin caching", z => int.TryParse(z, out minz)},
                {"Z|maxz=", "Maximum zoom scale at which to end caching", Z => int.TryParse(Z, out maxz)},
                {"x|minx=", "Minimum X coordinate value of the extent to cache", x => double.TryParse(x, out minx)},
                {"y|miny=", "Minimum Y coordinate value of the extent to cache", y => double.TryParse(y, out miny)},
                {"X|maxx=", "Maximum X coordinate value of the extent to cache", X => double.TryParse(X, out maxx)},
                {"Y|maxy=", "Maximum Y coordinate value of the extent to cache", Y => double.TryParse(Y, out maxy)},
                {"p|parallelops=", "Limits the number of concurrent operations run by TileCutter", p => int.TryParse(p, out maxDegreeOfParallelism)},
                {"r|replace=", "Delete existing tile cache MBTiles database if already present and create a new one.", r => Boolean.TryParse(r, out replaceExistingCacheDB)}
            };
            options.Parse(args);

            if (showHelp)
            {
                ShowHelp(options);
                return;
            }

            if (!string.IsNullOrEmpty(mapServiceType))
                tileSource = GetTileSource(mapServiceType, mapServiceUrl, settings);

            //Get the sqlite db file location from the config
            //if not provided, default to executing assembly location
            string dbLocation = Path.Combine(localCacheDirectory, "tilecache.mbtiles");

            //if the user option to delete existing tile cache db is true delete it or else add to it
            if (replaceExistingCacheDB && File.Exists(dbLocation))
                File.Delete(dbLocation);

            //Connect to the sqlite database
            string connectionString = string.Format("Data Source={0}; FailIfMissing=False", dbLocation);
            using (SQLiteConnection connection = new SQLiteConnection(connectionString))
            {
                connection.Open();

                //Check if the 'metadata' table exists, if not create it
                var rows = connection.GetSchema("Tables").Select("Table_Name = 'metadata'");
                if (!rows.Any())
                {
                    var command = connection.CreateCommand();
                    command.CommandText = "CREATE TABLE metadata (name text, value text);";
                    command.ExecuteNonQuery();
                    connection.Execute("CREATE UNIQUE INDEX name ON metadata ('name')");
                    connection.Execute(@"INSERT INTO metadata(name, value) values (@a, @b)",
                        new[] { 
                        new { a = "name", b = "cache" }, 
                        new { a = "type", b = "overlay" }, 
                        new { a = "version", b = "1" },
                        new { a = "description", b = "some info here" },
                        new { a = "format", b = "png" },
                        new { a = "bounds", b = string.Format("{0},{1},{2},{3}", minx, miny, maxx, maxy)}
                    });
                }

                //Check if the 'images' table exists, if not create it
                rows = connection.GetSchema("Tables").Select("Table_Name = 'images'");
                if (!rows.Any())
                {
                    var command = connection.CreateCommand();
                    command.CommandText = "CREATE TABLE [images] ([tile_id] INTEGER  NOT NULL PRIMARY KEY, [tile_md5hash] VARCHAR(256) NOT NULL, [tile_data] BLOB  NULL);";
                    command.ExecuteNonQuery();
                    command = connection.CreateCommand();
                    command.CommandText = "CREATE UNIQUE INDEX images_hash on images (tile_md5hash)";
                    command.ExecuteNonQuery();
                }

                //Check if the 'map' table exists, if not create it
                rows = connection.GetSchema("Tables").Select("Table_Name = 'map'");
                if (!rows.Any())
                {
                    var command = connection.CreateCommand();
                    command.CommandText = "CREATE TABLE [map] ([map_id] INTEGER  NOT NULL PRIMARY KEY AUTOINCREMENT, [tile_id] INTEGER  NOT NULL, [zoom_level] INTEGER  NOT NULL, [tile_row] INTEGER  NOT NULL, [tile_column] INTEGER  NOT NULL);";
                    command.ExecuteNonQuery();
                }
            }

            if (!Directory.Exists(localCacheDirectory))
                Directory.CreateDirectory(localCacheDirectory);

            Console.WriteLine("Output Cache Directory is: " + localCacheDirectory);
            BlockingCollection<TileImage> images = new BlockingCollection<TileImage>();
            var tiles = GetTiles(minz, maxz, minx, miny, maxx, maxy);
            Task.Factory.StartNew(() =>
            {
                ParallelOptions parallelOptions = new ParallelOptions() { MaxDegreeOfParallelism = maxDegreeOfParallelism / 2 };
                Parallel.ForEach(tiles, parallelOptions, (tile) =>
                {
                    string tileUrl = tileSource.GetTileUrl(tile);
                    WebClient client = new WebClient();
                    try
                    {
                        byte[] image = client.DownloadData(tileUrl);
                        images.Add(new TileImage()
                        {
                            Tile = tile,
                            Image = image
                        });

                        //FileStream fs = File.Create(Path.Combine(localCacheDirectory, tile.Level.ToString() + "_" + tile.Column.ToString() + "_" + tile.Row.ToString() + ".png"));
                        //fs.Write(image, 0, image.Length);
                        //fs.Flush();
                        //fs.Close();
                        //fs.Dispose();
                        if (verbose)
                            Console.WriteLine(string.Format("Tile Level:{0}, Row:{1}, Column:{2} downloaded.", tile.Level, tile.Row, tile.Column));
                    }
                    catch (WebException ex)
                    {
                        Console.WriteLine(string.Format("Error while downloading tile Level:{0}, Row:{1}, Column:{2} - {3}.", tile.Level, tile.Row, tile.Column, ex.Message));
                        return;
                    }
                    finally { client.Dispose(); }
                });
            }).ContinueWith(t =>
            {
                images.CompleteAdding();
            });

            int currentTileId = 1;
            Action<TileImage[]> processBatch = (batch) => {
                using (SQLiteConnection connection = new SQLiteConnection(connectionString))
                {
                    connection.Open();
                    using (SQLiteTransaction transaction = connection.BeginTransaction())
                    using (SQLiteCommand mapCommand = connection.CreateCommand(),
                        imagesCommand = connection.CreateCommand())
                    {
                        //Create a dummy query for the [map] table and fill the adapter with it
                        //the purpose of this is to get the table structure in a DataTable
                        //the adapter also builds the insert command for it in when it is populated
                        mapCommand.CommandText = "SELECT * FROM [map] WHERE 1 = 2";
                        SQLiteDataAdapter mapAdapter = new SQLiteDataAdapter(mapCommand);

                        //Create a dummy query for the [images] table and fill the adapter with it
                        //the purpose of this is to get the table structure in a DataTable
                        //the adapter also builds the insert command for it in when it is populated
                        imagesCommand.CommandText = "SELECT * FROM [images] WHERE 1 = 2";
                        SQLiteDataAdapter imagesAdapter = new SQLiteDataAdapter(imagesCommand);

                        using (SQLiteCommandBuilder mapCmdBuilder = new SQLiteCommandBuilder(mapAdapter),
                            imagesCmdBuilder = new SQLiteCommandBuilder(imagesAdapter))
                        {
                            using (imagesAdapter.InsertCommand = (SQLiteCommand)((ICloneable)imagesCmdBuilder.GetInsertCommand()).Clone())
                            using (mapAdapter.InsertCommand = (SQLiteCommand)((ICloneable)mapCmdBuilder.GetInsertCommand()).Clone())
                            {
                                imagesCmdBuilder.DataAdapter = null;
                                mapCmdBuilder.DataAdapter = null;
                                using (DataTable mapTable = new DataTable(),
                                    imagesTable = new DataTable())
                                {
                                    imagesAdapter.Fill(imagesTable);
                                    mapAdapter.Fill(mapTable);
                                    //Dictionary to eliminate duplicate images within batch
                                    Dictionary<string, int> added = new Dictionary<string, int>();
                                    //looping thru keys is safe to do here because
                                    //the Keys property of concurrentDictionary provides a snapshot of the keys
                                    //while enumerating
                                    //the TryGet & TryRemove inside the loop checks for items that were removed by another thread
                                    foreach (var tileimg in batch)
                                    {
                                        string hash = Convert.ToBase64String(new System.Security.Cryptography.MD5CryptoServiceProvider().ComputeHash(tileimg.Image));
                                        int tileid = -1;
                                        if (!added.ContainsKey(hash))
                                        {
                                            mapCommand.CommandText = "SELECT [tile_id] FROM [images] WHERE [tile_md5hash] = @hash";
                                            mapCommand.Parameters.Add(new SQLiteParameter("hash", hash));
                                            object tileObj = mapCommand.ExecuteScalar();
                                            if (tileObj != null && int.TryParse(tileObj.ToString(), out tileid))
                                                added.Add(hash, tileid);
                                            else
                                            {
                                                tileid = currentTileId++;
                                                added.Add(hash, tileid);
                                                DataRow idr = imagesTable.NewRow();
                                                idr["tile_md5hash"] = hash;
                                                idr["tile_data"] = tileimg.Image;
                                                idr["tile_id"] = added[hash];
                                                imagesTable.Rows.Add(idr);
                                            }
                                        }

                                        DataRow mdr = mapTable.NewRow();
                                        mdr["zoom_level"] = tileimg.Tile.Level;
                                        mdr["tile_column"] = tileimg.Tile.Column;
                                        mdr["tile_row"] = tileimg.Tile.Row;
                                        mdr["tile_id"] = added[hash];
                                        mapTable.Rows.Add(mdr);
                                    }//for loop thru images
                                    imagesAdapter.Update(imagesTable);
                                    mapAdapter.Update(mapTable);
                                    transaction.Commit();
                                    Console.WriteLine(String.Format("Saving an image batch of {0}.", batch.Length));
                                }//using for datatable
                            }//using for insert command
                        }//using for command builder
                    }//using for select command
                }//using for connection
            };

            ConcurrentStack<TileImage> buffer = new ConcurrentStack<TileImage>();
            Task.Factory.StartNew(() =>
            {
                ParallelOptions pOptions = new ParallelOptions() { MaxDegreeOfParallelism = maxDegreeOfParallelism / 2 };
                Parallel.ForEach(images.GetConsumingEnumerable(), pOptions, (tileimage) =>
                {
                    buffer.Push(tileimage);
                    if (buffer.Count < 50)
                        return;
                    TileImage[] bufferTileImages = new TileImage[50];
                    int count = buffer.TryPopRange(bufferTileImages);
                    if (count == 0)
                        return;
                    processBatch(bufferTileImages);
                });
            }).ContinueWith(t => {
                if (buffer.Count == 0)
                    return;
                TileImage[] bufferTileImages = new TileImage[buffer.Count];
                int count = buffer.TryPopRange(bufferTileImages);
                if (count == 0)
                    return;
                processBatch(bufferTileImages);
            }).ContinueWith(t => {
                using (SQLiteConnection connection = new SQLiteConnection(connectionString))
                {
                    connection.Open();
                    connection.Execute("CREATE UNIQUE INDEX map_index on map (zoom_level, tile_column, tile_row)");
                    connection.Execute("CREATE VIEW tiles as SELECT map.zoom_level as zoom_level, map.tile_column as tile_column, map.tile_row as tile_row, images.tile_data as tile_data FROM map JOIN images on images.tile_id = map.tile_id");
                    connection.Close();
                }
            }).Wait();

            
            Console.WriteLine("All Done !!!");
        }

        private static ITileUrlSource GetTileSource(string mapServiceType, string mapServiceUrl, string settings)
        {
            string type = mapServiceType.ToLower();
            if (type == "osm")
                return new OSMTileUrlSource() { MapServiceUrl = mapServiceUrl };
            else if (type == "agsd")
                return new AGSDynamicTileUrlSource()
                {
                    MapServiceUrl = mapServiceUrl,
                    QueryStringValues = settings.ParseQueryString()
                };
            else if (type == "wms1.1.1")
                return new WMSTileUrlSource()
                {
                    WMSVersion = TileHelper.WMS_VERSION_1_1_1,
                    MapServiceUrl = mapServiceUrl,
                    QueryStringValues = settings.ParseQueryString()
                };
            else if (type == "wms1.3.0")
                return new WMSTileUrlSource()
                {
                    WMSVersion = TileHelper.WMS_VERSION_1_3_0,
                    MapServiceUrl = mapServiceUrl,
                    QueryStringValues = settings.ParseQueryString()
                };

            throw new NotSupportedException(string.Format("The map service type '{0}' is not supported.", mapServiceType));
        }

        static IEnumerable<TileCoordinate> GetTiles(int minz, int maxz, double minx, double miny, double maxx, double maxy)
        {
            for (int i = minz; i <= maxz; i++)
            {
                var tiles = TileHelper.GetTilesFromLatLon(i, minx, miny, maxx, maxy);
                foreach (var coord in tiles)
                {
                    yield return new TileCoordinate() { Level = i, Column = coord.X, Row = coord.Y };
                }
            }
        }

        static void ShowHelp(OptionSet p)
        {
            Console.WriteLine("Usage: greet [OPTIONS]+ message");
            Console.WriteLine("Greet a list of individuals with an optional message.");
            Console.WriteLine("If no message is specified, a generic greeting is used.");
            Console.WriteLine();
            Console.WriteLine("Options:");
            p.WriteOptionDescriptions(Console.Out);
        }
    }
}
/*-z=7 -Z=10 -x=-95.844727 -y=35.978006 -X=-88.989258 -Y=40.563895 -o="C:\LocalCache" -t=agsd -m="http://sampleserver1.arcgisonline.com/ArcGIS/rest/services/Demographics/ESRI_Population_World/MapServer" -s="imageSR=3857"*/