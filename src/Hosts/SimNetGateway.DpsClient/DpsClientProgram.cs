using System.Diagnostics;
using System.Globalization;
using SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.DpsClient;

/// <summary>
/// The command line of the reference client: <c>--domain N --partition NAME [--headless --seconds N]</c>. Headless play for automated checks:
/// <c>--join</c>, <c>--forward F --drive-seconds N</c>, <c>--fire-after S</c>, <c>--leave-after S</c> (seconds of Playing).
/// </summary>
public static class DpsClientProgram
{
    public const string Usage = "usage: DpsClient --domain N --partition NAME [--headless --seconds N [--join] [--forward F --drive-seconds N] [--fire-after S] [--leave-after S]]";

    private static readonly string[] SummaryTopics =
        [DpsProtocol.Entity, DpsProtocol.EntityState, DpsProtocol.PersonState, DpsProtocol.Appearance, DpsProtocol.VehicleLook, DpsProtocol.Fire, DpsProtocol.Detonation,
         DpsProtocol.Effect, DpsProtocol.Articulation,
         DpsProtocol.Round, DpsProtocol.MunitionLook, DpsProtocol.FlameJet, DpsProtocol.FireSpots, DpsProtocol.GroundMarks, DpsProtocol.Animal, DpsProtocol.TkbType,
         DpsProtocol.BuildingDamage, DpsProtocol.ManDowned, DpsProtocol.PlayerMessage, DpsProtocol.Status, DpsProtocol.Exercise, DpsProtocol.Mover, DpsProtocol.MoverTracks,
         DpsProtocol.TrafficLights];

    public static int Run(string[] args, TextWriter output, TextWriter error)
    {
        uint domain = 0;
        string partition = "station-1";
        bool headless = false, join = false;
        double seconds = 5.0, driveSeconds = 0, fireAfter = -1, leaveAfter = -1;
        float forward = 0;

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string? v = i + 1 < args.Length ? args[i + 1] : null;
            bool Num(out double d) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out d) && double.IsFinite(d);
            double n;
            switch (a)
            {
                case "--domain" when uint.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var dom):
                    domain = dom;
                    i++;
                    break;
                case "--partition" when v is not null:
                    partition = v;
                    i++;
                    break;
                case "--seconds" when Num(out n) && n >= 0:
                    seconds = n;
                    i++;
                    break;
                case "--forward" when Num(out n):
                    forward = (float)n;
                    i++;
                    break;
                case "--drive-seconds" when Num(out n) && n >= 0:
                    driveSeconds = n;
                    i++;
                    break;
                case "--fire-after" when Num(out n) && n >= 0:
                    fireAfter = n;
                    i++;
                    break;
                case "--leave-after" when Num(out n) && n >= 0:
                    leaveAfter = n;
                    i++;
                    break;
                case "--headless":
                    headless = true;
                    break;
                case "--join":
                    join = true;
                    break;
                default:
                    error.WriteLine($"unknown or incomplete argument: {a}");
                    error.WriteLine(Usage);
                    return 2;
            }
        }

        using var world = new DpsWorld(domain, partition);
        if (!headless)
        {
            return DpsClientWindow.Run(world, domain, partition);
        }

        var sw = Stopwatch.StartNew();
        double nextLine = 1.0, nextFrame = 0, playingSince = -1;
        bool fired = false, left = false, triggerFrame = false;
        while (sw.Elapsed.TotalSeconds < seconds)
        {
            double t = sw.Elapsed.TotalSeconds;
            world.Pump();

            if (join && !world.IsPlaying)
            {
                world.TryAutoJoin();
            }

            if (world.IsPlaying)
            {
                if (playingSince < 0)
                {
                    playingSince = t;
                }

                double p = t - playingSince;
                bool controlling = (driveSeconds > 0 || fireAfter >= 0) && !left;
                if (leaveAfter >= 0 && p >= leaveAfter && !left)
                {
                    world.Leave();
                    left = true;
                }

                if (controlling && !left && t >= nextFrame)
                {
                    world.SetMove(p < driveSeconds ? forward : 0f, 0f);
                    if (triggerFrame)
                    {
                        world.SetTrigger(false);
                        triggerFrame = false;
                    }

                    if (fireAfter >= 0 && !fired && p >= fireAfter)
                    {
                        world.PressFire();
                        world.SetTrigger(true);
                        fired = true;
                        triggerFrame = true;
                    }

                    world.SendControl();
                    nextFrame = t + 1.0 / 30.0;
                }
            }
            else
            {
                playingSince = -1;
            }

            if (t >= nextLine)
            {
                output.WriteLine(Line(world, t));
                nextLine += 1.0;
            }

            Thread.Sleep(10);
        }

        world.Pump();
        output.WriteLine(
            $"entities={world.Entities.Count} slot={(world.Slot is { } slot ? slot.State.ToString() : "none")} gizmo_layers={world.Gizmos.Count} client_id={world.ClientId}");
        output.WriteLine("summary " + string.Join(' ', SummaryTopics.Select(topic => $"{topic.Replace("DPS_", string.Empty)}={world.Received.GetValueOrDefault(topic)}")) + $" own_fire={world.OwnFire}");
        return 0;
    }

    private static string Line(DpsWorld w, double t)
    {
        string state = w.Slot is { } s ? $"{s.State}/{s.UnitId}" : "none/0";
        string pos = "n/a";
        if (w.Slot is { } slot && w.Entities.TryGetValue(slot.UnitId, out var own) && own.State is { } st)
        {
            pos = FormattableString.Invariant($"{st.Position.X:0.0},{st.Position.Y:0.0},{st.Position.Z:0.0}");
        }

        string health = w.Own is { } o ? FormattableString.Invariant($"{o.Health:0.#}/{o.MaxHealth:0.#}") : "n/a";
        string sim = w.LatestSimTime > 0 ? FormattableString.Invariant($"{w.LatestSimTime:0.0}") : "n/a";
        return $"t={t:0} sim={sim} entities={w.Entities.Count} slot={state} pos={pos} health={health} gizmo_layers={w.Gizmos.Count}";
    }
}
