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

	// Wall-clock timing: per-game start/duration for concurrent play; total paused time (so long runs are exact)
	internal readonly Dictionary<Bot, Dictionary<uint, DateTime>> GameStartTimes = [];
	internal readonly Dictionary<Bot, Dictionary<uint, uint>> GameDurationMinutes = [];
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

	private const int MaxConcurrentGames = 32;

	private static async Task ScheduleGamesImpl(Bot bot, TimedPlayEntry entry) {
		try {
			LogGenericDebug($"ScheduleGamesImpl started for bot {bot.BotName}");

			// Stop any currently running games
			(bool stopSuccess, string stopMessage) = await bot.Actions.Play([]).ConfigureAwait(false);
			LogGenericDebug($"Stop result: {stopSuccess}. {stopMessage}");

			// Dispose any existing timer for this bot
			if (Instance != null && Instance.ActiveTimers.TryGetValue(bot, out Timer? existingTimer)) {
				await existingTimer.DisposeAsync().ConfigureAwait(false);
				_ = Instance.ActiveTimers.Remove(bot);
			}

			// Build set of games to play (concurrent up to 32, or single game when SequentialMode)
			int take = entry.SequentialMode ? 1 : MaxConcurrentGames;
			List<uint> gamesToPlay = entry.GameMinutes
				.Where(x => x.Value > 0)
				.Take(take)
				.Select(x => x.Key)
				.ToList();

			if (gamesToPlay.Count == 0) {
				if (entry.IdleGameIds.Count > 0) {
					if (!BotIdleModules.TryGetValue(bot, out IdleModule? module)) {
						module = new IdleModule(bot);
						BotIdleModules[bot] = module;
					}
					module.SetIdleGames(entry.IdleGameIds);
				}
				return;
			}

			// Initialize per-game wall-clock state
			DateTime now = DateTime.UtcNow;
			if (Instance != null) {
				Instance.GameStartTimes[bot] = gamesToPlay.ToDictionary(id => id, _ => now);
				Instance.GameDurationMinutes[bot] = gamesToPlay.ToDictionary(id => id, id => entry.GameMinutes[id]);
				Instance.TotalPausedDuration[bot] = TimeSpan.Zero;
			}

			// Single timer for all games: first tick in 1 minute, then every minute
			TimeSpan dueTime = TimeSpan.FromMinutes(1);
			Timer? playTimer = null;
			try {
				playTimer = new Timer(
					async _ => await TimerCallback(bot).ConfigureAwait(false),
					null,
					dueTime,
					TimeSpan.FromMinutes(1)
				);
				if (Instance != null) {
					Instance.ActiveTimers[bot] = playTimer;
					playTimer = null; // ownership transferred
				}
			} finally {
				if (playTimer != null) {
					await playTimer.DisposeAsync().ConfigureAwait(false);
				}
			}

			// Start playing game(s)
			HashSet<uint> set = [.. gamesToPlay];
			LogGenericDebug(entry.SequentialMode
				? $"Starting 1 game (sequential): {string.Join(",", set)}"
				: $"Starting {set.Count} game(s) concurrently: {string.Join(",", set)}");
			(bool success, string message) = await bot.Actions.Play(set).ConfigureAwait(false);
			LogGenericDebug($"Play result: {success}. {message}");

			if (!success) {
				if (Instance != null) {
					_ = Instance.GameStartTimes.Remove(bot);
					_ = Instance.GameDurationMinutes.Remove(bot);
					_ = Instance.TotalPausedDuration.Remove(bot);
					if (Instance.ActiveTimers.TryGetValue(bot, out Timer? t)) {
						await t.DisposeAsync().ConfigureAwait(false);
						_ = Instance.ActiveTimers.Remove(bot);
					}
				}
				throw new InvalidOperationException($"Failed to start games: {message}");
			}
		} catch (Exception ex) {
			LogGenericError($"Error in ScheduleGamesImpl: {ex}");
			throw;
		}
	}

	private static async Task TimerCallback(Bot bot) {
		if (Instance == null) {
			return;
		}

		// If config no longer has this bot (e.g. finished or stop command), stop timer and clear state so we don't keep playing
		if (Config?.TimedPlayGames?.ContainsKey(bot.BotName) != true && Instance.GameStartTimes.ContainsKey(bot)) {
			await CleanupTimerAndState(bot).ConfigureAwait(false);
			return;
		}

		if (!Instance.GameStartTimes.TryGetValue(bot, out Dictionary<uint, DateTime>? startTimes) ||
		    !Instance.GameDurationMinutes.TryGetValue(bot, out Dictionary<uint, uint>? durations)) {
			return;
		}

		HashSet<uint> currentSet = [.. startTimes.Keys];
		double pausedMinutes = Instance.TotalPausedDuration.TryGetValue(bot, out TimeSpan paused) ? paused.TotalMinutes : 0;

		// Check if bot is actually playing our set (otherwise treat as paused)
		bool isPlayingOurSet = bot.IsConnectedAndLoggedOn &&
			currentSet.All(g => bot.CardsFarmer.CurrentGamesFarmingReadOnly.Any(c => c.AppID == g));

		if (!isPlayingOurSet) {
			if (Instance.TimerPausedAt.TryGetValue(bot, out DateTime pausedAt)) {
				Instance.TotalPausedDuration[bot] += DateTime.UtcNow - pausedAt;
				Instance.TimerPausedAt[bot] = DateTime.UtcNow;
			} else {
				// First time pausing: we may have counted up to 1 minute while they were already on another machine. Add 1 min back to err on the side of caution.
				Instance.TimerPausedAt[bot] = DateTime.UtcNow;
				LogGenericDebug($"Timer paused for bot {bot.BotName} at {DateTime.UtcNow}");
				await ConfigLock.WaitAsync().ConfigureAwait(false);
				try {
					if (Config?.TimedPlayGames.TryGetValue(bot.BotName, out TimedPlayEntry? currentEntry) == true) {
						foreach (uint gameId in currentSet) {
							if (currentEntry.GameMinutes.TryGetValue(gameId, out uint remaining)) {
								currentEntry.GameMinutes[gameId] = remaining + 1;
							}
							// Keep in-memory duration in sync so wall-clock remaining is correct when we resume
							if (Instance.GameDurationMinutes.TryGetValue(bot, out Dictionary<uint, uint>? durs) && durs.TryGetValue(gameId, out uint dur)) {
								durs[gameId] = dur + 1;
							}
						}
						currentEntry.LastUpdate = DateTime.UtcNow;
						await SaveConfig(lockAlreadyHeld: true).ConfigureAwait(false);
						LogGenericDebug($"Added 1 minute to remaining for {bot.BotName} (pause compensation)");
					}
				} finally {
					_ = ConfigLock.Release();
				}
			}
			(bool resumeSuccess, string resumeMsg) = await bot.Actions.Play(currentSet).ConfigureAwait(false);
			if (resumeSuccess) {
				_ = Instance.TimerPausedAt.Remove(bot);
				LogGenericDebug($"Resumed playing {string.Join(",", currentSet)} for bot {bot.BotName}: {resumeMsg}");
			}
			return;
		}

		// Compute remaining per game
		List<uint> completed = [];
		Dictionary<uint, uint> remainingByGame = [];

		foreach (uint gameId in currentSet) {
			if (!startTimes.TryGetValue(gameId, out DateTime startTime) || !durations.TryGetValue(gameId, out uint durationMinutes)) {
				continue;
			}
			double elapsedMinutes = (DateTime.UtcNow - startTime).TotalMinutes;
			double effectiveElapsed = Math.Max(0, elapsedMinutes - pausedMinutes);
			uint remaining = (uint)Math.Max(0, (int)durationMinutes - (int)Math.Floor(effectiveElapsed));
			remainingByGame[gameId] = remaining;
			if (remaining == 0) {
				completed.Add(gameId);
			}
		}

		if (completed.Count == 0) {
			// No games finished this tick; just persist remaining times
			await ConfigLock.WaitAsync().ConfigureAwait(false);
			try {
				if (Config?.TimedPlayGames.TryGetValue(bot.BotName, out TimedPlayEntry? currentEntry) == true) {
					foreach (KeyValuePair<uint, uint> kv in remainingByGame) {
						currentEntry.GameMinutes[kv.Key] = kv.Value;
					}
					currentEntry.LastUpdate = DateTime.UtcNow;
					await SaveConfig(lockAlreadyHeld: true).ConfigureAwait(false);
					LogGenericDebug($"Updated remaining for {bot.BotName}: {string.Join(", ", remainingByGame.Select(k => $"{k.Key}={k.Value}m"))}");
				}
			} finally {
				_ = ConfigLock.Release();
			}
			return;
		}

		// One or more games finished: remove them, update play set, persist, then Play(newSet) or idle/done
		await ConfigLock.WaitAsync().ConfigureAwait(false);
		try {
			if (Instance == null || Config?.TimedPlayGames.TryGetValue(bot.BotName, out TimedPlayEntry? currentEntry) != true || currentEntry == null) {
				// Config was cleared (e.g. by HandleGameCompletion or stop) — ensure timer and state are cleaned up
				await CleanupTimerAndState(bot).ConfigureAwait(false);
				return;
			}

			ASFTimedPlay instance = Instance;
			_ = instance.TotalPausedDuration.Remove(bot);
			_ = instance.TimerPausedAt.Remove(bot);

			foreach (uint gameId in completed) {
				if (instance.GameStartTimes.TryGetValue(bot, out Dictionary<uint, DateTime>? starts)) {
					_ = starts.Remove(gameId);
				}
				if (instance.GameDurationMinutes.TryGetValue(bot, out Dictionary<uint, uint>? durs)) {
					_ = durs.Remove(gameId);
				}
				currentEntry.GameMinutes.Remove(gameId);
				await HandleGameCompletion(bot, gameId, lockAlreadyHeld: true).ConfigureAwait(false);
				LogGenericDebug($"Game {gameId} completed for {bot.BotName}");
			}

			// Update remaining for games still running
			foreach (KeyValuePair<uint, uint> kv in remainingByGame) {
				if (completed.Contains(kv.Key)) {
					continue;
				}
				currentEntry.GameMinutes[kv.Key] = kv.Value;
			}
			currentEntry.LastUpdate = DateTime.UtcNow;
			await SaveConfig(lockAlreadyHeld: true).ConfigureAwait(false);

			HashSet<uint> newSet = instance.GameStartTimes.TryGetValue(bot, out Dictionary<uint, DateTime>? remainingStarts) && remainingStarts.Count > 0
				? [.. remainingStarts.Keys]
				: [];

			if (newSet.Count > 0) {
				(bool success, string message) = await bot.Actions.Play(newSet).ConfigureAwait(false);
				LogGenericDebug($"After completion, now playing {string.Join(",", newSet)}: {success}. {message}");
			} else {
				// Always dispose timer and clear state first so we never leave an active timer with no config
				try {
					if (instance.ActiveTimers.TryGetValue(bot, out Timer? oldTimer)) {
						await oldTimer.DisposeAsync().ConfigureAwait(false);
						_ = instance.ActiveTimers.Remove(bot);
					}
					_ = instance.GameStartTimes.Remove(bot);
					_ = instance.GameDurationMinutes.Remove(bot);
					(bool success, string message) = await bot.Actions.Play([]).ConfigureAwait(false);
					LogGenericDebug($"Stopped games: {success}. {message}");
				} finally {
					// Ensure state is cleared even if Play throws
					_ = instance.TotalPausedDuration.Remove(bot);
					_ = instance.TimerPausedAt.Remove(bot);
				}

				// Sequential mode: if more games remain, schedule the next one
				if (currentEntry.SequentialMode && currentEntry.GameMinutes.Count > 0) {
					await ScheduleGamesImpl(bot, currentEntry).ConfigureAwait(false);
					return;
				}

				if (currentEntry.IdleGameIds.Count > 0) {
					if (!BotIdleModules.TryGetValue(bot, out IdleModule? module)) {
						module = new IdleModule(bot);
						BotIdleModules[bot] = module;
					}
					module.SetIdleGames(currentEntry.IdleGameIds);
					LogGenericInfo($"No more games to play, starting to idle {string.Join(",", currentEntry.IdleGameIds)}");
				} else if (Config.TimedPlayGames.ContainsKey(bot.BotName)) {
					(bool resumeSuccess, string resumeMsg) = bot.Actions.Resume();
					LogGenericInfo($"Timed play finished for {bot.BotName}: {resumeMsg}");
				}
			}
		} finally {
			_ = ConfigLock.Release();
		}
	}

	private static async Task CleanupTimerAndState(Bot bot) {
		if (Instance == null) {
			return;
		}
		ASFTimedPlay instance = Instance;
		if (instance.ActiveTimers.TryGetValue(bot, out Timer? timer)) {
			await timer.DisposeAsync().ConfigureAwait(false);
			_ = instance.ActiveTimers.Remove(bot);
			LogGenericDebug($"Cleaned up timer for {bot.BotName} (no config entry or early return)");
		}
		_ = instance.GameStartTimes.Remove(bot);
		_ = instance.GameDurationMinutes.Remove(bot);
		_ = instance.TotalPausedDuration.Remove(bot);
		_ = instance.TimerPausedAt.Remove(bot);
		(bool stopSuccess, string stopMsg) = await bot.Actions.Play([]).ConfigureAwait(false);
		LogGenericDebug($"Stopped playing for {bot.BotName}: {stopSuccess}. {stopMsg}");
		(bool resumeSuccess, string resumeMsg) = bot.Actions.Resume();
		LogGenericInfo($"Timed play cleanup for {bot.BotName}: {resumeMsg}");
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
		GameStartTimes.Clear();
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
