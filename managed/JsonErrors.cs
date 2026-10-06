using System.Text.Json;

namespace DeadworksManaged;

/// <summary>
/// Parse errors in hand-edited config files, worded for the person fixing them: System.Text.Json counts lines and
/// columns from 0 and tacks on its internal path, so "LineNumber: 20" meant the 21st line.
/// </summary>
internal static class JsonErrors
{
    /// <summary>"players.jsonc line 21, column 1: 'o' is invalid after a single JSON value. Expected end of data"</summary>
    public static string Describe(string file, Exception ex)
    {
        if (ex is not JsonException { LineNumber: { } line } json)
            return $"{file}: {ex.Message.TrimEnd('.')}";
        var message = json.Message;
        var tail = message.IndexOf(" Path: ", StringComparison.Ordinal);
        if (tail >= 0)
            message = message[..tail];
        var column = json.BytePositionInLine is { } position ? $", column {position + 1}" : "";
        return $"{file} line {line + 1}{column}: {message.TrimEnd().TrimEnd('.')}";
    }
}
