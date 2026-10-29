using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.NedRpr;
using SimNetGateway.Protocol.Display;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Showcase;

// Acts 0 - 4: setup, free play, load and start, forces appear, movement and timing.
public sealed partial class ConvoyAmbushScript
{
    // ---- Act 0 - setup and validation ---------------------------------------------------------------------------------------

    private static void Act0(ActBuilder a)
    {
        a.Check(0.5, "S-0.1", ctx =>
        {
            var v = new Verdict();
            var report = ctx.Host.Binding.Validation;
            v.Equal(0, report.Errors.Count, "validation errors " + string.Join(" | ", report.Errors));
            v.Equal(0, report.Warnings.Count, "validation warnings " + string.Join(" | ", report.Warnings));
            v.Note(F($"0 errors, 0 warnings; {ctx.Engine.Rules.Count} rules, {ctx.Engine.Attributes.Count} attributes (with the probe, the faulty module and the rogue processor)"));
            return v.Result();
        });
        a.Check(0.5, "S-0.2", ctx =>
        {
            var v = new Verdict();
            (string Name, Type Key)[] expected =
            [
                ("ned.entity", typeof(ulong)),
                ("ned.member", typeof(NedMemberKey)),
                ("ned.fire", typeof(Guid)),
                ("rpr.handle", typeof(RprObjectHandle)),
                ("rpr.entityId", typeof(DisEntityId)),
                ("display.label", typeof(string)),
            ];
            foreach (var (name, key) in expected)
            {
                var map = ctx.Engine.Identities.Find(name);
                v.That(map is not null, $"identity map {name} does not exist");
                if (map is not null)
                {
                    v.Equal(key, map.KeyType, $"key type of {name}");
                }
            }

            v.Equal(expected.Length, ctx.Engine.Identities.All.Count, "number of identity maps");
            v.Note("maps: " + string.Join(", ", ctx.Engine.Identities.All.Select(m => m.Name + "<" + m.KeyType.Name + ">")));
            return v.Result();
        });
    }

    // ---- Act 1 - free play in Idle ------------------------------------------------------------------------------------------

    private void Act1(ActBuilder a)
    {
        // Hawk's geo-spatial arrives BEFORE its master (C12)
        a.Inject(1.0, "Hawk: geo-spatial before master", h => h.SendNed(HawkStart.Message(HawkId)));
        a.Check(1.4, "S-1.1", ctx =>
        {
            var v = new Verdict();
            var hawk = ctx.NedEntity(HawkId);
            v.That(hawk is not null, "Hawk (NED 21) does not exist in the pivot");
            if (hawk is not null)
            {
                v.That(hawk.Has(ctx.Schema.Position), "Hawk has no position");
                v.That(!hawk.Has(ctx.Schema.TypeTkb), "Hawk has a type although its master has not arrived");
            }

            v.Equal(0, ctx.RprBetween(1.0, 1.4).Count, "RPR messages (type unknown)");
            v.Equal(0, ctx.DisplayBetween(1.0, 1.4).Count, "display messages (no label, no identity yet)");
            v.Equal(0, ctx.NedBetween(1.0, 1.4).Count, "NED messages");
            v.Note("Hawk exists with a position and no type; nothing went to RPR, display or NED");
            return v.Result();
        });

        a.Inject(2.0, "Hawk: master", h => h.SendNed(new NedEntityMaster(HawkId, "Hawk", TkbUav, NedForce.Friendly)));
        a.Check(2.4, "S-1.2", ctx =>
        {
            var v = new Verdict();
            var regs = ctx.RprOf<RprRegisterObject>(2.0, 2.4);
            var updates = ctx.RprOf<RprObjectUpdate>(2.0, 2.4);
            v.Equal(1, regs.Count, "registration requests at the simulated RTI");
            v.Equal(1, updates.Count, "RprObjectUpdate messages for Hawk");
            v.Equal(regs.Count + updates.Count, ctx.RprBetween(2.0, 2.4).Count, "RPR messages in total (nothing else)");
            if (regs.Count == 1 && updates.Count == 1)
            {
                v.Equal(ScenarioContext.TickOf(2.0), regs[0].Tick, "tick of the registration request");
                v.Equal(regs[0].Tick + 1, updates[0].Tick, "tick of the first update (the handle arrives one tick later)");
                var u = updates[0].Message;
                var hawk = ctx.NedEntity(HawkId);
                v.That(hawk is not null && ctx.HandleOf(hawk.Id) == u.Handle, "the update uses the handle that is bound to Hawk");
                v.That(ctx.Host.IsRegisteredHandle(u.Handle), "the handle was delivered by the RTI");
                NedRprConversions.GeoToPivot(HawkStart.Message(HawkId), out var p, out var o, out var vel);
                PeerSeesFirstUpdate(v, ctx, u, updates[0].Tick, p);
                v.Equal("Hawk", u.Marking, "marking");
                v.Equal(DisOf(TkbUav), u.EntityType ?? default, "DIS type of TKB 200");
                v.Equal(RprForceId.Friendly, u.ForceId ?? RprForceId.Other, "force");
                v.That(u.EntityIdentifier is { Site: 1, Application: 3001 }, "EntityIdentifier (1, 3001, n)");
                v.That(u.WorldLocation is { } loc && Dist(Ecef(loc), p) < 1e-6, "ECEF position of the geo-spatial");
                v.That(u.Orientation is { } ori && Dist(new Vec3(ori.Psi, ori.Theta, ori.Phi), o) < 1e-9, "geocentric orientation");
                v.That(u.VelocityVector is { } vv && Dist(new Vec3(vv.X, vv.Y, vv.Z), vel) < 1e-6, "ECEF velocity");
                v.Equal(2, u.DeadReckoningAlgorithm ?? 0, "dead-reckoning algorithm FPW");
                v.That(u.DamageState is null && u.DamagePercent is null, "no damage group (Hawk has no damage)");
                v.Note(F($"RprRegisterObject at tick {regs[0].Tick}, one full update at tick {updates[0].Tick}: handle {MessageText.H(u.Handle)}, id {MessageText.Id(u.EntityIdentifier ?? default)}"));
            }

            return v.Result();
        });

        // the red tank T-1 appears on RPR (a platform object)
        a.Inject(3.0, "T-1: RPR platform object", h => h.SendRpr(T1First()));
        a.Check(3.4, "S-1.3", ctx =>
        {
            var v = new Verdict();
            var ned = ctx.NedBetween(3.0, 3.4);
            var masters = Select<INedMessage, NedEntityMaster>(ned);
            var geos = Select<INedMessage, NedGeoSpatial>(ned);
            v.Equal(1, masters.Count, "NedEntityMaster messages");
            v.Equal(1, geos.Count, "NedGeoSpatial messages");
            v.Equal(2, ned.Count, "NED messages in total (master + geo-spatial and nothing else)");
            if (masters.Count == 1 && geos.Count == 1)
            {
                var m = masters[0].Message;
                v.Equal("T-1", m.Name, "master name");
                v.Equal(TkbEnemyTank, m.TkbType, "master type");
                v.Equal(NedForce.Opposing, m.Force, "master force");
                v.That(m.Id >= NedRprConfigurations.ShowcaseNedIdRange.First && m.Id <= NedRprConfigurations.ShowcaseNedIdRange.Last,
                    $"NED id {m.Id} is in the gateway range");
                v.Equal(m.Id, geos[0].Message.Id, "geo-spatial id");
                v.That(masters[0].Sequence < geos[0].Sequence, "master before geo-spatial");
                // DD-008 section 6: the RTI's callbacks are pumped BEFORE the engine tick, so what the RPR federate announced in a tick is processed in it
                v.Equal(ScenarioContext.TickOf(3.0), masters[0].Tick, "tick at which NED got the master (the tick the RPR federate announced T-1)");
                v.Note($"NED id {m.Id}");
            }

            v.Equal(0, ctx.RprBetween(3.0, 3.4).Count, "RPR messages caused by T-1 (it must never be registered or described to its own side)");
            var t1 = ctx.RprEntity(T1Handle);
            v.That(t1 is not null, "T-1 does not exist");
            if (t1 is not null)
            {
                var t1Handle = ctx.Host.ActualHandle(T1Handle);
                v.That(ctx.Rpr.Messages.All(r => r.Message is not RprObjectUpdate { } u || u.Handle != t1Handle),
                    "an RPR message names T-1's own handle (echo)");
                v.That(ctx.Peer.DiscoveredObject(t1Handle.Value) is null, "T-1 came back to the RPR federate that registered it (echo through the RTI)");
                v.That(ctx.DisIdOf(t1.Id) is { Site: RedSite }, "T-1 keeps the DIS identifier the RPR side gave it");
            }

            return v.Result();
        });

        a.Do(5.9, "note the entities of the free play", ctx =>
            ctx.Remember("free-play", ctx.Entities().Select(e => e.Id).ToHashSet()));
        a.Check(5.9, "S-1.4", ctx =>
        {
            var v = new Verdict();
            v.Equal(SessionState.Idle, ctx.Engine.Session.State, "session state");
            v.That(Math.Abs(ctx.SimTime - ctx.RunnerTime) < 1e-6, F($"sim time {ctx.SimTime:F6} follows wall time {ctx.RunnerTime:F6}"));
            v.Note(F($"sim time {ctx.SimTime:F3} s at wall time {ctx.RunnerTime:F3} s"));
            return v.Result();
        });
    }

