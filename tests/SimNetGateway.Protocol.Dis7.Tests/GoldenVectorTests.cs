using DISnet;

namespace SimNetGateway.Protocol.Dis7.Tests;

/// <summary>
/// Golden byte vectors: the PDUs the gateway uses, encoded and compared with bytes written by hand from the layouts of
/// IEEE 1278.1-2012 (big-endian, sizes 144 / 96 / 104 / 28 / 44 / 40 / 32 / 40). Each test also decodes the golden bytes
/// again, so a wrong-endian or wrong-order marshaller fails in both directions.
/// </summary>
public class GoldenVectorTests
{
    private static void AssertGolden(Pdu pdu, byte[] expected)
    {
        var actual = PduCodec.Encode(pdu);
        Assert.Equal(expected.Length, actual.Length);
        Assert.Equal(Convert.ToHexString(expected), Convert.ToHexString(actual));
        Assert.Equal(expected.Length, pdu.Length);

        var decoded = PduCodec.Decode(expected);
        Assert.False(decoded.HasErrors, string.Join("; ", decoded.Items.Select(i => i.Error)));
        var item = Assert.Single(decoded.Items);
        Assert.Equal(PduDecodeStatus.Decoded, item.Status);
        Assert.Empty(DeepCompare.Differences(pdu, item.Pdu!));
    }

    [Fact]
    public void Entity_state_without_variable_parameters_is_144_bytes()
    {
        var pdu = new EntityStatePdu { ExerciseID = 3, Timestamp = 0x01234567 };
        pdu.EntityID.SimulationAddress.Site = 0x0102;
        pdu.EntityID.SimulationAddress.Application = 0x0304;
        pdu.EntityID.EntityNumber = 0x0506;
        pdu.ForceId = 1;
        pdu.EntityType.EntityKind = 1; pdu.EntityType.Domain = 1; pdu.EntityType.Country = 225;
        pdu.EntityType.Category = 1; pdu.EntityType.Subcategory = 2; pdu.EntityType.Specific = 3; pdu.EntityType.Extra = 4;
        pdu.AlternativeEntityType.EntityKind = 9;
        pdu.EntityLinearVelocity.X = 1.5f; pdu.EntityLinearVelocity.Y = -2.5f; pdu.EntityLinearVelocity.Z = 3.25f;
        pdu.EntityLocation.X = 3972024.5; pdu.EntityLocation.Y = 1022636.25; pdu.EntityLocation.Z = -4868336.125;
        pdu.EntityOrientation.Psi = 0.5f; pdu.EntityOrientation.Theta = -0.25f; pdu.EntityOrientation.Phi = 1.0f;
        pdu.EntityAppearance = 0xAABBCCDD;
        pdu.DeadReckoningParameters.DeadReckoningAlgorithm = 4;
        pdu.DeadReckoningParameters.EntityLinearAcceleration.X = 0.5f;
        pdu.DeadReckoningParameters.EntityAngularVelocity.Z = -0.125f;
        EntityMarkingText.Set(pdu.Marking, "ABC");
        pdu.Capabilities = 0x11223344;

        var expected = new ByteBuilder()
            .Header(exercise: 3, type: 1, family: 1, timestamp: 0x01234567, length: 144)
            .EntityId(0x0102, 0x0304, 0x0506)
            .U8(1).U8(0) // force id, number of variable parameters
            .U8(1).U8(1).U16(225).U8(1).U8(2).U8(3).U8(4) // entity type
            .U8(9).Zeros(7) // alternative entity type
            .F32(1.5f).F32(-2.5f).F32(3.25f) // linear velocity
            .F64(3972024.5).F64(1022636.25).F64(-4868336.125) // ECEF location
            .F32(0.5f).F32(-0.25f).F32(1.0f) // psi theta phi
            .U32(0xAABBCCDD)
            .U8(4).Zeros(15).F32(0.5f).F32(0).F32(0).F32(0).F32(0).F32(-0.125f) // dead reckoning (40 bytes)
            .U8(1).Raw((byte)'A', (byte)'B', (byte)'C').Zeros(8) // marking
            .U32(0x11223344)
            .ToArray();

        Assert.Equal(144, expected.Length);
        AssertGolden(pdu, expected);
    }

    [Fact]
    public void Each_variable_parameter_adds_16_bytes_after_the_144()
    {
        var pdu = new EntityStatePdu { ExerciseID = 1 };
        var part = new ArticulatedPart(ChangeIndicator: 2, PartAttachedTo: 0, ParameterType: 4107, Value: 1.25f);
        pdu.VariableParameters.Add(part.ToVariableParameter());
        pdu.VariableParameters.Add(part.ToVariableParameter());
        pdu.NumberOfVariableParameters = 2;

        var expected = new ByteBuilder()
            .Header(1, 1, 1, 0, 176)
            .Zeros(6).U8(0).U8(2) // entity id, force id, number of variable parameters
            .Zeros(16 + 12 + 24 + 12 + 4 + 40 + 12 + 4)
            .U8(0).U8(2).U16(0).U32(4107).F32(1.25f).Zeros(4) // articulated part: type 0, change, attached, parameter type, value
            .U8(0).U8(2).U16(0).U32(4107).F32(1.25f).Zeros(4)
            .ToArray();

        Assert.Equal(176, expected.Length);
        AssertGolden(pdu, expected);
    }

