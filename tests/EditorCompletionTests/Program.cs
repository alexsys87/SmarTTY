using System;
using System.Collections.Generic;
using SmarTTY.CodeEditing;
using SmarTTY.Completion;
using SmarTTY.TextEditing;

// Checks of the completion of a code editor (CodeEditing/EditorCompletion.cs): when the word list opens, what it is told to select, what a
// separator, a digit, Escape and the switches do to it, and when the parameter hint of a call opens, follows the active parameter, is hidden
// and closes. The editor is a fake that types into a real document and logs what it is asked; the list and the hint are fake windows that log
// what the real controllers ask of them. The answers are those of the code of CodeEditorPP before it moved (the completion run of
// tests/TextViewTests drives the real editor).
// Run with: dotnet run --project tests/EditorCompletionTests
internal static class Program
{
    private static int _passed;

    private static void Check(bool condition, string name)
    {
        if (!condition)
        {
            throw new Exception("FAIL: " + name);
        }
        _passed++;
        Console.WriteLine("PASS: " + name);
    }

    private static void Equal<T>(T actual, T expected, string name)
    {
        Check(EqualityComparer<T>.Default.Equals(actual, expected), name + (EqualityComparer<T>.Default.Equals(actual, expected) ? "" : " (expected '" + expected + "', got '" + actual + "')"));
    }

    private const string WordCharacters = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789_";

    private sealed class Popup : ISuggestionPopup
    {
        private readonly Editor _editor;

        private readonly List<ISuggestion> _items;

        private string _prefix = "";

        public Popup(Editor editor, ISuggestionSource source)
        {
            _editor = editor;
            _items = new List<ISuggestion>(source.Items);
            editor.Log.Add("create");
        }

        public bool IsVisible { get; private set; }

        public RgbColor BackColor { set { _editor.Log.Add("back " + value.Argb.ToString("X8")); } }

        public ISuggestion SelectedSuggestion
        {
            get
            {
                foreach (ISuggestion item in _items)
                {
                    if (item.DisplayName.StartsWith(_prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        return item;
                    }
                }
                return _items.Count > 0 ? _items[0] : null;
            }
        }

        public IEnumerable<ISuggestion> Items => _items;

        public void Show(bool autoSize) { IsVisible = true; _editor.Log.Add("show " + autoSize); }

        public void Hide() { IsVisible = false; _editor.Log.Add("hide"); }

        public void SelectMatch(string prefix, bool exactMatch) { _prefix = prefix; _editor.Log.Add("select " + prefix + " " + exactMatch); }

        public void AcceptSelected()
        {
            ISuggestion selected = SelectedSuggestion;
            _editor.Log.Add("accept " + selected.DisplayName);
            IsVisible = false;
            _editor.ReplaceWordAtCaret(selected.DisplayName);
        }

        public void Dispose() { IsVisible = false; _editor.Log.Add("dispose"); }
    }

    private sealed class Hint : IHintView
    {
        private readonly Editor _editor;

        public readonly int Number;

        public bool IsShown;

        public Hint(Editor editor, int number) { _editor = editor; Number = number; }

        public string SignatureText { get; set; }

        public string Description { get; set; }

        public bool ShowDescription { get; set; }

        public int Active = -1;

        public string Signature;

        public bool Visible => IsShown;

        public void SetActiveParameter(int index) { Active = index; _editor.Log.Add("hint" + Number + " active " + index); }

        public void SetSignature(ISignatureInfo signatureInfo)
        {
            Signature = "";
            for (int i = 0; i < signatureInfo.ParameterCount; i++)
            {
                Signature += (i > 0 ? signatureInfo.Separator : "") + signatureInfo.GetParameter(i).Text;
            }
        }

        public void Dismiss() { IsShown = false; _editor.Log.Add("hint" + Number + " dismiss"); }

        public void ShowAt(bool isVertical) { IsShown = true; _editor.Log.Add("hint" + Number + " show"); }

        public void Reposition(bool isVertical) { _editor.Log.Add("hint" + Number + " reposition"); }
    }

    private sealed class Editor : IEditorCompletionHost, ISuggestionPopupFactory
    {
        public readonly List<string> Log = new List<string>();

        public readonly List<string> Lines = new List<string>();

        public readonly List<Hint> Hints = new List<Hint>();

        public Popup Popup;

        public EditorCompletion Completion;

        public bool HasDocument = true;

