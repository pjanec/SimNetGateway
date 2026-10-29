using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.NedRpr;
using SimNetGateway.Protocol.Display;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;
using SimNetGateway.Protocol.Rpr.Fom;

namespace SimNetGateway.Showcase;

// Acts 5 - 8: engagement, operator override, faults and loopback, pause and resume.
public sealed partial class ConvoyAmbushScript
{
    private const string DefaultDamageRule = "RprEntityEgress/Damage";
    private const string CustomDamageRule = "CustomerDamagePercent/Damage";

    private static readonly Vec3 EngagementArea = NedRprConversions.GeodeticToEcef(50.02, 14.026, 300.0);

    private static ulong T1NedId(ScenarioContext ctx) =>
        ctx.RprEntity(T1Handle) is { } t1 ? ctx.NedIdOf(t1.Id) : 0;

    private static RprObjectHandle? MemberHandle(ScenarioContext ctx, int index) =>
        ctx.NedMember(ViperId, index) is { } m ? ctx.HandleOf(m.Id) : null;

    private static string WithoutDamage(string shape) =>
        string.Join(",", shape.Split(',').Where(p => p != "DamageState" && p != "DamagePercent"));

    private static List<DiagnosticEntry> DiagnosticsContaining(ScenarioContext ctx, string part) =>
        DiagnosticsSince(ctx, 0).Where(d => d.Message.Contains(part, StringComparison.Ordinal)).ToList();

    // ---- Act 5 - engagement ------------------------------------------------------------------------------------------------

