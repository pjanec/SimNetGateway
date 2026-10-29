using SimNetGateway.Common;
using SimNetGateway.Engine;

namespace SimNetGateway.Gateway.HrotDis;

/// <summary>A DIS entity identifier (IEEE 1278.1 Entity ID): site, application and entity number. The key of the <c>dis.entity</c> map (DD-006 section 3.2).</summary>
public readonly record struct DisEntityKey(ushort Site, ushort Application, ushort Number)
{
    public override string ToString() => $"{Site}:{Application}:{Number}";
}

/// <summary>
/// The pool of entity numbers the gateway hands out to the HROT entities it publishes to DIS: <see cref="First"/> .. <see cref="Last"/>
/// (the defaults 1 .. 65534 leave 0 "no entity" and 65535 "all entities" free) with a time quarantine for released numbers
/// (a receiver that still remembers the old entity must not confuse it with a new one, DD-006 section 3.2).
/// </summary>
public sealed record DisNumbering(int First = 1, int Last = 65534, double QuarantineSeconds = 60.0)
{
    public void Validate()
    {
        if (First < 1 || Last > 65534 || Last < First)
        {
            throw new ArgumentException($"The DIS entity number range {First} .. {Last} must lie within 1 .. 65534 and not be empty.");
        }

        if (QuarantineSeconds < 0.0 || double.IsNaN(QuarantineSeconds))
        {
            throw new ArgumentException("The DIS entity number quarantine must not be negative.");
        }
    }
}

/// <summary>What the <c>dis.entity</c> map needs: the clock of the quarantine, the gateway's own site and application, the numbering.</summary>
public sealed record DisIdentityOptions(IClock Clock, ushort Site, ushort Application, DisNumbering Numbering)
{
    /// <summary>The allocator of the map: a number pool with quarantine under the gateway's site and application.</summary>
    public MappedAllocator<DisEntityKey> CreateAllocator()
    {
        Numbering.Validate();
        ushort site = Site;
        ushort application = Application;
        return new MappedAllocator<DisEntityKey>(
            new NumberPool(Numbering.First, Numbering.Last, Clock, Numbering.QuarantineSeconds),
            number => new DisEntityKey(site, application, (ushort)number),
            key => key.Number);
    }
}
