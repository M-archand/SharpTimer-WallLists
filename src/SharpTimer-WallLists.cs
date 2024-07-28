using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using CounterStrikeSharp.API.Modules.Timers;
using K4WorldTextSharedAPI;
using System.Drawing;
using System.Data.SQLite;
using System.Text.Json;
using System.Text.Json.Serialization;
using Dapper;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using Npgsql;

namespace SharpTimerWallLists
{
    public class PluginConfig : BasePluginConfig
    {
        [JsonPropertyName("TopCount")]
        public int TopCount { get; set; } = 5;

        [JsonPropertyName("TimeBasedUpdate")]
        public bool TimeBasedUpdate { get; set; } = false;

        [JsonPropertyName("UpdateInterval")]
        public int UpdateInterval { get; set; } = 60;

        [JsonPropertyName("DatabaseType")]
        public int DatabaseType { get; set; } = 1; // 1 = MySQL, 2 = SQLite. 3 = Postgres

        [JsonPropertyName("DatabaseSettings")]
        public DatabaseSettings DatabaseSettings { get; set; } = new DatabaseSettings();

        [JsonPropertyName("PointsTitleText")]
        public string PointsTitleText { get; set; } = "|--- Points Leaders ---|";

        [JsonPropertyName("MapsTitleText")]
        public string MapsTitleText { get; set; } = "|---- Map Records ----|";

        [JsonPropertyName("TitleFontSize")]
        public int TitleFontSize { get; set; } = 26;

        [JsonPropertyName("TitleTextScale")]
        public float TitleTextScale { get; set; } = 0.45f;

        [JsonPropertyName("ListFontSize")]
        public int ListFontSize { get; set; } = 24;

        [JsonPropertyName("ListTextScale")]
        public float ListTextScale { get; set; } = 0.35f;

        [JsonPropertyName("MaxNameLength")]
        public int MaxNameLength { get; set; } = 32; // Default value, 32 is max Steam name length

        [JsonPropertyName("TitleTextColor")]
        public string TitleTextColor { get; set; } = "Pink";

        [JsonPropertyName("FirstPlaceColor")]
        public string FirstPlaceColor { get; set; } = "Lime";

        [JsonPropertyName("SecondPlaceColor")]
        public string SecondPlaceColor { get; set; } = "Magenta";

        [JsonPropertyName("ThirdPlaceColor")]
        public string ThirdPlaceColor { get; set; } = "Cyan";

        [JsonPropertyName("DefaultColor")]
        public string DefaultColor { get; set; } = "White";

        [JsonPropertyName("ConfigVersion")]
        public override int Version { get; set; } = 6;
    }

    public sealed class DatabaseSettings
    {
        [JsonPropertyName("host")]
        public string Host { get; set; } = "localhost";

        [JsonPropertyName("username")]
        public string Username { get; set; } = "root";

        [JsonPropertyName("database")]
        public string Database { get; set; } = "database";

        [JsonPropertyName("password")]
        public string Password { get; set; } = "password";

        [JsonPropertyName("port")]
        public int Port { get; set; } = 3306;

        [JsonPropertyName("sslmode")]
        public string Sslmode { get; set; } = "none";
        [JsonPropertyName("table-prefix")]
	    public string TablePrefix { get; set; } = "";
    }

    [MinimumApiVersion(205)]
    public class PluginSharpTimerWallLists : BasePlugin, IPluginConfig<PluginConfig>
    {
        public override string ModuleName => "SharpTimer Wall Lists";
        public override string ModuleAuthor => "K4ryuu + Marchand";
        public override string ModuleVersion => "1.0.0";

        public required PluginConfig Config { get; set; } = new PluginConfig();

        public static PluginCapability<IK4WorldTextSharedAPI> Capability_SharedAPI { get; } = new("k4-worldtext:sharedapi");

        private List<int> _currentPointsList = new();
        private List<int> _currentMapList = new();
        private CounterStrikeSharp.API.Modules.Timers.Timer? _updateTimer;
        private string _gameDirectory = Server.GameDirectory;
        private string? _databasePath;
        private string? _connectionString;