    private void Act5(ActBuilder a)
    {
        a.Expect("Module", "completed by timeout", DiagnosticSeverity.Warning);

        a.Inject(60.0, "Viper member 2: weapon status (selected munition 501, ammo 30)", h =>
        {
            var m = new NedWeaponStatus(ViperId, TkbMunitionA, 30, Member: 2);
            _recording.Add(m);
            h.SendNed(m);
        });
        a.Inject(61.0, "T-1 fires at the platform of Viper member 1 (RPR fire interaction)", h =>
        {
            var target = h.NedMembers.TryResolve(new NedMemberKey(ViperId, 1), out var member) && h.RprHandles.TryGetKey(member, out var handle)
                ? handle
                : (RprObjectHandle?)null;
            h.SendRpr(new RprWeaponFire(RprFireE1, new RprObjectHandle(T1Handle), target, NedRprConversions.ToWorldLocation(EngagementArea),
                DisOf(TkbMunitionA), 1));
        });
        a.Check(61.5, "S-5.1", ctx =>
        {
            var v = new Verdict();
            var ned = ctx.NedBetween(61.0, 61.5);
            v.Equal(1, ned.Count, "NED messages (exactly one fire)");
            var fires = Select<INedMessage, NedWeaponFire>(ned);
            if (fires.Count == 1)
            {
                var f = fires[0].Message;
                v.Equal(ctx.Host.SeenEventId(RprFireE1), f.FireId, "fire id (the id the gateway saw for the RPR fire: HLA carries no guid)");
                v.Equal(T1NedId(ctx), f.Shooter, "shooter (T-1's NED id)");
                v.Equal(-1, f.ShooterMember, "shooter member");
                v.Equal(ViperId, f.Target, "target (the formation, not the platform)");
                v.Equal(1, f.TargetMember, "target member");
                v.Equal(TkbMunitionA, f.MunitionTkb, "munition");
                v.Equal(1, f.Quantity, "quantity");
                v.Note($"NedWeaponFire shooter {f.Shooter} -> target (formation {f.Target}, member {f.TargetMember})");
            }

            return v.Result();
        });

        a.Inject(62.0, "NED detonation on Viper member 1, damage 3 (destroyed)", h =>
        {
            var m = new NedDetonation(NedDetonationD1, ViperId, TkbMunitionA, 50.02, 14.026, 300.0, NedDetonationResult.EntityImpact, 3, TargetMember: 1);
            _recording.Add(m);
            h.SendNed(m);
        });
        a.Check(62.5, "S-5.2", ctx =>
        {
            var v = new Verdict();
            var member = ctx.NedMember(ViperId, 1);
            var handle = MemberHandle(ctx, 1);
            v.That(member is not null && handle is not null, "Viper member 1 or its handle is missing");
            var rpr = RprEvents(ctx, 62.0, 62.5);
            v.Equal(2, rpr.Count, "RPR messages (one update, one detonation)");
            if (rpr.Count == 2)
            {
                v.That(rpr[0].Message is RprObjectUpdate, "the first RPR message is the damage update");
                v.That(rpr[1].Message is RprMunitionDetonation, "the second RPR message is the detonation");
                if (rpr[0].Message is RprObjectUpdate u)
                {
                    v.Equal(handle, u.Handle, "update handle");
                    v.Equal("DamageState", Shape(u), "properties of the update (the damage state and nothing else)");
                    v.Equal(RprDamageState.Destroyed, u.DamageState ?? RprDamageState.NoDamage, "damage state");
                }

                if (rpr[1].Message is RprMunitionDetonation d)
                {
                    v.Equal(NedDetonationD1, d.EventId, "detonation id");
                    v.Equal(handle, d.TargetObject, "detonation target");
                    v.That(d.FiringObject is null, "the detonation has no firing object (NED did not name one)");
                    v.Equal(DisOf(TkbMunitionA), d.Munition, "munition");
                    v.Equal(RprDetonationResult.EntityImpact, d.Result, "result");
                }
            }

            // what the RPR federate received through the RTI: member 1's damage state (a partial update of one attribute) and the detonation
            var seenMember = handle is { } memberHandle ? ctx.Peer.DiscoveredObject(memberHandle.Value) : null;
            v.That(seenMember is not null, "the RPR federate does not hold member 1");
            if (seenMember is not null)
            {
                v.Equal(DamageStatusEnum32.Destroyed, seenMember.Damage ?? DamageStatusEnum32.NoDamage, "damage state of member 1 at the RPR federate");
                v.Equal(ScenarioContext.TickOf(62.0), seenMember.LastReflectTick, "tick at which the RPR federate received member 1's damage state");
                PeerHoldsWhatWasSent(v, ctx, handle!.Value, "member 1");
            }

            var peerDetonations = PeerDetonations(ctx, 62.0, 62.5);
            v.Equal(1, peerDetonations.Count, "detonations the RPR federate received");
            if (peerDetonations.Count == 1)
            {
                var d = peerDetonations[0];
                v.Equal(seenMember?.Name ?? "-", d.Target, "target object of the detonation at the RPR federate (member 1's RTI name)");
                v.Equal(string.Empty, d.Firing, "firing object of the detonation at the RPR federate (none)");
                v.Equal(DetonationResultCodeEnum8.EntityImpact, d.Result, "result code at the RPR federate");
                v.Equal(ToFom(DisOf(TkbMunitionA)), d.Munition, "munition type at the RPR federate");
            }

            // the probe saw the damage change dispatched before the detonation, both under the correlation of the input
            string? damage = ctx.Probe.Order.FirstOrDefault(o => o.StartsWith("damage:", StringComparison.Ordinal) && o.EndsWith(":3", StringComparison.Ordinal));
            string? detonation = ctx.Probe.Order.FirstOrDefault(o => o.StartsWith("detonation:", StringComparison.Ordinal));
            v.That(damage is not null && detonation is not null, "the probe did not see the damage change and the detonation");
            if (damage is not null && detonation is not null)
            {
                int iDamage = ctx.Probe.Order.IndexOf(damage);
                int iDetonation = ctx.Probe.Order.IndexOf(detonation);
                v.That(iDamage < iDetonation, "the damage change was dispatched after the detonation");
                var damageCorrelation = long.Parse(damage.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture);
                v.That(ctx.Probe.Order.Count(o => o.StartsWith("damage:", StringComparison.Ordinal) && o.EndsWith(":3", StringComparison.Ordinal)) == 1,
                    "more than one destroyed-damage change seen by the probe");
                // the detonation event has its own correlation id; the state record is the one that wrote the damage
                bool wrote = false;
                foreach (var write in ctx.Engine.State.Journal.GetWrites(new CorrelationId(damageCorrelation)))
                {
                    wrote |= write.Entity == member?.Id && write.Change.Attr == ctx.Schema.Damage && write.Change.New.AsInt() == 3;
                }

                v.That(wrote, "Journal.GetWrites of the input's correlation does not show the damage write of member 1");
                v.Note($"probe order: {damage} then {detonation}");
            }

            return v.Result();
        });

        // the split fire F1: first part, then (about 120 ms later) the ammunition
        a.Inject(64.0, "Viper member 2 fires F1: NedWeaponFire (munition not given)", h =>
        {
            var m = new NedWeaponFire(NedFireF1, ViperId, h.NedIds.TryGetKey(h.RprHandles.TryResolve(h.ActualHandle(T1Handle), out var t1) ? t1 : default, out ulong id) ? id : 0,
                0, 50.02, 14.027, 300.0, 1, ShooterMember: 2);
            _recording.Add(m);
            h.SendNed(m);
        });
        a.Inject(64.1167, "Viper member 2 fires F1: NedFireAmmunition (TKB 502, quantity 2), 117 ms later", h =>
        {
            var m = new NedFireAmmunition(NedFireF1, TkbMunitionB, 2);
            _recording.Add(m);
            h.SendNed(m);
        });
        a.Check(64.5, "S-5.3", ctx =>
        {
            var v = new Verdict();
            long fireTick = ScenarioContext.TickOf(64.0);
            long ammoTick = ScenarioContext.TickOf(64.1167);
            var fires = ctx.RprOf<RprWeaponFire>(64.0, 64.5);
            v.Equal(1, fires.Count, "RprWeaponFire messages for F1");
            v.Equal(0, ctx.RprOf<RprWeaponFire>(64.0, 64.1).Count, "RprWeaponFire before the ammunition part arrived");
            v.Equal(1, RprEvents(ctx, 64.0, 64.5).Count, "RPR messages in total (heartbeats aside)");
            if (fires.Count == 1)
            {
                var f = fires[0].Message;
                v.That(fires[0].Tick >= ammoTick && fires[0].Tick > fireTick, "the fire was sent with the ammunition part, not with the first part");
                v.Equal(NedFireF1, f.EventId, "event id");
                v.Equal(DisOf(TkbMunitionB), f.Munition, "munition (TKB 502)");
                v.Equal(2, f.Quantity, "quantity");
                v.Equal(MemberHandle(ctx, 2), f.FiringObject, "firing object (member 2's handle)");
                v.Equal(ctx.RprEntity(T1Handle) is { } t1 ? ctx.HandleOf(t1.Id) : null, f.TargetObject, "target object (T-1's handle)");
                v.Note($"sent at tick {fires[0].Tick} (first part at {fireTick}, ammunition at {ammoTick})");
            }

            // what the RPR federate received: ONE fire interaction, in the tick it was sent, with the objects named by their RTI names
            var peerFires = PeerFires(ctx, 64.0, 64.5);
            v.Equal(1, peerFires.Count, "weapon fires the RPR federate received");
            if (peerFires.Count == 1 && fires.Count == 1)
            {
                var received = peerFires[0];
                v.Equal(fires[0].Tick, received.Tick, "tick at which the RPR federate received the fire");
                v.Equal(PeerName(ctx, MemberHandle(ctx, 2)), received.Firing, "firing object at the RPR federate (member 2's RTI name)");
                v.Equal(PeerName(ctx, ctx.Host.ActualHandle(T1Handle)), received.Target, "target object at the RPR federate (T-1, its own object)");
                v.Equal(ToFom(DisOf(TkbMunitionB)), received.Munition, "munition type at the RPR federate");
                v.Equal((ushort)2, received.Quantity, "quantity at the RPR federate");
            }

            var seen = ctx.Probe.FireSeen.Where(o => o.EventId == NedFireF1).ToList();
            v.Equal(1, seen.Count, "the probe saw F1 dispatched once");
            if (seen.Count == 1)
            {
                v.Equal(28L, seen[0].Ammo ?? -1, "the shooter's ammunition when the event was dispatched (30 - 2)");
            }

            var member2 = ctx.NedMember(ViperId, 2);
            v.Equal(28L, member2 is not null && member2.TryGet(ctx.Schema.Ammo, out var ammo) ? ammo.AsInt() : -1, "Weapon.Ammo of member 2");
            return v.Result();
        });

        a.Inject(67.0, "Viper member 2 fires F2: NedWeaponFire, the ammunition part never arrives", h =>
        {
            ulong target = h.RprHandles.TryResolve(h.ActualHandle(T1Handle), out var t1) && h.NedIds.TryGetKey(t1, out ulong id) ? id : 0;
            h.SendNed(new NedWeaponFire(NedFireF2, ViperId, target, 0, 50.02, 14.027, 300.0, 1, ShooterMember: 2));
        });
        a.Check(69.0, "S-5.4", ctx =>
        {
            var v = new Verdict();
            var fires = ctx.RprOf<RprWeaponFire>(67.0, 69.0);
            v.Equal(1, fires.Count, "RprWeaponFire messages for F2");
            v.Equal(1, RprEvents(ctx, 67.0, 69.0).Count, "RPR messages in total (heartbeats aside)");
            if (fires.Count == 1)
            {
                var f = fires[0].Message;
                double delay = fires[0].WallTime - 67.0;
                v.That(delay >= 1.0 - 1e-6 && delay <= 1.0 + 0.2, F($"the fire was sent {delay:F3} s after its first part (timeout 1 s, checked every 100 ms)"));
                v.Equal(NedFireF2, f.EventId, "event id");
                v.Equal(DisOf(TkbMunitionA), f.Munition, "munition (the selected TKB 501 of the weapon status)");
                v.Equal(1, f.Quantity, "quantity");
                v.Equal(MemberHandle(ctx, 2), f.FiringObject, "firing object");
                v.Note(F($"sent {delay:F3} s after the first part"));
            }

            var peerFires = PeerFires(ctx, 67.0, 69.0);
            v.Equal(1, peerFires.Count, "weapon fires the RPR federate received");
            if (peerFires.Count == 1 && fires.Count == 1)
            {
                v.Equal(fires[0].Tick, peerFires[0].Tick, "tick at which the RPR federate received the fire");
                v.Equal(ToFom(DisOf(TkbMunitionA)), peerFires[0].Munition, "munition type at the RPR federate (the selected TKB 501)");
                v.Equal((ushort)1, peerFires[0].Quantity, "quantity at the RPR federate");
                v.Equal(PeerName(ctx, MemberHandle(ctx, 2)), peerFires[0].Firing, "firing object at the RPR federate");
            }

            var warnings = DiagnosticsContaining(ctx, $"fire {NedFireF2} completed by timeout");
            v.Equal(1, warnings.Count, "diagnostics 'fire F2 completed by timeout'");
            v.That(warnings.All(w => w.Severity == DiagnosticSeverity.Warning), "the diagnostic is a warning");
            v.Equal(0, ctx.Engine.State.Query().Has(ctx.Schema.FirePending).Count(), "assembly helpers left in the repository");
            v.Equal(0, ctx.Engine.State.Query().Has(ctx.Schema.FireId).Count(), "entities with a fire id");
            v.Equal(0, ctx.Host.NedFires.Count, "ned.fire bindings (the Guid keys are released)");
            v.That(!ctx.Host.NedFires.TryResolve(NedFireF2, out _) && !ctx.Host.NedFires.TryResolve(NedFireF1, out _), "a fire id is still bound");
            var member2 = ctx.NedMember(ViperId, 2);
            v.Equal(27L, member2 is not null && member2.TryGet(ctx.Schema.Ammo, out var ammo) ? ammo.AsInt() : -1, "Weapon.Ammo of member 2 (28 - 1)");
            return v.Result();
        });

        a.Inject(70.0, "T-1's damage state rises: none -> moderate (2)", h =>
            h.SendRpr(new RprObjectUpdate(new RprObjectHandle(T1Handle)) { DamageState = RprDamageState.ModerateDamage }));
        a.Inject(73.0, "T-1's damage state falls: moderate -> slight (repair, 1)", h =>
            h.SendRpr(new RprObjectUpdate(new RprObjectHandle(T1Handle)) { DamageState = RprDamageState.SlightDamage }));
        a.Check(73.5, "S-5.5", ctx =>
        {
            var v = new Verdict();
            ulong id = T1NedId(ctx);
            var up = ctx.NedBetween(70.0, 70.5);
            v.Equal(2, up.Count, "NED messages when the damage rises");
            if (up.Count == 2)
            {
                v.Equal<object>(new NedEntityDamage(id, 2), up[0].Message, "first message (the state)");
                v.Equal<object>(new NedDamageNotification(id, -1, 0, 2), up[1].Message, "second message (the occurrence)");
            }

            var down = ctx.NedBetween(73.0, 73.5);
            v.Equal(1, down.Count, "NED messages when the damage falls");
            if (down.Count == 1)
            {
                v.Equal<object>(new NedEntityDamage(id, 1), down[0].Message, "the only message (no notification for a decrease)");
            }

            v.Note("0 -> 2: NedEntityDamage(2) + NedDamageNotification(0 -> 2); 2 -> 1: NedEntityDamage(1)");
            return v.Result();
        });

        a.Check(74.5, "S-5.6", ctx =>
        {
            var v = new Verdict();
            var nedFires = ctx.NedOf<NedWeaponFire>(60.0, 74.5);
            v.Equal(1, nedFires.Count, "NedWeaponFire messages in the act (only T-1's shot, which came from RPR)");
            v.That(nedFires.All(f => f.Message.FireId == ctx.Host.SeenEventId(RprFireE1)), "NED got a fire that NED itself sent (F1, F2)");
            v.Equal(0, ctx.NedBetween(60.0, 74.5).Count(r => r.Message is NedDetonation), "NedDetonation sent to NED (D1 came from NED)");
            var rprFires = ctx.RprOf<RprWeaponFire>(60.0, 74.5);
            v.Equal(2, rprFires.Count, "RprWeaponFire messages (F1 and F2, both from NED)");
            v.That(rprFires.All(f => f.Message.EventId != ctx.Host.SeenEventId(RprFireE1)), "RPR got the fire it sent itself (E1)");
            var dets = ctx.RprOf<RprMunitionDetonation>(60.0, 74.5);
            v.Equal(1, dets.Count, "RprMunitionDetonation messages");
            // the same through the RTI: the federate received F1, F2 and D1 and none of them carries the identifier of the fire it issued itself
            var e1 = ctx.Peer.EventIdentifierOf(RprFireE1);
            v.That(e1 is not null, "the RPR federate never sent E1");
            v.Equal(2, PeerFires(ctx, 60.0, 74.5).Count, "weapon fires the RPR federate received (F1 and F2)");
            v.Equal(1, PeerDetonations(ctx, 60.0, 74.5).Count, "detonations the RPR federate received (D1)");
            v.That(ctx.Peer.Fires.All(f => f.Event != e1) && ctx.Peer.Detonations.All(d => d.Event != e1),
                "the RPR federate received an event with the event identifier of its own fire E1 (echo through the RTI)");
            // the sources of the events as the event log recorded them
            var sources = new Dictionary<Guid, string>();
            foreach (var ev in ctx.Engine.State.Journal.RecentEvents)
            {
                if (ev.Payload.TryGet(ctx.Schema.EventId, out var id))
                {
                    sources[id.AsObject<Guid>()] = ctx.Engine.Sources.NameOf(ev.Source);
                }
            }

            v.Equal("rpr", sources.GetValueOrDefault(ctx.Host.SeenEventId(RprFireE1)) ?? "-", "source of E1");
            foreach (var id in new[] { NedFireF1, NedFireF2, NedDetonationD1 })
            {
                v.Equal("ned", sources.GetValueOrDefault(id) ?? "-", "source of " + MessageText.Short(id));
            }

            v.Note("E1 (rpr) reached NED only; F1, F2, D1 (ned) reached RPR only");
            return v.Result();
        });
    }

