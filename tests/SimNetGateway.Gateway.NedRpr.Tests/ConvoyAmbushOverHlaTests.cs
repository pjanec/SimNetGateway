using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using SimNetGateway.Showcase;

namespace SimNetGateway.Gateway.NedRpr.Tests;

/// <summary>
/// Convoy Ambush with the RPR side over HLA (DD-008 section 12b, stage HLA-3). The golden files in <c>Golden/</c> are what the
/// gateway sent to NED, RPR and the display when the RPR side was the message-level RTI stand-in of stage 7 (taken before it was retired in HLA-3):
/// the run over the fake RTI must send the same, except where the RTI legitimately differs - the guid of an event that came from
/// RPR (HLA carries no guid, the transport correlates one) and the numbers of the RPR object handles (the RTI assigns them).
/// </summary>
public class ConvoyAmbushOverHlaTests
{
    private sealed record Run(ScenarioReport Report, ScenarioContext Context);

    private static readonly Lazy<Run> Shared = new(() =>
    {
        var runner = new ScenarioRunner();
        var report = runner.Run(ConvoyAmbushScript.Create());
        return new Run(report, runner.Context!);
    });

    private static string[] Golden(string name) =>
        File.ReadAllLines(Path.Combine(AppContext.BaseDirectory, "Golden", name));

    [Fact]
    public void All_48_checks_pass_with_the_rpr_side_on_the_fake_rti_and_the_rti_invariant_G9_holds_at_every_tick()
    {
        var run = Shared.Value;

        Assert.Equal(48, run.Report.Conditions.Count);
        Assert.All(run.Report.Conditions, c => Assert.True(c.Passed, c.Id));
        Assert.Empty(run.Report.Violations);
        Assert.Empty(run.Report.StepFailures);
        Assert.True(run.Context.Checker.CheckRti);
        Assert.Equal(8400, run.Context.Checker.Checks);
    }

    [Fact]
    public void The_rpr_side_really_crossed_the_rti()
    {
        var ctx = Shared.Value.Context;
        var trace = ctx.Host.Hub.Trace;

        Assert.Contains(trace, line => line.Contains("RegisterObjectInstance", StringComparison.Ordinal));
        Assert.Contains(trace, line => line.Contains("UpdateAttributeValues", StringComparison.Ordinal));
        Assert.Contains(trace, line => line.Contains("SendInteraction", StringComparison.Ordinal));
        Assert.Contains(trace, line => line.Contains("DeleteObjectInstance", StringComparison.Ordinal));
        Assert.Contains(trace, line => line.Contains("callback ReflectAttributeValues", StringComparison.Ordinal));
        Assert.Equal(ctx.Host.RegisteredCount, ctx.Peer.Discovered.Count); // the federate discovered every object the gateway registered
        Assert.True(ctx.Peer.Discovered.Count >= 10);
        Assert.Equal(0, ctx.Host.RprConnection.DecodeErrors + ctx.Host.RprConnection.EncodeErrors + ctx.Host.RprConnection.SendErrors);
        Assert.Equal(4, ctx.Host.RprConnection.DroppedSimulationControl); // Start, Pause, Resume, Start of the replay: RPR FOM 2.0 has no simulation control
    }

    [Fact]
    public void The_gateway_sent_to_NED_exactly_what_it_sent_with_the_message_level_rti()
    {
        var ctx = Shared.Value.Context;

        var actual = ctx.Ned.Messages.Select(m => Normalize($"{m.Tick} {MessageText.Format(m.Message)}")).ToArray();

        Assert.Equal(Golden("ConvoyAmbushNed.txt"), actual);
    }

    [Fact]
    public void The_gateway_sent_to_the_display_exactly_what_it_sent_with_the_message_level_rti()
    {
        var ctx = Shared.Value.Context;
        var text = new StringBuilder();
        foreach (var m in ctx.Display.Messages)
        {
            text.Append(m.Tick).Append(' ').Append(MessageText.Format(m.Message)).Append('\n');
        }

        string hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))).ToLowerInvariant();

        Assert.Equal(5334, ctx.Display.Messages.Count);
        Assert.Equal(Golden("ConvoyAmbushDisplay.sha256")[0], hash);
    }

    [Fact]
    public void The_gateway_sent_to_the_rpr_side_what_it_sent_with_the_message_level_rti_up_to_the_numbers_of_the_handles()
    {
        var ctx = Shared.Value.Context;
        var handles = new Dictionary<string, string>(StringComparer.Ordinal);
        string Handle(string value) => handles.TryGetValue(value, out string? name) ? name : handles[value] = "H" + handles.Count;

        var actual = ctx.Rpr.Messages.Select(m =>
        {
            string line = $"{m.Tick} {MessageText.Format(m.Message)}";
            line = Regex.Replace(line, @"\bh=(0x[0-9A-Fa-f]+)", x => "h=" + Handle(x.Groups[1].Value));
            line = Regex.Replace(line, @"(firing|target)=(0x[0-9A-Fa-f]+)", x => x.Groups[1].Value + "=" + Handle(x.Groups[2].Value));
            return Normalize(line);
        }).ToArray();

        Assert.Equal(Golden("ConvoyAmbushRpr.txt"), actual);
    }

    /// <summary>The event guids are the transport's (HLA carries none): masked, with the first 8 hex digits of every fire and detonation id.</summary>
    private static string Normalize(string line) =>
        Regex.Replace(Regex.Replace(line, "fire=[0-9a-f]{8}", "fire=X"), "det=[0-9a-f]{8}", "det=X");

    [Fact]
    public void The_federate_holds_every_object_the_gateway_has_published_and_nothing_else_after_the_unload()
    {
        var ctx = Shared.Value.Context;

        Assert.Empty(ctx.Host.RprPeer.Objects); // every object the gateway registered was deleted again (S-10.1 and S-11.3 at the RTI)
    }
}
