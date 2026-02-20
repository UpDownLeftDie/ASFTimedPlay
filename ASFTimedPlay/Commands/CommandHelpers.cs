using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArchiSteamFarm.Helpers.Json;
using ArchiSteamFarm.Steam;
using static ASFTimedPlay.ASFTimedPlay;
using static ASFTimedPlay.Utils;
using Timer = System.Threading.Timer;

namespace ASFTimedPlay.Commands;

internal static class CommandHelpers {
	public static async Task<string?> HandleStopCommand(Bot bot, bool stopIdleGame = true, bool stopTimedPlayGames = true) {
		if (Config?.TimedPlayGames.TryGetValue(bot.BotName, out TimedPlayEntry? entry) == true) {
			bool madeChanges = false;
			HashSet<uint> idleGamesToResume = [];

			// If we're only stopping timed play games and there are idle games, remember them
			if (!stopIdleGame && stopTimedPlayGames && entry.IdleGameIds.Count > 0) {
				idleGamesToResume = new HashSet<uint>(entry.IdleGameIds);
			}

			// Stop idle games if requested
			if (stopIdleGame && entry.IdleGameIds.Count > 0) {
				if (BotIdleModules.TryGetValue(bot, out IdleModule? module)) {
					module.StopIdling();
					module.Dispose();
					_ = BotIdleModules.Remove(bot);
					LogGenericDebug("Stopped and removed idle module");
				}
				entry.IdleGameIds.Clear();
				madeChanges = true;
			}

			// Clear timed play games if requested
			if (stopTimedPlayGames && entry.GameMinutes.Count > 0) {
				entry.GameMinutes.Clear();
				madeChanges = true;

								// Dispose timer for this bot
				if (Instance != null && Instance.ActiveTimers.TryGetValue(bot, out Timer? timer)) {
					await timer.DisposeAsync().ConfigureAwait(false);
					_ = Instance.ActiveTimers.Remove(bot);
					LogGenericDebug($"Cleared timer for bot {bot.BotName}");
				}

				// Clear per-game timer state and wall-clock tracking
				if (Instance != null) {
					_ = Instance.GameStartTimes.Remove(bot);
					_ = Instance.GameDurationMinutes.Remove(bot);
					_ = Instance.TimerPausedAt.Remove(bot);
					_ = Instance.TotalPausedDuration.Remove(bot);
					LogGenericDebug($"Cleared timer state for bot {bot.BotName}");
				}
			}

			// Update config if changes were made
			if (madeChanges) {
				entry.LastUpdate = DateTime.UtcNow;
				await SaveConfig().ConfigureAwait(false);

				// Stop any currently playing games
				_ = await bot.Actions.Play([]).ConfigureAwait(false);
				LogGenericDebug("Stopped playing games in Steam");

				// If we have idle games to resume and the bot isn't busy
				if (idleGamesToResume.Count > 0 && bot.IsConnectedAndLoggedOn) {
					LogGenericDebug($"Resuming idle games {string.Join(",", idleGamesToResume)}");
					if (!BotIdleModules.TryGetValue(bot, out IdleModule? module)) {
						module = new IdleModule(bot);
						BotIdleModules[bot] = module;
					}
					module.SetIdleGames(idleGamesToResume);
				}
			}

			string message = (stopIdleGame, stopTimedPlayGames) switch {
				(true, true) => "Stopped all games and idling",
				(true, false) => "Stopped idling games",
				(false, true) => "Stopped timed play and resumed idling",
				_ => "No changes made"
			};

			return bot.Commands.FormatBotResponse(message);
		}

		return bot.Commands.FormatBotResponse("No active games found");
	}