    // ---- Act 2 - Load and Start ---------------------------------------------------------------------------------------------

    private void Act2(ActBuilder a)
    {
        a.Inject(LoadAt, "NED control: LoadScenario 'Ambush'", h => h.SendNed(new NedSimControl(NedControlKind.Load, "Ambush")));
        a.Inject(StartAt, "NED control: Start", h => h.SendNed(new NedSimControl(NedControlKind.Start, null)));
        a.Check(6.3, "S-2.1", ctx => LoadedAndFrozen(ctx));
        a.Check(6.45, "S-2.1", ctx => LoadedAndFrozen(ctx));
        a.Check(6.9, "S-2.1", ctx =>
        {
            var v = new Verdict();
            v.Equal(SessionState.Running, ctx.Engine.Session.State, "state after Start");
            v.Equal(SessionMode.Live, ctx.Engine.Session.Mode, "mode");
            v.That(ctx.SimTime > 0.3 && ctx.SimTime < 0.45, F($"sim time {ctx.SimTime:F4} runs from Start (expected about 0.4)"));
            v.Note(F($"Running, sim time {ctx.SimTime:F3} s at wall time {ctx.RunnerTime:F3} s"));
            return v.Result();
        });

        a.Check(7.4, "S-2.2", ctx =>
        {
            var v = new Verdict();
            var rpr = ctx.RprPayloadBetween(LoadAt, 7.4);
            v.Equal(1, rpr.Count, "RPR messages from Load to after Start (exactly one control message)");
            if (rpr.Count == 1)
            {
                v.That(rpr[0].Message is RprSimulationControl { Kind: RprControlKind.StartResume }, "the RPR message is StartResume (at Start, nothing for Load)");
                v.Equal(ScenarioContext.TickOf(StartAt), rpr[0].Tick, "tick of the control message");
            }

            v.Equal(0, ctx.NedBetween(LoadAt, 7.4).Count, "NED messages (the control came from NED: no echo)");
            // over HLA: RPR FOM 2.0 has no simulation management interaction (R-107) - the control message is the gateway's output, the transport counts it as dropped
            v.Equal(1, ctx.Host.RprConnection.DroppedSimulationControl, "simulation controls the HLA transport dropped (not carried by RPR FOM 2.0)");
            v.Note("RPR got one StartResume at Start (the gateway sent it; HLA carries no simulation control, R-107: dropped by the transport), NED nothing");
            return v.Result();
        });
        a.Check(7.4, "S-2.3", ctx =>
        {
            var v = new Verdict();
            var before = ctx.Recall<HashSet<EntityId>>("free-play");
            var after = ctx.Entities().Select(e => e.Id).ToHashSet();
            v.That(before.SetEquals(after), $"the entities after Load ({after.Count}) differ from the free play ({before.Count})");
            v.That(ctx.NedEntity(HawkId) is not null, "Hawk survived");
            v.That(ctx.RprEntity(T1Handle) is not null, "T-1 survived");
            v.Equal(0, ctx.RprOf<RprObjectRemoved>(LoadAt, 7.4).Count, "RprObjectRemoved");
            v.Equal(0, ctx.NedOf<NedEntityDisposed>(LoadAt, 7.4).Count, "NedEntityDisposed");
            v.Equal(0, ctx.DisplayOf<DisplayEntityRemoved>(LoadAt, 7.4).Count, "DisplayEntityRemoved");
            v.Note($"{after.Count} entities before and after");
            return v.Result();
        });
    }