    // ---- Act 6 - operator override -------------------------------------------------------------------------------------------

    private void Act6(ActBuilder a)
    {
        a.Do(74.9, "note the fire counts of the two damage contributor rules", ctx =>
        {
            ctx.Remember("default-damage-fires", ctx.Rule(DefaultDamageRule).FireCount);
            ctx.Remember("custom-damage-fires", ctx.Rule(CustomDamageRule).FireCount);
        });
        a.Operator(75.0, DefaultDamageRule, enable: false);
        a.Operator(75.0, CustomDamageRule, enable: true);
        a.Inject(76.0, "NED reports damage 1 on Gate", h => h.SendNed(new NedEntityDamage(GateId, 1)));
        a.Check(76.5, "S-6.1", ctx =>
        {
            var v = new Verdict();
            var gate = ctx.NedEntity(GateId);
            var handle = gate is null ? null : ctx.HandleOf(gate.Id);
            var rpr = RprEvents(ctx, 76.0, 76.5);
            v.Equal(1, rpr.Count, "RPR messages");
            if (rpr.Count == 1 && rpr[0].Message is RprObjectUpdate u)
            {
                v.Equal(handle, u.Handle, "update handle (Gate)");
                v.Equal((byte)33, u.DamagePercent ?? 255, "DamagePercent");
                v.That(u.DamageState is null, "the default DamageState is present");
                v.Equal("DamagePercent", Shape(u), "properties of the update (no other change)");
                // over HLA: RPR FOM 2.0 has no damage percentage attribute (R-105), the transport drops it - the RPR federate receives nothing
                var seenGate = handle is { } gateHandle ? ctx.Peer.DiscoveredObject(gateHandle.Value) : null;
                v.That(seenGate is not null && seenGate.LastReflectTick != rpr[0].Tick, "the RPR federate received something for a damage percentage");
                v.Equal(ctx.Rpr.Messages.Count(r => r.Message is RprObjectUpdate { DamagePercent: not null }), ctx.Host.RprConnection.DroppedDamagePercent,
                    "damage percentages the transport dropped = the updates that carried one (RPR FOM 2.0 cannot carry them)");
                v.Note("RprObjectUpdate(Gate): DamagePercent = 33 (dropped by the HLA transport, R-105)");
            }
            else
            {
                v.That(false, "the RPR message is not an RprObjectUpdate");
            }

            return v.Result();
        });
        a.Check(81.5, "S-6.2", ctx =>
        {
            var v = new Verdict();
            long swap = ScenarioContext.TickOf(75.0);
            var notes = new List<string>();
            foreach (var (name, handle) in new (string, RprObjectHandle?)[]
            {
                ("Hawk", ctx.NedEntity(HawkId) is { } hawk ? ctx.HandleOf(hawk.Id) : null),
                ("Viper-1", MemberHandle(ctx, 1)),
                ("Gate", ctx.NedEntity(GateId) is { } gate ? ctx.HandleOf(gate.Id) : null),
            })
            {
                var beats = UpdatesOf(ctx.RprOf<RprObjectUpdate>(0, 81.5), handle ?? default).Where(r => IsHeartbeat(r.Message)).ToList();
                var before = beats.LastOrDefault(r => r.Tick < swap);
                var after = beats.FirstOrDefault(r => r.Tick >= swap);
                v.That(before.Message is not null && after.Message is not null, $"{name}: no heartbeat before and after the swap");
                if (before.Message is null || after.Message is null)
                {
                    continue;
                }

                string shapeBefore = WithoutDamage(Shape(before.Message));
                string shapeAfter = WithoutDamage(Shape(after.Message));
                v.Equal(shapeBefore, shapeAfter, $"{name}: shape of the heartbeat before/after the swap (without the damage group)");
                v.That(after.Message.DamageState is null, $"{name}: the replaced default damage group is still in the heartbeat");
                v.Equal(before.Message.Marking, after.Message.Marking, $"{name}: marking");
                v.Equal(before.Message.EntityType, after.Message.EntityType, $"{name}: entity type");
                v.Equal(before.Message.EntityIdentifier, after.Message.EntityIdentifier, $"{name}: entity identifier");
                byte? expected = name switch { "Viper-1" => 100, "Gate" => 33, _ => null };
                v.Equal(expected, after.Message.DamagePercent, $"{name}: damage percent of the heartbeat after the swap");
                notes.Add($"{name}: [{Shape(before.Message)}] -> [{Shape(after.Message)}]");
            }

            v.Note(string.Join("; ", notes));
            return v.Result();
        });
        a.Check(79.0, "S-6.3", ctx =>
        {
            var v = new Verdict();
            var def = ctx.Rule(DefaultDamageRule);
            var custom = ctx.Rule(CustomDamageRule);
            v.That(!def.Enabled, "the default rule is enabled");
            v.That(custom.Enabled, "the custom rule is disabled");
            v.Equal(ctx.Recall<long>("default-damage-fires"), def.FireCount, "fire count of the default rule (frozen at the swap)");
            v.Equal(ctx.Recall<long>("custom-damage-fires"), custom.FireCount - 1, "fire count of the custom rule (counting since the swap: Gate's damage)");
            v.That(def.FireCount > 0, "the default rule never fired before the swap");
            v.Note($"{DefaultDamageRule}: disabled, {def.FireCount} firings (frozen); {CustomDamageRule}: enabled, {custom.FireCount} firing(s)");
            return v.Result();
        });
    }

