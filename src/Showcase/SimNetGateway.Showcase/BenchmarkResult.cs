using System.Globalization;
using System.Text;

namespace SimNetGateway.Showcase;

/// <summary>
/// The outcome of one benchmark run (<see cref="BenchmarkRunner"/>). Counts and rates are in simulated time (the gateway is
/// driven by a manual clock); tick times are real milliseconds of the engine step (RTI delivery plus
/// <c>Engine.Tick()</c>) - the senders' own work is not part of them.
/// </summary>
public sealed record BenchmarkResult
{
    public required string Configuration { get; init; }

    public required int Entities { get; init; }

    public required int NedEntities { get; init; }

    public required int RprEntities { get; init; }

    public required int StaticNedEntities { get; init; }

    public required int StaticRprEntities { get; init; }

    public required int Seed { get; init; }

    public required double ThresholdMetres { get; init; }

    /// <summary>Simulated seconds of the measurement phase.</summary>
    public required double MeasuredSeconds { get; init; }

    public required double WarmupSeconds { get; init; }

    /// <summary>Ticks of the measurement phase.</summary>
    public required long Ticks { get; init; }

    /// <summary>Ticks the setup phase needed until every entity was published and registered.</summary>
    public required long SetupTicks { get; init; }

    /// <summary>Messages the NED senders sent to the gateway during the measurement.</summary>
    public required long MessagesInNed { get; init; }

    /// <summary>Messages from the RPR side during the measurement: the RPR senders' updates and the RTI's registrations.</summary>
    public required long MessagesInRpr { get; init; }

    /// <summary>Messages of static NED entities during the measurement (none are expected).</summary>
    public required long MessagesInStaticNed { get; init; }

    /// <summary>Messages of static RPR entities during the measurement (their heartbeats).</summary>
    public required long MessagesInStaticRpr { get; init; }

    public required long MessagesOutNed { get; init; }

    public required long MessagesOutRpr { get; init; }

    public required long MessagesOutDisplay { get; init; }

    /// <summary>Committed changes of <c>Derived.PredictedPosition</c> during the measurement.</summary>
    public required long DerivedWrites { get; init; }

    public required double TickMeanMs { get; init; }

    public required double TickP50Ms { get; init; }

    public required double TickP95Ms { get; init; }

    public required double TickP99Ms { get; init; }

    public required double TickMaxMs { get; init; }

    /// <summary>Bytes allocated on the engine thread inside the engine step during the measurement.</summary>
    public required long AllocatedBytes { get; init; }

    public required int Gen0Collections { get; init; }

    public required int Gen1Collections { get; init; }

    public required int Gen2Collections { get; init; }

    /// <summary>Entities of the NED side that have an RPR handle at the end of the run.</summary>
    public required int PublishedToRpr { get; init; }

    /// <summary>Entities of the RPR side that have a NED id at the end of the run.</summary>
    public required int PublishedToNed { get; init; }

    public required int FinalEntities { get; init; }

    public required int DiagnosticWarnings { get; init; }

    public required int DiagnosticErrors { get; init; }

    public long MessagesIn => MessagesInNed + MessagesInRpr;

    public long MessagesOut => MessagesOutNed + MessagesOutRpr + MessagesOutDisplay;

    public double MessagesInPerSecond => PerSecond(MessagesIn);

    public double MessagesInNedPerSecond => PerSecond(MessagesInNed);

    public double MessagesInRprPerSecond => PerSecond(MessagesInRpr);

    public double MessagesOutNedPerSecond => PerSecond(MessagesOutNed);

    public double MessagesOutRprPerSecond => PerSecond(MessagesOutRpr);

    public double MessagesOutDisplayPerSecond => PerSecond(MessagesOutDisplay);

    public double DerivedWritesPerSecond => PerSecond(DerivedWrites);

    /// <summary>Simulated seconds divided by the real seconds the engine steps took.</summary>
    public double RealTimeFactor
    {
        get
        {
            double real = TickMeanMs * Ticks / 1000.0;
            return real > 0 ? MeasuredSeconds / real : double.PositiveInfinity;
        }
    }

    public double AllocatedBytesPerMessageIn => MessagesIn > 0 ? (double)AllocatedBytes / MessagesIn : 0.0;

    private double PerSecond(long count) => MeasuredSeconds > 0 ? count / MeasuredSeconds : 0.0;

    /// <summary>A plain-text report (invariant culture).</summary>
    public string Format()
    {
        var c = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        sb.Append("Benchmark ").Append(Configuration).Append('\n');
        sb.Append(string.Create(c, $"  entities               {Entities} ({NedEntities} NED-origin, {RprEntities} RPR-origin; static {StaticNedEntities} + {StaticRprEntities})\n"));
        sb.Append(string.Create(c, $"  seed / threshold       {Seed} / {ThresholdMetres:F2} m\n"));
        sb.Append(string.Create(c, $"  phases                 setup {SetupTicks} ticks, warm-up {WarmupSeconds:F1} s (excluded), measurement {MeasuredSeconds:F1} s ({Ticks} ticks)\n"));
        sb.Append(string.Create(c, $"  messages in            {MessagesInPerSecond:F0}/s (NED {MessagesInNedPerSecond:F0}/s, RPR {MessagesInRprPerSecond:F0}/s) = {MessagesInPerSecond / Entities:F3} per entity per second\n"));
        sb.Append(string.Create(c, $"  messages out           NED {MessagesOutNedPerSecond:F0}/s, RPR {MessagesOutRprPerSecond:F0}/s, display {MessagesOutDisplayPerSecond:F0}/s\n"));
        sb.Append(string.Create(c, $"  derived writes         {DerivedWritesPerSecond:F0}/s (Derived.PredictedPosition changes)\n"));
        sb.Append(string.Create(c, $"  tick time (ms)         mean {TickMeanMs:F3}  p50 {TickP50Ms:F3}  p95 {TickP95Ms:F3}  p99 {TickP99Ms:F3}  max {TickMaxMs:F3}\n"));
        sb.Append(string.Create(c, $"  real-time factor       {RealTimeFactor:F1}x (simulated time / engine step time)\n"));
        sb.Append(string.Create(c, $"  allocated              {AllocatedBytes} bytes in the measurement, {AllocatedBytesPerMessageIn:F1} bytes per inbound message\n"));
        sb.Append(string.Create(c, $"  GC collections         gen0 {Gen0Collections}, gen1 {Gen1Collections}, gen2 {Gen2Collections}\n"));
        sb.Append(string.Create(c, $"  published              {PublishedToRpr}/{NedEntities} NED entities to RPR, {PublishedToNed}/{RprEntities} RPR entities to NED\n"));
        sb.Append(string.Create(c, $"  final entities         {FinalEntities}\n"));
        sb.Append(string.Create(c, $"  diagnostics            {DiagnosticWarnings} warnings, {DiagnosticErrors} errors\n"));
        return sb.ToString();
    }
}
