using SimNetGateway.Engine;
using SimNetGateway.Engine.Monitoring;

namespace SimNetGateway.Viewer.Tests;

/// <summary>
/// The refresh policy (DD-005 P-52, R-075): dashboard and map every frame; entity table, inspector and the other tables at most
/// 4 times per second of frame time - and not later than the frame in which a quarter second has passed.
/// </summary>
public class RefreshPolicyTests
{
    private const double Period = 0.25;
    private const double Eps = 1e-9;

    [Fact]
    public void Dashboard_and_map_refresh_every_frame_and_the_tables_every_15th_frame_at_60_fps()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Empty));

        for (int frame = 1; frame <= 300; frame++)
        {
            session.Frame(1.0 / 60);
            Assert.Equal(frame, session.Refreshes.Dashboard);
            Assert.Equal(frame, session.Refreshes.Map);
            Assert.Equal(1 + (frame - 1) / 15, session.Refreshes.Tables);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void With_random_frame_times_the_tables_never_refresh_more_than_4_times_a_second_nor_late(int seed)
    {
        var random = new Random(seed);
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Empty, playing: random.Next(2) == 0));
        double[] dts = [1.0 / 240, 1.0 / 120, 1.0 / 60, 1.0 / 30, 0.1, 0.2, 0.25, 0.5, 0.0];
        double lastRefreshAt = 0;
        int lastTables = 0;
        bool first = true;
        int refreshes = 0;
        for (int frame = 1; frame <= 600; frame++)
        {
            session.Frame(dts[random.Next(dts.Length)]);

            // dashboard and map: exactly once per frame
            Assert.Equal(frame, session.Refreshes.Dashboard);
            Assert.Equal(frame, session.Refreshes.Map);

            int tables = session.Refreshes.Tables;
            Assert.InRange(tables - lastTables, 0, 1);
            if (tables != lastTables)
            {
                refreshes++;
                if (!first)
                {
                    Assert.True(session.FrameSeconds - lastRefreshAt >= Period - Eps,
                        $"seed {seed} frame {frame}: refreshed after {session.FrameSeconds - lastRefreshAt} s");
                }

                first = false;
                lastRefreshAt = session.FrameSeconds;
                lastTables = tables;
            }

            // not late: when a quarter second of frame time has passed since the last refresh, this frame refreshed
            Assert.False(!first && session.FrameSeconds - lastRefreshAt >= Period - Eps, $"seed {seed} frame {frame}: refresh is overdue");
        }

        Assert.True(refreshes >= 1);
        Assert.True(refreshes <= 1 + session.FrameSeconds / Period + Eps, $"{refreshes} refreshes in {session.FrameSeconds} s");
    }

    [Fact]
    public void Between_refreshes_the_tables_keep_their_copy_while_the_dashboard_and_the_map_follow_the_engine()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Traffic, speed: 1, entities: 50));
        int sawStaleTable = 0;
        int lastTables = 0;
        long firesAtRefresh = -1;
        for (int frame = 1; frame <= 80; frame++)
        {
            session.Frame(1.0 / 60);
            int engineCount = session.Engine.State.Count;

            // every frame
            Assert.Equal(engineCount, session.Dashboard.Entities);
            Assert.Equal(engineCount, session.Map.EntityCount);
            Assert.Equal(session.Engine.TickCount, session.Dashboard.Ticks);

            long engineFires = session.Engine.Rules.Sum(r => r.FireCount);
            if (session.Refreshes.Tables != lastTables)
            {
                lastTables = session.Refreshes.Tables;
                firesAtRefresh = engineFires;
            }

            // the tables show what the engine held at the last refresh
            Assert.Equal(engineCount, session.Entities.TotalEntities);
            Assert.Equal(firesAtRefresh, session.Rules.Rows.Sum(r => r.FireCount));
            if (firesAtRefresh != engineFires)
            {
                sawStaleTable++;
            }
        }

        Assert.True(sawStaleTable > 0, "the rules fired between table refreshes, so the table was seen to lag");
    }

    [Fact]
    public void The_inspector_is_refreshed_with_the_tables_not_every_frame()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Traffic, speed: 1, entities: 30));
        Drive.Frames(session, 20);
        var moving = Drive.MovingEntity(session);
        session.Select(moving);
        Assert.Equal(InspectorState.Alive, session.Inspector.State);

        double atRefresh = session.Inspector.LastWriteWallTime;
        int tables = session.Refreshes.Tables;
        int framesAtStale = 0;
        for (int frame = 0; frame < 120; frame++)
        {
            session.Frame(1.0 / 60);
            var entity = session.Engine.State.Find(moving)!;
            if (session.Refreshes.Tables != tables)
            {
                tables = session.Refreshes.Tables;
                atRefresh = entity.LastWriteWallTime;
            }

            Assert.Equal(atRefresh, session.Inspector.LastWriteWallTime);
            if (entity.LastWriteWallTime != atRefresh)
            {
                framesAtStale++;
            }
        }

        Assert.True(framesAtStale > 0, "the entity was written between refreshes, so the inspector was seen to lag");
    }

    [Fact]
    public void After_a_restart_the_first_frame_refreshes_everything_at_once()
    {
        var session = new ViewerSession(Drive.Unlimited(ViewerScenario.Empty));
        Drive.Frames(session, 5);
        Assert.Equal(1, session.Refreshes.Tables);

        session.Enqueue(new ViewerAction.Restart());
        session.Frame(1.0 / 60);

        Assert.Equal(1, session.Refreshes.Dashboard);
        Assert.Equal(1, session.Refreshes.Map);
        Assert.Equal(1, session.Refreshes.Tables);
    }
}
