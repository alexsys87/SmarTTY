using System;

namespace SmarTTY.Terminal.Emulation;

public class TabStops
{
    private bool[] _stops = new bool[0];

    public void SetStop(int column)
    {
        if (column > 65536)
        {
            column = 65536;
        }
        if (column >= _stops.Length)
        {
            Array.Resize(ref _stops, column + 1);
        }
        _stops[column] = true;
    }

    public int NextDistance(int column, int tabSize)
    {
        if (column >= this._stops.Length)
        {
            if (tabSize <= 0)
            {
                tabSize = 8;
            }
            return tabSize - column % tabSize;
        }
        for (int nextColumn = column + 1; nextColumn < this._stops.Length; nextColumn++)
        {
            if (this._stops[nextColumn])
            {
                return nextColumn - column;
            }
        }
        return 1;
    }

    public void ClearAll()
    {
        for (int column = 0; column < _stops.Length; column++)
        {
            _stops[column] = false;
        }
    }

    internal void ClearStop(int column)
    {
        if (column < _stops.Length)
        {
            _stops[column] = false;
        }
    }

    internal int PreviousDistance(int column)
    {
        if (column >= _stops.Length)
        {
            column = _stops.Length - 1;
        }
        if (column <= 0)
        {
            return 0;
        }
        for (int previousColumn = column - 1; previousColumn > 0; previousColumn--)
        {
            if (_stops[previousColumn])
            {
                return column - previousColumn;
            }
        }
        return column;
    }
}
