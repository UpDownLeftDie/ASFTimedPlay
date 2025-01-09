using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ArchiSteamFarm.Steam;

namespace ASFTimedPlay.Commands;

internal static class PlayForCommand {
	public static async Task<string?> Response(Bot bot, string[] args) {
		string[] parameters = args.Skip(1).ToArray();
		if (parameters.Length < 2) {
			return bot.Commands.FormatBotResponse(
				"Usage: !playfor [Bots] <AppID1,AppID2,...> <Minutes>"
			);
		}

		// Handle bot selection
		HashSet<Bot>? bots;
		if (uint.TryParse(parameters[0], out _)) {
			bots = Bot.GetBots("ASF");
		} else {
			bots = Bot.GetBots(parameters[0]);
			if (bots != null && bots.Count > 0) {
				parameters = parameters.Skip(1).ToArray();
			} else {
				bots = [bot];
			}
		}

		if (bots == null || bots.Count == 0) {
			return bot.Commands.FormatBotResponse("No bots found!");
		}

		// Parse game IDs
		List<uint> gameIds = [];
		string[] gameStrings = parameters[0].Split(',');
		foreach (string game in gameStrings) {
			if (!uint.TryParse(game, out uint gameId)) {
				return bot.Commands.FormatBotResponse($"Invalid game ID: {game}");
			}
			gameIds.Add(gameId);
		}

		// Parse minutes
		List<uint> minutes = [];
		string[] minuteStrings = parameters[1].Split(',');
		foreach (string min in minuteStrings) {
			if (!uint.TryParse(min, out uint minuteValue) || minuteValue == 0) {
				return bot.Commands.FormatBotResponse("Please provide valid numbers of minutes!");
			}
			minutes.Add(minuteValue);
		}

		// Validate input combinations
		if (minutes.Count != 1 && minutes.Count != gameIds.Count) {
			return bot.Commands.FormatBotResponse(
				"Must enter single number of minutes for all games or list of minutes for each game!"
			);
		}

		await ASFTimedPlay.ConfigLock.WaitAsync().ConfigureAwait(false);
		try {
			PlayForEntry entry = new() {
				GameIds = [.. gameIds],
				Minutes = minutes,
				RemainingMinutes = gameIds.ToDictionary(
					gameId => gameId,
					gameId => minutes.Count == 1 ? minutes[0] : minutes[gameIds.IndexOf(gameId)]
				),
			};

			if (ASFTimedPlay.Config != null) {
				if (!ASFTimedPlay.Config.PlayForGames.TryGetValue(bot.BotName, out List<PlayForEntry>? entries)) {
					entries = [];
					ASFTimedPlay.Config.PlayForGames[bot.BotName] = entries;
				}
				entries.Add(entry);
			}

			await ASFTimedPlay.SaveConfig().ConfigureAwait(false);
			await ASFTimedPlay.ScheduleGames(bot, entry).ConfigureAwait(false);
		} finally {
			_ = ASFTimedPlay.ConfigLock.Release();
		}

		// Format response message
		string gamesInfo = string.Join(
			", ",
			gameIds.Select(
				(id, index) => {
					uint duration = minutes.Count == 1 ? minutes[0] : minutes[index];
					return $"game {id} for {duration} minutes";
				}
			)
		);

		return bot.Commands.FormatBotResponse($"Now playing {gamesInfo}.");
	}
}