        public TextDocument.TextPosition Caret;

        public bool LoseFocusOnFocus;

        public int Factories;

        public Editor(params string[] lines)
        {
            Lines.AddRange(lines);
            Completion = new EditorCompletion(this);
        }

        public EditableTextDocument Document => HasDocument ? new EditableTextDocument(Lines, TextDocument.LineEnding.Lf) : null;

        TextDocument.TextPosition IEditorCompletionHost.CaretPosition => Caret;

        public string CurrentText
        {
            get
            {
                if (!HasDocument || Caret.Line == -1)
                {
                    return null;
                }
                string line = Lines[Caret.Line];
                int column = Caret.Column;
                while (column > 0 && WordCharacters.IndexOf(line[column - 1]) != -1)
                {
                    column--;
                }
                return line.Substring(column, Caret.Column - column);
            }
        }

        public RgbColor SuggestionBackColor => RgbColor.FromArgb(255, 1, 2, 3);

        public ISuggestionPopupFactory CreatePopupFactory()
        {
            Factories++;
            return this;
        }

        public ISuggestionPopup Create(ISuggestionSource suggestionSource, bool allowMultiSelect)
        {
            Popup = new Popup(this, suggestionSource);
            Check(!allowMultiSelect, "the list is made without the multi-select option");
            return Popup;
        }

        public IHintView CreateHintView()
        {
            Hint hint = new Hint(this, Hints.Count);
            Hints.Add(hint);
            return hint;
        }

        public void FocusView()
        {
            Log.Add("focus");
            if (LoseFocusOnFocus)
            {
                // The hint window took the keyboard while it was shown: the editor hears of it before it has the keyboard back.
                Completion.OnLostFocus();
            }
        }

        public bool IsWordSeparator(char ch)
        {
            return WordCharacters.IndexOf(ch) == -1;
        }

        public void ReplaceWordAtCaret(string text)
        {
            string word = CurrentText;
            string line = Lines[Caret.Line];
            int start = Caret.Column - word.Length;
            Lines[Caret.Line] = line.Substring(0, start) + text + line.Substring(Caret.Column);
            Caret.Column = start + text.Length;
        }

        /// <summary>A character typed as the view does it: the hook before the edit, the edit, the hook after it, then the KeyPress is over.</summary>
        public void Type(string text, bool control = false)
        {
            foreach (char ch in text)
            {
                // Backspace deletes: the view does not call the typing hooks for it.
                if (ch == '\b')
                {
                    string line = Lines[Caret.Line];
                    Lines[Caret.Line] = line.Remove(Caret.Column - 1, 1);
                    Caret.Column--;
                }
                else
                {
                    Completion.OnCharTyping(ch, control);
                    string line = Lines[Caret.Line];
                    Lines[Caret.Line] = line.Insert(Caret.Column, ch.ToString());
                    Caret.Column++;
                    Completion.OnCharTyped(ch, control);
                }
                Completion.AfterKeyPress(ch);
            }
        }

        public void Key(VirtualKey key)
        {
            if (key == VirtualKey.Left)
            {
                Caret.Column--;
            }
            else if (key == VirtualKey.Right)
            {
                Caret.Column++;
            }
            if (key == VirtualKey.Escape)
            {
                Completion.OnEscape();
            }
            Completion.AfterKeyDown(key);
        }

        public string Take()
        {
            string text = string.Join(";", Log);
            Log.Clear();
            return text;
        }

        public string Line => Lines[Caret.Line];
    }

    private static Editor WordEditor()
    {
        Editor editor = new Editor("int value = 1;", "int valid = 2;", "");
        editor.Caret = new TextDocument.TextPosition(0, 2);
        editor.Completion.Language = SyntaxHighlighting.Language.CLike;
        return editor;
    }

