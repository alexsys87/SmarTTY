namespace SmarTTY.Completion;

/// <summary>
/// The prefix rule of the completion lists, kept free of any UI dependency so the test harness can check it.
/// A null prefix matches everything; a name shorter than the prefix can never start with it.
/// </summary>
public static class SuggestionFilter
{
    /// <summary>Whether <paramref name="name"/> starts with <paramref name="prefix"/>.</summary>
    public static bool MatchesPrefix(string name, string prefix)
    {
        if (prefix == null)
        {
            return true;
        }
        return name != null && name.Length >= prefix.Length && name.Substring(0, prefix.Length) == prefix;
    }
}
