namespace DurableOutbox;

/// <summary>Turns error text into what is stored and logged: through the service's filter once, then cut.</summary>
internal static class ErrorText
{
    public const int MaxLength = 2000;
    public const string TimedOut = "timed out";
    public const string GiveUpHookFailed = "The give-up hook failed: ";
    public const string CouldNotRedact = "The error text could not be redacted.";

    public static string From(string text, Func<string, string> redact)
    {
        string redacted;
        try
        {
            redacted = redact(text) ?? string.Empty;
        }
        catch (Exception)
        {
            // Never fall back to the original text: it is what the filter exists to keep out.
            return CouldNotRedact;
        }

        if (redacted.Length <= MaxLength)
        {
            return redacted;
        }

        var length = char.IsHighSurrogate(redacted[MaxLength - 1]) ? MaxLength - 1 : MaxLength;
        return redacted[..length];
    }
}
