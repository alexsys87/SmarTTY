using SmarTTY.TextEditing;
using System;
using System.Collections.Generic;

namespace SmarTTY.Completion;

/// <summary>What a typed word does to the completion list.</summary>
public enum SuggestionMatchKind
{
    /// <summary>An item is the word or starts with it: it is selected (<see cref="SuggestionMatch.Index"/>) and the list is put back in its order.</summary>
    Item,

    /// <summary>
    /// Items that have the word inside them go on top in the order of <see cref="SuggestionMatch.Matches"/> and the first is selected; the
    /// others are grey.
    /// </summary>
    Filtered,

    /// <summary>Nothing matches: the list is put back in its order and the nearest item before the word is selected (<see cref="SuggestionMatch.Index"/>).</summary>
    Nearest
}

/// <summary>The answer of <see cref="SuggestionListMatch.Find"/>.</summary>
public sealed class SuggestionMatch
{
    public SuggestionMatchKind Kind;

    /// <summary>The item to select, an index of the names as they were given (for <see cref="SuggestionMatchKind.Item"/> and <see cref="SuggestionMatchKind.Nearest"/>).</summary>
    public int Index;

    /// <summary>The indexes of the names that go on top, in their new order (for <see cref="SuggestionMatchKind.Filtered"/>).</summary>
    public List<int> Matches = new List<int>();
}

/// <summary>What a key pressed in the completion list does.</summary>
public enum SuggestionListKeyAction
{
    /// <summary>The list does not take the key.</summary>
    None,

    /// <summary>The list hides.</summary>
    Hide,

    /// <summary>The list hides and the item is accepted with the key.</summary>
    Accept,

    /// <summary>The list hides and a blank is typed into the editor (the item does not continue the typed word).</summary>
    HideAndTypeBlank,

    /// <summary>The key goes to the editor.</summary>
    Forward,

    /// <summary>The key goes to the editor and the first item is selected (Home).</summary>
    ForwardAndSelectFirst,

    /// <summary>The key goes to the editor and the last item is selected (End).</summary>
    ForwardAndSelectLast
}

/// <summary>
/// What the completion list decides about a typed word (which item it selects, which it puts on top: the abbreviations of the capital letters,
/// digits and words of a name, its words, its parts) and about a key. Moved as it was from <c>SuggestionListForm.SelectMatch</c> and
/// <c>SuggestionListView.HandleKey</c> (stage 3D), so that the WPF list decides the same; the views keep the items, the colours and the window.
/// </summary>
public static class SuggestionListMatch
{
    /// <summary>
    /// The item the typed <paramref name="prefix"/> selects among <paramref name="names"/> (the items of the list in the order it shows them);
    /// with <paramref name="allowSubstrings"/> the items that have it inside them go on top when no item starts with it.
    /// </summary>
    public static SuggestionMatch Find(IList<string> names, string prefix, bool allowSubstrings, StringComparer comparer)
    {
        int nearestIdx = 0;
        for (int i = 0; i < names.Count; i++)
        {
            if (comparer.Compare(names[i], prefix) == 0)
            {
                return new SuggestionMatch { Kind = SuggestionMatchKind.Item, Index = i };
            }
        }
        for (int i = 0; i < names.Count; i++)
        {
            string itemText = names[i];
            if (comparer.Compare(itemText, prefix) < 0)
            {
                nearestIdx = i;
            }
            if (itemText.Length >= prefix.Length && comparer.Compare(itemText.Substring(0, prefix.Length), prefix) == 0)
            {
                return new SuggestionMatch { Kind = SuggestionMatchKind.Item, Index = i };
            }
        }
        List<int> matches = new List<int>();
        int bestMatch = -1;
        if (allowSubstrings)
        {
            bestMatch = AddAbbreviationMatches(names, prefix, matches);
            AddSubstringMatches(names, prefix, matches);
        }
        if (bestMatch != -1)
        {
            matches.Remove(bestMatch);
            matches.Insert(0, bestMatch);
        }
        if (matches.Count != 0)
        {
            return new SuggestionMatch { Kind = SuggestionMatchKind.Filtered, Matches = matches };
        }
        return new SuggestionMatch { Kind = SuggestionMatchKind.Nearest, Index = nearestIdx };
    }

