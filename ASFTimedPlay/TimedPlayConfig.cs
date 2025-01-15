using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace ASFTimedPlay;

[UsedImplicitly]
internal sealed class TimedPlayConfig {
	[JsonRequired]
	public Dictionary<string, PlayForEntry> PlayForGames { get; set; } = [];
}

internal sealed class PlayForEntry {
	[JsonRequired]
	public Dictionary<uint, uint> GameMinutes { get; set; } = [];

	[JsonRequired]
	public uint IdleGameId { get; set; }

	[JsonRequired]
	public DateTime LastUpdate { get; set; }
}
