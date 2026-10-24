using System.Numerics;
using ImGuiNET;
using Raylib_cs;

namespace SimNetGateway.Viewer.Frontend;

/// <summary>
/// The raylib window with the ImGui panels of DD-005 section 3 (R-073). It owns no state of the gateway: every frame it calls
/// <see cref="ViewerSession.Frame"/> with the real frame time, draws the map with raylib and the panels with ImGui from the
/// session's models, and turns the operator's clicks into <see cref="ViewerAction"/>s on the session.
/// </summary>
internal sealed class ViewerFrontend : IDisposable
{
    private readonly ViewerSession _session;
    private readonly UiState _ui = new();
    private readonly MapView _map = new();
    private readonly List<Panel> _panels = new();
    private readonly HashSet<Panel> _placed = new();
    private bool _windowOpen;
    private bool _imguiReady;

    private readonly ViewerCommandLine? _line;
    private readonly List<(double Seconds, string Path)> _shots = new();

    public ViewerFrontend(ViewerSession session, ViewerCommandLine? line = null)
    {
        _session = session;
        _line = line;
        if (line is not null)
        {
            _shots.AddRange(line.Screenshots.OrderBy(x => x.Seconds));
        }

        Add(new ControlsPanel(), true);
        Add(new DashboardPanel(), true);
        Add(new ScenarioPanel(), true);
        Add(new EntitiesPanel(), true);
        Add(new InspectorPanel(), false);
        Add(new MapPanel(_map), true);
        Add(new RulesPanel(), false);
        Add(new TracePanel(), false);
        Add(new JournalPanel(), false);
        Add(new IdentitiesPanel(), false);
        Add(new DiagnosticsPanel(), false);
        Add(new VocabularyPanel(), false);
        if (line is not null)
        {
            foreach (string name in line.OpenPanels)
            {
                SetOpen(name, true);
            }

            foreach (string name in line.ClosePanels)
            {
                SetOpen(name, false);
            }

            _ui.InspectorTab = line.InspectorTab;
            if (line.TraceAll)
            {
                session.Enqueue(new ViewerAction.WatchAll(true));
            }
        }
    }

    private void SetOpen(string name, bool open)
    {
        foreach (var panel in _panels)
        {
            if (panel.Title.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                panel.Open = open;
            }
        }
    }

    private void Add(Panel panel, bool open)
    {
        panel.Open = open;
        _panels.Add(panel);
        _ui.Panels[panel.Title] = panel;
    }

    /// <summary>Opens the window and runs until it is closed.</summary>
    public void Run()
    {
        Raylib.SetConfigFlags(ConfigFlags.ResizableWindow | ConfigFlags.Msaa4xHint);
        Raylib.InitWindow(1600, 960, "SimNetGateway viewer");
        _windowOpen = true;
        Raylib.SetTargetFPS(60);
        Raylib_cs.Raylib.SetExitKey(KeyboardKey.Null);
        rlImGui_cs.rlImGui.Setup(true);
        _imguiReady = true;
        DisableIniFile();

        while (!Raylib.WindowShouldClose())
        {
            // a long frame (window drag, breakpoint) must not owe seconds of ticks
            float dt = Math.Min(Raylib.GetFrameTime(), 0.1f);
            HandleKeys();
            _session.Frame(dt);
            SelectByName();

            Raylib.BeginDrawing();
            Raylib.ClearBackground(new Color(24, 26, 32, 255));
            _map.Draw(_session, _ui);
            rlImGui_cs.rlImGui.Begin();
            DrawUi();
            rlImGui_cs.rlImGui.End();
            if (_shots.Count > 0 && Raylib.GetTime() >= _shots[0].Seconds)
            {
                var image = Raylib.LoadImageFromScreen();
                Raylib.ExportImage(image, _shots[0].Path);
                Raylib.UnloadImage(image);
                _shots.RemoveAt(0);
                if (_shots.Count == 0)
                {
                    Raylib.EndDrawing();
                    break;
                }
            }

            Raylib.EndDrawing();
        }
    }

