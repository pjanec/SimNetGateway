using System.Globalization;
using System.Numerics;
using ImGuiNET;
using Raylib_cs;
using SimNetGateway.Engine;

namespace SimNetGateway.Viewer.Frontend;

/// <summary>
/// Draws the <see cref="MapModel"/> with raylib 2D primitives behind the ImGui windows (DD-005 section 3, Map): an equirectangular
/// projection around the view centre, pan with the left mouse button, zoom with the wheel (about the cursor), click to select,
/// entities coloured by origin, formation links, the received-versus-predicted error vector and the stale and static marks.
/// </summary>
internal sealed class MapView
{
    private const float PointRadius = 3.5f;
    private const float PickRadius = 12f;

    private double _centerLat = 50.0;
    private double _centerLon = 14.0;
    private double _cosLat = Math.Cos(50.0 * Math.PI / 180.0);
    private double _pixelsPerDegree = 20_000;
    private int _fitGeneration = -1;
    private Vector2 _pressAt;
    private bool _dragging;
    private bool _pressed;
    private readonly Dictionary<string, int> _legend = new(StringComparer.Ordinal);

    public bool ShowLabels { get; set; } = true;

    public bool ShowLinks { get; set; } = true;

    public bool ShowErrors { get; set; } = true;

    public bool ShowStale { get; set; } = true;

    public bool ShowGrid { get; set; } = true;

    /// <summary>Error vectors are drawn this many times longer than the real error in metres (so that metres are visible).</summary>
    public float ErrorMagnification { get; set; } = 1f;

    /// <summary>Points whose error is above this many metres get a warning ring.</summary>
    public float ErrorThresholdMetres { get; set; } = 5f;

    public double PixelsPerDegree => _pixelsPerDegree;

    public double CenterLatitude => _centerLat;

    public double CenterLongitude => _centerLon;

    /// <summary>Hovered entity (nearest point under the cursor), 0 = none.</summary>
    public int HoveredPoint { get; private set; } = -1;

    /// <summary>Fits the view to the entities (or a default area around Prague when there are none).</summary>
    public void Fit(MapModel map)
    {
        if (!map.HasBounds)
        {
            _centerLat = 50.0;
            _centerLon = 14.0;
            _pixelsPerDegree = 20_000;
            _cosLat = Math.Cos(_centerLat * Math.PI / 180.0);
            return;
        }

        _centerLat = (map.MinLatitudeDeg + map.MaxLatitudeDeg) / 2;
        _centerLon = (map.MinLongitudeDeg + map.MaxLongitudeDeg) / 2;
        _cosLat = Math.Max(0.05, Math.Cos(_centerLat * Math.PI / 180.0));
        double spanLat = Math.Max(map.MaxLatitudeDeg - map.MinLatitudeDeg, 0.0002);
        double spanLon = Math.Max((map.MaxLongitudeDeg - map.MinLongitudeDeg) * _cosLat, 0.0002);
        // the default layout leaves the middle of the window free
        double w = Math.Max(200, Raylib.GetScreenWidth() * 0.40);
        double h = Math.Max(150, Raylib.GetScreenHeight() * 0.32);
        _pixelsPerDegree = Math.Min(w / spanLon, h / spanLat);
    }

