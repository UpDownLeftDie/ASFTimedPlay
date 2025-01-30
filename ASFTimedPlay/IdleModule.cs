using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ArchiSteamFarm.Steam;
using static ASFTimedPlay.Utils;

namespace ASFTimedPlay;

internal sealed class IdleModule : IDisposable {
	private readonly Bot Bot;
	public string Name => nameof(IdleModule);
	public Version Version =>
			typeof(ASFTimedPlay).Assembly.GetName().Version
			?? throw new InvalidOperationException(nameof(Version));
	private HashSet<uint> IdleGameId = [];
	private readonly SemaphoreSlim IdleLock = new(1, 1);

	internal IdleModule(Bot bot) => Bot = bot ?? throw new ArgumentNullException(nameof(bot));

	public void SetIdleGames(uint gameId) {
		IdleGameId = [gameId];
		_ = TryIdleGames();
	}

	public void StopIdling() => IdleGameId = [];

	private async Task TryIdleGames() {
		if (IdleGameId.Count == 0) {
			return;
		}

		await IdleLock.WaitAsync().ConfigureAwait(false);

		try {
			// Don't interfere with ASF operations
			if (
					!Bot.IsConnectedAndLoggedOn
					|| Bot.BotConfig.GamesPlayedWhileIdle.Count > 0
					|| Bot.CardsFarmer.CurrentGamesFarmingReadOnly.Count > 0
					|| Bot.GamesToRedeemInBackgroundCount > 0
			) {
				return;
			}

			_ = await Bot.Actions.Play(IdleGameId).ConfigureAwait(false);
			LogGenericDebug(
					$"Resumed idling game {string.Join(",", IdleGameId)} for {Bot.BotName}"
			);
		} finally {
			_ = IdleLock.Release();
		}
	}

	public static Task<bool> OnGamesPrioritized() => Task.FromResult(true);

	public async Task OnGamesPrioritizedFinished() =>
			// When ASF is done with its operations, try to resume idling
			await TryIdleGames().ConfigureAwait(false);

	public void Dispose() {
		IdleLock.Dispose();
		GC.SuppressFinalize(this);
	}
}
