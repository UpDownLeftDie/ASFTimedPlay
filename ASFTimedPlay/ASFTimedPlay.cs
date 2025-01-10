using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading; // Add for Timer
using System.Threading.Tasks;
using System.Composition;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Helpers.Json;
using ArchiSteamFarm.Plugins.Interfaces; // For IBotModules
using ArchiSteamFarm.Steam;
using JetBrains.Annotations;

namespace ASFTimedPlay;

#pragma warning disable CA1812 // ASF uses this class during runtime
[Export(typeof(IPlugin))]
[UsedImplicitly]
internal sealed class ASFTimedPlay : IGitHubPluginUpdates, IPlugin, IAsyncDisposable, IBotCommand2 {
	public string Name => nameof(ASFTimedPlay);
	public string RepositoryName => "UpDownLeftDie/ASFTimedPlay";
	public Version Version =>
		typeof(ASFTimedPlay).Assembly.GetName().Version
		?? throw new InvalidOperationException(nameof(Version));
	public static string UpdateChannel => "stable";

	private static ASFTimedPlay? Instance { get; set; }
	private readonly List<Timer> ActiveTimers = [];
	private static readonly Dictionary<Bot, HashSet<uint>> IdleGames = [];
	internal static readonly Dictionary<Bot, IdleModule> BotIdleModules = [];
	internal static TimedPlayConfig? Config { get; private set; }

	private const string ConfigFile = "ASFTimedPlay.json";
	private readonly Timer ConfigSaveTimer;
	internal static readonly SemaphoreSlim ConfigLock = new(1, 1);

	public ASFTimedPlay() {
		Instance = this;
		_ = LoadConfig();

		// Save config every 5 minutes
		ConfigSaveTimer = new Timer(
			async _ => await SaveConfig().ConfigureAwait(false),
			null,
			TimeSpan.FromMinutes(5),
			TimeSpan.FromMinutes(5)
		);
	}

	private static async Task LoadConfig() {
		await ConfigLock.WaitAsync().ConfigureAwait(false);

		try {
			if (!File.Exists(ConfigFile)) {
				// Create default config
				Config = new TimedPlayConfig {
					IdleGames = [],
					PlayForGames = []
				};
				await SaveConfig().ConfigureAwait(false);
				return;
			}

			string json = await File.ReadAllTextAsync(ConfigFile).ConfigureAwait(false);
			Config = json.ToJsonObject<TimedPlayConfig>();

			// Restore idle games
			foreach (
				(string botName, HashSet<uint> games) in Config?.IdleGames ?? []
			) {
				Bot? bot = Bot.GetBot(botName);
				if (bot != null) {
					if (!BotIdleModules.TryGetValue(bot, out IdleModule? module)) {
						module = new IdleModule(bot);
						BotIdleModules[bot] = module;
					}
					module.SetIdleGames(games);
				}
			}

			// Restore PlayFor sessions
			foreach (
				(string botName, List<PlayForEntry> entries) in Config?.PlayForGames
					?? []
			) {
				Bot? bot = Bot.GetBot(botName);
				if (bot != null) {
					foreach (PlayForEntry entry in entries) {
						// Update remaining time based on time passed since last update
						TimeSpan timePassed = DateTime.UtcNow - entry.LastUpdate;
						foreach ((uint gameId, uint remaining) in entry.RemainingMinutes.ToList()) {
							uint minutesPassed = (uint) Math.Min(remaining, timePassed.TotalMinutes);
							entry.RemainingMinutes[gameId] = remaining - minutesPassed;
						}

						// Remove completed games
						entry.RemainingMinutes = entry
							.RemainingMinutes.Where(x => x.Value > 0)
							.ToDictionary(x => x.Key, x => x.Value);

						if (entry.RemainingMinutes.Count > 0) {
							await ScheduleGames(bot, entry).ConfigureAwait(false);
						}
					}
				}
			}
		} finally {
			_ = ConfigLock.Release();
		}
	}

	internal static async Task SaveConfig() => await SaveConfigImpl().ConfigureAwait(false);

	private static async Task SaveConfigImpl() {
		await ConfigLock.WaitAsync().ConfigureAwait(false);

		try {
			if (Config == null) {
				return;
			}

			// Update idle games
			Config.IdleGames.Clear();
			foreach ((Bot bot, IdleModule module) in BotIdleModules) {
				Config.IdleGames[bot.BotName] = module.GetIdleGames();
			}

			// Update PlayFor entries timestamps
			foreach (List<PlayForEntry> entries in Config.PlayForGames.Values) {
				foreach (PlayForEntry entry in entries) {
					entry.LastUpdate = DateTime.UtcNow;
				}
			}

			string json = Config.ToJsonText(true); // true for indented output
			await File.WriteAllTextAsync(ConfigFile, json).ConfigureAwait(false);
		} finally {
			_ = ConfigLock.Release();
		}
	}

	internal static async Task ScheduleGames(Bot bot, PlayForEntry entry) =>
		await ScheduleGamesImpl(bot, entry).ConfigureAwait(false);

	private static async Task ScheduleGamesImpl(Bot bot, PlayForEntry entry) {
		foreach ((uint gameId, uint remainingMinutes) in entry.RemainingMinutes) {
			Timer? playTimer = null;
			try {
				playTimer = new Timer(
					async state => {
						try {
							if (Instance != null) {
								if (state is Timer timer) {
									await timer.DisposeAsync().ConfigureAwait(false);
									_ = Instance.ActiveTimers.Remove(timer);
								}

								await HandleGameCompletion(bot, gameId).ConfigureAwait(false);
							}
						} catch (Exception ex) {
							ASF.ArchiLogger.LogGenericError($"Error in timer callback: {ex}");
						}
					},
					null,
					TimeSpan.FromMinutes(remainingMinutes),
					Timeout.InfiniteTimeSpan
				);

				Instance?.ActiveTimers.Add(playTimer);
				playTimer = null; // Ownership transferred to ActiveTimers
			} finally {
				if (playTimer != null) {
					await playTimer.DisposeAsync().ConfigureAwait(false);
				}
			}
		}

		// Start first game if not farming
		if (bot.CardsFarmer.CurrentGamesFarmingReadOnly.Count == 0) {
			_ = await bot
				.Actions.Play([entry.RemainingMinutes.First().Key])
				.ConfigureAwait(false);
		}
	}

