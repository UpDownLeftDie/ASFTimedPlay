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
				"Usage: !idle [Bots] <AppID1,AppID2,...>\n" +
				"Use: \"!idle stop\" to stop idling\n" +
				"Aliases: !i (same as !idle)"
			)).ConfigureAwait(false);
		}

		// Handle stop command
		if (parameters[0].Equals("stop", StringComparison.OrdinalIgnoreCase)) {
			return await CommandHelpers.HandleStopCommand(bot, stopIdleGame: true, stopPlayForGames: false).ConfigureAwait(false);
		}

		// Handle bot selection
		HashSet<Bot>? bots;
		if (parameters[0].Contains(',') || uint.TryParse(parameters[0], out _)) {
			// If first parameter contains commas or is a number, assume it's game IDs and use current bot
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
		string[] gameStrings = gameString.Split(',');

		// Check for 32-game limit
		if (gameStrings.Length > 32) {
			return await Task.FromResult<string?>(bot.Commands.FormatBotResponse("Too many games specified! Maximum is 32 games.")).ConfigureAwait(false);
		}

		List<uint> gameIds = [];
		foreach (string game in gameStrings) {
			if (!uint.TryParse(game, out uint gameId)) {
				return await Task.FromResult<string?>(bot.Commands.FormatBotResponse($"Invalid game ID: {game}")).ConfigureAwait(false);
			}
			gameIds.Add(gameId);
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

			// Update config with new idle games
			if (ASFTimedPlay.Config != null) {
				if (!ASFTimedPlay.Config.PlayForGames.TryGetValue(targetBot.BotName, out PlayForEntry? entry)) {
					entry = new PlayForEntry();
					ASFTimedPlay.Config.PlayForGames[targetBot.BotName] = entry;
				}
				entry.IdleGameIds = new HashSet<uint>(gameIds);
				entry.LastUpdate = DateTime.UtcNow;
			}

			if (!ASFTimedPlay.BotIdleModules.TryGetValue(targetBot, out IdleModule? module)) {
				module = new IdleModule(targetBot);
				ASFTimedPlay.BotIdleModules[targetBot] = module;
			}

			module.SetIdleGames(gameIds);
		}

		await ASFTimedPlay.SaveConfig().ConfigureAwait(false);

		string botsText = string.Join(",", bots.Select(b => b.BotName));
		string gamesText = string.Join(",", gameIds);
		return await Task.FromResult<string?>(bot.Commands.FormatBotResponse(
			$"Now idling {gamesText} on {botsText} during downtime."
		)).ConfigureAwait(false);
	}
}
