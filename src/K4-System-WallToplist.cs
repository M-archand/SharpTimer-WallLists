using System.Drawing;
using System.Text.Json.Serialization;
using CounterStrikeSharp.API;
using CounterStrikeSharp.API.Core;
using CounterStrikeSharp.API.Core.Attributes;
using CounterStrikeSharp.API.Core.Attributes.Registration;
using CounterStrikeSharp.API.Core.Capabilities;
using CounterStrikeSharp.API.Modules.Admin;
using CounterStrikeSharp.API.Modules.Commands;
using CounterStrikeSharp.API.Modules.Utils;
using Dapper;
using K4WorldTextSharedAPI;
using Microsoft.Extensions.Logging;
using MySqlConnector;
using System.Text.Json;
using CounterStrikeSharp.API.Modules.Timers;

namespace SharpTimerMapList;

public class PluginConfig : BasePluginConfig
{
	[JsonPropertyName("topCount")]
	public int TopCount { get; set; } = 5;
	[JsonPropertyName("timeBasedUpdate")]
	public bool TimeBasedUpdate { get; set; } = false;
	[JsonPropertyName("updateInterval")]
	public int UpdateInterval { get; set; } = 60;
	[JsonPropertyName("databaseSettings")]
	public DatabaseSettings DatabaseSettings { get; set; } = new DatabaseSettings();
	[JsonPropertyName("titleText")]
	public string TitleText { get; set; } = "---- Map Records ----";
	[JsonPropertyName("maxNameLength")]
	public int MaxNameLength { get; set; } = 20;
	
	[JsonPropertyName("titleTextColor")]
	public string TitleTextColor { get; set; } = "Pink";
	[JsonPropertyName("firstPlaceColor")]
	public string FirstPlaceColor { get; set; } = "Lime";
	[JsonPropertyName("secondPlaceColor")]
	public string SecondPlaceColor { get; set; } = "Cyan";
	[JsonPropertyName("thirdPlaceColor")]
	public string ThirdPlaceColor { get; set; } = "Purple";
	[JsonPropertyName("defaultColor")]
	public string DefaultColor { get; set; } = "White";
	[JsonPropertyName("configVersion")]
	public override int Version { get; set; } = 4;
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
}

[MinimumApiVersion(205)]
public class PluginSharpTimerMapList : BasePlugin, IPluginConfig<PluginConfig>
{
	public override string ModuleName => "SharpTimer Map Top List";
	public override string ModuleAuthor => "K4ryuu (SharpTimer edit by Marchand)";
	public override string ModuleVersion => "1.0.2";
	public required PluginConfig Config { get; set; } = new PluginConfig();
	public static PluginCapability<IK4WorldTextSharedAPI> Capability_SharedAPI { get; } = new("k4-worldtext:sharedapi");

	private readonly List<int> _currentTopLists = new();
	private CounterStrikeSharp.API.Modules.Timers.Timer? _updateTimer;

	public void OnConfigParsed(PluginConfig config)
	{
		if (config.Version < Config.Version)
			base.Logger.LogWarning("Configuration version mismatch (Expected: {0} | Current: {1})", this.Config.Version, config.Version);

		this.Config = config;
	}

	public override void OnAllPluginsLoaded(bool hotReload)
	{
		AddTimer(3, () => LoadWorldTextFromFile(Server.MapName));

		if (Config.TimeBasedUpdate)
		{
			_updateTimer = AddTimer(Config.UpdateInterval, RefreshTopLists, TimerFlags.REPEAT);
		}

		RegisterEventHandler((EventRoundStart @event, GameEventInfo info) =>
		{
			RefreshTopLists();
			return HookResult.Continue;
		});

		RegisterListener<Listeners.OnMapStart>((mapName) =>
		{
			var mapNameString = mapName;
			AddTimer(1, () => LoadWorldTextFromFile(mapNameString));
		});

		RegisterListener<Listeners.OnMapEnd>(() =>
		{
			ClearCurrentTopLists();
		});
	}

	public override void Unload(bool hotReload)
	{
		foreach (int messageID in _currentTopLists)
		{
			Capability_SharedAPI.Get()?.RemoveWorldText(messageID);
		}
		ClearCurrentTopLists();
		_updateTimer?.Kill();
	}