    private static void ListChecks()
    {
        Editor editor = WordEditor();
        Equal(editor.Completion.Enabled, true, "completion is on at first");
        Equal(editor.Completion.OnTyping, false, "completion on typing is off at first");
        Equal(new EditorCompletion(editor).Language, SyntaxHighlighting.Language.Plain, "the language is Plain at first");
        Check(editor.Completion.ApiSymbols == null, "no API symbols at first");
        editor.Type("va");
        Equal(editor.Take(), "", "with completion on typing off two letters open nothing");
        Check(!editor.Completion.IsSuggestionVisible, "no list");

        editor = WordEditor();
        editor.Completion.OnTyping = true;
        editor.Type("v");
        Equal(editor.Take(), "", "one letter opens nothing");
        editor.Type("a");
        Equal(editor.Take(), "create;back FF010203;show True;select va True;select va True", "the second letter opens the list, told the typed word once by the opening and once by the KeyPress");
        Check(editor.Completion.IsSuggestionVisible, "the list is visible");
        Equal(editor.Factories, 1, "the factory of the windows is asked for once");
        Check(Contains(editor.Popup.Items, "valid") && Contains(editor.Popup.Items, "value") && Contains(editor.Popup.Items, "int"), "the list holds the words of the text and the keywords");
        Check(Contains(editor.Popup.Items, "typedef"), "and the keywords of C");
        editor.Type("l");
        Equal(editor.Take(), "select val True", "a third letter selects again and opens nothing new");
        editor.Type("\b");
        Equal(editor.Take(), "select va True", "Backspace selects the shorter word");
        editor.Completion.AfterKeyPress('\r');
        editor.Completion.AfterKeyPress('\t');
        Equal(editor.Take(), "", "other control characters do not select");
        editor.Type("li");
        editor.Take();
        editor.Type(" ");
        Equal(editor.Take(), "accept valid", "a separator accepts the selected word that continues the typed one");
        Equal(editor.Line, "valid ", "the word is replaced, then the separator typed");
        Check(!editor.Completion.IsSuggestionVisible, "the list is closed");

        editor.Type("xy");
        Equal(editor.Take(), "dispose;create;back FF010203;show True;select xy True;select xy True", "a new list replaces the old window");
        editor.Type("q");
        editor.Take();
        editor.Popup.SelectMatch("zz", false);
        editor.Take();
        editor.Type(".");
        Equal(editor.Take(), "hide", "a separator closes the list when the selected word does not continue the typed one");
        Equal(editor.Line, "valid xyq.", "nothing replaced");

        editor.Type(" ab");
        editor.Take();
        editor.Type("(", control: true);
        Equal(editor.Take(), "select  True", "a separator typed with Ctrl accepts nothing: the list selects for the empty word after it");
        Check(editor.Completion.IsSuggestionVisible, "still visible");
        editor.Completion.OnEscape();
        Equal(editor.Take(), "hide", "Escape hides the list");
        editor.Type("cd", control: true);
        Equal(editor.Take(), "", "letters typed with Ctrl do not open it");
        editor.Type(" 12");
        Equal(editor.Take(), "dispose;create;back FF010203;show True;hide", "two digits open the list, and the word that starts with a digit closes it at once");
        editor.Type("x");
        Equal(editor.Take(), "dispose;create;back FF010203;show True;hide", "and so does a letter after them");
        editor.Completion.ShowCompletion();
        Equal(editor.Take(), "dispose;create;back FF010203;show True;hide", "Ctrl+Space on a word that starts with a digit opens the list and closes it");
        editor.Type(" 0a");
        Equal(editor.Take(), "dispose;create;back FF010203;show True;hide", "a word that starts with 0 closes the list too");
        editor.Type(" ");
        editor.Completion.ShowCompletion();
        Equal(editor.Take(), "dispose;create;back FF010203;show True;select  True", "Ctrl+Space on no word lists everything");
        editor.Type("1");
        Equal(editor.Take(), "hide", "a digit is a word character: it accepts nothing, and the word that starts with it closes the list");
    }

