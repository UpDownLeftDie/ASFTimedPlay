using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading; // Add for Timer
using System.Threading.Tasks;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.Plugins.Interfaces;
using ArchiSteamFarm.Steam;
using JetBrains.Annotations;

namespace ASFTimedPlay;

#pragma warning disable CA1812 // ASF uses this class during runtime
[UsedImplicitly]
internal sealed class ASFTimedPlay : IGitHubPluginUpdates, IPlugin, IAsyncDisposable, IBotCommand2
{
	public string Name => nameof(ASFTimedPlay);
	public string RepositoryName => "UpDownLeftDie/ASFTimedPlay";
	public Version Version =>
		typeof(ASFTimedPlay).Assembly.GetName().Version
		?? throw new InvalidOperationException(nameof(Version));

	private static ASFTimedPlay? Instance { get; set; }
	private readonly List<Timer> activeTimers = new();

	public ASFTimedPlay()
	{
		Instance = this;
	}

	public Task OnLoaded()
	{
		ASF.ArchiLogger.LogGenericInfo($"{Name} has been loaded!");
		return Task.CompletedTask;
	}

	public async ValueTask DisposeAsync()
	{
		foreach (Timer timer in activeTimers)
		{
			await timer.DisposeAsync().ConfigureAwait(false);
		}
		activeTimers.Clear();
		Instance = null;
	}

	public async Task<string?> OnBotCommand(
		Bot bot,
		EAccess access,
		string message,
		string[] args,
		ulong steamID = 0
	)
	{
		if (args.Length == 0)
		{
			return null;
		}

		return args[0].ToUpperInvariant() switch
		{
			"PLAYFOR" => await ResponsePlayFor(bot, args).ConfigureAwait(false),
			"RESUME" => await ResponseResume(bot).ConfigureAwait(false),
			_ => null,
		};
	}

	private static async Task<string?> ResponsePlayFor(Bot bot, string[] args)
	{
		string[] parameters = args.Skip(1).ToArray();
		if (parameters.Length < 2)
		{
			return bot.Commands.FormatBotResponse(
				"Usage: !playfor [Bots] <AppID1,AppID2,...> <Minutes>"
			);
		}

		// Handle bot selection
		HashSet<Bot>? bots;
		if (uint.TryParse(parameters[0], out _))
		{
			// If first parameter is a number, assume it's a game ID and use "ASF" as bot identifier
			bots = Bot.GetBots("ASF");
			// Don't skip parameters since the first one is a game ID
		}
		else
		{
			bots = Bot.GetBots(parameters[0]);
			if (bots != null && bots.Count > 0)
			{
				parameters = parameters.Skip(1).ToArray();
			}
			else
			{
				bots = new HashSet<Bot> { bot };
			}
		}

		if (bots == null || bots.Count == 0)
		{
			return bot.Commands.FormatBotResponse("No bots found!");
		}

		// Parse game IDs
		List<uint> gameIds = new();
		string[] gameStrings = parameters[0].Split(',');
		foreach (string game in gameStrings)
		{
			if (!uint.TryParse(game, out uint gameId))
			{
				return bot.Commands.FormatBotResponse($"Invalid game ID: {game}");
			}
			gameIds.Add(gameId);
		}

		// Parse minutes
		List<uint> minutes = new();
		string[] minuteStrings = parameters[1].Split(',');
		foreach (string min in minuteStrings)
		{
			if (!uint.TryParse(min, out uint minuteValue) || minuteValue == 0)
			{
				return bot.Commands.FormatBotResponse("Please provide valid numbers of minutes!");
			}
			minutes.Add(minuteValue);
		}

		// Validate input combinations
		if (minutes.Count != 1 && minutes.Count != gameIds.Count)
		{
			return bot.Commands.FormatBotResponse(
				"Must enter single number of minutes for all games or list of minutes for each game!"
			);
		}

		// Clear any existing timers first
		foreach (Timer timer in Instance?.activeTimers ?? new List<Timer>())
		{
			await timer.DisposeAsync().ConfigureAwait(false);
		}
		Instance?.activeTimers.Clear();

		// Schedule games for each bot
		foreach (Bot targetBot in bots)
		{
			async Task ScheduleNextGame(int gameIndex)
			{
				if (gameIndex >= gameIds.Count)
				{
					ASF.ArchiLogger.LogGenericDebug(
						"All games completed, resuming normal operation"
					);
					targetBot.Actions.Resume();
					return;
				}

				uint playTime = minutes.Count == 1 ? minutes[0] : minutes[gameIndex];
				uint gameId = gameIds[gameIndex];

				Timer? playTimer = null;
				try
				{
					playTimer = new Timer(
						async state =>
						{
							if (Instance != null)
							{
								try
								{
									ASF.ArchiLogger.LogGenericDebug(
										$"Timer fired for game {gameId} after {playTime} minutes"
									);

									if (state is Timer timer)
									{
										await timer.DisposeAsync().ConfigureAwait(false);
										Instance.activeTimers.Remove(timer);
										ASF.ArchiLogger.LogGenericDebug(
											$"Timer disposed, {Instance.activeTimers.Count} timers remaining"
										);
									}

									// Schedule the next game when this timer completes
									await ScheduleNextGame(gameIndex + 1).ConfigureAwait(false);
								}
								catch (Exception ex)
								{
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

					Instance?.activeTimers.Add(playTimer);
					ASF.ArchiLogger.LogGenericDebug(
						$"Timer scheduled for game {gameId} to run for {playTime} minutes"
					);
					playTimer = null; // Ownership transferred to activeTimers

					// If this isn't the first game, start it now
					if (gameIndex > 0)
					{
						await targetBot
							.Actions.Play(new HashSet<uint> { gameId })
							.ConfigureAwait(false);
					}
				}
				finally
				{
					if (playTimer != null)
					{
						await playTimer.DisposeAsync().ConfigureAwait(false);
					}
				}
			}

			// Start the sequence with the first game
			if (gameIds.Count > 0)
			{
				ASF.ArchiLogger.LogGenericDebug($"Starting first game: {gameIds[0]}");
				await targetBot
					.Actions.Play(new HashSet<uint> { gameIds[0] })
					.ConfigureAwait(false);
				await ScheduleNextGame(0).ConfigureAwait(false);
			}
		}

		// Format response message
		string gamesInfo = string.Join(
			", ",
			gameIds.Select(
				(id, index) =>
				{
					uint duration = minutes.Count == 1 ? minutes[0] : minutes[index];
					return $"game {id} for {duration} minutes";
				}
			)
		);
		string botsInfo = bots.Count == 1 ? "bot" : $"{bots.Count} bots";

		return bot.Commands.FormatBotResponse($"Now playing {gamesInfo} on {botsInfo}.");
	}

	private async Task<string?> ResponseResume(Bot bot)
	{
		// Clear any existing timers
		foreach (Timer timer in activeTimers)
		{
			await timer.DisposeAsync().ConfigureAwait(false);
		}
		activeTimers.Clear();
		return null;
	}

	public void Dispose()
	{
		foreach (Timer timer in activeTimers)
		{
			timer.Dispose();
		}
		activeTimers.Clear();
		Instance = null;
	}
}
#pragma warning restore CA1812 // ASF uses this class during runtime
