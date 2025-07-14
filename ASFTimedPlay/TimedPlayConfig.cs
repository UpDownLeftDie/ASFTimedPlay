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
	public HashSet<uint> IdleGameIds { get; set; } = [];

	[JsonRequired]
	public DateTime LastUpdate { get; set; }

	// Handle single IdleGameId
	// [JsonIgnore]
	// public uint IdleGameId {
	// 	get => IdleGameIds.FirstOrDefault();
	// 	set {
	// 		IdleGameIds.Clear();
	// 		if (value > 0) {
	// 			IdleGameIds.Add(value);
	// 		}
	// 	}
	// }
}
