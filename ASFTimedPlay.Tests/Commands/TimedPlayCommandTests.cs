using System;
using System.Collections.Generic;
using System.Reflection;
using ArchiSteamFarm.Steam;
using ArchiSteamFarm.Steam.Interaction;
using ASFTimedPlay.Commands;
using Moq;
using Xunit;

namespace ASFTimedPlay.Tests.Commands;

public class TimedPlayCommandTests {
	private readonly Mock<Bot> _mockBot;
	private readonly Mock<Actions> _mockActions;

	public TimedPlayCommandTests() {
		_mockBot = new Mock<Bot>("TestBot");
		_mockActions = new Mock<Actions>(_mockBot.Object);
		_ = _mockBot.Setup(b => b.Actions).Returns(_mockActions.Object);

		// Setup basic command formatting
		_ = _mockBot.Setup(b => b.Commands.FormatBotResponse(It.IsAny<string>()))
				.Returns<string>(s => $"<{_mockBot.Object.BotName}> {s}");

		// Initialize plugin config via reflection (plugin and config types are internal)
		Assembly asm = typeof(TimedPlayCommand).Assembly;
		Type? configType = asm.GetType("ASFTimedPlay.TimedPlayConfig");
		Type? entryType = asm.GetType("ASFTimedPlay.TimedPlayEntry");
		if (configType != null && entryType != null) {
			object config = Activator.CreateInstance(configType)!;
			Type dictType = typeof(Dictionary<,>).MakeGenericType(typeof(string), entryType);
			object dict = Activator.CreateInstance(dictType)!;
			configType.GetProperty("TimedPlayGames")!.SetValue(config, dict);
			Type? pluginType = asm.GetType("ASFTimedPlay.ASFTimedPlay");
			pluginType?.GetProperty("Config", BindingFlags.Public | BindingFlags.Static)?.SetValue(null, config);
		}
	}

	[Fact]
	public async Task ResponseNoArgsReturnsUsage() {
		string[] args = ["timedplay"];
		string? response = await TimedPlayCommand.Response(_mockBot.Object, args);
		Assert.Contains("Usage: !timedplay", response, StringComparison.Ordinal);
		Assert.Contains("<TestBot>", response, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ResponseStopWithNoActiveGamesReturnsNoActiveGamesFound() {
		string[] args = ["timedplay", "stop"];
		string? response = await TimedPlayCommand.Response(_mockBot.Object, args);
		Assert.Contains("No active games found", response, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ResponseStopWithActiveEntryStopsTimedPlayGames() {
		// Seed config via reflection so there is an active timed play entry to stop
		Assembly asm = typeof(TimedPlayCommand).Assembly;
		Type? entryType = asm.GetType("ASFTimedPlay.TimedPlayEntry");
		Type? pluginType = asm.GetType("ASFTimedPlay.ASFTimedPlay");
		if (entryType == null || pluginType == null) {
			return;
		}
		object? config = pluginType.GetProperty("Config", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
		if (config == null) {
			return;
		}
		object entry = Activator.CreateInstance(entryType)!;
		Type dictType = typeof(Dictionary<,>).MakeGenericType(typeof(uint), typeof(uint));
		object gameMinutes = Activator.CreateInstance(dictType)!;
		dictType.GetMethod("Add", [typeof(uint), typeof(uint)])!.Invoke(gameMinutes, [440u, 60u]);
		entryType.GetProperty("GameMinutes")!.SetValue(entry, gameMinutes);
		entryType.GetProperty("IdleGameIds")!.SetValue(entry, new HashSet<uint>());
		entryType.GetProperty("LastUpdate")!.SetValue(entry, DateTime.UtcNow);
		config.GetType().GetProperty("TimedPlayGames")!.GetValue(config);
		object? gamesDict = config.GetType().GetProperty("TimedPlayGames")!.GetValue(config);
		gamesDict!.GetType().GetMethod("Add", [typeof(string), entryType])!.Invoke(gamesDict, [_mockBot.Object.BotName, entry]);

		string[] args = ["timedplay", "stop"];
		_ = _mockBot.Setup(b => b.Actions.Play(It.IsAny<IReadOnlyCollection<uint>>()))
				.ReturnsAsync((true, "Stopped playing"));

		string? response = await TimedPlayCommand.Response(_mockBot.Object, args);

		_mockBot.Verify(b => b.Actions.Play(It.Is<IReadOnlyCollection<uint>>(g => !g.Any())), Times.Once);
		Assert.Contains("Stopped timed play", response, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ResponseSingleGameAndMinutesStartsPlaying() {
		string[] args = ["timedplay", "440", "60"];
		_ = _mockBot.Setup(b => b.Actions.Play(It.IsAny<IReadOnlyCollection<uint>>()))
				.ReturnsAsync((true, "Started playing"));

		string? response = await TimedPlayCommand.Response(_mockBot.Object, args);

		Assert.Contains("Now playing: 440 for 60 minutes", response, StringComparison.Ordinal);
		Assert.Contains(_mockBot.Object.BotName, response, StringComparison.Ordinal);

		// Verify game was added to config via reflection
		Assembly asm = typeof(TimedPlayCommand).Assembly;
		Type? pluginType = asm.GetType("ASFTimedPlay.ASFTimedPlay");
		object? config = pluginType?.GetProperty("Config", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
		object? games = config?.GetType().GetProperty("TimedPlayGames")?.GetValue(config);
		bool hasKey = (bool)games!.GetType().GetMethod("ContainsKey", [typeof(string)])!.Invoke(games, [_mockBot.Object.BotName])!;
		Assert.True(hasKey);
	}

	[Fact]
	public async Task ResponseMultipleGamesOneMinuteStartsPlayingAll() {
		string[] args = ["timedplay", "440,570", "30"];
		_ = _mockBot.Setup(b => b.Actions.Play(It.IsAny<IReadOnlyCollection<uint>>()))
				.ReturnsAsync((true, "Started playing"));

		string? response = await TimedPlayCommand.Response(_mockBot.Object, args);

		Assert.Contains("Now playing: 440 for 30 minutes, 570 for 30 minutes", response, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ResponseGameWithIdleMarkerSetsIdleGame() {
		string[] args = ["timedplay", "440,570", "30,*"];
		_ = _mockBot.Setup(b => b.Actions.Play(It.IsAny<IReadOnlyCollection<uint>>()))
				.ReturnsAsync((true, "Started playing"));

		string? response = await TimedPlayCommand.Response(_mockBot.Object, args);

		Assert.Contains("Now playing: 440 for 30 minutes", response, StringComparison.Ordinal);
		Assert.Contains("570 will be idled after completion", response, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ResponseInvalidGameIdReturnsError() {
		string[] args = ["timedplay", "invalid", "30"];
		string? response = await TimedPlayCommand.Response(_mockBot.Object, args);
		Assert.Contains("Invalid game ID: invalid", response, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ResponseInvalidMinutesReturnsError() {
		string[] args = ["timedplay", "440", "invalid"];
		string? response = await TimedPlayCommand.Response(_mockBot.Object, args);
		Assert.Contains("Invalid minutes value: invalid", response, StringComparison.Ordinal);
	}

	[Fact]
	public async Task ResponseMinutesCountMismatchReturnsError() {
		// 2 games but 3 minute values -> mismatch
		string[] args = ["timedplay", "440,570", "30,45,60"];
		string? response = await TimedPlayCommand.Response(_mockBot.Object, args);
		Assert.Contains("Number of minute values must match number of games", response, StringComparison.Ordinal);
	}
}
