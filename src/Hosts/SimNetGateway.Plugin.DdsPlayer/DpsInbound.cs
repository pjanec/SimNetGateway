using System.Collections.Concurrent;
using CycloneDDS.Runtime;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// The player topics (client to gateway) driving the player-station seam (DD-017 3.2, 3.6): <c>JoinRequest</c>, <c>ControlFrame</c> and <c>Leave</c>, read in the
/// station's partition. Liveliness is the presence signal: when the owning client's writer is lost (<c>AliveCountChange &lt; 0</c> or <c>NotAliveCountChange &gt; 0</c>)
/// the unit is released once and the slot is free for the next join. One player at a time, across all three topics: the player is the client (its <c>ClientId</c>) whose
/// <c>JoinRequest</c> the seam accepted; samples of another client on any topic are dropped with one warning per foreign client. The publication handles that carried the
/// player's client id are remembered, and losing any of them is losing the client. A join whose writer is already gone (its instance is not alive, or its writer's liveliness
/// was lost) is never accepted, so a client that wrote a join and vanished in the same host frame cannot become a player nobody can release. The topics are keyed by
/// <c>ClientId</c>, so one client's samples never displace another's. Thread rule: the DDS listener callbacks only record into queues; every call into the seam happens in <see cref="Pump"/>, on the
/// host's main thread.
/// </summary>
internal sealed class DpsInbound : IDisposable
{
    private readonly record struct Liveliness(int Topic, long Handle, int AliveChange, int NotAliveChange, uint AliveCount);

    private sealed class Topic<T>
        where T : struct
    {
        public readonly DdsReader<T> Reader;

        public Topic(DdsReader<T> reader) => Reader = reader;
    }

    private const int JoinTopic = 0, ControlTopic = 1, LeaveTopic = 2;

    private readonly IPlayerStation? _station;
    private readonly Action<string> _log;
    private readonly Topic<D.JoinRequest> _join;
    private readonly Topic<D.ControlFrame> _control;
    private readonly Topic<D.Leave> _leave;
    private readonly ConcurrentQueue<Liveliness> _liveliness = new();
    private readonly HashSet<ulong> _warned = new();
    private readonly HashSet<(int Topic, long Handle)> _ownerHandles = new();
    private readonly HashSet<long> _lostHandles = new();
    private ulong _owner;
    private bool _hasJoinSeq, _hasControlSeq;
    private uint _joinSeq, _controlSeq;

    public DpsInbound(DdsParticipant participant, DpsSettings settings, IPlayerStation? station, Action<string> log)
    {
        _station = station;
        _log = log;
        var options = new DdsReaderOptions { LivelinessLeaseDuration = TimeSpan.FromSeconds(settings.LeaseSeconds) };
        _join = new Topic<D.JoinRequest>(new DdsReader<D.JoinRequest>(participant, null, options, null));
        _control = new Topic<D.ControlFrame>(new DdsReader<D.ControlFrame>(participant, null, options, null));
        _leave = new Topic<D.Leave>(new DdsReader<D.Leave>(participant, null, options, null));
        Listen(_join.Reader, JoinTopic);
        Listen(_control.Reader, ControlTopic);
        Listen(_leave.Reader, LeaveTopic);
    }

    /// <summary>A listener only records.</summary>
    private void Listen<T>(DdsReader<T> reader, int topic)
        where T : struct =>
        reader.LivelinessChanged += (_, st) => _liveliness.Enqueue(new Liveliness(topic, st.LastPublicationHandle, st.AliveCountChange, st.NotAliveCountChange, st.AliveCount));

    /// <summary>The client id of the player the station accepted (0 = nobody plays).</summary>
    internal ulong Owner => _owner;

    public void Pump()
    {
        if (_station is null)
        {
            Discard();   // a host with no player mode: the player topics are ignored
            return;
        }

        HandleLiveliness();
        ReadJoin();
        ReadControl();
        ReadLeave();
    }