    // ---- Act 7 - faults and loopback -----------------------------------------------------------------------------------------

    private void Act7(ActBuilder a)
    {
        a.Expect("Transaction", "damage level 9 out of range", DiagnosticSeverity.Warning);
        a.Expect("Transaction", "FaultyModule: the handler for NedIffStatus failed on purpose", DiagnosticSeverity.Warning);
        a.Expect("Cascade", "refused at depth", DiagnosticSeverity.Warning);
        a.Expect("Module", "NED id range exhausted", DiagnosticSeverity.Warning);

        // 1. out-of-range content
        a.Do(80.4, "note the state before the out-of-range damage", ctx =>
        {
            ctx.Remember("snapshot", Snapshot(ctx));
            ctx.Remember("failures", ctx.Engine.Diagnostics.TransactionFailures);
            ctx.Remember("diagnostics", ctx.Engine.Diagnostics.Count);
        });
        a.Inject(80.5, "NED damage level 9 (out of range) for Gate", h => h.SendNed(new NedEntityDamage(GateId, 9)));
        a.Check(80.9, "S-7.1", ctx =>
        {
            var v = new Verdict();
            v.Equal(ctx.Recall<string>("snapshot"), Snapshot(ctx), "the repository changed (slot versions)");
            v.Equal(0, CountRecords(ctx, ctx.Host.NedSource, 80.5 - 0.5 * ScenarioRunner.Dt), "journal records of the NED input");
            v.Equal(0, ctx.NedBetween(80.5, 80.9).Count, "NED messages");
            v.That(ctx.RprPayloadBetween(80.5, 80.9).All(r => r.Message is RprObjectUpdate u && IsHeartbeat(u) && u.DamagePercent is null),
                "RPR got something caused by the rejected message");
            v.Equal(ctx.Recall<long>("failures") + 1, ctx.Engine.Diagnostics.TransactionFailures, "transaction failures");
            var entries = DiagnosticsSince(ctx, ctx.Recall<int>("diagnostics"));
            v.Equal(1, entries.Count, "new diagnostics");
            if (entries.Count == 1)
            {
                v.Equal("Transaction", entries[0].Category, "diagnostic category");
                v.Equal(nameof(NedDamageIngress), entries[0].Origin, "diagnostic origin (the module)");
                v.That(entries[0].Message.Contains("damage level 9 out of range", StringComparison.Ordinal), "diagnostic reason");
                v.Note(entries[0].Message);
            }

            return v.Result();
        });

        // 2. a message type nobody handles
        a.Do(81.4, "note the state before the unknown message", ctx =>
        {
            ctx.Remember("snapshot", Snapshot(ctx));
            ctx.Remember("unhandled", ctx.Engine.Diagnostics.UnhandledMessages);
            ctx.Remember("processed", ctx.Engine.Diagnostics.MessagesProcessed);
            ctx.Remember("diagnostics", ctx.Engine.Diagnostics.Count);
            ctx.Remember("failures", ctx.Engine.Diagnostics.TransactionFailures);
        });
        a.Inject(81.5, "NED message of a type nobody handles", h => h.SendNed(new UnknownNedMessage(77)));
        a.Check(81.9, "S-7.2", ctx =>
        {
            var v = new Verdict();
            v.Equal(ctx.Recall<long>("unhandled") + 1, ctx.Engine.Diagnostics.UnhandledMessages, "unhandled messages");
            v.Equal(1L, ctx.Engine.Diagnostics.UnhandledCount(typeof(UnknownNedMessage)), "unhandled UnknownNedMessage");
            v.Equal(ctx.Recall<long>("processed"), ctx.Engine.Diagnostics.MessagesProcessed, "messages that reached a handler");
            v.Equal(ctx.Recall<long>("failures"), ctx.Engine.Diagnostics.TransactionFailures, "transaction failures");
            v.Equal(ctx.Recall<int>("diagnostics"), ctx.Engine.Diagnostics.Count, "diagnostic entries");
            v.Equal(ctx.Recall<string>("snapshot"), Snapshot(ctx), "the repository changed (slot versions)");
            v.Equal(0, ctx.NedBetween(81.5, 81.9).Count, "NED messages");
            v.That(ctx.RprPayloadBetween(81.5, 81.9).All(r => r.Message is RprObjectUpdate u && IsHeartbeat(u)), "RPR got something caused by the unknown message");
            v.Note("counted once; no transaction, no diagnostic entry");
            return v.Result();
        });

        // 3. a handler that throws
        a.Do(82.4, "note the state before the throwing handler", ctx =>
        {
            ctx.Remember("snapshot", Snapshot(ctx));
            ctx.Remember("failures", ctx.Engine.Diagnostics.TransactionFailures);
            ctx.Remember("diagnostics", ctx.Engine.Diagnostics.Count);
            ctx.Faulty.Armed = true;
        });
        a.Inject(82.5, "NedIffStatus (the armed FaultyModule throws)", h => h.SendNed(new NedIffStatus(GateId, 1)));
        a.Check(82.9, "S-7.3", ctx =>
        {
            var v = new Verdict();
            v.Equal(1, ctx.Faulty.Thrown, "exceptions thrown by the handler");
            v.Equal(ctx.Recall<string>("snapshot"), Snapshot(ctx), "the repository changed (the transaction must be discarded)");
            v.Equal(ctx.Recall<long>("failures") + 1, ctx.Engine.Diagnostics.TransactionFailures, "transaction failures");
            var entries = DiagnosticsSince(ctx, ctx.Recall<int>("diagnostics"));
            v.Equal(1, entries.Count, "new diagnostics");
            if (entries.Count == 1)
            {
                v.Equal("Transaction", entries[0].Category, "diagnostic category");
                v.Equal(nameof(FaultyModule), entries[0].Origin, "diagnostic origin (the module)");
                v.That(entries[0].Message.Contains("exception: FaultyModule: the handler for NedIffStatus failed on purpose", StringComparison.Ordinal), "diagnostic carries the exception");
                v.Note(entries[0].Message);
            }

            v.Equal(0, ctx.NedBetween(82.5, 82.9).Count, "NED messages");
            return v.Result();
        });
        a.Do(83.0, "disarm the FaultyModule", ctx => ctx.Faulty.Armed = false);
        a.Inject(83.5, "the next valid message on the same channel: NED damage 2 for Gate", h => h.SendNed(new NedEntityDamage(GateId, 2)));
        a.Check(83.9, "S-7.3", ctx =>
        {
            var v = new Verdict();
            var gate = ctx.NedEntity(GateId);
            v.Equal(2L, gate is not null && gate.TryGet(ctx.Schema.Damage, out var damage) ? damage.AsInt() : -1, "Gate's damage after the next message");
            var rpr = RprEvents(ctx, 83.5, 83.9);
            v.Equal(1, rpr.Count, "RPR messages");
            v.That(rpr.Count == 1 && rpr[0].Message is RprObjectUpdate { DamagePercent: 66 } u && Shape(u) == "DamagePercent", "RPR got the update DamagePercent = 66");
            v.Equal(1, ctx.Faulty.Thrown, "exceptions thrown in total");
            v.Note("the message after the failure was processed normally (Gate damage 2, RPR 66 percent)");
            return v.Result();
        });

        // 4. the rogue processor: an infinite cascade, stopped by the engine
        a.Do(84.9, "note the state before the rogue cascade", ctx =>
        {
            ctx.Remember("cascades", ctx.Engine.Diagnostics.CascadeOverflows);
            ctx.Remember("diagnostics", ctx.Engine.Diagnostics.Count);
            ctx.Remember("rogue-records", CountRecords(ctx, ctx.Rogue.Source, 0));
            ctx.Remember("failures", ctx.Engine.Diagnostics.TransactionFailures);
        });
        a.Operator(85.0, RogueProcessor.LoopRule, enable: true);
        a.Inject(85.0, "NED damage 3 for Gate (a valid message in the tick of the cascade)", h => h.SendNed(new NedEntityDamage(GateId, 3)));
        a.Do(85.0, "kick the rogue processor: Gate's Rogue.Counter = 1", ctx => ctx.Rogue.Kick(ctx.NedEntity(GateId)!.Id));
        a.Operator(85.0 + ScenarioRunner.Dt, RogueProcessor.LoopRule, enable: false);
        a.Check(85.3, "S-7.4", ctx =>
        {
            var v = new Verdict();
            var gate = ctx.NedEntity(GateId);
            v.Equal(ctx.Recall<long>("cascades") + 1, ctx.Engine.Diagnostics.CascadeOverflows, "cascade overflows");
            var entries = DiagnosticsSince(ctx, ctx.Recall<int>("diagnostics")).Where(d => d.Category == "Cascade").ToList();
            v.Equal(1, entries.Count, "cascade-overflow diagnostics");
            if (entries.Count == 1)
            {
                v.That(entries[0].Message.Contains($"refused at depth {ctx.Engine.Options.MaxCascadeDepth + 1}", StringComparison.Ordinal),
                    "the overflow is the change set at depth MaxCascadeDepth + 1");
                v.Note(entries[0].Message);
            }

            int rogue = CountRecords(ctx, ctx.Rogue.Source, 0) - ctx.Recall<int>("rogue-records");
            v.Equal(ctx.Engine.Options.MaxCascadeDepth + 1, rogue, "committed rogue change sets (depths 0 .. MaxCascadeDepth)");
            long counter = gate is not null && gate.TryGet(ctx.Rogue.Counter, out var c) ? c.AsInt() : -1;
            v.Equal((long)(ctx.Engine.Options.MaxCascadeDepth + 1), counter, "Rogue.Counter (every committed change set is complete, the refused one left no trace)");
            // each committed rogue change set holds exactly the one counter write (nothing half applied)
            foreach (var record in ctx.Engine.State.Journal.Since(85.0 - 0.5 * ScenarioRunner.Dt))
            {
                if (record.Source == ctx.Rogue.Source)
                {
                    v.That(record.Count == 1 && record.Entities[0].Changes.Count == 1, $"rogue record {record.Correlation.Value} is not a single complete write");
                }
            }

            // the other rules of the tick ran normally
            v.Equal(3L, gate is not null && gate.TryGet(ctx.Schema.Damage, out var damage) ? damage.AsInt() : -1, "the valid message of the tick (Gate damage 3)");
            var rpr = RprEvents(ctx, 85.0, 85.3);
            v.That(rpr.Any(r => r.Message is RprObjectUpdate { DamagePercent: 100 }), "RPR did not get the 100 percent update of the valid message");
            v.Equal(0L, ctx.Engine.Diagnostics.RuleFailures, "rule failures");
            v.Equal(ctx.Recall<long>("failures"), ctx.Engine.Diagnostics.TransactionFailures, "transaction failures");
            v.That(!ctx.Rule(RogueProcessor.LoopRule).Enabled, "the rogue rule is still enabled");
            return v.Result();
        });

        // 5. loopback: for two seconds every NED outbound message is fed back into NED inbound
        a.Do(85.9, "note the state before the loopback", ctx =>
        {
            ctx.Remember("failures", ctx.Engine.Diagnostics.TransactionFailures);
            ctx.Remember("unhandled", ctx.Engine.Diagnostics.UnhandledMessages);
            ctx.Remember("looped", ctx.LoopedNed);
            ctx.Remember("diagnostics", ctx.Engine.Diagnostics.Count);
        });
        a.Do(86.0, "loopback on: NED outbound is fed back into NED inbound", ctx => ctx.LoopbackNed = true);
        a.Inject(86.5, "T-1 moves 11 m (RPR)", h =>
        {
            NedRprConversions.GeoToPivot(T1Geo.Lat + 0.0002, T1Geo.Lon, T1Geo.Alt, T1Geo.Heading, 0, 0, 0, out var p, out _, out _);
            h.SendRpr(new RprObjectUpdate(new RprObjectHandle(T1Handle)) { WorldLocation = NedRprConversions.ToWorldLocation(p) });
        });
        a.Inject(87.0, "T-1's damage state rises: slight -> moderate (RPR)", h =>
            h.SendRpr(new RprObjectUpdate(new RprObjectHandle(T1Handle)) { DamageState = RprDamageState.ModerateDamage }));
        a.Inject(87.5, "Wolf-2 moves 11 m (RPR)", h =>
        {
            NedRprConversions.GeoToPivot(50.12 + 0.0001, 14.12 + 0.0004 * 2, 280.0, 270.0, 0, 0, 0, out var p, out _, out _);
            h.SendRpr(new RprObjectUpdate(WolfMember(2)) { WorldLocation = NedRprConversions.ToWorldLocation(p) });
        });
        a.Do(88.0, "loopback off", ctx => ctx.LoopbackNed = false);
        a.Check(88.6, "S-7.5", ctx =>
        {
            var v = new Verdict();
            var ned = ctx.NedBetween(86.0, 88.6);
            int looped = ctx.LoopedNed - ctx.Recall<int>("looped");
            v.Equal(looped, ned.Count, "NED messages sent in the window (all of them looped back, none caused by the loop)");
            v.Equal(4, looped, "looped-back messages (geo-spatial, damage, damage notification, Wolf formation)");
            v.That(ned.Any(r => r.Message is NedGeoSpatial) && ned.Any(r => r.Message is NedEntityDamage) &&
                   ned.Any(r => r.Message is NedDamageNotification) && ned.Any(r => r.Message is NedFormation),
                "the expected kinds were looped");
            v.Equal(0, CountRecords(ctx, ctx.Host.NedSource, 86.0 - 0.5 * ScenarioRunner.Dt), "journal records committed by the NED side (versions unchanged)");
            v.Equal(ctx.Recall<long>("failures"), ctx.Engine.Diagnostics.TransactionFailures, "transaction failures");
            v.Equal(ctx.Recall<int>("diagnostics"), ctx.Engine.Diagnostics.Count, "diagnostic entries");
            v.Equal(ctx.Recall<long>("unhandled") + 1, ctx.Engine.Diagnostics.UnhandledMessages, "unhandled messages (the notification has no ingress)");
            v.Equal(0, ctx.NedBetween(88.0 + ScenarioRunner.Dt, 88.6).Count, "NED messages after the loopback ended");
            v.That(ctx.RprPayloadBetween(86.0, 88.6).All(r => r.Message is RprObjectUpdate u && IsHeartbeat(u)), "RPR got something caused by the looped-back messages");
            v.Note($"{looped} NED messages were fed back; none produced a record, a message or a diagnostic");
            return v.Result();
        });

        // 6. exhaustion of the NED id range: six reinforcements, four free ids
        a.Do(88.9, "note the NED id range before the reinforcements", ctx =>
        {
            ctx.Remember("ids-in-use", ctx.NedRange.InUse);
            ctx.Remember("diagnostics", ctx.Engine.Diagnostics.Count);
        });
        a.Inject(89.0, "red reinforcements R-1 .. R-6: six RPR platform objects", h =>
        {
            for (int n = 1; n <= 6; n++)
            {
                h.SendRpr(Reinforcement(n));
            }
        });
        a.Check(89.5, "S-7.6", ctx =>
        {
            var v = new Verdict();
            v.Equal(2UL, ctx.Recall<ulong>("ids-in-use"), "ids in use before (T-1 and the Wolf unit)");
            var ned = ctx.NedBetween(89.0, 89.5);
            var masters = Select<INedMessage, NedEntityMaster>(ned);
            var geos = Select<INedMessage, NedGeoSpatial>(ned);
            v.Equal(4, masters.Count, "reinforcements published to NED");
            v.Equal(4, geos.Count, "geo-spatials of those");
            v.Equal(8, ned.Count, "NED messages in total");
            v.Equal(4, masters.Select(m => m.Message.Id).Distinct().Count(), "distinct NED ids");
            v.That(masters.All(m => m.Message.Id >= NedRprConfigurations.ShowcaseNedIdRange.First && m.Message.Id <= NedRprConfigurations.ShowcaseNedIdRange.Last),
                "all ids are in the gateway range");
            v.That(masters.Select(m => m.Message.Name).SequenceEqual(["R-1", "R-2", "R-3", "R-4"]), "the first four reinforcements got the free ids");
            var warnings = DiagnosticsSince(ctx, ctx.Recall<int>("diagnostics")).Where(d => d.Message.Contains("NED id range exhausted", StringComparison.Ordinal)).ToList();
            v.Equal(2, warnings.Count, "allocation-failure diagnostics");
            v.That(warnings.All(w => w.Severity == DiagnosticSeverity.Warning && w.Origin == nameof(NedIdentityEgress)), "the diagnostics are warnings of NedIdentityEgress");
            v.Equal(0UL, ctx.NedRange.Available, "free ids in the range");
            foreach (int n in new[] { 5, 6 })
            {
                var entity = ctx.RprEntity(ReinforcementHandle(n));
                v.That(entity is not null && ctx.NedIdOf(entity.Id) == 0, $"R-{n} has a NED id although the range is exhausted");
            }

            v.Note("R-1 .. R-4 published, R-5 and R-6 not: " + string.Join(" | ", warnings.Select(w => w.Message)));
            return v.Result();
        });
    }

