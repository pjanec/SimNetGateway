using SimNetGateway.Engine;
using SimNetGateway.Engine.InMemory;

namespace SimNetGateway.Engine.Tests;

/// <summary>GatewayBuilder.UseWriteAuthority (DD-012 section 4a item 3): a profile installs the policy while it configures; never after a commit.</summary>
public class UseWriteAuthorityTests
{
    private record PolicySettings : GatewaySettings;

    private sealed class PolicyProfile(IWriteAuthorityPolicy policy) : IGatewayProfile
    {
        public string Name => "policy";

        public void Configure(GatewayBuilder builder) => builder.UseWriteAuthority(policy);
    }

    private static GatewayBuilder NewBuilder(IWriteAuthorityPolicy policy) =>
        new GatewayConfiguration("p", new PolicyProfile(policy), new PolicySettings()).CreateBuilder(new ManualClock());

    [Fact]
    public void A_policy_installed_during_configure_governs_the_commits()
    {
        var b = NewBuilder(new SingleWriterPolicy());
        Assert.IsType<SingleWriterPolicy>(b.Engine.State.Policy);
        var a = b.Sources.Register("a");
        var other = b.Sources.Register("other");
        var count = b.Attributes.Register("count", AttrKind.Int);
        var cs = b.Engine.BeginChange(a);
        var id = cs.CreateEntity();
        cs.Set(id, count, AttrValue.From(1L));
        Assert.True(cs.Commit().Succeeded);
        cs = b.Engine.BeginChange(other);
        cs.Set(id, count, AttrValue.From(2L));
        Assert.False(cs.Commit().Succeeded); // the owner is a, not other
    }

    [Fact]
    public void The_default_stays_permissive_without_a_call()
    {
        var b = new GatewayConfiguration("p", new PolicyProfile(PermissivePolicy.Instance), new PolicySettings()).CreateBuilder(new ManualClock());
        Assert.Same(PermissivePolicy.Instance, b.Engine.State.Policy);
    }

    [Fact]
    public void It_throws_after_the_first_commit()
    {
        var b = NewBuilder(PermissivePolicy.Instance);
        var cs = b.Engine.BeginChange(b.Sources.Register("a"));
        cs.CreateEntity();
        Assert.True(cs.Commit().Succeeded);
        Assert.Throws<InvalidOperationException>(() => b.UseWriteAuthority(new SingleWriterPolicy()));
        Assert.Same(PermissivePolicy.Instance, b.Engine.State.Policy);
    }

    [Fact]
    public void It_throws_after_build_and_rejects_null()
    {
        var b = NewBuilder(PermissivePolicy.Instance);
        Assert.Throws<ArgumentNullException>(() => b.UseWriteAuthority(null!));
        b.Build();
        Assert.Throws<InvalidOperationException>(() => b.UseWriteAuthority(new SingleWriterPolicy()));
    }

    [Fact]
    public void A_rejected_commit_does_not_count_as_a_commit()
    {
        var b = NewBuilder(new SingleWriterPolicy());
        var a = b.Sources.Register("a");
        var count = b.Attributes.Register("count", AttrKind.Int);
        var cs = b.Engine.BeginChange(a);
        var id = cs.CreateEntity();
        cs.Set(id, count, AttrValue.From(1L));
        cs.Commit();
        var bad = b.Engine.BeginChange(b.Sources.Register("x"));
        bad.Set(id, count, AttrValue.From(2L));
        Assert.False(bad.Commit().Succeeded);
        Assert.Throws<InvalidOperationException>(() => b.UseWriteAuthority(PermissivePolicy.Instance)); // the first commit already happened
    }
}