    private void Discard()
    {
        using (_join.Reader.Take(16))
        {
        }

        using (_control.Reader.Take(16))
        {
        }

        using (_leave.Reader.Take(16))
        {
        }

        while (_liveliness.TryDequeue(out _))
        {
        }
    }

    /// <summary>
    /// The player's client going away (any writer that carried its client id): release the unit once and free the station for the next client. Every lost writer is
    /// remembered, also while nobody plays, so a join sample it left behind is not accepted later (<see cref="ReadJoin"/>).
    /// </summary>
    private void HandleLiveliness()
    {
        bool lost = false;
        while (_liveliness.TryDequeue(out var e))
        {
            if (e.AliveChange >= 0 && e.NotAliveChange <= 0)
            {
                if (e.AliveChange > 0)
                {
                    _lostHandles.Remove(e.Handle);   // the writer is alive again
                }

                continue;
            }

            _lostHandles.Add(e.Handle);
            if (_owner == 0)
            {
                continue;
            }

            if (_ownerHandles.Contains((e.Topic, e.Handle)) || (e.AliveCount == 0 && _ownerHandles.Any(h => h.Topic == e.Topic)))
            {
                lost = true;
            }
        }

        if (!lost)
        {
            return;
        }

        _station!.Leave();
        ClearOwner();
    }

    private void ClearOwner()
    {
        _owner = 0;
        _ownerHandles.Clear();
        _hasJoinSeq = _hasControlSeq = false;
    }

    private void ReadJoin()
    {
        using var loan = _join.Reader.Take(16);
        foreach (var sample in loan)
        {
            // a join of a client that is already gone (its writer deleted or its liveliness lost) is never accepted: nobody would be left to release the unit
            if (!sample.IsValid || sample.Info.InstanceState != DdsInstanceState.Alive || _lostHandles.Contains(sample.Info.PublicationHandle))
            {
                continue;
            }

            var request = sample.Data;
            if (!IsKnownClient(request.ClientId, "JoinRequest") || (_owner != 0 && !FromOwner(request.ClientId, "JoinRequest")))
            {
                continue;
            }

            if (_owner == request.ClientId && _hasJoinSeq && !Newer(request.RequestSeq, _joinSeq))
            {
                continue;
            }

            var kind = request.Kind switch
            {
                D.JoinKind.JoinKindUnit => PlayerJoinKind.Unit,
                D.JoinKind.JoinKindRole => PlayerJoinKind.Role,
                _ => PlayerJoinKind.Observe,
            };
            bool accepted = _station!.Join(new PlayerJoin(kind, request.UnitId, string.IsNullOrEmpty(request.Role) ? null : request.Role, request.Side, request.Kit, request.AreaId));
            if (_owner == request.ClientId)
            {
                _hasJoinSeq = true;
                _joinSeq = request.RequestSeq;
            }
            else if (accepted)
            {
                // the seam took this client's join: it is the player of the station until it leaves or is lost
                _owner = request.ClientId;
                _hasJoinSeq = true;
                _joinSeq = request.RequestSeq;
            }

            if (_owner == request.ClientId)
            {
                _ownerHandles.Add((JoinTopic, sample.Info.PublicationHandle));
            }
        }
    }

    /// <summary>One control frame per pump: the newest by sequence number (a client may send several between two frames of the host).</summary>
    private void ReadControl()
    {
        D.ControlFrame newest = default;
        bool any = false;
        using (var loan = _control.Reader.Take(64))
        {
            foreach (var sample in loan)
            {
                if (!sample.IsValid)
                {
                    continue;
                }

                var frame = sample.Data;
                if (!IsPlayer(frame.ClientId, "ControlFrame"))
                {
                    continue;
                }

                _ownerHandles.Add((ControlTopic, sample.Info.PublicationHandle));
                if (_hasControlSeq && !Newer(frame.Seq, _controlSeq))
                {
                    continue;
                }

                if (!any || Newer(frame.Seq, newest.Seq))
                {
                    newest = frame;
                    any = true;
                }
            }
        }

        if (!any)
        {
            return;
        }

        _hasControlSeq = true;
        _controlSeq = newest.Seq;
        _station!.Control(ToControl(in newest));
    }

