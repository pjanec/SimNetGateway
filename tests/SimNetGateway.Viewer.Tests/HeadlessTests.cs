using SimNetGateway.Engine;
using SimNetGateway.Showcase;

namespace SimNetGateway.Viewer.Tests;

/// <summary>
/// The viewer without a window (DD-005 section 8, R-076): <c>--headless --frames N</c> runs Convoy Ambush through the frame loop with
/// every model refreshed by the policy and the tracer attached, and ends with the report the acceptance test produces.
/// </summary>
public class HeadlessTests
{
    private static string AcceptanceReport() => new ScenarioRunner().Run(ConvoyAmbushScript.Create()).Render();

    [Fact]
    public void Headless_Convoy_Ambush_passes_and_prints_the_PASS_line_without_touching_the_window()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var launcher = new RecordingLauncher();

        int exit = ViewerProgram.Run(["--headless", "--frames", "1200", "--speed", "8"], output, error, launcher);

        Assert.Equal(0, exit);
        Assert.Equal(string.Empty, error.ToString());
        Assert.Contains("Result: PASS", output.ToString());
        Assert.Contains("ticks: 8400 of 8400 (finished)", output.ToString());
        Assert.Contains("48 of 48 passed, 0 invariant violations", output.ToString());
        Assert.Equal(0, launcher.Checks);
        Assert.Equal(0, launcher.Runs);
    }

    [Fact]
    public void The_headless_Convoy_Ambush_report_is_the_report_of_the_acceptance_test()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Convoy, speed: 8));

        Drive.Frames(session, 1200);

        Assert.True(session.Finished);
        Assert.Equal(8400, session.TicksDone);
        Assert.Equal(AcceptanceReport(), session.ConvoyReport!.Render());
        Assert.True(session.ConvoyReport.Passed);
    }

    [Fact]
    public void Every_model_is_refreshed_by_the_policy_during_the_run_and_ends_equal_to_the_engine()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Convoy, speed: 8));

        Drive.Frames(session, 1200);

        // dashboard and map every frame; tables at frame 1 and then every 15th frame (0.25 s of 1/60 s frames)
        Assert.Equal(1200, session.Refreshes.Dashboard);
        Assert.Equal(1200, session.Refreshes.Map);
        Assert.Equal(1 + (1200 - 1) / 15, session.Refreshes.Tables);

        // the script ended long before the last refresh, so every model shows the final state
        var engine = session.Engine;
        Assert.Equal(engine.TickCount, session.Dashboard.Ticks);
        Assert.Equal(engine.State.Count, session.Dashboard.Entities);
        Assert.Equal(engine.State.Count, session.Map.EntityCount);
        Assert.Equal(engine.State.Count, session.Entities.TotalEntities);
        Assert.Equal(engine.Rules.Count, session.Rules.TotalRules);
        Assert.Equal(engine.Identities.All.Count, session.Identities.Maps.Count);
        Assert.Equal(engine.Diagnostics.TransactionFailures, session.Diagnostics.Counters.TransactionFailures);
        Assert.False(session.Vocabulary.HasErrors);
        Assert.NotEmpty(session.Vocabulary.Dump);
        Assert.Equal(session.Sampler.Count, Math.Min(8400, session.Sampler.Capacity));
        Assert.Equal(engine.TickCount, session.Sampler.Latest.Counters.Tick);
    }

    [Fact]
    public void The_tracer_is_attached_records_when_asked_and_changes_nothing_in_the_report()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Convoy, speed: 8));
        Assert.NotNull(session.Engine.DispatchObserver);
        Assert.Empty(session.Tracer.Traces);
        session.Enqueue(new ViewerAction.WatchAll(true));

        Drive.Frames(session, 1200);

        Assert.True(session.Tracer.Traces.Count > 100, "traces kept: " + session.Tracer.Traces.Count);
        Assert.Equal(AcceptanceReport(), session.ConvoyReport!.Render());
    }

    [Fact]
    public void The_dashboard_counts_the_messages_in_and_out_per_side()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Convoy, speed: 8));

        Drive.Frames(session, 300);

        var sides = session.Dashboard.Sides.ToDictionary(s => s.Side);
        var ctx = session.Host;
        Assert.Equal(ctx.NedSentCount, sides["ned"].OutTotal);
        Assert.Equal(ctx.RprSentCount, sides["rpr"].OutTotal);
        Assert.Equal(ctx.DisplaySentCount, sides["display"].OutTotal);
        Assert.True(sides["ned"].InTotal > 0, "NED inputs were counted");
        Assert.True(sides["rpr"].InTotal > 0, "RPR inputs were counted");
        Assert.Equal(0, sides["display"].InTotal);
    }

    [Fact]
    public void Stepping_a_scenario_run_tick_by_tick_gives_the_report_of_Run()
    {
        var run = new ScenarioRunner().Start(ConvoyAmbushScript.Create());
        long steps = 0;
        while (run.Step())
        {
            steps++;
        }

        Assert.Equal(run.TotalTicks, steps);
        Assert.True(run.Finished);
        Assert.False(run.Step());
        Assert.Equal(AcceptanceReport(), run.Finish().Render());
    }

    [Fact]
    public void Stepping_the_demo_tick_by_tick_gives_the_result_of_Run()
    {
        var run = DemoScenario.Start();
        while (run.Step())
        {
        }

        Assert.Equal(1200, run.Ticks);
        Assert.Equal(DemoScenario.Run().Format(), run.Finish().Format());
    }

    [Theory]
    [InlineData(ViewerScenario.Demo)]
    [InlineData(ViewerScenario.Traffic)]
    [InlineData(ViewerScenario.Empty)]
    public void The_other_scenarios_run_headless_and_the_manual_clock_is_ticks_over_60(ViewerScenario scenario)
    {
        var output = new StringWriter();
        string name = scenario.ToString().ToLowerInvariant();

        int exit = ViewerProgram.Run(["--headless", "--frames", "90", "--scenario", name, "--entities", "40", "--speed", "2"], output,
            new StringWriter(), new RecordingLauncher());

        Assert.Equal(0, exit);
        Assert.Contains("ticks: 180", output.ToString());

        var session = new ViewerSession(Drive.Unlimited(scenario, speed: 2, entities: 40));
        Drive.Frames(session, 90);
        Assert.Equal(180, session.TicksDone);
        Assert.Equal(180, session.Engine.TickCount);
        Assert.Equal(180 / 60.0, session.Clock.Now, 9);
    }

    [Fact]
    public void The_traffic_generator_builds_the_requested_population_and_the_map_sees_it()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Traffic, speed: 4, entities: 75));

        Drive.Frames(session, 60);

        Assert.Equal(75, session.TrafficEntities);
        Assert.Equal(75, session.Engine.State.Count);
        Assert.Equal(75, session.Map.Points.Count);
        Assert.Equal(75, session.Dashboard.Origins.Sum(o => o.Count));
    }

    [Fact]
    public void The_demo_ends_after_20_seconds_and_nothing_more_is_owed_afterwards()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Demo, speed: 8));

        Drive.Frames(session, 200);

        Assert.True(session.Finished);
        Assert.Equal(1200, session.TicksDone);
        Assert.Equal(0, session.Backlog);
        Assert.False(session.Behind);
        session.Enqueue(new ViewerAction.Pause());
        session.Enqueue(new ViewerAction.Step());
        session.Frame(1.0);
        Assert.Equal(1200, session.TicksDone);
        Assert.Equal(1200, session.Engine.TickCount);
        Assert.False(session.ActionLog[^1].Accepted);
    }

    [Fact]
    public void The_live_status_of_Convoy_Ambush_grows_and_ends_with_every_condition_passed()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Convoy, speed: 8));
        var conditions = session.ConvoyConditions!;
        Assert.Equal(48, conditions.Count);
        Assert.All(conditions, c => Assert.False(c.Evaluated));

        Drive.Frames(session, 300);
        int midway = conditions.Count(c => c.Evaluated);
        Assert.InRange(midway, 1, 47);
        Assert.Empty(session.ConvoyViolations!);

        Drive.Frames(session, 900);
        Assert.Equal(48, conditions.Count(c => c.Evaluated && c.Passed));
        Assert.Equal(11, session.ConvoyAct);
    }

    [Fact]
    public void The_map_model_equals_a_brute_force_projection_of_the_engine_after_every_frame()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Convoy, speed: 8));
        bool sawLink = false, sawCentroid = false, sawError = false, sawStatic = false;
        for (int frame = 0; frame < 700; frame++)
        {
            session.Frame(1.0 / 60.0);
            var schema = session.Schema;
            var expected = new List<(EntityId Id, double Lat, double Lon)>();
            foreach (var entity in session.Engine.State.Entities)
            {
                if (entity.TryGet(schema.Position, out var p))
                {
                    var g = SimNetGateway.Common.Wgs84.ToGeodetic(p.AsVec3());
                    expected.Add((entity.Id, g.LatitudeDegrees, g.LongitudeDegrees));
                }
            }

            var points = session.Map.Points;
            Assert.Equal(expected.Count, points.Count);
            Assert.Equal(session.Engine.State.Count, session.Map.EntityCount);
            for (int i = 0; i < points.Count; i++)
            {
                var point = points[i];
                Assert.Equal(expected[i].Id, point.Id);
                Assert.Equal(expected[i].Lat, point.LatitudeDeg, 9);
                Assert.Equal(expected[i].Lon, point.LongitudeDeg, 9);
                var entity = session.Engine.State.Find(point.Id)!;
                if (entity.TryGet(schema.Parent, out var parent) && session.Map.IndexOf(parent.AsEntity()) is var index and >= 0)
                {
                    Assert.Equal(index, point.ParentIndex);
                    Assert.Equal(parent.AsEntity(), points[point.ParentIndex].Id);
                }
                else
                {
                    Assert.Equal(-1, point.ParentIndex);
                }

                if (entity.TryGet(schema.PredictedPosition, out var predicted))
                {
                    var a = entity.Get(schema.Position).AsVec3();
                    var b = predicted.AsVec3();
                    double distance = Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2) + Math.Pow(a.Z - b.Z, 2));
                    Assert.True(point.HasPrediction);
                    Assert.Equal(distance, point.ErrorMetres, 6);
                    sawError |= distance > 0.01;
                }
                else
                {
                    Assert.False(point.HasPrediction);
                }

                sawStatic |= point.IsStatic;
                Assert.Equal(session.Engine.Sources.NameOf(entity.Origin), point.OriginName);
            }

            // links: one per member with a position and a parent; the anchor is the parent's position or the centroid of its members
            var expectedLinks = new List<(EntityId Child, EntityId Parent, bool OnMap, double Lat, double Lon)>();
            foreach (var point in points)
            {
                var entity = session.Engine.State.Find(point.Id)!;
                if (!entity.TryGet(schema.Parent, out var parentValue))
                {
                    continue;
                }

                var parentId = parentValue.AsEntity();
                int parentIndex = session.Map.IndexOf(parentId);
                if (parentIndex >= 0)
                {
                    expectedLinks.Add((point.Id, parentId, true, points[parentIndex].LatitudeDeg, points[parentIndex].LongitudeDeg));
                }
                else
                {
                    var members = points.Where(m => session.Engine.State.Find(m.Id)!.TryGet(schema.Parent, out var q) && q.AsEntity() == parentId).ToList();
                    expectedLinks.Add((point.Id, parentId, false, members.Average(m => m.LatitudeDeg), members.Average(m => m.LongitudeDeg)));
                }
            }

            Assert.Equal(expectedLinks.Count, session.Map.Links.Count);
            for (int i = 0; i < expectedLinks.Count; i++)
            {
                var link = session.Map.Links[i];
                Assert.Equal(expectedLinks[i].Child, points[link.ChildIndex].Id);
                Assert.Equal(expectedLinks[i].Parent, link.Parent);
                Assert.Equal(expectedLinks[i].OnMap, link.ParentOnMap);
                Assert.Equal(expectedLinks[i].Lat, link.AnchorLatitudeDeg, 9);
                Assert.Equal(expectedLinks[i].Lon, link.AnchorLongitudeDeg, 9);
                sawLink = true;
                sawCentroid |= !link.ParentOnMap;
            }
        }

        Assert.True(sawLink, "formation links were exercised");
        Assert.True(sawCentroid, "links to parents without a position of their own were exercised");
        Assert.True(sawError, "predicted positions that differ from the received ones were exercised");
        Assert.True(sawStatic, "static entities (the checkpoint) were exercised");
    }

    [Fact]
    public void Every_live_entry_of_every_entity_table_describes_itself_with_its_field_values()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Convoy, speed: 8));
        var seen = new Dictionary<string, int>();
        for (int frame = 0; frame < 1100; frame++)
        {
            session.Frame(1.0 / 60.0);
            if (frame % 20 != 0)
            {
                continue;
            }

            foreach (var table in session.Engine.EntityTables)
            {
                foreach (var entity in session.Engine.State.Entities)
                {
                    if (!table.TryDescribe(entity.Id, out string text))
                    {
                        continue;
                    }

                    seen[table.Name] = seen.GetValueOrDefault(table.Name) + 1;
                    string context = $"{table.Name} entity {entity.Id.Value}: '{text}'";
                    Assert.False(string.IsNullOrWhiteSpace(text), context);
                    Assert.False(text.Contains("SimNetGateway.", StringComparison.Ordinal), "a type name, not a state: " + context);
                    Assert.True(text.Any(char.IsDigit) || text.Contains("True", StringComparison.Ordinal) || text.Contains("False", StringComparison.Ordinal),
                        "no field value: " + context);
                }
            }
        }

        Assert.True(seen.GetValueOrDefault("RprPublication.State") > 20, "publication states were described");
        Assert.True(seen.GetValueOrDefault("NedFormationEgress.Units") > 0, "formation unit states were described");
    }

    [Fact]
    public void The_dashboard_model_equals_the_engine_every_frame()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Traffic, speed: 2, entities: 120));
        for (int frame = 0; frame < 120; frame++)
        {
            session.Frame(1.0 / 60.0);
            var d = session.Dashboard;
            var engine = session.Engine;
            Assert.Equal(engine.TickCount, d.Ticks);
            Assert.Equal(engine.State.Count, d.Entities);
            Assert.Equal(engine.SimTime, d.SimTime);
            Assert.Equal(engine.State.Count, d.Origins.Sum(o => o.Count));
            Assert.Equal(engine.State.Journal.Count, d.JournalRecords);
            Assert.Equal(engine.Diagnostics.MessagesProcessed, d.Counters.MessagesProcessed);
            Assert.Equal(Math.Min(session.Sampler.Count, DashboardModel.Window), d.TickTimeCount);
            Assert.Equal(session.Sampler.Latest.TickMilliseconds, d.LatestTickMs);
        }
    }
}
