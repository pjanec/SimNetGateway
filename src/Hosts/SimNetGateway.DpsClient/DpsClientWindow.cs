using System.Numerics;
using Raylib_cs;
using D = SimNetGateway.Protocol.DdsPlayer;

namespace SimNetGateway.DpsClient;

/// <summary>
/// The raylib top-down map of the reference client: draws what <see cref="DpsWorld"/> holds and reads the keyboard and mouse. Keys: J join the first unit offer,
/// WASD move, mouse aim, left button fire, C stance, Esc leave (a second Esc closes), right button picks a unit, G toggles the Paths and Contacts gizmo layers for the
/// picked unit, mouse wheel zooms, arrow keys pan while not playing.
/// </summary>
internal static class DpsClientWindow
{
    private static readonly string[] GizmoLayers = ["Paths", "Contacts"];

    public static int Run(DpsWorld world, uint domain, string partition)
    {
        Raylib.SetConfigFlags(ConfigFlags.ResizableWindow);
        Raylib.InitWindow(1100, 760, $"DPS client - domain {domain}, partition {partition}");
        Raylib.SetTargetFPS(60);
        Raylib.SetExitKey(KeyboardKey.Null);

        float metersPerPixel = 0.5f;
        var center = new Vector2();
        int picked = 0;
        bool gizmosOn = false;
        double nextControl = 0;
        bool wasPlaying = false;
        string note = "J: join";

        while (!Raylib.WindowShouldClose())
        {
            world.Pump();
            double now = world.Now;
            bool playing = world.IsPlaying;

            // follow the own unit while playing
            if (playing && world.Slot is { } slot && world.Entities.TryGetValue(slot.UnitId, out var own) && own.State is not null)
            {
                var p = own.PositionAt(now);
                center = new Vector2((float)p.X, (float)p.Y);
            }
            else
            {
                float pan = 400f * metersPerPixel * Raylib.GetFrameTime();
                if (Raylib.IsKeyDown(KeyboardKey.Left)) center.X -= pan;
                if (Raylib.IsKeyDown(KeyboardKey.Right)) center.X += pan;
                if (Raylib.IsKeyDown(KeyboardKey.Up)) center.Y += pan;
                if (Raylib.IsKeyDown(KeyboardKey.Down)) center.Y -= pan;
            }

            float wheel = Raylib.GetMouseWheelMove();
            if (wheel != 0)
            {
                metersPerPixel = Math.Clamp(metersPerPixel * MathF.Pow(0.85f, wheel), 0.02f, 200f);
            }

            var mouse = Raylib.GetMousePosition();
            var mouseWorld = ToWorld(mouse, center, metersPerPixel);

            if (Raylib.IsKeyPressed(KeyboardKey.J))
            {
                note = world.JoinFirstUnit() ? "join sent" : "no free unit offer";
            }

            if (Raylib.IsMouseButtonPressed(MouseButton.Right))
            {
                picked = Nearest(world, mouseWorld, 12f * metersPerPixel, now);
                note = picked == 0 ? "nothing under the cursor" : $"picked {picked}";
                if (gizmosOn)
                {
                    SendView(world, picked);
                }
            }

            if (Raylib.IsKeyPressed(KeyboardKey.G))
            {
                gizmosOn = !gizmosOn;
                SendView(world, gizmosOn ? picked : 0, gizmosOn);
                note = gizmosOn ? "gizmos on" : "gizmos off";
            }

            if (playing)
            {
                float forward = (Raylib.IsKeyDown(KeyboardKey.W) ? 1f : 0f) - (Raylib.IsKeyDown(KeyboardKey.S) ? 1f : 0f);
                float lateral = (Raylib.IsKeyDown(KeyboardKey.D) ? 1f : 0f) - (Raylib.IsKeyDown(KeyboardKey.A) ? 1f : 0f);
                world.SetMove(forward, lateral);
                world.SetAim(mouseWorld.X, mouseWorld.Y);
                world.SetTrigger(Raylib.IsMouseButtonDown(MouseButton.Left));
                if (Raylib.IsMouseButtonPressed(MouseButton.Left))
                {
                    world.PressFire();
                }

                if (Raylib.IsKeyPressed(KeyboardKey.C))
                {
                    world.CycleStance();
                }

                if (Raylib.IsKeyPressed(KeyboardKey.F))
                {
                    world.PressUse();
                }

                if (Raylib.IsKeyPressed(KeyboardKey.Escape))
                {
                    world.Leave();
                    note = "left";
                }

                // 30 Hz control frames while playing
                if (now >= nextControl)
                {
                    world.SendControl();
                    nextControl = now + 1.0 / 30.0;
                }
            }
            else if (wasPlaying)
            {
                world.SetMove(0f, 0f);
                world.SetTrigger(false);
            }

            wasPlaying = playing;

            Raylib.BeginDrawing();
            Raylib.ClearBackground(new Color(24, 28, 34, 255));
            DrawGrid(center, metersPerPixel);
            DrawGizmos(world, center, metersPerPixel);
            DrawRoundTwo(world, center, metersPerPixel, now);
            DrawEntities(world, center, metersPerPixel, now, picked);
            DrawHud(world, note, mouseWorld);
            DrawPlayerHud(world, now);
            Raylib.EndDrawing();

            if (!playing && Raylib.IsKeyPressed(KeyboardKey.Escape))
            {
                break;
            }
        }

        if (world.IsPlaying)
        {
            world.Leave();
        }

        Raylib.CloseWindow();
        return 0;
    }

