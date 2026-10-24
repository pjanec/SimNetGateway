using SimNetGateway.Engine;

namespace SimNetGateway.Engine.Tests;

public class SessionTests
{
    private static readonly SessionCommandKind[] AllKinds =
        { SessionCommandKind.Load, SessionCommandKind.Start, SessionCommandKind.Pause, SessionCommandKind.Resume, SessionCommandKind.Unload };

    private static SessionCommand Make(SessionCommandKind kind) =>
        kind == SessionCommandKind.Load ? SessionCommand.Load("s") : new SessionCommand(kind);

    [Fact]
    public void All_valid_transitions_are_accepted_and_raise_a_session_trigger()
    {
        var f = new EngineFixture();
        var log = new List<string>();
        f.Module("m", s => s.OnSessionChanged().Do(t => log.Add($"{t.Command.Kind}:{t.Previous}->{t.Current}:{t.Source.Value}")));

        Assert.True(f.Engine.Execute(SessionCommand.Load("scn"), f.Net));
        Assert.Equal(SessionState.Loaded, f.Engine.Session.State);
        Assert.True(f.Engine.Execute(SessionCommand.Start, f.Net));
        Assert.Equal(SessionState.Running, f.Engine.Session.State);
        Assert.True(f.Engine.Execute(SessionCommand.Pause, f.Net));
        Assert.Equal(SessionState.Paused, f.Engine.Session.State);
        Assert.True(f.Engine.Execute(SessionCommand.Resume, f.Net));
        Assert.Equal(SessionState.Running, f.Engine.Session.State);
        Assert.True(f.Engine.Execute(SessionCommand.Unload, f.Net));
        Assert.Equal(SessionState.Idle, f.Engine.Session.State);
        Assert.True(f.Engine.Execute(SessionCommand.Load("scn"), f.Net));
        Assert.True(f.Engine.Execute(SessionCommand.Unload, f.Net)); // Loaded -> Idle
        Assert.True(f.Engine.Execute(SessionCommand.Load("scn"), f.Net));
        Assert.True(f.Engine.Execute(SessionCommand.Start, f.Net));
        Assert.True(f.Engine.Execute(SessionCommand.Pause, f.Net));
        Assert.True(f.Engine.Execute(SessionCommand.Unload, f.Net)); // Paused -> Idle

        var n = f.Net.Value;
        Assert.Equal($"Load:Idle->Loaded:{n}", log[0]);
        Assert.Equal($"Start:Loaded->Running:{n}", log[1]);
        Assert.Equal($"Pause:Running->Paused:{n}", log[2]);
        Assert.Equal($"Resume:Paused->Running:{n}", log[3]);
        Assert.Equal($"Unload:Running->Idle:{n}", log[4]);
        Assert.Equal(11, log.Count);
        Assert.Equal(0, f.Engine.Diagnostics.SessionRejections);
    }

    [Fact]
    public void Invalid_transitions_are_rejected_without_a_trigger()
    {
        var states = new Dictionary<SessionState, SessionCommandKind[]>
        {
            [SessionState.Idle] = new[] { SessionCommandKind.Load },
            [SessionState.Loaded] = new[] { SessionCommandKind.Start, SessionCommandKind.Unload },
            [SessionState.Running] = new[] { SessionCommandKind.Pause, SessionCommandKind.Unload },
            [SessionState.Paused] = new[] { SessionCommandKind.Resume, SessionCommandKind.Unload },
        };
        var prepare = new Dictionary<SessionState, SessionCommand[]>
        {
            [SessionState.Idle] = Array.Empty<SessionCommand>(),
            [SessionState.Loaded] = new[] { SessionCommand.Load("s") },
            [SessionState.Running] = new[] { SessionCommand.Load("s"), SessionCommand.Start },
            [SessionState.Paused] = new[] { SessionCommand.Load("s"), SessionCommand.Start, SessionCommand.Pause },
        };

        foreach (var (state, valid) in states)
        {
            foreach (var kind in AllKinds.Where(k => !valid.Contains(k)))
            {
                var f = new EngineFixture();
                int triggers = 0;
                f.Module("m", s => s.OnSessionChanged().Do(_ => triggers++));
                foreach (var c in prepare[state])
                {
                    Assert.True(f.Engine.Execute(c, f.Net));
                }

                int before = triggers;
                Assert.False(f.Engine.Execute(Make(kind), f.Net));

                Assert.Equal(before, triggers);
                Assert.Equal(state, f.Engine.Session.State);
                Assert.Equal(1, f.Engine.Diagnostics.SessionRejections);
                var entry = EngineFixture.Entries(f.Engine.Diagnostics).Last();
                Assert.Equal(DiagnosticSeverity.Warning, entry.Severity);
                Assert.Contains(kind.ToString(), entry.Message);
            }
        }
    }

