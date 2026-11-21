using System.Numerics;
using Fdp.Core;
using Fdp.Toolkit.Behavior.Components;
using Fdp.Toolkit.Behavior.Possession;
using Fdp.Toolkit.Combat.Components;
using Fdp.Toolkit.Replication.Components;
using Hrot.Common;
using Hrot.Map.Common.Components;
using SimNetGateway.Gateway.HrotDis;
using SimNetGateway.Protocol.Hrot;
using HG = Hrot.Gameplay;

namespace SimNetGateway.HrotHost.Tests;

/// <summary>
/// T-053 (DD-023 D-3, D-4, D-13, D-14; HROT <c>DESIGN_Player_Join.md</c> section 12): the seats deploy through HROT's deploy server. The harness world composes HROT's real
/// <c>DeployProcessorSystem</c> + <c>ServerPlayerSlotReaderSystem</c> over the server end of <c>LocalGameplayHub.Of(world)</c> (what the editor's <c>GameplayServerPack</c> does) and HROT's real
/// <c>PossessionSystem</c> (ticked only while sim time advances, as its <c>TickPolicy.WhenTimeAdvances</c>); every seat's <see cref="DeployFlowPlayerFlow"/> is the one the host chose. The
/// invariants checked after every pumped step (the seats are independent): no unit is held by a station other than the one the seat's slot names, and a seat's UnitId is held by that seat.
/// </summary>
public class PlayerStationDeployTests
{
    private const int RoleId = 900, AreaA = 910, AreaB = 911, PlacedId = 2001;

    private static int SeatStation(int n) => HrotPlayerStation.StationOf(PackHarness.LocalNodeId, n);

    private sealed class Deploy : PackHarness
    {
        private long _ms = 1_000;
        private int _nextMan = 5000;
        private readonly bool _server;
        private readonly bool _deferred;

        public readonly List<(long Tkb, SimTransform At, int NetId)> Spawns = new();
        public readonly List<int> Mens = new();
        public HG.DeployProcessorSystem? Processor;
        private HG.ServerPlayerSlotReaderSystem? _slots;
        private readonly PossessionSystem _possession = new("OperatorControl");

        /// <param name="server">false = a node with no deploy server (the editor of a build that does not compose it).</param>
        /// <param name="deferSpawn">true = the man exists only when <see cref="Materialise"/> is called (the creation path is asynchronous in HROT).</param>
        public Deploy(int seats = 3, bool server = true, bool deferSpawn = false)
            : base(seats: seats)
        {
            _server = server;
            _deferred = deferSpawn;
            Register<Playable>();
            Register<DeployRole>();
            Register<SpawnArea>();
            Register<BehaviorState>();
            Register<PossessedBy>();
            Register<OperatorInput>();
            Register<GameplaySettings>();
            World.RegisterManagedComponent<EditablePolyline>();

            if (!World.Bus.IsRegistered<Fdp.Toolkit.Behavior.Events.ClearBehaviorEvent>())
            {
                World.Bus.Register<Fdp.Toolkit.Behavior.Events.ClearBehaviorEvent>();
            }

            World.RegisterManagedEvent<Fdp.Toolkit.Behavior.Events.AssignBehaviorEvent>();

            if (server)
            {
                var end = HG.LocalGameplayHub.Of(World).ServerEnd();
                Processor = new HG.DeployProcessorSystem(end, Map, LocalNodeId, election: null, nowMs: () => _ms, spawner: Spawn, rng: new Random(7));
                _slots = new HG.ServerPlayerSlotReaderSystem(end);
            }

            foreach (var seat in System.PlayerSeats)
            {
                seat.NowMs = () => _ms;
            }
        }

        private void Register<T>()
            where T : unmanaged
        {
            if (!World.IsComponentTypeRegistered<T>())
            {
                World.RegisterComponent<T>();
            }
        }

        public HrotPlayerStation Seat(int n) => System.PlayerSeats[n - 1];

        public long WallMs => _ms;

        public void AdvanceMs(long ms) => _ms += ms;

        protected override void AfterSystem(float dt)
        {
            _ms += 17;
            Processor?.Execute(World, dt);
            _slots?.Execute(World, dt);
            if (Advancing)
            {
                _possession.Execute(World, dt);   // HROT's PossessionSystem is TickPolicy.WhenTimeAdvances
            }
        }

        private int Spawn(long tkb, SimTransform at, IReadOnlyList<object> comps)
        {
            int id = _nextMan++;
            Spawns.Add((tkb, at, id));
            if (!_deferred)
            {
                MakeMan(id, at, comps);
            }

            return id;
        }

        /// <summary>The creation path finished: the man with the pre-allocated id now exists (what the editor's EntityCreation does a while after the request).</summary>
        public void Materialise(int call = 0)
        {
            var (_, at, id) = Spawns[call];
            MakeMan(id, at, [new EntityInfo { Name = new FixedString64("man"), ForceId = ForceId.Friend }]);
        }