    private static void SendView(DpsWorld world, int picked, bool on = true) =>
        world.SetGizmoView(on ? GizmoLayers : [], on && picked != 0 ? [picked] : []);

    private static Vector2 ToScreen(double x, double y, Vector2 center, float mpp) =>
        new(Raylib.GetScreenWidth() / 2f + ((float)x - center.X) / mpp, Raylib.GetScreenHeight() / 2f - ((float)y - center.Y) / mpp);

    private static Vector2 ToWorld(Vector2 screen, Vector2 center, float mpp) =>
        new(center.X + (screen.X - Raylib.GetScreenWidth() / 2f) * mpp, center.Y - (screen.Y - Raylib.GetScreenHeight() / 2f) * mpp);

    private static int Nearest(DpsWorld world, Vector2 at, float radius, double now)
    {
        int best = 0;
        float bestDistance = radius;
        foreach (var (id, e) in world.Entities)
        {
            if (e.State is null)
            {
                continue;
            }

            var p = e.PositionAt(now);
            float d = Vector2.Distance(at, new Vector2((float)p.X, (float)p.Y));
            if (d <= bestDistance)
            {
                bestDistance = d;
                best = id;
            }
        }

        return best;
    }

    private static Color SideColor(byte side) => side switch
    {
        1 => new Color(80, 140, 255, 255),
        2 => new Color(240, 90, 80, 255),
        3 => new Color(90, 210, 120, 255),
        _ => new Color(170, 170, 170, 255),
    };

    private static void DrawGrid(Vector2 center, float mpp)
    {
        float step = 100f;
        while (step / mpp < 40f)
        {
            step *= 10f;
        }

        int w = Raylib.GetScreenWidth(), h = Raylib.GetScreenHeight();
        var min = ToWorld(new Vector2(0, h), center, mpp);
        var max = ToWorld(new Vector2(w, 0), center, mpp);
        var line = new Color(44, 50, 60, 255);
        for (float x = MathF.Floor(min.X / step) * step; x <= max.X; x += step)
        {
            var s = ToScreen(x, 0, center, mpp);
            Raylib.DrawLine((int)s.X, 0, (int)s.X, h, line);
        }

        for (float y = MathF.Floor(min.Y / step) * step; y <= max.Y; y += step)
        {
            var s = ToScreen(0, y, center, mpp);
            Raylib.DrawLine(0, (int)s.Y, w, (int)s.Y, line);
        }

        Raylib.DrawText($"grid {step:0} m", 10, h - 22, 14, new Color(120, 130, 145, 255));
    }

