using System.Collections.Generic;
using System.Text;

namespace OpenClawTray.Chat;

internal static class ClipboardImagePasteDiagnostics
{
    private const int MaxFormats = 24;
    private const int MaxFormatLength = 96;

    public static string FormatAvailableFormats(IReadOnlyList<string>? formats)
    {
        if (formats is null || formats.Count == 0)
            return "(none)";

        var builder = new StringBuilder();
        var count = System.Math.Min(formats.Count, MaxFormats);
        for (var index = 0; index < count; index++)
        {
            if (index > 0)
                builder.Append(", ");

            AppendFormat(builder, formats[index]);
        }

        if (formats.Count > MaxFormats)
            builder.Append($", ... (+{formats.Count - MaxFormats} more)");

        return builder.ToString();
    }

    private static void AppendFormat(StringBuilder builder, string? format)
    {
        if (string.IsNullOrEmpty(format))
        {
            builder.Append("(empty)");
            return;
        }

        var length = System.Math.Min(format.Length, MaxFormatLength);
        for (var index = 0; index < length; index++)
        {
            var character = format[index];
            builder.Append(char.IsControl(character) ? '?' : character);
        }

        if (format.Length > MaxFormatLength)
            builder.Append("...");
    }
}
