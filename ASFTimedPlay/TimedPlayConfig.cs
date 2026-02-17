using System;
using System.Collections.Generic;
using JetBrains.Annotations;

namespace ASFTimedPlay;

[UsedImplicitly]
internal sealed class TimedPlayConfig {
	public Dictionary<string, TimedPlayEntry> TimedPlayGames { get; set; } = [];
}

internal sealed class TimedPlayEntry {
	public Dictionary<uint, uint> GameMinutes { get; set; } = [];
	public HashSet<uint> IdleGameIds { get; set; } = [];
	public DateTime LastUpdate { get; set; }
	public bool SequentialMode { get; set; }
}