    private static void DrawEntities(DpsWorld world, Vector2 center, float mpp, double now, int picked)
    {
        int ownId = world.IsPlaying && world.Slot is { } slot ? slot.UnitId : 0;
        foreach (var (id, e) in world.Entities)
        {
            if (e.State is null)
            {
                continue;
            }

            var p = e.PositionAt(now);
            var s = ToScreen(p.X, p.Y, center, mpp);
            byte side = e.Info?.Side ?? 0;
            var color = SideColor(side);
            var domain = e.Info?.Domain ?? D.Domain.DomainOther;
            switch (domain)
            {
                case D.Domain.DomainPerson:
                    Raylib.DrawCircleV(s, 4f, color);
                    break;
                case D.Domain.DomainAir:
                    Raylib.DrawPoly(s, 3, 7f, -90f, color);
                    break;
                case D.Domain.DomainSurface:
                case D.Domain.DomainSubsurface:
                    Raylib.DrawPoly(s, 5, 7f, 0f, color);
                    break;
                default:
                    Raylib.DrawRectangleV(new Vector2(s.X - 5, s.Y - 5), new Vector2(10, 10), color);
                    break;
            }

            // heading from the velocity
            var v = e.State.Value.LinearVelocity;
            if (v.X * v.X + v.Y * v.Y > 0.25f)
            {
                var dir = Vector2.Normalize(new Vector2(v.X, -v.Y));
                Raylib.DrawLineV(s, s + dir * 12f, color);
            }

            if (id == ownId)
            {
                Raylib.DrawCircleLines((int)s.X, (int)s.Y, 12f, Color.Yellow);
                Raylib.DrawCircleLines((int)s.X, (int)s.Y, 13f, Color.Yellow);
            }

            if (id == picked)
            {
                Raylib.DrawCircleLines((int)s.X, (int)s.Y, 10f, Color.White);
            }

            if (id == ownId || id == picked || mpp < 0.25f)
            {
                Raylib.DrawText(e.Info?.Name ?? id.ToString(), (int)s.X + 8, (int)s.Y - 16, 12, Color.White);
            }
        }
    }


    /// <summary>The DPS round 2 state on the map: ground marks, burning spots, flame jets, signal heads and the delayed ambient movers (simple shapes).</summary>
    private static void DrawRoundTwo(DpsWorld world, Vector2 center, float mpp, double now)
    {
        var corners = new Vector2[4];
        foreach (var marks in world.GroundMarks.Values)
        {
            foreach (var m in marks.Marks ?? [])
            {
                var color = m.Kind == D.HeatMarkKind.HeatMarkKindBurnedArea ? new Color(10, 10, 10, 200) : new Color(120, 80, 40, 90);
                double c = Math.Cos(m.Heading), s = Math.Sin(m.Heading), hl = m.Length / 2, hw = m.Width / 2;
                double[] fx = [hl, -hl, -hl, hl], fy = [hw, hw, -hw, -hw];
                for (int i = 0; i < 4; i++)
                {
                    corners[i] = ToScreen(m.Centre.X + fx[i] * c - fy[i] * s, m.Centre.Y + fx[i] * s + fy[i] * c, center, mpp);
                }

                for (int i = 0; i < 4; i++)
                {
                    Raylib.DrawLineV(corners[i], corners[(i + 1) % 4], color);
                }
            }
        }

        foreach (var spots in world.FireSpots.Values)
        {
            foreach (var spot in spots.Spots ?? [])
            {
                var p = ToScreen(spot.Position.X, spot.Position.Y, center, mpp);
                Raylib.DrawCircleV(p, Math.Max(3f, spot.Radius / mpp), new Color(255, 140, 30, 180));
            }
        }

        foreach (var jet in world.FlameJets.Values)
        {
            if (world.Entities.TryGetValue(jet.EntityId, out var shooter) && shooter.State is not null)
            {
                var from = shooter.PositionAt(now);
                Raylib.DrawLineEx(ToScreen(from.X, from.Y, center, mpp), ToScreen(jet.AimPoint.X, jet.AimPoint.Y, center, mpp), 3f, new Color(255, 170, 40, 220));
            }
        }

        foreach (var light in world.TrafficLights.Values)
        {
            var lamp = light.State switch
            {
                D.TrafficLightState.TrafficLightStateRed => new Color(225, 40, 40, 255),
                D.TrafficLightState.TrafficLightStateYellow => new Color(245, 205, 40, 255),
                D.TrafficLightState.TrafficLightStateGreen => new Color(40, 210, 70, 255),
                _ => new Color(90, 90, 90, 255),
            };
            Raylib.DrawCircleV(ToScreen(light.Position.X, light.Position.Y, center, mpp), 3f, lamp);
        }

        // ambient movers: where they were MoverPlayback.PlaybackDelay seconds ago, never extrapolated (DD-018 3.6c)
        foreach (var (info, pose) in world.Movers.Visible(now))
        {
            var p = ToScreen(pose.X, pose.Y, center, mpp);
            if (info.Kind == D.MoverKind.MoverKindPedestrian)
            {
                Raylib.DrawCircleV(p, 2.5f, new Color(230, 230, 120, 255));
                continue;
            }

            var body = Rgba(info.ColourRgba == 0 ? 0xA0A0A0FFu : info.ColourRgba);
            float length = (info.Length > 0 ? info.Length : 4.5f) / mpp, width = (info.Width > 0 ? info.Width : 1.8f) / mpp;
            var rect = new Rectangle(p.X, p.Y, Math.Max(length, 6f), Math.Max(width, 3f));
            Raylib.DrawRectanglePro(rect, new Vector2(rect.Width / 2, rect.Height / 2), -(float)(pose.HeadingRad * 180.0 / Math.PI), body);
        }
    }

