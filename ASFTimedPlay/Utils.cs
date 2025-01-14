using System.Globalization;
using ArchiSteamFarm.Core;
using ArchiSteamFarm.NLog;

namespace ASFTimedPlay;

internal static class Utils {
	internal static ArchiLogger ASFLogger => ASF.ArchiLogger;

	internal static string FormatStaticResponse(string message) => $"<ASFTimedPlay> {message}";

	internal static string FormatStaticResponse(string message, params object?[] args) =>
		FormatStaticResponse(string.Format(CultureInfo.InvariantCulture, message, args));

	internal static void LogGenericInfo(string message) => ASFLogger.LogGenericInfo(FormatStaticResponse(message));
	internal static void LogGenericInfo(string message, params object?[] args) => ASFLogger.LogGenericInfo(FormatStaticResponse(message, args));

	internal static void LogGenericDebug(string message) => ASFLogger.LogGenericDebug(FormatStaticResponse(message));
	internal static void LogGenericDebug(string message, params object?[] args) => ASFLogger.LogGenericDebug(FormatStaticResponse(message, args));

	internal static void LogGenericWarning(string message) => ASFLogger.LogGenericWarning(FormatStaticResponse(message));
	internal static void LogGenericWarning(string message, params object?[] args) => ASFLogger.LogGenericWarning(FormatStaticResponse(message, args));

	internal static void LogGenericError(string message) => ASFLogger.LogGenericError(FormatStaticResponse(message));
	internal static void LogGenericError(string message, params object?[] args) => ASFLogger.LogGenericError(FormatStaticResponse(message, args));
}
