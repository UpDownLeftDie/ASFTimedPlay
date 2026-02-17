using System;
using System.Collections.Generic;
using System.Composition;
using System.IO;
using System.Linq;
using System.Threading; // Add for Timer
using System.Threading.Tasks;
using ArchiSteamFarm.Helpers.Json;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using JetBrains.Annotations;
using SteamKit2;
using static ASFTimedPlay.Utils;

namespace ASFTimedPlay;

#pragma warning disable CA1812 // ASF uses this class during runtime
[Export(typeof(IPlugin))]
[UsedImplicitly]
internal sealed class ASFTimedPlay
	: IGitHubPluginUpdates,
		IPlugin,
		IAsyncDisposable,
		IBotCommand2,
		IBot,
		IBotConnection {
	public string Name => nameof(ASFTimedPlay);
	public string RepositoryName => "UpDownLeftDie/ASFTimedPlay";
	public Version Version =>
		typeof(ASFTimedPlay).Assembly.GetName().Version
		?? throw new InvalidOperationException(nameof(Version));
	public static string UpdateChannel => "stable";

	internal static ASFTimedPlay? Instance { get; set; }
	internal readonly Dictionary<Bot, Timer> ActiveTimers = [];
	internal static readonly Dictionary<Bot, IdleModule> BotIdleModules = [];
	public static TimedPlayConfig? Config { get; private set; }

	// Wall-clock timing: duration for current game and total paused time (so long runs are exact)
	internal readonly Dictionary<Bot, DateTime> TimerStartTimes = [];
	internal readonly Dictionary<Bot, uint> GameDurationMinutes = [];
	internal readonly Dictionary<Bot, TimeSpan> TotalPausedDuration = [];

	private const string ConfigFile = "ASFTimedPlay.json";
	internal static readonly string ConfigPath = Path.Combine(
		Path.GetDirectoryName(typeof(ASFTimedPlay).Assembly.Location) ?? "",
		ConfigFile
	);
	internal static readonly SemaphoreSlim ConfigLock = new(1, 1);

	internal readonly Dictionary<Bot, DateTime> TimerPausedAt = [];

	private const int IdleResumeIntervalMinutes = 2;
	private Timer? IdleResumeTimer;

	public ASFTimedPlay() => Instance = this;

	private static async Task LoadConfig() {
		LogGenericDebug("Starting LoadConfig");

		if (!File.Exists(ConfigPath)) {
			LogGenericDebug($"Config file not found at path: {ConfigPath}");
			LogGenericDebug("Creating new TimedPlayConfig object");

			Config = new TimedPlayConfig { TimedPlayGames = [] };

			LogGenericDebug("About to save new config file");
			// Don't need lock for initial file creation
			string json = Config.ToJsonText(true);
			await File.WriteAllTextAsync(ConfigPath, json).ConfigureAwait(false);
			LogGenericDebug($"Config properties - TimedPlayGames count: {Config.TimedPlayGames?.Count}");
			return;
		}

		await ConfigLock.WaitAsync().ConfigureAwait(false);
		try {
			LogGenericDebug("Reading existing config file");
			string json = await File.ReadAllTextAsync(ConfigPath).ConfigureAwait(false);
			Config = json.ToJsonObject<TimedPlayConfig>();

			if (Config == null) {
				throw new InvalidOperationException("Failed to deserialize config");
			}

			// Ensure required collections exist (handles empty {} or old config format)
			Config.TimedPlayGames ??= [];
			foreach (TimedPlayEntry entry in Config.TimedPlayGames.Values) {
				entry.GameMinutes ??= [];
				entry.IdleGameIds ??= [];
			}

			LogGenericDebug("Config loaded successfully");
		} catch (Exception ex) {
			LogGenericError($"Error loading config: {ex}");
			throw;
		} finally {
			_ = ConfigLock.Release();
			LogGenericDebug("LoadConfig completed");
		}
	}

	internal static async Task SaveConfig(bool lockAlreadyHeld = false) {
		LogGenericDebug("Entering SaveConfig");
		if (Config == null) {
			LogGenericError("Cannot save: Config is null");
			return;
		}

		// Compare with existing file content
		string newJson = Config.ToJsonText(true);
		if (File.Exists(ConfigPath)) {
			string existingJson = await File.ReadAllTextAsync(ConfigPath).ConfigureAwait(false);
			if (existingJson == newJson) {
				LogGenericDebug("Skipping save: No changes detected");
				return;
			}
		}

		bool lockTaken = false;
		try {
			if (!lockAlreadyHeld) {
				LogGenericDebug("Waiting for ConfigLock");
				using CancellationTokenSource cts = new(TimeSpan.FromSeconds(5));
				await ConfigLock.WaitAsync(cts.Token).ConfigureAwait(false);
				lockTaken = true;
				LogGenericDebug("Acquired ConfigLock");
			}

			// Remove any empty entries
			foreach (string? botName in Config.TimedPlayGames.Keys.ToList()) {
				TimedPlayEntry entry = Config.TimedPlayGames[botName];
				if (entry.GameMinutes.Count == 0 && entry.IdleGameIds.Count == 0) {
					_ = Config.TimedPlayGames.Remove(botName);
				}
			}

			string json = Config.ToJsonText(true);
			LogGenericDebug($"Saving config: {json}");
			await File.WriteAllTextAsync(ConfigPath, json).ConfigureAwait(false);
			LogGenericDebug("File write completed successfully");
		} catch (OperationCanceledException) {
			LogGenericError("Timeout waiting for ConfigLock");
			throw new TimeoutException("Failed to acquire ConfigLock within 5 seconds");
		} catch (Exception ex) {
			LogGenericError($"Error in SaveConfig: {ex.Message}\nStack trace: {ex.StackTrace}");
			throw;
		} finally {
			if (lockTaken) {
				_ = ConfigLock.Release();
				LogGenericDebug("Released ConfigLock");
			}
		}
	}

	internal static async Task ScheduleGames(Bot bot, TimedPlayEntry entry) =>
		await ScheduleGamesImpl(bot, entry).ConfigureAwait(false);

	private static void ValidateTimerAccuracy(Bot bot, uint gameId) {
		if (Instance?.TimerStartTimes.TryGetValue(bot, out DateTime startTime) == true) {
			TimeSpan elapsed = DateTime.UtcNow - startTime;
			TimeSpan expectedMinutes = TimeSpan.FromMinutes(elapsed.TotalMinutes);
			TimeSpan actualMinutes = elapsed;

			// Log if there's significant drift (more than 30 seconds)
			if (Math.Abs((actualMinutes - expectedMinutes).TotalSeconds) > 30) {
				LogGenericWarning($"Timer drift detected for {bot.BotName} game {gameId}: Expected {expectedMinutes.TotalMinutes:F1} minutes, actual {actualMinutes.TotalMinutes:F1} minutes");
			}
		}
	}

	private static async Task ScheduleGamesImpl(Bot bot, TimedPlayEntry entry) {
		try {
			LogGenericDebug($"ScheduleGamesImpl started for bot {bot.BotName}");

			// First stop any currently running games
			LogGenericDebug("Stopping current games");
			(bool stopSuccess, string stopMessage) = await bot
				.Actions.Play([])
				.ConfigureAwait(false);
			LogGenericDebug($"Stop result: {stopSuccess}. {stopMessage}");

			// Dispose any existing timers for this bot
			if (Instance != null) {
				if (Instance.ActiveTimers.TryGetValue(bot, out Timer? timers)) {
					await timers.DisposeAsync().ConfigureAwait(false);
				}
			}

			// Get first game that still has time remaining
			KeyValuePair<uint, uint> firstGame = entry
				.GameMinutes.Where(x => x.Value > 0)
				.FirstOrDefault();

			if (firstGame.Key != 0) {
				// Set up timer for the game
				Timer? playTimer = null;
				try {
					LogGenericDebug(
						$"Setting up timer for game {firstGame.Key} for {firstGame.Value} minutes"
					);

					uint currentGameId = firstGame.Key;
					if (Instance != null) {
						Instance.GameDurationMinutes[bot] = firstGame.Value;
						Instance.TotalPausedDuration[bot] = TimeSpan.Zero;
					}

					// First tick about 1 minute from now, then every minute
					DateTime nextMinute = DateTime.UtcNow.AddMinutes(1);
					TimeSpan dueTime = nextMinute - DateTime.UtcNow;

					playTimer = new Timer(
						async _ => {
							try {
								LogGenericDebug($"Timer callback triggered for game {currentGameId}");
								if (Instance == null) {
									return;
								}

								// Compute wall-clock remaining (even when paused) so we can complete when time is up
								uint remaining = 0;
								DateTime startTime = default;
								uint durationMinutes = 0;
								bool canComputeRemaining = Instance.TimerStartTimes.TryGetValue(bot, out startTime) &&
									Instance.GameDurationMinutes.TryGetValue(bot, out durationMinutes);
								if (canComputeRemaining) {
									double elapsedMinutes = (DateTime.UtcNow - startTime).TotalMinutes;
									double pausedMinutes = Instance.TotalPausedDuration.TryGetValue(bot, out TimeSpan paused) ? paused.TotalMinutes : 0;
									double effectiveElapsed = Math.Max(0, elapsedMinutes - pausedMinutes);
									remaining = (uint)Math.Max(0, (int)durationMinutes - (int)Math.Floor(effectiveElapsed));
								}

								// Time's up: run completion even if bot is paused (e.g. user on another device)
								if (canComputeRemaining && remaining == 0) {
									await ConfigLock.WaitAsync().ConfigureAwait(false);
									try {
										if (Config?.TimedPlayGames.TryGetValue(bot.BotName, out TimedPlayEntry? currentEntry) == true) {
											_ = Instance.TotalPausedDuration.Remove(bot);
											_ = Instance.GameDurationMinutes.Remove(bot);
											_ = Instance.TimerPausedAt.Remove(bot);
											(bool success, string message) = await bot.Actions.Play([]).ConfigureAwait(false);
											LogGenericDebug($"Time's up, stopped game {currentGameId}: {success}. {message}");

											await HandleGameCompletion(bot, currentGameId, lockAlreadyHeld: true).ConfigureAwait(false);

											// Next game, idle, or done – and stop this timer
											if (Instance.ActiveTimers.TryGetValue(bot, out Timer? oldTimer)) {
												await oldTimer.DisposeAsync().ConfigureAwait(false);
												_ = Instance.ActiveTimers.Remove(bot);
											}
											_ = Instance.TimerStartTimes.Remove(bot);

											if (Config.TimedPlayGames.TryGetValue(bot.BotName, out TimedPlayEntry? entryAfter)) {
												KeyValuePair<uint, uint> nextGame = entryAfter.GameMinutes.Where(x => x.Value > 0).FirstOrDefault();
												if (nextGame.Key != 0) {
													(success, message) = await bot.Actions.Play([nextGame.Key]).ConfigureAwait(false);
													await ScheduleGamesImpl(bot, entryAfter).ConfigureAwait(false);
												} else if (entryAfter.IdleGameIds.Count > 0) {
													if (!BotIdleModules.TryGetValue(bot, out IdleModule? module)) {
														module = new IdleModule(bot);
														BotIdleModules[bot] = module;
													}
													module.SetIdleGames(entryAfter.IdleGameIds);
													LogGenericInfo($"No more games to play, starting to idle {string.Join(",", entryAfter.IdleGameIds)}");
												}
											}
										}
									} finally {
										_ = ConfigLock.Release();
									}
									return;
								}

								// Bot not playing this game: treat as paused (don't count time)
								bool isPlayingGame = bot.IsConnectedAndLoggedOn &&
									bot.CardsFarmer.CurrentGamesFarmingReadOnly.Any(game => game.AppID == currentGameId);
								if (!isPlayingGame) {
									if (Instance.TimerPausedAt.TryGetValue(bot, out DateTime pausedAt)) {
										Instance.TotalPausedDuration[bot] += DateTime.UtcNow - pausedAt;
										Instance.TimerPausedAt[bot] = DateTime.UtcNow;
									} else {
										Instance.TimerPausedAt[bot] = DateTime.UtcNow;
										LogGenericDebug($"Timer paused for bot {bot.BotName} at {DateTime.UtcNow}");
									}
									(bool resumeSuccess, string resumeMsg) = await bot.Actions.Play([currentGameId]).ConfigureAwait(false);
									if (resumeSuccess) {
										_ = Instance.TimerPausedAt.Remove(bot);
										LogGenericDebug($"Resumed playing {currentGameId} for bot {bot.BotName}: {resumeMsg}");
									}
									return;
								}

								// Bot is playing: update config with remaining and handle completion if now 0
								await ConfigLock.WaitAsync().ConfigureAwait(false);
								try {
									if (Config?.TimedPlayGames.TryGetValue(bot.BotName, out TimedPlayEntry? currentEntry) == true) {
										currentEntry.GameMinutes[currentGameId] = remaining;
										currentEntry.LastUpdate = DateTime.UtcNow;
										LogGenericDebug($"Updated game {currentGameId} for {bot.BotName}: {remaining} minutes remaining (wall-clock)");
										await SaveConfig(lockAlreadyHeld: true).ConfigureAwait(false);

										if (remaining == 0) {
											_ = Instance.TotalPausedDuration.Remove(bot);
											_ = Instance.GameDurationMinutes.Remove(bot);
											(bool success, string message) = await bot.Actions.Play([]).ConfigureAwait(false);
											LogGenericDebug($"Stopped game {currentGameId}: {success}. {message}");

											await HandleGameCompletion(bot, currentGameId, lockAlreadyHeld: true).ConfigureAwait(false);

											if (Instance.ActiveTimers.TryGetValue(bot, out Timer? oldTimer)) {
												await oldTimer.DisposeAsync().ConfigureAwait(false);
												_ = Instance.ActiveTimers.Remove(bot);
											}
											_ = Instance.TimerStartTimes.Remove(bot);

											KeyValuePair<uint, uint> nextGame = currentEntry.GameMinutes.Where(x => x.Value > 0).FirstOrDefault();
											if (nextGame.Key != 0) {
												(success, message) = await bot.Actions.Play([nextGame.Key]).ConfigureAwait(false);
												await ScheduleGamesImpl(bot, currentEntry).ConfigureAwait(false);
											} else if (currentEntry.IdleGameIds.Count > 0) {
												if (!BotIdleModules.TryGetValue(bot, out IdleModule? module)) {
													module = new IdleModule(bot);
													BotIdleModules[bot] = module;
												}
												module.SetIdleGames(currentEntry.IdleGameIds);
												LogGenericInfo($"No more games to play, starting to idle {string.Join(",", currentEntry.IdleGameIds)}");
											}
										}
									}
								} finally {
									_ = ConfigLock.Release();
								}
							} catch (Exception ex) {
								LogGenericError(
									$"Error in timer callback for game {currentGameId}: {ex}"
								);
							}
						},
						null,
						dueTime, // First trigger exactly one minute from now
						TimeSpan.FromMinutes(1) // Subsequent triggers every minute
					);

					if (Instance != null) {
						Instance.ActiveTimers[bot] = playTimer;
						Instance.TimerStartTimes[bot] = DateTime.UtcNow;
						LogGenericDebug(
							$"Timer added to active timers for game {currentGameId} on bot {bot.BotName}"
						);
						playTimer = null; // Transfer ownership to ActiveTimers
					}

					// Start first game
					LogGenericDebug($"Starting first game {currentGameId}");
					(bool success, string message) = await bot
						.Actions.Play([currentGameId])
						.ConfigureAwait(false);
					LogGenericDebug($"Game start result: {success}. {message}");

					if (!success) {
						LogGenericWarning($"Failed to start game {currentGameId}: {message}");
						throw new InvalidOperationException($"Failed to start game: {message}");
					}
				} finally {
					if (playTimer != null) {
						await playTimer.DisposeAsync().ConfigureAwait(false);
					}
				}
			} else if (entry.IdleGameIds.Count > 0) {
				// If no games with remaining time, start idling immediately
				if (!BotIdleModules.TryGetValue(bot, out IdleModule? module)) {
					module = new IdleModule(bot);
					BotIdleModules[bot] = module;
				}
				module.SetIdleGames(entry.IdleGameIds);
			}
		} catch (Exception ex) {
			LogGenericError($"Error in ScheduleGamesImpl: {ex}");
			throw;
		}
	}

	internal static void ApplyWallClockCorrectionToEntry(TimedPlayEntry entry) {
		if (entry.GameMinutes.Count == 0) {
			return;
		}

		double elapsedMinutes = (DateTime.UtcNow - entry.LastUpdate).TotalMinutes;
		if (elapsedMinutes <= 0) {
			return;
		}

		List<uint> toRemove = [];
		foreach (KeyValuePair<uint, uint> kvp in entry.GameMinutes) {
			uint remaining = (uint)Math.Max(0, (int)kvp.Value - (int)Math.Floor(elapsedMinutes));
			if (remaining == 0) {
				toRemove.Add(kvp.Key);
			} else {
				entry.GameMinutes[kvp.Key] = remaining;
			}
		}

		foreach (uint gameId in toRemove) {
			entry.GameMinutes.Remove(gameId);
		}

		entry.LastUpdate = DateTime.UtcNow;
		if (toRemove.Count > 0) {
			LogGenericDebug($"Wall-clock correction: removed {toRemove.Count} completed game(s), elapsed {elapsedMinutes:F0} min");
		}
	}

	private static async Task HandleGameCompletion(
		Bot bot,
		uint gameId,
		bool lockAlreadyHeld = false
	) {
		if (!lockAlreadyHeld) {
			await ConfigLock.WaitAsync().ConfigureAwait(false);
		}

		try {
			LogGenericDebug($"Handling completion for game {gameId} on bot {bot.BotName}");

			if (Config?.TimedPlayGames.TryGetValue(bot.BotName, out TimedPlayEntry? entry) == true) {
				if (entry.GameMinutes.Remove(gameId)) {
					LogGenericDebug($"Removed game {gameId} from remaining minutes");

					// Update LastUpdate time
					entry.LastUpdate = DateTime.UtcNow;

					// Only remove entry if there are no remaining minutes AND no idle games
					if (entry.GameMinutes.Count == 0) {
						if (entry.IdleGameIds.Count > 0) {
							// Start idling if specified
							if (!BotIdleModules.TryGetValue(bot, out IdleModule? module)) {
								module = new IdleModule(bot);
								BotIdleModules[bot] = module;
							}
							module.SetIdleGames(entry.IdleGameIds);
							LogGenericInfo($"All games completed, now idling {string.Join(",", entry.IdleGameIds)}");
						} else {
							LogGenericDebug(
								$"No remaining games and no idle games, removing from TimedPlayGames"
							);
							_ = Config.TimedPlayGames.Remove(bot.BotName);
							// Return to normal ASF operation
							(bool success, string message) = bot.Actions.Resume();
							LogGenericInfo($"Timed play finished for {bot.BotName}: {message}");
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
		LogGenericInfo($"{Name} has been loaded!");
		try {
			// Initialize config here instead of constructor
			Config ??= new TimedPlayConfig { TimedPlayGames = [] };

			await LoadConfig().ConfigureAwait(false);

			// Every 2 min try to resume idling (reclaim after farming or use elsewhere). IdleModule skips Play when account is in use elsewhere (IsPlayingPossible).
			IdleResumeTimer = new Timer(
				_ => {
					try {
						if (Instance == null || Config?.TimedPlayGames == null) {
							return;
						}
						foreach (KeyValuePair<string, TimedPlayEntry> kvp in Config.TimedPlayGames) {
							if (kvp.Value.IdleGameIds.Count == 0) {
								continue;
							}
							Bot? targetBot = Bot.GetBot(kvp.Key);
							if (targetBot == null || !targetBot.IsConnectedAndLoggedOn) {
								continue;
							}
							if (!BotIdleModules.TryGetValue(targetBot, out IdleModule? module)) {
								module = new IdleModule(targetBot);
								BotIdleModules[targetBot] = module;
							}
							module.SetIdleGames(kvp.Value.IdleGameIds);
						}
					} catch (Exception ex) {
						LogGenericError($"Idle resume timer error: {ex}");
					}
				},
				null,
				TimeSpan.FromMinutes(IdleResumeIntervalMinutes),
				TimeSpan.FromMinutes(IdleResumeIntervalMinutes)
			);

			LogGenericDebug("Plugin initialization completed");
		} catch (Exception ex) {
			LogGenericError($"Error in OnLoaded: {ex}");
			throw;
		}
	}

	public async Task OnBotLoggedOn(Bot bot) {
		try {
			if (Config == null || !bot.IsConnectedAndLoggedOn) {
				return;
			}

			// Restore timed play sessions for this bot
			if (Config.TimedPlayGames.TryGetValue(bot.BotName, out TimedPlayEntry? entry)) {
				LogGenericInfo(
					$"Restoring session for {bot.BotName}: {entry.GameMinutes.Count} games, IdleGameIds: {string.Join(",", entry.IdleGameIds)}"
				);

				if (entry.GameMinutes.Count > 0) {
					// Apply wall-clock correction so remaining time reflects elapsed time since last save (e.g. across restarts)
					await ConfigLock.WaitAsync().ConfigureAwait(false);
					try {
						ApplyWallClockCorrectionToEntry(entry);
						await SaveConfig(lockAlreadyHeld: true).ConfigureAwait(false);
					} finally {
						_ = ConfigLock.Release();
					}

					// If all games were exhausted by the correction, may have nothing left to play
					if (entry.GameMinutes.Count == 0) {
						if (entry.IdleGameIds.Count > 0) {
							LogGenericInfo($"Starting idle games {string.Join(",", entry.IdleGameIds)}");
							if (!BotIdleModules.TryGetValue(bot, out IdleModule? module)) {
								module = new IdleModule(bot);
								BotIdleModules[bot] = module;
							}
							module.SetIdleGames(entry.IdleGameIds);
						}
						return;
					}

					LogGenericInfo(
						$"Resuming games with remaining minutes: {string.Join(",", entry.GameMinutes.Keys)}"
					);
					await ScheduleGames(bot, entry).ConfigureAwait(false);
				} else if (entry.IdleGameIds.Count > 0) {
					// If no remaining minutes but there are idle games, start idling
					LogGenericInfo($"Starting idle games {string.Join(",", entry.IdleGameIds)}");
					if (!BotIdleModules.TryGetValue(bot, out IdleModule? module)) {
						module = new IdleModule(bot);
						BotIdleModules[bot] = module;
					}
					module.SetIdleGames(entry.IdleGameIds);
				}
			}
		} catch (Exception ex) {
			LogGenericError($"Error in OnBotLoggedOn for {bot.BotName}: {ex}");
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
		await SaveConfig().ConfigureAwait(false);

		if (IdleResumeTimer != null) {
			await IdleResumeTimer.DisposeAsync().ConfigureAwait(false);
			IdleResumeTimer = null;
		}

		foreach (Timer timer in ActiveTimers.Values) {
			await timer.DisposeAsync().ConfigureAwait(false);
		}

		ActiveTimers.Clear();
		TimerStartTimes.Clear();
		GameDurationMinutes.Clear();
		TotalPausedDuration.Clear();
		TimerPausedAt.Clear();

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
		LogGenericDebug($"OnBotCommand received: {command} with {args.Length} args");

		return command switch {
			"TIMEDPLAY" => await Commands.TimedPlayCommand.Response(bot, args).ConfigureAwait(false),
			"IDLE" => await Commands.IdleCommand.Response(bot, args).ConfigureAwait(false),
			// Aliases: tp (short), pf (legacy)
			"TP" => await Commands.TimedPlayCommand.Response(bot, args).ConfigureAwait(false),
			"PF" => await Commands.TimedPlayCommand.Response(bot, args).ConfigureAwait(false),
			"I" => await Commands.IdleCommand.Response(bot, args).ConfigureAwait(false),
			_ => null,
		};
	}

	public Task OnBotInit(Bot bot) => Task.CompletedTask;

	public Task OnBotDisconnected(Bot bot, EResult reason) => Task.CompletedTask;
}
#pragma warning restore CA1812 // ASF uses this class during runtime