	private static async Task HandleGameCompletion(Bot bot, uint gameId) {
		await ConfigLock.WaitAsync().ConfigureAwait(false);

		try {
			if (Config?.PlayForGames.TryGetValue(bot.BotName, out List<PlayForEntry>? entries) == true) {
				foreach (PlayForEntry? entry in entries.ToList()) {
					if (entry.RemainingMinutes.Remove(gameId)) {
						// Check if this game was marked for idling after completion
						if (entry.IdleAfterCompletion.Contains(gameId)) {
							if (!BotIdleModules.TryGetValue(bot, out IdleModule? module)) {
								module = new IdleModule(bot);
								BotIdleModules[bot] = module;
							}
							module.SetIdleGames([gameId]);
							ASF.ArchiLogger.LogGenericInfo($"Game {gameId} completed PlayFor duration, now idling");
						}
					}

					if (entry.RemainingMinutes.Count == 0) {
						_ = entries.Remove(entry);
					}
				}

				if (entries.Count == 0) {
					_ = Config.PlayForGames.Remove(bot.BotName);
				}

				await SaveConfig().ConfigureAwait(false);
			}
		} finally {
			_ = ConfigLock.Release();
		}
	}

	public Task OnLoaded() {
		ASF.ArchiLogger.LogGenericInfo($"{Name} has been loaded!");
		return Task.CompletedTask;
	}

	public async ValueTask DisposeAsync() {
		await ConfigSaveTimer.DisposeAsync().ConfigureAwait(false);
		await SaveConfig().ConfigureAwait(false);

		foreach (Timer timer in ActiveTimers) {
			await timer.DisposeAsync().ConfigureAwait(false);
		}
		ActiveTimers.Clear();

		foreach (IdleModule module in BotIdleModules.Values) {
			module.StopIdling();
			module.Dispose();
		}
		BotIdleModules.Clear();

		Instance = null;
	}

	public async Task<string?> OnBotCommand(
		Bot bot,
		EAccess access,
		string message,
		string[] args,
		ulong steamID = 0
	) => args.Length == 0
			? null
			: args[0].ToUpperInvariant() switch {
				"PLAYFOR" => await Commands.PlayForCommand.Response(bot, args).ConfigureAwait(false),
				"IDLE" => await Commands.IdleCommand.Response(bot, args).ConfigureAwait(false),
				"RESUME" => await Commands.ResumeCommand.Response(bot, args).ConfigureAwait(false),
				_ => null,
			};

	public sealed class IdleModule : IDisposable {
		private readonly Bot Bot;
		public string Name => nameof(IdleModule);
		public Version Version =>
			typeof(ASFTimedPlay).Assembly.GetName().Version
			?? throw new InvalidOperationException(nameof(Version));
		private HashSet<uint> IdleGames = [];
		private readonly SemaphoreSlim IdleLock = new(1, 1);

		internal IdleModule(Bot bot) => Bot = bot ?? throw new ArgumentNullException(nameof(bot));

		public void SetIdleGames(HashSet<uint> games) {
			IdleGames = games;
			_ = TryIdleGames();
		}

		public void StopIdling() => IdleGames.Clear();

		private async Task TryIdleGames() {
			if (IdleGames.Count == 0) {
				return;
			}

			await IdleLock.WaitAsync().ConfigureAwait(false);

			try {
				// Only play games if bot isn't farming cards and has no active games
				if (
					Bot.CardsFarmer.CurrentGamesFarmingReadOnly.Count == 0
					&& Bot.GamesToRedeemInBackgroundCount == 0
					&& Bot.BotConfig.GamesPlayedWhileIdle.Count == 0
				) {
					_ = await Bot.Actions.Play(IdleGames).ConfigureAwait(false);
					ASF.ArchiLogger.LogGenericDebug(
						$"Resumed idling games {string.Join(",", IdleGames)} for {Bot.BotName}"
					);
				}
			} finally {
				_ = IdleLock.Release();
			}
		}

		public static Task<bool> OnGamesPrioritized() => Task.FromResult(true);

		public async Task OnGamesPrioritizedFinished() {
			// Check if we have any PlayFor games that need to be resumed
			if (
				Config?.PlayForGames.TryGetValue(Bot.BotName, out List<PlayForEntry>? entries) == true
				&& entries.Count > 0
				&& entries[0].RemainingMinutes.Count > 0
			) {
				_ = await Bot
					.Actions.Play([entries[0].RemainingMinutes.First().Key])
					.ConfigureAwait(false);
			} else {
				await TryIdleGames().ConfigureAwait(false);
			}
		}

		public HashSet<uint> GetIdleGames() => [.. IdleGames];

		public void Dispose() {
			IdleLock.Dispose();
			GC.SuppressFinalize(this);
		}
	}

	public async void Dispose() {
		foreach (Timer timer in ActiveTimers) {
			await timer.DisposeAsync().ConfigureAwait(false);
		}

		ActiveTimers.Clear();
		IdleGames.Clear();
		Instance = null;
	}
}
#pragma warning restore CA1812 // ASF uses this class during runtime