    [Fact]
    public void Sim_time_advances_only_while_idle_or_running_and_is_reset_by_load_and_unload()
    {
        var f = new EngineFixture();
        f.Clock.Now = 1;
        f.Engine.Tick();
        Assert.Equal(1, f.Engine.SimTime); // Idle: runs with wall time

        f.Clock.Now = 2;
        Assert.True(f.Engine.Execute(SessionCommand.Load("s"), f.Net));
        Assert.Equal(0, f.Engine.SimTime);

        f.Clock.Now = 4;
        f.Engine.Tick();
        Assert.Equal(0, f.Engine.SimTime); // Loaded: frozen

        Assert.True(f.Engine.Execute(SessionCommand.Start, f.Net));
        f.Clock.Now = 6;
        f.Engine.Tick();
        Assert.Equal(2, f.Engine.SimTime); // Running

        Assert.True(f.Engine.Execute(SessionCommand.Pause, f.Net));
        f.Clock.Now = 9;
        f.Engine.Tick();
        Assert.Equal(2, f.Engine.SimTime); // Paused: frozen

        Assert.True(f.Engine.Execute(SessionCommand.Resume, f.Net));
        f.Clock.Now = 10;
        f.Engine.Tick();
        Assert.Equal(3, f.Engine.SimTime);
        Assert.Equal(3, f.Engine.Session.SimTime);

        Assert.True(f.Engine.Execute(SessionCommand.Unload, f.Net));
        Assert.Equal(0, f.Engine.SimTime);
        f.Clock.Now = 12;
        f.Engine.Tick();
        Assert.Equal(2, f.Engine.SimTime); // Idle again
    }

    [Fact]
    public void Unload_destroys_all_entities_with_removed_triggers_after_the_session_trigger()
    {
        var f = new EngineFixture();
        var log = new List<string>();
        f.Module("m", s =>
        {
            s.OnSessionChanged().Do(t => log.Add("session:" + t.Command.Kind + ":" + f.Engine.State.Count));
            s.OnEntityRemoved().Do(t => log.Add("removed:" + t.Id.Value + ":" + t.Source.Value));
        });
        var a = f.Create();
        var b = f.Create();
        f.Write(a, f.Pos, EngineFixture.V(1));
        Assert.True(f.Engine.Execute(SessionCommand.Load("s"), f.Net));
        Assert.Equal(2, f.Engine.State.Count); // load keeps the entities
        log.Clear();

        Assert.True(f.Engine.Execute(SessionCommand.Unload, f.Net));

        Assert.Equal(0, f.Engine.State.Count);
        Assert.Equal(new[] { "session:Unload:2", $"removed:{a.Value}:1", $"removed:{b.Value}:1" }, log);
        Assert.Null(f.Engine.Session.Scenario);
        Assert.Equal(SessionMode.Live, f.Engine.Session.Mode);
    }

    [Fact]
    public void Unload_with_no_entities_is_fine()
    {
        var f = new EngineFixture();
        Assert.True(f.Engine.Execute(SessionCommand.Load("s"), f.Net));
        Assert.True(f.Engine.Execute(SessionCommand.Unload, f.Net));
        Assert.Equal(SessionState.Idle, f.Engine.Session.State);
    }

    [Fact]
    public void Replay_mode_and_scenario_are_exposed()
    {
        var f = new EngineFixture();

        Assert.True(f.Engine.Execute(SessionCommand.Load("debrief-3", SessionMode.Replay), f.Net));

        Assert.Equal(SessionMode.Replay, f.Engine.Session.Mode);
        Assert.Equal("debrief-3", f.Engine.Session.Scenario);
        Assert.Equal(SessionState.Loaded, f.Engine.Session.State);
    }

