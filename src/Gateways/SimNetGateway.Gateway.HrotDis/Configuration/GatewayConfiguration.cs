namespace SimNetGateway.Gateway.HrotDis;

// The runtime configuration of the HROT DIS gateway (R-094, DD-007 section 3a): a file on disk with the sections
// "shared", "hrot", "dis" and "hla". The defaults below are the ones of the DD-007 example; every key except
// dis.exerciseId and hla.federation may be left out.

/// <summary>Damage thresholds on the health fraction (current / max): from <see cref="Slight"/> up is slight damage, above <see cref="Moderate"/> moderate, else destroyed (full health is no damage).</summary>
public sealed record DamageThresholds
{
    public double Slight { get; init; } = 0.5;

    public double Moderate { get; init; } = 0.0;
}

/// <summary>Values every side uses (the "shared" section).</summary>
public sealed record SharedConfiguration
{
    /// <summary>DIS site / RPR federate id of the gateway's entity and event identifiers.</summary>
    public int Site { get; init; } = 1;

    /// <summary>DIS application of the gateway's entity and event identifiers.</summary>
    public int Application { get; init; } = 3001;

    /// <summary>The health that a DIS entity has when undamaged (HROT replicas of DIS entities).</summary>
    public double EntityMaxHealth { get; init; } = 100;

    public DamageThresholds DamageThresholds { get; init; } = new();

    /// <summary>The TKB type of a replica whose DIS type has no TKB counterpart.</summary>
    public long DefaultTkbForUnknown { get; init; }

    public bool ForwardUnitEntities { get; init; }

    public bool SendEventsInReplay { get; init; }
}

/// <summary>The pack's own values (the "hrot" section).</summary>
public sealed record HrotConfiguration
{
    /// <summary>How often the pack gathers HROT entities (Hz); read by the pack, the gateway only validates it.</summary>
    public double SampleRateHz { get; init; } = 20;

    /// <summary>The owner id of DIS replicas in HROT (a reserved id, not equal to any node id); read by the pack.</summary>
    public long ReplicaOwnerNodeId { get; init; } = 65000;

    /// <summary>True: an origin of exactly (0, 0, 0) is a real origin; false: it means "not set".</summary>
    public bool AllowZeroOrigin { get; init; }
}

/// <summary>The DIS side (the "dis" section).</summary>
public sealed record DisConfiguration
{
    /// <summary>The side runs when its section is present and this is not false.</summary>
    public bool Enabled { get; init; } = true;

    /// <summary>The DIS exercise id (0 .. 255); required.</summary>
    public required int ExerciseId { get; init; }

    public int Port { get; init; } = 3000;

    public string Address { get; init; } = "255.255.255.255";

    public double PositionThresholdM { get; init; } = 1.0;

    public double OrientationThresholdDeg { get; init; } = 3.0;

    public double HeartbeatS { get; init; } = 5;

    public double TimeoutS { get; init; } = 12;

    public bool HrotControlsDis { get; init; } = true;

    public bool DisControlsHrot { get; init; }
}

/// <summary>The HLA side (the "hla" section): the RPR modules of the gateway run on it (DD-009), the RTI binding is the host's.</summary>
public sealed record HlaConfiguration
{
    /// <summary>The side runs when its section is present and this is not false.</summary>
    public bool Enabled { get; init; } = true;

    public string Rti { get; init; } = "fake";

    /// <summary>The federation execution name; required.</summary>
    public required string Federation { get; init; }

    public string FederateName { get; init; } = "HROT-Gateway";

    public string FederateType { get; init; } = "SimNetGateway";

    public IReadOnlyList<string> FomModules { get; init; } = ["RPR-FOM-2.0"];

    public bool CreateFederation { get; init; } = true;

    /// <summary>The HLA side's own dead-reckoning position threshold, metres (DD-009 section 3 item 5; like <c>dis.positionThresholdM</c>, never read from the dis section).</summary>
    public double PositionThresholdM { get; init; } = 1.0;

    /// <summary>The HLA side's own orientation threshold, degrees.</summary>
    public double OrientationThresholdDeg { get; init; } = 3.0;

    public double HeartbeatS { get; init; } = 5;

    public double TimeoutS { get; init; } = 12;

    public bool Equals(HlaConfiguration? other) =>
        other is not null && Enabled == other.Enabled && Rti == other.Rti && Federation == other.Federation &&
        FederateName == other.FederateName && FederateType == other.FederateType &&
        FomModules.SequenceEqual(other.FomModules) && CreateFederation == other.CreateFederation &&
        PositionThresholdM.Equals(other.PositionThresholdM) && OrientationThresholdDeg.Equals(other.OrientationThresholdDeg) &&
        HeartbeatS.Equals(other.HeartbeatS) && TimeoutS.Equals(other.TimeoutS);

    public override int GetHashCode() => HashCode.Combine(Enabled, Rti, Federation, FederateName, FederateType, FomModules.Count);
}

/// <summary>
/// The whole configuration file: <see cref="Shared"/> and <see cref="Hrot"/> always exist (defaults), <see cref="Dis"/> and
/// <see cref="Hla"/> only when their section is present. Not to be confused with the engine's
/// <c>SimNetGateway.Engine.GatewayConfiguration</c> (a named profile with settings), which <see cref="HrotDisConfigurations"/> builds from this.
/// </summary>
public sealed record GatewayConfiguration(SharedConfiguration Shared, HrotConfiguration Hrot, DisConfiguration? Dis, HlaConfiguration? Hla)
{
    /// <summary>The DIS side runs: its section is present and not disabled.</summary>
    public bool DisRuns => Dis is { Enabled: true };

    /// <summary>The HLA side runs: its section is present and not disabled.</summary>
    public bool HlaRuns => Hla is { Enabled: true };

    /// <summary>False: no side runs and the gateway is not instantiated (DD-007 section 3a).</summary>
    public bool AnySideEnabled => DisRuns || HlaRuns;

    /// <summary>All defaults with a DIS section for the given exercise (for hosts and tests that configure in code).</summary>
    public static GatewayConfiguration CreateDefault(int disExerciseId = 1) =>
        new(new SharedConfiguration(), new HrotConfiguration(), new DisConfiguration { ExerciseId = disExerciseId }, null);
}
