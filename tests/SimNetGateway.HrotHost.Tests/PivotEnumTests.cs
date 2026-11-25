using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>
/// T-080: HROT's enumerations reach the pivot as the pivot's ENUMS, mapped BY MEMBER NAME (never by number) through <see cref="PivotEnum{TPivot}"/>. Properties checked on every pair: a HROT member goes to the pivot member of
/// the same name (or the one named by a rename row: Entry to Entered, FixedKey to Fixed, Huge to Large ...), a member the pivot does not have goes to the pivot's unknown / none value (never to another member), every pivot member
/// except its own unknown / none values is the target of some HROT member (no dead mirror member), and a lookup allocates nothing.
/// </summary>
public class PivotEnumTests
{
    private static readonly (Type Hrot, Type Pivot)[] Pairs =
    [
        (typeof(Fdp.Toolkit.Tkb.Domain.WeaponCategory), typeof(HrotWeaponCategory)),
        (typeof(Fdp.Toolkit.Civilians.CivilianMode), typeof(HrotCivilianMode)),
        (typeof(Fdp.Toolkit.Tkb.Domain.SeatPose), typeof(HrotSeatPose)),
        (typeof(Hrot.Look.Effects.EffectSurface), typeof(HrotEffectSurface)),
        (typeof(Fdp.Toolkit.Tkb.Domain.EffectKind), typeof(HrotEffectKind)),
        (typeof(Hrot.Core.Tkb.EffectTkbCatalog.CalibreClass), typeof(HrotEffectSize)),
        (typeof(Fdp.Toolkit.Combat.Components.DestructionKind), typeof(HrotKillKind)),
        (typeof(Fdp.Toolkit.Tkb.Domain.MunitionShape), typeof(HrotMunitionShape)),
        (typeof(Fdp.Toolkit.Terrain.HeatMarkKind), typeof(HrotHeatMarkKind)),
        (typeof(Fdp.Toolkit.Terrain.HeatMarkEnd), typeof(HrotHeatMarkEnd)),
        (typeof(Fdp.Toolkit.Combat.InjuryState), typeof(HrotInjury)),
        (typeof(Fdp.Toolkit.Animals.AnimalMode), typeof(HrotAnimalMode)),
        (typeof(Fdp.Toolkit.Animals.FlockMode), typeof(HrotFlockMode)),
        (typeof(Fdp.Toolkit.Terrain.BreachKind), typeof(HrotBreachKind)),
        (typeof(Fdp.Toolkit.Terrain.BreachCause), typeof(HrotBreachCause)),
        (typeof(Fdp.Toolkit.Terrain.CraterKind), typeof(HrotCraterKind)),
        (typeof(Fdp.Toolkit.Combat.ImpactKind), typeof(HrotImpactKind)),
        (typeof(Hrot.Map.Common.Components.CameraSource), typeof(HrotViewSource)),
        (typeof(Hrot.Map.Common.Components.CameraMode), typeof(HrotViewMode)),
        (typeof(Fdp.Toolkit.ScenarioRules.ObjectiveRole), typeof(HrotObjectiveRole)),
        (typeof(Fdp.Toolkit.ScenarioRules.ObjectiveState), typeof(HrotObjectiveState)),
        (typeof(Fdp.Toolkit.ScenarioRules.TriggerState), typeof(HrotTriggerState)),
        (typeof(Fdp.Toolkit.ScenarioRules.ExercisePhase), typeof(HrotExercisePhase)),
        (typeof(Fdp.Toolkit.ScenarioRules.SideOutcome), typeof(HrotSideOutcome)),
        (typeof(Fdp.Toolkit.Behavior.Possession.UseVerbKind), typeof(HrotUseVerb)),
        (typeof(Fdp.Toolkit.Behavior.Possession.UseReason), typeof(HrotUseReason)),
        (typeof(Fdp.Toolkit.Behavior.Possession.UseOutcome), typeof(HrotUseOutcome)),
        (typeof(Fdp.Toolkit.Combat.OperatorFireMode), typeof(HrotFireMode)),
        (typeof(Fdp.Toolkit.Perception.Radar.RadarWarningLevel), typeof(HrotRadarWarningLevel)),
        (typeof(Fdp.Toolkit.Tkb.Domain.GuidanceKind), typeof(HrotMissileSeeker)),
    ];

