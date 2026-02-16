using System;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ASFTimedPlay;

/// <summary>
/// Parses duration strings into total minutes. No seconds — minutes only.
/// Supports: plain minutes (MM), HH:MM, DD:HH:MM, and suffix form (10h 45m, 1d 2h).
/// </summary>
internal static partial class DurationParser {
	// Match number + unit: e.g. "10h", "45m", "1d", "2h30m"
	[GeneratedRegex(@"(\d+)\s*([dDhHmM])\b")]
	private static partial Regex UnitPattern();

	/// <summary>
	/// Try to parse a duration string into total minutes.
	/// </summary>
	/// <param name="input">Trimmed input: MM, HH:MM, DD:HH:MM, or 10h 45m / 1d 2h style.</param>
	/// <param name="totalMinutes">Total duration in minutes (0 if invalid).</param>
	/// <returns>True if parsing succeeded.</returns>
	public static bool TryParse(string input, out uint totalMinutes) {
		totalMinutes = 0;
		if (string.IsNullOrWhiteSpace(input)) {
			return false;
		}

		string s = input.Trim();

		// Suffix form: contains d, h, or m as unit (e.g. 10h 45m, 1d2h)
		if (UnitPattern().IsMatch(s)) {
			return TryParseUnitForm(s, out totalMinutes);
		}

		// Colon form: MM, HH:MM, or DD:HH:MM (all integer parts, minutes only)
		if (s.Contains(':', StringComparison.Ordinal)) {
			return TryParseColonForm(s, out totalMinutes);
		}

		// Plain number = minutes
		if (uint.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out uint m) && m > 0) {
			totalMinutes = m;
			return true;
		}

		return false;
	}

	private static bool TryParseColonForm(string s, out uint totalMinutes) {
		totalMinutes = 0;
		string[] parts = s.Split(':');
		if (parts.Length is < 1 or > 3) {
			return false;
		}

		// All segments must be non-negative integers
		var values = new uint[parts.Length];
		for (int i = 0; i < parts.Length; i++) {
			if (!uint.TryParse(parts[i].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out values[i])) {
				return false;
			}
		}

		// MM
		if (parts.Length == 1) {
			if (values[0] == 0) return false;
			totalMinutes = values[0];
			return true;
		}

		// HH:MM
		if (parts.Length == 2) {
			totalMinutes = values[0] * 60 + values[1];
			return totalMinutes > 0;
		}

		// DD:HH:MM
		totalMinutes = values[0] * 24 * 60 + values[1] * 60 + values[2];
		return totalMinutes > 0;
	}

	private static bool TryParseUnitForm(string s, out uint totalMinutes) {
		totalMinutes = 0;
		uint days = 0, hours = 0, minutes = 0;

		foreach (Match m in UnitPattern().Matches(s)) {
			if (m.Groups.Count != 3 || !uint.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out uint val)) {
				continue;
			}
			switch (m.Groups[2].Value.ToUpperInvariant()[0]) {
				case 'D': days += val; break;
				case 'H': hours += val; break;
				case 'M': minutes += val; break;
			}
		}

		ulong total = (ulong)days * 24 * 60 + (ulong)hours * 60 + minutes;
		if (total == 0 || total > uint.MaxValue) {
			return false;
		}
		totalMinutes = (uint)total;
		return true;
	}
}
