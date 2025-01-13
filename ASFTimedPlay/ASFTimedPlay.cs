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
using SteamKit2;

namespace ASFTimedPlay;

#pragma warning disable CA1812 // ASF uses this class during runtime
[Export(typeof(IPlugin))]
[UsedImplicitly]
internal sealed class ASFTimedPlay : IGitHubPluginUpdates, IPlugin, IAsyncDisposable, IBotCommand2, IBot, IBotConnection {
	public string Name => nameof(ASFTimedPlay);
	public string RepositoryName => "UpDownLeftDie/ASFTimedPlay";
	public Version Version =>
		typeof(ASFTimedPlay).Assembly.GetName().Version
		?? throw new InvalidOperationException(nameof(Version));
	public static string UpdateChannel => "stable";

	private static ASFTimedPlay? Instance { get; set; }
	private readonly List<Timer> ActiveTimers = [];
	internal static readonly Dictionary<Bot, IdleModule> BotIdleModules = [];
	internal static TimedPlayConfig? Config { get; private set; }

	private const string ConfigFile = "ASFTimedPlay.json";
	internal static readonly string ConfigPath = Path.Combine(
		Path.GetDirectoryName(typeof(ASFTimedPlay).Assembly.Location) ?? "",
		ConfigFile
	);
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
			if (!File.Exists(ConfigPath)) {
				ASF.ArchiLogger.LogGenericDebug($"Config file not found at path: {ConfigPath}");
				ASF.ArchiLogger.LogGenericDebug("Creating new TimedPlayConfig object");

				Config = new TimedPlayConfig {
					PlayForGames = []
				};

				ASF.ArchiLogger.LogGenericDebug("About to save new config file");
				await SaveConfig().ConfigureAwait(false);
				ASF.ArchiLogger.LogGenericDebug($"Config properties - PlayForGames count: {Config.PlayForGames?.Count}");
			} else {
				ASF.ArchiLogger.LogGenericDebug("Reading existing config file");
				string json = await File.ReadAllTextAsync(ConfigPath).ConfigureAwait(false);
				Config = json.ToJsonObject<TimedPlayConfig>();

				if (Config == null) {
					throw new InvalidOperationException("Failed to deserialize config");
				}
			}