	[ConsoleCommand("css_maplist", "Sets up the map top list")]
	[RequiresPermissions("@css/root")]
	public void OnToplistAdd(CCSPlayerController player, CommandInfo command)
	{
		var checkAPI = Capability_SharedAPI.Get();
		if (checkAPI is null)
		{
			command.ReplyToCommand($" {ChatColors.Silver}[ {ChatColors.Lime}MapList {ChatColors.Silver}] {ChatColors.LightRed}Failed to get the shared API.");
			return;
		}

		var mapName = Server.MapName;

		_ = Task.Run(async () =>
		{
			var topList = await GetTopPlayersAsync(Config.TopCount, mapName).ConfigureAwait(false);
			var linesList = GetTopListTextLines(topList);

			Server.NextWorldUpdate(() =>
			{
				int messageID = checkAPI.AddWorldTextAtPlayer(player, TextPlacement.Wall, linesList);
				AddToCurrentTopLists(messageID);

				var lineList = checkAPI.GetWorldTextLineEntities(messageID);
				if (lineList?.Count > 0)
				{
					var location = lineList[0]?.AbsOrigin;
					var rotation = lineList[0]?.AbsRotation;

					if (location != null && rotation != null)
					{
						SaveWorldTextToFile(location, rotation);
					}
					else
					{
						Logger.LogError("Failed to get location or rotation for message ID: {0}", messageID);
					}
				}
				else
				{
					Logger.LogError("Failed to get world text line entities for message ID: {0}", messageID);
				}
			});
		});
	}

	[ConsoleCommand("css_maprem", "Removes the closest list")]
	[RequiresPermissions("@css/root")]
	public void OnToplistRemove(CCSPlayerController player, CommandInfo command)
	{
		var checkAPI = Capability_SharedAPI.Get();
		if (checkAPI is null)
		{
			command.ReplyToCommand($" {ChatColors.Silver}[ {ChatColors.Lime}MapList {ChatColors.Silver}] {ChatColors.LightRed}Failed to get the shared API.");
			return;
		}

		//new chatgpt
		var playerPosition = player.PlayerPawn.Value?.AbsOrigin;
		if (playerPosition == null)
		{
			command.ReplyToCommand($" {ChatColors.Silver}[ {ChatColors.Lime}MapList {ChatColors.Silver}] {ChatColors.Red}Could not determine player position.");
			return;
		}

		var target = _currentTopLists
			.SelectMany(id => checkAPI.GetWorldTextLineEntities(id)?.Select(entity => new { Id = id, Entity = entity }) ?? Enumerable.Empty<dynamic>())
			.Where(x => x.Entity.AbsOrigin != null && DistanceTo(x.Entity.AbsOrigin, playerPosition) < 100)
			.OrderBy(x => DistanceTo(x.Entity.AbsOrigin, playerPosition))
			.FirstOrDefault();

		if (target is null)
		{
			command.ReplyToCommand($" {ChatColors.Silver}[ {ChatColors.Lime}MapList {ChatColors.Silver}] {ChatColors.Red}Move closer to the list that you want to remove.");
			return;
		}

		checkAPI.RemoveWorldText(target.Id);
		RemoveFromCurrentTopLists(target.Id);

		var mapName = Server.MapName;
		var path = Path.Combine(ModuleDirectory, $"{mapName}_maplist.json");
		if (File.Exists(path))
		{
			var data = JsonSerializer.Deserialize<List<WorldTextData>>(File.ReadAllText(path), JsonSerializerOptions);
			if (data != null)
			{
				var targetLocation = SerializeVector(target.Entity.AbsOrigin);
				var targetRotation = SerializeQAngle(target.Entity.AbsRotation);

				data.RemoveAll(x => x.Location == targetLocation && x.Rotation == targetRotation);

				File.WriteAllText(path, JsonSerializer.Serialize(data, JsonSerializerOptions));
			}
		}

		command.ReplyToCommand($" {ChatColors.Silver}[ {ChatColors.Lime}MapList {ChatColors.Silver}] {ChatColors.Green}List removed!");
	}

	private float DistanceTo(Vector a, Vector b)
	{
		float dx = a.X - b.X;
		float dy = a.Y - b.Y;
		float dz = a.Z - b.Z;
		return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
	}

	private static readonly JsonSerializerOptions JsonSerializerOptions = new JsonSerializerOptions
	{
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
		WriteIndented = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
	};
	
	private string SerializeVector(Vector vector)
	{
		return $"{vector.X} {vector.Y} {vector.Z}";
	}

	private string SerializeQAngle(QAngle qangle)
	{
		return $"{qangle.X} {qangle.Y} {qangle.Z}";
	}
	private void SaveWorldTextToFile(Vector location, QAngle rotation)
	{
		var mapName = Server.MapName;
		var path = Path.Combine(ModuleDirectory, $"{mapName}_maplist.json");
		var worldTextData = new WorldTextData
		{
			Location = SerializeVector(location),
        	Rotation = SerializeQAngle(rotation)
		};

		List<WorldTextData> data;
		if (File.Exists(path))
		{
			data = JsonSerializer.Deserialize<List<WorldTextData>>(File.ReadAllText(path), JsonSerializerOptions) ?? new List<WorldTextData>();
		}
		else
		{
			data = new List<WorldTextData>();
		}

		data.Add(worldTextData);

		File.WriteAllText(path, JsonSerializer.Serialize(data, JsonSerializerOptions));
	}

