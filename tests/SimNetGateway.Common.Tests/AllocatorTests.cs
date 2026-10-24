using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;

namespace SimNetGateway.Common.Tests;

public class AllocatorTests
{
    private static ulong Alloc(RangeAllocator a)
    {
        Assert.True(a.TryAllocate(out var key));
        return key;
    }

    private static int Alloc(NumberPool p)
    {
        Assert.True(p.TryAllocate(out var n));
        return n;
    }

    // ---- RangeAllocator -------------------------------------------------------------------------------------

    [Fact]
    public void Range_hands_out_the_range_in_order_and_then_is_exhausted()
    {
        var a = new RangeAllocator(10, 12);
        Assert.Equal(3UL, a.Capacity);
        Assert.Equal(3UL, a.Available);
        Assert.Equal(10UL, Alloc(a));
        Assert.Equal(11UL, Alloc(a));
        Assert.Equal(12UL, Alloc(a));
        Assert.Equal(0UL, a.Available);
        Assert.False(a.TryAllocate(out var none));
        Assert.Equal(0UL, none);
    }

    [Fact]
    public async Task Range_exhaustion_terminates_and_never_hangs()
    {
        // Guard: an allocation loop on an exhausted range must end; a hang would stall the whole test run, so it runs
        // on a worker thread and fails after a timeout instead.
        var work = Task.Run(() =>
        {
            var a = new RangeAllocator(1, 1000);
            ulong allocated = 0;
            while (a.TryAllocate(out _))
            {
                allocated++;
            }

            a.Release(500UL);
            bool again = a.TryAllocate(out var key);
            return (allocated, again, key, again && !a.TryAllocate(out _));
        });
        var finished = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.True(ReferenceEquals(finished, work), "the allocation loop on an exhausted RangeAllocator did not finish within 5 s");
        var (count, reallocated, reused, exhaustedAgain) = await work;
        Assert.Equal(1000UL, count);
        Assert.True(reallocated);
        Assert.Equal(500UL, reused);
        Assert.True(exhaustedAgain);
    }

    [Fact]
    public void Range_is_next_fit_so_a_just_released_key_is_not_reused_at_once()
    {
        var a = new RangeAllocator(1, 4);
        Assert.Equal(1UL, Alloc(a));
        Assert.Equal(2UL, Alloc(a));
        a.Release(1UL);
        Assert.Equal(3UL, Alloc(a));
        Assert.Equal(4UL, Alloc(a));
        Assert.Equal(1UL, Alloc(a)); // wrapped around
        Assert.Equal(0UL, a.Available);
    }

    [Fact]
    public void Range_reuses_a_released_key_when_it_is_the_only_free_one()
    {
        var a = new RangeAllocator(1, 3);
        Alloc(a);
        Alloc(a);
        Alloc(a);
        a.Release(2UL);
        Assert.Equal(1UL, a.Available);
        Assert.Equal(2UL, Alloc(a));
        Assert.False(a.TryAllocate(out _));
    }

    [Fact]
    public void Range_ignores_releases_of_unknown_keys()
    {
        var a = new RangeAllocator(10, 12);
        Alloc(a);
        a.Release(5UL);       // below the range
        a.Release(13UL);      // above the range
        a.Release(11UL);      // in range, never allocated
        Assert.Equal(2UL, a.Available);
        a.Release(10UL);
        a.Release(10UL);      // twice
        Assert.Equal(3UL, a.Available);
        Assert.Equal(0UL, a.InUse);
    }

    [Fact]
    public void Range_works_at_the_top_of_the_ulong_space_and_validates_its_arguments()
    {
        var a = new RangeAllocator(ulong.MaxValue - 1, ulong.MaxValue);
        Assert.Equal(ulong.MaxValue - 1, Alloc(a));
        Assert.Equal(ulong.MaxValue, Alloc(a));
        Assert.False(a.TryAllocate(out _));
        a.Release(ulong.MaxValue);
        Assert.Equal(ulong.MaxValue, Alloc(a));
        Assert.Throws<ArgumentException>(() => new RangeAllocator(5, 4));
        Assert.Throws<ArgumentException>(() => new RangeAllocator(0, ulong.MaxValue));
        var single = new RangeAllocator(7, 7);
        Assert.Equal(7UL, Alloc(single));
        Assert.False(single.TryAllocate(out _));
    }

