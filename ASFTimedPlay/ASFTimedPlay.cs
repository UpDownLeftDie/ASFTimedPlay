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

		// Only initialize the timer in constructor
		ConfigSaveTimer = new Timer(
			async _ => await SaveConfig().ConfigureAwait(false),
			null,
			TimeSpan.FromMinutes(5),
			TimeSpan.FromMinutes(5)
		);
	}

	private static async Task LoadConfig() {
		ASF.ArchiLogger.LogGenericDebug("Starting LoadConfig");
		await ConfigLock.WaitAsync().ConfigureAwait(false);

		try {
			if (!File.Exists(ConfigFile)) {
				ASF.ArchiLogger.LogGenericDebug("Config file not found, creating new one");
				Config = new TimedPlayConfig {
					Enabled = true,
					IdleGames = [],
					PlayForGames = []
				};
				await SaveConfig().ConfigureAwait(false);
				ASF.ArchiLogger.LogGenericDebug("New config file created and saved");
				return;
			}

			ASF.ArchiLogger.LogGenericDebug("Reading config file");
			string json = await File.ReadAllTextAsync(ConfigFile).ConfigureAwait(false);
			Config = json.ToJsonObject<TimedPlayConfig>();

			if (Config == null) {
				throw new InvalidOperationException("Failed to deserialize config");
			}

			// Restore idle games
			foreach ((string botName, HashSet<uint> games) in Config.IdleGames) {
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
			foreach ((string botName, List<PlayForEntry> entries) in Config.PlayForGames) {
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
						entry.RemainingMinutes = entry.RemainingMinutes
							.Where(x => x.Value > 0)
							.ToDictionary(x => x.Key, x => x.Value);

						if (entry.RemainingMinutes.Count > 0) {
							await ScheduleGames(bot, entry).ConfigureAwait(false);
						}
					}
				}
			}

			ASF.ArchiLogger.LogGenericDebug("Config loaded and restored successfully");
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericError($"Error loading config: {ex}");
			throw;
		} finally {
			_ = ConfigLock.Release();
			ASF.ArchiLogger.LogGenericDebug("LoadConfig completed");
		}
	}

	internal static async Task SaveConfig() => await SaveConfigImpl().ConfigureAwait(false);

	private static async Task SaveConfigImpl() {
		if (Config == null) {
			ASF.ArchiLogger.LogGenericError("Cannot save: Config is null");
			return;
		}

		if (!ConfigLock.Wait(0)) {
			ASF.ArchiLogger.LogGenericDebug("Config is being saved by another operation, skipping");
			return;
		}

		try {
			string configPath = Path.GetFullPath(ConfigFile);
			string json = Config.ToJsonText(true);
			await File.WriteAllTextAsync(configPath, json).ConfigureAwait(false);
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericError($"Error saving config: {ex}");
		} finally {
			_ = ConfigLock.Release();
		}
	}

	internal static async Task ScheduleGames(Bot bot, PlayForEntry entry) =>
		await ScheduleGamesImpl(bot, entry).ConfigureAwait(false);

	private static async Task ScheduleGamesImpl(Bot bot, PlayForEntry entry) {
		try {
			ASF.ArchiLogger.LogGenericDebug($"ScheduleGamesImpl started for bot {bot.BotName}");

			// First stop any currently running games
			ASF.ArchiLogger.LogGenericDebug("Stopping current games");
			(bool stopSuccess, string stopMessage) = await bot.Actions.Play([]).ConfigureAwait(false);
			ASF.ArchiLogger.LogGenericDebug($"Stop result: {stopSuccess}. {stopMessage}");

			// Get first non-idle game
			KeyValuePair<uint, uint> firstGame = entry.RemainingMinutes.FirstOrDefault(x => !entry.IdleAfterCompletion.Contains(x.Key));

			if (firstGame.Key != 0) {
				// Set up timer for the first non-idle game
				Timer? playTimer = null;
				try {
					ASF.ArchiLogger.LogGenericDebug($"Setting up timer for game {firstGame.Key} for {firstGame.Value} minutes");

					playTimer = new Timer(
						async _ => {
							try {
								ASF.ArchiLogger.LogGenericDebug($"Timer callback triggered for game {firstGame.Key}");
								if (Instance != null) {
									// Stop the game immediately when timer triggers
									(bool success, string message) = await bot.Actions.Play([]).ConfigureAwait(false);
									ASF.ArchiLogger.LogGenericDebug($"Stopped game {firstGame.Key}: {success}. {message}");

									await HandleGameCompletion(bot, firstGame.Key).ConfigureAwait(false);

									// Find next non-idle game if any
									KeyValuePair<uint, uint> nextGame = entry.RemainingMinutes
										.FirstOrDefault(x => !entry.IdleAfterCompletion.Contains(x.Key) && x.Key != firstGame.Key);

									if (nextGame.Key != 0) {
										(success, message) = await bot.Actions.Play([nextGame.Key]).ConfigureAwait(false);
										ASF.ArchiLogger.LogGenericDebug($"Started next game {nextGame.Key}: {success}. {message}");
									} else if (entry.IdleAfterCompletion.Count > 0) {
										// Start idling the marked game
										uint idleGame = entry.IdleAfterCompletion.First();
										if (!BotIdleModules.TryGetValue(bot, out IdleModule? module)) {
											module = new IdleModule(bot);
											BotIdleModules[bot] = module;
										}
										module.SetIdleGames([idleGame]);
									}
								}
							} catch (Exception ex) {
								ASF.ArchiLogger.LogGenericError($"Error in timer callback for game {firstGame.Key}: {ex}");
							} finally {
								if (playTimer != null) {
									await playTimer.DisposeAsync().ConfigureAwait(false);
									if (Instance != null) {
										_ = Instance.ActiveTimers.Remove(playTimer);
									}
								}
							}
						},
						playTimer,
						TimeSpan.FromMinutes(firstGame.Value),
						Timeout.InfiniteTimeSpan
					);

					if (Instance != null) {
						Instance.ActiveTimers.Add(playTimer);
						ASF.ArchiLogger.LogGenericDebug($"Timer added to active timers for game {firstGame.Key}");
						playTimer = null; // Transfer ownership to ActiveTimers
					}

					// Start first game
					ASF.ArchiLogger.LogGenericDebug($"Starting first game {firstGame.Key}");
					(bool success, string message) = await bot.Actions.Play([firstGame.Key]).ConfigureAwait(false);
					ASF.ArchiLogger.LogGenericDebug($"Game start result: {success}. {message}");

					if (!success) {
						ASF.ArchiLogger.LogGenericWarning($"Failed to start game {firstGame.Key}: {message}");
						throw new InvalidOperationException($"Failed to start game: {message}");
					}
				} finally {
					if (playTimer != null) {
						await playTimer.DisposeAsync().ConfigureAwait(false);
					}
				}
			} else if (entry.IdleAfterCompletion.Count > 0) {
				// If no timed games, start idling immediately
				uint idleGame = entry.IdleAfterCompletion.First();
				if (!BotIdleModules.TryGetValue(bot, out IdleModule? module)) {
					module = new IdleModule(bot);
					BotIdleModules[bot] = module;
				}
				module.SetIdleGames([idleGame]);
			}
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericError($"Error in ScheduleGamesImpl: {ex}");
			throw;
		}
	}

	private static async Task HandleGameCompletion(Bot bot, uint gameId) {
		await ConfigLock.WaitAsync().ConfigureAwait(false);

		try {
			ASF.ArchiLogger.LogGenericDebug($"Handling completion for game {gameId} on bot {bot.BotName}");

			if (Config?.PlayForGames.TryGetValue(bot.BotName, out List<PlayForEntry>? entries) == true) {
				foreach (PlayForEntry? entry in entries.ToList()) {
					if (entry.RemainingMinutes.Remove(gameId)) {
						ASF.ArchiLogger.LogGenericDebug($"Removed game {gameId} from remaining minutes");

						// Update LastUpdate time
						entry.LastUpdate = DateTime.UtcNow;

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
						ASF.ArchiLogger.LogGenericDebug($"No remaining games for entry, removing from list");
						_ = entries.Remove(entry);
					}
				}

				if (entries.Count == 0) {
					ASF.ArchiLogger.LogGenericDebug($"No remaining entries for bot {bot.BotName}, removing from PlayForGames");
					_ = Config.PlayForGames.Remove(bot.BotName);
				}

				await SaveConfig().ConfigureAwait(false);
			}
		} finally {
			_ = ConfigLock.Release();
		}
	}

	public async Task OnLoaded() {
		try {
			ASF.ArchiLogger.LogGenericInfo($"{Name} has been loaded!");

			// Initialize config here instead of constructor
			Config ??= new TimedPlayConfig {
				Enabled = true,
				IdleGames = [],
				PlayForGames = []
			};

			await LoadConfig().ConfigureAwait(false);
			ASF.ArchiLogger.LogGenericDebug("Plugin initialization completed");
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericError($"Error in OnLoaded: {ex}");
			throw;
		}
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
				"PLAYFOR" => await Commands.PlayForCommand.Response(bot, args, access, steamID).ConfigureAwait(false),
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
