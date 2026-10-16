using System;
using System.Threading;

namespace SmarTTY;

public class ReentrancyCounter
{
    public class Scope : IDisposable
    {
        private ReentrancyCounter _owner;

        public readonly bool IsOutermost;

        public Scope(ReentrancyCounter reentrancyCounter)
        {
            this._owner = reentrancyCounter;
            IsOutermost = Interlocked.Increment(ref this._owner._depth) == 1;
        }

        public void Dispose()
        {
            Interlocked.Decrement(ref _owner._depth);
        }
    }

    private int _depth;

    public bool IsActive => _depth != 0;

    public override string ToString()
    {
        return _depth.ToString();
    }

    public Scope Enter()
    {
        return new Scope(this);
    }
}