        public void OnConfigParsed(PluginConfig config)
        {
            if (config.Version < Config.Version)
                base.Logger.LogWarning("Configuration version mismatch (Expected: {0} | Current: {1})", this.Config.Version, config.Version);

            this.Config = config;
        }

        public override void OnAllPluginsLoaded(bool hotReload)
        {
            InitializeDatabasePathAndConnectionString();

            AddTimer(3, () => LoadWorldTextFromFile(Server.MapName));

            if (Config.TimeBasedUpdate)
            {
                _updateTimer = AddTimer(Config.UpdateInterval, RefreshLists, TimerFlags.REPEAT);
            }

            RegisterEventHandler((EventRoundStart @event, GameEventInfo info) =>
            {
                RefreshLists();
                return HookResult.Continue;
            });

            RegisterListener<Listeners.OnMapStart>((mapName) =>
            {
                AddTimer(1, () => LoadWorldTextFromFile(mapName));
            });

            RegisterListener<Listeners.OnMapEnd>(() =>
            {
                var checkAPI = Capability_SharedAPI.Get();
                if (checkAPI != null)
                {
                    _currentPointsList.ForEach(id => checkAPI.RemoveWorldText(id, false));
                    _currentMapList.ForEach(id => checkAPI.RemoveWorldText(id, false));
                }
                _currentPointsList.Clear();
                _currentMapList.Clear();
            });
        }

        public override void Unload(bool hotReload)
        {
            var checkAPI = Capability_SharedAPI.Get();
            if (checkAPI != null)
            {
                _currentPointsList.ForEach(id => checkAPI.RemoveWorldText(id, false));
                _currentMapList.ForEach(id => checkAPI.RemoveWorldText(id, false));
            }
            _currentPointsList.Clear();
            _currentMapList.Clear();
            _updateTimer?.Kill();
        }

        private void InitializeDatabasePathAndConnectionString()
        {
            var dbSettings = Config.DatabaseSettings;
            if (Config.DatabaseType == 1)
            {
                var mySqlSslMode = dbSettings.Sslmode.ToLower() switch
                {
                    "none" => MySqlSslMode.None,
                    "preferred" => MySqlSslMode.Preferred,
                    "required" => MySqlSslMode.Required,
                    "verifyca" => MySqlSslMode.VerifyCA,
                    "verifyfull" => MySqlSslMode.VerifyFull,
                    _ => MySqlSslMode.None
                };
                _connectionString = $@"Server={dbSettings.Host};Port={dbSettings.Port};Database={dbSettings.Database};Uid={dbSettings.Username};Pwd={dbSettings.Password};SslMode={mySqlSslMode};";
            }
            else if (Config.DatabaseType == 2)
            {
                _databasePath = Path.Combine(_gameDirectory, "csgo", "cfg", "SharpTimer", "database.db");
                _connectionString = $"Data Source={_databasePath};Version=3;";
            }
            else if (Config.DatabaseType == 3)
            {
                var npgSqlSslMode = dbSettings.Sslmode.ToLower() switch
                {
                    "disable" => SslMode.Disable,
                    "require" => SslMode.Require,
                    "prefer" => SslMode.Prefer,
                    "allow" => SslMode.Allow,
                    "verify-full" => SslMode.VerifyFull,
                    _ => SslMode.Disable
                };
                _connectionString = $"Host={dbSettings.Host};Port={dbSettings.Port};Database={dbSettings.Database};Username={dbSettings.Username};Password={dbSettings.Password};SslMode={npgSqlSslMode};";
            }
        }

        [ConsoleCommand("css_pointslist", "Sets up the points list")]
        [RequiresPermissions("@css/root")]
        public void OnPointsListAdd(CCSPlayerController player, CommandInfo command)
        {
            CreateTopList(player, command, ListType.Points);
        }