    /// <summary>The words of a name: a capital letter or a digit starts one, an underscore ends one (and what follows up to the next underscore is skipped).</summary>
    public static string[] SplitWords(string text)
    {
        int start = 0;
        List<string> words = new List<string>();
        int i;
        for (i = 0; i < text.Length; i++)
        {
            if (char.IsUpper(text[i]) || i == 0 || char.IsDigit(text[i]))
            {
                if (start < i)
                {
                    words.Add(text.Substring(start, i - start));
                }
                start = i;
            }
            else if (text[i] == '_')
            {
                if (start < i)
                {
                    words.Add(text.Substring(start, i - start));
                }
                for (start = i + 1; start < text.Length && text[start] != '_'; start++)
                {
                }
            }
        }
        if (start < i)
        {
            words.Add(text.Substring(start, i - start));
        }
        return words.ToArray();
    }

    private static void AddSubstringMatches(IList<string> names, string filterText, List<int> matches)
    {
        for (int i = 0; i < names.Count; i++)
        {
            if (names[i].IndexOf(filterText) != -1 && !matches.Contains(i))
            {
                matches.Add(i);
            }
        }
        for (int i = 0; i < names.Count; i++)
        {
            if (names[i].IndexOf(filterText, StringComparison.CurrentCultureIgnoreCase) != -1 && !matches.Contains(i))
            {
                matches.Add(i);
            }
        }
    }

    private static int AddAbbreviationMatches(IList<string> names, string prefix, List<int> matches)
    {
        List<int> exactAbbrev = new List<int>();
        List<int> prefixAbbrev = new List<int>();
        List<int> exactWord = new List<int>();
        List<int> prefixWord = new List<int>();
        for (int i = 0; i < names.Count; i++)
        {
            string[] words = SplitWords(names[i]);
            string abbrev = "";
            foreach (string word in words)
            {
                abbrev += char.ToUpper(word[0]);
            }
            foreach (string word in words)
            {
                if (StringComparer.CurrentCultureIgnoreCase.Compare(word, prefix) == 0)
                {
                    exactWord.Add(i);
                }
                if (word.StartsWith(prefix, StringComparison.CurrentCultureIgnoreCase))
                {
                    prefixWord.Add(i);
                }
            }
            if (abbrev == prefix)
            {
                exactAbbrev.Add(i);
            }
            if (abbrev.StartsWith(prefix))
            {
                prefixAbbrev.Add(i);
            }
        }
        int bestMatch = -1;
        if (exactAbbrev.Count > 0)
        {
            bestMatch = exactAbbrev[0];
        }
        else if (exactWord.Count > 0)
        {
            bestMatch = exactWord[0];
        }
        foreach (int item in exactAbbrev)
        {
            if (!matches.Contains(item))
            {
                matches.Add(item);
            }
        }
        foreach (int item in prefixAbbrev)
        {
            if (!matches.Contains(item))
            {
                matches.Add(item);
            }
        }
        foreach (int item in exactWord)
        {
            if (!matches.Contains(item))
            {
                matches.Add(item);
            }
        }
        foreach (int item in prefixWord)
        {
            if (!matches.Contains(item))
            {
                matches.Add(item);
            }
        }
        return bestMatch;
    }

    /// <summary>
    /// What a key pressed in the list does. <paramref name="item"/> is the focused item (null: none); <paramref name="currentText"/> gives the
    /// typed word and is asked only for a blank with an item.
    /// </summary>
    public static SuggestionListKeyAction MapKey(VirtualKey key, ISuggestion item, Func<string> currentText)
    {
        switch (key)
        {
            case VirtualKey.Menu:
            case VirtualKey.Escape:
                return SuggestionListKeyAction.Hide;
            case VirtualKey.Tab:
            case VirtualKey.Return:
            case VirtualKey.Space:
                if (item != null)
                {
                    if (key == VirtualKey.Space)
                    {
                        string text = currentText();
                        if (text.Length > item.DisplayName.Length || item.DisplayName.Substring(0, text.Length) != text)
                        {
                            return SuggestionListKeyAction.HideAndTypeBlank;
                        }
                    }
                    return SuggestionListKeyAction.Accept;
                }
                break;
        }
        switch (key)
        {
            case VirtualKey.Home:
                return SuggestionListKeyAction.ForwardAndSelectFirst;
            case VirtualKey.End:
                return SuggestionListKeyAction.ForwardAndSelectLast;
            case VirtualKey.Left:
            case VirtualKey.Right:
            case VirtualKey.Delete:
            case VirtualKey.Tab:
                return SuggestionListKeyAction.Forward;
        }
        return SuggestionListKeyAction.None;
    }
}