    [Fact]
    public void A_veto_rejects_the_command_without_a_session_trigger_and_keeps_the_first_reason()
    {
        var f = new EngineFixture();
        int triggers = 0;
        string? seenScenario = null;
        bool veto = true;
        f.Module("wan", s =>
        {
            s.OnSessionRequest().Do(t =>
            {
                seenScenario = t.Command.Scenario;
                if (veto)
                {
                    t.Veto("network hash differs");
                    t.Veto("second reason");
                }
            });
            s.OnSessionRequest().Do(t => Assert.True(t.IsVetoed || !veto));
            s.OnSessionChanged().Do(_ => triggers++);
        });

        Assert.False(f.Engine.Execute(SessionCommand.Load("scn"), f.Net));

        Assert.Equal("scn", seenScenario);
        Assert.Equal(0, triggers);
        Assert.Equal(SessionState.Idle, f.Engine.Session.State);
        Assert.Null(f.Engine.Session.Scenario);
        Assert.Equal(1, f.Engine.Diagnostics.SessionRejections);
        var entry = EngineFixture.Entries(f.Engine.Diagnostics).Last();
        Assert.Contains("network hash differs", entry.Message);
        Assert.DoesNotContain("second reason", entry.Message);

        veto = false;
        Assert.True(f.Engine.Execute(SessionCommand.Load("scn"), f.Net));
        Assert.Equal(1, triggers);
    }

    [Fact]
    public void An_invalid_command_does_not_run_the_veto_rules()
    {
        var f = new EngineFixture();
        int requests = 0;
        f.Module("m", s => s.OnSessionRequest().Do(_ => requests++));

        Assert.False(f.Engine.Execute(SessionCommand.Start, f.Net));

        Assert.Equal(0, requests);
    }

    [Fact]
    public void Ingress_commands_run_after_the_message_state_changes()
    {
        var f = new EngineFixture();
        var log = new List<string>();
        f.Module("m", s =>
        {
            s.OnMessage<MoveMsg>((in MoveMsg m, IngressContext ctx) =>
            {
                ctx.Changes.Set(m.Entity, f.Pos, AttrValue.From(m.Pos));
                ctx.Publish(f.Fired, ctx.RentPayload());
                ctx.Command(SessionCommand.Load("from-wire"));
                ctx.Command(SessionCommand.Start);
                ctx.Command(SessionCommand.Start); // invalid the second time
            });
            s.OnAttributesChanged(f.Pos).Do(_ => log.Add("entity"));
            s.OnEvent(f.Fired).Do(_ => log.Add("event"));
            s.OnSessionChanged().Do(t => log.Add("session:" + t.Command.Kind));
        });
        var id = f.Create();

        f.Engine.Ingest(f.Net, new MoveMsg(id, new Vec3(1, 0, 0)));

        Assert.Equal(new[] { "entity", "event", "session:Load", "session:Start" }, log);
        Assert.Equal(SessionState.Running, f.Engine.Session.State);
        Assert.Equal("from-wire", f.Engine.Session.Scenario);
        Assert.Equal(1, f.Engine.Diagnostics.SessionRejections);
    }

    [Fact]
    public void A_session_rule_can_execute_a_command_which_is_applied_at_once()
    {
        var f = new EngineFixture();
        var log = new List<string>();
        f.Module("m", s =>
        {
            s.OnSessionChanged().When(t => t.Command.Kind == SessionCommandKind.Load).Do(t =>
            {
                Assert.True(f.Engine.Execute(SessionCommand.Start, f.Proc));
                log.Add("state-after-execute:" + f.Engine.Session.State);
            });
            s.OnSessionChanged().Do(t => log.Add("trigger:" + t.Command.Kind));
        });

        f.Engine.Execute(SessionCommand.Load("s"), f.Net);

        Assert.Equal(new[] { "state-after-execute:Running", "trigger:Load", "trigger:Start" }, log);
    }

    [Fact]
    public void SessionCommand_helpers_and_equality()
    {
        Assert.Equal(new SessionCommand(SessionCommandKind.Start), SessionCommand.Start);
        Assert.Equal(SessionCommandKind.Pause, SessionCommand.Pause.Kind);
        Assert.Equal(SessionCommandKind.Resume, SessionCommand.Resume.Kind);
        Assert.Equal(SessionCommandKind.Unload, SessionCommand.Unload.Kind);
        var load = SessionCommand.Load("x", SessionMode.Replay);
        Assert.Equal((SessionCommandKind.Load, "x", SessionMode.Replay), (load.Kind, load.Scenario, load.Mode));
    }

    [Fact]
    public void The_controller_applies_transitions_on_its_own()
    {
        var c = new SessionController();
        Assert.False(c.TryApply(SessionCommand.Start));
        Assert.True(c.TryApply(SessionCommand.Load("a")));
        Assert.Equal(SessionState.Loaded, c.State);
        Assert.False(c.TryApply(SessionCommand.Load("b")));
        Assert.Equal("a", c.Scenario);
    }
}