        private void MakeMan(int id, SimTransform at, IReadOnlyList<object> comps)
        {
            var e = AddHrotEntity(id, at.Position, tkb: 300);
            World.SetComponent(e, at);
            World.AddComponent(e, new BehaviorState());
            World.SetAuthority<BehaviorState>(e, true);
            Mens.Add(id);
        }

        public Entity Man(int netId) => Map.TryGetEntity(netId, out var e) ? e : default;

        public Entity AddRole(int netId, ForceId side, string name, byte max, long tkb = 300, long kit1 = 0, string kit1Name = "", long kit2 = 0, string kit2Name = "")
        {
            var e = World.CreateEntity();
            World.AddComponent(e, new NetworkIdentity { Value = netId });
            World.AddComponent(e, new DeployRole
            {
                Side = side, PersonTkbType = (TkbTypeId)tkb, Name = new FixedString32(name), MaxCount = max,
                Kit1Tkb = (TkbTypeId)kit1, Kit1Name = new FixedString32(kit1Name), Kit2Tkb = (TkbTypeId)kit2, Kit2Name = new FixedString32(kit2Name),
            });
            Map.Register(netId, e);
            return e;
        }

        public Entity AddArea(int netId, ForceId side, Vector3 origin, params Vector2[] shape)
        {
            var e = World.CreateEntity();
            World.AddComponent(e, new NetworkIdentity { Value = netId });
            World.AddComponent(e, new EntityInfo { Name = new FixedString64("Area " + netId), ForceId = side });
            World.AddComponent(e, new SimTransform { Position = origin, Rotation = Quaternion.Identity });
            World.AddComponent(e, new SpawnArea { Side = side, FacingDeg = 90f });
            World.SetManagedComponent(e, new EditablePolyline { Points = shape });
            Map.Register(netId, e);
            return e;
        }

        public Entity AddPlaced(int netId, ForceId side, string slot)
        {
            var e = AddHrotEntity(netId, new Vector3(1, 2, 0), tkb: 300);
            World.AddComponent(e, new Playable { Side = side, SlotName = new FixedString32(slot) });
            World.AddComponent(e, new BehaviorState());
            World.SetAuthority<BehaviorState>(e, true);
            return e;
        }

        public static Vector2[] Square(float half) => [new(-half, -half), new(half, -half), new(half, half), new(-half, half)];

        /// <summary>Steps until <paramref name="done"/>; the invariants of independent seats are checked after every step.</summary>
        public void Run(Func<bool> done, string what, int maxSteps = 900)
        {
            for (int i = 0; i < maxSteps; i++)
            {
                if (done())
                {
                    return;
                }

                Step();
                CheckInvariants();
            }

            Assert.True(done(), "timed out: " + what + " :: " + string.Join(" | ", System.PlayerSeats.Select(s => $"{s.Station}:{s.Slot.State}/{s.Slot.UnitId}/'{s.Slot.Reason}'")));
        }

        public void RunSteps(int n)
        {
            for (int i = 0; i < n; i++)
            {
                Step();
                CheckInvariants();
            }
        }

        /// <summary>A seat's slot unit is held by that seat while Playing; no unit is held by two stations (a component holds one); a holder is a seat of this node.</summary>
        private void CheckInvariants()
        {
            foreach (var seat in System.PlayerSeats)
            {
                if (seat.Slot.State == PlayerSlotState.Playing)
                {
                    Assert.True(Map.TryGetEntity(seat.Slot.UnitId, out var man), "a Playing seat names a unit that exists");
                    if (PossessionSystem.Alive(World, man))   // a killed man is released by HROT one step before the slot says Dead
                    {
                        Assert.Equal(seat.Station, PossessionSystem.HolderOf(World, man));
                    }
                }
            }
        }

        public int Holder(int netId) => Map.TryGetEntity(netId, out var e) ? PossessionSystem.HolderOf(World, e) ?? 0 : 0;

        /// <summary>The server republishes its offers every second of wall time: let that pass, then show the seats' flows what it wrote.</summary>
        public void Republish()
        {
            AdvanceMs(1_100);
            RunSteps(2);
        }
    }

    // ---- (a) two roles of one side ---------------------------------------------------------------------------------------------------

