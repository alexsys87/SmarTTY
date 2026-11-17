using System;

namespace SmarTTY.TextEditing;

/// <summary>
/// A position on a character grid: <see cref="X"/> is the column and <see cref="Y"/> the row. It is a mutable struct
/// with public fields, like the GDI+ point type, so that code such as <c>buffer.Cursor.X++</c> works unchanged.
/// It is never a pixel position: pixels belong to the view.
/// </summary>
public struct GridPoint : IEquatable<GridPoint>
{
    /// <summary>The point (0, 0).</summary>
    public static readonly GridPoint Empty = new GridPoint();

    /// <summary>The column.</summary>
    public int X;

    /// <summary>The row.</summary>
    public int Y;

    /// <summary>Builds a point from a column and a row.</summary>
    public GridPoint(int x, int y)
    {
        X = x;
        Y = y;
    }

    /// <summary>True when both the column and the row are 0.</summary>
    public bool IsEmpty => X == 0 && Y == 0;

    public bool Equals(GridPoint other)
    {
        return X == other.X && Y == other.Y;
    }

    public override bool Equals(object obj)
    {
        return obj is GridPoint other && Equals(other);
    }

    public override int GetHashCode()
    {
        return unchecked(X * 397 ^ Y);
    }

    public static bool operator ==(GridPoint left, GridPoint right)
    {
        return left.X == right.X && left.Y == right.Y;
    }

    public static bool operator !=(GridPoint left, GridPoint right)
    {
        return left.X != right.X || left.Y != right.Y;
    }

    public override string ToString()
    {
        return "{X=" + X + ",Y=" + Y + "}";
    }
}
