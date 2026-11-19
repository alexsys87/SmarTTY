using System;

namespace SmarTTY.Terminal;

// A stand-in for the real CompletionSession (Terminal/CompletionSession.cs, which needs SSH.NET and is not compiled here): only its nested
// AppliedEventArgs, with the members of the real one. If the real type changes, the controls that use it stop compiling in the application and the Mono stands.
internal class CompletionSession
{
    public class AppliedEventArgs : EventArgs
    {
        public string NewText;

        public int CaretIndex;

        public bool Commit;

        public CompletionItem Item;

        public bool IsDirectory;
    }
}
