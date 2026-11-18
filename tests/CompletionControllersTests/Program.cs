using System;
using System.Collections.Generic;
using System.Linq;
using SmarTTY.Completion;
using SmarTTY.TextEditing;

// Checks of the controllers of the suggestion popup and of the signature hint (Completion/), which work on the windows of the view
// through ISuggestionPopup and IHintView: the order of what they ask of a window, when they close one, what they do with a typed word.
// The windows are fakes that record the calls. The expected answers are those of the controllers when they made the windows themselves.
// Run with: dotnet run --project tests/CompletionControllersTests
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

    private sealed class Item : ISuggestion
    {
        public Item(string name) { DisplayName = name; }

        public int ImageIndex => -1;

        public string DisplayName { get; }

        public string DetailText => null;

        public string HintText => null;

        public bool IsBold => false;

        public string HintDescription => null;
    }

    private sealed class FakePopup : ISuggestionPopup
    {
        public readonly List<string> Log;

        public readonly ISuggestionSource Source;

        public readonly bool MultiSelect;

        public bool Visible;

        public ISuggestion Selected;

        public RgbColor Color;

        public FakePopup(List<string> log, ISuggestionSource source, bool multiSelect)
        {
            Log = log;
            Source = source;
            MultiSelect = multiSelect;
        }

        public bool IsVisible => Visible;

        public RgbColor BackColor { set { Color = value; Log.Add("back " + value.Argb.ToString("X8")); } }

        public ISuggestion SelectedSuggestion => Selected;

        public void Show(bool autoSize) { Visible = true; Log.Add("show " + autoSize); }

        public void Hide() { Visible = false; Log.Add("hide"); }

        public void SelectMatch(string prefix, bool exactMatch) { Log.Add("select " + prefix + " " + exactMatch); }

        public void AcceptSelected() { Log.Add("accept"); }

        public void Dispose() { Visible = false; Log.Add("dispose"); }
    }

    private sealed class FakeFactory : ISuggestionPopupFactory
    {
        public readonly List<string> Log = new List<string>();

        public readonly List<FakePopup> Popups = new List<FakePopup>();

        public ISuggestionPopup Create(ISuggestionSource suggestionSource, bool allowMultiSelect)
        {
            Log.Add("create " + allowMultiSelect);
            FakePopup popup = new FakePopup(Log, suggestionSource, allowMultiSelect);
            Popups.Add(popup);
            return popup;
        }
    }

    private sealed class Source : ISuggestionSource
    {
        public System.Collections.IEnumerable Images => new object[0];

        public IEnumerable<ISuggestion> Items => new ISuggestion[0];
    }

    private static string Log(FakeFactory factory) { return string.Join(",", factory.Log); }

    private static void ControllerChecks()
    {
        FakeFactory factory = new FakeFactory();
        SuggestionController controller = new SuggestionController(factory);
        Check(!controller.IsVisible, "a controller with no popup is not visible");
        controller.Hide();
        controller.SelectMatch("a", exactMatch: true);
        controller.AcceptSelected();
        controller.AcceptSelectedIfMatches("a");
        Equal(Log(factory), "", "without a popup Hide, SelectMatch and Accept do nothing");

        controller.BackColor = RgbColor.FromArgb(10, 20, 30);
        Source source = new Source();
        controller.Show(source, autoSize: true, allowMultiSelect: false);
        Equal(Log(factory), "create False,back FF0A141E,show True", "a popup is made for the source, gets the colour, then is shown fitted to its items");
        Check(ReferenceEquals(factory.Popups[0].Source, source), "the popup lists the source it was given");
        Check(controller.IsVisible, "the controller is visible while its popup is");

        controller.Show(source, autoSize: false, allowMultiSelect: true);
        Equal(Log(factory), "create False,back FF0A141E,show True,dispose,create True,back FF0A141E,show False", "a new popup closes the one before it, for good, and takes the options of the new call");
        Check(factory.Popups[1].MultiSelect && factory.Popups.Count == 2, "the popup is made with the multi-select option");

        factory.Log.Clear();
        controller.SelectMatch(null, exactMatch: false);
        Equal(Log(factory), "", "no prefix: the selection is left alone");
        controller.SelectMatch("ab", exactMatch: true);
        controller.SelectMatch("", exactMatch: false);
        Equal(Log(factory), "select ab True,select  False", "a prefix, even an empty one, goes to the popup");
        factory.Log.Clear();
        controller.Hide();
        Equal(Log(factory), "hide", "Hide hides the popup");
        controller.AcceptSelected();
        Equal(Log(factory), "hide", "a hidden popup accepts nothing");
        factory.Popups[1].Visible = true;
        controller.AcceptSelected();
        Equal(Log(factory), "hide,accept", "a visible popup accepts its selected item");
    }

    private static void AcceptIfMatchesChecks()
    {
        FakeFactory factory = new FakeFactory();
        SuggestionController controller = new SuggestionController(factory);
        controller.Show(new Source(), autoSize: true, allowMultiSelect: false);
        FakePopup popup = factory.Popups[0];

        factory.Log.Clear();
        popup.Visible = false;
        controller.AcceptSelectedIfMatches("ab");
        Equal(Log(factory), "", "a hidden popup: nothing happens");

        popup.Visible = true;
        popup.Selected = null;
        controller.AcceptSelectedIfMatches("ab");
        Equal(Log(factory), "hide", "nothing selected: the popup just closes");

        factory.Log.Clear();
        popup.Visible = true;
        popup.Selected = new Item("abcd");
        controller.AcceptSelectedIfMatches(null);
        popup.Visible = true;
        controller.AcceptSelectedIfMatches("");
        Equal(Log(factory), "hide,hide", "no typed word: the popup closes, the item does not replace anything");

        factory.Log.Clear();
        popup.Visible = true;
        controller.AcceptSelectedIfMatches("xy");
        Equal(Log(factory), "hide", "an item that does not continue the word is not taken");

        factory.Log.Clear();
        popup.Visible = true;
        controller.AcceptSelectedIfMatches("ab");
        Equal(Log(factory), "accept", "an item that continues the word is accepted");

        factory.Log.Clear();
        popup.Visible = true;
        controller.AcceptSelectedIfMatches("ABC");
        Equal(Log(factory), "accept", "the case of the typed word is ignored");

        factory.Log.Clear();
        popup.Visible = true;
        controller.AcceptSelectedIfMatches("abcdef");
        Equal(Log(factory), "hide", "a typed word longer than the item is not a prefix of it");

        factory.Log.Clear();
        popup.Visible = true;
        popup.Selected = new Item(null);
        controller.AcceptSelectedIfMatches("a");
        Equal(Log(factory), "hide", "an item without a name is not taken");
    }

    private static void CatalogChecks()
    {
        SuggestionCatalog catalog = new SuggestionCatalog();
        Check(!catalog.Add("alpha", -1, "d1", "h1", "doc1"), "Add reports false, as it always did");
        catalog.Add("alpine", 3, "d2", "h2", "doc2");
        catalog.Add("beta", 0, null, null, null);
        catalog.Add("alpha", -1, "other", "other", "other");
        Equal(catalog.CountMatching(null), 3, "three names (a name is added once)");
        Equal(catalog.CountMatching("al"), 2, "two names start with al");
        Equal(catalog.CountMatching("AL"), 0, "the prefix is case sensitive");
        Equal(catalog.CountMatching(""), 3, "an empty prefix matches all");
        Equal(catalog.CountMatching("alphabet"), 0, "a prefix longer than every name matches none");
        Check(catalog.Contains("beta") && !catalog.Contains("gamma"), "Contains");
        Equal(catalog.Find("alpha").Detail, "d1", "a name keeps its first detail");
        Equal(catalog.Find("alpine").IconIndex, -1, "an image index past the images (there are none) becomes -1");
        Check(catalog.Find("gamma") == null, "Find of an unknown name is null");

        ISuggestionSource snapshot = catalog.CreateSnapshot("al");
        Equal(string.Join(",", snapshot.Items.Select(i => i.DisplayName)), "alpha,alpine", "the snapshot holds the names that start with the prefix, in the order they were added");
        Equal(snapshot.Images.Cast<object>().Count(), 0, "the catalog has no images");
        Equal(string.Join(",", catalog.CreateSnapshot(null).Items.Select(i => i.DisplayName)), "alpha,alpine,beta", "no prefix: every item");
        catalog.Add("be", 0, null, null, null);
        Equal(string.Join(",", snapshot.Items.Select(i => i.DisplayName)), "alpha,alpine", "a snapshot is taken once: later additions are not in it");
        Equal(string.Join(",", catalog.CreateSnapshot("be").Items.Select(i => i.DisplayName)), "beta,be", "a short name and a long one with the same start");
        Equal(string.Join(",", catalog.CreateSnapshot("betax").Items.Select(i => i.DisplayName)), "", "a prefix longer than a name does not throw and does not match");

        catalog.Clear();
        Equal(catalog.CountMatching(null), 0, "Clear drops every item");

        List<ISuggestion> loaded = new List<ISuggestion> { new Item("one"), null, new Item(""), new Item(null), new Item("two"), new Item("one") };
        catalog.Load(loaded);
        Equal(string.Join(",", catalog.CreateSnapshot(null).Items.Select(i => i.DisplayName)), "one,two", "Load skips null items, items without a name and names it has");
        catalog.Load(null);
        Equal(catalog.CountMatching(null), 2, "Load of null changes nothing");

        // The catalog drives a controller: the popup of a catalog lists the items of the prefix only.
        FakeFactory factory = new FakeFactory();
        SuggestionController controller = new SuggestionController(factory);
        controller.Show(catalog, "tw", autoSize: true, allowMultiSelect: false);
        Equal(string.Join(",", factory.Popups[0].Source.Items.Select(i => i.DisplayName)), "two", "a catalog is shown through a snapshot narrowed by the prefix");
        controller.Show(catalog, null, autoSize: true, allowMultiSelect: false);
        Equal(string.Join(",", factory.Popups[1].Source.Items.Select(i => i.DisplayName)), "one,two", "a null prefix shows the whole catalog");
        Equal(Log(factory), "create False,back 00000000,show True,dispose,create False,back 00000000,show True", "the catalog popups are made, coloured and shown as the others; the colour not set is the empty one");
    }

    private sealed class Parameter : ISignatureParameter
    {
        public Parameter(string text) { Text = text; }

        public string Text { get; }
    }

    private sealed class Signature : ISignatureInfo
    {
        private readonly string[] _parameters;

        public Signature(bool vertical, params string[] parameters) { IsVertical = vertical; _parameters = parameters; }

        public int ParameterCount => _parameters.Length;

        public string Separator => ", ";

        public bool IsVertical { get; }

        public ISignatureParameter GetParameter(int index) { return new Parameter(_parameters[index]); }
    }

    private sealed class FakeHint : IHintView
    {
        public readonly List<string> Log;

        public readonly int Number;

        public bool IsShown;

        public FakeHint(List<string> log, int number) { Log = log; Number = number; }

        public string SignatureText { get; set; }

        public string Description { get; set; }

        public bool ShowDescription { get { return _showDescription; } set { _showDescription = value; Log.Add(Number + ":showDescription " + value); } }

        private bool _showDescription;

        public bool Visible => IsShown;

        public void SetActiveParameter(int index) { Log.Add(Number + ":active " + index); }

        public void SetSignature(ISignatureInfo signatureInfo) { Log.Add(Number + ":signature " + signatureInfo.ParameterCount); }

        public void Dismiss() { IsShown = false; Log.Add(Number + ":dismiss"); }

        public void ShowAt(bool isVertical) { IsShown = true; Log.Add(Number + ":showAt " + isVertical); }

        public void Reposition(bool isVertical) { Log.Add(Number + ":reposition " + isVertical); }
    }

    private static void HintChecks()
    {
        List<string> log = new List<string>();
        List<FakeHint> views = new List<FakeHint>();
        SignatureHintController controller = new SignatureHintController(() =>
        {
            FakeHint view = new FakeHint(log, views.Count + 1);
            views.Add(view);
            return view;
        });
        Check(!controller.IsVisible, "no hint, not visible");
        controller.Reposition();
        controller.SetActiveParameter(1);
        controller.Hide();
        Equal(string.Join(",", log), "", "without a hint Reposition, SetActiveParameter and Hide do nothing");

        controller.Show(new Signature(false, "a", "b"), 1, "Press TAB to insert");
        Equal(string.Join(",", log), "1:showDescription True,1:signature 2,1:active 1,1:showAt False", "a hint: the description, the signature, the active parameter, then it is shown");
        Equal(views[0].Description, "Press TAB to insert", "the description is the text given");
        Check(controller.IsVisible, "the hint is visible once shown");

        log.Clear();
        controller.Reposition();
        controller.SetActiveParameter(0);
        Equal(string.Join(",", log), "1:reposition False,1:active 0", "a visible hint follows the caret and the active parameter");

        log.Clear();
        controller.Show(new Signature(true, "x"), 0, null);
        Equal(string.Join(",", log), "1:dismiss,2:showDescription False,2:signature 1,2:active 0,2:showAt True", "a new hint closes the old one first; no description hides the description; a vertical signature is shown vertical");
        Check(views[1].Description == null, "no description is set when there is none");

        log.Clear();
        controller.Hide();
        Equal(string.Join(",", log), "2:dismiss", "Hide dismisses the hint");
        Check(!controller.IsVisible, "no longer visible");
        controller.Hide();
        controller.Reposition();
        Equal(string.Join(",", log), "2:dismiss", "after Hide there is no hint to dismiss or to move");

        log.Clear();
        views[2 - 1].IsShown = false;
        controller.Show(new Signature(false), 0, "");
        Equal(string.Join(",", log), "3:showDescription True,3:signature 0,3:active 0,3:showAt False", "a signature with no parameters is shown; an empty description is a description");
        Equal(views[2].Description, "", "the empty description is set");
    }

    private static void Main()
    {
        ControllerChecks();
        AcceptIfMatchesChecks();
        CatalogChecks();
        HintChecks();
        Console.WriteLine();
        Console.WriteLine("All " + _passed + " completion controller checks passed.");
    }
}