    [Fact]
    public void Fire_is_96_bytes()
    {
        var pdu = new FirePdu { ExerciseID = 5, Timestamp = 0x00000010 };
        pdu.FiringEntityID.SimulationAddress.Site = 1; pdu.FiringEntityID.SimulationAddress.Application = 2; pdu.FiringEntityID.EntityNumber = 3;
        pdu.TargetEntityID.SimulationAddress.Site = 4; pdu.TargetEntityID.SimulationAddress.Application = 5; pdu.TargetEntityID.EntityNumber = 6;
        pdu.MunitionExpendibleID.EntityNumber = 7;
        pdu.EventID.SimulationAddress.Site = 8; pdu.EventID.SimulationAddress.Application = 9; pdu.EventID.EventNumber = 10;
        pdu.FireMissionIndex = 0xDEADBEEF;
        pdu.LocationInWorldCoordinates.X = 100.5; pdu.LocationInWorldCoordinates.Y = -200.5; pdu.LocationInWorldCoordinates.Z = 300.5;
        pdu.Descriptor.MunitionType.EntityKind = 2; pdu.Descriptor.MunitionType.Country = 225;
        pdu.Descriptor.Warhead = 1000; pdu.Descriptor.Fuse = 2000; pdu.Descriptor.Quantity = 5; pdu.Descriptor.Rate = 600;
        pdu.Velocity.X = 10; pdu.Velocity.Y = 20; pdu.Velocity.Z = 30;
        pdu.Range = 1234.5f;

        var expected = new ByteBuilder()
            .Header(5, 2, 2, 0x10, 96)
            .EntityId(1, 2, 3).EntityId(4, 5, 6).EntityId(0, 0, 7).EntityId(8, 9, 10)
            .U32(0xDEADBEEF)
            .F64(100.5).F64(-200.5).F64(300.5)
            .U8(2).U8(0).U16(225).Zeros(4).U16(1000).U16(2000).U16(5).U16(600) // munition descriptor (16)
            .F32(10).F32(20).F32(30)
            .F32(1234.5f)
            .ToArray();

        Assert.Equal(96, expected.Length);
        AssertGolden(pdu, expected);
    }

    [Fact]
    public void Detonation_without_variable_parameters_is_104_bytes()
    {
        var pdu = new DetonationPdu { ExerciseID = 5 };
        pdu.FiringEntityID.EntityNumber = 1;
        pdu.TargetEntityID.EntityNumber = 2;
        pdu.ExplodingEntityID.EntityNumber = 3;
        pdu.EventID.EventNumber = 4;
        pdu.Velocity.X = 1; pdu.Velocity.Y = 2; pdu.Velocity.Z = 3;
        pdu.LocationInWorldCoordinates.X = -10.5; pdu.LocationInWorldCoordinates.Y = 20.5; pdu.LocationInWorldCoordinates.Z = -30.5;
        pdu.Descriptor.Warhead = 7;
        pdu.LocationOfEntityCoordinates.X = 0.5f; pdu.LocationOfEntityCoordinates.Y = 0.25f; pdu.LocationOfEntityCoordinates.Z = 0.125f;
        pdu.DetonationResult = 3;

        var expected = new ByteBuilder()
            .Header(5, 3, 2, 0, 104)
            .EntityId(0, 0, 1).EntityId(0, 0, 2).EntityId(0, 0, 3).EntityId(0, 0, 4)
            .F32(1).F32(2).F32(3)
            .F64(-10.5).F64(20.5).F64(-30.5)
            .Zeros(8).U16(7).Zeros(6) // munition descriptor
            .F32(0.5f).F32(0.25f).F32(0.125f)
            .U8(3).U8(0).U16(0) // detonation result, number of variable parameters, pad
            .ToArray();

        Assert.Equal(104, expected.Length);
        AssertGolden(pdu, expected);
    }

    [Fact]
    public void Create_entity_is_28_bytes()
    {
        var pdu = new CreateEntityPdu { ExerciseID = 2 };
        pdu.OriginatingEntityID.SimulationAddress.Site = 7;
        pdu.ReceivingEntityID.EntityNumber = 9;
        pdu.RequestID = 0x01020304;

        var expected = new ByteBuilder()
            .Header(2, 11, 5, 0, 28).EntityId(7, 0, 0).EntityId(0, 0, 9).U32(0x01020304)
            .ToArray();

        Assert.Equal(28, expected.Length);
        AssertGolden(pdu, expected);
    }

