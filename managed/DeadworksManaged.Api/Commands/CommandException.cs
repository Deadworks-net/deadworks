namespace DeadworksManaged.Api;

/// <summary>
/// Thrown from a <see cref="CommandAttribute"/> handler, or a <see cref="CommandConverters"/> parser, to stop the
/// command and send <see cref="Exception.Message"/> to the caller. Nothing is logged: it's an answer, not an error.
/// Any other exception is logged with its stack trace, and the caller only hears that the command failed.
/// </summary>
public sealed class CommandException : Exception
{
    /// <param name="message">What the caller sees, e.g. "lapka isn't alive."</param>
    public CommandException(string message) : base(message) { }

    /// <param name="message">What the caller sees.</param>
    /// <param name="inner">The exception that caused it, kept for debugging; the caller doesn't see it.</param>
    public CommandException(string message, Exception inner) : base(message, inner) { }
}
