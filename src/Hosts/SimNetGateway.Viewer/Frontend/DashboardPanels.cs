using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SimNetGateway.Engine;

namespace SimNetGateway.Viewer.Frontend;

/// <summary>Play / pause / single step / speed and the "behind" indicator (DD-005 section 4.1). Always visible.</summary>
internal sealed class ControlsPanel : Panel
{
    private static readonly double[] s_speeds = [0.25, 0.5, 1, 2, 4, 8];

    public override string Title => "Controls";

    public override Vector2 DefaultPosition => new(10, 28);

    public override Vector2 DefaultSize => new(1140, 96);

    public override void Draw(ViewerSession session, UiState ui)
    {
        if (ImGui.Button(session.Playing ? "Pause (Space)" : "Play (Space)"))
        {
            session.Enqueue(session.Playing ? new ViewerAction.Pause() : new ViewerAction.Play());
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(session.Playing || session.Finished);
        if (ImGui.Button("Step (N)"))
        {
            session.Enqueue(new ViewerAction.Step());
        }

        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.Text("speed");
        foreach (double speed in s_speeds)
        {
            ImGui.SameLine();
            bool current = Math.Abs(session.Speed - speed) < 1e-9;
            if (current)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.45f, 0.75f, 1f));
            }

            if (ImGui.SmallButton("x" + speed.ToString("0.##", CultureInfo.InvariantCulture)))
            {
                session.Enqueue(new ViewerAction.SetSpeed(speed));
            }

            if (current)
            {
                ImGui.PopStyleColor();
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Restart"))
        {
            session.Enqueue(new ViewerAction.Restart());
        }

        var d = session.Dashboard;
        string state = session.Finished ? "finished" : session.Playing ? "playing" : "paused";
        ImGui.Text(string.Create(CultureInfo.InvariantCulture,
            $"{session.ScenarioName} - {state} - tick {session.TicksDone}{(session.TotalTicks > 0 ? " / " + session.TotalTicks : string.Empty)} - sim {d.SimTime:F2} s - clock {session.ClockSeconds:F2} s"));
        if (session.Behind)
        {
            ImGui.SameLine();
            Ui.Colored(Ui.Bad, string.Create(CultureInfo.InvariantCulture, $"BEHIND by {session.Backlog} ticks (dropped {session.TicksDropped:F0})"));
        }
        else if (session.TicksDropped > 0)
        {
            ImGui.SameLine();
            Ui.Colored(Ui.Warn, string.Create(CultureInfo.InvariantCulture, $"dropped {session.TicksDropped:F0} ticks"));
        }

        if (session.TotalTicks > 0)
        {
            ImGui.ProgressBar((float)session.TicksDone / session.TotalTicks, new Vector2(-1, 6), string.Empty);
        }
    }
}

/// <summary>Session, rates, tick time, entities per origin and the counters of diagnostics, each clickable into the Diagnostics panel.</summary>
internal sealed class DashboardPanel : Panel
{
    public override string Title => "Dashboard";

    public override Vector2 DefaultPosition => new(10, 134);

    public override Vector2 DefaultSize => new(430, 560);

    public override void Draw(ViewerSession session, UiState ui)
    {
        var d = session.Dashboard;
        var c = CultureInfo.InvariantCulture;
        ImGui.SeparatorText("Session");
        ImGui.Text(string.Create(c, $"state {d.SessionState}  mode {d.SessionMode}  scenario {d.SessionScenario ?? "-"}"));
        ImGui.Text(string.Create(c, $"sim time {d.SimTime:F3} s   wall (clock) {d.WallTime:F3} s   tick {d.Ticks}"));
        ImGui.Text(string.Create(c, $"entities {d.Entities}   tick rate {d.TickRate:F1} /s"));

        ImGui.SeparatorText("Tick time");
        ImGui.Text(string.Create(c, $"last {d.LatestTickMs:F3} ms   p50 {d.TickP50:F3}   p95 {d.TickP95:F3}   max {d.TickMax:F3} ms"));
        if (d.TickTimeCount > 0)
        {
            ImGui.PlotLines("##ticktime", ref d.TickTimeBuffer[0], d.TickTimeCount, 0, string.Empty, 0f, float.MaxValue, new Vector2(-1, 60));
        }

        ImGui.SeparatorText("Messages per second (last 2 s)");
        if (ImGui.BeginTable("sides", 5, ImGuiTableFlags.SizingFixedFit | ImGuiTableFlags.RowBg))
        {
            ImGui.TableSetupColumn("side");
            ImGui.TableSetupColumn("in /s");
            ImGui.TableSetupColumn("out /s");
            ImGui.TableSetupColumn("in total");
            ImGui.TableSetupColumn("out total");
            ImGui.TableHeadersRow();
            foreach (var side in d.Sides)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(side.Side);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(side.InPerSecond.ToString("F1", c));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(side.OutPerSecond.ToString("F1", c));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(side.InTotal.ToString(c));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(side.OutTotal.ToString(c));
            }

            ImGui.EndTable();
        }

