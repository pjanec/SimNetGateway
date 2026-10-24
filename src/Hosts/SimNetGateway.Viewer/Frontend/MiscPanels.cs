using System.Globalization;
using System.Numerics;
using ImGuiNET;
using SimNetGateway.Engine;
using SimNetGateway.Engine.Monitoring;

namespace SimNetGateway.Viewer.Frontend;

/// <summary>Options of the map drawn behind the windows, and what is under the selection (DD-005 section 3, Map).</summary>
internal sealed class MapPanel : Panel
{
    private readonly MapView _view;

    public MapPanel(MapView view) => _view = view;

    public override string Title => "Map";

    public override Vector2 DefaultPosition => new(10, 704);

    public override Vector2 DefaultSize => new(430, 250);

    public override void Draw(ViewerSession session, UiState ui)
    {
        var map = session.Map;
        var c = CultureInfo.InvariantCulture;
        bool labels = _view.ShowLabels, links = _view.ShowLinks, errors = _view.ShowErrors, stale = _view.ShowStale, grid = _view.ShowGrid;
        if (ImGui.Checkbox("labels (zoomed in)", ref labels))
        {
            _view.ShowLabels = labels;
        }

        ImGui.SameLine();
        if (ImGui.Checkbox("formation links", ref links))
        {
            _view.ShowLinks = links;
        }

        if (ImGui.Checkbox("error vectors", ref errors))
        {
            _view.ShowErrors = errors;
        }

        ImGui.SameLine();
        if (ImGui.Checkbox("stale candidates", ref stale))
        {
            _view.ShowStale = stale;
        }

        ImGui.SameLine();
        if (ImGui.Checkbox("grid", ref grid))
        {
            _view.ShowGrid = grid;
        }

        float magnification = _view.ErrorMagnification;
        if (ImGui.SliderFloat("error vector x", ref magnification, 1f, 1000f, "%.0f", ImGuiSliderFlags.Logarithmic))
        {
            _view.ErrorMagnification = magnification;
        }

        float threshold = _view.ErrorThresholdMetres;
        if (ImGui.SliderFloat("ring above (m)", ref threshold, 0.1f, 100f, "%.1f", ImGuiSliderFlags.Logarithmic))
        {
            _view.ErrorThresholdMetres = threshold;
        }

        if (ImGui.Button("Fit view"))
        {
            _view.RequestFit();
        }

        ImGui.SameLine();
        ImGui.Text(string.Create(c, $"{map.Points.Count} of {map.EntityCount} entities have a position"));
        ImGui.TextDisabled("drag = pan, wheel = zoom, click = select. circle = moving, square = static, magenta ring = stale, orange ring = large error, yellow line = received -> predicted.");
        foreach (var (origin, count) in _view.Legend.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            ImGui.TextColored(MapView.OriginColorVector(origin), string.Create(c, $"{origin}: {count}"));
            ImGui.SameLine();
        }

        ImGui.NewLine();
        int index = ui.Selected.IsNone ? -1 : map.IndexOf(ui.Selected);
        if (index >= 0)
        {
            var p = map.Points[index];
            ImGui.Text(string.Create(c, $"{p.Name}: lat {p.LatitudeDeg:F6} lon {p.LongitudeDeg:F6}"));
            ImGui.Text(p.HasPrediction
                ? string.Create(c, $"received vs predicted: {p.ErrorMetres:F2} m apart; {(p.IsStatic ? "static" : "moving")}{(p.IsStale ? ", stale candidate" : string.Empty)}")
                : "no prediction yet");
        }
    }
}

/// <summary>Identity maps with their bound counts and the allocators behind them (DD-005 section 3, Identities).</summary>
internal sealed class IdentitiesPanel : Panel
{
    public override string Title => "Identities";

    public override Vector2 DefaultPosition => new(450, 134);

    public override Vector2 DefaultSize => new(1140, 400);

    public override void Draw(ViewerSession session, UiState ui)
    {
        var c = CultureInfo.InvariantCulture;
        if (ImGui.BeginTable("maps", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.SizingFixedFit))
        {
            foreach (string h in new[] { "map", "side", "key type", "bound" })
            {
                ImGui.TableSetupColumn(h);
            }

            ImGui.TableHeadersRow();
            foreach (var map in session.Identities.Maps)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(map.Name);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(map.Side);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(map.KeyType);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(map.Count.ToString(c));
            }

            ImGui.EndTable();
        }

        ImGui.SeparatorText("Allocators (exhaustion explains \"not published\")");
        foreach (var map in session.Identities.Maps)
        {
            if (map.Allocator is not { } a)
            {
                continue;
            }

            ImGui.Text(string.Create(c, $"{map.Name} ({a.Name}): {a.InUse} in use, {a.Quarantined} in quarantine, {a.Available} available of {a.Capacity}"));
            float fraction = a.Capacity > 0 ? (float)(a.InUse + a.Quarantined) / a.Capacity : 0f;
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, fraction > 0.9f ? Ui.Bad : fraction > 0.7f ? Ui.Warn : Ui.Good);
            ImGui.ProgressBar(fraction, new Vector2(-1, 8), string.Empty);
            ImGui.PopStyleColor();
        }
    }
}