    /// <summary>
    /// (a) Two seats deploy as two roles of one side: both reach Playing, each holds its own man (<c>PossessedBy</c> = its station), the role's count is respected (a third request past Max is refused with
    /// HROT's reason in the slot's Reason), and the slot's side is the role's DIS force. 🔴 Mutations: the flow built with another seat's station; Kit / Area dropped from the join (see below).
    /// </summary>
    [Fact]
    public void Two_seats_deploy_as_two_roles_of_one_side_each_holding_its_own_man_and_a_third_request_is_refused_past_the_max()
    {
        using var h = new Deploy(seats: 3);
        h.AddRole(RoleId, ForceId.Friend, "Rifleman", max: 2);
        h.AddArea(AreaA, ForceId.Friend, new Vector3(100, 200, 5), Deploy.Square(20));
        h.RunSteps(3);
        Assert.All(new[] { 1, 2, 3 }, n => Assert.IsType<DeployFlowPlayerFlow>(h.Seat(n).Flow));
        h.Run(() => h.Seat(1).Offers.Any(o => o.Kind == PlayerOfferKind.Role), "the role is offered");

        Assert.True(h.Seat(1).Join(new PlayerJoin(PlayerJoinKind.Role, RoleId, null, Side: 1)));
        Assert.True(h.Seat(2).Join(new PlayerJoin(PlayerJoinKind.Role, 0, "Rifleman", Side: 1)));   // a role by NAME is the same role
        h.Run(() => h.Seat(1).Slot.State == PlayerSlotState.Playing && h.Seat(2).Slot.State == PlayerSlotState.Playing, "both seats are playing");

        int u1 = h.Seat(1).Slot.UnitId, u2 = h.Seat(2).Slot.UnitId;
        Assert.NotEqual(u1, u2);
        Assert.NotEqual(RoleId, u1);
        Assert.Equal(h.Seat(1).Station, h.Holder(u1));
        Assert.Equal(h.Seat(2).Station, h.Holder(u2));
        Assert.Equal(2, h.Mens.Count);
        Assert.Equal((byte)1, h.Seat(1).Slot.Side);

        h.Republish();
        var role = h.Seat(3).Offers.Single(o => o.Kind == PlayerOfferKind.Role);
        Assert.Equal((2, 2, false), (role.Count, role.Max, role.Free));   // full: not free to take

        Assert.True(h.Seat(3).Join(new PlayerJoin(PlayerJoinKind.Role, RoleId, null, Side: 1)));
        h.Run(() => h.Seat(3).Slot.State == PlayerSlotState.Choosing && h.Seat(3).Slot.Reason == "RoleFull", "the third request is refused");
        Assert.Equal(0, h.Seat(3).Slot.UnitId);
        Assert.Equal(PlayerSlotState.Playing, h.Seat(1).Slot.State);   // the others are unaffected
        Assert.Equal(PlayerSlotState.Playing, h.Seat(2).Slot.State);
        Assert.Equal(2, h.Mens.Count);
    }

    /// <summary>The join's Kit and AreaId reach the server: the man is made from the kit's type and stands inside the chosen area, not the other one. 🔴 Mutation: Kit / Area dropped from the join.</summary>
    [Fact]
    public void The_join_carries_the_kit_and_the_area_to_the_server()
    {
        using var h = new Deploy(seats: 1);
        h.AddRole(RoleId, ForceId.Friend, "Rifleman", max: 0, tkb: 300, kit1: 301, kit1Name: "Medic");
        h.AddArea(AreaA, ForceId.Friend, new Vector3(100, 200, 5), Deploy.Square(10));
        h.AddArea(AreaB, ForceId.Friend, new Vector3(-500, -500, 5), Deploy.Square(10));
        h.Run(() => h.Seat(1).Offers.Any(o => o.Kind == PlayerOfferKind.Role), "offers");

        Assert.True(h.Seat(1).Join(new PlayerJoin(PlayerJoinKind.Role, RoleId, null, Side: 1, Kit: 1, AreaId: AreaB)));
        h.Run(() => h.Seat(1).Slot.State == PlayerSlotState.Playing, "playing");

        var (tkb, at, _) = h.Spawns.Single();
        Assert.Equal(301L, tkb);                                          // kit 1 = the Medic type
        Assert.InRange(at.Position.X, -510f, -490f);                       // inside area B
        Assert.InRange(at.Position.Y, -510f, -490f);
    }

    // ---- (b) a unit another seat holds -----------------------------------------------------------------------------------------------

    /// <summary>(b) A seat requests a placed unit another seat holds: refused with HROT's <c>HeldByAnotherStation</c>, naming the holder; the holder is unaffected.</summary>
    [Fact]
    public void A_placed_unit_another_seat_holds_is_refused_and_the_holder_is_unaffected()
    {
        using var h = new Deploy(seats: 2);
        h.AddPlaced(PlacedId, ForceId.Friend, "Rifleman");
        h.Run(() => h.Seat(1).Offers.Count == 1, "offers");

        Assert.True(h.Seat(1).Join(new PlayerJoin(PlayerJoinKind.Unit, PlacedId, null)));
        h.Run(() => h.Seat(1).Slot.State == PlayerSlotState.Playing, "seat 1 plays");
        h.Republish();
        Assert.False(h.Seat(2).Offers.Single().Free);   // the offer shows its holder

        Assert.True(h.Seat(2).Join(new PlayerJoin(PlayerJoinKind.Unit, PlacedId, null)));
        h.Run(() => h.Seat(2).Slot.State == PlayerSlotState.Choosing && h.Seat(2).Slot.Reason is { Length: > 0 }, "seat 2 is refused");

        Assert.StartsWith("HeldByAnotherStation", h.Seat(2).Slot.Reason);
        Assert.Contains($"station {h.Seat(1).Station}", h.Seat(2).Slot.Reason);
        Assert.Equal(0, h.Seat(2).Slot.UnitId);
        Assert.Equal(PlayerSlotState.Playing, h.Seat(1).Slot.State);
        Assert.Equal(h.Seat(1).Station, h.Holder(PlacedId));
    }