        ImGui.SeparatorText("Entities per origin");
        foreach (var origin in d.Origins)
        {
            ImGui.TextColored(MapView.OriginColorVector(origin.Origin), origin.Origin);
            ImGui.SameLine(120);
            ImGui.Text(origin.Count.ToString(c));
        }

        ImGui.SeparatorText("History");
        ImGui.Text(d.HistoryOff ? "journal: history off" : string.Create(c, $"journal {d.JournalRecords} / {d.JournalCapacity} records"));
        ImGui.Text(d.EventsOff ? "event log: off" : string.Create(c, $"event log {d.EventsRetained} / {d.EventCapacity} events"));

        ImGui.SeparatorText("Diagnostics (click for details)");
        var k = d.Counters;
        Counter(ui, "warnings", d.Warnings, Ui.Warn);
        Counter(ui, "errors", d.Errors, Ui.Bad);
        Counter(ui, "transaction failures", k.TransactionFailures, Ui.Bad);
        Counter(ui, "rule failures", k.RuleFailures, Ui.Bad);
        Counter(ui, "cascade overflows", k.CascadeOverflows, Ui.Bad);
        Counter(ui, "unhandled message types", k.UnhandledMessages, Ui.Warn);
        Counter(ui, "session rejections", k.SessionRejections, Ui.Warn);
        Counter(ui, "events without subscriber", k.EventsWithoutSubscriber, Ui.Dim);
        Counter(ui, "messages processed", k.MessagesProcessed, Ui.Dim);
    }

    private static void Counter(UiState ui, string label, long value, Vector4 color)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, value > 0 ? color : Ui.Dim);
        if (ImGui.Selectable(string.Create(CultureInfo.InvariantCulture, $"{label,-28} {value}")))
        {
            ui.Open("Diagnostics");
        }

        ImGui.PopStyleColor();
    }
}

/// <summary>Scenario choice, the Convoy Ambush status (S-* and G-*), and the operator's session commands.</summary>
internal sealed class ScenarioPanel : Panel
{
    private static readonly string[] s_names = ["Convoy Ambush", "Default demo", "Traffic generator", "Empty gateway"];
    private int _choice;
    private int _entities = 1000;
    private bool _onlyOpen;
    private string _loadName = "demo";
    private bool _replay;
    private bool _chosen;

    public override string Title => "Scenario";

    public override Vector2 DefaultPosition => new(1160, 28);

    public override Vector2 DefaultSize => new(430, 470);

    public override void Draw(ViewerSession session, UiState ui)
    {
        if (!_chosen)
        {
            _choice = (int)session.Scenario;
            _entities = session.TrafficEntities > 0 ? session.TrafficEntities : _entities;
            _chosen = true;
        }

        ImGui.SeparatorText("Scenario (a new gateway is built)");
        ImGui.Combo("##scenario", ref _choice, s_names, s_names.Length);
        if (_choice == (int)ViewerScenario.Traffic)
        {
            ImGui.SliderInt("entities", ref _entities, 10, 20_000, "%d", ImGuiSliderFlags.Logarithmic);
        }

        if (ImGui.Button("Start this scenario"))
        {
            session.Enqueue(new ViewerAction.SwitchScenario((ViewerScenario)_choice, _entities));
        }

        ImGui.SameLine();
        if (ImGui.Button("Restart current"))
        {
            session.Enqueue(new ViewerAction.Restart());
        }

        ImGui.SeparatorText("Session commands (source: operator)");
        ImGui.SetNextItemWidth(120);
        Ui.Input("scenario name", ref _loadName, 64);
        ImGui.SameLine();
        ImGui.Checkbox("replay", ref _replay);
        if (ImGui.Button("Load"))
        {
            session.Enqueue(new ViewerAction.ExecuteSession(SessionCommand.Load(_loadName, _replay ? SessionMode.Replay : SessionMode.Live)));
        }

        ImGui.SameLine();
        if (ImGui.Button("Start"))
        {
            session.Enqueue(new ViewerAction.ExecuteSession(SessionCommand.Start));
        }

        ImGui.SameLine();
        if (ImGui.Button("Pause session"))
        {
            session.Enqueue(new ViewerAction.ExecuteSession(SessionCommand.Pause));
        }

        ImGui.SameLine();
        if (ImGui.Button("Resume"))
        {
            session.Enqueue(new ViewerAction.ExecuteSession(SessionCommand.Resume));
        }

        ImGui.SameLine();
        if (ImGui.Button("Unload"))
        {
            session.Enqueue(new ViewerAction.ExecuteSession(SessionCommand.Unload));
        }

        ImGui.TextWrapped("Pause above is the viewer's debugger pause (nothing ticks); \"Pause session\" sends the session command to the gateway.");

        if (session.ConvoyConditions is { } conditions)
        {
            DrawConvoy(session, ui, conditions);
        }

        ImGui.SeparatorText("Operator log");
        var log = session.ActionLog;
        if (ImGui.BeginChild("oplog", new Vector2(0, 110), ImGuiChildFlags.Borders))
        {
            for (int i = log.Count - 1; i >= 0; i--)
            {
                var o = log[i];
                Ui.Colored(o.Accepted ? Ui.Dim : Ui.Warn, string.Create(CultureInfo.InvariantCulture, $"tick {o.Tick,6}  {o.Text}"));
            }
        }

        ImGui.EndChild();
    }