    /// <summary>The text of the player HUD (DD-018 3.1): weapon line, use prompt and verdict, kill feed, the last message, the objectives and the exercise result.</summary>
    private static void DrawPlayerHud(DpsWorld world, double now)
    {
        int y = 70;
        var text = new Color(235, 235, 235, 255);
        if (world.IsPlaying)
        {
            foreach (string? line in new[] { world.WeaponLine(), world.UsePrompt(), world.UseVerdict() })
            {
                if (line is not null)
                {
                    Raylib.DrawText(line, 10, y, 16, Color.Yellow);
                    y += 20;
                }
            }

            if (world.Own is { MissileLaunch: true } own)
            {
                Raylib.DrawText($"MISSILE LAUNCH {own.MissileBearingDeg:0} deg  {own.MissileTimeToImpactS:0.#} s", 10, y, 16, Color.Red);
                y += 20;
            }
        }

        if (world.CurrentMessage(now) is { } message)
        {
            Raylib.DrawText(message.Text ?? string.Empty, Raylib.GetScreenWidth() / 2 - 200, Raylib.GetScreenHeight() - 60, 18, Color.White);
        }

        int ky = 74;
        foreach (string line in world.KillFeed)
        {
            Raylib.DrawText(line, Raylib.GetScreenWidth() - 360, ky, 14, new Color(255, 190, 170, 255));
            ky += 16;
        }

        foreach (var status in world.VisibleObjectives().Take(8))
        {
            Raylib.DrawText(DpsHud.StatusLine(status), Raylib.GetScreenWidth() - 360, ky + 10, 13, text);
            ky += 15;
        }

        if (world.Exercise is { Phase: D.ExercisePhase.ExercisePhaseEnded } ex)
        {
            Raylib.DrawText($"EXERCISE ENDED {ex.Reason}", Raylib.GetScreenWidth() / 2 - 120, 12, 20, Color.Orange);
        }
    }

