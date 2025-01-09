using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ArchiSteamFarm.Steam;

namespace ASFTimedPlay.Commands;

internal static class IdleCommand {
	public static Task<string?> Response(Bot bot, string[] args) {
		string[] parameters = args.Skip(1).ToArray();

		// Clear idle games if no parameters
		if (parameters.Length == 0) {
			if (ASFTimedPlay.BotIdleModules.TryGetValue(bot, out ASFTimedPlay.IdleModule? module)) {
				module.StopIdling();
				_ = ASFTimedPlay.BotIdleModules.Remove(bot);
			}
			return Task.FromResult<string?>(bot.Commands.FormatBotResponse("Cleared idle games list."));
		}

		// Handle bot selection
		HashSet<Bot>? bots;
		if (uint.TryParse(parameters[0], out _)) {
			// If first parameter is a number, assume it's a game ID and use current bot
			bots = [bot];
		} else {
			bots = Bot.GetBots(parameters[0]);
			if (bots != null && bots.Count > 0) {
				parameters = parameters.Skip(1).ToArray();
			} else {
				bots = [bot];
			}
		}

		if (parameters.Length == 0) {
			return Task.FromResult<string?>(bot.Commands.FormatBotResponse("Please provide at least one game ID!"));
		}

		// Parse game IDs
		HashSet<uint> gameIds = [];
		string[] gameStrings = parameters[0].Split(',');
		foreach (string game in gameStrings) {
			if (!uint.TryParse(game, out uint gameId)) {
				return Task.FromResult<string?>(bot.Commands.FormatBotResponse($"Invalid game ID: {game}"));
			}
			_ = gameIds.Add(gameId);
		}

		// Set up or update idle module for each bot
		foreach (Bot targetBot in bots) {
			if (!ASFTimedPlay.BotIdleModules.TryGetValue(targetBot, out ASFTimedPlay.IdleModule? module)) {
				module = new ASFTimedPlay.IdleModule(targetBot);
				ASFTimedPlay.BotIdleModules[targetBot] = module;
			}

			module.SetIdleGames(gameIds);
		}

		string gamesText = string.Join(",", gameIds);
		string botsText = string.Join(",", bots.Select(b => b.BotName));
		return Task.FromResult<string?>(bot.Commands.FormatBotResponse(
			$"Now idling {gamesText} on {botsText} during downtime."
		));
	}
}
