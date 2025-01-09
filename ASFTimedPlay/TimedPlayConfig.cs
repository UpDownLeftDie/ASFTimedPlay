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
	public bool Enabled { get; private set; } = true;

	[JsonRequired]
	public Dictionary<string, HashSet<uint>> IdleGames { get; set; } = [];

	[JsonRequired]
	public Dictionary<string, List<PlayForEntry>> PlayForGames { get; set; } = [];
}

internal sealed class PlayForEntry {
	[JsonRequired]
	public HashSet<uint> GameIds { get; set; } = [];

	[JsonRequired]
	public List<uint> Minutes { get; set; } = [];

	[JsonRequired]
	public Dictionary<uint, uint> RemainingMinutes { get; set; } = [];

	[JsonRequired]
	public HashSet<uint> IdleAfterCompletion { get; set; } = [];

	[JsonRequired]
	public DateTime LastUpdate { get; set; } = DateTime.UtcNow;
}