    /// <summary>Called every frame: draws the map and handles the mouse unless ImGui wants it.</summary>
    public void Draw(ViewerSession session, UiState ui)
    {
        var map = session.Map;
        if (_fitGeneration != session.Generation)
        {
            _fitGeneration = session.Generation;
            _fitPending = true;
            _userMoved = false;
        }

        // fit when the first positions of a new scenario have arrived
        // the view follows the entities until the operator pans or zooms; "Fit view" (F) follows again
        if ((_fitPending || !_userMoved) && map.HasBounds)
        {
            Fit(map);
            _fitPending = false;
        }

        bool wantMouse = ImGui.GetIO().WantCaptureMouse;
        HandleMouse(map, ui, session, wantMouse);

        int width = Raylib.GetScreenWidth(), height = Raylib.GetScreenHeight();
        if (ShowGrid)
        {
            DrawGrid(width, height);
        }

        var points = map.Points;
        var selected = ui.Selected;
        _legend.Clear();

        if (ShowLinks)
        {
            var linkColor = new Color(170, 170, 230, 210);
            var anchorColor = new Color(190, 190, 255, 255);
            var drawn = new HashSet<EntityId>();
            foreach (var link in map.Links)
            {
                var child = points[link.ChildIndex];
                var anchor = ToScreen(link.AnchorLatitudeDeg, link.AnchorLongitudeDeg);
                Raylib.DrawLineEx(ToScreen(child.LatitudeDeg, child.LongitudeDeg), anchor, 2f, linkColor);
                if (!link.ParentOnMap && drawn.Add(link.Parent))
                {
                    // a parent without a position of its own (unit, aggregate): a diamond at its members' centroid
                    Raylib.DrawPoly(anchor, 4, 7f, 45f, anchorColor);
                    if (ShowLabels)
                    {
                        Raylib.DrawText(link.ParentName, (int)anchor.X + 9, (int)anchor.Y - 7, 13, anchorColor);
                    }
                }
            }
        }

        bool labels = ShowLabels && _pixelsPerDegree > 1500;
        for (int i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var s = ToScreen(p.LatitudeDeg, p.LongitudeDeg);
            if (s.X < -20 || s.Y < -20 || s.X > width + 20 || s.Y > height + 20)
            {
                continue;
            }

            var color = OriginColor(p.OriginName);
            _legend[p.OriginName] = _legend.TryGetValue(p.OriginName, out int c) ? c + 1 : 1;
            if (ShowErrors && p.HasPrediction && p.ErrorMetres > 0.01)
            {
                var to = ToScreen(p.PredictedLatitudeDeg, p.PredictedLongitudeDeg);
                var d = to - s;
                // the real vector is tiny at map scale: scale it by the magnification and keep it at least a few pixels long
                var shown = s + d * ErrorMagnification;
                if ((shown - s).Length() < 4f && d.Length() > 0)
                {
                    shown = s + Vector2.Normalize(d) * 4f;
                }

                Raylib.DrawLineV(s, shown, new Color(255, 220, 60, 255));
                if (p.ErrorMetres > ErrorThresholdMetres)
                {
                    Raylib.DrawCircleLines((int)s.X, (int)s.Y, PointRadius + 5, new Color(255, 120, 40, 255));
                }
            }

            if (p.IsStatic)
            {
                Raylib.DrawRectangleV(new Vector2(s.X - PointRadius, s.Y - PointRadius), new Vector2(PointRadius * 2, PointRadius * 2), color);
            }
            else
            {
                Raylib.DrawCircleV(s, PointRadius, color);
            }

            if (ShowStale && p.IsStale)
            {
                Raylib.DrawCircleLines((int)s.X, (int)s.Y, PointRadius + 3, new Color(200, 80, 200, 255));
            }

            if (labels)
            {
                Raylib.DrawText(p.Name, (int)s.X + 7, (int)s.Y - 6, 12, new Color(235, 235, 245, 255));
            }
        }

        int selectedIndex = selected.IsNone ? -1 : map.IndexOf(selected);
        if (selectedIndex >= 0)
        {
            var p = points[selectedIndex];
            var s = ToScreen(p.LatitudeDeg, p.LongitudeDeg);
            Raylib.DrawCircleLines((int)s.X, (int)s.Y, PointRadius + 6, Color.White);
            Raylib.DrawText(p.Name, (int)s.X + 10, (int)s.Y - 18, 16, Color.White);
        }

        if (HoveredPoint >= 0 && HoveredPoint < points.Count && !wantMouse)
        {
            var p = points[HoveredPoint];
            var hs = ToScreen(p.LatitudeDeg, p.LongitudeDeg);
            Raylib.DrawText(p.Name, (int)hs.X + 8, (int)hs.Y - 18, 14, Color.White);
            var mouse = Raylib.GetMousePosition();
            string text = string.Create(CultureInfo.InvariantCulture, $"{p.Name}  [{p.OriginName}]  err {p.ErrorMetres:F1} m");
            Raylib.DrawRectangle((int)mouse.X + 12, (int)mouse.Y + 12, Raylib.MeasureText(text, 12) + 8, 18, new Color(20, 20, 30, 220));
            Raylib.DrawText(text, (int)mouse.X + 16, (int)mouse.Y + 15, 12, Color.White);
        }

        DrawScale(height);
    }