    private static CheckResult LoadedAndFrozen(ScenarioContext ctx)
    {
        var v = new Verdict();
        v.Equal(SessionState.Loaded, ctx.Engine.Session.State, "state after Load");
        v.Equal(SessionMode.Live, ctx.Engine.Session.Mode, "mode");
        v.Equal("Ambush", ctx.Engine.Session.Scenario, "scenario");
        v.Equal(0.0, ctx.SimTime, "sim time (reset by Load, frozen until Start)");
        v.Note(F($"Loaded 'Ambush', sim time {ctx.SimTime:F3} s at wall time {ctx.RunnerTime:F3} s"));
        return v.Result();
    }

    // ---- Act 3 - forces appear -----------------------------------------------------------------------------------------------

    private static readonly double[] ViperShiftLon = [0.00001, 0.00001, 0.00001];

    /// <summary>The convoy's states: [0] at the first report (7.5 s), [k] after the k-th report of <see cref="ViperReportTimes"/>, [8] at 100 s.</summary>
    private static readonly Geo[][] ViperStates = BuildViperStates();

    private static Geo[][] BuildViperStates()
    {
        var states = new Geo[9][];
        states[0] = [ViperStart(0), ViperStart(1), ViperStart(2)];
        double previous = 7.5;
        for (int k = 0; k < ViperReportTimes.Length; k++)
        {
            states[k + 1] = Advance(states[k], previous, ViperReportTimes[k], ViperReportHeadings[k], ViperReportSpeeds[k]);
            previous = ViperReportTimes[k];
        }

        states[8] = Advance(states[7], previous, 100.0, 90.0, 10.0);
        return states;
    }

    private static Geo[] Advance(Geo[] from, double fromTime, double toTime, double heading, double speed) =>
        from.Select(g => g.After(World(toTime) - World(fromTime), heading, speed)).ToArray();