    [Fact]
    public void Remove_entity_is_28_bytes_and_the_alias_properties_write_the_inherited_ids()
    {
        var pdu = new RemoveEntityPdu { ExerciseID = 2 };
        pdu.OriginatingID.SimulationAddress.Site = 7; // alias of OriginatingEntityID
        pdu.ReceivingID.EntityNumber = 9;
        pdu.RequestID = 42;

        // A literal vector: header (version 7, exercise 2, type 12, family 5, timestamp 0, length 28, status 0, padding 0),
        // originating 0007:0000:0000, receiving 0000:0000:0009, request id 42.
        var expected = Convert.FromHexString("07020C0500000000001C0000" + "000700000000" + "000000000009" + "0000002A");
        Assert.Equal(28, expected.Length);
        AssertGolden(pdu, expected);
        Assert.Equal(7, pdu.OriginatingEntityID.SimulationAddress.Site);
        Assert.Equal(9, pdu.ReceivingEntityID.EntityNumber);
    }

    [Fact]
    public void Start_resume_is_44_bytes()
    {
        var pdu = new StartResumePdu { ExerciseID = 1 };
        pdu.OriginatingEntityID.EntityNumber = 1;
        pdu.ReceivingEntityID.EntityNumber = 2;
        pdu.RealWorldTime.Hour = 10; pdu.RealWorldTime.TimePastHour = 0x11111111;
        pdu.SimulationTime.Hour = 11; pdu.SimulationTime.TimePastHour = 0x22222222;
        pdu.RequestID = 77;

        var expected = new ByteBuilder()
            .Header(1, 13, 5, 0, 44).EntityId(0, 0, 1).EntityId(0, 0, 2)
            .U32(10).U32(0x11111111).U32(11).U32(0x22222222).U32(77)
            .ToArray();

        Assert.Equal(44, expected.Length);
        AssertGolden(pdu, expected);
    }

    [Fact]
    public void Stop_freeze_is_40_bytes()
    {
        var pdu = new StopFreezePdu { ExerciseID = 1 };
        pdu.OriginatingEntityID.EntityNumber = 1;
        pdu.ReceivingEntityID.EntityNumber = 2;
        pdu.RealWorldTime.Hour = 10; pdu.RealWorldTime.TimePastHour = 0x11111111;
        pdu.Reason = 4; pdu.FrozenBehavior = 3;
        pdu.RequestID = 78;

        var expected = new ByteBuilder()
            .Header(1, 14, 5, 0, 40).EntityId(0, 0, 1).EntityId(0, 0, 2)
            .U32(10).U32(0x11111111).U8(4).U8(3).U16(0).U32(78)
            .ToArray();

        Assert.Equal(40, expected.Length);
        AssertGolden(pdu, expected);
    }

    [Fact]
    public void Acknowledge_is_32_bytes()
    {
        var pdu = new AcknowledgePdu { ExerciseID = 1 };
        pdu.OriginatingEntityID.EntityNumber = 1;
        pdu.ReceivingEntityID.EntityNumber = 2;
        pdu.AcknowledgeFlag = 2; pdu.ResponseFlag = 1; pdu.RequestID = 79;

        var expected = new ByteBuilder()
            .Header(1, 15, 5, 0, 32).EntityId(0, 0, 1).EntityId(0, 0, 2).U16(2).U16(1).U32(79)
            .ToArray();

        Assert.Equal(32, expected.Length);
        AssertGolden(pdu, expected);
    }

    [Fact]
    public void Action_request_without_datums_is_40_bytes()
    {
        var pdu = new ActionRequestPdu { ExerciseID = 1 };
        pdu.OriginatingEntityID.EntityNumber = 1;
        pdu.ReceivingEntityID.EntityNumber = 2;
        pdu.RequestID = 80; pdu.ActionID = 5;

        var expected = new ByteBuilder()
            .Header(1, 16, 5, 0, 40).EntityId(0, 0, 1).EntityId(0, 0, 2)
            .U32(80).U32(5).U32(0).U32(0)
            .ToArray();

        Assert.Equal(40, expected.Length);
        AssertGolden(pdu, expected);
    }

    [Fact]
    public void Pdus_are_big_endian()
    {
        var pdu = new CreateEntityPdu { Timestamp = 0x01020304, RequestID = 0x0A0B0C0D };
        var bytes = PduCodec.Encode(pdu);
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, bytes[4..8]);
        Assert.Equal(new byte[] { 0, 28 }, bytes[8..10]);
        Assert.Equal(new byte[] { 0x0A, 0x0B, 0x0C, 0x0D }, bytes[24..28]);
    }
}