    private void DrawConvoy(ViewerSession session, UiState ui, IReadOnlyList<SimNetGateway.Showcase.ConditionResult> conditions)
    {
        var c = CultureInfo.InvariantCulture;
        var violations = session.ConvoyViolations!;
        int passed = 0, failed = 0, pending = 0;
        foreach (var condition in conditions)
        {
            if (!condition.Evaluated)
            {
                pending++;
            }
            else if (condition.Passed)
            {
                passed++;
            }
            else
            {
                failed++;
            }
        }

        ImGui.SeparatorText("Convoy Ambush");
        ImGui.Text(string.Create(c, $"act {session.ConvoyAct}   S-*: "));
        ImGui.SameLine();
        Ui.Colored(Ui.Good, $"{passed} pass");
        ImGui.SameLine();
        Ui.Colored(failed > 0 ? Ui.Bad : Ui.Dim, $"{failed} fail");
        ImGui.SameLine();
        Ui.Colored(Ui.Dim, $"{pending} not yet");
        ImGui.SameLine();
        Ui.Colored(violations.Count > 0 ? Ui.Bad : Ui.Good, $"   G-*: {violations.Count} violations");
        if (session.Finished)
        {
            var report = session.ConvoyReport!;
            Ui.Colored(report.Passed ? Ui.Good : Ui.Bad, report.Passed ? "Result: PASS" : "Result: FAIL");
        }

        ImGui.Checkbox("only failing and pending", ref _onlyOpen);
        if (ImGui.BeginChild("conditions", new Vector2(0, 200), ImGuiChildFlags.Borders))
        {
            foreach (var condition in conditions)
            {
                bool ok = condition.Evaluated && condition.Passed;
                if (_onlyOpen && ok)
                {
                    continue;
                }

                Vector4 color = !condition.Evaluated ? Ui.Dim : ok ? Ui.Good : Ui.Bad;
                string verdict = !condition.Evaluated ? "..." : ok ? "PASS" : "FAIL";
                ImGui.TextColored(color, $"{condition.Id,-6} {verdict,-5}");
                ImGui.SameLine();
                ImGui.TextUnformatted(condition.Text);
                if (condition.Evaluated && !condition.Passed && ImGui.IsItemHovered())
                {
                    ImGui.BeginTooltip();
                    foreach (var evaluation in condition.Evaluations)
                    {
                        if (!evaluation.Passed)
                        {
                            ImGui.TextUnformatted(string.Create(c, $"r={evaluation.Time:F3}: {evaluation.Details}"));
                        }
                    }

                    ImGui.EndTooltip();
                }
            }
        }

        ImGui.EndChild();

        if (violations.Count > 0)
        {
            ImGui.SeparatorText("Invariant violations (click to find that tick's trace)");
            for (int i = 0; i < violations.Count && i < 50; i++)
            {
                var v = violations[i];
                if (ImGui.Selectable(string.Create(c, $"{v.Id} tick {v.Tick}: {v.Detail}##v{i}")))
                {
                    TracePanel.JumpToTick(session, ui, v.Tick);
                }
            }
        }
    }
}
