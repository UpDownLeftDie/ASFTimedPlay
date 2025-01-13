using System;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Steam;
using static ASFTimedPlay.ASFTimedPlay;

namespace ASFTimedPlay.Commands;

internal static class CommandHelpers {
	public static async Task<string?> HandleStopCommand(Bot bot, bool stopIdleGame = true, bool stopPlayForGames = true) {
		if (Config?.PlayForGames.TryGetValue(bot.BotName, out PlayForEntry? entry) == true) {
			bool madeChanges = false;

			// Stop idle games if requested
			if (stopIdleGame && entry.IdleGameId > 0) {
				if (BotIdleModules.TryGetValue(bot, out IdleModule? module)) {
					module.StopIdling();
					module.Dispose();
					_ = BotIdleModules.Remove(bot);
					ASF.ArchiLogger.LogGenericDebug("Stopped and removed idle module");
				}
				entry.IdleGameId = 0;
				madeChanges = true;
			}

			// Clear PlayFor games if requested
			if (stopPlayForGames && entry.GameMinutes.Count > 0) {
				entry.GameMinutes.Clear();
				madeChanges = true;
			}

			// Update config if changes were made
			if (madeChanges) {
				entry.LastUpdate = DateTime.UtcNow;
				await SaveConfig().ConfigureAwait(false);

				// Stop any currently playing games
				_ = await bot.Actions.Play([]).ConfigureAwait(false);
				ASF.ArchiLogger.LogGenericDebug("Stopped playing games in Steam");
			}

			string message = (stopIdleGame, stopPlayForGames) switch {
				(true, true) => "Stopped all games",
				(true, false) => "Stopped idling game",
				(false, true) => "Stopped playing games",
				_ => "No changes made"
			};

			return bot.Commands.FormatBotResponse(message);
		}

		return bot.Commands.FormatBotResponse("No active games found");
	}
}
