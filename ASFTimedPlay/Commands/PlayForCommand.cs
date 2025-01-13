using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Steam;

namespace ASFTimedPlay.Commands;

internal static class PlayForCommand {
	public static async Task<string?> Response(Bot bot, string[] args, EAccess access = EAccess.None, ulong steamID = 0) {
		try {
			string[] parameters = args.Skip(1).ToArray();
			if (parameters.Length < 2) {
				return bot.Commands.FormatBotResponse(
					"Usage: !playfor [Bots] <AppID1,AppID2,...> <Minutes>"
				);
			}

			// Handle bot selection
			HashSet<Bot>? bots;
			if (parameters[0].Contains(',', StringComparison.Ordinal) || uint.TryParse(parameters[0], out _)) {
				// If first parameter contains commas or is a number, assume it's game IDs and use current bot
				bots = [bot];
			} else {
				bots = Bot.GetBots(parameters[0]);
				if (bots == null || bots.Count == 0) {
					return bot.Commands.FormatBotResponse("No valid bots found!");
				}
				parameters = parameters.Skip(1).ToArray();
			}

			if (parameters.Length < 2) {
				return bot.Commands.FormatBotResponse(
					"Please provide both game IDs and minutes!"
				);
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

			// Parse minutes and handle idle marker (*)
			List<uint> minutes = [];
			HashSet<uint> idleAfterCompletion = [];
			string[] minuteStrings = parameters[1].Split(',');

			// If single minute value provided, apply to all games
			if (minuteStrings.Length == 1) {
				string min = minuteStrings[0];
				if (min == "*") {
					// Only the last game should be idled
					_ = idleAfterCompletion.Add(gameIds[^1]); // Add only the last game
					minutes.AddRange(gameIds.Select(id => idleAfterCompletion.Contains(id) ? 0 : (uint) 1));
				} else if (uint.TryParse(min, out uint minuteValue) && minuteValue > 0) {
					minutes.AddRange(Enumerable.Repeat(minuteValue, gameIds.Count));
				} else {
					return bot.Commands.FormatBotResponse($"Invalid minutes value: {min}");
				}
			} else {
				// Handle individual minute values for each game
				if (minuteStrings.Length != gameIds.Count) {
					return bot.Commands.FormatBotResponse(
						"Number of minute values must match number of games!"
					);
				}

				bool hasIdleGame = false;
				for (int i = 0; i < minuteStrings.Length; i++) {
					string min = minuteStrings[i];
					if (min == "*") {
						if (hasIdleGame) {
							return bot.Commands.FormatBotResponse("Only one game can be marked for idling!");
						}
						if (i != minuteStrings.Length - 1) {
							return bot.Commands.FormatBotResponse("Only the last game can be marked for idling!");
						}
						hasIdleGame = true;
						_ = idleAfterCompletion.Add(gameIds[i]);
						minutes.Add(0);
					} else if (uint.TryParse(min, out uint minuteValue) && minuteValue > 0) {
						minutes.Add(minuteValue);
					} else {
						return bot.Commands.FormatBotResponse($"Invalid minutes value: {min}");
					}
				}
			}

			foreach (Bot targetBot in bots) {
				try {
					PlayForEntry entry = new() {
						GameIds = [.. gameIds],
						Minutes = minutes,
						RemainingMinutes = gameIds.ToDictionary(
							gameId => gameId,
							gameId => minutes[gameIds.IndexOf(gameId)]
						),
						IdleAfterCompletion = idleAfterCompletion,
						LastUpdate = DateTime.UtcNow
					};

					if (ASFTimedPlay.Config == null) {
						return bot.Commands.FormatBotResponse("Internal error: Config is null");
					}

					await ASFTimedPlay.ConfigLock.WaitAsync().ConfigureAwait(false);
					try {
						if (!ASFTimedPlay.Config.PlayForGames.TryGetValue(targetBot.BotName, out List<PlayForEntry>? entries)) {
							entries = [];
							ASFTimedPlay.Config.PlayForGames[targetBot.BotName] = entries;
						}

						entries.Add(entry);
						await ASFTimedPlay.SaveConfig().ConfigureAwait(false);
						await ASFTimedPlay.ScheduleGames(targetBot, entry).ConfigureAwait(false);
					} finally {
						_ = ASFTimedPlay.ConfigLock.Release();
					}
				} catch (Exception ex) {
					return bot.Commands.FormatBotResponse($"Failed to process bot {targetBot.BotName}: {ex.Message}");
				}
			}

			string gamesInfo = string.Join(", ", gameIds.Select((id, index) => idleAfterCompletion.Contains(id) ? $"game {id} will be idled indefinitely" : $"game {id} for {minutes[index]} minutes"));

			string botsText = string.Join(",", bots.Select(b => b.BotName));
			return bot.Commands.FormatBotResponse($"Now playing {gamesInfo} on {botsText}.");
		} catch (Exception ex) {
			ASF.ArchiLogger.LogGenericError($"PlayFor command failed: {ex}");
			return bot.Commands.FormatBotResponse($"Command failed: {ex.Message}");
		}
	}
}