    private bool _fitPending = true;
    private bool _userMoved;

    /// <summary>Centres the view on a point and shows about <paramref name="metres"/> across; the view stops following the entities.</summary>
    public void Focus(int generation, double lat, double lon, double metres)
    {
        _fitGeneration = generation;
        _centerLat = lat;
        _centerLon = lon;
        _cosLat = Math.Max(0.05, Math.Cos(lat * Math.PI / 180.0));
        _pixelsPerDegree = Raylib.GetScreenWidth() * 0.4 / (metres / 111_320.0);
        _userMoved = true;
        _fitPending = false;
    }

    public void RequestFit()
    {
        _fitPending = true;
        _userMoved = false;
    }

    /// <summary>Entities drawn per origin at the last draw (for the legend).</summary>
    public IReadOnlyDictionary<string, int> Legend => _legend;

    public static Vector4 OriginColorVector(string origin)
    {
        var c = OriginColor(origin);
        return new Vector4(c.R / 255f, c.G / 255f, c.B / 255f, 1f);
    }

    private static Color OriginColor(string origin)
    {
        if (origin.StartsWith("ned", StringComparison.OrdinalIgnoreCase))
        {
            return new Color(90, 170, 255, 255);
        }

        if (origin.StartsWith("rpr", StringComparison.OrdinalIgnoreCase))
        {
            return new Color(255, 120, 90, 255);
        }

        if (origin.StartsWith("display", StringComparison.OrdinalIgnoreCase))
        {
            return new Color(200, 200, 90, 255);
        }

        return new Color(150, 220, 150, 255);
    }

    // ---- projection -----------------------------------------------------------------------------------------------

    // the view centre is the middle of the part of the window that the default panel layout leaves free
    private static float CenterX => Raylib.GetScreenWidth() / 2f;

    private static float CenterY => Raylib.GetScreenHeight() * 0.36f;

    private Vector2 ToScreen(double lat, double lon) => new(
        (float)(CenterX + (lon - _centerLon) * _cosLat * _pixelsPerDegree),
        (float)(CenterY - (lat - _centerLat) * _pixelsPerDegree));

    private void FromScreen(Vector2 s, out double lat, out double lon)
    {
        lon = _centerLon + (s.X - CenterX) / (_pixelsPerDegree * _cosLat);
        lat = _centerLat - (s.Y - CenterY) / _pixelsPerDegree;
    }

