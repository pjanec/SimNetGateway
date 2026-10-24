using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SimNetGateway.Engine;
using SimNetGateway.Engine.Monitoring;

namespace SimNetGateway.Viewer.Frontend;

/// <summary>Every rule with its counters and rates, filters, and the operator's enable/disable toggle (DD-005 section 3, Rules).</summary>
internal sealed class RulesPanel : Panel
{
    private bool _failed;
    private bool _rejecting;
    private bool _never;
    private string _text = string.Empty;
    private bool _dirty = true;

    public override string Title => "Rules";

    public override Vector2 DefaultPosition => new(450, 134);

    public override Vector2 DefaultSize => new(1140, 810);

    public override void Draw(ViewerSession session, UiState ui)
    {
        var model = session.Rules;
        var c = CultureInfo.InvariantCulture;
        _dirty |= ImGui.Checkbox("failed", ref _failed);
        ImGui.SameLine();
        _dirty |= ImGui.Checkbox("rejecting", ref _rejecting);
        ImGui.SameLine();
        _dirty |= ImGui.Checkbox("never fired", ref _never);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(200);
        _dirty |= Ui.Input("text", ref _text, 64);
        if (_dirty)
        {
            _dirty = false;
            model.Filter = new RuleFilter { Failed = _failed, Rejecting = _rejecting, NeverFired = _never, Text = string.IsNullOrWhiteSpace(_text) ? null : _text.Trim() };
        }

        ImGui.Text(string.Create(c, $"{model.Rows.Count} of {model.TotalRules} rules. Disabling a rule is an operator action applied between ticks (R-046)."));
        if (!Ui.BeginSizedTable("rules", [("on", 30), ("rule", 330), ("trigger", 0), ("fired", 60), ("rejects", 60), ("failures", 60), ("fired /s", 60), ("rej. /s", 60), ("fail /s", 60)]))
        {
            return;
        }

        var rows = model.Rows;
        Ui.ClippedRows(rows.Count, i =>
        {
            var r = rows[i];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            bool enabled = r.Enabled;
            if (ImGui.Checkbox("##on" + r.RegistrationIndex.ToString(c), ref enabled))
            {
                session.Enqueue(new ViewerAction.SetRuleEnabled(r.FullName, enabled));
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip("Disabling RprEntityEgress/Spatial before an entity's first position prevents its registration (DD-002 section 10).");
            }

            ImGui.TableNextColumn();
            if (r.Failures > 0)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, Ui.Bad);
            }

            if (ImGui.Selectable(r.FullName + "##r" + r.RegistrationIndex.ToString(c), false, ImGuiSelectableFlags.None) && r.Failures > 0)
            {
                session.Diagnostics.Text = r.Name;
                ui.Open("Diagnostics");
            }

            if (r.Failures > 0)
            {
                ImGui.PopStyleColor();
            }

            ImGui.TableNextColumn();
            Ui.Cell(r.Trigger);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.FireCount.ToString(c));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.ConditionRejects.ToString(c));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.Failures.ToString(c));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.FireRate.ToString("F1", c));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.RejectRate.ToString("F1", c));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.FailureRate.ToString("F1", c));
        });

        ImGui.EndTable();
    }
}

/// <summary>The recorded course of correlations: input, commit, triggers, rule outcomes, outbound messages and cascades (DD-005 section 4.3).</summary>
internal sealed class TracePanel : Panel
{
    private bool _autoSelected;

    public override string Title => "Trace";

    public override Vector2 DefaultPosition => new(450, 134);

    public override Vector2 DefaultSize => new(1140, 810);

    /// <summary>Finds the trace nearest to a tick and shows it; says in the operator log why when there is none.</summary>
    public static void JumpToTick(ViewerSession session, UiState ui, long tick)
    {
        double wall = tick / ViewerSession.TickRate;
        DispatchTrace? best = null;
        double bestDistance = double.MaxValue;
        foreach (var trace in session.Tracer.Traces)
        {
            double d = Math.Abs(trace.WallTime - wall);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = trace;
            }
        }

