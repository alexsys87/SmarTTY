
namespace SmarTTY.Completion;

public interface ISuggestion
{
    int ImageIndex { get; }

    string DisplayName { get; }

    string DetailText { get; }

    string HintText { get; }

    bool IsBold { get; }

    string HintDescription { get; }
}