    // ---- (c) death, respawn, leave ---------------------------------------------------------------------------------------------------

    /// <summary>(c) The man dies: Dead with RespawnAtSim, then Choosing again after the delay; Leave frees the seat, the server's slot and the man's station (HROT's ReleaseStation).</summary>
    [Fact]
    public void Death_is_Dead_with_a_respawn_time_then_Choosing_and_Leave_frees_the_seat_and_the_man()
    {
        using var h = new Deploy(seats: 2);
        h.AddRole(RoleId, ForceId.Friend, "Rifleman", max: 0);
        h.AddArea(AreaA, ForceId.Friend, new Vector3(0, 0, 0), Deploy.Square(20));
        h.Run(() => h.Seat(1).Offers.Any(o => o.Kind == PlayerOfferKind.Role), "offers");
        h.Seat(1).Join(new PlayerJoin(PlayerJoinKind.Role, RoleId, null, Side: 1));
        h.Seat(2).Join(new PlayerJoin(PlayerJoinKind.Role, RoleId, null, Side: 1));
        h.Run(() => h.Seat(1).Slot.State == PlayerSlotState.Playing && h.Seat(2).Slot.State == PlayerSlotState.Playing, "both play");

        int dying = h.Seat(1).Slot.UnitId;
        double died = h.SimTime;
        h.World.SetComponent(h.Man(dying), new Health { Current = 0f, Max = 100f });
        h.Run(() => h.Seat(1).Slot.State == PlayerSlotState.Dead, "seat 1 is dead");
        Assert.Equal(dying, h.Seat(1).Slot.UnitId);
        Assert.InRange(h.Seat(1).Slot.RespawnAt, died + 9.0, died + 11.0);   // HROT's default delay, 10 s of sim time
        Assert.Equal(PlayerSlotState.Playing, h.Seat(2).Slot.State);          // the other seat is unaffected

        h.Run(() => h.Seat(1).Slot.State == PlayerSlotState.Choosing, "seat 1 chooses again after the delay", maxSteps: 1200);
        Assert.Equal(0, h.Seat(1).Slot.UnitId);
        Assert.Equal(0.0, h.Seat(1).Slot.RespawnAt);

        // leave: seat 2 gives its man up
        int man2 = h.Seat(2).Slot.UnitId;
        h.Seat(2).Leave();
        h.Run(() => h.Holder(man2) == 0 && h.Seat(2).Slot.State == PlayerSlotState.Choosing, "seat 2 left and its man is free");
        Assert.Equal(0, h.Seat(2).Slot.UnitId);
        h.RunSteps(3);
        Assert.False(h.Processor!.TryGetSlot(h.Seat(2).Station, out _, out _));   // the server's slot is gone too
    }

    // ---- (d) a paused clock ----------------------------------------------------------------------------------------------------------

    /// <summary>
    /// (d) A paused clock: HROT's PossessionSystem runs only while sim time advances, so a request made while paused stays TakingControl. Measured: HROT's flow counts its 10 s in the wall time it is given; the gateway
    /// gives it the wall time in which sim time ADVANCES, so 20 s of pause end in no refusal (Reason stays empty) and the seat is Playing as soon as the clock runs.
    /// </summary>
    [Fact]
    public void A_request_made_while_the_clock_is_paused_waits_without_a_timeout_refusal_and_completes_when_time_runs()
    {
        using var h = new Deploy(seats: 1);
        h.AddRole(RoleId, ForceId.Friend, "Rifleman", max: 0);
        h.AddArea(AreaA, ForceId.Friend, new Vector3(0, 0, 0), Deploy.Square(20));
        h.Run(() => h.Seat(1).Offers.Any(o => o.Kind == PlayerOfferKind.Role), "offers");

        h.Advancing = false;
        Assert.True(h.Seat(1).Join(new PlayerJoin(PlayerJoinKind.Role, RoleId, null, Side: 1)));
        h.Run(() => h.Seat(1).Slot.State == PlayerSlotState.TakingControl, "the request is waiting for control");
        long wall = h.WallMs;
        h.RunSteps(1_200);   // 20 s of wall time while paused (HROT's own deadline is 10 s)
        Assert.True(h.WallMs - wall >= 20_000);
        Assert.Equal(PlayerSlotState.TakingControl, h.Seat(1).Slot.State);
        Assert.True(string.IsNullOrEmpty(h.Seat(1).Slot.Reason), h.Seat(1).Slot.Reason);   // truthful: no refusal, nothing timed out

        h.Advancing = true;
        h.Run(() => h.Seat(1).Slot.State == PlayerSlotState.Playing, "playing once time runs");
        Assert.Equal(h.Seat(1).Station, h.Holder(h.Seat(1).Slot.UnitId));
    }

