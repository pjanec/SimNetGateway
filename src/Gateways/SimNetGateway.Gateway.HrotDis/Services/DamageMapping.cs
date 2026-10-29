namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// Health &lt;-&gt; DIS damage state (DD-006 P-62, R-085, the "shared" configuration section). Damage: 0 none, 1 slight, 2
/// moderate, 3 destroyed. Health to damage by the fraction current / max: full health (&gt;= 1) is none, from
/// <c>damageThresholds.slight</c> up is slight, above <c>damageThresholds.moderate</c> moderate, else destroyed (a unit without
/// a maximum counts as undamaged). Damage to health (the reverse values): 100 % / 75 % / 40 % / 0 % of <c>entityMaxHealth</c>,
/// moved into the band of their level when the thresholds leave the preferred value outside it, so that
/// <c>DamageOf(HealthOf(d)) == d</c> holds for every damage level whose band is not empty and the values fall with the damage.
/// </summary>
public sealed class DamageMapping
{
    public const int None = 0;
    public const int Slight = 1;
    public const int Moderate = 2;
    public const int Destroyed = 3;

    private const double PreferredSlight = 0.75;
    private const double PreferredModerate = 0.40;

    private readonly double _slight;
    private readonly double _moderate;

    public DamageMapping(SharedConfiguration shared)
    {
        ArgumentNullException.ThrowIfNull(shared);
        MaxHealth = shared.EntityMaxHealth;
        _slight = shared.DamageThresholds.Slight;
        _moderate = shared.DamageThresholds.Moderate;
    }

    /// <summary>The health of an undamaged DIS entity.</summary>
    public double MaxHealth { get; }

    /// <summary>The damage state of a health value.</summary>
    public int DamageOf(double current, double max)
    {
        double fraction = max > 0.0 ? current / max : 1.0;
        if (fraction >= 1.0)
        {
            return None;
        }

        if (fraction >= _slight)
        {
            return Slight;
        }

        return fraction > _moderate ? Moderate : Destroyed;
    }

    /// <summary>The health fraction (0 .. 1) that stands for a damage state.</summary>
    public double FractionOf(int damage)
    {
        switch (damage)
        {
            case None:
                return 1.0;
            case Slight:
                return _slight < PreferredSlight ? PreferredSlight : (_slight + 1.0) / 2.0;
            case Moderate:
                return _moderate < PreferredModerate && PreferredModerate < _slight ? PreferredModerate : (_moderate + _slight) / 2.0;
            default:
                return 0.0;
        }
    }

    /// <summary>The current health that stands for a damage state, in units of <see cref="MaxHealth"/>.</summary>
    public double HealthOf(int damage) => FractionOf(damage) * MaxHealth;
}