    private void Act3(ActBuilder a)
    {
        a.Inject(7.5, "Viper: NedFormation 7 with members 0, 1, 2", h =>
        {
            var m = ViperMessage(ViperStates[0]);
            _recording.Add(m);
            h.SendNed(m);
        });
        // a second message in the same tick: the members have moved 1 m while their registration is pending (coalescing)
        a.Inject(7.5, "Viper: the members moved 1 m (their handles are still pending)", h =>
            h.SendNed(ViperMessage(ViperStates[0].Select((g, i) => g with { Lon = g.Lon + ViperShiftLon[i] }).ToArray())));
        a.Inject(7.5, "Gate: master", h =>
        {
            var m = new NedEntityMaster(GateId, "Gate", TkbCheckpoint, NedForce.Friendly);
            _recording.Add(m);
            h.SendNed(m);
        });
        a.Inject(7.5, "Gate: geo-spatial", h =>
        {
            var m = GateGeo.Message(GateId);
            _recording.Add(m);
            h.SendNed(m);
        });
        a.Check(7.9, "S-3.1", ctx =>
        {
            var v = new Verdict();
            var unit = ctx.NedEntity(ViperId);
            v.That(unit is not null, "unit (NED 7) does not exist");
            var creating = ctx.Probe.CreatingRecords.Where(r => r.Source == "ned" && r.Created == 4).ToList();
            v.Equal(1, creating.Count, "records from NED that created 4 entities");
            if (creating.Count == 1)
            {
                v.Equal(4, creating[0].Deltas, "deltas of that record (unit + 3 members)");
                v.Equal(3, creating[0].ParentWrites, "members linked in that record (Structure.Parent)");
                v.Note($"the probe saw record {creating[0].Correlation}: {creating[0].Deltas} deltas, {creating[0].Created} created, {creating[0].ParentWrites} Structure.Parent writes");
            }

            if (unit is not null)
            {
                v.That(!unit.Has(ctx.Schema.Position), "the unit has a position");
                v.Equal("Viper", unit.TryGet(ctx.Schema.Name, out var name) ? name.AsString() : null, "unit name");
                var members = ctx.Engine.State.Query().ReferencedBy(ctx.Schema.Parent, unit.Id).Count();
                v.Equal(3, members, "members found through the reverse reference index");
                for (int i = 0; i < 3; i++)
                {
                    var m = ctx.NedMember(ViperId, i);
                    v.That(m is not null, $"member {i} does not exist");
                    if (m is not null)
                    {
                        v.That(m.TryGet(ctx.Schema.Parent, out var parent) && parent.AsEntity() == unit.Id, $"member {i} Structure.Parent");
                        v.Equal(i, m.TryGet(ctx.Schema.MemberIndex, out var index) ? (int)index.AsInt() : -1, $"member {i} Structure.MemberIndex");
                        v.Equal("Viper-" + i, m.TryGet(ctx.Schema.Name, out var mn) ? mn.AsString() : null, $"member {i} name");
                        v.That(m.CreatedBy == unit.CreatedBy, $"member {i} was created by another transaction than the unit");
                    }
                }
            }

            return v.Result();
        });
        a.Check(7.9, "S-3.2", ctx =>
        {
            var v = new Verdict();
            var regs = ctx.RprOf<RprRegisterObject>(7.5, 7.9);
            var updates = ctx.RprOf<RprObjectUpdate>(7.5, 7.9);
            v.Equal(4, regs.Count, "registrations (3 members + Gate)");
            v.Equal(4, updates.Count, "RprObjectUpdate messages (one per object)");
            v.Equal(regs.Count + updates.Count, ctx.RprBetween(7.5, 7.9).Count, "RPR messages in total");
            var unit = ctx.NedEntity(ViperId);
            v.That(unit is null || ctx.HandleOf(unit.Id) is null, "the unit has an RPR handle");
            v.That(unit is null || ctx.DisIdOf(unit.Id) is null, "the unit has a DIS entity identifier");
            var handles = new HashSet<RprObjectHandle>();
            var ids = new HashSet<DisEntityId>();
            long registrationTick = ScenarioContext.TickOf(7.5);
            for (int i = 0; i < 3; i++)
            {
                var member = ctx.NedMember(ViperId, i);
                var handle = member is null ? null : ctx.HandleOf(member.Id);
                v.That(handle is not null, $"member {i} has no RPR handle");
                if (member is null || handle is null)
                {
                    continue;
                }

                handles.Add(handle.Value);
                var id = ctx.DisIdOf(member.Id);
                v.That(id is not null, $"member {i} has no DIS identifier");
                if (id is { } dis)
                {
                    ids.Add(dis);
                }

                var own = updates.Where(r => r.Message.Handle == handle).ToList();
                v.Equal(1, own.Count, $"updates of member {i}");
                if (own.Count == 1)
                {
                    var u = own[0].Message;
                    v.Equal(registrationTick + 1, own[0].Tick, $"tick of member {i}'s first update");
                    v.Equal("Viper-" + i, u.Marking, $"member {i} marking");
                    v.That(u.EntityIdentifier == id, $"member {i} update carries its DIS identifier");
                    var shifted = ViperStates[0][i] with { Lon = ViperStates[0][i].Lon + ViperShiftLon[i] };
                    NedRprConversions.GeoToPivot(shifted.Lat, shifted.Lon, shifted.Alt, shifted.Heading, 0, 0, shifted.Speed, out var p, out _, out _);
                    v.That(u.WorldLocation is { } loc && Dist(Ecef(loc), p) < 1e-6,
                        $"member {i} update has the position of the SECOND formation message (the update during the pending handle was coalesced)");
                    v.That(u.VelocityVector is not null && u.Orientation is not null && u.DeadReckoningAlgorithm == 2, $"member {i} update is complete");
                }
            }

            v.Equal(3, handles.Count, "distinct handles of the members");
            v.Equal(3, ids.Count, "distinct DIS identifiers of the members");
            v.That(regs.All(r => r.Tick == registrationTick), "all registrations were requested in the tick of the message");
            v.That(updates.Any(r => r.Message.Marking == "Gate"), "Gate was published as well");
            // what the RPR federate received through the RTI: the four objects discovered, each with its one full update in the tick it was sent
            v.Equal(5, ctx.Peer.Discovered.Count, "objects the RPR federate discovered so far (Hawk, 3 members, Gate)");
            foreach (var sent in updates)
            {
                var seen = ctx.Peer.DiscoveredObject(sent.Message.Handle.Value);
                v.That(seen is not null, $"the RPR federate did not discover '{sent.Message.Marking}' under handle {sent.Message.Handle.Value}");
                if (seen is not null)
                {
                    v.Equal(sent.Tick, seen.LastReflectTick, $"tick at which the RPR federate received the update of {sent.Message.Marking}");
                    v.Equal(1, seen.Count("Spatial"), $"reflections of {sent.Message.Marking}'s Spatial");
                    PeerHoldsWhatWasSent(v, ctx, sent.Message.Handle, sent.Message.Marking ?? "?");
                }
            }

            v.Note($"3 member objects + Gate; handles {string.Join(",", handles.Select(h => MessageText.H(h)).OrderBy(s => s))}");
            return v.Result();
        });
        a.Check(8.2, "S-3.3", ctx =>
        {
            var v = new Verdict();
            var positions = ctx.DisplayOf<DisplayEntityPosition>(7.5, 8.2);
            var labels = positions.Select(p => p.Message.Label).Distinct().ToList();
            foreach (string label in new[] { "Viper-0", "Viper-1", "Viper-2", "Gate" })
            {
                v.That(labels.Contains(label), $"no display position for '{label}'");
            }

            v.That(!labels.Contains("Viper"), "the unit has a marker on the display");
            v.Equal(1, positions.Count(p => p.Message.Label == "Gate"), "display positions of Gate (static)");
            for (int i = 0; i < 3; i++)
            {
                var m = ctx.NedMember(ViperId, i);
                v.That(m is not null && ctx.Host.DisplayLabels.TryGetKey(m.Id, out string? label) && label == "Viper-" + i, $"display.label of member {i}");
            }

            v.Note("labels " + Names(labels.Where(l => l.StartsWith("Viper", StringComparison.Ordinal) || l == "Gate")));
            return v.Result();
        });

        // Hawk reports after the scenario started (the sim time was reset by Load)
        a.Inject(8.0, "Hawk: position report after Start", h => h.SendNed(HawkAt(8.0).Message(HawkId)));

        // the red squad: three platforms first (members 1 .. 3), then the aggregate that lists them plus member 4
        a.Inject(8.5, "Wolf-1, Wolf-2, Wolf-3: RPR platform objects", h =>
        {
            for (int n = 1; n <= 3; n++)
            {
                h.SendRpr(WolfPlatform(n));
            }
        });
        a.Check(9.0, "S-3.4", ctx =>
        {
            var v = new Verdict();
            for (int n = 1; n <= 3; n++)
            {
                var member = ctx.RprEntity(WolfMember(n).Value);
                v.That(member is not null, $"Wolf-{n} does not exist");
                v.That(member is null || !member.Has(ctx.Schema.Parent), $"Wolf-{n} has a parent before the aggregate arrived");
            }

            var ned = ctx.NedBetween(8.5, 9.0);
            var masters = Select<INedMessage, NedEntityMaster>(ned);
            v.Equal(3, masters.Count, "NED masters of the three individuals");
            v.Equal(6, ned.Count, "NED messages (3 masters + 3 geo-spatials)");
            v.Note("before the aggregate NED knows the three platforms as individuals: ids " + string.Join(",", masters.Select(m => m.Message.Id)));
            return v.Result();
        });
        a.Inject(9.5, "Wolf: RprAggregateEntity listing the DIS identifiers 1, 2, 3 and 4 (member 4 does not exist yet)", h => h.SendRpr(WolfAggregate()));
        a.Check(9.9, "S-3.4", ctx =>
        {
            var v = new Verdict();
            var unit = ctx.RprEntity(WolfHandle);
            v.That(unit is not null, "the Wolf unit does not exist");
            if (unit is not null)
            {
                var members = new List<EntityRecord>();
                ctx.Engine.State.Query().ReferencedBy(ctx.Schema.Parent, unit.Id).CopyTo(members);
                v.Equal(3, members.Count, "ReferencedBy(Structure.Parent, Wolf)");
                for (int n = 1; n <= 3; n++)
                {
                    var member = ctx.RprEntity(WolfMember(n).Value);
                    v.That(member is not null && member.TryGet(ctx.Schema.Parent, out var p) && p.AsEntity() == unit.Id, $"Wolf-{n} is linked");
                    v.Equal(n - 1, member is not null && member.TryGet(ctx.Schema.MemberIndex, out var index) ? (int)index.AsInt() : -1, $"member index of Wolf-{n}");
                }

                v.That(unit.Has(ctx.Schema.AggregateMemberIds), "the unit keeps the received member list");
            }

            var ned = ctx.NedBetween(9.5, 9.9);
            var formations = Select<INedMessage, NedFormation>(ned);
            var disposed = Select<INedMessage, NedEntityDisposed>(ned);
            var firstMasters = Select<INedMessage, NedEntityMaster>(ctx.NedBetween(8.5, 9.0));
            v.Equal(1, formations.Count, "NedFormation messages for Wolf (one, not three)");
            v.Equal(3, disposed.Count, "the three individuals are withdrawn");
            v.Equal(4, ned.Count, "NED messages in total (withdrawals + one formation)");
            v.That(disposed.Select(d => d.Message.Id).OrderBy(i => i).SequenceEqual(firstMasters.Select(m => m.Message.Id).OrderBy(i => i)),
                "the withdrawn ids are the ids the individuals were introduced with");
            if (formations.Count == 1 && unit is not null)
            {
                var f = formations[0].Message;
                v.Equal(ctx.NedIdOf(unit.Id), f.Id, "formation id is the unit's NED id");
                v.Equal("Wolf", f.Name, "formation name");
                v.Equal(NedForce.Opposing, f.Force, "formation force");
                v.That(f.Members.Select(m => m.Index).SequenceEqual([0, 1, 2]), "formation member indexes 0, 1, 2");
                v.That(f.Members.All(m => m.TkbType == TkbEnemyTank), "member types");
                v.Note($"one NedFormation (id {f.Id}) with {f.Members.Length} members after {disposed.Count} withdrawals");
            }

            return v.Result();
        });

        a.Inject(11.0, "Wolf-4: RPR platform object (the member the aggregate already listed)", h => h.SendRpr(WolfPlatform(4)));
        a.Check(11.4, "S-3.5", ctx =>
        {
            var v = new Verdict();
            var unit = ctx.RprEntity(WolfHandle);
            var member = ctx.RprEntity(WolfMember(4).Value);
            v.That(member is not null && unit is not null && member.TryGet(ctx.Schema.Parent, out var p) && p.AsEntity() == unit.Id, "Wolf-4 is linked to Wolf");
            v.Equal(3, member is not null && member.TryGet(ctx.Schema.MemberIndex, out var index) ? (int)index.AsInt() : -1, "member index of Wolf-4");
            var ned = ctx.NedBetween(11.0, 11.4);
            var formations = Select<INedMessage, NedFormation>(ned);
            v.Equal(1, formations.Count, "NedFormation messages (one updated formation)");
            if (formations.Count == 1)
            {
                v.That(formations[0].Message.Members.Select(m => m.Index).SequenceEqual([0, 1, 2, 3]), "formation member indexes 0, 1, 2, 3");
                v.Equal(unit is null ? 0UL : ctx.NedIdOf(unit.Id), formations[0].Message.Id, "formation id");
            }

            var masters = Select<INedMessage, NedEntityMaster>(ned);
            var disposed = Select<INedMessage, NedEntityDisposed>(ned);
            v.That(masters.Select(m => m.Message.Id).OrderBy(i => i).SequenceEqual(disposed.Select(d => d.Message.Id).OrderBy(i => i)),
                "an individual introduction of the late member is withdrawn again");
            v.Note($"{masters.Count} individual introduction(s) of Wolf-4 withdrawn within the tick (DD-002 section 10: the linker runs after the creation record)");
            return v.Result();
        });
    }

