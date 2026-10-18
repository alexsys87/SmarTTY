using System;
using System.IO;

namespace SmarTTY.Sync;

public interface IExtractionTarget
{
    void CreateDirectory(string text, DateTime dateTime);

    Stream CreateFile(string text);
}
