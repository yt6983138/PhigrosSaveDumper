using System.Diagnostics.CodeAnalysis;
using System.Text.Json;

namespace PhigrosSaveDumper;
internal static class Extension
{
	private static readonly JsonSerializerOptions _options = new()
	{
		WriteIndented = true
	};

	internal static string ToJson<T>(this T obj)
	{
		return JsonSerializer.Serialize(obj, _options);
	}

	[return: NotNull]
	internal static T EnsureNotNull<T>(this T? obj) where T : class
	{
		if (obj is null)
			throw new ArgumentNullException(nameof(obj));
		return obj;
	}
	[return: NotNull]
	internal static T EnsureNotNull<T>(this T? obj) where T : struct
	{
		if (obj is null)
			throw new ArgumentNullException(nameof(obj));
		return obj.Value;
	}
}