    private static void DrawGizmos(DpsWorld world, Vector2 center, float mpp)
    {
        foreach (var frame in world.Gizmos.Values)
        {
            if (frame.Primitives is null)
            {
                continue;
            }

            foreach (var prim in frame.Primitives)
            {
                var color = Rgba(prim.ColourRgba);
                var pts = prim.Points ?? [];
                switch (prim.Shape)
                {
                    case D.GizmoShape.GizmoShapeLineStrip:
                    case D.GizmoShape.GizmoShapeArrow:
                        for (int i = 1; i < pts.Count; i++)
                        {
                            Raylib.DrawLineV(ToScreen(pts[i - 1].X, pts[i - 1].Y, center, mpp), ToScreen(pts[i].X, pts[i].Y, center, mpp), color);
                        }

                        if (prim.Shape == D.GizmoShape.GizmoShapeArrow && pts.Count >= 2)
                        {
                            var a = ToScreen(pts[^2].X, pts[^2].Y, center, mpp);
                            var b = ToScreen(pts[^1].X, pts[^1].Y, center, mpp);
                            var d = b - a;
                            if (d.LengthSquared() > 1f)
                            {
                                d = Vector2.Normalize(d) * 8f;
                                var n = new Vector2(-d.Y, d.X) * 0.5f;
                                Raylib.DrawLineV(b, b - d + n, color);
                                Raylib.DrawLineV(b, b - d - n, color);
                            }
                        }

                        break;
                    case D.GizmoShape.GizmoShapeCircle when pts.Count >= 1:
                        {
                            var c = ToScreen(pts[0].X, pts[0].Y, center, mpp);
                            Raylib.DrawCircleLines((int)c.X, (int)c.Y, Math.Max(2f, prim.Width / mpp), color);
                            break;
                        }

                    case D.GizmoShape.GizmoShapePolygon:
                        for (int i = 0; i < pts.Count; i++)
                        {
                            var next = pts[(i + 1) % pts.Count];
                            Raylib.DrawLineV(ToScreen(pts[i].X, pts[i].Y, center, mpp), ToScreen(next.X, next.Y, center, mpp), color);
                        }

                        break;
                    case D.GizmoShape.GizmoShapeBox when pts.Count >= 2:
                        {
                            // centre, then (extent x, extent y, angle in degrees)
                            double ex = pts[1].X / 2, ey = pts[1].Y / 2, a = pts[1].Z * Math.PI / 180.0;
                            double cos = Math.Cos(a), sin = Math.Sin(a);
                            Span<Vector2> corners = stackalloc Vector2[4];
                            double[] sx = [-ex, ex, ex, -ex], sy = [-ey, -ey, ey, ey];
                            for (int i = 0; i < 4; i++)
                            {
                                corners[i] = ToScreen(pts[0].X + sx[i] * cos - sy[i] * sin, pts[0].Y + sx[i] * sin + sy[i] * cos, center, mpp);
                            }

                            for (int i = 0; i < 4; i++)
                            {
                                Raylib.DrawLineV(corners[i], corners[(i + 1) % 4], color);
                            }

                            break;
                        }

                    case D.GizmoShape.GizmoShapeText when pts.Count >= 1:
                    case D.GizmoShape.GizmoShapeIcon when pts.Count >= 1:
                        {
                            var t = ToScreen(pts[0].X, pts[0].Y, center, mpp);
                            Raylib.DrawText(prim.Text ?? "*", (int)t.X, (int)t.Y, 12, color);
                            break;
                        }
                }

            }
        }
    }

    private static Color Rgba(uint rgba) => new((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);

    private static void DrawHud(DpsWorld world, string note, Vector2 mouseWorld)
    {
        string state = world.Slot is { } slot ? slot.State.ToString().Replace("SlotState", string.Empty) : "no slot yet";
        string reason = world.Slot is { Reason: { Length: > 0 } r } ? $" ({r})" : string.Empty;
        Raylib.DrawText($"{state}{reason}   entities {world.Entities.Count}   gizmo layers {world.Gizmos.Count}   {note}", 10, 8, 16, Color.White);
        Raylib.DrawText("J join  WASD move  mouse aim  LMB fire  C stance  F use  Esc leave/quit  RMB pick  G gizmos  wheel zoom", 10, 28, 14, new Color(150, 160, 175, 255));
        if (world.Own is { } own && world.IsPlaying)
        {
            string ammo = string.Empty;   // the weapon line below says it
            Raylib.DrawText($"health {own.Health:0}/{own.MaxHealth:0}{ammo}", 10, 48, 16, Color.Yellow);
        }

        if (world.Slot is { Offers: { Count: > 0 } offers } && !world.IsPlaying)
        {
            int y = 74;
            foreach (var offer in offers)
            {
                if (y > 74 + 15 * 12)
                {
                    break;
                }

                Raylib.DrawText($"{(offer.Kind == D.OfferKind.OfferKindUnit ? "unit" : "role")} {offer.Id} {offer.Name} {(offer.Free ? string.Empty : "(taken)")}", 10, y, 13, new Color(200, 205, 215, 255));
                y += 15;
            }
        }

        Raylib.DrawText($"{mouseWorld.X:0}, {mouseWorld.Y:0} m", Raylib.GetScreenWidth() - 140, 8, 14, new Color(150, 160, 175, 255));
    }
}