        ui.Open("Trace");
        if (best is not null && bestDistance < 0.5)
        {
            ui.SelectedTrace = best.Id;
        }
        else
        {
            ui.SelectedTrace = -1;
            session.Enqueue(new ViewerAction.WatchAll(true));
        }
    }

    public override void Draw(ViewerSession session, UiState ui)
    {
        var tracer = session.Tracer;
        var c = CultureInfo.InvariantCulture;
        bool all = tracer.WatchAll;
        if (ImGui.Checkbox("trace everything (costly with big populations)", ref all))
        {
            session.Enqueue(new ViewerAction.WatchAll(all));
        }

        ImGui.SameLine();
        if (ImGui.Button("Clear"))
        {
            session.Enqueue(new ViewerAction.ClearTraces());
        }

        if (!ui.Selected.IsNone)
        {
            ImGui.SameLine();
            if (ImGui.Button(string.Create(c, $"Trace entity {ui.Selected.Value}")))
            {
                session.Enqueue(new ViewerAction.WatchEntity(ui.Selected));
            }
        }

        ImGui.Text(string.Create(c, $"{tracer.Traces.Count} of {tracer.Capacity} traces kept; watching {tracer.WatchedEntities.Count} entities, {tracer.WatchedCorrelations.Count} correlations; nothing is recorded until an entity or correlation is watched or everything is traced"));
        if (tracer.WatchedEntities.Count > 0)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("(" + string.Join(", ", tracer.WatchedEntities.Take(8).Select(e => e.Value)) + ")");
        }

        var traces = tracer.Traces;
        float height = Math.Max(120, ImGui.GetContentRegionAvail().Y * 0.4f);
        if (Ui.BeginSizedTable("traces", [("#", 45), ("correlation", 85), ("caused by", 80), ("kind", 85), ("status", 90), ("source", 60), ("input", 0), ("sim time", 70)],
            new Vector2(0, height)))
        {
            Ui.ClippedRows(traces.Count, row =>
            {
                var t = traces[traces.Count - 1 - row];
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                if (ImGui.Selectable(t.Id.ToString(c) + "##t" + row.ToString(c), t.Id == ui.SelectedTrace, ImGuiSelectableFlags.SpanAllColumns))
                {
                    ui.SelectedTrace = t.Id;
                }

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(t.Correlation.IsNone ? "-" : t.Correlation.Value.ToString(c));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(t.CausedBy.IsNone ? "-" : t.CausedBy.Value.ToString(c));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(t.Kind.ToString());
                ImGui.TableNextColumn();
                ImGui.TextColored(t.Status == TraceStatus.Completed ? Ui.Good : Ui.Bad, t.Status.ToString());
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(t.SourceName);
                ImGui.TableNextColumn();
                Ui.Cell(t.InputType);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(t.SimTime.ToString("F3", c));
            });
            ImGui.EndTable();
        }

        // the first time traces exist, show the newest one that evaluated rules (the operator can pick another)
        if (!_autoSelected && ui.SelectedTrace < 0 && traces.Count > 0)
        {
            _autoSelected = true;
            for (int i = traces.Count - 1; i >= 0; i--)
            {
                if (traces[i].Steps.Any(st => st.Kind == TraceStepKind.Rule))
                {
                    ui.SelectedTrace = traces[i].Id;
                    break;
                }
            }
        }

        DispatchTrace? selected = null;
        foreach (var t in traces)
        {
            if (t.Id == ui.SelectedTrace)
            {
                selected = t;
                break;
            }
        }

        if (selected is null)
        {
            ImGui.TextWrapped("Select a trace. Inspector -> \"Trace this entity\" records every input that touches the entity; the Journal panel can trace a correlation.");
            return;
        }

        DrawDetail(session, ui, selected);
    }

    private static void DrawDetail(ViewerSession session, UiState ui, DispatchTrace t)
    {
        var c = CultureInfo.InvariantCulture;
        var engine = session.Engine;
        ImGui.SeparatorText(string.Create(c, $"trace {t.Id}: {t.Kind} {t.InputType} from {t.SourceName}"));
        ImGui.Text(string.Create(c, $"correlation {(t.Correlation.IsNone ? "-" : t.Correlation.Value.ToString(c))}, caused by {(t.CausedBy.IsNone ? "-" : t.CausedBy.Value.ToString(c))}, depth {t.Depth}, sim {t.SimTime:F3} s, wall {t.WallTime:F3} s"));
        if (t.Status != TraceStatus.Completed)
        {
            Ui.Colored(Ui.Bad, $"{t.Status}: {t.Detail}");
        }

        ImGui.Text(t.Committed ? string.Create(c, $"committed a change set touching {t.EntityCount} entities") : "committed nothing");
        if (!t.Correlation.IsNone)
        {
            if (ImGui.SmallButton("trace this correlation"))
            {
                session.Enqueue(new ViewerAction.WatchCorrelation(t.Correlation));
            }

            if (!t.CausedBy.IsNone && session.Tracer.TraceOf(t.CausedBy) is { } parent)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton(string.Create(c, $"go to cause {t.CausedBy.Value}")))
                {
                    ui.SelectedTrace = parent.Id;
                }
            }

            foreach (var cascade in session.Tracer.Cascades(t.Correlation))
            {
                if (ImGui.Selectable(string.Create(c, $"cascade: trace {cascade.Id} ({cascade.Kind} {cascade.InputType}, correlation {cascade.Correlation.Value})##casc{cascade.Id}")))
                {
                    ui.SelectedTrace = cascade.Id;
                }
            }
        }

        if (ImGui.BeginChild("steps", new Vector2(0, 0), ImGuiChildFlags.Borders))
        {
            foreach (var step in t.Steps)
            {
                switch (step.Kind)
                {
                    case TraceStepKind.Trigger:
                        {
                            string target = step.Entity.IsNone ? (step.EventType.IsNone ? string.Empty : "event " + engine.EventTypes.NameOf(step.EventType)) : "entity " + step.Entity.Value;
                            ImGui.Text(string.Create(c, $"trigger {step.Trigger} {target} (depth {step.Depth})"));
                            if (!step.Entity.IsNone)
                            {
                                ImGui.SameLine();
                                if (ImGui.SmallButton("select##" + t.Id + "_" + step.Entity.Value))
                                {
                                    ui.Select(session, step.Entity);
                                }
                            }

                            break;
                        }

                    case TraceStepKind.Rule:
                        ImGui.TextColored(OutcomeColor(step.Outcome), string.Create(c, $"    {step.Outcome,-18} {step.Rule}"));
                        break;
                    case TraceStepKind.Outbound:
                        ImGui.TextColored(new Vector4(0.5f, 0.8f, 1f, 1f), "  out: " + step.Text);
                        break;
                    case TraceStepKind.SessionApplied:
                        ImGui.TextColored(Ui.Good, "  session applied: " + step.Text);
                        break;
                    case TraceStepKind.SessionRejected:
                        ImGui.TextColored(Ui.Bad, "  session refused: " + step.Text);
                        break;
                }
            }
        }

        ImGui.EndChild();
    }

    private static Vector4 OutcomeColor(RuleOutcome outcome) => outcome switch
    {
        RuleOutcome.Fired => Ui.Good,
        RuleOutcome.ConditionRejected => Ui.Warn,
        RuleOutcome.Failed => Ui.Bad,
        _ => Ui.Dim,
    };
}