    // ---- Act 4 - movement and timing ----------------------------------------------------------------------------------------

    /// <summary>Hawk's reported state at a runner time: 6.5 s of flight after the first report (8 s), the turn at 40 s (south).</summary>
    private static Geo HawkAt(double time) => time switch
    {
        8.0 => HawkStart.After(World(8.0) - World(1.0), 90.0, 50.0),
        40.0 => HawkAt(8.0).After(World(40.0) - World(8.0), 180.0, 50.0),
        _ => throw new ArgumentOutOfRangeException(nameof(time)),
    };

    private void Act4(ActBuilder a)
    {
        for (int k = 0; k < ViperReportTimes.Length; k++)
        {
            int index = k + 1;
            a.Inject(ViperReportTimes[k], "Viper: members report their state (DR threshold exceeded)", h => h.SendNed(ViperMessage(ViperStates[index])));
        }

        a.Inject(40.0, "Hawk: turns south", h => h.SendNed(HawkAt(40.0).Message(HawkId)));
        foreach (double t in new[] { 25.0, 35.0, 45.0, 55.0 })
        {
            a.Inject(t, "Gate: unchanged geo-spatial re-sent", h => h.SendNed(GateGeo.Message(GateId)));
        }

        var movedT1 = T1Geo with { Lat = T1Geo.Lat + 0.0001 };
        a.Inject(48.0, "T-1: the tank repositions", h =>
        {
            NedRprConversions.GeoToPivot(movedT1.Lat, movedT1.Lon, movedT1.Alt, movedT1.Heading, 0, 0, 0, out var p, out _, out _);
            h.SendRpr(new RprObjectUpdate(new RprObjectHandle(T1Handle)) { WorldLocation = NedRprConversions.ToWorldLocation(p) });
        });

        a.Check(59.9, "S-4.1", HawkHeartbeats);
        a.Check(59.9, "S-4.2", DisplayRates);

        // S-4.3 around the re-send at 45 s
        a.Do(44.9, "note the rule counters and the position version of Gate", ctx =>
        {
            ctx.Remember("spatial-rules", SpatialRuleCounts(ctx));
            ctx.Remember("gate-position-version", ctx.NedEntity(GateId)!.TryGetSlot(ctx.Schema.Position, out var slot) ? (long)slot.Meta.Version : -1L);
        });
        a.Check(45.4, "S-4.3", ctx =>
        {
            var v = new Verdict();
            var gate = ctx.NedEntity(GateId);
            v.That(gate is not null, "Gate does not exist");
            if (gate is null)
            {
                return v.Result();
            }

            double from = 45.0 - 0.5 * ScenarioRunner.Dt;
            var written = ctx.Probe.PositionWritten.Where(o => o.WallTime >= from && o.WallTime <= 45.4).ToList();
            var changed = ctx.Probe.SpatialChanged.Where(o => o.WallTime >= from && o.WallTime <= 45.4).ToList();
            v.Equal(1, written.Count, "Probe/SpatialWritten (OnAttributesWritten) firings in [45.0, 45.4]");
            v.That(written.All(o => o.Entity == gate.Id), "the write was of Gate");
            v.Equal(0, changed.Count, "Probe/SpatialChanged (OnAttributesChanged) firings in [45.0, 45.4]");
            var before = ctx.Recall<Dictionary<string, long>>("spatial-rules");
            var after = SpatialRuleCounts(ctx);
            foreach (var (name, count) in before)
            {
                v.Equal(count, after[name], $"fire count of {name}");
            }

            v.Equal(ctx.Recall<long>("gate-position-version"),
                gate.TryGetSlot(ctx.Schema.Position, out var slot) ? (long)slot.Meta.Version : -1L, "version of Gate's Spatial.Position");
            var rpr = ctx.RprPayloadBetween(45.0, 45.4);
            v.That(rpr.All(r => r.Message is RprObjectUpdate u && IsHeartbeat(u)), "RPR got something else than heartbeats");
            v.Equal(0, ctx.NedBetween(45.0, 45.4).Count, "NED messages");
            v.Note($"{before.Count} OnAttributesChanged rules on Spatial.Position stayed silent ({rpr.Count} heartbeat(s) in the window)");
            return v.Result();
        });

        a.Check(59.0, "S-4.4", ctx =>
        {
            var v = new Verdict();
            var hawk = ctx.NedEntity(HawkId);
            var s = ctx.Schema;
            v.That(hawk is not null, "Hawk does not exist");
            if (hawk is null)
            {
                return v.Result();
            }

            AttrId[] spatial = [s.Position, s.Velocity, s.Orientation, s.DrAlgorithm, s.StateTime];
            foreach (var attr in spatial)
            {
                v.That(hawk.TryGetSlot(attr, out var slot) && slot.Meta.Source == ctx.Host.NedSource,
                    $"{ctx.Engine.Attributes[attr].Name} of Hawk was last written by " + (hawk.TryGetSlot(attr, out var sl) ? ctx.Engine.Sources.NameOf(sl.Meta.Source) : "nobody"));
            }

            int nedReports = ctx.InputsOf<NedGeoSpatial>("NED").Count(i => ((NedGeoSpatial)i.Message).Id == HawkId);
            v.Equal(nedReports, hawk.TryGetSlot(s.Position, out var position) ? (int)position.Meta.Version : -1,
                "version of Hawk's Spatial.Position (one per NED report)");
            v.That(hawk.TryGetSlot(s.PredictedPosition, out var predicted) && predicted.Meta.Source == SourceNamed(ctx, "proc.dr"),
                "Derived.PredictedPosition source");
            v.That(hawk.TryGetSlot(s.PredictedPosition, out var pp) && pp.Meta.Version > 100, "predictions are written continuously");
            v.That(hawk.TryGet(s.PredictedPosition, out var pv) && hawk.TryGet(s.Position, out var pos) && pv.AsVec3() != pos.AsVec3(),
                "the prediction differs from the received position");

            // no change set of the DR processor writes anything but Derived.*, and every write of Hawk's Spatial.* came from NED
            var dr = SourceNamed(ctx, "proc.dr");
            int drRecords = 0;
            foreach (var record in ctx.Engine.State.Journal.Since(0))
            {
                foreach (var delta in record.Entities)
                {
                    foreach (var change in delta.Changes)
                    {
                        if (record.Source == dr)
                        {
                            drRecords++;
                            v.That(change.Attr == s.PredictedPosition || change.Attr == s.PredictedAt,
                                $"proc.dr wrote {ctx.Engine.Attributes[change.Attr].Name}");
                        }
                        else if (delta.Id == hawk.Id && spatial.Contains(change.Attr))
                        {
                            v.That(record.Source == ctx.Host.NedSource, $"{ctx.Engine.Attributes[change.Attr].Name} of Hawk written by {ctx.Engine.Sources.NameOf(record.Source)}");
                        }
                    }
                }
            }

            v.Note($"{nedReports} NED reports = Position version {position.Meta.Version}; {drRecords} derived writes by proc.dr, none outside Derived.*");
            return v.Result();
        });

        // S-4.5: the queries at t = 50 s
        a.Check(50.0, "S-4.5", WindowQueries);
    }

