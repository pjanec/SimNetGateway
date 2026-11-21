using System.Diagnostics;
using CycloneDDS.Runtime;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.Plugin.DdsPlayer;

/// <summary>
/// Answers <c>ClockProbe</c> with <c>ClockEcho</c> (DD-022 3b), the station's half of the clock handshake, on a thread of its own. CycloneDDS sample info carries no reception time, so a
/// probe read in the per-frame gather would be stamped up to a frame late: the outbound leg would look longer than the return leg and every client's offset would be biased by about
/// half a frame. This thread blocks on the probe reader (a wait set), stamps <c>StationReceiveTicks</c> the moment the wait returns, and writes the echo at once with
/// <c>StationTransmitTicks</c> stamped just before the write. It touches only the probe reader and its own echo writer; the host's frame never runs it.
/// </summary>
internal sealed class DpsClockEcho : IDisposable
{
    private readonly DdsReader<D.ClockProbe> _reader;
    private readonly DdsWriter<D.ClockEcho> _writer;
    private readonly DdsWaitSet _waitSet;
    private readonly Func<long> _ticks;
    private readonly Action<string> _log;
    private readonly CancellationTokenSource _stop = new();
    private readonly Action? _afterWait;
    private readonly Thread _thread;

    /// <param name="afterWait">A test seam: runs between the receive stamp and the take (a slow reader), which must not move the stamp.</param>
    public DpsClockEcho(DdsParticipant participant, Func<long> ticks, Action<string> log, Action? afterWait = null)
    {
        _afterWait = afterWait;
        _ticks = ticks;
        _log = log;
        _reader = new DdsReader<D.ClockProbe>(participant);
        _writer = new DdsWriter<D.ClockEcho>(participant);
        _waitSet = new DdsWaitSet(participant);
        _waitSet.Attach(_reader);
        _thread = new Thread(Run) { IsBackground = true, Name = "dps-clock-echo" };
        _thread.Start();
    }

    /// <summary>Probes answered (tests and diagnostics).</summary>
    public int Answered => Volatile.Read(ref _answered);

    private int _answered;

    private void Run()
    {
        var triggered = new IDdsReader[1];
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                int n = _waitSet.Wait(triggered, TimeSpan.FromMilliseconds(250), _stop.Token);
                long received = _ticks();   // right when the wait returns: the only reception time there is
                if (n <= 0)
                {
                    continue;
                }

                _afterWait?.Invoke();
                using var loan = _reader.Take(32);
                foreach (var sample in loan)
                {
                    if (!sample.IsValid || sample.Info.InstanceState != DdsInstanceState.Alive)
                    {
                        continue;
                    }

                    var probe = sample.Data;
                    _writer.Write(new D.ClockEcho
                    {
                        ClientId = probe.ClientId,
                        ClientSendTicks = probe.ClientSendTicks,
                        StationReceiveTicks = received,
                        StationTransmitTicks = _ticks(),   // just before the write
                    });
                    Interlocked.Increment(ref _answered);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _log($"dps: the clock echo thread stopped: {ex.GetType().Name}: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        if (!_thread.Join(TimeSpan.FromSeconds(3)))
        {
            _log("dps: the clock echo thread did not stop in time");
        }

        _waitSet.Dispose();
        _reader.Dispose();
        _writer.Dispose();
        _stop.Dispose();
    }

    /// <summary>The station's wall tick when no host clock is given: UTC read once plus a monotonic stopwatch, 100 ns since 0001-01-01 UTC (the shape of the host's own clock).</summary>
    internal static class DefaultClock
    {
        private static readonly long UtcBaseline = DateTime.UtcNow.Ticks;
        private static readonly long StopwatchBaseline = Stopwatch.GetTimestamp();

        public static long Ticks() => UtcBaseline + Stopwatch.GetElapsedTime(StopwatchBaseline, Stopwatch.GetTimestamp()).Ticks;
    }
}
