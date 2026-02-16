using System;
using System.Collections.Generic;
using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace ASFTimedPlay;

[UsedImplicitly]
internal sealed class TimedPlayConfig {
	[JsonRequired]
	public Dictionary<string, TimedPlayEntry> TimedPlayGames { get; set; } = [];
}

internal sealed class TimedPlayEntry {
	[JsonRequired]
	public Dictionary<uint, uint> GameMinutes { get; set; } = [];

	[JsonRequired]
	public HashSet<uint> IdleGameIds { get; set; } = [];

	[JsonRequired]
	public DateTime LastUpdate { get; set; }
}
