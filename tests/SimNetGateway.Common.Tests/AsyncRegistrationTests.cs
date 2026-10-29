using SimNetGateway.Common;
using SimNetGateway.Engine;

namespace SimNetGateway.Common.Tests;

/// <summary>
/// DD-009 section 8: the asynchronous registration machine model-checked against a plain reference model over seeded random
/// request / answer / complete / release sequences. After every operation: the status of every entity, the counts, the handle of a
/// bound entity, the arrivals and orphans equal the model's; tokens are issued strictly increasing and never reused; an answer for
/// a released token is never bound; a token is answered once; a second request of a pending or bound entity is coalesced (0).
/// </summary>
public class AsyncRegistrationTests
{
    private const int Entities = 6;

    private sealed class Model
    {
        public readonly Dictionary<int, (RegistrationStatus Status, long Token, uint Handle)> State = new();
        public readonly Dictionary<long, int> Awaiting = new();
        public readonly HashSet<long> Released = new();
        public readonly List<(int Entity, uint Handle)> Arrivals = new();
        public readonly List<uint> Orphans = new();
        public long LastToken;
    }

    private static void Compare(AsyncRegistration<uint> reg, Model m, string where)
    {
        int pending = 0, bound = 0;
        for (int e = 1; e <= Entities; e++)
        {
            var expected = m.State.TryGetValue(e, out var s) ? s.Status : RegistrationStatus.None;
            Assert.True(expected == reg.StatusOf(new EntityId(e)), $"{where}: status of {e}: expected {expected}, got {reg.StatusOf(new EntityId(e))}");
            pending += expected == RegistrationStatus.Pending ? 1 : 0;
            bound += expected == RegistrationStatus.Bound ? 1 : 0;
            bool hasHandle = reg.TryGetHandle(new EntityId(e), out var handle);
            Assert.True(hasHandle == (expected == RegistrationStatus.Bound), $"{where}: handle presence of {e}");
            if (hasHandle)
            {
                Assert.Equal(m.State[e].Handle, handle);
            }

            bool hasToken = reg.TryGetToken(new EntityId(e), out long token);
            Assert.True(hasToken == (expected == RegistrationStatus.Pending), $"{where}: token presence of {e}");
            if (hasToken)
            {
                Assert.Equal(m.State[e].Token, token);
            }
        }

        Assert.Equal(pending, reg.PendingCount);
        Assert.Equal(bound, reg.BoundCount);
        Assert.Equal(m.Arrivals.Select(a => (a.Entity, a.Handle)), reg.Arrivals.Select(a => (a.Entity.Value, a.Handle)));
        Assert.Equal(m.Orphans, reg.Orphans);
    }

