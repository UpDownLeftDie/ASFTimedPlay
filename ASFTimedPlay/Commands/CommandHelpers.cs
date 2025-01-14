using System;
using System.Collections.Generic;
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
			uint? idleGameToResume = null;

			// If we're only stopping PlayFor games and there's an idle game, remember it
			if (!stopIdleGame && stopPlayForGames && entry.IdleGameId > 0) {
				idleGameToResume = entry.IdleGameId;
			}

			// Stop idle games if requested
			if (stopIdleGame && entry.IdleGameId > 0) {
				if (BotIdleModules.TryGetValue(bot, out IdleModule? module)) {
					module.StopIdling();
					module.Dispose();
					_ = BotIdleModules.Remove(bot);
					LogGenericDebug("Stopped and removed idle module");
				}
				entry.IdleGameId = 0;
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
			}

			// Update config if changes were made
			if (madeChanges) {
				entry.LastUpdate = DateTime.UtcNow;
				await SaveConfig().ConfigureAwait(false);

				// Stop any currently playing games
				_ = await bot.Actions.Play([]).ConfigureAwait(false);
				LogGenericDebug("Stopped playing games in Steam");

				// If we have an idle game to resume and the bot isn't busy
				if (idleGameToResume.HasValue && bot.IsConnectedAndLoggedOn) {
					LogGenericDebug($"Resuming idle game {idleGameToResume.Value}");
					if (!BotIdleModules.TryGetValue(bot, out IdleModule? module)) {
						module = new IdleModule(bot);
						BotIdleModules[bot] = module;
					}
					module.SetIdleGames(idleGameToResume.Value);
				}
			}

			string message = (stopIdleGame, stopPlayForGames) switch {
				(true, true) => "Stopped all games and idling",
				(true, false) => "Stopped idling game",
				(false, true) => "Stopped playfor games and resumed idling",
				_ => "No changes made"
			};

			return bot.Commands.FormatBotResponse(message);
		}

		return bot.Commands.FormatBotResponse("No active games found");
	}
}
