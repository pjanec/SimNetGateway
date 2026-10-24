using System.Numerics;
using ImGuiNET;
using SimNetGateway.Engine;

namespace SimNetGateway.Viewer.Frontend;

/// <summary>What the panels share besides the session: the selection and the jumps between panels. UI state only.</summary>
internal sealed class UiState
{
    public EntityId Selected { get; private set; }

    /// <summary>The inspector tab to switch to once (command line); null = none.</summary>
    public string? InspectorTab { get; set; }

    /// <summary>The trace the Trace panel shows in detail; -1 = none.</summary>
    public long SelectedTrace { get; set; } = -1;

    /// <summary>The panels by title, so that one panel can open another.</summary>
    public Dictionary<string, Panel> Panels { get; } = new(StringComparer.Ordinal);

    public void Select(ViewerSession session, EntityId id)
    {
        Selected = id;
        if (!id.IsNone)
        {
            session.Select(id);
            if (Panels.TryGetValue("Inspector", out var inspector))
            {
                inspector.Open = true;
            }
        }
    }

    public void Open(string panel)
    {
        if (Panels.TryGetValue(panel, out var p))
        {
            p.Open = true;
            ImGui.SetWindowFocus(panel);
        }
    }
}

/// <summary>One ImGui window of the viewer (DD-005 section 3). A panel reads the session and enqueues actions; it never touches the engine.</summary>
internal abstract class Panel
{
    public abstract string Title { get; }

    public bool Open { get; set; }

    public virtual Vector2 DefaultPosition => new(40, 80);

    public virtual Vector2 DefaultSize => new(520, 380);

    public abstract void Draw(ViewerSession session, UiState ui);
}

/// <summary>Small shared drawing helpers and the colours.</summary>
internal static class Ui
{
    public static readonly Vector4 Good = new(0.35f, 0.85f, 0.45f, 1f);
    public static readonly Vector4 Bad = new(1.0f, 0.4f, 0.35f, 1f);
    public static readonly Vector4 Warn = new(1.0f, 0.8f, 0.3f, 1f);
    public static readonly Vector4 Dim = new(0.6f, 0.6f, 0.65f, 1f);

    /// <summary>Seconds as "12.3 s", "-" for none.</summary>
    public static string Age(double now, double then) =>
        then <= 0 ? "-" : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Math.Max(0, now - then):F2} s");

    public static string Fmt(double value, string format = "F3") =>
        value.ToString(format, System.Globalization.CultureInfo.InvariantCulture);

    public static void Colored(Vector4 color, string text) => ImGui.TextColored(color, text);

    /// <summary>Wraps a clipped table body: <paramref name="row"/> is called for every visible index.</summary>
    public static unsafe void ClippedRows(int count, Action<int> row)
    {
        var clipper = new ImGuiListClipperPtr(ImGuiNative.ImGuiListClipper_ImGuiListClipper());
        try
        {
            clipper.Begin(count);
            while (clipper.Step())
            {
                for (int i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
                {
                    row(i);
                }
            }

            clipper.End();
        }
        finally
        {
            clipper.Destroy();
        }
    }

    /// <summary>Starts a table whose columns are sized by the caller: <c>(name, width)</c> with width 0 = takes the rest and wraps.</summary>
    public static bool BeginSizedTable(string id, (string Name, float Width)[] columns, Vector2 outer = default, bool scrollY = true)
    {
        var flags = ImGuiTableFlags.Resizable | ImGuiTableFlags.RowBg | ImGuiTableFlags.Borders | ImGuiTableFlags.BordersOuter;
        if (scrollY)
        {
            flags |= ImGuiTableFlags.ScrollY;
        }

        if (!ImGui.BeginTable(id, columns.Length, flags, outer))
        {
            return false;
        }

        ImGui.TableSetupScrollFreeze(0, 1);
        foreach (var (name, width) in columns)
        {
            if (width > 0)
            {
                ImGui.TableSetupColumn(name, ImGuiTableColumnFlags.WidthFixed, width);
            }
            else
            {
                ImGui.TableSetupColumn(name, ImGuiTableColumnFlags.WidthStretch);
            }
        }

        ImGui.TableHeadersRow();
        return true;
    }

    /// <summary>A cell with a long text: wrapped to the column, the full text also in a tooltip.</summary>
    public static void Cell(string text)
    {
        ImGui.TextWrapped(text);
        if (ImGui.IsItemHovered() && text.Length > 40)
        {
            ImGui.SetTooltip(text);
        }
    }

    /// <summary>A text input bound to a string field; returns true when it changed.</summary>
    public static bool Input(string label, ref string value, uint max = 128) => ImGui.InputText(label, ref value, max);
}