/// <summary>Recent change sets with their deltas and the event log (DD-005 section 3, Journal / Events).</summary>
internal sealed class JournalPanel : Panel
{
    private long _selectedCorrelation;

    public override string Title => "Journal / Events";

    public override Vector2 DefaultPosition => new(450, 134);

    public override Vector2 DefaultSize => new(1140, 810);

    public override void Draw(ViewerSession session, UiState ui)
    {
        var model = session.Journal;
        var c = CultureInfo.InvariantCulture;
        if (!ImGui.BeginTabBar("journal"))
        {
            return;
        }

        if (ImGui.BeginTabItem("Change sets"))
        {
            if (model.HistoryOff)
            {
                ImGui.TextDisabled("history off (journal capacity 0)");
            }
            else
            {
                ImGui.Text(string.Create(c, $"{model.Records.Count} of {model.RetainedRecords} retained records (newest first)"));
                var records = model.Records;
                float height = Math.Max(120, ImGui.GetContentRegionAvail().Y * 0.5f);
                if (ImGui.BeginTable("records", 5, ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingFixedFit
                    | ImGuiTableFlags.Resizable, new Vector2(0, height)))
                {
                    ImGui.TableSetupScrollFreeze(0, 1);
                    foreach (string h in new[] { "correlation", "caused by", "source", "sim time", "entities" })
                    {
                        ImGui.TableSetupColumn(h);
                    }

                    ImGui.TableHeadersRow();
                    Ui.ClippedRows(records.Count, row =>
                    {
                        var r = records[records.Count - 1 - row];
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        if (ImGui.Selectable(r.Correlation.Value.ToString(c) + "##rec" + row.ToString(c), r.Correlation.Value == _selectedCorrelation,
                            ImGuiSelectableFlags.SpanAllColumns))
                        {
                            _selectedCorrelation = r.Correlation.Value;
                        }

                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(r.CausedBy.IsNone ? "-" : r.CausedBy.Value.ToString(c));
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(r.SourceName);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(r.SimTime.ToString("F3", c));
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(r.EntityCount.ToString(c));
                    });
                    ImGui.EndTable();
                }

                DrawRecordDetail(session, ui, model);
            }

            ImGui.EndTabItem();
        }

        if (ImGui.BeginTabItem("Events"))
        {
            if (model.EventsOff)
            {
                ImGui.TextDisabled("event log off (capacity 0)");
            }
            else
            {
                ImGui.Text(string.Create(c, $"{model.Events.Count} of {model.RetainedEvents} retained events (newest first)"));
                if (Ui.BeginSizedTable("events", [("type", 160), ("source", 70), ("sim time", 70), ("correlation", 100), ("payload", 0)]))
                {
                    var events = model.Events;
                    Ui.ClippedRows(events.Count, row =>
                    {
                        var e = events[events.Count - 1 - row];
                        ImGui.TableNextRow();
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(e.TypeName);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(e.SourceName);
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(e.SimTime.ToString("F3", c));
                        ImGui.TableNextColumn();
                        ImGui.TextUnformatted(e.Correlation.Value.ToString(c) + (e.CausedBy.IsNone ? string.Empty : " <- " + e.CausedBy.Value.ToString(c)));
                        ImGui.TableNextColumn();
                        Ui.Cell(string.Join(", ", e.Payload.Select(p => p.AttrName + "=" + p.Value)));
                    });
                    ImGui.EndTable();
                }
            }

            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
    }

    private void DrawRecordDetail(ViewerSession session, UiState ui, JournalModel model)
    {
        var c = CultureInfo.InvariantCulture;
        if (_selectedCorrelation == 0 || !model.TryFindRecord(new CorrelationId(_selectedCorrelation), out var record))
        {
            return;
        }

        var chain = model.CausedByChain(record.Correlation);
        ImGui.SeparatorText(string.Create(c, $"change set {record.Correlation.Value}"));
        if (chain.Count > 0)
        {
            ImGui.Text("caused by: " + string.Join(" <- ", chain.Select(x => x.Value)));
            for (int i = 0; i < chain.Count && i < 8; i++)
            {
                ImGui.SameLine();
                if (ImGui.SmallButton("go " + chain[i].Value.ToString(c)))
                {
                    _selectedCorrelation = chain[i].Value;
                }
            }
        }

        if (ImGui.SmallButton("trace this correlation"))
        {
            session.Enqueue(new ViewerAction.WatchCorrelation(record.Correlation));
        }

        if (ImGui.BeginChild("deltas", new Vector2(0, 0), ImGuiChildFlags.Borders))
        {
            foreach (var delta in record.Deltas)
            {
                string flags = (delta.Created ? " created" : string.Empty) + (delta.Destroyed ? " destroyed" : string.Empty)
                    + (delta.OwnerChanged ? " owner changed" : string.Empty);
                if (ImGui.Selectable(string.Create(c, $"entity {delta.Entity.Value}{flags}##d{delta.Entity.Value}")))
                {
                    ui.Select(session, delta.Entity);
                }

                foreach (var change in delta.Changes)
                {
                    ImGui.TextUnformatted(string.Create(c, $"    {change.AttrName}: {(change.Old.IsNone ? "-" : change.Old.ToString())} -> {change.New}{(change.Changed ? string.Empty : " (unchanged)")}"));
                }
            }
        }

        ImGui.EndChild();
    }
}
