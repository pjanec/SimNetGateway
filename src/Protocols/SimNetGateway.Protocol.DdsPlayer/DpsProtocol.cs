namespace SimNetGateway.Protocol.DdsPlayer;

/// <summary>Version and topic names of the DDS Player Station protocol (DD-017).</summary>
public static class DpsProtocol
{
    /// <summary>A client refuses another major version; the minor grows with appended fields.</summary>
    public const int Major = 1, Minor = 0;

    /// <summary>The lease of the player topics' liveliness; losing it means the player left.</summary>
    public const int DefaultLeaseMs = 2000;

    public const string Session = "DPS_Session";
    public const string Entity = "DPS_Entity";
    public const string EntityState = "DPS_EntityState";
    public const string Articulation = "DPS_Articulation";
    public const string PersonState = "DPS_PersonState";
    public const string Appearance = "DPS_Appearance";
    public const string Fire = "DPS_Fire";
    public const string Detonation = "DPS_Detonation";
    public const string Effect = "DPS_Effect";
    public const string VehicleLook = "DPS_VehicleLook";
    public const string Round = "DPS_Round";
    public const string MunitionLook = "DPS_MunitionLook";
    public const string FlameJet = "DPS_FlameJet";
    public const string FireSpots = "DPS_FireSpots";
    public const string GroundMarks = "DPS_GroundMarks";
    public const string Animal = "DPS_Animal";
    public const string TkbType = "DPS_TkbType";
    public const string ManDowned = "DPS_ManDowned";
    public const string PlayerMessage = "DPS_PlayerMessage";
    public const string Status = "DPS_Status";
    public const string Exercise = "DPS_Exercise";
    public const string PlayerSlot = "DPS_PlayerSlot";
    public const string OwnUnit = "DPS_OwnUnit";
    public const string JoinRequest = "DPS_JoinRequest";
    public const string ControlFrame = "DPS_ControlFrame";
    public const string Leave = "DPS_Leave";
    public const string GizmoView = "DPS_GizmoView";
    public const string GizmoFrame = "DPS_GizmoFrame";

    /// <summary>Every topic name, in the order of DD-017 section 4.</summary>
    public static readonly IReadOnlyList<string> AllTopics =
    [
        Session, Entity, EntityState, Articulation, PersonState, Appearance, Fire, Detonation, Effect, VehicleLook,
        Round, MunitionLook, FlameJet, FireSpots, GroundMarks,
        Animal, TkbType,
        ManDowned, PlayerMessage, Status, Exercise,
        PlayerSlot, OwnUnit, JoinRequest, ControlFrame, Leave, GizmoView, GizmoFrame,
    ];
}