    private static void SwitchChecks()
    {
        Editor editor = WordEditor();
        editor.Completion.Language = SyntaxHighlighting.Language.Bash;
        editor.Completion.Enabled = true;
        Equal(editor.Take(), "", "the setters do nothing to a list that was never made");
        editor.Completion.ShowCompletion();
        editor.Take();
        editor.Completion.Language = SyntaxHighlighting.Language.Bash;
        Equal(editor.Take(), "hide", "a language set while the list is open closes it, the same one too");
        Equal(editor.Completion.Language, SyntaxHighlighting.Language.Bash, "the language is kept");
        editor.Completion.Language = SyntaxHighlighting.Language.CLike;
        Equal(editor.Take(), "", "a language set while the list is closed does not hide it again");
        editor.Completion.Enabled = true;
        Equal(editor.Take(), "", "switched on, nothing happens");
        editor.Completion.Enabled = false;
        Equal(editor.Take(), "hide", "switched off, the list is hidden (even when closed)");
        Equal(editor.Completion.Enabled, false, "the switch is kept");
        editor.Completion.ShowCompletion();
        editor.Completion.OnTyping = true;
        editor.Type("val");
        Equal(editor.Take(), "", "switched off, neither Ctrl+Space nor typing opens the list");
        editor.Completion.Enabled = true;
        editor.Completion.OnTyping = false;
        editor.Type("u");
        Equal(editor.Take(), "", "completion on typing off opens nothing");
        Equal(editor.Completion.OnTyping, false, "the switch of typing is kept");

        editor = WordEditor();
        editor.HasDocument = false;
        editor.Completion.ShowCompletion();
        Equal(editor.Take(), "", "no document: Ctrl+Space opens nothing");
        editor = new Editor("", "");
        editor.Caret = new TextDocument.TextPosition(0, 0);
        editor.Completion.ShowCompletion();
        Equal(editor.Take(), "", "plain text without words: nothing to offer, nothing opens");
        editor.Completion.HideCompletion();
        Equal(editor.Take(), "", "Hide before a list was made does nothing");

        editor = WordEditor();
        editor.Completion.ShowCompletion();
        Check(Contains(editor.Popup.Items, "typedef"), "a list of C");
        editor.Completion.Language = SyntaxHighlighting.Language.Bash;
        editor.Completion.ShowCompletion();
        Check(!Contains(editor.Popup.Items, "typedef") && Contains(editor.Popup.Items, "valid"), "a new list holds the words of the new language only");

        ApiSymbolDatabase api = new ApiSymbolDatabase();
        editor.Completion.ApiSymbols = api;
        Check(ReferenceEquals(editor.Completion.ApiSymbols, api), "the API symbols are kept");
    }

    private static Editor CallEditor()
    {
        Editor editor = new Editor("int sum(int a, int b) { return a + b; }", "");
        editor.Caret = new TextDocument.TextPosition(0, 1);
        editor.Completion.Language = SyntaxHighlighting.Language.CLike;
        return editor;
    }

