using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using ArchiSteamFarm.Steam;

namespace ASFTimedPlay.Commands;

internal static class ResumeCommand {
	public static async Task<string?> Response(Bot bot, string[] args) {
		string[] parameters = args.Skip(1).ToArray();

		// Handle bot selection
		HashSet<Bot>? bots;
		if (parameters.Length > 0) {
			bots = Bot.GetBots(parameters[0]);
			if (bots == null || bots.Count == 0) {
				return bot.Commands.FormatBotResponse("No valid bots found!");
			}
		} else {
			bots = [bot];
		}

		await ASFTimedPlay.ConfigLock.WaitAsync().ConfigureAwait(false);
		try {
			foreach (Bot targetBot in bots) {
				// Clear idle module
				if (
					ASFTimedPlay.BotIdleModules.TryGetValue(
						targetBot,
						out ASFTimedPlay.IdleModule? module
					)
				) {
					module.StopIdling();
					_ = ASFTimedPlay.BotIdleModules.Remove(targetBot);
				}

				// Clear PlayFor entries
				if (ASFTimedPlay.Config?.PlayForGames.Remove(targetBot.BotName) == true) {
					await ASFTimedPlay.SaveConfig().ConfigureAwait(false);
				}
			}
		} finally {
			_ = ASFTimedPlay.ConfigLock.Release();
		}

		string botsText = string.Join(",", bots.Select(b => b.BotName));
		return bot.Commands.FormatBotResponse(
			$"Cleared all idle and PlayFor games for {botsText}."
		);
	}
}
