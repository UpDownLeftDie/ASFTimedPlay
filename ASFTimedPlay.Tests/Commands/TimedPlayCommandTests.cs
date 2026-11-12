using Xunit;

namespace ASFTimedPlay.Tests.Commands;

/// <summary>
/// Bot is sealed in ASF v7, so Moq cannot substitute it. Command integration tests that need a live
/// Bot belong in a future harness; parsing coverage lives in <see cref="DurationParserTests"/>.
/// </summary>
public class TimedPlayCommandTests {
	[Fact]
	public void DurationParserIsAvailableToCommands() {
		// Sanity check that the internal parser used by TimedPlayCommand stays testable.
		Assert.True(DurationParser.TryParse("1h", out uint minutes));
		Assert.Equal(60u, minutes);
	}
}
