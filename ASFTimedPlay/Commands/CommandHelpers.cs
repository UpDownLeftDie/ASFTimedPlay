using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ArchiSteamFarm.Steam;
using static ASFTimedPlay.ASFTimedPlay;
using static ASFTimedPlay.Utils;
using Timer = System.Threading.Timer;

namespace ASFTimedPlay.Commands;

internal static class CommandHelpers {
	public static async Task<string?> HandleStopCommand(Bot bot, bool stopIdleGame = true, bool stopPlayForGames = true) {
		if (Config?.PlayForGames.TryGetValue(bot.BotName, out PlayForEntry? entry) == true) {
			bool madeChanges = false;
			HashSet<uint> idleGamesToResume = [];

			// If we're only stopping PlayFor games and there are idle games, remember them
			if (!stopIdleGame && stopPlayForGames && entry.IdleGameIds.Count > 0) {
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

			// Clear PlayFor games if requested
			if (stopPlayForGames && entry.GameMinutes.Count > 0) {
				entry.GameMinutes.Clear();
				madeChanges = true;

								// Dispose timer for this bot
				if (Instance != null && Instance.ActiveTimers.TryGetValue(bot, out Timer? timer)) {
					await timer.DisposeAsync().ConfigureAwait(false);
					_ = Instance.ActiveTimers.Remove(bot);
					LogGenericDebug($"Cleared timer for bot {bot.BotName}");
				}

				// Clear timer start time
				if (Instance != null && Instance.TimerStartTimes.ContainsKey(bot)) {
					_ = Instance.TimerStartTimes.Remove(bot);
					LogGenericDebug($"Cleared timer start time for bot {bot.BotName}");
				}

				// Also clear any paused timer state
				if (Instance != null && Instance.TimerPausedAt.ContainsKey(bot)) {
					_ = Instance.TimerPausedAt.Remove(bot);
					LogGenericDebug($"Cleared paused timer state for bot {bot.BotName}");
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

			string message = (stopIdleGame, stopPlayForGames) switch {
				(true, true) => "Stopped all games and idling",
				(true, false) => "Stopped idling games",
				(false, true) => "Stopped playfor games and resumed idling",
				_ => "No changes made"
			};

			return bot.Commands.FormatBotResponse(message);
		}

		return bot.Commands.FormatBotResponse("No active games found");
	}

	public static Task<string?> HandleStatusCommand(Bot bot) {
		if (Config?.PlayForGames.TryGetValue(bot.BotName, out PlayForEntry? entry) == true) {
			List<string> statusParts = [];

			// Check if there are active PlayFor games
			if (entry.GameMinutes.Count > 0) {
				string gamesStatus = string.Join(", ", entry.GameMinutes.Select(kvp =>
					$"{kvp.Key} ({kvp.Value} minutes remaining)"));
				statusParts.Add($"Playing: {gamesStatus}");

				// Check if timer is active
				bool hasTimer = Instance?.ActiveTimers.ContainsKey(bot) == true;
				bool isPaused = Instance?.TimerPausedAt.ContainsKey(bot) == true;

				if (hasTimer) {
					if (isPaused) {
						statusParts.Add("Timer: PAUSED");
					} else {
						statusParts.Add("Timer: ACTIVE");
					}
				} else {
					statusParts.Add("Timer: INACTIVE");
				}
			}

						// Check idle games
			if (entry.IdleGameIds.Count > 0) {
				bool isIdling = BotIdleModules.TryGetValue(bot, out IdleModule? module) &&
					module != null;
				statusParts.Add($"Idle games: {string.Join(",", entry.IdleGameIds)} ({(isIdling ? "ACTIVE" : "INACTIVE")})");
			}

						if (statusParts.Count > 0) {
				return Task.FromResult(bot.Commands.FormatBotResponse(string.Join(" | ", statusParts)));
			}
		}

		return Task.FromResult(bot.Commands.FormatBotResponse("No active PlayFor games or idle games"));
	}
}