    private static Dictionary<string, long> SpatialRuleCounts(ScenarioContext ctx)
    {
        var counts = new Dictionary<string, long>();
        foreach (var rule in ctx.Engine.Rules)
        {
            if (rule.Trigger.StartsWith("OnAttributesChanged(", StringComparison.Ordinal) && rule.Trigger.Contains("Spatial.Position", StringComparison.Ordinal))
            {
                counts[rule.FullName] = rule.FireCount;
            }
        }

        return counts;
    }

    private static CheckResult HawkHeartbeats(ScenarioContext ctx)
    {
        var v = new Verdict();
        var hawk = ctx.NedEntity(HawkId);
        var handle = hawk is null ? null : ctx.HandleOf(hawk.Id);
        v.That(handle is not null, "Hawk has no RPR handle");
        if (handle is null)
        {
            return v.Result();
        }

        var all = UpdatesOf(ctx.RprOf<RprObjectUpdate>(0, 59.9), handle.Value);
        var window = all.Where(r => r.Tick >= ScenarioContext.TickOf(20.0)).ToList();
        var partials = window.Where(r => !IsHeartbeat(r.Message)).ToList();
        var beats = window.Where(r => IsHeartbeat(r.Message)).ToList();
        v.Equal(1, partials.Count, "partial Hawk updates in [20, 60) (only the turn)");
        if (partials.Count == 1)
        {
            v.Equal(ScenarioContext.TickOf(40.0), partials[0].Tick, "tick of the partial update (the turn at 40 s)");
            var u = partials[0].Message;
            v.That(u.WorldLocation is not null && u.Orientation is not null && u.VelocityVector is not null && u.Marking is null && u.EntityIdentifier is null,
                "the turn is a partial update of the spatial group (" + Shape(u) + ")");
        }

        v.That(beats.Count >= 6, $"heartbeats in [20, 60): {beats.Count}");
        double worst = 0, maxOffset = 0;
        foreach (var beat in beats)
        {
            if (!TryReportOfEntity(ctx, HawkId, beat.Tick, out var report))
            {
                v.That(false, "no NED report of Hawk before a heartbeat");
                continue;
            }

            var truth = report.At(beat.SimTime);
            var location = Ecef(beat.Message.WorldLocation ?? default);
            worst = Math.Max(worst, Dist(location, truth));
            maxOffset = Math.Max(maxOffset, Dist(location, report.Position));
        }

        v.That(worst < 1.0, F($"a heartbeat is {worst:F3} m from the analytic position"));
        v.That(maxOffset > 50.0, F($"heartbeats are predicted to now (largest offset from the stored position {maxOffset:F1} m)"));

        // the rhythm: every update restarts the 5 s timer; the check runs once a second, so 5 s <= gap < 6 s
        for (int i = 1; i < all.Count; i++)
        {
            if (all[i].Tick < ScenarioContext.TickOf(10.0))
            {
                continue;
            }

            double gap = all[i].WallTime - all[i - 1].WallTime;
            v.That(gap <= 6.0 + 1e-6, F($"a gap of {gap:F3} s before the update at r={all[i].WallTime:F3}"));
            if (IsHeartbeat(all[i].Message))
            {
                v.That(gap >= 5.0 - 1e-6, F($"a heartbeat {gap:F3} s after the previous update at r={all[i].WallTime:F3}"));
            }
        }

        // what the RPR federate received: one reflection of Hawk's Spatial per update the gateway sent (a heartbeat is a full update), the
        // last one in the tick of the last update, and what it holds is what the gateway said (the turn and the heartbeats re-encode the whole variant)
        var seenHawk = ctx.Peer.DiscoveredObject(handle.Value.Value);
        v.That(seenHawk is not null, "the RPR federate does not hold Hawk");
        if (seenHawk is not null && all.Count > 0)
        {
            v.Equal(all.Count, seenHawk.Count("Spatial"), "reflections of Hawk's Spatial at the RPR federate (one per update sent)");
            v.Equal(all[^1].Tick, seenHawk.LastReflectTick, "tick of the last reflection of Hawk at the RPR federate");
            v.Equal(all.Count(r => r.Message.Marking is not null), seenHawk.Count("Marking"), "reflections of Hawk's Marking (one per full update: the first and the heartbeats)");
            PeerHoldsWhatWasSent(v, ctx, handle.Value, "Hawk");
        }

        v.Note(F($"turn at tick {(partials.Count == 1 ? partials[0].Tick : -1)}, {beats.Count} heartbeats, worst distance from the analytic position {worst:F6} m, largest prediction offset {maxOffset:F1} m"));
        return v.Result();
    }

