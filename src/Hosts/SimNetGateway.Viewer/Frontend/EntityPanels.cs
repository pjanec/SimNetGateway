using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SimNetGateway.Engine;
using SimNetGateway.Engine.Monitoring;

namespace SimNetGateway.Viewer.Frontend;

/// <summary>The virtualised table of all entities with filters and sorting (DD-005 section 3, Entities).</summary>
internal sealed class EntitiesPanel : Panel
{
    private string _name = string.Empty;
    private int _origin;
    private int _knownTo;
    private int _unknownTo;
    private float _stale;
    private float _changedWithin;
    private bool _hasPosition;
    private bool _lacksPosition;
    private bool _dirty = true;

    public override string Title => "Entities";

    public override Vector2 DefaultPosition => new(450, 560);

    public override Vector2 DefaultSize => new(430, 390);

    public override void Draw(ViewerSession session, UiState ui)
    {
        var model = session.Entities;
        var engine = session.Engine;
        var c = CultureInfo.InvariantCulture;

        string[] origins = ["any origin", .. session.Dashboard.Origins.Select(o => o.Origin)];
        string[] maps = ["-", .. model.MapNames];
        ImGui.SetNextItemWidth(120);
        _dirty |= Ui.Input("name", ref _name, 64);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        _dirty |= ImGui.Combo("origin", ref _origin, origins, origins.Length);
        ImGui.SetNextItemWidth(120);
        _dirty |= ImGui.Combo("known to", ref _knownTo, maps, maps.Length);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        _dirty |= ImGui.Combo("unknown to", ref _unknownTo, maps, maps.Length);
        ImGui.SetNextItemWidth(120);
        _dirty |= ImGui.InputFloat("not origin-written within s (stale)", ref _stale);
        ImGui.SetNextItemWidth(120);
        _dirty |= ImGui.InputFloat("changed within s", ref _changedWithin);
        _dirty |= ImGui.Checkbox("has position", ref _hasPosition);
        ImGui.SameLine();
        _dirty |= ImGui.Checkbox("lacks position", ref _lacksPosition);

        if (_dirty)
        {
            _dirty = false;
            model.Filter = BuildFilter(session, origins, maps);
        }

        ImGui.Text(string.Create(c, $"{model.Count} of {model.TotalEntities} entities (table refreshed 4 times per second)"));

        int mapCount = model.MapNames.Count;
        int columns = 9 + mapCount;
        var flags = ImGuiTableFlags.Resizable | ImGuiTableFlags.Reorderable | ImGuiTableFlags.Sortable | ImGuiTableFlags.ScrollX
            | ImGuiTableFlags.ScrollY | ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingFixedFit;
        if (!ImGui.BeginTable("entities", columns, flags, new Vector2(0, 0)))
        {
            return;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        ImGui.TableSetupColumn("id", ImGuiTableColumnFlags.DefaultSort);
        ImGui.TableSetupColumn("name");
        ImGui.TableSetupColumn("origin");
        ImGui.TableSetupColumn("owner");
        ImGui.TableSetupColumn("parent");
        ImGui.TableSetupColumn("attrs");
        ImGui.TableSetupColumn("last write");
        ImGui.TableSetupColumn("last change");
        ImGui.TableSetupColumn("origin write");
        for (int m = 0; m < mapCount; m++)
        {
            ImGui.TableSetupColumn(model.MapNames[m]);
        }

        ImGui.TableHeadersRow();
        var specs = ImGui.TableGetSortSpecs();
        if (specs.SpecsDirty && specs.SpecsCount > 0)
        {
            int column = specs.Specs.ColumnIndex;
            bool descending = specs.Specs.SortDirection == ImGuiSortDirection.Descending;
            if (column < 9)
            {
                model.Sort(column switch
                {
                    0 => EntityColumn.Id,
                    1 => EntityColumn.Name,
                    2 => EntityColumn.Origin,
                    3 => EntityColumn.Owner,
                    4 => EntityColumn.Parent,
                    5 => EntityColumn.Attributes,
                    6 => EntityColumn.LastWrite,
                    7 => EntityColumn.LastChange,
                    _ => EntityColumn.LastOriginWrite,
                }, descending);
            }
            else
            {
                model.Sort(EntityColumn.Key, descending, column - 9);
            }

            specs.SpecsDirty = false;
        }

        double now = session.Dashboard.WallTime;
        var rows = model.Rows;
        Ui.ClippedRows(rows.Count, i =>
        {
            var r = rows[i];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            bool selected = r.Id == ui.Selected;
            if (ImGui.Selectable(r.Id.Value.ToString(c) + "##e" + i.ToString(c), selected, ImGuiSelectableFlags.SpanAllColumns))
            {
                ui.Select(session, r.Id);
            }

            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.Name);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.OriginName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.OwnerName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.Parent.IsNone ? "-" : r.Parent.Value.ToString(c));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(r.AttributeCount.ToString(c));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Ui.Age(now, r.LastWriteWallTime));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Ui.Age(now, r.LastChangeWallTime));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(Ui.Age(now, r.LastOriginWriteWallTime));
            for (int m = 0; m < mapCount; m++)
            {
                ImGui.TableNextColumn();
                string? key = r.Keys is not null && m < r.Keys.Length ? r.Keys[m] : null;
                ImGui.TextUnformatted(key ?? "-");
            }
        });

        ImGui.EndTable();
    }

    private EntityFilter BuildFilter(ViewerSession session, string[] origins, string[] maps)
    {
        var schema = session.Schema;
        SourceId? origin = null;
        if (_origin > 0 && _origin < origins.Length && session.Engine.Sources.TryGet(origins[_origin], out var id))
        {
            origin = id;
        }

        List<AttrId>? has = _hasPosition ? [schema.Position] : null;
        List<AttrId>? lacks = _lacksPosition ? [schema.Position] : null;
        return new EntityFilter
        {
            NameContains = string.IsNullOrWhiteSpace(_name) ? null : _name.Trim(),
            Origin = origin,
            KnownTo = _knownTo > 0 && _knownTo < maps.Length ? [maps[_knownTo]] : null,
            UnknownTo = _unknownTo > 0 && _unknownTo < maps.Length ? [maps[_unknownTo]] : null,
            NotOriginWrittenWithin = _stale > 0 ? _stale : null,
            ChangedWithin = _changedWithin > 0 ? _changedWithin : null,
            Has = has,
            Lacks = lacks,
        };
    }
}

