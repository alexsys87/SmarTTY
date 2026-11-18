namespace SmarTTY.Terminal.Emulation;

internal class ScrollRegion
{
    public int Top;

    public int Bottom;

    public override string ToString()
    {
        return $"Lines {Top}-{Bottom}";
    }

    public bool Contains(int line)
    {
        if (line >= Top)
        {
            return line <= Bottom;
        }
        return false;
    }
}
