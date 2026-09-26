namespace TransGo.Core.Runtime;

/// <summary>
/// Helpers for constructing POSIX shell commands.
/// </summary>
public static class PosixShell
{
    public static string QuoteArgument(
        string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return "'" +
            value.Replace(
                "'",
                "'\"'\"'",
                StringComparison.Ordinal) +
            "'";
    }
}