    /// <summary>
    /// The same wait while the man's creation is still on its way: Requesting, no refusal. ⚠ Measured: the SERVER's own deadline for the man to exist is 10 s of WALL time (<c>DeployProcessorSystem.SpawnTimeoutMs</c>), not
    /// sim time, so a creation that needs sim ticks and is paused longer than that is refused BY THE SERVER - and the seat reports HROT's text truthfully (Choosing, Reason); it is not a gateway timeout.
    /// </summary>
    [Fact]
    public void A_request_whose_man_is_not_created_yet_waits_in_Requesting_and_the_servers_own_wall_deadline_is_reported_as_it_is()
    {
        using var h = new Deploy(seats: 1, deferSpawn: true);
        h.AddRole(RoleId, ForceId.Friend, "Rifleman", max: 0);
        h.AddArea(AreaA, ForceId.Friend, new Vector3(0, 0, 0), Deploy.Square(20));
        h.Run(() => h.Seat(1).Offers.Any(o => o.Kind == PlayerOfferKind.Role), "offers");

        h.Advancing = false;
        Assert.True(h.Seat(1).Join(new PlayerJoin(PlayerJoinKind.Role, RoleId, null, Side: 1)));
        h.RunSteps(300);   // 5 s of wall time paused
        Assert.True(PlayerSlotState.Requesting == h.Seat(1).Slot.State, $"{h.Seat(1).Slot.State} '{h.Seat(1).Slot.Reason}'");
        Assert.True(string.IsNullOrEmpty(h.Seat(1).Slot.Reason));

        h.Advancing = true;
        h.Materialise();
        h.Run(() => h.Seat(1).Slot.State == PlayerSlotState.Playing, "playing once the man exists");

        // the server's wall deadline: the man never appears
        h.Seat(1).Leave();
        h.Run(() => h.Seat(1).Slot.State == PlayerSlotState.Choosing, "left");
        Assert.True(h.Seat(1).Join(new PlayerJoin(PlayerJoinKind.Role, RoleId, null, Side: 1)));
        h.Run(() => h.Seat(1).Slot.State == PlayerSlotState.Choosing && h.Seat(1).Slot.Reason is { Length: > 0 }, "the server gives up on the man", maxSteps: 1500);
        Assert.Contains(h.Seat(1).Slot.Reason, new[] { HG.DeployProcessorSystem.SpawnTimedOut, HG.DeployFlowSystem.NoAnswer });   // the server's 10 s and the flow's 10 s race; each is HROT's own text
    }

    /// <summary>
    /// The measurement behind the deviation from D-14 (<see cref="DeployFlowPlayerFlow.PauseAsWaiting"/>): with <c>OperatingLive</c> = "live AND sim time advances" a pause drops the seat's flow to Waiting, it FORGETS the request in
    /// flight, and after the resume the flow is Choosing while the server still holds the seat's slot - the seat and the server disagree. Pinned so the choice is not made again without the facts.
    /// </summary>
    [Fact]
    public void Measured_literal_D14_a_pause_forgets_the_request_in_flight_and_leaves_the_server_slot_behind()
    {
        using var h = new Deploy(seats: 1);
        h.AddRole(RoleId, ForceId.Friend, "Rifleman", max: 0);
        h.AddArea(AreaA, ForceId.Friend, new Vector3(0, 0, 0), Deploy.Square(20));
        h.RunSteps(2);
        ((DeployFlowPlayerFlow)h.Seat(1).Flow).PauseAsWaiting = true;
        h.Run(() => h.Seat(1).Offers.Any(o => o.Kind == PlayerOfferKind.Role), "offers");

        Assert.True(h.Seat(1).Join(new PlayerJoin(PlayerJoinKind.Role, RoleId, null, Side: 1)));
        h.Advancing = false;
        h.Run(() => h.Seat(1).Slot.State == PlayerSlotState.Waiting, "the pause sends the flow to Waiting");
        Assert.True(h.Processor!.TryGetSlot(h.Seat(1).Station, out _, out _));   // the server kept the slot of the request

        h.Advancing = true;
        h.Run(() => h.Seat(1).Slot.State == PlayerSlotState.Choosing, "after the resume the flow chooses again");
        Assert.True(h.Processor.TryGetSlot(h.Seat(1).Station, out _, out _));   // ... while the server still has the slot: a disagreement the deadline clock avoids
    }

    // ---- (e) offers ------------------------------------------------------------------------------------------------------------------

