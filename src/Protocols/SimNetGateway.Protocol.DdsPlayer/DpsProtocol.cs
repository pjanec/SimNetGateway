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
    public const string EntityStatus = "DPS_EntityStatus";
    public const string Articulation = "DPS_Articulation";
    public const string PersonState = "DPS_PersonState";
    public const string PersonAim = "DPS_PersonAim";
    public const string Appearance = "DPS_Appearance";
    public const string Fire = "DPS_Fire";
    public const string Detonation = "DPS_Detonation";
    public const string Effect = "DPS_Effect";
    public const string Impact = "DPS_Impact";
    public const string VehicleLook = "DPS_VehicleLook";
    public const string Round = "DPS_Round";
    public const string MunitionLook = "DPS_MunitionLook";
    public const string FlameJet = "DPS_FlameJet";
    public const string FireSpots = "DPS_FireSpots";
    public const string GroundMarks = "DPS_GroundMarks";
    public const string Animal = "DPS_Animal";
    public const string Mover = "DPS_Mover";
    public const string MoverTracks = "DPS_MoverTracks";
    public const string TrafficLights = "DPS_TrafficLights";
    public const string BuildingDamage = "DPS_BuildingDamage";
    public const string TkbType = "DPS_TkbType";
    public const string ManDowned = "DPS_ManDowned";
    public const string PlayerMessage = "DPS_PlayerMessage";
    public const string Status = "DPS_Status";
    public const string Exercise = "DPS_Exercise";
    public const string View = "DPS_View";
    public const string PlayerSlot = "DPS_PlayerSlot";
    public const string OwnUnit = "DPS_OwnUnit";
    public const string JoinRequest = "DPS_JoinRequest";
    public const string ControlFrame = "DPS_ControlFrame";
    public const string Leave = "DPS_Leave";
    public const string GizmoView = "DPS_GizmoView";
    public const string GizmoFrame = "DPS_GizmoFrame";
    public const string SimClock = "DPS_SimClock";
    public const string ClockProbe = "DPS_ClockProbe";
    public const string ClockEcho = "DPS_ClockEcho";
    public const string ClockStep = "DPS_ClockStep";
    public const string StepAck = "DPS_StepAck";
    public const string ClientWorld = "DPS_ClientWorld";
    public const string RenderClient = "DPS_RenderClient";
    public const string RenderJob = "DPS_RenderJob";
    public const string FrameRequest = "DPS_FrameRequest";
    public const string FrameDone = "DPS_FrameDone";
    public const string RenderJobResult = "DPS_RenderJobResult";
    public const string Utterance = "DPS_Utterance";
    public const string Report = "DPS_Report";
    public const string Order = "DPS_Order";
    public const string Ambience = "DPS_Ambience";
    public const string Weather = "DPS_Weather";

    /// <summary>Every topic name, in the order of DD-017 section 4.</summary>
    public static readonly IReadOnlyList<string> AllTopics =
    [
        Session, Entity, EntityState, EntityStatus, Articulation, PersonState, PersonAim, Appearance, Fire, Detonation, Effect, Impact, VehicleLook,
        Round, MunitionLook, FlameJet, FireSpots, GroundMarks,
        Animal, TkbType, BuildingDamage, Mover, MoverTracks, TrafficLights,
        ManDowned, PlayerMessage, Status, Exercise, View,
        PlayerSlot, OwnUnit, JoinRequest, ControlFrame, Leave, GizmoView, GizmoFrame,
        SimClock, ClockProbe, ClockEcho, ClockStep, StepAck, ClientWorld, RenderClient, RenderJob, FrameRequest, FrameDone, RenderJobResult,
        Utterance, Report, Order, Ambience, Weather,
    ];
}

/// <summary>How often a topic's samples change (DD-020 3.1); a field goes into the topic of its class.</summary>
public enum DpsRateClass
{
    /// <summary>Once per instance (or per type); never re-sent unless the thing itself changes.</summary>
    Fixed,

    /// <summary>On discrete events; no continuous field: a value that moves over time is sent as a start time + target + duration.</summary>
    Discrete,

    /// <summary>By threshold plus a heartbeat; the client extrapolates between the small samples.</summary>
    Continuous,

    /// <summary>As they happen; a reader keeps up to 1024 samples.</summary>
    Event,
}

/// <summary>States the <see cref="DpsRateClass"/> of a topic type.</summary>
[AttributeUsage(AttributeTargets.Struct)]
public sealed class DpsRateAttribute(DpsRateClass rateClass) : Attribute
{
    public DpsRateClass RateClass { get; } = rateClass;
}