			ASF.ArchiLogger.LogGenericDebug("Config loaded successfully");
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericError($"Error loading config: {ex}");
			throw;
		} finally {
			_ = ConfigLock.Release();
			ASF.ArchiLogger.LogGenericDebug("LoadConfig completed");
		}
	}

	internal static async Task SaveConfig(bool lockAlreadyHeld = false) {
		ASF.ArchiLogger.LogGenericDebug("Entering SaveConfig");
		if (Config == null) {
			ASF.ArchiLogger.LogGenericError("Cannot save: Config is null");
			return;
		}

		bool lockTaken = false;
		try {
			if (!lockAlreadyHeld) {
				ASF.ArchiLogger.LogGenericDebug("Waiting for ConfigLock");
				using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
				await ConfigLock.WaitAsync(cts.Token).ConfigureAwait(false);
				lockTaken = true;
				ASF.ArchiLogger.LogGenericDebug("Acquired ConfigLock");
			}

			// Remove any empty entries
			foreach (string? botName in Config.PlayForGames.Keys.ToList()) {
				PlayForEntry entry = Config.PlayForGames[botName];
				if (entry.GameMinutes.Count == 0 && entry.IdleGameId == 0) {
					_ = Config.PlayForGames.Remove(botName);
				}
			}

			string json = Config.ToJsonText(true);
			ASF.ArchiLogger.LogGenericDebug($"Saving config: {json}");
			await File.WriteAllTextAsync(ConfigPath, json).ConfigureAwait(false);
			ASF.ArchiLogger.LogGenericDebug("File write completed successfully");
		} catch (OperationCanceledException) {
			ASF.ArchiLogger.LogGenericError("Timeout waiting for ConfigLock");
			throw new TimeoutException("Failed to acquire ConfigLock within 5 seconds");
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericError($"Error in SaveConfig: {ex.Message}\nStack trace: {ex.StackTrace}");
			throw;
		} finally {
			if (lockTaken) {
				_ = ConfigLock.Release();
				ASF.ArchiLogger.LogGenericDebug("Released ConfigLock");
			}
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

			// Get first game that still has time remaining
			KeyValuePair<uint, uint> firstGame = entry.GameMinutes
				.Where(x => x.Value > 0)
				.OrderBy(x => x.Key)
				.FirstOrDefault();

			if (firstGame.Key != 0) {
				// Set up timer for the game
				Timer? playTimer = null;
				try {
					ASF.ArchiLogger.LogGenericDebug($"Setting up timer for game {firstGame.Key} for {firstGame.Value} minutes");

					playTimer = new Timer(
						async _ => {
							try {
								ASF.ArchiLogger.LogGenericDebug($"Timer callback triggered for game {firstGame.Key}");
								if (Instance != null) {
									// Update remaining minutes in config
									await ConfigLock.WaitAsync().ConfigureAwait(false);
									try {
										PlayForEntry? currentEntry = null;
										if (Config?.PlayForGames.TryGetValue(bot.BotName, out currentEntry) == true) {
											if (currentEntry.GameMinutes.TryGetValue(firstGame.Key, out uint remainingMinutes) && remainingMinutes > 0) {
												currentEntry.GameMinutes[firstGame.Key] = remainingMinutes - 1;
												currentEntry.LastUpdate = DateTime.UtcNow;
												await SaveConfig(lockAlreadyHeld: true).ConfigureAwait(false);
											}
										}

										// Stop the game if no minutes remaining
										if (currentEntry?.GameMinutes.GetValueOrDefault(firstGame.Key) == 0) {
											(bool success, string message) = await bot.Actions.Play([]).ConfigureAwait(false);
											ASF.ArchiLogger.LogGenericDebug($"Stopped game {firstGame.Key}: {success}. {message}");

											await HandleGameCompletion(bot, firstGame.Key, lockAlreadyHeld: true).ConfigureAwait(false);

											// Find next game with remaining time
											KeyValuePair<uint, uint> nextGame = entry.GameMinutes
												.Where(x => x.Value > 0 && x.Key != firstGame.Key)
												.OrderBy(x => x.Key)
												.FirstOrDefault();

											if (nextGame.Key != 0) {
												(success, message) = await bot.Actions.Play([nextGame.Key]).ConfigureAwait(false);
												ASF.ArchiLogger.LogGenericDebug($"Started next game {nextGame.Key}: {success}. {message}");

												// Schedule the next game
												await ScheduleGamesImpl(bot, entry).ConfigureAwait(false);
											} else if (entry.IdleGameId > 0) {
												// If no next game but we have an idle game, start idling
												if (!BotIdleModules.TryGetValue(bot, out IdleModule? module)) {
													module = new IdleModule(bot);
													BotIdleModules[bot] = module;
												}
												module.SetIdleGames(entry.IdleGameId);
												ASF.ArchiLogger.LogGenericInfo($"No more games to play, starting to idle {entry.IdleGameId}");
											}
										}
									} finally {
										_ = ConfigLock.Release();
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
						null,
						TimeSpan.FromMinutes(1), // Change to trigger every minute
						TimeSpan.FromMinutes(1)  // Repeat every minute
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
			} else if (entry.IdleGameId > 0) {
				// If no games with remaining time, start idling immediately
				if (!BotIdleModules.TryGetValue(bot, out IdleModule? module)) {
					module = new IdleModule(bot);
					BotIdleModules[bot] = module;
				}
				module.SetIdleGames(entry.IdleGameId);
			}
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericError($"Error in ScheduleGamesImpl: {ex}");
			throw;
		}
	}

	private static async Task HandleGameCompletion(Bot bot, uint gameId, bool lockAlreadyHeld = false) {
		if (!lockAlreadyHeld) {
			await ConfigLock.WaitAsync().ConfigureAwait(false);
		}

		try {
			ASF.ArchiLogger.LogGenericDebug($"Handling completion for game {gameId} on bot {bot.BotName}");

			if (Config?.PlayForGames.TryGetValue(bot.BotName, out PlayForEntry? entry) == true) {
				if (entry.GameMinutes.Remove(gameId)) {
					ASF.ArchiLogger.LogGenericDebug($"Removed game {gameId} from remaining minutes");

					// Update LastUpdate time
					entry.LastUpdate = DateTime.UtcNow;

					// Only remove entry if there are no remaining minutes AND no idle game
					if (entry.GameMinutes.Count == 0) {
						if (entry.IdleGameId > 0) {
							// Start idling if specified
							if (!BotIdleModules.TryGetValue(bot, out IdleModule? module)) {
								module = new IdleModule(bot);
								BotIdleModules[bot] = module;
							}
							module.SetIdleGames(entry.IdleGameId);
							ASF.ArchiLogger.LogGenericInfo($"All games completed, now idling {entry.IdleGameId}");
						} else {
							ASF.ArchiLogger.LogGenericDebug($"No remaining games and no idle game, removing from PlayForGames");
							_ = Config.PlayForGames.Remove(bot.BotName);
						}
					}

					// Always save config when we modify it
					await SaveConfig(lockAlreadyHeld: true).ConfigureAwait(false);
				}
			}
		} finally {
			if (!lockAlreadyHeld) {
				_ = ConfigLock.Release();
			}
		}
	}

	public async Task OnLoaded() {
		ASF.ArchiLogger.LogGenericInfo($"{Name} has been loaded!");
		try {
			// Initialize config here instead of constructor
			Config ??= new TimedPlayConfig {
				PlayForGames = []
			};

			await LoadConfig().ConfigureAwait(false);

			ASF.ArchiLogger.LogGenericDebug("Plugin initialization completed");
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericError($"Error in OnLoaded: {ex}");
			throw;
		}
	}

	public async Task OnBotLoggedOn(Bot bot) {
		try {
			if (Config == null || !bot.IsConnectedAndLoggedOn) {
				return;
			}

			// Restore PlayFor sessions for this bot
			if (Config.PlayForGames.TryGetValue(bot.BotName, out PlayForEntry? entry)) {
				ASF.ArchiLogger.LogGenericInfo($"Restoring session for {bot.BotName}: {entry.GameMinutes.Count} games, IdleGameId: {entry.IdleGameId}");

				if (entry.GameMinutes.Count > 0) {
					// If there are remaining minutes, start playing
					ASF.ArchiLogger.LogGenericInfo($"Resuming games with remaining minutes: {string.Join(",", entry.GameMinutes.Keys)}");
					await ScheduleGames(bot, entry).ConfigureAwait(false);
				} else if (entry.IdleGameId > 0) {
					// If no remaining minutes but there's an idle game, start idling
					ASF.ArchiLogger.LogGenericInfo($"Starting idle game {entry.IdleGameId}");
					if (!BotIdleModules.TryGetValue(bot, out IdleModule? module)) {
						module = new IdleModule(bot);
						BotIdleModules[bot] = module;
					}
					module.SetIdleGames(entry.IdleGameId);
				}
			}
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericError($"Error in OnBotLoggedOn for {bot.BotName}: {ex}");
		}
	}

	public Task OnBotDestroy(Bot bot) {
		if (BotIdleModules.TryGetValue(bot, out IdleModule? module)) {
			module.StopIdling();
			module.Dispose();
			_ = BotIdleModules.Remove(bot);
		}

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
	) {
		if (args.Length == 0) {
			return null;
		}

		string command = args[0].ToUpperInvariant();
		ASF.ArchiLogger.LogGenericDebug($"OnBotCommand received: {command} with {args.Length} args");

		return command switch {
			"PLAYFOR" => await Commands.PlayForCommand.Response(bot, args).ConfigureAwait(false),
			"IDLE" => await Commands.IdleCommand.Response(bot, args).ConfigureAwait(false),
			_ => null,
		};
	}

	public sealed class IdleModule : IDisposable {
		private readonly Bot Bot;
		public string Name => nameof(IdleModule);
		public Version Version =>
			typeof(ASFTimedPlay).Assembly.GetName().Version
			?? throw new InvalidOperationException(nameof(Version));
		private HashSet<uint> IdleGameId = [];
		private readonly SemaphoreSlim IdleLock = new(1, 1);

		internal IdleModule(Bot bot) => Bot = bot ?? throw new ArgumentNullException(nameof(bot));

		public void SetIdleGames(uint gameId) {
			IdleGameId = [gameId];
			_ = TryIdleGames();
		}

		public void StopIdling() => IdleGameId = [];

		private async Task TryIdleGames() {
			if (IdleGameId.Count == 0) {
				return;
			}

			await IdleLock.WaitAsync().ConfigureAwait(false);

			try {
				// Don't interfere with ASF operations
				if (!Bot.IsConnectedAndLoggedOn ||
					Bot.BotConfig.GamesPlayedWhileIdle.Count > 0 ||
					Bot.CardsFarmer.CurrentGamesFarmingReadOnly.Count > 0 ||
					Bot.GamesToRedeemInBackgroundCount > 0) {
					return;
				}

				_ = await Bot.Actions.Play(IdleGameId).ConfigureAwait(false);
				ASF.ArchiLogger.LogGenericDebug(
					$"Resumed idling game {string.Join(",", IdleGameId)} for {Bot.BotName}"
				);
			} finally {
				_ = IdleLock.Release();
			}
		}

		public static Task<bool> OnGamesPrioritized() => Task.FromResult(true);

		public async Task OnGamesPrioritizedFinished() =>
			// When ASF is done with its operations, try to resume idling
			await TryIdleGames().ConfigureAwait(false);

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
		Instance = null;
	}

	public Task OnBotInit(Bot bot) => Task.CompletedTask;

	public Task OnBotDisconnected(Bot bot, EResult reason) => Task.CompletedTask;
}
#pragma warning restore CA1812 // ASF uses this class during runtime