    /// <summary>(e) Kind / Count / Max / Free / Kits / Outline of the seam's offers match HROT's offers; an area's outline is its position plus its relative points, in world metres.</summary>
    [Fact]
    public void Offers_carry_kind_count_max_free_kits_and_an_areas_outline_points()
    {
        using var h = new Deploy(seats: 1);
        h.AddRole(RoleId, ForceId.Friend, "Rifleman", max: 3, kit1: 301, kit1Name: "Medic", kit2: 302, kit2Name: "Sniper");
        h.AddArea(AreaA, ForceId.Friend, new Vector3(100, 200, 5), new Vector2(-10, -10), new Vector2(10, -10), new Vector2(0, 15));
        h.AddPlaced(PlacedId, ForceId.Hostile, "Gunner");
        h.Run(() => h.Seat(1).Offers.Count == 3, "three offers");

        var offers = h.Seat(1).Offers;
        var hrot = ((DeployFlowPlayerFlow)h.Seat(1).Flow).Flow!.Offers;
        Assert.Equal(hrot.Count, offers.Count);
        for (int i = 0; i < hrot.Count; i++)
        {
            Assert.Equal(hrot[i].UnitNetId, offers[i].UnitId);   // the same order, the same ids
        }

        var role = offers.Single(o => o.Kind == PlayerOfferKind.Role);
        Assert.Equal((RoleId, "Rifleman", 1, 0, 3, true), (role.UnitId, role.Role, role.Force, role.Count, role.Max, role.Free));
        Assert.Equal(["Rifleman", "Medic", "Sniper"], role.Kits!.Take(3));   // index = kit number; 0 is the role's own
        var placed = offers.Single(o => o.Kind == PlayerOfferKind.Unit);
        Assert.Equal((PlacedId, "Gunner", 2, true, 0), (placed.UnitId, placed.Role, placed.Force, placed.Free, placed.Max));
        Assert.Null(placed.Outline);
        var area = offers.Single(o => o.Kind == PlayerOfferKind.Area);
        Assert.Equal(AreaA, area.UnitId);
        Assert.Equal(1, area.Force);
        Assert.Equal([new HrotVec3(90, 190, 5), new HrotVec3(110, 190, 5), new HrotVec3(100, 215, 5)], area.Outline);   // never empty for an area

        // a placed unit held by the seat shows as not free
        Assert.True(h.Seat(1).Join(new PlayerJoin(PlayerJoinKind.Unit, PlacedId, null, Side: 2)));
        h.Run(() => h.Seat(1).Slot.State == PlayerSlotState.Playing, "playing");
        h.Republish();
        Assert.False(h.Seat(1).Offers.Single(o => o.Kind == PlayerOfferKind.Unit).Free);
    }

    /// <summary>An area outline of more than 32 points is decimated evenly to 32 (the wire's bound): the first point is kept and the order is kept.</summary>
    [Fact]
    public void An_outline_of_more_than_32_points_is_decimated_evenly()
    {
        using var h = new Deploy(seats: 1);
        var shape = Enumerable.Range(0, 64).Select(i => new Vector2(MathF.Cos(i * MathF.Tau / 64f) * 50f, MathF.Sin(i * MathF.Tau / 64f) * 50f)).ToArray();
        h.AddArea(AreaA, ForceId.Friend, new Vector3(0, 0, 0), shape);
        h.Run(() => h.Seat(1).Offers.Any(o => o.Kind == PlayerOfferKind.Area), "the area is offered");

        var outline = h.Seat(1).Offers.Single().Outline!;
        Assert.Equal(DeployFlowPlayerFlow.MaxOutlinePoints, outline.Count);
        Assert.Equal(shape[0].X, outline[0].X, 3);
        Assert.Equal(shape[2].X, outline[1].X, 3);   // every second point of the 64
        Assert.Equal(1, ((DeployFlowPlayerFlow)h.Seat(1).Flow).DecimatedOutlines);
    }

    // ---- (f) no deploy server --------------------------------------------------------------------------------------------------------

    /// <summary>(f) A node with no deploy server keeps today's placed-unit possession for every seat; the decision is logged once per seat. 🔴 Mutation: the deploy flow chosen with no server.</summary>
    [Fact]
    public void Without_a_server_end_every_seat_uses_the_possession_flow()
    {
        using var h = new Deploy(seats: 2, server: false);
        h.AddPlaced(PlacedId, ForceId.Friend, "Rifleman");
        h.RunSteps(5);

        Assert.All(new[] { 1, 2 }, n => Assert.IsType<PossessionFlow>(h.Seat(n).Flow));
        Assert.Equal(PlayerSlotState.Choosing, h.Seat(1).Slot.State);
        Assert.Single(h.Seat(1).Offers);
        Assert.Equal(PlayerOfferKind.Unit, h.Seat(1).Offers[0].Kind);
        Assert.Equal(2, h.Log.Count(l => l.Contains("possession of placed playable units")));   // one line per seat, once
        h.RunSteps(30);
        Assert.Equal(2, h.Log.Count(l => l.Contains("possession of placed playable units")));
        Assert.DoesNotContain(h.Log, l => l.Contains("deploy flow"));
    }