	private void LoadWorldTextFromFile(string mapName)
	{
		
		var path = Path.Combine(ModuleDirectory, $"{mapName}_maplist.json");

		if (File.Exists(path))
		{
			var data = JsonSerializer.Deserialize<List<WorldTextData>>(File.ReadAllText(path), JsonSerializerOptions);
			if (data == null) return;

			_ = Task.Run(async () =>
			{
				var topList = await GetTopPlayersAsync(Config.TopCount, mapName).ConfigureAwait(false);
				var linesList = GetTopListTextLines(topList);

				Server.NextWorldUpdate(() =>
				{
					var checkAPI = Capability_SharedAPI.Get();
					if (checkAPI is null) return;

					foreach (var worldTextData in data)
					{
						if (!string.IsNullOrEmpty(worldTextData.Location) && !string.IsNullOrEmpty(worldTextData.Rotation))
						{
							var messageID = checkAPI.AddWorldText(TextPlacement.Wall, linesList, ParseVector(worldTextData.Location), ParseQAngle(worldTextData.Rotation));
							AddToCurrentTopLists(messageID);
						}
					}
				});
			});
		}
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
    private void AddToCurrentTopLists(int messageID)
    {
        lock (_currentTopLists)
        {
            _currentTopLists.Add(messageID);
        }
    }

    private void RemoveFromCurrentTopLists(int messageID)
    {
        lock (_currentTopLists)
        {
            _currentTopLists.Remove(messageID);
        }
    }

    private void ClearCurrentTopLists()
    {
        lock (_currentTopLists)
        {
            _currentTopLists.Clear();
        }
    }
	private void RefreshTopLists()
	{
		var mapName = Server.MapName;
		
		_ = Task.Run(async () =>
		{
			var topList = await GetTopPlayersAsync(Config.TopCount, mapName).ConfigureAwait(false);
			var linesList = GetTopListTextLines(topList);

			Server.NextWorldUpdate(() =>
			{
				AddTimer(1, () =>
				{
					var checkAPI = Capability_SharedAPI.Get();
					if (checkAPI != null)
					{
						foreach (int messageID in _currentTopLists)
						{
							checkAPI.UpdateWorldText(messageID, linesList);
						}
					}
				});
			});
		});
	}


	private string TruncateString(string value, int maxLength)
	{
		if (string.IsNullOrEmpty(value)) return value;
		return value.Length <= maxLength ? value : value.Substring(0, maxLength) + "...";
	}
	private List<TextLine> GetTopListTextLines(List<PlayerPlace> topList)
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
		var linesList = new List<TextLine>
		{
			new TextLine
			{
				Text = Config.TitleText,
				Color = ParseColor(Config.TitleTextColor),
				FontSize = 26,
				FullBright = true,
				Scale = 0.45f
			}
		};

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

			linesList.Add(new TextLine
			{
				Text = $"{i + 1}. {topplayer.FormattedTime} - {truncatedName}",
				Color = color,
				FontSize = 24,
				FullBright = true,
				Scale = 0.35f,
			});
		}

		return linesList;
	}

	public async Task<List<PlayerPlace>> GetTopPlayersAsync(int topCount, string mapName)
	{
		try
		{
			var dbSettings = Config.DatabaseSettings;

			using (var connection = new MySqlConnection($@"Server={dbSettings.Host};Port={dbSettings.Port};Database={dbSettings.Database};
				Uid={dbSettings.Username};Pwd={dbSettings.Password};
				SslMode={Enum.Parse<MySqlSslMode>(dbSettings.Sslmode, true)};"))
			{
				string query = @"
				WITH RankedPlayers AS (
					SELECT
						SteamID,
						PlayerName,
						FormattedTime,
						DENSE_RANK() OVER (ORDER BY STR_TO_DATE(FormattedTime, '%i:%s.%f') ASC) AS playerPlace
					FROM PlayerRecords
					WHERE MapName = @MapName
				)
				SELECT SteamID, PlayerName, FormattedTime, playerPlace
				FROM RankedPlayers
				ORDER BY STR_TO_DATE(FormattedTime, '%i:%s.%f') ASC
				LIMIT @TopCount";

				var parameters = new { TopCount = topCount, MapName = mapName };
				return (await connection.QueryAsync<PlayerPlace>(query, parameters)).ToList();
			}
		}

		catch (MySqlException ex)
		{
			Logger.LogError(ex, "Failed to retrieve top players: {Message}", ex.Message);
			return new List<PlayerPlace>();
		}
		catch (Exception ex)
		{
			Logger.LogError(ex, "An unexpected error occurred: {Message}", ex.Message);
			return new List<PlayerPlace>();
		}
	}
}


public class PlayerPlace
{
    public string SteamID { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string FormattedTime { get; set; } = "";
    public int playerPlace { get; set; }
}

public class WorldTextData
{
	public required string Location { get; set; }
	public required string Rotation { get; set; }
}
