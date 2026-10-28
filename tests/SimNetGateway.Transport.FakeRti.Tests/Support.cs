using SimNetGateway.Protocol.Rpr.Fom;
using SimNetGateway.Protocol.Rpr.Hla;
using SimNetGateway.Transport.FakeRti;

namespace SimNetGateway.Transport.FakeRti.Tests;

/// <summary>A callback as the federate saw it, with the handles and octets exactly as delivered.</summary>
public abstract record RtiEvent;

public sealed record NameReserved(string Name) : RtiEvent;

public sealed record NameReservationFailed(string Name) : RtiEvent;

public sealed record Discovered(ObjectInstanceHandle Object, ObjectClassHandle Class, string Name) : RtiEvent;

public sealed record Reflected(ObjectInstanceHandle Object, (AttributeHandle Attribute, byte[] Value)[] Values, byte[] Tag) : RtiEvent;

public sealed record Removed(ObjectInstanceHandle Object, byte[] Tag) : RtiEvent;

public sealed record Interaction(InteractionClassHandle Class, (ParameterHandle Parameter, byte[] Value)[] Values, byte[] Tag) : RtiEvent;

public sealed record Provide(ObjectInstanceHandle Object, AttributeHandle[] Attributes, byte[] Tag) : RtiEvent;

/// <summary>Records every callback; optionally runs a probe inside each callback (to check what the RTI allows there).</summary>
public sealed class Recorder : IFederateAmbassador
{
    public List<RtiEvent> Events { get; } = new();

    /// <summary>Runs inside every callback, before the event is recorded.</summary>
    public Action? InsideCallback { get; set; }

    public IEnumerable<T> Of<T>() where T : RtiEvent => Events.OfType<T>();

    public void ObjectInstanceNameReservationSucceeded(string objectName) => Record(new NameReserved(objectName));

    public void ObjectInstanceNameReservationFailed(string objectName) => Record(new NameReservationFailed(objectName));

    public void DiscoverObjectInstance(ObjectInstanceHandle theObject, ObjectClassHandle theObjectClass, string objectName) =>
        Record(new Discovered(theObject, theObjectClass, objectName));

    public void ReflectAttributeValues(ObjectInstanceHandle theObject, AttributeHandleValueMap theAttributes, byte[] userSuppliedTag) =>
        Record(new Reflected(theObject, theAttributes.Select(p => (p.Key, p.Value)).ToArray(), userSuppliedTag));

    public void RemoveObjectInstance(ObjectInstanceHandle theObject, byte[] userSuppliedTag) =>
        Record(new Removed(theObject, userSuppliedTag));

    public void ReceiveInteraction(InteractionClassHandle interactionClass, ParameterHandleValueMap theParameters, byte[] userSuppliedTag) =>
        Record(new Interaction(interactionClass, theParameters.Select(p => (p.Key, p.Value)).ToArray(), userSuppliedTag));

    public void ProvideAttributeValueUpdate(ObjectInstanceHandle theObject, AttributeHandleSet theAttributes, byte[] userSuppliedTag) =>
        Record(new Provide(theObject, theAttributes.ToArray(), userSuppliedTag));

    private void Record(RtiEvent e)
    {
        InsideCallback?.Invoke();
        Events.Add(e);
    }
}

/// <summary>A connected federate of a test federation with handle lookup helpers.</summary>
public sealed class Fed
{
    public Fed(FakeRtiAmbassador rti, Recorder recorder, string name)
    {
        Rti = rti;
        Recorder = recorder;
        Name = name;
    }

    public FakeRtiAmbassador Rti { get; }

    public Recorder Recorder { get; }

    public string Name { get; }

    public IReadOnlyList<RtiEvent> Events => Recorder.Events;

    public ObjectClassHandle Class(string fullName) => Rti.GetObjectClassHandle(fullName);

    public AttributeHandle Attr(string classFullName, string attribute) => Rti.GetAttributeHandle(Class(classFullName), attribute);