    private void ReadLeave()
    {
        using var loan = _leave.Reader.Take(16);
        foreach (var sample in loan)
        {
            if (!sample.IsValid || !IsPlayer(sample.Data.ClientId, "Leave"))
            {
                continue;
            }

            _ownerHandles.Add((LeaveTopic, sample.Info.PublicationHandle));
            _station!.Leave();
            ClearOwner();   // the player left: the station is free for the next client
        }
    }

    /// <summary>A control frame or a leave counts only from the player; with no player (nobody joined yet) it is ignored quietly, from another client with one warning.</summary>
    private bool IsPlayer(ulong clientId, string name) =>
        IsKnownClient(clientId, name) && _owner != 0 && FromOwner(clientId, name);

    private bool IsKnownClient(ulong clientId, string name)
    {
        if (clientId != 0)
        {
            return true;
        }

        if (_warned.Add(0))
        {
            _log($"dps: a {name} without a client id is ignored (a client puts a nonzero ClientId on every player sample)");
        }

        return false;
    }

    /// <summary>True for the player's own client id; another client's sample is dropped with one warning per foreign client.</summary>
    private bool FromOwner(ulong clientId, string name)
    {
        if (clientId == _owner)
        {
            return true;
        }

        if (_warned.Add(clientId))
        {
            _log($"dps: a second player (client id {clientId}) is ignored on {name} while another client is the player of this station");
        }

        return false;
    }

    /// <summary>A sequence number newer than the last, with wrap-around.</summary>
    private static bool Newer(uint candidate, uint last) => unchecked((int)(candidate - last)) > 0;

    /// <summary>
    /// The one rule for the control frame's old and new fields (DD-020 3.3): a new enumeration that is set (not 0) wins, one that is 0 falls back to the old byte, so a client
    /// that fills only the old bytes (or only the new enumerations) works. The new gait and stance are written as the numbers the simulation takes (walk 1, run 0; stance + 1);
    /// the use verb crosses as its name, the host maps it.
    /// </summary>
    internal static PlayerControl ToControl(in D.ControlFrame f) => new(
        f.Seq, f.Forward, f.Lateral, f.Steer, f.Yaw, f.Vertical, GaitOf(in f), f.Trigger != 0, f.Mount, new HrotVec3(f.AimPoint.X, f.AimPoint.Y, f.AimPoint.Z), StanceOf(in f),
        f.ShotSeq, f.CancelSeq, f.AmmoSeq, f.ShotTarget, f.UseSeq, f.UseVerb, f.UseTarget, f.Lean, f.Binoculars != 0, new HrotVec3(f.LookFrom.X, f.LookFrom.Y, f.LookFrom.Z),
        new HrotVec3(f.LookDir.X, f.LookDir.Y, f.LookDir.Z), f.FirePref, VerbNameOf(in f));

    private static byte GaitOf(in D.ControlFrame f) => f.GaitMode switch
    {
        D.Gait.GaitWalk => 1,
        D.Gait.GaitRun => 0,
        _ => f.Gait,
    };

    private static byte StanceOf(in D.ControlFrame f) => f.StanceWanted switch
    {
        D.StanceRequest.StanceRequestStanding => 1,
        D.StanceRequest.StanceRequestCrouched => 2,
        D.StanceRequest.StanceRequestProne => 3,
        _ => f.Stance,
    };

    private static string VerbNameOf(in D.ControlFrame f) =>
        f.Verb == D.UseVerb.UseVerbNone || !Enum.IsDefined(f.Verb) ? string.Empty : f.Verb.ToString()["UseVerb".Length..];

    public void Dispose()
    {
        _join.Reader.Dispose();
        _control.Reader.Dispose();
        _leave.Reader.Dispose();
    }
}