    [Fact]
    public void Range_allocation_is_free_of_garbage_in_steady_state()
    {
        var a = new RangeAllocator(1, 1000);
        var live = new ulong[1000];
        for (int i = 0; i < 1000; i++)
        {
            live[i] = Alloc(a);
        }

        for (int round = 0; round < 20; round++)
        {
            for (int i = 0; i < live.Length; i++)
            {
                a.Release(live[i]);
                live[i] = Alloc(a);
            }
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int round = 0; round < 20; round++)
        {
            for (int i = 0; i < live.Length; i++)
            {
                a.Release(live[i]);
                live[i] = Alloc(a);
            }
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ---- NumberPool -----------------------------------------------------------------------------------------

    [Fact]
    public void Pool_hands_out_fresh_numbers_lowest_first_before_reusing_released_ones()
    {
        var clock = new ManualClock();
        var pool = new NumberPool(1, 5, clock, 10);
        Assert.Equal(1, Alloc(pool));
        Assert.Equal(2, Alloc(pool));
        pool.Release(1);
        Assert.Equal(3, Alloc(pool)); // fresh numbers first, even though 1 would be lower
        Assert.Equal(4, Alloc(pool));
        Assert.Equal(5, Alloc(pool));
        Assert.False(pool.TryAllocate(out _)); // 1 is still in quarantine
    }

    [Fact]
    public void Pool_quarantine_ends_exactly_at_the_boundary()
    {
        var clock = new ManualClock();
        var pool = new NumberPool(1, 3, clock, 5);
        Assert.Equal(1, Alloc(pool));
        Assert.Equal(2, Alloc(pool));
        Assert.Equal(3, Alloc(pool));
        Assert.Equal(0, pool.Available);

        clock.Now = 0;
        pool.Release(2);
        clock.Now = 1;
        pool.Release(1);
        Assert.Equal(2, pool.InQuarantine);
        Assert.Equal(0, pool.Available);

        clock.Now = 4.75;
        Assert.False(pool.TryAllocate(out _));
        Assert.Equal(2, pool.InQuarantine);

        clock.Now = 5.0;                       // 2 was released at 0: exactly quarantine seconds ago
        Assert.Equal(1, pool.Available);
        Assert.Equal(1, pool.InQuarantine);
        Assert.Equal(2, Alloc(pool));
        Assert.False(pool.TryAllocate(out _)); // 1 was released at 1

        clock.Now = 5.75;
        Assert.False(pool.TryAllocate(out _));
        clock.Now = 6.0;
        Assert.Equal(1, Alloc(pool));
        Assert.Equal(0, pool.InQuarantine);
        Assert.Equal(0, pool.Available);
    }

    [Fact]
    public void Pool_reuses_released_numbers_in_release_order()
    {
        var clock = new ManualClock();
        var pool = new NumberPool(1, 3, clock, 2);
        Alloc(pool);
        Alloc(pool);
        Alloc(pool);
        pool.Release(3);
        clock.Now = 1;
        pool.Release(1);
        clock.Now = 2;
        pool.Release(2);
        clock.Now = 10;
        Assert.Equal(3, Alloc(pool));
        Assert.Equal(1, Alloc(pool));
        Assert.Equal(2, Alloc(pool));
    }

    [Fact]
    public void Pool_keeps_release_order_when_its_queue_grows_while_wrapped()
    {
        var clock = new ManualClock();
        var pool = new NumberPool(1, 40, clock, 5);
        for (int i = 0; i < 40; i++)
        {
            Alloc(pool);
        }

        // Move the ring head off zero first, then fill the ring beyond its initial size of 16.
        for (int n = 1; n <= 3; n++)
        {
            pool.Release(n);
        }

        clock.Now = 10;
        for (int n = 1; n <= 3; n++)
        {
            Assert.Equal(n, Alloc(pool));
        }

        for (int n = 10; n < 30; n++)
        {
            clock.Now = 20 + n;
            pool.Release(n);
        }

        clock.Now = 100;
        for (int n = 10; n < 30; n++)
        {
            Assert.Equal(n, Alloc(pool));
        }

        Assert.False(pool.TryAllocate(out _));
    }

    [Fact]
    public void Pool_with_zero_quarantine_reuses_at_once()
    {
        var pool = new NumberPool(1, 1, new ManualClock(), 0);
        Assert.Equal(1, Alloc(pool));
        Assert.False(pool.TryAllocate(out _));
        pool.Release(1);
        Assert.Equal(1, Alloc(pool));
    }

    [Fact]
    public void Pool_ignores_releases_of_unknown_numbers()
    {
        var clock = new ManualClock();
        var pool = new NumberPool(10, 12, clock, 1);
        Alloc(pool);
        pool.Release(9);   // outside
        pool.Release(13);  // outside
        pool.Release(11);  // never allocated
        Assert.Equal(2, pool.Available);
        pool.Release(10);
        pool.Release(10);  // twice: the second is ignored
        Assert.Equal(1, pool.InQuarantine);
        clock.Now = 1;
        Assert.Equal(3, pool.Available);
        Assert.Equal(11, Alloc(pool));
        Assert.Equal(12, Alloc(pool));
        Assert.Equal(10, Alloc(pool));
        Assert.False(pool.TryAllocate(out _));
        Assert.Equal(3, pool.InUse);
    }

    [Fact]
    public void Pool_validates_its_arguments()
    {
        var clock = new ManualClock();
        Assert.Throws<ArgumentException>(() => new NumberPool(5, 4, clock, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new NumberPool(1, 5, clock, -1));
        Assert.Throws<ArgumentNullException>(() => new NumberPool(1, 5, null!, 1));
        Assert.Equal(65535, new NumberPool(1, 65535, clock, 1).Capacity);
    }

    [Fact]
    public void Pool_allocation_is_free_of_garbage_in_steady_state()
    {
        var clock = new ManualClock();
        var pool = new NumberPool(1, 1000, clock, 1);
        var live = new int[1000];
        for (int i = 0; i < live.Length; i++)
        {
            live[i] = Alloc(pool);
        }

        void Cycle()
        {
            for (int i = 0; i < live.Length; i++)
            {
                pool.Release(live[i]);
            }

            clock.Advance(1);
            for (int i = 0; i < live.Length; i++)
            {
                live[i] = Alloc(pool);
            }
        }

        for (int i = 0; i < 5; i++)
        {
            Cycle();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 20; i++)
        {
            Cycle();
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    // ---- MappedAllocator ------------------------------------------------------------------------------------

    [Fact]
    public void Mapped_allocator_builds_structured_keys_from_pool_numbers()
    {
        var clock = new ManualClock();
        var pool = new NumberPool(100, 101, clock, 5);
        var mapped = new MappedAllocator<(int Site, int App, int Number)>(
            pool, n => (1, 3001, n), k => k.Number);

        Assert.True(mapped.TryAllocate(out var first));
        Assert.Equal((1, 3001, 100), first);
        Assert.True(mapped.TryAllocate(out var second));
        Assert.Equal((1, 3001, 101), second);
        Assert.False(mapped.TryAllocate(out _));

        mapped.Release(in first);
        Assert.Equal(1, pool.InQuarantine);
        Assert.False(mapped.TryAllocate(out _));
        clock.Now = 5;
        Assert.True(mapped.TryAllocate(out var again));
        Assert.Equal(first, again);
    }

    [Fact]
    public void Mapped_allocator_rejects_missing_arguments()
    {
        var pool = new NumberPool(1, 2, new ManualClock(), 0);
        Assert.Throws<ArgumentNullException>(() => new MappedAllocator<int>(null!, n => n, k => k));
        Assert.Throws<ArgumentNullException>(() => new MappedAllocator<int>(pool, null!, k => k));
        Assert.Throws<ArgumentNullException>(() => new MappedAllocator<int>(pool, n => n, null!));
    }
}