    /// <summary>With the server end open the decision is the deploy flow, logged once per seat, and survives frames.</summary>
    [Fact]
    public void With_a_server_end_every_seat_uses_the_deploy_flow_logged_once()
    {
        using var h = new Deploy(seats: 2);
        h.RunSteps(30);
        Assert.All(new[] { 1, 2 }, n => Assert.IsType<DeployFlowPlayerFlow>(h.Seat(n).Flow));
        Assert.Equal(2, h.Log.Count(l => l.Contains("the deploy flow of HROT's deploy server")));
        Assert.True(DeployServer.Exists(h.World));
    }

    /// <summary>The deploy server composed by HROT's own pack over the hub's server end is what the host's probe finds.</summary>
    [Fact]
    public void The_probe_finds_the_server_end_a_gameplay_pack_composes()
    {
        using var h = new Deploy(seats: 1, server: false);
        Assert.False(DeployServer.Exists(h.World));
        var pack = new HG.GameplayServerPack(local: true);
        var input = new List<Fdp.ModuleHost.Abstractions.IEcsModuleSystem>();
        pack.Compose(HG.LocalGameplayHub.Of(h.World).ServerEnd(), h.Map, PackHarness.LocalNodeId, null, input);
        Assert.True(DeployServer.Exists(h.World));
        Assert.Same(HG.LocalGameplayHub.Of(h.World), ((HG.LocalGameplayEnd)pack.WireEnd!).Hub);
        ((HG.LocalGameplayEnd)pack.WireEnd!).Dispose();
        Assert.False(DeployServer.Exists(h.World));   // a closed server end is no server
    }

    // ---- boundary --------------------------------------------------------------------------------------------------------------------

    /// <summary>A world boundary rebuilds the seat's flow (D-13): the old flow is forgotten by HROT's registry, the seat's slot and its man are freed, and the seat chooses again in the next world.</summary>
    [Fact]
    public void A_world_boundary_rebuilds_the_flow_and_frees_the_seat()
    {
        using var h = new Deploy(seats: 1);
        h.AddRole(RoleId, ForceId.Friend, "Rifleman", max: 0);
        h.AddArea(AreaA, ForceId.Friend, new Vector3(0, 0, 0), Deploy.Square(20));
        h.Run(() => h.Seat(1).Offers.Any(o => o.Kind == PlayerOfferKind.Role), "offers");
        h.Seat(1).Join(new PlayerJoin(PlayerJoinKind.Role, RoleId, null, Side: 1));
        h.Run(() => h.Seat(1).Slot.State == PlayerSlotState.Playing, "playing");
        int man = h.Seat(1).Slot.UnitId;
        var before = ((DeployFlowPlayerFlow)h.Seat(1).Flow).Flow!;
        Assert.Same(before, HG.DeployFlowSystem.Of(h.World, h.Seat(1).Station));

        h.Seat(1).Boundary();
        Assert.Null(HG.DeployFlowSystem.Of(h.World, h.Seat(1).Station));
        Assert.Equal(PlayerSlotState.Waiting, h.Seat(1).Slot.State);
        h.Run(() => h.Seat(1).Slot.State == PlayerSlotState.Choosing && h.Holder(man) == 0, "chooses again; the man is free");
        Assert.NotSame(before, ((DeployFlowPlayerFlow)h.Seat(1).Flow).Flow);
        Assert.Same(((DeployFlowPlayerFlow)h.Seat(1).Flow).Flow, HG.DeployFlowSystem.Of(h.World, h.Seat(1).Station));
    }

    // ---- T-055: the chosen side addresses a seat that has not deployed ---------------------------------------------------------------

    /// <summary>
    /// T-055: a seat with no unit is addressed by the side it CHOSE (observed, or asked for and was refused), by HROT's own rule; before it asked it is Neutral. Mutation: the requested side ignored.
    /// </summary>
    [Fact]
    public void A_seat_that_has_not_deployed_is_addressed_by_the_side_it_asked_for()
    {
        using var h = new Deploy(seats: 3);
        h.AddPlaced(PlacedId, ForceId.Friend, "Rifleman");
        h.Run(() => h.Seat(1).Offers.Count == 1, "offers");
        Assert.True(h.Seat(1).Join(new PlayerJoin(PlayerJoinKind.Observe, 0, null, Side: 2)));
        Assert.True(h.Seat(2).Join(new PlayerJoin(PlayerJoinKind.Unit, PlacedId, null, Side: 2)));   // a Friend unit asked for as Hostile: refused
        h.Run(() => h.Seat(1).Slot.State == PlayerSlotState.Observing && h.Seat(2).Slot.State == PlayerSlotState.Choosing && h.Seat(2).Slot.Reason == "WrongSide", "observing; refused");

        foreach (int n in new[] { 1, 2 })
        {
            Assert.Equal((0, true, false), (h.Seat(n).Slot.UnitId, h.Seat(n).Addressed("Side", 2), h.Seat(n).Addressed("Side", 1)));   // Hostile yes, Friend no
        }

        Assert.False(h.Seat(3).Addressed("Side", 2));   // asked nothing: Neutral (DIS 3)
        Assert.True(h.Seat(3).Addressed("Side", 3));
        Assert.True(h.Seat(3).Addressed("All", 0));
    }

