using SmarTTY.Utilities;
using System;
using System.IO;
using System.Linq;
using System.Reflection;

namespace VisualTTY;

public class CommandCompletionDatabase
{
    public CommandCompletion[] Completions;

    public static CommandCompletionDatabase Load()
    {
        return XmlStorage.LoadFromFile<CommandCompletionDatabase>(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "CommandCompletions.xml"), new Type[0]);
    }

    public CommandCompletion LookupCommand(string cmdLine)
    {
        return Completions.FirstOrDefault((CommandCompletion commandCompletion) => commandCompletion.IsMatch(cmdLine));
    }
}
