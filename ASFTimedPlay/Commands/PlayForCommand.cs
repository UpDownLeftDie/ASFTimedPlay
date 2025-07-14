using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Globalization;
using static ASFTimedPlay.Utils;
using ArchiSteamFarm.Steam;

namespace ASFTimedPlay.Commands;

internal static class PlayForCommand {
	public static async Task<string?> Response(Bot bot, string[] args) {
		try {
			LogGenericDebug($"PlayFor command started with args: {string.Join(" ", args)}");

			string[] parameters = [.. args.Skip(1)];
			if (parameters.Length == 0) {
				return bot.Commands.FormatBotResponse(
					"Usage: !playfor [Bots] <AppID1,AppID2,...> <Minutes1,Minutes2,...>\n" +
					"Examples:\n" +
					"  !playfor 440,570 30,45,* → Play 440 for 30min, 570 for 45min, then idle both\n" +
					"  !playfor 440,570,123,456 30,45,* → Play 440/570 for time, then idle 123/456\n" +
					"Use: \"!playfor stop\" to stop playing (keeps any idle games)\n" +
					"Use: \"!playfor stopall\" to stop everything (including idling)\n" +
					"Use: \"!playfor status\" to check current status\n" +
					"Maximum 32 games total\n" +
					"Aliases: !pf (same as !playfor)"
				);
			}

			// Handle stop command
			if (parameters[0].Equals("stop", StringComparison.OrdinalIgnoreCase)) {
				return await CommandHelpers.HandleStopCommand(bot, stopIdleGame: false, stopPlayForGames: true).ConfigureAwait(false);
			}

			// Handle stopall command
			if (parameters[0].Equals("stopall", StringComparison.OrdinalIgnoreCase)) {
				return await CommandHelpers.HandleStopCommand(bot, stopIdleGame: true, stopPlayForGames: true).ConfigureAwait(false);
			}

			// Handle status command
			if (parameters[0].Equals("status", StringComparison.OrdinalIgnoreCase)) {
				return await CommandHelpers.HandleStatusCommand(bot).ConfigureAwait(false);
			}

			// Handle bot selection
			HashSet<Bot>? bots;
			if (parameters[0].Contains(',', StringComparison.Ordinal) || uint.TryParse(parameters[0], out _)) {
				LogGenericDebug("Using current bot");
				bots = [bot];
			} else {
				bots = Bot.GetBots(parameters[0]);
				LogGenericDebug($"Got bots: {string.Join(",", bots?.Select(b => b.BotName) ?? [])}");
				if (bots == null || bots.Count == 0) {
					return bot.Commands.FormatBotResponse("No valid bots found!");
				}
				parameters = [.. parameters.Skip(1)];
			}

			LogGenericDebug($"Processing parameters: {string.Join(" ", parameters)}");

			if (parameters.Length < 2) {
				return bot.Commands.FormatBotResponse(
					"Please provide both game IDs and minutes!"
				);
			}

			// Parse game IDs and handle idle marker (*)
			List<uint> gameIds = [];
			List<uint> idleGames = [];
			string[] gameStrings = parameters[0].Split(',');
			string[] minuteStrings = parameters[1].Split(',');

			// Check for 32-game limit
			if (gameStrings.Length > 32) {
				return bot.Commands.FormatBotResponse("Too many games specified! Maximum is 32 games.");
			}

			// If we have multiple games but single minute value
			if (minuteStrings.Length == 1) {
				string min = minuteStrings[0];
				if (min == "*") {
					// All games after the first are idle games
					for (int i = 1; i < gameStrings.Length; i++) {
						idleGames.Add(uint.Parse(gameStrings[i], CultureInfo.InvariantCulture));
					}
					// Keep only the first game for timed play
					gameStrings = [gameStrings[0]];
				}
			} else if (minuteStrings.Length == gameStrings.Length) {
				// Check if any minute value is "*"
				int starIndex = Array.IndexOf(minuteStrings, "*");
				if (starIndex >= 0) {
					// All games after the star are idle games
					for (int i = starIndex; i < gameStrings.Length; i++) {
						idleGames.Add(uint.Parse(gameStrings[i], CultureInfo.InvariantCulture));
					}
					// Keep only games before the star for timed play
					gameStrings = [.. gameStrings.Take(starIndex)];
					minuteStrings = [.. minuteStrings.Take(starIndex)];
				}
			}

			// Parse remaining game IDs
			foreach (string game in gameStrings) {
				if (!uint.TryParse(game, out uint gameId)) {
					return bot.Commands.FormatBotResponse($"Invalid game ID: {game}");
				}
				gameIds.Add(gameId);
			}

			// Parse minutes
			List<uint> minutes = [];
			if (minuteStrings.Length == 1 && gameIds.Count > 0) {
				// Single minute value for all games
				if (!uint.TryParse(minuteStrings[0], out uint minuteValue) || minuteValue == 0) {
					return bot.Commands.FormatBotResponse($"Invalid minutes value: {minuteStrings[0]}");
				}
				minutes.AddRange(Enumerable.Repeat(minuteValue, gameIds.Count));
			} else {
				// Individual minute values
				if (minuteStrings.Length != gameIds.Count) {
					return bot.Commands.FormatBotResponse("Number of minute values must match number of games!");
				}

				foreach (string min in minuteStrings) {
					if (!uint.TryParse(min, out uint minuteValue) || minuteValue == 0) {
						return bot.Commands.FormatBotResponse($"Invalid minutes value: {min}");
					}
					minutes.Add(minuteValue);
				}
			}

			foreach (Bot targetBot in bots) {
				PlayForEntry entry = new() {
					GameMinutes = gameIds.ToDictionary(
						gameId => gameId,
						gameId => minutes[gameIds.IndexOf(gameId)]
					),
					IdleGameIds = new HashSet<uint>(idleGames),
					LastUpdate = DateTime.UtcNow
				};

				if (ASFTimedPlay.Config == null) {
					return bot.Commands.FormatBotResponse("Internal error: Config is null");
				}

				bool lockTaken = false;
				try {
					await ASFTimedPlay.ConfigLock.WaitAsync().ConfigureAwait(false);
					lockTaken = true;

					ASFTimedPlay.Config.PlayForGames[targetBot.BotName] = entry;

					await ASFTimedPlay.SaveConfig(lockAlreadyHeld: true).ConfigureAwait(false);
					await ASFTimedPlay.ScheduleGames(targetBot, entry).ConfigureAwait(false);
				} finally {
					if (lockTaken) {
						_ = ASFTimedPlay.ConfigLock.Release();
					}
				}
			}

			string gamesInfo = string.Join(", ", gameIds.Select((id, index) =>
				$"{id} for {minutes[index]} minutes"));
			if (idleGames.Count > 0) {
				gamesInfo += $", {string.Join(",", idleGames)} will be idled after completion";
			}

			string botsText = string.Join(",", bots.Select(b => b.BotName));
			string response = $"Now playing: {gamesInfo} on {botsText}";
			LogGenericDebug($"Sending response: {response}");

			// Use the first bot in the list to format the response, or the current bot if only one target
			Bot responseBot = bots.Count == 1 ? bots.First() : bot;
			return responseBot.Commands.FormatBotResponse(response);
		} catch (Exception ex) {
			LogGenericError($"PlayFor command failed: {ex}");
			return bot.Commands.FormatBotResponse($"Command failed: {ex.Message}");
		}
	}
}