    // ---- (g) 0 B ---------------------------------------------------------------------------------------------------------------------

    /// <summary>(g) With three seats Playing, the seat flows' per-frame path allocates nothing in the steady state (HOT PATH rule): the flows' Frame over 300 frames (about 5 s of run time, so the 1 s server polls are in it).</summary>
    [Fact]
    public void Three_playing_seats_allocate_0_bytes_per_steady_frame()
    {
        using var h = new Deploy(seats: 3);
        h.AddRole(RoleId, ForceId.Friend, "Rifleman", max: 0);
        h.AddArea(AreaA, ForceId.Friend, new Vector3(0, 0, 0), Deploy.Square(30));
        h.Run(() => h.Seat(1).Offers.Any(o => o.Kind == PlayerOfferKind.Role), "offers");
        for (int n = 1; n <= 3; n++)
        {
            h.Seat(n).Join(new PlayerJoin(PlayerJoinKind.Role, RoleId, null, Side: 1));
        }

        h.Run(() => Enumerable.Range(1, 3).All(n => h.Seat(n).Slot.State == PlayerSlotState.Playing), "all play");
        h.RunSteps(150);   // warm up: the server's republish, the first polls

        var flows = Enumerable.Range(1, 3).Select(n => h.Seat(n).Flow).ToArray();
        // the steady frame of the gateway: the clock moves, no server write is pending (the server system is not stepped here: its own once-a-second republish allocates in HROT)
        for (int i = 0; i < 20; i++)
        {
            h.AdvanceMs(17);
            foreach (var f in flows)
            {
                f.Frame(h.World, true, false);
            }
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++)
        {
            h.AdvanceMs(17);
            foreach (var f in flows)
            {
                f.Frame(h.World, true, false);
            }
        }

        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.All(new[] { 1, 2, 3 }, n => Assert.Equal(PlayerSlotState.Playing, h.Seat(n).Slot.State));
    }

    /// <summary>
    /// T-055 (b), HOT path: the station's whole frame (flow, gizmos, the held unit's status, the slot) of three PLAYING seats allocates nothing in the steady state. Found and removed: <c>ReadUnit</c> built a list, an iterator and an
    /// enum name string per seat per frame (about 370 B a seat). Mutation: the use actions read through the allocating overload again.
    /// </summary>
    [Fact]
    public void Three_playing_seats_allocate_0_bytes_in_the_stations_whole_frame()
    {
        using var h = new Deploy(seats: 3);
        h.AddRole(RoleId, ForceId.Friend, "Rifleman", max: 0);
        h.AddArea(AreaA, ForceId.Friend, new Vector3(0, 0, 0), Deploy.Square(30));
        h.Run(() => h.Seat(1).Offers.Any(o => o.Kind == PlayerOfferKind.Role), "offers");
        for (int n = 1; n <= 3; n++)
        {
            h.Seat(n).Join(new PlayerJoin(PlayerJoinKind.Role, RoleId, null, Side: 1));
        }

        h.Run(() => Enumerable.Range(1, 3).All(n => h.Seat(n).Slot.State == PlayerSlotState.Playing), "all play");
        h.RunSteps(150);
        var seats = new[] { h.Seat(1), h.Seat(2), h.Seat(3) };
        for (int i = 0; i < 20; i++)
        {
            h.AdvanceMs(17);
            foreach (var seat in seats)
            {
                seat.Frame(h.World, true, false);
            }
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 300; i++)
        {
            h.AdvanceMs(17);
            for (int k = 0; k < seats.Length; k++)
            {
                seats[k].Frame(h.World, true, false);
            }
        }

        Assert.Equal(0L, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.All(seats, seat => Assert.Equal(PlayerSlotState.Playing, seat.Slot.State));
    }

    // ---- replay ----------------------------------------------------------------------------------------------------------------------

    /// <summary>A shown recording: the seat's flow sits in Waiting (DD-023 section 6) and joins are refused; live again, it chooses.</summary>
    [Fact]
    public void While_a_recording_is_shown_the_seat_waits_and_a_join_is_refused()
    {
        using var h = new Deploy(seats: 1);
        h.AddPlaced(PlacedId, ForceId.Friend, "Rifleman");
        h.Run(() => h.Seat(1).Slot.State == PlayerSlotState.Choosing, "choosing");
        h.State = Fdp.Toolkit.Orchestration.ClusterState.OperatingReplay;
        h.RunSteps(3);
        Assert.Equal(PlayerSlotState.Waiting, h.Seat(1).Slot.State);
        Assert.False(h.Seat(1).Join(new PlayerJoin(PlayerJoinKind.Unit, PlacedId, null)));
        h.State = Fdp.Toolkit.Orchestration.ClusterState.OperatingLive;
        h.Run(() => h.Seat(1).Slot.State == PlayerSlotState.Choosing, "choosing after the replay");
    }
}