    public AttributeHandleSet Attrs(string classFullName, params string[] attributes) =>
        new(attributes.Select(a => Attr(classFullName, a)));

    public InteractionClassHandle Interaction(string fullName) => Rti.GetInteractionClassHandle(fullName);

    public ParameterHandle Param(string interaction, string parameter) => Rti.GetParameterHandle(Interaction(interaction), parameter);

    public void Publish(string classFullName, params string[] attributes) =>
        Rti.PublishObjectClassAttributes(Class(classFullName), Attrs(classFullName, attributes));

    public void Subscribe(string classFullName, params string[] attributes) =>
        Rti.SubscribeObjectClassAttributes(Class(classFullName), Attrs(classFullName, attributes));

    public ObjectInstanceHandle Register(string classFullName) => Rti.RegisterObjectInstance(Class(classFullName));

    public void Update(ObjectInstanceHandle obj, string classFullName, params (string Attribute, byte[] Value)[] values)
    {
        var map = new AttributeHandleValueMap();
        foreach (var (attribute, value) in values)
        {
            map.Set(Attr(classFullName, attribute), value);
        }
        Rti.UpdateAttributeValues(obj, map, Array.Empty<byte>());
    }

    /// <summary>Evokes everything queued; returns the number of events recorded by that.</summary>
    public int Evoke()
    {
        var before = Recorder.Events.Count;
        Rti.EvokeMultipleCallbacks(0, 0);
        return Recorder.Events.Count - before;
    }

    public void Clear() => Recorder.Events.Clear();
}

/// <summary>A hub with a created federation and helpers to add connected, joined federates.</summary>
public sealed class Rig
{
    public const string FederationName = "TestFederation";

    public Rig(params string[] modules) : this(null, modules)
    {
    }

    /// <summary>A rig whose hub is prepared by <paramref name="setup"/> (extra FOM modules) before the federation is created.</summary>
    public static Rig With(Action<FakeRtiHub> setup, params string[] modules) => new(setup, modules);

    private Rig(Action<FakeRtiHub>? setup, string[] modules)
    {
        Hub = new FakeRtiHub();
        setup?.Invoke(Hub);
        Admin = Hub.CreateAmbassador();
        Admin.Connect(new Recorder());
        Admin.CreateFederationExecution(FederationName, modules.Length == 0 ? [RprFom2.ModuleName] : modules);
    }

    public FakeRtiHub Hub { get; }

    /// <summary>A connected, not joined ambassador used to create and destroy federations.</summary>
    public FakeRtiAmbassador Admin { get; }

    public Fed Join(string name)
    {
        var rti = Hub.CreateAmbassador();
        var recorder = new Recorder();
        rti.Connect(recorder);
        rti.JoinFederationExecution(name, "test", FederationName);
        return new Fed(rti, recorder, name);
    }

    public Fed[] Join(int count) => Enumerable.Range(1, count).Select(i => Join("F" + i)).ToArray();
}

public static class Names
{
    public const string Physical = RprFom2.ObjectClasses.PhysicalEntity;
    public const string Platform = RprFom2.ObjectClasses.Platform;
    public const string Ground = RprFom2.ObjectClasses.GroundVehicle;
    public const string Aircraft = RprFom2.ObjectClasses.Aircraft;
    public const string Lifeform = RprFom2.ObjectClasses.Lifeform;
    public const string Human = RprFom2.ObjectClasses.Human;
    public const string Munition = RprFom2.ObjectClasses.Munition;
    public const string Aggregate = RprFom2.ObjectClasses.AggregateEntity;
    public const string BaseEntity = RprFom2.ObjectClasses.BaseEntity;
    public const string Fire = RprFom2.InteractionClasses.WeaponFire;
    public const string Detonation = RprFom2.InteractionClasses.MunitionDetonation;

    public static byte[] Bytes(params int[] values) => values.Select(v => (byte)v).ToArray();
}
