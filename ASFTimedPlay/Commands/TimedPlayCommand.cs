using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Globalization;
using static ASFTimedPlay.Utils;
using ArchiSteamFarm.Steam;

namespace ASFTimedPlay.Commands;

public static class TimedPlayCommand {
	public static async Task<string?> Response(Bot bot, string[] args) {
		ArgumentNullException.ThrowIfNull(bot);
		try {
			LogGenericDebug($"TimedPlay command started with args: {string.Join(" ", args)}");

			string[] parameters = [.. args.Skip(1)];
			if (parameters.Length == 0) {
				return bot.Commands.FormatBotResponse(
					"Usage: !timedplay [Bots] <AppID1,AppID2,...> <Duration1,Duration2,...>\n" +
					"Durations: MM (minutes), HH:MM, DD:HH:MM, or 10h45m / 1d 2h\n" +
					"Examples:\n" +
					"  !timedplay 440 2000 → 440 for 2000 min (or use 33:20 or 1:9:20)\n" +
					"  !timedplay 440,100 8:30,70 → 440 for 8h30m, 100 for 70 min\n" +
					"  !timedplay 440,570 2h,45,* → Play 440/570 for time, then idle both\n" +
					"Use: \"!timedplay stop\" / \"!timedplay stopall\" / \"!timedplay status\"\n" +
					"Max 32 games. Extra games use the last duration.\n" +
					"Aliases: !tp, !pf"
				);
			}

			// Handle stop command
			if (parameters[0].Equals("stop", StringComparison.OrdinalIgnoreCase)) {
				return await CommandHelpers.HandleStopCommand(bot, stopIdleGame: false, stopTimedPlayGames: true).ConfigureAwait(false);
			}

			// Handle stopall command
			if (parameters[0].Equals("stopall", StringComparison.OrdinalIgnoreCase)) {
				return await CommandHelpers.HandleStopCommand(bot, stopIdleGame: true, stopTimedPlayGames: true).ConfigureAwait(false);
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

			// Parse durations (MM, HH:MM, DD:HH:MM, or 10h 45m / 1d 2h). Extra games use the last value.
			List<uint> minutes = [];
			if (minuteStrings.Length == 1 && gameIds.Count > 0) {
				if (!DurationParser.TryParse(minuteStrings[0], out uint minuteValue) || minuteValue == 0) {
					return bot.Commands.FormatBotResponse($"Invalid duration: {minuteStrings[0]} (use MM, HH:MM, DD:HH:MM, or 10h45m)");
				}
				minutes.AddRange(Enumerable.Repeat(minuteValue, gameIds.Count));
			} else {
				if (minuteStrings.Length == 0) {
					return bot.Commands.FormatBotResponse("Please provide at least one duration value.");
				}
				foreach (string min in minuteStrings) {
					if (!DurationParser.TryParse(min, out uint minuteValue) || minuteValue == 0) {
						return bot.Commands.FormatBotResponse($"Invalid duration: {min} (use MM, HH:MM, DD:HH:MM, or 10h45m)");
					}
					minutes.Add(minuteValue);
				}
				// Pad with last minute value for any extra games
				while (minutes.Count < gameIds.Count) {
					minutes.Add(minutes[^1]);
				}
				// If more minute values than games, use only the first gameIds.Count
				if (minutes.Count > gameIds.Count) {
					minutes = minutes.Take(gameIds.Count).ToList();
				}
			}

			foreach (Bot targetBot in bots) {
				TimedPlayEntry entry = new() {
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

					ASFTimedPlay.Config.TimedPlayGames[targetBot.BotName] = entry;

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

			string responseMessage = $"Now playing: {gamesInfo}";
			LogGenericDebug($"Sending response: {responseMessage}");

			// Match ASF built-in commands: one line per target bot with that bot's name in angle brackets
			return string.Join(
				Environment.NewLine,
				bots.Select(b => b.Commands.FormatBotResponse(responseMessage))
			);
		} catch (Exception ex) {
			LogGenericError($"TimedPlay command failed: {ex}");
			return bot.Commands.FormatBotResponse($"Command failed: {ex.Message}");
		}
	}
}
