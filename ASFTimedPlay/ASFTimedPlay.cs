using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Composition;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using JetBrains.Annotations;

namespace ASFTimedPlay;

#pragma warning disable CA1812 // ASF uses this class during runtime
[Export(typeof(IPlugin))]
[UsedImplicitly]
internal sealed class ASFTimedPlay : IGitHubPluginUpdates, IPlugin, IAsyncDisposable, IBotCommand2 {
	public string Name => nameof(ASFTimedPlay);
	public string RepositoryName => "UpDownLeftDie/ASFTimedPlay";
	public Version Version =>
		typeof(ASFTimedPlay).Assembly.GetName().Version
		?? throw new InvalidOperationException(nameof(Version));

	private static ASFTimedPlay? Instance { get; set; }
	private readonly List<Timer> ActiveTimers = [];

	public ASFTimedPlay() => Instance = this;

	public Task OnLoaded() {
		ASF.ArchiLogger.LogGenericInfo($"{Name} has been loaded!");
		return Task.CompletedTask;
	}

	public async ValueTask DisposeAsync() {
		foreach (Timer timer in ActiveTimers) {
			await timer.DisposeAsync().ConfigureAwait(false);
		}
		ActiveTimers.Clear();
		Instance = null;
	}

	public async Task<string?> OnBotCommand(
		Bot bot,
		EAccess access,
		string message,
		string[] args,
		ulong steamID = 0
	) => args.Length == 0
			? null
			: args[0].ToUpperInvariant() switch {
				"PLAYFOR" => await ResponsePlayFor(bot, args).ConfigureAwait(false),
				"RESUME" => await ResponseResume(bot).ConfigureAwait(false),
				_ => null,
			};

	private static async Task<string?> ResponsePlayFor(Bot bot, string[] args) {
		string[] parameters = args.Skip(1).ToArray();
		if (parameters.Length < 2) {
			return bot.Commands.FormatBotResponse(
				"Usage: !playfor [Bots] <AppID1,AppID2,...> <Minutes>"
			);
		}

		// Handle bot selection
		HashSet<Bot>? bots;
		if (uint.TryParse(parameters[0], out _)) {
			// If first parameter is a number, assume it's a game ID and use "ASF" as bot identifier
			bots = Bot.GetBots("ASF");
			// Don't skip parameters since the first one is a game ID
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

		// Clear any existing timers first
		foreach (Timer timer in Instance?.ActiveTimers ?? []) {
			await timer.DisposeAsync().ConfigureAwait(false);
		}
		Instance?.ActiveTimers.Clear();

		// Schedule games for each bot
		foreach (Bot targetBot in bots) {
			async Task scheduleNextGame(int gameIndex) {
				if (gameIndex >= gameIds.Count) {
					ASF.ArchiLogger.LogGenericDebug(
						"All games completed, resuming normal operation"
					);
					_ = targetBot.Actions.Resume();
					return;
				}

				uint playTime = minutes.Count == 1 ? minutes[0] : minutes[gameIndex];
				uint gameId = gameIds[gameIndex];

				Timer? playTimer = null;
				try {
					playTimer = new Timer(
						async state => {
							if (Instance != null) {
								try {
									ASF.ArchiLogger.LogGenericDebug(
										$"Timer fired for game {gameId} after {playTime} minutes"
									);

									if (state is Timer timer) {
										await timer.DisposeAsync().ConfigureAwait(false);
										_ = Instance.ActiveTimers.Remove(timer);
										ASF.ArchiLogger.LogGenericDebug(
											$"Timer disposed, {Instance.ActiveTimers.Count} timers remaining"
										);
									}

									// Schedule the next game when this timer completes
									await scheduleNextGame(gameIndex + 1).ConfigureAwait(false);
								} catch (Exception ex) {
									ASF.ArchiLogger.LogGenericError(
										$"Error in timer callback: {ex}"
									);
								}
							}
						},
						null,
						TimeSpan.FromMinutes(playTime),
						Timeout.InfiniteTimeSpan
					);

					Instance?.ActiveTimers.Add(playTimer);
					ASF.ArchiLogger.LogGenericDebug(
						$"Timer scheduled for game {gameId} to run for {playTime} minutes"
					);
					playTimer = null; // Ownership transferred to activeTimers

					// If this isn't the first game, start it now
					if (gameIndex > 0) {
						_ = await targetBot
							.Actions.Play([gameId])
							.ConfigureAwait(false);
					}
				} finally {
					if (playTimer != null) {
						await playTimer.DisposeAsync().ConfigureAwait(false);
					}
				}
			}

			// Start the sequence with the first game
			if (gameIds.Count > 0) {
				ASF.ArchiLogger.LogGenericDebug($"Starting first game: {gameIds[0]}");
				_ = await targetBot
					.Actions.Play([gameIds[0]])
					.ConfigureAwait(false);
				await scheduleNextGame(0).ConfigureAwait(false);
			}
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
		string botsInfo = bots.Count == 1 ? "bot" : $"{bots.Count} bots";

		return bot.Commands.FormatBotResponse($"Now playing {gamesInfo} on {botsInfo}.");
	}

	private async Task<string?> ResponseResume(Bot bot) {
		// Clear only timers for the specified bot
		List<Timer> timersToRemove = ActiveTimers.Where(timer => timer.GetType().GetField("bot")?.GetValue(timer) as Bot == bot).ToList();
		foreach (Timer timer in timersToRemove) {
			await timer.DisposeAsync().ConfigureAwait(false);
			_ = ActiveTimers.Remove(timer);
		}
		return bot.Commands.FormatBotResponse("Cleared all scheduled games.");
	}

	public void Dispose() {
		foreach (Timer timer in ActiveTimers) {
			timer.Dispose();
		}
		ActiveTimers.Clear();
		Instance = null;
	}
}
#pragma warning restore CA1812 // ASF uses this class during runtime
