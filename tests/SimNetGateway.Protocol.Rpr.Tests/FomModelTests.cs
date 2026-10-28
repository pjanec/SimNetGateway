using System.Reflection;
using SimNetGateway.Protocol.Rpr.Fom;
using SimNetGateway.Protocol.Rpr.Hla;

namespace SimNetGateway.Protocol.Rpr.Tests;

/// <summary>Success condition 4: the FOM model, its name constants, the class tree and merging.</summary>
public class FomModelTests
{
    private static readonly FomModel Rpr = RprFom2.Model();

    private static IEnumerable<(Type Holder, FieldInfo Field)> ConstantsOf(Func<Type, bool> holder) =>
        typeof(RprFom2).GetNestedTypes(BindingFlags.Public)
            .Where(holder)
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral).Select(f => (t, f)));

    [Fact]
    public void Every_object_class_constant_resolves_to_a_class_with_that_full_name()
    {
        var constants = ConstantsOf(t => t.Name == "ObjectClasses").ToList();

        Assert.NotEmpty(constants);
        foreach (var (_, field) in constants)
        {
            var name = (string)field.GetRawConstantValue()!;
            var cls = Rpr.FindObjectClass(name);
            Assert.NotNull(cls);
            Assert.Equal(name, cls!.FullName);
            Assert.StartsWith("HLAobjectRoot.", name);
            Assert.EndsWith("." + field.Name, name); // the constant is named after the class
        }
    }

    [Fact]
    public void Every_interaction_class_constant_resolves()
    {
        var constants = ConstantsOf(t => t.Name == "InteractionClasses").ToList();

        Assert.Equal(2, constants.Count);
        foreach (var (_, field) in constants)
        {
            var name = (string)field.GetRawConstantValue()!;
            Assert.NotNull(Rpr.FindInteractionClass(name));
            Assert.StartsWith("HLAinteractionRoot.", name);
        }
    }

    [Fact]
    public void Every_attribute_and_parameter_constant_is_declared_by_its_class()
    {
        var count = 0;
        foreach (var (holder, field) in ConstantsOf(t => t.Name.EndsWith("Attributes", StringComparison.Ordinal)))
        {
            var className = holder.Name[..^"Attributes".Length];
            var cls = Rpr.ObjectClasses.Single(c => c.Name == className);
            var attribute = cls.DeclaredAttributes.SingleOrDefault(a => a.Name == (string)field.GetRawConstantValue()!);
            Assert.True(attribute is not null, $"{holder.Name}.{field.Name} is not declared by {cls.FullName}");
            count++;
        }
        foreach (var (holder, field) in ConstantsOf(t => t.Name.EndsWith("Parameters", StringComparison.Ordinal)))
        {
            var className = holder.Name[..^"Parameters".Length];
            var cls = Rpr.InteractionClasses.Single(c => c.Name == className);
            Assert.NotNull(cls.DeclaredParameters.SingleOrDefault(p => p.Name == (string)field.GetRawConstantValue()!));
            count++;
        }
        Assert.True(count >= 5 + 25 + 16 + 5 + 1 + 12 + 13 + 14, "constants exist for all attributes and parameters of the subset");
    }

    [Fact]
    public void Every_declared_attribute_and_parameter_has_a_constant()
    {
        var attributeConstants = ConstantsOf(t => t.Name.EndsWith("Attributes", StringComparison.Ordinal))
            .Select(c => (c.Holder.Name[..^"Attributes".Length], (string)c.Field.GetRawConstantValue()!)).ToHashSet();
        foreach (var cls in Rpr.ObjectClasses.Skip(1))
        {
            foreach (var attribute in cls.DeclaredAttributes)
            {
                Assert.Contains((cls.Name, attribute.Name), attributeConstants);
            }
        }
        var parameterConstants = ConstantsOf(t => t.Name.EndsWith("Parameters", StringComparison.Ordinal))
            .Select(c => (c.Holder.Name[..^"Parameters".Length], (string)c.Field.GetRawConstantValue()!)).ToHashSet();
        foreach (var cls in Rpr.InteractionClasses.Skip(1))
        {
            foreach (var parameter in cls.DeclaredParameters)
            {
                Assert.Contains((cls.Name, parameter.Name), parameterConstants);
            }
        }
    }

    [Fact]
    public void Class_tree_parent_links_and_inherited_attributes_are_correct()
    {
        var ground = Rpr.FindObjectClass(RprFom2.ObjectClasses.GroundVehicle)!;
        var platform = Rpr.FindObjectClass(RprFom2.ObjectClasses.Platform)!;
        var physical = Rpr.FindObjectClass(RprFom2.ObjectClasses.PhysicalEntity)!;
        var baseEntity = Rpr.FindObjectClass(RprFom2.ObjectClasses.BaseEntity)!;

        Assert.Same(platform, ground.Parent);
        Assert.Same(physical, platform.Parent);
        Assert.Same(baseEntity, physical.Parent);
        Assert.Same(Rpr.ObjectRoot, baseEntity.Parent);
        Assert.Null(Rpr.ObjectRoot.Parent);
        Assert.Equal("HLAobjectRoot", Rpr.ObjectRoot.FullName);
        Assert.Equal(
            new[] { "GroundVehicle", "Platform", "PhysicalEntity", "BaseEntity", "HLAobjectRoot" },
            ground.SelfAndAncestors.Select(c => c.Name));
        Assert.Contains(ground, platform.Children);
        Assert.Equal(7, platform.Children.Count);
        Assert.Equal(new[] { "Human", "NonHuman" }, Rpr.FindObjectClass(RprFom2.ObjectClasses.Lifeform)!.Children.Select(c => c.Name));

        // a subclass declares none of its own and inherits everything, root first
        Assert.Empty(ground.DeclaredAttributes);
        Assert.Equal(
            Rpr.ObjectRoot.DeclaredAttributes.Concat(baseEntity.DeclaredAttributes).Concat(physical.DeclaredAttributes).Concat(platform.DeclaredAttributes),
            ground.AllAttributes);
        Assert.Equal(1 + 5 + 25 + 16, ground.AllAttributes.Count);
        Assert.Same(physical, ground.FindAttribute("Marking")!.DeclaringClass);
        Assert.Same(baseEntity, ground.FindAttribute("Spatial")!.DeclaringClass);
        Assert.Null(ground.FindAttribute("StanceCode")); // Lifeform only
        Assert.NotNull(Rpr.FindObjectClass(RprFom2.ObjectClasses.Human)!.FindAttribute("StanceCode"));
        Assert.True(ground.IsSameOrSubclassOf(physical));
        Assert.False(physical.IsSameOrSubclassOf(ground));
        Assert.True(ground.IsSameOrSubclassOf(ground));
    }

    [Fact]
    public void The_root_carries_the_delete_privilege_attribute_and_every_class_inherits_it()
    {
        Assert.Equal(FomModel.PrivilegeToDeleteObjectName, Rpr.ObjectRoot.DeclaredAttributes.First().Name);
        Assert.All(Rpr.ObjectClasses, c => Assert.NotNull(c.FindAttribute(FomModel.PrivilegeToDeleteObjectName)));
    }

    [Fact]
    public void AggregateEntity_is_a_BaseEntity_with_the_attributes_of_the_aggregate_module()
    {
        var aggregate = Rpr.FindObjectClass(RprFom2.ObjectClasses.AggregateEntity)!;

        Assert.Same(Rpr.FindObjectClass(RprFom2.ObjectClasses.BaseEntity), aggregate.Parent);
        Assert.Equal(
            new[]
            {
                "AggregateMarking", "AggregateState", "Dimensions", "EntityIdentifiers", "ForceIdentifier", "Formation",
                "NumberOfSilentEntities", "NumberOfVariableDatums", "SilentAggregates", "SilentEntities", "SubAggregateIdentifiers",
                "VariableDatums",
            },
            aggregate.DeclaredAttributes.Select(a => a.Name));
        Assert.NotNull(aggregate.FindAttribute("EntityType")); // from BaseEntity
    }

    [Fact]
    public void Interactions_are_children_of_the_interaction_root_with_the_parameters_of_the_warfare_module()
    {
        var fire = Rpr.FindInteractionClass(RprFom2.InteractionClasses.WeaponFire)!;
        var detonation = Rpr.FindInteractionClass(RprFom2.InteractionClasses.MunitionDetonation)!;

        Assert.Same(Rpr.InteractionRoot, fire.Parent);
        Assert.Equal(13, fire.AllParameters.Count);
        Assert.Equal(14, detonation.AllParameters.Count);
        Assert.Equal("EventIdentifierStruct", fire.FindParameter("EventIdentifier")!.DataType);
        Assert.Equal("RTIobjectId", fire.FindParameter("FiringObjectIdentifier")!.DataType);
        Assert.Equal("DetonationResultCodeEnum8", detonation.FindParameter("DetonationResultCode")!.DataType);
    }

    [Fact]
    public void Data_type_names_of_the_used_attributes_are_the_FOM_names()
    {
        var spatial = Rpr.FindObjectClass(RprFom2.ObjectClasses.GroundVehicle)!;
        Assert.Equal("SpatialVariantStruct", spatial.FindAttribute("Spatial")!.DataType);
        Assert.Equal("MarkingStruct", spatial.FindAttribute("Marking")!.DataType);
        Assert.Equal("ForceIdentifierEnum8", spatial.FindAttribute("ForceIdentifier")!.DataType);
        Assert.Equal("DamageStatusEnum32", spatial.FindAttribute("DamageState")!.DataType);
        Assert.Equal("EntityTypeStruct", spatial.FindAttribute("EntityType")!.DataType);
        Assert.Equal("EntityIdentifierStruct", spatial.FindAttribute("EntityIdentifier")!.DataType);
        Assert.Equal("ArticulatedParameterStructLengthlessArray", spatial.FindAttribute("ArticulatedParametersArray")!.DataType);
    }

    [Fact]
    public void Model_is_one_immutable_instance_and_registered_as_RPR_FOM_2_0()
    {
        Assert.Same(RprFom2.Model(), RprFom2.Model());
        Assert.Same(RprFom2.Model(), FomModules.Get("RPR-FOM-2.0"));
        Assert.Contains("RPR-FOM-2.0", FomModules.Names);
        Assert.Throws<NameNotFound>(() => FomModules.Get("no-such-module"));
        Assert.False(FomModules.TryGet("no-such-module", out _));
        Assert.Same(Rpr.FindObjectClass(RprFom2.ObjectClasses.Platform), Rpr.ObjectClasses.Single(c => c.FullName == RprFom2.ObjectClasses.Platform));
    }

    [Fact]
    public void Merging_a_model_with_itself_or_a_subset_gives_the_same_model()
    {
        var empty = new FomModelBuilder().Build();
        var partial = new FomModelBuilder()
            .ObjectClass("HLAobjectRoot.BaseEntity", ("EntityType", "EntityTypeStruct"))
            .Build();

        Assert.Equal(Rpr, Rpr.Merge(Rpr));
        Assert.Equal(Rpr, Rpr.Merge(partial));
        Assert.Equal(Rpr, empty.Merge(Rpr));
        Assert.Equal(Rpr.ToCanonicalString(), Rpr.Merge(Rpr).ToCanonicalString());
        Assert.Equal(empty, empty.Merge(empty));
        Assert.NotEqual(Rpr, empty);
    }

    [Fact]
    public void Merging_modules_unions_classes_and_members()
    {
        var a = new FomModelBuilder().ObjectClass("HLAobjectRoot.X", ("P", "T1")).InteractionClass("HLAinteractionRoot.I", ("Q", "T2")).Build();
        var b = new FomModelBuilder().ObjectClass("HLAobjectRoot.X", ("R", "T3")).ObjectClass("HLAobjectRoot.X.Y").Build();

        var merged = a.Merge(b);

        Assert.Equal(new[] { "P", "R" }, merged.FindObjectClass("HLAobjectRoot.X")!.DeclaredAttributes.Select(x => x.Name));
        Assert.NotNull(merged.FindObjectClass("HLAobjectRoot.X.Y"));
        Assert.NotNull(merged.FindInteractionClass("HLAinteractionRoot.I"));
    }

    [Fact]
    public void Merging_conflicting_definitions_is_InconsistentFdd()
    {
        var a = new FomModelBuilder().ObjectClass("HLAobjectRoot.X", ("P", "T1")).Build();
        var conflicting = new FomModelBuilder().ObjectClass("HLAobjectRoot.X", ("P", "T2")).Build();
        var interactionA = new FomModelBuilder().InteractionClass("HLAinteractionRoot.I", ("Q", "T1")).Build();
        var interactionB = new FomModelBuilder().InteractionClass("HLAinteractionRoot.I", ("Q", "T2")).Build();

        Assert.Throws<InconsistentFdd>(() => a.Merge(conflicting));
        Assert.Throws<InconsistentFdd>(() => conflicting.Merge(a));
        Assert.Throws<InconsistentFdd>(() => interactionA.Merge(interactionB));
        Assert.Throws<InconsistentFdd>(() => Rpr.Merge(new FomModelBuilder().ObjectClass(
            RprFom2.ObjectClasses.BaseEntity, ("EntityType", "SomethingElse")).Build()));
        Assert.Throws<InconsistentFdd>(() => new FomModelBuilder()
            .ObjectClass("HLAobjectRoot", (FomModel.PrivilegeToDeleteObjectName, "NotAToken")));
    }

    [Fact]
    public void Builder_rejects_names_outside_the_roots()
    {
        Assert.Throws<ArgumentException>(() => new FomModelBuilder().ObjectClass("BaseEntity"));
        Assert.Throws<ArgumentException>(() => new FomModelBuilder().ObjectClass("HLAobjectRoot..X"));
        Assert.Throws<ArgumentException>(() => new FomModelBuilder().InteractionClass("HLAobjectRoot.X"));
    }

    [Fact]
    public void Class_order_is_parents_before_children_in_declaration_order()
    {
        var model = new FomModelBuilder()
            .ObjectClass("HLAobjectRoot.B.C")
            .ObjectClass("HLAobjectRoot.A")
            .ObjectClass("HLAobjectRoot.B", ("x", "t"))
            .Build();

        Assert.Equal(new[] { "HLAobjectRoot", "HLAobjectRoot.B", "HLAobjectRoot.B.C", "HLAobjectRoot.A" }, model.ObjectClasses.Select(c => c.FullName));
    }
}
