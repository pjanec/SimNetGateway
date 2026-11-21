namespace SimNetGateway.DpsClient;

/// <summary>
/// The key-to-axis arithmetic of the reference client's WASD movement (no Raylib here, so a test can rail it). The wire follows HROT: <c>ControlFrame.Forward</c> and
/// <c>Lateral</c> are body-relative, forward + = ahead, <b>lateral + = LEFT</b> (HROT <c>ManualMotion</c>, <c>EmbodiedKeys</c>: A = +1, D = -1). T-040.
/// </summary>
public static class DpsMoveKeys
{
    /// <summary>W / S: +1 forward, -1 back, 0 when both or neither are held.</summary>
    public static float Forward(bool w, bool s) => (w ? 1f : 0f) - (s ? 1f : 0f);

    /// <summary>A / D: A = +1 (left), D = -1 (right), 0 when both or neither are held.</summary>
    public static float Lateral(bool a, bool d) => (a ? 1f : 0f) - (d ? 1f : 0f);
}
