using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ArchiSteamFarm.Steam;

namespace ASFTimedPlay.Commands;

internal static class IdleCommand {
	public static async Task<string?> Response(Bot bot, string[] args) {
		string[] parameters = [.. args.Skip(1)];

		if (parameters.Length == 0) {
			return await Task.FromResult<string?>(bot.Commands.FormatBotResponse(
				"Usage: !idle [Bots] <AppID>\n" +
				"Use: \"!idle stop\" to stop idling"
			)).ConfigureAwait(false);
		}

		// Handle stop command
		if (parameters[0].Equals("stop", StringComparison.OrdinalIgnoreCase)) {
			return await CommandHelpers.HandleStopCommand(bot, stopIdleGame: true, stopPlayForGames: false).ConfigureAwait(false);
		}

		// Handle bot selection
		HashSet<Bot>? bots;
		if (uint.TryParse(parameters[0], out _)) {
			// If first parameter is a number, assume it's a game ID and use current bot
			bots = [bot];
		} else {
			bots = Bot.GetBots(parameters[0]);
			if (bots == null || bots.Count == 0) {
				return await Task.FromResult<string?>(bot.Commands.FormatBotResponse("No valid bots found!")).ConfigureAwait(false);
			} else {
				parameters = [.. parameters.Skip(1)];
			}
		}

		if (parameters.Length == 0) {
			return await Task.FromResult<string?>(bot.Commands.FormatBotResponse("Please provide at least one game ID!")).ConfigureAwait(false);
		}

		// Parse game IDs
		string gameString = parameters[0];
		if (!uint.TryParse(gameString, out uint gameId)) {
			return await Task.FromResult<string?>(bot.Commands.FormatBotResponse($"Invalid game ID: {gameId}")).ConfigureAwait(false);
		}

		// Set up or update idle module for each bot
		foreach (Bot targetBot in bots) {
			// Only set up idling if the bot isn't busy with critical tasks
			if (!targetBot.IsConnectedAndLoggedOn) {
				return await Task.FromResult<string?>(bot.Commands.FormatBotResponse(
					$"{targetBot.BotName} is not connected and logged on."
				)).ConfigureAwait(false);
			}

			if (targetBot.CardsFarmer.CurrentGamesFarmingReadOnly.Count > 0) {
				return await Task.FromResult<string?>(bot.Commands.FormatBotResponse(
					$"{targetBot.BotName} is currently farming cards."
				)).ConfigureAwait(false);
			}

			if (targetBot.GamesToRedeemInBackgroundCount > 0) {
				return await Task.FromResult<string?>(bot.Commands.FormatBotResponse(
					$"{targetBot.BotName} is currently redeeming games."
				)).ConfigureAwait(false);
			}

			// Update config with new idle game
			if (ASFTimedPlay.Config != null) {
				if (!ASFTimedPlay.Config.PlayForGames.TryGetValue(targetBot.BotName, out PlayForEntry? entry)) {
					entry = new PlayForEntry();
					ASFTimedPlay.Config.PlayForGames[targetBot.BotName] = entry;
				}
				entry.IdleGameId = gameId;
				entry.LastUpdate = DateTime.UtcNow;
			}

			if (!ASFTimedPlay.BotIdleModules.TryGetValue(targetBot, out IdleModule? module)) {
				module = new IdleModule(targetBot);
				ASFTimedPlay.BotIdleModules[targetBot] = module;
			}

			module.SetIdleGames(gameId);
		}

		await ASFTimedPlay.SaveConfig().ConfigureAwait(false);

		string botsText = string.Join(",", bots.Select(b => b.BotName));
		return await Task.FromResult<string?>(bot.Commands.FormatBotResponse(
			$"Now idling {gameId} on {botsText} during downtime."
		)).ConfigureAwait(false);
	}
}