    /// <summary>The same rows as <c>PivotEnums.Renames</c>, restated here independently so a change of one is noticed.</summary>
    private static readonly Dictionary<(string Pivot, string Hrot), string> Renames = new()
    {
        [(nameof(HrotEffectSize), "Huge")] = "Large",
        [(nameof(HrotImpactKind), "Entry")] = "Entered",
        [(nameof(HrotImpactKind), "Exit")] = "Left",
        [(nameof(HrotImpactKind), "Stop")] = "Stopped",
        [(nameof(HrotViewMode), "FixedKey")] = "Fixed",
        [(nameof(HrotEffectKind), "Decal")] = "GroundMark",
        [(nameof(HrotEffectKind), "Fire")] = "WreckFlame",
        [(nameof(HrotEffectKind), "Smoke")] = "WreckSmoke",
        [(nameof(HrotEffectKind), "Tracer")] = "Omitted",
        [(nameof(HrotEffectKind), "Flare")] = "Omitted",
        [(nameof(HrotEffectKind), "Chaff")] = "Omitted",
    };

    private static object Map(Type hrot, Type pivot, object value) =>
        typeof(PivotEnum<>).MakeGenericType(pivot).GetMethod("Of")!.MakeGenericMethod(hrot).Invoke(null, [value])!;

    [Fact]
    public void A_hrot_member_goes_to_the_pivot_member_of_its_name_and_an_unknown_one_to_the_unknown_value_never_to_another_member()
    {
        foreach (var (hrot, pivot) in Pairs)
        {
            var pivotNames = Enum.GetNames(pivot);
            string fallback = pivot == typeof(HrotKillKind) ? nameof(HrotKillKind.Unknown) : Enum.GetName(pivot, Enum.ToObject(pivot, 0))!;
            foreach (var value in Enum.GetValues(hrot))
            {
                string name = value.ToString()!;
                string want = Renames.TryGetValue((pivot.Name, name), out var renamed) ? renamed : name;
                string expected = pivotNames.Contains(want) ? want : fallback;
                Assert.True(expected == Map(hrot, pivot, value).ToString(), $"{hrot.Name}.{name} -> {pivot.Name}.{expected}");
            }
        }
    }

    [Fact]
    public void A_hrot_value_no_member_has_goes_to_the_unknown_value()
    {
        Assert.Equal(HrotKillKind.Unknown, PivotEnum<HrotKillKind>.Of((Fdp.Toolkit.Combat.Components.DestructionKind)200));
        Assert.Equal(HrotBreachCause.Unknown, PivotEnum<HrotBreachCause>.Of((Fdp.Toolkit.Terrain.BreachCause)99));
        Assert.Equal(HrotAnimalMode.Unknown, PivotEnum<HrotAnimalMode>.Of((Fdp.Toolkit.Animals.AnimalMode)250));
    }

    [Fact]
    public void Every_pivot_member_except_the_unknown_ones_is_reached_by_a_hrot_member()
    {
        foreach (var (hrot, pivot) in Pairs)
        {
            var reached = Enum.GetValues(hrot).Cast<object>().Select(v => Map(hrot, pivot, v).ToString()).ToHashSet();
            foreach (var name in Enum.GetNames(pivot))
            {
                if (Enum.Parse(pivot, name).Equals(Enum.ToObject(pivot, 0)) || name is "Unknown" or "None")
                {
                    continue;
                }

                Assert.True(reached.Contains(name), $"{pivot.Name}.{name} is never produced from {hrot.Name}");
            }
        }
    }

    [Fact]
    public void A_lookup_allocates_nothing()
    {
        var kind = Fdp.Toolkit.Combat.Components.DestructionKind.BurntOut;
        var mode = Fdp.Toolkit.Animals.AnimalMode.Alert;
        PivotEnum<HrotKillKind>.Of(kind);
        PivotEnum<HrotAnimalMode>.Of(mode);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            PivotEnum<HrotKillKind>.Of(kind);
            PivotEnum<HrotAnimalMode>.Of(mode);
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated == 0L, $"{allocated} B");
    }
}
