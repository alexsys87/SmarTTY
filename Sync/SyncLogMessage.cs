using System.Text;

namespace SmarTTY.Sync;

public abstract class SyncLogMessage
{
    public class RunCommandMessage : SyncLogMessage
    {
        public readonly string Command;

        public RunCommandMessage(string text)
        {
            this.Command = text;
        }

        public override string ToString()
        {
            return "Running remote command: " + Command;
        }
    }

    public class RunScriptMessage : SyncLogMessage
    {
        public readonly byte[] Script;

        public RunScriptMessage(byte[] buffer)
        {
            this.Script = buffer;
        }

        public override string ToString()
        {
            return "Running remote script\n" + Encoding.UTF8.GetString(Script);
        }
    }
}