    [Fact]
    public void Random_sequences_agree_with_the_reference_model()
    {
        int accepted = 0, released = 0, unknown = 0, coalesced = 0, completed = 0;
        for (int seed = 0; seed < 300; seed++)
        {
            var rng = new Random(seed);
            var reg = new AsyncRegistration<uint>();
            var m = new Model();
            var issued = new HashSet<long>();
            uint nextHandle = 100;
            for (int step = 0; step < 120; step++)
            {
                int e = rng.Next(1, Entities + 1);
                var id = new EntityId(e);
                string where = $"seed {seed} step {step}";
                switch (rng.Next(6))
                {
                    case 0:
                    case 1: // request
                    {
                        long token = reg.Request(id);
                        if (m.State.ContainsKey(e))
                        {
                            Assert.True(token == 0, $"{where}: a second request must be coalesced");
                            coalesced++;
                        }
                        else
                        {
                            Assert.True(token > m.LastToken, $"{where}: token {token} not above {m.LastToken}");
                            Assert.True(issued.Add(token), $"{where}: token {token} reused");
                            m.LastToken = token;
                            m.State[e] = (RegistrationStatus.Pending, token, 0);
                            m.Awaiting[token] = e;
                        }

                        break;
                    }

                    case 2: // answer: a token that is awaiting, one already answered / released, or one never issued
                    {
                        long token = rng.Next(4) switch
                        {
                            0 => 9999,
                            1 when issued.Count > 0 => issued.ElementAt(rng.Next(issued.Count)),
                            _ => m.Awaiting.Count > 0 ? m.Awaiting.Keys.ElementAt(rng.Next(m.Awaiting.Count)) : 9999,
                        };
                        uint handle = nextHandle++;
                        var answer = reg.OnRegistered(token, handle, out var entity);
                        if (m.Awaiting.Remove(token, out int owner))
                        {
                            Assert.True(answer == RegistrationAnswer.Accepted && entity.Value == owner, $"{where}: expected Accepted for {owner}, got {answer}");
                            m.Arrivals.Add((owner, handle));
                            accepted++;
                        }
                        else if (m.Released.Remove(token))
                        {
                            Assert.True(answer == RegistrationAnswer.Released, $"{where}: an answer after release must be Released, got {answer}");
                            m.Orphans.Add(handle);
                            released++;
                        }
                        else
                        {
                            Assert.True(answer == RegistrationAnswer.Unknown, $"{where}: expected Unknown, got {answer}");
                            unknown++;
                        }

                        break;
                    }

                    case 3: // complete one of the accepted arrivals (or an arbitrary entity)
                    {
                        uint handle = nextHandle++;
                        // valid only for a pending entity whose answer was accepted (its token is no longer awaiting)
                        bool expected = m.State.TryGetValue(e, out var s) && s.Status == RegistrationStatus.Pending && !m.Awaiting.ContainsKey(s.Token);
                        bool done = reg.Complete(id, handle);
                        Assert.True(done == expected, $"{where}: complete of {e}");
                        if (done)
                        {
                            m.State[e] = (RegistrationStatus.Bound, 0, handle);
                            completed++;
                        }

                        break;
                    }

                    case 4: // release
                    {
                        reg.Release(id);
                        if (m.State.Remove(e, out var s))
                        {
                            if (s.Status == RegistrationStatus.Pending && m.Awaiting.Remove(s.Token))
                            {
                                m.Released.Add(s.Token);
                            }
                        }

                        break;
                    }

                    default: // drain the queues like the caller does after a commit
                        reg.ClearArrivals();
                        reg.ClearOrphans();
                        m.Arrivals.Clear();
                        m.Orphans.Clear();
                        break;
                }

                Compare(reg, m, where);
            }
        }

        Assert.True(accepted > 100 && released > 20 && unknown > 100 && coalesced > 100 && completed > 50,
            $"coverage: accepted {accepted}, released {released}, unknown {unknown}, coalesced {coalesced}, completed {completed}");
    }

    [Fact]
    public void An_answer_after_release_never_binds_and_the_handle_is_an_orphan()
    {
        var reg = new AsyncRegistration<uint>();
        var e = new EntityId(1);
        long token = reg.Request(e);
        reg.Release(e);
        Assert.Equal(RegistrationAnswer.Released, reg.OnRegistered(token, 77, out _));
        Assert.Equal(RegistrationStatus.None, reg.StatusOf(e));
        Assert.Empty(reg.Arrivals);
        Assert.Equal(new uint[] { 77 }, reg.Orphans);
        Assert.False(reg.Complete(e, 77));
        // a token is answered once
        Assert.Equal(RegistrationAnswer.Unknown, reg.OnRegistered(token, 78, out _));
    }

    [Fact]
    public void An_answer_for_an_entity_that_is_not_alive_is_an_orphan_and_frees_the_entity()
    {
        var reg = new AsyncRegistration<uint>();
        var e = new EntityId(1);
        long token = reg.Request(e);
        Assert.Equal(RegistrationAnswer.Released, reg.OnRegistered(token, 9, out _, _ => false));
        Assert.Equal(RegistrationStatus.None, reg.StatusOf(e));
        Assert.Equal(0, reg.PendingCount);
        Assert.Equal(new uint[] { 9 }, reg.Orphans);
    }

    [Fact]
    public void A_released_entity_can_register_again_with_a_new_token()
    {
        var reg = new AsyncRegistration<uint>();
        var e = new EntityId(1);
        long first = reg.Request(e);
        Assert.Equal(0, reg.Request(e));
        reg.Release(e);
        long second = reg.Request(e);
        Assert.True(second > first);
        Assert.Equal(RegistrationAnswer.Released, reg.OnRegistered(first, 1, out _)); // the old answer must not bind the new registration
        Assert.Equal(RegistrationStatus.Pending, reg.StatusOf(e));
        Assert.Equal(RegistrationAnswer.Accepted, reg.OnRegistered(second, 2, out var entity));
        Assert.Equal(e, entity);
        Assert.True(reg.Complete(e, 2));
        Assert.True(reg.TryGetHandle(e, out uint handle) && handle == 2);
    }
}
