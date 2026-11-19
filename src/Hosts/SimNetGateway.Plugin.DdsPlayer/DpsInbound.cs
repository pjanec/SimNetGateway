using System.Collections.Concurrent;
using CycloneDDS.Runtime;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// The player topics (client to gateway) driving the player-station seam (DD-017 3.2, 3.6): <c>JoinRequest</c>, <c>ControlFrame</c> and <c>Leave</c>, read in the
/// station's partition. Liveliness is the presence signal: when the owning client's writer is lost (<c>AliveCountChange &lt; 0</c> or <c>NotAliveCountChange &gt; 0</c>)
/// the unit is released once and the slot is free for the next join. One player at a time: the first writer seen on a topic owns it, samples of any other writer are
/// dropped with one warning per writer. Thread rule: the DDS listener callbacks only record into queues; every call into the seam happens in <see cref="Pump"/>, on the
/// host's main thread.
/// </summary>
internal sealed class DpsInbound : IDisposable
{
    private readonly record struct Liveliness(int Topic, long Handle, int AliveChange, int NotAliveChange, uint AliveCount);

    private sealed class Topic<T>
        where T : struct
    {
        public readonly DdsReader<T> Reader;
        public long Owner;

        public Topic(DdsReader<T> reader) => Reader = reader;
    }

    private const int JoinTopic = 0, ControlTopic = 1, LeaveTopic = 2;

    private readonly IPlayerStation? _station;
    private readonly Action<string> _log;
    private readonly Topic<D.JoinRequest> _join;
    private readonly Topic<D.ControlFrame> _control;
    private readonly Topic<D.Leave> _leave;
    private readonly ConcurrentQueue<Liveliness> _liveliness = new();
    private readonly HashSet<long> _warned = new();
    private bool _hasJoinSeq, _hasControlSeq, _hasLeaveSeq;
    private uint _joinSeq, _controlSeq, _leaveSeq;

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

    /// <summary>The owner of a topic going away means the client is gone: release the unit once and free every topic for the next client.</summary>
    private void HandleLiveliness()
    {
        bool lost = false;
        while (_liveliness.TryDequeue(out var e))
        {
            if (e.AliveChange >= 0 && e.NotAliveChange <= 0)
            {
                continue;
            }

            long owner = e.Topic switch { JoinTopic => _join.Owner, ControlTopic => _control.Owner, _ => _leave.Owner };
            if (owner != 0 && (e.Handle == owner || e.AliveCount == 0))
            {
                lost = true;
            }
        }

        if (!lost)
        {
            return;
        }

        _station!.Leave();
        _join.Owner = _control.Owner = _leave.Owner = 0;
        _hasJoinSeq = _hasControlSeq = _hasLeaveSeq = false;
    }

    private void ReadJoin()
    {
        using var loan = _join.Reader.Take(16);
        foreach (var sample in loan)
        {
            if (!sample.IsValid || !Accept(_join, sample.Info.PublicationHandle, "JoinRequest"))
            {
                continue;
            }

            var request = sample.Data;
            if (_hasJoinSeq && !Newer(request.RequestSeq, _joinSeq))
            {
                continue;
            }

            _hasJoinSeq = true;
            _joinSeq = request.RequestSeq;
            var kind = request.Kind switch
            {
                D.JoinKind.JoinKindUnit => PlayerJoinKind.Unit,
                D.JoinKind.JoinKindRole => PlayerJoinKind.Role,
                _ => PlayerJoinKind.Observe,
            };
            _station!.Join(new PlayerJoin(kind, request.UnitId, string.IsNullOrEmpty(request.Role) ? null : request.Role));
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
                if (!sample.IsValid || !Accept(_control, sample.Info.PublicationHandle, "ControlFrame"))
                {
                    continue;
                }

                var frame = sample.Data;
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
            if (!sample.IsValid || !Accept(_leave, sample.Info.PublicationHandle, "Leave"))
            {
                continue;
            }

            if (_hasLeaveSeq && !Newer(sample.Data.Seq, _leaveSeq))
            {
                continue;
            }

            _hasLeaveSeq = true;
            _leaveSeq = sample.Data.Seq;
            _station!.Leave();
        }
    }

    /// <summary>The first writer seen on a topic owns it; another writer's samples are dropped, with one warning per writer.</summary>
    private bool Accept<T>(Topic<T> topic, long handle, string name)
        where T : struct
    {
        if (topic.Owner == 0)
        {
            topic.Owner = handle;
            return true;
        }

        if (topic.Owner == handle)
        {
            return true;
        }

        if (_warned.Add(handle))
        {
            _log($"dps: a second player writer on {name} is ignored while another client is the player of this station");
        }

        return false;
    }

    /// <summary>A sequence number newer than the last, with wrap-around.</summary>
    private static bool Newer(uint candidate, uint last) => unchecked((int)(candidate - last)) > 0;

    private static PlayerControl ToControl(in D.ControlFrame f) => new(
        f.Seq, f.Forward, f.Lateral, f.Steer, f.Yaw, f.Vertical, f.Gait, f.Trigger != 0, f.Mount, new HrotVec3(f.AimPoint.X, f.AimPoint.Y, f.AimPoint.Z), f.Stance,
        f.ShotSeq, f.CancelSeq, f.AmmoSeq, f.ShotTarget, f.UseSeq, f.UseVerb, f.UseTarget, f.Lean, f.Binoculars != 0, new HrotVec3(f.LookFrom.X, f.LookFrom.Y, f.LookFrom.Z),
        new HrotVec3(f.LookDir.X, f.LookDir.Y, f.LookDir.Z), f.FirePref);

    public void Dispose()
    {
        _join.Reader.Dispose();
        _control.Reader.Dispose();
        _leave.Reader.Dispose();
    }
}
