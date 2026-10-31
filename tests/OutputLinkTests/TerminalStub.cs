namespace SmarTTY.Controls
{
    // The three types of the terminal control that the link scanner is written against. The real ones are nested in the
    // WinForms control TerminalEmulatorBase, which the harness cannot load; these have the same shape.
    internal class TerminalEmulatorBase
    {
        internal interface ILinkTarget
        {
            bool IsActive { get; }

            void Activate();
        }

        internal struct LinkCell
        {
            public ILinkTarget Target;

            public byte Severity;
        }

        internal interface ILinkScanner
        {
            LinkCell[] Scan(string text);

            void Finish();
        }

        internal interface IColumnAwareLinkScanner : ILinkScanner
        {
            LinkCell[] Scan(string text, int column);
        }
    }
}