    private static CheckResult DisplayRates(ScenarioContext ctx)
    {
        var v = new Verdict();
        var window = ctx.DisplayOf<DisplayEntityPosition>(20.0, 59.9);
        long ticks = ScenarioContext.TickOf(59.9) - ScenarioContext.TickOf(20.0) + 1;
        double seconds = ticks / 60.0;
        var notes = new List<string>();
        foreach (string label in new[] { "Hawk", "Viper-0", "Viper-1", "Viper-2" })
        {
            var own = window.Where(p => p.Message.Label == label).ToList();
            v.That(Math.Abs(own.Count - 10.0 * seconds) <= 3, F($"{label}: {own.Count} display positions in {seconds:F2} s (expected {10.0 * seconds:F0} at 10 Hz)"));
            double worst = 0;
            for (int i = 0; i < own.Count; i++)
            {
                if (i > 0)
                {
                    double gap = own[i].WallTime - own[i - 1].WallTime;
                    v.That(gap > 0.08 && gap < 0.13, F($"{label}: interval {gap:F4} s at r={own[i].WallTime:F3}"));
                }

                bool known = label == "Hawk"
                    ? TryReportOfEntity(ctx, HawkId, own[i].Tick, out var report)
                    : TryReportOfMember(ctx, ViperId, label[^1] - '0', own[i].Tick, out report);
                if (!known)
                {
                    v.That(false, $"{label}: no NED report before r={own[i].WallTime:F3}");
                    continue;
                }

                worst = Math.Max(worst, Dist(DisplayEcef(own[i].Message), report.At(own[i].SimTime)));
            }

            v.That(worst < 0.01, F($"{label}: a display position is {worst:F5} m from the analytic prediction"));
            notes.Add(F($"{label} {own.Count} (worst {worst * 1000:F3} mm)"));
        }

        v.Equal(0, window.Count(p => p.Message.Label == "Gate"), "display positions of Gate in [20, 60) (static)");
        v.Equal(1, ctx.Display.Messages.Count(r => r.Message is DisplayEntityPosition { Label: "Gate" }), "display positions of Gate since the start (static: only once)");
        v.Note(string.Join(", ", notes));
        return v.Result();
    }

