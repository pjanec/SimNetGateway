using SimNetGateway.Engine;

namespace SimNetGateway.Common;

/// <summary>
/// A pool of numbers [first, last] with a time-based quarantine (e.g. DIS entity numbers under a fixed site and
/// application, DD-001 3.6): a released number becomes available again only when <c>now - releasedAt &gt;= quarantine</c>
/// (the boundary itself is available), so receivers that still remember the old entity do not confuse it with a new one.
/// <para>
/// Order: never-used numbers are handed out first, lowest first, ascending from <c>first</c>; after that, released
/// numbers are reused FIFO by release time. Releasing a number that is outside the range or not allocated is ignored.
/// Allocation-free in steady state (the quarantine queue is a ring that doubles when needed).
/// </para>
/// </summary>
public sealed class NumberPool : IIdAllocator<int>
{
    private enum State : byte
    {
        Fresh,
        Allocated,
        Quarantined,
    }

    private readonly int _first;
    private readonly int _last;
    private readonly IClock _clock;
    private readonly double _quarantine;
    private readonly State[] _state;
    private int _nextFresh;                  // offset of the next never-used number
    private int[] _queueNumber = new int[16];
    private double[] _queueReleasedAt = new double[16];
    private int _head;
    private int _count;
    private int _allocatedCount;

    public NumberPool(int first, int last, IClock clock, double quarantineSeconds)
    {
        ArgumentNullException.ThrowIfNull(clock);
        if (last < first)
        {
            throw new ArgumentException("The range is empty (last < first).", nameof(last));
        }

        if (quarantineSeconds < 0 || double.IsNaN(quarantineSeconds))
        {
            throw new ArgumentOutOfRangeException(nameof(quarantineSeconds), "The quarantine must not be negative.");
        }

        long capacity = (long)last - first + 1;
        if (capacity > int.MaxValue / 2)
        {
            throw new ArgumentException("The range is too large for a pool (it keeps one byte per number).", nameof(last));
        }

        _first = first;
        _last = last;
        _clock = clock;
        _quarantine = quarantineSeconds;
        _state = new State[capacity];
        Capacity = (int)capacity;
    }

    public int Capacity { get; }

    /// <summary>Numbers handed out and not released.</summary>
    public int InUse => _allocatedCount;

    /// <summary>Released numbers still waiting out their quarantine.</summary>
    public int InQuarantine
    {
        get
        {
            double now = _clock.Now;
            int waiting = 0;
            for (int i = 0; i < _count; i++)
            {
                if (now - _queueReleasedAt[(_head + i) % _queueNumber.Length] < _quarantine)
                {
                    waiting++;
                }
            }

            return waiting;
        }
    }

    /// <summary>Numbers that <see cref="TryAllocate"/> can hand out right now.</summary>
    public int Available => (Capacity - _nextFresh) + (_count - InQuarantine);

    public bool TryAllocate(out int number)
    {
        if (_nextFresh < _state.Length)
        {
            int offset = _nextFresh++;
            _state[offset] = State.Allocated;
            _allocatedCount++;
            number = _first + offset;
            return true;
        }

        if (_count > 0 && _clock.Now - _queueReleasedAt[_head] >= _quarantine)
        {
            number = _queueNumber[_head];
            _head = (_head + 1) % _queueNumber.Length;
            _count--;
            _state[number - _first] = State.Allocated;
            _allocatedCount++;
            return true;
        }

        number = 0;
        return false;
    }

    public void Release(in int number)
    {
        if (number < _first || number > _last)
        {
            return;
        }

        int offset = number - _first;
        if (_state[offset] != State.Allocated)
        {
            return;
        }

        _state[offset] = State.Quarantined;
        _allocatedCount--;
        if (_count == _queueNumber.Length)
        {
            Grow();
        }

        int tail = (_head + _count) % _queueNumber.Length;
        _queueNumber[tail] = number;
        _queueReleasedAt[tail] = _clock.Now;
        _count++;
    }

    private void Grow()
    {
        int size = _queueNumber.Length;
        var numbers = new int[size * 2];
        var times = new double[size * 2];
        for (int i = 0; i < _count; i++)
        {
            int from = (_head + i) % size;
            numbers[i] = _queueNumber[from];
            times[i] = _queueReleasedAt[from];
        }

        _queueNumber = numbers;
        _queueReleasedAt = times;
        _head = 0;
    }
}
