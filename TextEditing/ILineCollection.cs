using System.Collections;
using System.Collections.Generic;
using System.Reflection;

namespace SmarTTY.TextEditing;

[DefaultMember("Item")]
public interface ILineCollection : IEnumerable<string>, IEnumerable
{
    string GetLine(int value2);
}
