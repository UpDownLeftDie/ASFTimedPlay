using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using JetBrains.Annotations;

namespace ASFTimedPlay;

[SuppressMessage("Performance", "CA1812:Avoid uninstantiated internal classes")]
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