	public static Task<string?> HandleStatusCommand(Bot bot) {
		if (Config?.TimedPlayGames.TryGetValue(bot.BotName, out TimedPlayEntry? entry) == true) {
			List<string> statusParts = [];

			// Check if there are active timed play games
			if (entry.GameMinutes.Count > 0) {
				// Same check as timer callback: is the bot actually playing these games right now? (e.g. not playing on another machine)
				bool actuallyPlayingRightNow = bot.IsConnectedAndLoggedOn &&
					entry.GameMinutes.Keys.All(g => bot.CardsFarmer.CurrentGamesFarmingReadOnly.Any(c => c.AppID == g));
				bool hasTimer = Instance?.ActiveTimers.ContainsKey(bot) == true;
				bool inPausedState = Instance?.TimerPausedAt.ContainsKey(bot) == true || (hasTimer && !actuallyPlayingRightNow);

				// When paused, show raw remaining (time does not count). When playing, apply wall-clock correction.
				string gamesStatus;
				if (inPausedState) {
					gamesStatus = string.Join(", ", entry.GameMinutes.Select(kvp => $"{kvp.Key} ({kvp.Value} min remaining, paused)"));
				} else {
					double elapsedSinceUpdate = (DateTime.UtcNow - entry.LastUpdate).TotalMinutes;
					gamesStatus = string.Join(", ", entry.GameMinutes.Select(kvp => {
						uint effectiveRemaining = (uint)Math.Max(0, (int)kvp.Value - (int)Math.Floor(elapsedSinceUpdate));
						return $"{kvp.Key} ({effectiveRemaining} minutes remaining)";
					}));
				}
				statusParts.Add($"Playing: {gamesStatus}");

				// Timer: ACTIVE = counting down because bot is playing. PAUSED = not playing (e.g. playing on another machine) so time does not count.
				if (hasTimer) {
					if (inPausedState) {
						statusParts.Add("Timer: PAUSED (time not counting)");
					} else {
						statusParts.Add("Timer: ACTIVE");
					}
				} else {
					statusParts.Add("Timer: INACTIVE");
				}
				if (entry.SequentialMode) {
					statusParts.Add("Mode: sequential");
				} else {
					statusParts.Add("Mode: concurrent");
				}
			}

						// Check idle games
			if (entry.IdleGameIds.Count > 0) {
				bool isIdling = BotIdleModules.TryGetValue(bot, out IdleModule? module) &&
					module != null;
				statusParts.Add($"Idle games: {string.Join(",", entry.IdleGameIds)} ({(isIdling ? "ACTIVE" : "INACTIVE")})");
			}

						if (statusParts.Count > 0) {
				return Task.FromResult<string?>(bot.Commands.FormatBotResponse(string.Join(" | ", statusParts)));
			}
		}

		return Task.FromResult<string?>(bot.Commands.FormatBotResponse("No active timed play or idle games"));
	}

	public static async Task<string?> HandleDebugCommand(Bot bot) {
		Version? version = typeof(CommandHelpers).Assembly.GetName().Version;
		string versionStr = version?.ToString(3) ?? "?.?.?";
		string configPath = ConfigPath;
		bool configExists = File.Exists(configPath);

		string configJson;
		await ConfigLock.WaitAsync().ConfigureAwait(false);
		try {
			configJson = Config != null ? Config.ToJsonText(true) : "null";
		} finally {
			_ = ConfigLock.Release();
		}

		int activeTimerCount = Instance?.ActiveTimers.Count ?? 0;
		int botsWithEntries = Config?.TimedPlayGames.Count ?? 0;
		string botNames = Config?.TimedPlayGames.Count > 0
			? string.Join(", ", Config.TimedPlayGames.Keys)
			: "(none)";

		string debug = string.Join(
			"\n",
			"[ASFTimedPlay Debug]",
			$"Version: {versionStr}",
			$"Config path: {configPath}",
			$"Config file exists: {configExists}",
			$"Active timers: {activeTimerCount} | Bots with entries: {botsWithEntries} ({botNames})",
			"--- Config JSON ---",
			configJson
		);

		return bot.Commands.FormatBotResponse(debug);
	}
}
