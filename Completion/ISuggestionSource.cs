using System.Collections;
using System.Collections.Generic;

namespace SmarTTY.Completion;

public interface ISuggestionSource
{
    IEnumerable Images { get; }

    IEnumerable<ISuggestion> Items { get; }
}