    private static void HintChecks()
    {
        Editor editor = CallEditor();
        editor.Type("x = sum");
        Equal(editor.Take(), "", "letters do not look for a call");
        Check(editor.Completion.SignatureHintName == null, "no hint name");
        editor.Type("(");
        Equal(editor.Take(), "hint0 active 0;hint0 show;focus", "the parenthesis opens the hint at the first parameter and gives the keyboard back");
        Check(editor.Completion.IsSignatureHintVisible, "the hint is visible");
        Equal(editor.Completion.SignatureHintName, "sum", "the hint name");
        Equal(editor.Hints[0].Signature, "int a, int b", "the parameters");
        Equal(editor.Hints[0].ShowDescription, false, "no description");
        editor.Type("1");
        Equal(editor.Take(), "", "a digit leaves the hint");
        editor.Type(",");
        Equal(editor.Take(), "hint0 active 1", "a comma: the next parameter, in the same window");
        editor.Type(" 2");
        editor.Key(VirtualKey.Left);
        Equal(editor.Take(), "hint0 active 1", "Left: the same window told the parameter again");
        editor.Key(VirtualKey.Right);
        editor.Take();
        editor.Type(")");
        Equal(editor.Take(), "hint0 dismiss", "after the call the hint closes");
        Check(!editor.Completion.IsSignatureHintVisible && editor.Completion.SignatureHintName == null, "no hint, no name");
        editor.Key(VirtualKey.Left);
        Equal(editor.Take(), "hint1 active 1;hint1 show;focus", "Left back into the call opens a new window");
        foreach (VirtualKey key in new[] { VirtualKey.Up, VirtualKey.Down, VirtualKey.Home, VirtualKey.End, VirtualKey.Back, VirtualKey.Delete })
        {
            editor.Key(key);
            Equal(editor.Take(), "hint1 active 1", key + " looks at the call again");
        }
        foreach (VirtualKey key in new[] { VirtualKey.PageUp, VirtualKey.A, VirtualKey.Return, VirtualKey.Tab })
        {
            editor.Key(key);
            Equal(editor.Take(), "", key + " does not");
        }
        editor.Key(VirtualKey.Right);
        Equal(editor.Take(), "hint1 dismiss", "Right out of the call closes it");
        editor.Key(VirtualKey.Left);
        editor.Take();
        editor.Completion.RepositionHint();
        Equal(editor.Take(), "hint2 reposition", "a scroll moves the hint");
        editor.Completion.OnEscape();
        Equal(editor.Take(), "hint2 dismiss", "Escape closes the hint (and hides no list that was never made)");
        editor.Completion.RepositionHint();
        editor.Completion.SetActiveParameter(0);
        editor.Completion.HideSignatureHint();
        Equal(editor.Take(), "", "with no hint, a scroll, a parameter and Hide do nothing");

        editor.Key(VirtualKey.Left);
        editor.Take();
        editor.Completion.OnLostFocus();
        Equal(editor.Take(), "hint3 dismiss", "the lost focus closes the hint");
        editor.LoseFocusOnFocus = true;
        editor.Key(VirtualKey.Right);
        Equal(editor.Take(), "hint4 active 1;hint4 show;focus", "the focus the hint window takes while it is shown does not close it");
        Check(editor.Completion.IsSignatureHintVisible, "visible");
        editor.LoseFocusOnFocus = false;

        editor.Completion.ShowCompletion();
        Equal(editor.Take(), "hint4 dismiss;create;back FF010203;show True;hide", "Ctrl+Space closes the hint first (the list closes at once here: the word is a digit)");
        editor.Key(VirtualKey.Left);
        editor.Take();
        editor.Completion.OnTyping = true;
        editor.Type("ab");
        Equal(editor.Take(), "hint5 dismiss;dispose;create;back FF010203;show True;select ab True;select ab True", "the list opened by typing closes the hint");
        editor.Key(VirtualKey.Left);
        Equal(editor.Take(), "", "no hint while the list is open");
        Check(!editor.Completion.IsSignatureHintVisible, "none");
        editor.Completion.OnEscape();
        editor.Key(VirtualKey.Left);
        Equal(editor.Take(), "hide;hint6 active 1;hint6 show;focus", "Escape, then Left: the hint again");

        editor.Completion.SetActiveParameter(0);
        Equal(editor.Take(), "hint6 active 0", "SetActiveParameter tells the shown hint");
        editor.Completion.ShowSignatureHint(new ParameterSignature("f", new[] { "x" }), 0, "complete");
        Equal(editor.Take(), "hint6 dismiss;hint7 active 0;hint7 show;focus", "ShowSignatureHint replaces the window");
        Equal(editor.Hints[7].ShowDescription, true, "with a text it shows a description");
        Equal(editor.Hints[7].Description, "Press TAB to complete", "the description");
        Check(editor.Completion.SignatureHintName == null, "ShowSignatureHint from outside sets no name");
        editor.Key(VirtualKey.Left);
        Equal(editor.Take(), "hint7 dismiss;hint8 active 1;hint8 show;focus", "so the next update makes a window of its own");

        editor.Hints[8].IsShown = false;
        editor.Completion.SetActiveParameter(0);
        editor.Completion.RepositionHint();
        Equal(editor.Take(), "", "a hint window that closed by itself is not told the parameter nor moved");
        editor.Key(VirtualKey.Right);
        Equal(editor.Take(), "hint8 dismiss;hint9 active 1;hint9 show;focus", "the next update makes a new window, though the name is the same");
        editor.Completion.Enabled = false;
        Equal(editor.Take(), "hide", "switched off, the list is hidden and the hint stays");
        editor.Key(VirtualKey.Left);
        Equal(editor.Take(), "hint9 dismiss", "the next key closes the hint");
        editor.Key(VirtualKey.Right);
        Equal(editor.Take(), "", "and opens none");

        editor = CallEditor();
        editor.Type("x = sum(");
        editor.Take();
        editor.HasDocument = false;
        editor.Completion.UpdateSignatureHint();
        Equal(editor.Take(), "hint0 dismiss", "no document: the hint closes");
        editor.HasDocument = true;
        editor.Completion.UpdateSignatureHint();
        editor.Take();
        editor.Caret = new TextDocument.TextPosition(-1, -1);
        editor.Completion.UpdateSignatureHint();
        Equal(editor.Take(), "hint1 dismiss", "no caret: the hint closes");
    }

    private static bool Contains(IEnumerable<ISuggestion> items, string name)
    {
        foreach (ISuggestion item in items)
        {
            if (item.DisplayName == name)
            {
                return true;
            }
        }
        return false;
    }

    private static void Main()
    {
        ListChecks();
        SwitchChecks();
        HintChecks();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " editor completion checks passed.");
    }
}