    // the window layout is not saved: no imgui.ini is written into the working directory
    private static unsafe void DisableIniFile() => ImGui.GetIO().NativePtr->IniFilename = null;

    private string? _pendingSelect;
    private bool _focused;

    // --select NAME: pick the entity as soon as the name exists (scripted screenshots)
    private void SelectByName()
    {
        if (!_focused && _line?.FocusName is { } focus)
        {
            foreach (var point in _session.Map.Points)
            {
                if (point.Name == focus)
                {
                    _map.Focus(_session.Generation, point.LatitudeDeg, point.LongitudeDeg, _line.FocusMetres);
                    _focused = true;
                    break;
                }
            }
        }

        _pendingSelect ??= _line?.SelectName;
        if (_pendingSelect is null)
        {
            return;
        }

        foreach (var point in _session.Map.Points)
        {
            if (point.Name == _pendingSelect)
            {
                _ui.Select(_session, point.Id);
                _pendingSelect = null;
                return;
            }
        }

        // entities without a position are not on the map: look at the table's names too
        foreach (var row in _session.Entities.Rows)
        {
            if (row.Name == _pendingSelect)
            {
                _ui.Select(_session, row.Id);
                _pendingSelect = null;
                return;
            }
        }
    }

    private void HandleKeys()
    {
        if (ImGui.GetCurrentContext() != IntPtr.Zero && ImGui.GetIO().WantTextInput)
        {
            return;
        }

        if (Raylib.IsKeyPressed(KeyboardKey.Space))
        {
            _session.Enqueue(_session.Playing ? new ViewerAction.Pause() : new ViewerAction.Play());
        }

        if (Raylib.IsKeyPressed(KeyboardKey.N))
        {
            _session.Enqueue(new ViewerAction.Step());
        }

        if (Raylib.IsKeyPressed(KeyboardKey.F))
        {
            _map.RequestFit();
        }

        if (Raylib.IsKeyPressed(KeyboardKey.Equal) || Raylib.IsKeyPressed(KeyboardKey.KpAdd))
        {
            _session.Enqueue(new ViewerAction.SetSpeed(_session.Speed * 2));
        }

        if (Raylib.IsKeyPressed(KeyboardKey.Minus) || Raylib.IsKeyPressed(KeyboardKey.KpSubtract))
        {
            _session.Enqueue(new ViewerAction.SetSpeed(_session.Speed / 2));
        }
    }

    private void DrawUi()
    {
        if (ImGui.BeginMainMenuBar())
        {
            if (ImGui.BeginMenu("View"))
            {
                foreach (var panel in _panels)
                {
                    bool open = panel.Open;
                    if (ImGui.MenuItem(panel.Title, string.Empty, ref open))
                    {
                        panel.Open = open;
                    }
                }

                ImGui.EndMenu();
            }

            ImGui.Text($"   {_session.ScenarioName}   {ImGui.GetIO().Framerate:F0} fps");
            ImGui.EndMainMenuBar();
        }

        foreach (var panel in _panels)
        {
            if (!panel.Open)
            {
                continue;
            }

            if (_placed.Add(panel))
            {
                ImGui.SetNextWindowPos(panel.DefaultPosition, ImGuiCond.FirstUseEver);
                ImGui.SetNextWindowSize(panel.DefaultSize, ImGuiCond.FirstUseEver);
            }

            bool open = panel.Open;
            if (ImGui.Begin(panel.Title, ref open))
            {
                panel.Draw(_session, _ui);
            }

            ImGui.End();
            panel.Open = open;
        }
    }

    public void Dispose()
    {
        if (_imguiReady)
        {
            rlImGui_cs.rlImGui.Shutdown();
            _imguiReady = false;
        }

        if (_windowOpen)
        {
            Raylib.CloseWindow();
            _windowOpen = false;
        }
    }
}
