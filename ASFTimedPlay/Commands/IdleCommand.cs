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
				"[Bots]: bot name, comma-separated names, or ASF for all bots\n" +
				"Use: \"!idle stop\" or \"!idle stop [Bots]\" to stop idling\n" +
				"Aliases: !i (same as !idle)"
			)).ConfigureAwait(false);
		}

		// Handle stop command
		if (parameters[0].Equals("stop", StringComparison.OrdinalIgnoreCase)) {
			HashSet<Bot>? stopBots = parameters.Length >= 2 ? Bot.GetBots(parameters[1]) : [bot];
			if (stopBots == null || stopBots.Count == 0) {
				return await Task.FromResult<string?>(bot.Commands.FormatBotResponse("No valid bots found!")).ConfigureAwait(false);
			}
			var results = new List<string?>();
			foreach (Bot b in stopBots) {
				results.Add(await CommandHelpers.HandleStopCommand(b, stopIdleGame: true, stopTimedPlayGames: false).ConfigureAwait(false));
			}
			return string.Join(Environment.NewLine, results.Where(r => !string.IsNullOrEmpty(r)));
		}

		// Handle bot selection
		HashSet<Bot>? bots;
		if (parameters[0].Contains(',', StringComparison.Ordinal) || uint.TryParse(parameters[0], out _)) {
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

		// Set up or update idle module for each bot (idling can be interrupted by card farming or use elsewhere; we resume when free)
		foreach (Bot targetBot in bots) {
			if (!targetBot.IsConnectedAndLoggedOn) {
				return await Task.FromResult<string?>(bot.Commands.FormatBotResponse(
					$"{targetBot.BotName} is not connected and logged on."
				)).ConfigureAwait(false);
			}

			// Update config with new idle games
			if (ASFTimedPlay.Config != null) {
				if (!ASFTimedPlay.Config.TimedPlayGames.TryGetValue(targetBot.BotName, out TimedPlayEntry? entry)) {
					entry = new TimedPlayEntry();
					ASFTimedPlay.Config.TimedPlayGames[targetBot.BotName] = entry;
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
		string msg = $"Now idling {gamesText} on {botsText} during downtime.";
		// If any bot is busy (farming/redeeming), idling will start when they're free
		if (bots.Any(b => b.CardsFarmer.CurrentGamesFarmingReadOnly.Count > 0 || b.GamesToRedeemInBackgroundCount > 0)) {
			msg += " (Some bots are busy; idling will resume when free.)";
		}
		return await Task.FromResult<string?>(bot.Commands.FormatBotResponse(msg)).ConfigureAwait(false);
	}
}
