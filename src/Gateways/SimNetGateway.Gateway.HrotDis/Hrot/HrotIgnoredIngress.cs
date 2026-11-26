using SimNetGateway.Engine;
using SimNetGateway.Protocol.Hrot;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>
/// The HROT messages this profile deliberately ignores: data for sides that draw (the DDS player's look, HUD, type and traffic data; DD-017, DD-018), which the DIS and
/// HLA sides have no use for. The engine counts a message nobody handles as unhandled (a diagnostic of a contract that outgrew the gateway), so every message of the
/// contract is either handled by a module of this profile or declared HERE; <see cref="Types"/> is the one list. A new message of <see cref="IHrotToGatewayMessage"/> that
/// is neither handled nor listed stays unhandled, and the profile tests fail until someone decides which it is.
/// </summary>
public sealed class HrotIgnoredIngress : IGatewayModule
{
    private static readonly (Type Type, Action<ModuleSetup> Register)[] Entries =
    [
        Ignored<HrotPersonDetail>(),
        Ignored<HrotClockAnchor>(),   // the simulation clock goes to the dps side (DD-022); DIS keeps its own
        Ignored<HrotStep>(),
        Ignored<HrotTerrain>(),   // the resident terrain goes to the dps side (Session.Terrain, T-048); DIS has no place for it
        Ignored<HrotEffect>(),
        Ignored<HrotPersonAppearance>(),
        Ignored<HrotVehicleLook>(),
        Ignored<HrotRound>(),
        Ignored<HrotMunitionLook>(),
        Ignored<HrotFlameJet>(),
        Ignored<HrotFlameJetRemoved>(),
        Ignored<HrotFireSpots>(),
        Ignored<HrotGroundMarks>(),
        Ignored<HrotManDowned>(),
        Ignored<HrotPlayerMessage>(),
        Ignored<HrotStatus>(),
        Ignored<HrotStatusRemoved>(),
        Ignored<HrotExercise>(),
        Ignored<HrotTypeRecord>(),
        Ignored<HrotAnimal>(),
        Ignored<HrotBuildingDamage>(),
        Ignored<HrotGroundDamage>(),
        Ignored<HrotCarried>(),
        Ignored<HrotCloud>(),
        Ignored<HrotCloudEnded>(),
        Ignored<HrotDeck>(),
        Ignored<HrotDeckEnded>(),
        Ignored<HrotMover>(),
        Ignored<HrotMoverRemoved>(),
        Ignored<HrotMoverTracks>(),
        Ignored<HrotTrafficLight>(),
        Ignored<HrotTrafficLightRemoved>(),
        Ignored<HrotView>(),
        Ignored<HrotImpact>(),
        Ignored<HrotEntityStatus>(),   // a client's card data goes to the dps side (DD-025); DIS has no place for it
        Ignored<HrotEntityStatusRemoved>(),
        Ignored<HrotUtterance>(),   // a voice line is a client's (DD-026); DIS has no place for it
        Ignored<HrotReport>(),
        Ignored<HrotOrder>(),
        Ignored<HrotAmbience>(),   // ambience and weather are a client's (DD-028); DIS has no place for them
        Ignored<HrotAmbienceRemoved>(),
        Ignored<HrotWeather>(),
        Ignored<HrotWeatherRemoved>(),
    ];

    /// <summary>The message types this profile ignores on purpose.</summary>
    public static IReadOnlyList<Type> Types { get; } = Entries.Select(e => e.Type).ToArray();

    public string Name => nameof(HrotIgnoredIngress);

    public void Register(ModuleSetup setup)
    {
        foreach (var entry in Entries)
        {
            entry.Register(setup);
        }
    }

    private static (Type Type, Action<ModuleSetup> Register) Ignored<T>()
        where T : IHrotToGatewayMessage => (typeof(T), setup => setup.OnMessage<T>(Ignore));

    private static void Ignore<T>(in T message, IngressContext ctx)
    {
    }
}