/// <summary>Everything the gateway knows about the selected entity (DD-005 section 3, Inspector).</summary>
internal sealed class InspectorPanel : Panel
{
    public override string Title => "Inspector";

    public override Vector2 DefaultPosition => new(890, 508);

    public override Vector2 DefaultSize => new(700, 442);

    public override void Draw(ViewerSession session, UiState ui)
    {
        var m = session.Inspector;
        var c = CultureInfo.InvariantCulture;
        if (m.State == InspectorState.NoSelection)
        {
            ImGui.TextWrapped("Select an entity in the Entities table or on the Map.");
            return;
        }

        double now = session.Dashboard.WallTime;
        string title = string.Create(c, $"entity {m.Selected.Value}");
        if (m.IsRemoved)
        {
            Ui.Colored(Ui.Bad, string.Create(c, $"{title} - REMOVED at sim {m.RemovedSimTime:F3} s (wall {m.RemovedWallTime:F3} s); last copy shown"));
        }
        else
        {
            Ui.Colored(Ui.Good, title + " - alive");
        }

        ImGui.Text(string.Create(c,
            $"origin {m.OriginName}   owner {m.OwnerName}   created {Ui.Age(now, m.CreatedWallTime)} ago   last write {Ui.Age(now, m.LastWriteWallTime)}   last change {Ui.Age(now, m.LastChangeWallTime)}   last origin write {Ui.Age(now, m.LastOriginWriteWallTime)}"));
        if (ImGui.Button("Trace this entity"))
        {
            session.Enqueue(new ViewerAction.WatchEntity(m.Selected));
            ui.Open("Trace");
        }

        ImGui.SameLine();
        if (ImGui.Button("Stop tracing it"))
        {
            session.Enqueue(new ViewerAction.UnwatchEntity(m.Selected));
        }

        if (!ImGui.BeginTabBar("inspector"))
        {
            return;
        }

        if (BeginTab("Attributes", ui, "attributes"))
        {
            DrawSlots(m);
            ImGui.EndTabItem();
        }

        if (BeginTab("Identities and module state", ui, "modules"))
        {
            ImGui.SeparatorText("Identity in every map");
            foreach (var id in m.Identities)
            {
                if (id.Known)
                {
                    ImGui.TextWrapped(string.Create(c, $"{id.Map,-16} {id.Key}   ({id.KeyType}, side {id.Side})"));
                }
                else
                {
                    Ui.Colored(Ui.Warn, string.Create(c, $"{id.Map,-16} not known   (side {id.Side})"));
                }
            }

            ImGui.SeparatorText("Module state (entity tables)");
            if (m.ModuleState.Count == 0)
            {
                ImGui.TextDisabled("no module keeps state for this entity");
            }

            foreach (var row in m.ModuleState)
            {
                ImGui.TextWrapped(string.Create(c, $"{row.Table}:  {row.Text}"));
            }

            ImGui.EndTabItem();
        }

        if (BeginTab("Structure", ui, "structure"))
        {
            foreach (var reference in m.References)
            {
                if (ImGui.Selectable(string.Create(c, $"{reference.AttrName} -> entity {reference.Target.Value}##ref{reference.Attr.Value}")))
                {
                    ui.Select(session, reference.Target);
                }
            }

            foreach (var group in m.Members)
            {
                ImGui.SeparatorText(string.Create(c, $"{group.Members.Length} entities point here through {group.AttrName}"));
                for (int i = 0; i < group.Members.Length && i < 200; i++)
                {
                    var member = group.Members[i];
                    if (ImGui.Selectable(string.Create(c, $"entity {member.Value}##mem{i}")))
                    {
                        ui.Select(session, member);
                    }
                }
            }

            ImGui.EndTabItem();
        }

        if (BeginTab("Recent writes", ui, "writes"))
        {
            if (m.HistoryOff)
            {
                ImGui.TextDisabled("history off (journal capacity 0)");
            }
            else
            {
                DrawWrites(m);
            }

            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
        ui.InspectorTab = null;
    }

    private static bool BeginTab(string label, UiState ui, string name)
    {
        bool open = true;
        var flags = string.Equals(ui.InspectorTab, name, StringComparison.OrdinalIgnoreCase) ? ImGuiTabItemFlags.SetSelected : ImGuiTabItemFlags.None;
        return ImGui.BeginTabItem(label, ref open, flags);
    }

    private static void DrawSlots(InspectorModel m)
    {
        var c = CultureInfo.InvariantCulture;
        if (!Ui.BeginSizedTable("slots", [("attribute", 190), ("value", 0), ("source", 60), ("corr.", 60), ("sim time", 70), ("changed", 70), ("ver.", 40)]))
        {
            return;
        }

        foreach (var s in m.Slots)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(s.Name);
            ImGui.TableNextColumn();
            Ui.Cell(s.Text);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(s.SourceName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(s.Correlation.Value.ToString(c));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(s.SimTime.ToString("F3", c));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(s.ChangedSimTime.ToString("F3", c));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(s.Version.ToString(c));
        }

        ImGui.EndTable();
    }

    private static void DrawWrites(InspectorModel m)
    {
        var c = CultureInfo.InvariantCulture;
        if (!Ui.BeginSizedTable("writes", [("sim time", 70), ("source", 70), ("correlation", 100), ("attribute", 180), ("old", 0), ("new", 0)]))
        {
            return;
        }

        var writes = m.RecentWrites;
        for (int i = writes.Count - 1; i >= 0; i--)
        {
            var w = writes[i];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(w.SimTime.ToString("F3", c));
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(w.SourceName);
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(w.Correlation.Value.ToString(c) + (w.CausedBy.IsNone ? string.Empty : " <- " + w.CausedBy.Value.ToString(c)));
            ImGui.TableNextColumn();
            string flag = w.Created ? " (created)" : w.Destroyed ? " (destroyed)" : w.OwnerChanged ? " (owner changed)" : string.Empty;
            Ui.Cell(w.AttrName + flag + (w.Attr.IsNone || w.Changed ? string.Empty : " (unchanged)"));
            ImGui.TableNextColumn();
            Ui.Cell(w.Old.IsNone ? "-" : w.Old.ToString());
            ImGui.TableNextColumn();
            Ui.Cell(w.New.IsNone ? "-" : w.New.ToString());
        }

        ImGui.EndTable();
    }
}