        [ConsoleCommand("css_maplist", "Sets up the map top list")]
        [RequiresPermissions("@css/root")]
        public void OnMapListAdd(CCSPlayerController player, CommandInfo command)
        {
            CreateTopList(player, command, ListType.Maps);
        }

        [ConsoleCommand("css_remlist", "Removes the closest list, whether points or map")]
        [RequiresPermissions("@css/root")]
        public void OnRemoveList(CCSPlayerController player, CommandInfo command)
        {
            RemoveClosestList(player, command);
        }

        private void CreateTopList(CCSPlayerController player, CommandInfo command, ListType listType)
        {
            var checkAPI = Capability_SharedAPI.Get();
            if (checkAPI is null)
            {
                command.ReplyToCommand($" {ChatColors.Silver}[ {ChatColors.Lime}{listType}List {ChatColors.Silver}] {ChatColors.LightRed}Failed to get the shared API.");
                return;
            }

            var mapName = Server.MapName;

            Task.Run(async () =>
            {
                try
                {
                    var topList = await GetTopPlayersAsync(Config.TopCount, listType, mapName);
                    var linesList = GetTopListTextLines(topList, listType);

                    Server.NextWorldUpdate(() =>
                    {
                        try
                        {
                            int messageID = checkAPI.AddWorldTextAtPlayer(player, TextPlacement.Wall, linesList);
                            if (listType == ListType.Points)
                                _currentPointsList.Add(messageID);
                            if (listType == ListType.Maps)
                                _currentMapList.Add(messageID);

                            var lineList = checkAPI.GetWorldTextLineEntities(messageID);
                            if (lineList?.Count > 0)
                            {
                                var location = lineList[0]?.AbsOrigin;
                                var rotation = lineList[0]?.AbsRotation;

                                if (location != null && rotation != null)
                                {
                                    SaveWorldTextToFile(location, rotation, listType);
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError(ex, "Error during NextWorldUpdate for CreateTopList.");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error creating top list in CreateTopList method.");
                }
            });
        }

        private void RemoveClosestList(CCSPlayerController player, CommandInfo command)
        {
            var checkAPI = Capability_SharedAPI.Get();
            if (checkAPI is null)
            {
                command.ReplyToCommand($" {ChatColors.Silver}[ {ChatColors.Lime}List {ChatColors.Silver}] {ChatColors.LightRed}Failed to get the shared API.");
                return;
            }

            var combinedList = _currentPointsList.Concat(_currentMapList).ToList();

            var target = combinedList
                .SelectMany(id => checkAPI.GetWorldTextLineEntities(id)?.Select(entity => new { Id = id, Entity = entity, IsPointsList = _currentPointsList.Contains(id) }) ?? Enumerable.Empty<dynamic>())
                .Where(x => x.Entity.AbsOrigin != null && player.PlayerPawn.Value?.AbsOrigin != null && DistanceTo(x.Entity.AbsOrigin, player.PlayerPawn.Value!.AbsOrigin) < 100)
                .OrderBy(x => x.Entity.AbsOrigin != null && player.PlayerPawn.Value?.AbsOrigin != null ? DistanceTo(x.Entity.AbsOrigin, player.PlayerPawn.Value!.AbsOrigin) : float.MaxValue)
                .FirstOrDefault();

            if (target is null)
            {
                command.ReplyToCommand($" {ChatColors.Silver}[ {ChatColors.Lime}List {ChatColors.Silver}] {ChatColors.Red}Move closer to the list that you want to remove.");
                return;
            }

            try
            {
                checkAPI.RemoveWorldText(target.Id, false);
                if (target.IsPointsList)
                {
                    _currentPointsList.Remove(target.Id);
                }
                else
                {
                    _currentMapList.Remove(target.Id);
                }

                var mapName = Server.MapName;
                var path = target.IsPointsList
                    ? Path.Combine(ModuleDirectory, $"{mapName}_pointslist.json")
                    : Path.Combine(ModuleDirectory, $"{mapName}_maplist.json");

                if (File.Exists(path))
                {
                    var data = JsonSerializer.Deserialize<List<WorldTextData>>(File.ReadAllText(path));
                    if (data != null)
                    {
                        Vector entityVector = target.Entity.AbsOrigin;
                        data.RemoveAll(x =>
                        {
                            Vector location = ParseVector(x.Location);
                            return location.X == entityVector.X &&
                                location.Y == entityVector.Y &&
                                x.Rotation == target.Entity.AbsRotation.ToString();
                        });

                        var options = new JsonSerializerOptions
                        {
                            WriteIndented = true
                        };

                        string jsonString = JsonSerializer.Serialize(data, options);
                        File.WriteAllText(path, jsonString);
                    }
                }
                command.ReplyToCommand($" {ChatColors.Silver}[ {ChatColors.Lime}List {ChatColors.Silver}] {ChatColors.Green}List removed!");
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error removing list in RemoveClosestList method.");
            }
        }


        private float DistanceTo(Vector a, Vector b)
        {
            float dx = a.X - b.X;
            float dy = a.Y - b.Y;
            float dz = a.Z - b.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        private void SaveWorldTextToFile(Vector location, QAngle rotation, ListType listType)
        {
            try
            {
                var mapName = Server.MapName;
                var path = Path.Combine(ModuleDirectory, $"{mapName}_{listType.ToString().ToLower()}list.json");
                var worldTextData = new WorldTextData
                {
                    Location = location.ToString(),
                    Rotation = rotation.ToString()
                };

                List<WorldTextData> data;
                if (File.Exists(path))
                {
                    data = JsonSerializer.Deserialize<List<WorldTextData>>(File.ReadAllText(path)) ?? new List<WorldTextData>();
                }
                else
                {
                    data = new List<WorldTextData>();
                }

                data.Add(worldTextData);

                var options = new JsonSerializerOptions
                {
                    WriteIndented = true
                };

                string jsonString = JsonSerializer.Serialize(data, options);
                File.WriteAllText(path, jsonString);
            }
            catch (Exception ex)
            {
                Logger.LogError(ex, "Error saving world text to file in SaveWorldTextToFile method.");
            }
        }

        private void LoadWorldTextFromFile(string path, ListType listType, string mapName)
        {
            if (File.Exists(path))
            {
                var data = JsonSerializer.Deserialize<List<WorldTextData>>(File.ReadAllText(path));
                if (data == null) return;

                Task.Run(async () =>
                {
                    try
                    {
                        var topList = await GetTopPlayersAsync(Config.TopCount, listType, mapName);
                        var linesList = GetTopListTextLines(topList, listType);

                        Server.NextWorldUpdate(() =>
                        {
                            try
                            {
                                var checkAPI = Capability_SharedAPI.Get();
                                if (checkAPI is null) return;

                                foreach (var worldTextData in data)
                                {
                                    if (!string.IsNullOrEmpty(worldTextData.Location) && !string.IsNullOrEmpty(worldTextData.Rotation))
                                    {
                                        var messageID = checkAPI.AddWorldText(TextPlacement.Wall, linesList, ParseVector(worldTextData.Location), ParseQAngle(worldTextData.Rotation));
                                        if (listType == ListType.Points)
                                            _currentPointsList.Add(messageID);
                                        else if (listType == ListType.Maps)
                                            _currentMapList.Add(messageID);
                                    }
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.LogError(ex, "Error during NextWorldUpdate in LoadWorldTextFromFile.");
                            }
                        });
                    }
                    catch (Exception ex)
                    {
                        Logger.LogError(ex, "Error loading world text from file in LoadWorldTextFromFile method.");
                    }
                });
            }
        }

        private void LoadWorldTextFromFile(string? passedMapName = null)
        {
            var mapName = passedMapName ?? Server.MapName;
            var pointsPath = Path.Combine(ModuleDirectory, $"{mapName}_pointslist.json");
            var mapsPath = Path.Combine(ModuleDirectory, $"{mapName}_maplist.json");

            LoadWorldTextFromFile(pointsPath, ListType.Points, mapName);
            LoadWorldTextFromFile(mapsPath, ListType.Maps, mapName);
        }

        public static Vector ParseVector(string vectorString)
        {
            string[] components = vectorString.Split(' ');
            if (components.Length == 3 &&
                float.TryParse(components[0], out float x) &&
                float.TryParse(components[1], out float y) &&
                float.TryParse(components[2], out float z))
            {
                return new Vector(x, y, z);
            }
            throw new ArgumentException("Invalid vector string format.");
        }

        public static QAngle ParseQAngle(string qangleString)
        {
            string[] components = qangleString.Split(' ');
            if (components.Length == 3 &&
                float.TryParse(components[0], out float x) &&
                float.TryParse(components[1], out float y) &&
                float.TryParse(components[2], out float z))
            {
                return new QAngle(x, y, z);
            }
            throw new ArgumentException("Invalid QAngle string format.");
        }

        private void RefreshLists()
        {
            var mapName = Server.MapName;
            
            Task.Run(async () =>
            {
                try
                {
                    var pointsTopList = await GetTopPlayersAsync(Config.TopCount, ListType.Points, mapName);
                    var mapsTopList = await GetTopPlayersAsync(Config.TopCount, ListType.Maps, mapName);

                    var pointsLinesList = GetTopListTextLines(pointsTopList, ListType.Points);
                    var mapsLinesList = GetTopListTextLines(mapsTopList, ListType.Maps);

                    Server.NextWorldUpdate(() =>
                    {
                        try
                        {
                            var checkAPI = Capability_SharedAPI.Get();
                            if (checkAPI != null)
                            {
                                _currentPointsList.ForEach(id => checkAPI.UpdateWorldText(id, pointsLinesList));
                                _currentMapList.ForEach(id => checkAPI.UpdateWorldText(id, mapsLinesList));
                            }
                        }
                        catch (Exception ex)
                        {
                            Logger.LogError(ex, "Error during NextWorldUpdate in RefreshLists.");
                        }
                    });
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "Error refreshing lists in RefreshLists method.");
                }
            });
        }

        private string TruncateString(string value, int maxLength)
        {
            if (string.IsNullOrEmpty(value)) return value;
            return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
        }

        private List<TextLine> GetTopListTextLines(List<PlayerPlace> topList, ListType listType)
        {
            Color ParseColor(string colorName)
            {
                try
                {
                    var colorProperty = typeof(Color).GetProperty(colorName, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
                    if (colorProperty == null)
                    {
                        throw new ArgumentException($"Invalid color name: {colorName}");
                    }

                    var colorValue = colorProperty.GetValue(null);
                    if (colorValue == null)
                    {
                        throw new InvalidOperationException($"Color property '{colorName}' has no value.");
                    }

                    return (Color)colorValue;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, $"Invalid color name: {colorName}. Falling back to White.");
                    return Color.White;
                }
            }

            int maxNameLength = Config.MaxNameLength;
            var linesList = new List<TextLine>();

            if (listType == ListType.Points)
            {
                linesList.Add(new TextLine
                {
                    Text = Config.PointsTitleText,
                    Color = ParseColor(Config.TitleTextColor),
                    FontSize = Config.TitleFontSize,
                    FullBright = true,
                    Scale = Config.TitleTextScale
                });

                for (int i = 0; i < topList.Count; i++)
                {
                    var topplayer = topList[i];
                    var truncatedName = TruncateString(topplayer.PlayerName, maxNameLength);
                    var color = i switch
                    {
                        0 => ParseColor(Config.FirstPlaceColor),
                        1 => ParseColor(Config.SecondPlaceColor),
                        2 => ParseColor(Config.ThirdPlaceColor),
                        _ => ParseColor(Config.DefaultColor)
                    };

                    var lineText = $"{i + 1}. {truncatedName} - {topplayer.GlobalPoints} points";

                    linesList.Add(new TextLine
                    {
                        Text = lineText,
                        Color = color,
                        FontSize = Config.ListFontSize,
                        FullBright = true,
                        Scale = Config.ListTextScale
                    });
                }
            }
            else if (listType == ListType.Maps)
            {
                linesList.Add(new TextLine
                {
                    Text = Config.MapsTitleText,
                    Color = ParseColor(Config.TitleTextColor),
                    FontSize = Config.TitleFontSize,
                    FullBright = true,
                    Scale = Config.TitleTextScale
                });

                for (int i = 0; i < topList.Count; i++)
                {
                    var topplayer = topList[i];
                    var truncatedName = TruncateString(topplayer.PlayerName, maxNameLength);
                    var color = i switch
                    {
                        0 => ParseColor(Config.FirstPlaceColor),
                        1 => ParseColor(Config.SecondPlaceColor),
                        2 => ParseColor(Config.ThirdPlaceColor),
                        _ => ParseColor(Config.DefaultColor)
                    };

                    var lineText = $"{i + 1}. {topplayer.FormattedTime} - {truncatedName}";

                    linesList.Add(new TextLine
                    {
                        Text = lineText,
                        Color = color,
                        FontSize = Config.ListFontSize,
                        FullBright = true,
                        Scale = Config.ListTextScale
                    });
                }
            }

            return linesList;
        }

        public async Task<List<PlayerPlace>> GetTopPlayersAsync(int topCount, ListType listType, string mapName)
        {
            string tablePrefix = Config.DatabaseSettings.TablePrefix;
            string query;
            if (Config.DatabaseType == 1)
            {
                query = listType switch
                {
                    ListType.Points => $@"
                    WITH RankedPlayers AS (
                        SELECT
                            SteamID,
                            PlayerName,
                            GlobalPoints,
                            DENSE_RANK() OVER (ORDER BY GlobalPoints DESC) AS playerPlace
                        FROM {tablePrefix}PlayerStats
                    )
                    SELECT SteamID, PlayerName, GlobalPoints, playerPlace
                    FROM RankedPlayers
                    ORDER BY GlobalPoints DESC
                    LIMIT @TopCount",
                    ListType.Maps => $@"
                    WITH RankedPlayers AS (
                        SELECT
                            SteamID,
                            PlayerName,
                            FormattedTime,
                            DENSE_RANK() OVER (ORDER BY STR_TO_DATE(FormattedTime, '%i:%s.%f') ASC) AS playerPlace
                        FROM {tablePrefix}PlayerRecords
                        WHERE MapName = @MapName
                    )
                    SELECT SteamID, PlayerName, FormattedTime, playerPlace
                    FROM RankedPlayers
                    ORDER BY STR_TO_DATE(FormattedTime, '%i:%s.%f') ASC
                    LIMIT @TopCount",
                    _ => throw new ArgumentException("Invalid list type")
                };
            }
            else if (Config.DatabaseType == 2)
            {
                query = listType switch
                {
                    ListType.Points => $@"
                    WITH RankedPlayers AS (
                        SELECT
                            SteamID,
                            PlayerName,
                            GlobalPoints,
                            DENSE_RANK() OVER (ORDER BY GlobalPoints DESC) AS playerPlace
                        FROM {tablePrefix}PlayerStats
                    )
                    SELECT SteamID, PlayerName, GlobalPoints, playerPlace
                    FROM RankedPlayers
                    ORDER BY GlobalPoints DESC
                    LIMIT @TopCount",
                    ListType.Maps => $@"
                    WITH RankedPlayers AS (
                        SELECT
                            SteamID,
                            PlayerName,
                            FormattedTime,
                            DENSE_RANK() OVER (ORDER BY strftime('%M:%S.%f', FormattedTime) ASC) AS playerPlace
                        FROM {tablePrefix}PlayerRecords
                        WHERE MapName = @MapName
                    )
                    SELECT SteamID, PlayerName, FormattedTime, playerPlace
                    FROM RankedPlayers
                    ORDER BY strftime('%M:%S.%f', FormattedTime) ASC
                    LIMIT @TopCount",
                    _ => throw new ArgumentException("Invalid list type")
                };
            }
            else if (Config.DatabaseType == 3)
            {
                query = listType switch
                {
                    ListType.Points => $@"
                    WITH RankedPlayers AS (
                        SELECT
                            ""SteamID"",
                            ""PlayerName"",
                            ""GlobalPoints"",
                            DENSE_RANK() OVER (ORDER BY ""GlobalPoints"" DESC) AS playerPlace
                        FROM ""{tablePrefix}PlayerStats""
                    )
                    SELECT ""SteamID"", ""PlayerName"", ""GlobalPoints"", playerPlace
                    FROM RankedPlayers
                    ORDER BY ""GlobalPoints"" DESC
                    LIMIT @TopCount",
                    ListType.Maps => $@"
                    WITH RankedPlayers AS (
                        SELECT
                            ""SteamID"",
                            ""PlayerName"",
                            ""FormattedTime"",
                            DENSE_RANK() OVER (ORDER BY to_timestamp(""FormattedTime"", 'MI:SS.US') ASC) AS playerPlace
                        FROM ""{tablePrefix}PlayerRecords""
                        WHERE ""MapName"" = @MapName
                    )
                    SELECT ""SteamID"", ""PlayerName"", ""FormattedTime"", playerPlace
                    FROM RankedPlayers
                    ORDER BY to_timestamp(""FormattedTime"", 'MI:SS.US') ASC
                    LIMIT @TopCount",
                    _ => throw new ArgumentException("Invalid list type")
                };
            }
            else
            {
                Logger.LogError("Invalid DatabaseType specified in config");
                return new List<PlayerPlace>();
            }

            if (Config.DatabaseType == 1)
            {
                try
                {
                    using (var connection = new MySqlConnection(_connectionString))
                    {
                        object parameters = listType switch
                        {
                            ListType.Points => new { TopCount = topCount },
                            ListType.Maps => new { TopCount = topCount, MapName = mapName },
                            _ => throw new ArgumentException("Invalid list type")
                        };

                        return (await connection.QueryAsync<PlayerPlace>(query, parameters)).ToList();
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, $"Failed to retrieve top players from MySQL for {listType}, please check your database credentials in the config");
                    return new List<PlayerPlace>();
                }
            }
            else if (Config.DatabaseType == 2)
            {
                try
                {
                    using (var connection = new SQLiteConnection(_connectionString))
                    {
                        connection.Open();
                        object parameters = listType switch
                        {
                            ListType.Points => new { TopCount = topCount },
                            ListType.Maps => new { TopCount = topCount, MapName = mapName },
                            _ => throw new ArgumentException("Invalid list type")
                        };

                        return (await connection.QueryAsync<PlayerPlace>(query, parameters)).ToList();
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, $"Failed to retrieve top players from SQLite. Make sure your database is located at cfg/SharpTimer/database.db");
                    return new List<PlayerPlace>();
                }
            }
            else if (Config.DatabaseType == 3)
            {
                try
                {
                    using (var connection = new NpgsqlConnection(_connectionString))
                    {
                        object parameters = listType switch
                        {
                            ListType.Points => new { TopCount = topCount },
                            ListType.Maps => new { TopCount = topCount, MapName = mapName },
                            _ => throw new ArgumentException("Invalid list type")
                        };

                        return (await connection.QueryAsync<PlayerPlace>(query, parameters)).ToList();
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogError(ex, $"Failed to retrieve top players from PostgreSQL for {listType}, please check your database credentials in the config");
                    return new List<PlayerPlace>();
                }
            }
            else
            {
                Logger.LogError("Invalid DatabaseType specified in config");
                return new List<PlayerPlace>();
            }
        }
    }

    public enum ListType
    {
        Points,
        Maps
    }

    public class PlayerPlace
    {
        public required string PlayerName { get; set; }
        public int GlobalPoints { get; set; }
        public string? FormattedTime { get; set; }
        public int Placement { get; set; }
    }

    public class WorldTextData
    {
        public required string Location { get; set; }
        public required string Rotation { get; set; }
    }
}