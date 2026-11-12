using System;
using Xunit;

namespace ASFTimedPlay.Tests;

public class DurationParserTests {
	[Theory]
	[InlineData("30", 30u)]
	[InlineData("2000", 2000u)]
	[InlineData("8:30", 510u)]
	[InlineData("1:9:20", 2000u)] // DD:HH:MM → 1d 9h 20m
	[InlineData("2h", 120u)]
	[InlineData("45m", 45u)]
	[InlineData("2h30m", 150u)]
	[InlineData("1d", 1440u)]
	[InlineData("1d 2h", 1560u)]
	[InlineData("10h45m", 645u)]
	public void TryParseValidDurations(string input, uint expectedMinutes) {
		Assert.True(DurationParser.TryParse(input, out uint minutes));
		Assert.Equal(expectedMinutes, minutes);
	}

	[Theory]
	[InlineData("")]
	[InlineData(" ")]
	[InlineData("0")]
	[InlineData("invalid")]
	[InlineData("*")]
	[InlineData("-5")]
	[InlineData("h30")]
	public void TryParseInvalidDurations(string input) {
		Assert.False(DurationParser.TryParse(input, out _));
	}
}