    private void HandleMouse(MapModel map, UiState ui, ViewerSession session, bool wantMouse)
    {
        var mouse = Raylib.GetMousePosition();
        HoveredPoint = wantMouse ? -1 : Nearest(map, mouse);
        if (wantMouse)
        {
            _pressed = false;
            _dragging = false;
            return;
        }

        float wheel = Raylib.GetMouseWheelMove();
        if (wheel != 0)
        {
            _userMoved = true;
            FromScreen(mouse, out double lat0, out double lon0);
            _pixelsPerDegree = Math.Clamp(_pixelsPerDegree * Math.Pow(1.15, wheel), 50, 5e8);
            FromScreen(mouse, out double lat1, out double lon1);
            _centerLat += lat0 - lat1;
            _centerLon += lon0 - lon1;
        }

        if (Raylib.IsMouseButtonPressed(MouseButton.Left))
        {
            _pressed = true;
            _dragging = false;
            _pressAt = mouse;
        }

        if (_pressed && Raylib.IsMouseButtonDown(MouseButton.Left))
        {
            var delta = Raylib.GetMouseDelta();
            if (_dragging || (mouse - _pressAt).Length() > 4f)
            {
                _dragging = true;
                _userMoved = true;
                _centerLon -= delta.X / (_pixelsPerDegree * _cosLat);
                _centerLat += delta.Y / _pixelsPerDegree;
            }
        }

        if (_pressed && Raylib.IsMouseButtonReleased(MouseButton.Left))
        {
            if (!_dragging)
            {
                int hit = Nearest(map, mouse);
                if (hit >= 0)
                {
                    ui.Select(session, map.Points[hit].Id);
                }
            }

            _pressed = false;
            _dragging = false;
        }
    }

    private int Nearest(MapModel map, Vector2 mouse)
    {
        var points = map.Points;
        int best = -1;
        float bestDistance = PickRadius * PickRadius;
        for (int i = 0; i < points.Count; i++)
        {
            var s = ToScreen(points[i].LatitudeDeg, points[i].LongitudeDeg);
            float dx = s.X - mouse.X, dy = s.Y - mouse.Y;
            float d = dx * dx + dy * dy;
            if (d <= bestDistance)
            {
                bestDistance = d;
                best = i;
            }
        }

        return best;
    }

    // ---- decoration -----------------------------------------------------------------------------------------------

    private void DrawGrid(int width, int height)
    {
        // a graticule whose lines are at least 90 pixels apart
        double[] steps = [0.00001, 0.00002, 0.00005, 0.0001, 0.0002, 0.0005, 0.001, 0.002, 0.005, 0.01, 0.02, 0.05, 0.1, 0.2, 0.5, 1, 2, 5, 10];
        double step = steps[^1];
        foreach (double s in steps)
        {
            if (s * _pixelsPerDegree >= 90)
            {
                step = s;
                break;
            }
        }

        FromScreen(new Vector2(0, height), out double minLat, out double minLon);
        FromScreen(new Vector2(width, 0), out double maxLat, out double maxLon);
        var line = new Color(60, 60, 75, 255);
        var text = new Color(110, 110, 130, 255);
        for (double lat = Math.Floor(minLat / step) * step; lat <= maxLat; lat += step)
        {
            int y = (int)ToScreen(lat, minLon).Y;
            Raylib.DrawLine(0, y, width, y, line);
            Raylib.DrawText(lat.ToString("F5", CultureInfo.InvariantCulture), 4, y - 12, 10, text);
        }

        for (double lon = Math.Floor(minLon / step) * step; lon <= maxLon; lon += step)
        {
            int x = (int)ToScreen(minLat, lon).X;
            Raylib.DrawLine(x, 0, x, height, line);
            Raylib.DrawText(lon.ToString("F5", CultureInfo.InvariantCulture), x + 3, height - 14, 10, text);
        }
    }

    private void DrawScale(int height)
    {
        // a bar of about 100 pixels, rounded to 1, 2 or 5 x 10^n metres
        double metresPerPixel = 111_320.0 / _pixelsPerDegree;
        double target = metresPerPixel * 100;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(target)));
        double nice = target / magnitude >= 5 ? 5 : target / magnitude >= 2 ? 2 : 1;
        double metres = nice * magnitude;
        int pixels = (int)(metres / metresPerPixel);
        string label = metres >= 1000 ? (metres / 1000).ToString("0.##", CultureInfo.InvariantCulture) + " km" : metres.ToString("0.##", CultureInfo.InvariantCulture) + " m";
        Raylib.DrawLine(20, height - 30, 20 + pixels, height - 30, Color.White);
        Raylib.DrawText(label, 20, height - 28 - 14, 12, Color.White);
    }
}