/// <summary>Recent diagnostics with severity and text filters (DD-005 section 3, Diagnostics).</summary>
internal sealed class DiagnosticsPanel : Panel
{
    private static readonly string[] s_severities = ["Info", "Warning", "Error"];
    private int _severity;
    private string _text = string.Empty;
    private string _appliedText = string.Empty;

    public override string Title => "Diagnostics";

    public override Vector2 DefaultPosition => new(450, 134);

    public override Vector2 DefaultSize => new(1140, 810);

    public override void Draw(ViewerSession session, UiState ui)
    {
        var model = session.Diagnostics;
        var c = CultureInfo.InvariantCulture;

        // another panel may have set the text filter (Rules -> a failed rule)
        if (!string.Equals(model.Text ?? string.Empty, _appliedText, StringComparison.Ordinal))
        {
            _text = model.Text ?? string.Empty;
            _appliedText = _text;
        }

        _severity = Math.Clamp((int)model.MinSeverity, 0, 2);
        ImGui.SetNextItemWidth(110);
        if (ImGui.Combo("min severity", ref _severity, s_severities, s_severities.Length))
        {
            model.MinSeverity = (DiagnosticSeverity)_severity;
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(220);
        if (Ui.Input("text", ref _text, 96))
        {
            _appliedText = _text;
            model.Text = _text;
        }

        var k = model.Counters;
        ImGui.Text(string.Create(c,
            $"{model.Entries.Count} shown of {k.EntriesRetained}/{k.EntryCapacity} retained; {k.TransactionFailures} transaction failures, {k.RuleFailures} rule failures, {k.CascadeOverflows} cascade overflows, {k.UnhandledMessages} unhandled"));
        if (!Ui.BeginSizedTable("diag", [("time", 70), ("severity", 70), ("category", 110), ("origin", 200), ("message", 0)]))
        {
            return;
        }

        var entries = model.Entries;
        Ui.ClippedRows(entries.Count, row =>
        {
            var e = entries[entries.Count - 1 - row];
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(e.WallTime.ToString("F3", c));
            ImGui.TableNextColumn();
            ImGui.TextColored(e.Severity == DiagnosticSeverity.Error ? Ui.Bad : e.Severity == DiagnosticSeverity.Warning ? Ui.Warn : Ui.Dim, e.Severity.ToString());
            ImGui.TableNextColumn();
            ImGui.TextUnformatted(e.Category);
            ImGui.TableNextColumn();
            if (ImGui.Selectable(e.Origin + "##o" + row.ToString(c)) && session.Engine.FindRule(e.Origin) is not null)
            {
                ui.Open("Rules");
            }

            ImGui.TableNextColumn();
            Ui.Cell(e.Message);
        });
        ImGui.EndTable();
    }
}

/// <summary>The result of <c>Validate()</c> and the schema dump (DD-005 section 3, Vocabulary).</summary>
internal sealed class VocabularyPanel : Panel
{
    private string? _dump;
    private string[] _lines = [];

    public override string Title => "Vocabulary";

    public override Vector2 DefaultPosition => new(450, 134);

    public override Vector2 DefaultSize => new(1140, 810);

    public override void Draw(ViewerSession session, UiState ui)
    {
        var model = session.Vocabulary;
        ImGui.Text(string.Create(CultureInfo.InvariantCulture, $"Validate(): {model.Errors.Count} errors, {model.Warnings.Count} warnings"));
        foreach (var issue in model.Errors)
        {
            Ui.Colored(Ui.Bad, issue.ToString());
        }

        foreach (var issue in model.Warnings)
        {
            Ui.Colored(Ui.Warn, issue.ToString());
        }

        ImGui.SeparatorText("Schema dump (producers and consumers)");
        if (!ReferenceEquals(_dump, model.Dump))
        {
            _dump = model.Dump;
            _lines = model.Dump.Split('\n');
        }

        if (ImGui.BeginChild("dump", new Vector2(0, 0), ImGuiChildFlags.Borders))
        {
            Ui.ClippedRows(_lines.Length, i =>
            {
                string line = _lines[i];
                bool warning = line.Contains("never", StringComparison.OrdinalIgnoreCase) || line.Contains("WARN", StringComparison.Ordinal);
                if (warning)
                {
                    ImGui.TextColored(Ui.Warn, line);
                }
                else
                {
                    ImGui.TextUnformatted(line);
                }
            });
        }

        ImGui.EndChild();
    }
}