    // ---- Act 8 - pause and resume --------------------------------------------------------------------------------------------

    private void Act8(ActBuilder a)
    {
        a.Inject(PauseAt, "NED control: Pause", h => h.SendNed(new NedSimControl(NedControlKind.Pause, null)));
        a.Do(90.5, "note the frozen sim time", ctx => ctx.Remember("frozen", ctx.SimTime));
        a.Check(98.9, "S-8.1", ctx =>
        {
            var v = new Verdict();
            v.Equal(SessionState.Paused, ctx.Engine.Session.State, "session state");
            var stop = ctx.RprOf<RprSimulationControl>(PauseAt, PauseAt + 0.1);
            v.Equal(1, stop.Count, "StopFreeze messages");
            v.That(stop.Count == 1 && stop[0].Message.Kind == RprControlKind.StopFreeze, "the control message is StopFreeze");
            v.Equal(2, ctx.Host.RprConnection.DroppedSimulationControl, "simulation controls the HLA transport dropped so far (StartResume at Start, StopFreeze: not carried by RPR FOM 2.0, R-107)");
            v.Equal(ctx.Recall<double>("frozen"), ctx.SimTime, "sim time at 98.9 s against 90.5 s");
            var display = ctx.DisplayBetween(PauseAt + 0.3, 98.9);
            v.Equal(0, display.Count, "display messages during the pause (the prediction is unchanged: no writes)");
            v.Note(F($"sim time frozen at {ctx.SimTime:F3} s while wall time ran from 90.5 to 98.9 s"));
            return v.Result();
        });
        a.Check(98.9, "S-8.2", ctx =>
        {
            var v = new Verdict();
            double frozen = ctx.SimTime;
            var window = ctx.RprOf<RprObjectUpdate>(PauseAt + 0.1, 98.9).Where(r => IsHeartbeat(r.Message)).ToList();
            var subjects = new List<(string Name, RprObjectHandle? Handle, Func<long, (bool, Report)> Report)>
            {
                ("Hawk", ctx.NedEntity(HawkId) is { } hawk ? ctx.HandleOf(hawk.Id) : null, t => (TryReportOfEntity(ctx, HawkId, t, out var r), r)),
                ("Gate", ctx.NedEntity(GateId) is { } gate ? ctx.HandleOf(gate.Id) : null, t => (TryReportOfEntity(ctx, GateId, t, out var r), r)),
                ("Viper-0", MemberHandle(ctx, 0), t => (TryReportOfMember(ctx, ViperId, 0, t, out var r), r)),
                ("Viper-2", MemberHandle(ctx, 2), t => (TryReportOfMember(ctx, ViperId, 2, t, out var r), r)),
            };
            foreach (var (name, handle, report) in subjects)
            {
                var own = window.Where(r => r.Message.Handle == handle).ToList();
                v.That(own.Count >= 1, $"{name}: no heartbeat during the pause");
                double worst = 0;
                var first = own.Count > 0 ? Ecef(own[0].Message.WorldLocation ?? default) : default;
                foreach (var beat in own)
                {
                    var (ok, r) = report(beat.Tick);
                    v.That(ok, $"{name}: no NED report before the heartbeat");
                    worst = Math.Max(worst, Dist(Ecef(beat.Message.WorldLocation ?? default), r.At(frozen)));
                    v.That(Dist(Ecef(beat.Message.WorldLocation ?? default), first) < 1e-6, $"{name}: the position changed between the heartbeats of the pause");
                    v.Equal(frozen, beat.SimTime, $"{name}: sim time of the heartbeat");
                }

                v.That(worst < 0.01, F($"{name}: a heartbeat is {worst:F5} m from the analytic position at the frozen sim time"));
            }

            v.Note($"{window.Count} heartbeats during the pause, at the position predicted to the frozen sim time {frozen:F3} s");
            return v.Result();
        });
        a.Inject(ResumeAt, "NED control: Resume", h => h.SendNed(new NedSimControl(NedControlKind.Resume, null)));
        a.Check(99.5, "S-8.3", ctx =>
        {
            var v = new Verdict();
            v.Equal(SessionState.Running, ctx.Engine.Session.State, "session state");
            var go = ctx.RprOf<RprSimulationControl>(ResumeAt, 99.5);
            v.Equal(1, go.Count, "StartResume messages");
            v.That(go.Count == 1 && go[0].Message.Kind == RprControlKind.StartResume, "the control message is StartResume");
            v.Equal(3, ctx.Host.RprConnection.DroppedSimulationControl, "simulation controls the HLA transport dropped so far (R-107)");
            var display = ctx.DisplayOf<DisplayEntityPosition>(ResumeAt, 99.5);
            foreach (string label in new[] { "Hawk", "Viper-0", "Viper-1", "Viper-2" })
            {
                var own = display.Where(p => p.Message.Label == label).ToList();
                v.That(own.Count > 0, $"{label}: no display position after the resume");
                if (own.Count > 0)
                {
                    double delay = own[0].WallTime - ResumeAt;
                    v.That(delay <= 0.1 + 2 * ScenarioRunner.Dt, F($"{label}: first position {delay:F3} s after the resume (processor period 0.1 s)"));
                }
            }

            v.Note("display updates resumed within one processor period");
            return v.Result();
        });
    }
}