    private static CheckResult WindowQueries(ScenarioContext ctx)
    {
        var v = new Verdict();
        var s = ctx.Schema;
        var state = ctx.Engine.State;
        // what the script changed in [47, 50]: formation and position messages (a marking-only refresh writes but changes nothing)
        var updated = new HashSet<string>();
        foreach (var input in ctx.Inputs.Where(i => i.RunnerTime >= 47.0 - 1e-9 && i.RunnerTime <= 50.0 + 1e-9))
        {
            switch (input.Message)
            {
                case NedFormation f:
                    foreach (var m in f.Members)
                    {
                        updated.Add(f.Name + "-" + m.Index);
                    }

                    break;
                case NedGeoSpatial g:
                    updated.Add(NameOf(ctx, ctx.NedEntity(g.Id)!));
                    break;
                case RprObjectUpdate { WorldLocation: not null } r:
                    updated.Add(NameOf(ctx, ctx.RprEntity(r.Handle.Value)!));
                    break;
            }
        }

        var position = new List<EntityRecord>();
        state.Query().AttributeChangedWithin(s.Position, TimeSpan.FromSeconds(3)).CopyTo(position);
        var changed = new List<EntityRecord>();
        state.Query().ChangedWithin(TimeSpan.FromSeconds(3)).CopyTo(changed);
        // every entity whose Derived.* prediction changed in the window (the DR processor writes the moving entities, R-029)
        var predicted = new List<EntityRecord>();
        state.Query().AttributeChangedWithin(s.PredictedPosition, TimeSpan.FromSeconds(3)).CopyTo(predicted);
        var predictedAt = new List<EntityRecord>();
        state.Query().AttributeChangedWithin(s.PredictedAt, TimeSpan.FromSeconds(3)).CopyTo(predictedAt);
        var derived = predicted.Concat(predictedAt).Select(e => NameOf(ctx, e)).ToHashSet();
        var movingNames = new List<EntityRecord>();
        state.Query().Where(e => e.Has(s.Name) && e.TryGet(s.Velocity, out var vel) && vel.AsVec3().Length > 0).CopyTo(movingNames);
        var moving = movingNames.Select(e => NameOf(ctx, e)).ToHashSet();
        v.That(moving.IsSubsetOf(derived), "every moving entity has a changed Derived.* prediction in the window");

        // S-4.5 (R-113)
        v.Equal(Names(updated), Names(position.Select(e => NameOf(ctx, e))), "AttributeChangedWithin(Spatial.Position, 3 s): exactly the entities the script updated in [47, 50]");
        var expectedChanged = updated.Concat(derived).ToHashSet();
        v.Equal(Names(expectedChanged), Names(changed.Select(e => NameOf(ctx, e))), "ChangedWithin(3 s): the updated entities plus every entity whose Derived.* prediction changed");

        var rpr = ctx.Host.RprSource;
        var silent = new List<EntityRecord>();
        state.Query().FromOrigin(rpr).NotWrittenWithin(TimeSpan.FromSeconds(10)).CopyTo(silent);
        var silentOrigin = new List<EntityRecord>();
        state.Query().FromOrigin(rpr).NotOriginWrittenWithin(TimeSpan.FromSeconds(10)).CopyTo(silentOrigin);
        v.Equal(Names(["Wolf-1", "Wolf-2"]), Names(silent.Select(e => NameOf(ctx, e))), "NotWrittenWithin(10 s).FromOrigin(RPR)");
        v.Equal(Names(["Wolf-1", "Wolf-2"]), Names(silentOrigin.Select(e => NameOf(ctx, e))), "NotOriginWrittenWithin(10 s).FromOrigin(RPR)");
        v.Note($"updated {Names(updated)}; derived {Names(derived)}; silent RPR entities {Names(silent.Select(e => NameOf(ctx, e)))}");
        return v.Result();
    }
}
