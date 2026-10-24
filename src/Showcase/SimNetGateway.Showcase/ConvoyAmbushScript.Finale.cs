using SimNetGateway.Common;
using SimNetGateway.Engine;
using SimNetGateway.Gateway.NedRpr;
using SimNetGateway.Protocol.Display;
using SimNetGateway.Protocol.Ned;
using SimNetGateway.Protocol.Rpr;

namespace SimNetGateway.Showcase;

// Acts 9 - 11: losses and timeouts, unload, debrief replay.
public sealed partial class ConvoyAmbushScript
{
    // ---- Act 9 - losses and timeouts -----------------------------------------------------------------------------------------

    private void Act9(ActBuilder a)
    {
        a.Do(99.9, "note member 1's handle and DIS identifier and the DIS pool", ctx =>
        {
            var m1 = ctx.NedMember(ViperId, 1)!;
            ctx.Remember("m1-handle", ctx.HandleOf(m1.Id)!.Value);
            ctx.Remember("m1-dis", ctx.DisIdOf(m1.Id)!.Value);
            ctx.Remember("quarantine-before", ctx.DisPool.InQuarantine);
            ctx.Remember("in-use-before", ctx.DisPool.InUse);
        });
        a.Inject(100.0, "Viper: NedFormation without member 1 (destroyed)", h => h.SendNed(ViperMessage(ViperStates[8], indexes: [0, 2])));
        a.Check(100.6, "S-9.1", ctx =>
        {
            var v = new Verdict();
            var handle = ctx.Recall<RprObjectHandle>("m1-handle");
            var removed = ctx.RprOf<RprObjectRemoved>(100.0, 100.6);
            v.Equal(1, removed.Count, "RprObjectRemoved messages");
            v.That(removed.Count == 1 && removed[0].Message.Handle == handle, "the removal is for member 1's handle");
            v.Equal(0, UpdatesOf(ctx.RprOf<RprObjectUpdate>(100.0, 100.6), handle).Count, "updates for member 1's handle");
            v.Equal(1, RprEvents(ctx, 100.0, 100.6).Count(r => r.Message is not RprObjectUpdate), "RPR messages other than updates (the one removal)");
            var markers = ctx.DisplayOf<DisplayEntityRemoved>(100.0, 100.6);
            v.Equal(1, markers.Count, "display removals");
            v.That(markers.Count == 1 && markers[0].Message.Label == "Viper-1", "the removed marker is Viper-1");
            v.Equal(0, ctx.DisplayOf<DisplayEntityPosition>(100.2, 100.6).Count(p => p.Message.Label == "Viper-1"), "positions of Viper-1 after the removal");
            v.That(ctx.NedMember(ViperId, 1) is null, "ned.member still resolves (7, 1)");
            v.That(!ctx.Host.RprHandles.TryResolve(handle, out _), "rpr.handle still resolves member 1's handle");
            v.That(!ctx.Host.RprEntityIds.TryResolve(ctx.Recall<DisEntityId>("m1-dis"), out _), "rpr.entityId still resolves member 1's identifier");
            v.Equal(2, ctx.Engine.State.Query().ReferencedBy(ctx.Schema.Parent, ctx.NedEntity(ViperId)!.Id).Count(), "members left in the unit");
            var pool = ctx.DisPool;
            v.Equal(ctx.Recall<int>("quarantine-before") + 1, pool.InQuarantine, "numbers in quarantine (member 1's number rests)");
            v.Equal(ctx.Recall<int>("in-use-before") - 1, pool.InUse, "numbers in use");
            v.Note($"member 1's DIS identifier {MessageText.Id(ctx.Recall<DisEntityId>("m1-dis"))} is in quarantine until 110 s");
            return v.Result();
        });
        a.Check(112.0, "S-9.1", ctx =>
        {
            var v = new Verdict();
            // member 1's number was released at 100 s and rests 10 s; Hawk's (released at 104 s) still rests
            v.Equal(1, ctx.DisPool.InQuarantine, "numbers in quarantine at 112 s (only Hawk's; member 1's came back at 110 s)");
            v.Note("member 1's DIS identifier left the quarantine at 110 s");
            return v.Result();
        });

        // two of the reinforcements that NED knows go away, so two ids come back
        a.Do(100.9, "note the NED ids of R-1 and R-2", ctx =>
        {
            ctx.Remember("freed-ids", new[] { ctx.NedIdOf(ctx.RprEntity(ReinforcementHandle(1))!.Id), ctx.NedIdOf(ctx.RprEntity(ReinforcementHandle(2))!.Id) });
            ctx.Remember("diagnostics", ctx.Engine.Diagnostics.Count);
        });
        a.Inject(101.0, "RPR removes the reinforcements R-1 and R-2", h =>
        {
            h.SendRpr(new RprObjectRemoved(new RprObjectHandle(ReinforcementHandle(1))));
            h.SendRpr(new RprObjectRemoved(new RprObjectHandle(ReinforcementHandle(2))));
        });
        a.Inject(102.0, "R-5 and R-6 move 3 m (RPR)", h =>
        {
            foreach (int n in new[] { 5, 6 })
            {
                NedRprConversions.GeoToPivot(50.14 + 0.00003, 14.14 + 0.0003 * n, 260.0, 270.0, 0, 0, 0, out var p, out _, out _);
                h.SendRpr(new RprObjectUpdate(new RprObjectHandle(ReinforcementHandle(n))) { WorldLocation = NedRprConversions.ToWorldLocation(p) });
            }
        });
        a.Check(102.5, "S-7.6", PublishedAfterRelease);
        a.Check(102.5, "S-9.4", PublishedAfterRelease);

        // Hawk is lost; the replacement Kestrel appears at once, while the DIS numbers of member 1 and Hawk rest in quarantine
        a.Do(103.9, "note Hawk's DIS identifier and the DIS pool", ctx =>
        {
            ctx.Remember("hawk-dis", ctx.DisIdOf(ctx.NedEntity(HawkId)!.Id)!.Value);
            ctx.Remember("hawk-handle", ctx.HandleOf(ctx.NedEntity(HawkId)!.Id)!.Value);
            ctx.Remember("available-before", ctx.DisPool.Available);
        });
        a.Inject(104.0, "NED disposes Hawk (shot down)", h => h.SendNed(new NedEntityDisposed(HawkId)));
        a.Inject(105.0, "Kestrel: master", h => h.SendNed(new NedEntityMaster(KestrelId, "Kestrel", TkbUav, NedForce.Friendly)));
        a.Inject(105.0, "Kestrel: geo-spatial", h => h.SendNed(KestrelGeo.Message(KestrelId)));
        a.Inject(105.0, "Kestrel fires F3 at once: NedWeaponFire", h =>
        {
            ulong target = h.RprHandles.TryResolve(new RprObjectHandle(T1Handle), out var t1) && h.NedIds.TryGetKey(t1, out ulong id) ? id : 0;
            h.SendNed(new NedWeaponFire(NedFireF3, KestrelId, target, TkbMunitionA, 50.03, 14.02, 600.0, 1));
        });
        a.Inject(105.0, "Kestrel fires F3 at once: NedFireAmmunition", h => h.SendNed(new NedFireAmmunition(NedFireF3, TkbMunitionA, 1)));
        a.Check(105.5, "S-9.3", ctx =>
        {
            var v = new Verdict();
            var hawkHandle = ctx.Recall<RprObjectHandle>("hawk-handle");
            var removed = ctx.RprOf<RprObjectRemoved>(104.0, 104.5);
            v.Equal(1, removed.Count, "RprObjectRemoved messages for Hawk");
            v.That(removed.Count == 1 && removed[0].Message.Handle == hawkHandle, "the removal is for Hawk's handle");
            v.Equal(1, ctx.DisplayOf<DisplayEntityRemoved>(104.0, 104.5).Count(r => r.Message.Label == "Hawk"), "display removal of Hawk");
            var kestrel = ctx.NedEntity(KestrelId);
            var id = kestrel is null ? null : ctx.DisIdOf(kestrel.Id);
            v.That(id is not null, "Kestrel has no DIS identifier");
            if (id is { } kestrelId)
            {
                v.That(kestrelId != ctx.Recall<DisEntityId>("hawk-dis"), "Kestrel got Hawk's number although it rests in quarantine");
                v.That(kestrelId != ctx.Recall<DisEntityId>("m1-dis"), "Kestrel got member 1's number although it rests in quarantine");
                v.Note($"Hawk had {MessageText.Id(ctx.Recall<DisEntityId>("hawk-dis"))}, member 1 had {MessageText.Id(ctx.Recall<DisEntityId>("m1-dis"))}, Kestrel got {MessageText.Id(kestrelId)}");
            }

            v.Equal(2, ctx.DisPool.InQuarantine, "numbers in quarantine (member 1's and Hawk's)");
            return v.Result();
        });
        a.Check(115.0, "S-9.3", ctx =>
        {
            var v = new Verdict();
            v.Equal(0, ctx.DisPool.InQuarantine, "numbers in quarantine after the quarantine");
            v.Equal(ctx.Recall<int>("available-before") - 1 + 2, ctx.DisPool.Available,
                "available numbers (Kestrel took a fresh one, member 1's and Hawk's numbers came back)");
            v.Note("both rested numbers are available again");
            v.Question("'after the quarantine a freed number may be reused' is only checkable as availability: the DIS number pool hands out never-used " +
                "numbers first (ascending), so a freed number is reused only after all 65,534 numbers were used once. Checked: the pool counts the numbers " +
                "as in quarantine until 10 s after their release and as available afterwards.");
            return v.Result();
        });
        a.Check(105.5, "S-9.5", ctx =>
        {
            var v = new Verdict();
            var kestrel = ctx.NedEntity(KestrelId);
            var handle = kestrel is null ? null : ctx.HandleOf(kestrel.Id);
            long tick = ScenarioContext.TickOf(105.0);
            var regs = ctx.RprOf<RprRegisterObject>(105.0, 105.5);
            var updates = UpdatesOf(ctx.RprOf<RprObjectUpdate>(105.0, 105.5), handle ?? default);
            var fires = ctx.RprOf<RprWeaponFire>(105.0, 105.5);
            v.Equal(1, regs.Count, "registrations");
            v.Equal(1, updates.Count, "updates of the new object");
            v.That(updates.Count == 1 && IsHeartbeat(updates[0].Message), "the update of the new object is a full update");
            v.Equal(1, fires.Count, "RprWeaponFire messages");
            if (regs.Count == 1 && updates.Count == 1 && fires.Count == 1)
            {
                v.Equal(tick, regs[0].Tick, "tick of the registration (the same tick as the fire event)");
                v.That(fires[0].Tick > tick, "the fire was sent in the tick of the event, before the handle existed");
                v.Equal(tick + 1, fires[0].Tick, "tick of the fire (when the handle arrived)");
                v.That(updates[0].Sequence < fires[0].Sequence, "the full update of the new object comes before the fire");
                v.Equal(handle, fires[0].Message.FiringObject, "firing object (Kestrel's handle, not 'no object')");
                v.That(fires[0].Message.FiringObject is not null, "the fire was sent with 'no object' for its shooter");
                v.Equal(NedFireF3, fires[0].Message.EventId, "event id");
                v.Equal(DisOf(TkbMunitionA), fires[0].Message.Munition, "munition");
                v.Note($"registration at tick {regs[0].Tick}; the fire was held and sent at tick {fires[0].Tick} with firing object {Hex(fires[0].Message.FiringObject)}");
            }

            v.Equal(0, DiagnosticsContaining(ctx, "Held fire").Count, "hold timeout diagnostics");
            return v.Result();
        });

        // Wolf-3 stops sending at 101.1 s: the reaper removes it 12 s later (at the first check after that)
        a.Check(113.5, "S-9.2", ctx =>
        {
            var v = new Verdict();
            var wolf3 = ctx.RprEntity(WolfMember(3).Value);
            v.That(wolf3 is not null, "Wolf-3 was reaped early");
            if (wolf3 is not null)
            {
                // only writes by the RPR side keep an entity alive (R-068): the dead-reckoning processor keeps writing Wolf-3's prediction
                v.That(ctx.Clock.Now - wolf3.LastWriteWallTime < 0.2, F($"the processor's derived writes stopped ({ctx.Clock.Now - wolf3.LastWriteWallTime:F3} s ago)"));
                v.That(ctx.Clock.Now - wolf3.LastOriginWriteWallTime > 12.0, F($"Wolf-3's last write by RPR is only {ctx.Clock.Now - wolf3.LastOriginWriteWallTime:F3} s old"));
                v.Note(F($"at 113.5 s the last write by RPR is {ctx.Clock.Now - wolf3.LastOriginWriteWallTime:F1} s old while a derived write came {ctx.Clock.Now - wolf3.LastWriteWallTime:F2} s ago"));
            }

            v.Equal(0, ctx.NedBetween(113.0, 113.5).Count, "NED messages before the timeout");
            return v.Result();
        });
        a.Check(114.5, "S-9.2", ctx =>
        {
            var v = new Verdict();
            var unit = ctx.RprEntity(WolfHandle);
            v.That(ctx.RprEntity(WolfMember(3).Value) is null, "Wolf-3 still exists");
            double last = ctx.Inputs.Where(i => i.Message is RprObjectUpdate u && u.Handle == WolfMember(3)).Max(i => i.RunnerTime);
            var reaped = DiagnosticsSince(ctx, 0).Where(d => d.Origin == "RprStaleReaper").ToList();
            v.Equal(1, reaped.Count, "reaper reports");
            if (reaped.Count == 1)
            {
                double age = reaped[0].WallTime - last;
                v.That(age > 12.0 && age <= 13.0 + 1e-6, F($"Wolf-3 was reaped {age:F3} s after its last update (timeout 12 s, check every second)"));
                v.Note(F($"last update r={last:F2}, reaped r={reaped[0].WallTime:F2} ({age:F2} s)"));
            }

            var ned = ctx.NedBetween(113.5, 114.5);
            var formations = Select<INedMessage, NedFormation>(ned);
            v.Equal(1, ned.Count, "NED messages");
            v.Equal(1, formations.Count, "NedFormation messages");
            if (formations.Count == 1 && unit is not null)
            {
                v.Equal(ctx.NedIdOf(unit.Id), formations[0].Message.Id, "formation id");
                v.That(formations[0].Message.Members.Select(m => m.Index).SequenceEqual([0, 1, 3]), "members of the formation (indexes 0, 1, 3: Wolf-3 is gone)");
            }

            v.Equal(1, ctx.DisplayOf<DisplayEntityRemoved>(113.5, 114.5).Count(r => r.Message.Label == "Wolf-3"), "display removal of Wolf-3");
            v.Equal(0, ctx.RprOf<RprObjectRemoved>(113.5, 114.5).Count, "RPR removals (an RPR object is not echoed)");
            return v.Result();
        });
    }

    private static CheckResult PublishedAfterRelease(ScenarioContext ctx)
    {
        var v = new Verdict();
        var freed = ctx.Recall<ulong[]>("freed-ids");
        var disposed = ctx.NedOf<NedEntityDisposed>(101.0, 101.5);
        v.Equal(2, disposed.Count, "NedEntityDisposed messages for R-1 and R-2");
        v.That(disposed.Select(d => d.Message.Id).OrderBy(i => i).SequenceEqual(freed.OrderBy(i => i)), "the disposed ids are the ids R-1 and R-2 had");
        var ned = ctx.NedBetween(102.0, 102.5);
        var masters = Select<INedMessage, NedEntityMaster>(ned);
        var geos = Select<INedMessage, NedGeoSpatial>(ned);
        v.Equal(2, masters.Count, "NedEntityMaster messages for the unpublished reinforcements");
        v.That(masters.Select(m => m.Message.Name).OrderBy(n => n).SequenceEqual(["R-5", "R-6"]), "the published reinforcements are R-5 and R-6");
        v.Equal(2, geos.Count, "NedGeoSpatial messages");
        v.That(masters.All(m => freed.Contains(m.Message.Id)), "the new ids are the ones that came back");
        v.Equal(4, ned.Count, "NED messages in total");
        foreach (int n in new[] { 5, 6 })
        {
            var entity = ctx.RprEntity(ReinforcementHandle(n));
            v.That(entity is not null && ctx.NedIdOf(entity.Id) != 0, $"R-{n} still has no NED id");
        }

        v.Equal(0UL, ctx.NedRange.Available, "free ids in the range");
        v.Equal(ctx.Recall<int>("diagnostics"), ctx.Engine.Diagnostics.Count, "new diagnostics (no second allocation failure)");
        v.Note("2 ids freed at 101 s, R-5 and R-6 published by their next update at 102 s");
        return v.Result();
    }

    // ---- Act 10 - unload -----------------------------------------------------------------------------------------------------

    private void Act10(ActBuilder a)
    {
        a.Do(124.9, "note everything that was published", ctx =>
        {
            var entities = ctx.Entities();
            ctx.Remember("entities", entities.Count);
            ctx.Remember("rpr-published", entities.Where(e => e.Origin == ctx.Host.NedSource && ctx.HandleOf(e.Id) is not null)
                .Select(e => ctx.HandleOf(e.Id)!.Value).ToHashSet());
            ctx.Remember("ned-published", entities.Where(e => e.Origin != ctx.Host.NedSource && ctx.NedIdOf(e.Id) != 0)
                .Select(e => ctx.NedIdOf(e.Id)).ToHashSet());
            ctx.Remember("labels", entities.Where(e => ctx.Host.DisplayLabels.TryGetKey(e.Id, out _))
                .Select(e => { ctx.Host.DisplayLabels.TryGetKey(e.Id, out string? label); return label!; }).ToHashSet());
            ctx.Remember("dis-published", entities.Count(e => e.Origin == ctx.Host.NedSource && ctx.DisIdOf(e.Id) is not null));
            ctx.Remember("last-dispatch", ctx.Probe.Dispatches[^1].Correlation);
        });
        a.Inject(UnloadAt, "NED control: Unload", h => h.SendNed(new NedSimControl(NedControlKind.Unload, null)));
        a.Check(126.5, "S-10.1", ctx =>
        {
            var v = new Verdict();
            var expectedRpr = ctx.Recall<HashSet<RprObjectHandle>>("rpr-published");
            var removed = ctx.RprOf<RprObjectRemoved>(UnloadAt, 126.5);
            v.Equal(expectedRpr.Count, removed.Count, "RprObjectRemoved messages");
            v.That(removed.Select(r => r.Message.Handle).ToHashSet().SetEquals(expectedRpr),
                "the removed handles are not exactly the objects the gateway had published to RPR: " + string.Join(",", expectedRpr.Select(h => MessageText.H(h)).OrderBy(s => s)));
            v.That(ctx.RprPayloadBetween(UnloadAt, 126.5).All(r => r.Message is RprObjectRemoved), "RPR got something else than removals (not even a heartbeat after the unload)");

            var expectedNed = ctx.Recall<HashSet<ulong>>("ned-published");
            var disposed = ctx.NedOf<NedEntityDisposed>(UnloadAt, 126.5);
            v.Equal(expectedNed.Count, disposed.Count, "NedEntityDisposed messages");
            v.That(disposed.Select(d => d.Message.Id).ToHashSet().SetEquals(expectedNed), "the disposed ids are not exactly the ids the gateway introduced to NED");
            v.Equal(disposed.Count, ctx.NedBetween(UnloadAt, 126.5).Count, "NED messages in total (only disposals)");

            var expectedLabels = ctx.Recall<HashSet<string>>("labels");
            var markers = ctx.DisplayOf<DisplayEntityRemoved>(UnloadAt, 126.5);
            v.Equal(expectedLabels.Count, markers.Count, "DisplayEntityRemoved messages");
            v.That(markers.Select(m => m.Message.Label).ToHashSet().SetEquals(expectedLabels), "the removed markers are not exactly the markers on the display");
            v.Note($"{removed.Count} RPR objects, {disposed.Count} NED ids (T-1, the Wolf unit, R-3 .. R-6), {markers.Count} display markers removed");
            return v.Result();
        });
        a.Check(126.5, "S-10.2", ctx => EmptyState(ctx, ctx.Recall<int>("dis-published"), UnloadAt));
        a.Check(126.5, "S-10.3", ctx =>
        {
            var v = new Verdict();
            var journal = ctx.Engine.State.Journal;
            v.That(journal.Count > 1000, $"the journal holds only {journal.Count} records");
            int entities = ctx.Recall<int>("entities");
            bool found = false;
            foreach (var record in journal.Since(UnloadAt - 0.5 * ScenarioRunner.Dt))
            {
                if (record.Source == SourceRegistry.Engine)
                {
                    int destroyed = 0;
                    foreach (var delta in record.Entities)
                    {
                        destroyed += delta.Destroyed ? 1 : 0;
                    }

                    found |= destroyed == entities;
                }
            }

            v.That(found, $"the journal has no engine record that destroyed the {entities} entities");
            string? detonation = ctx.Probe.Order.FirstOrDefault(o => o.StartsWith("damage:", StringComparison.Ordinal) && o.EndsWith(":3", StringComparison.Ordinal));
            v.That(detonation is not null && journal.TryGet(new CorrelationId(long.Parse(detonation.Split(':')[1], System.Globalization.CultureInfo.InvariantCulture)), out _),
                "the record of act 5's detonation is gone from the journal");
            var events = new Dictionary<Guid, string>();
            foreach (var ev in journal.RecentEvents)
            {
                if (ev.Payload.TryGet(ctx.Schema.EventId, out var id))
                {
                    events[id.AsObject<Guid>()] = ctx.Engine.EventTypes.NameOf(ev.Type);
                }
            }

            foreach (var id in new[] { RprFireE1, NedFireF1, NedFireF2, NedFireF3 })
            {
                v.Equal("WeaponFire", events.GetValueOrDefault(id) ?? "-", "event log entry of " + MessageText.Short(id));
            }

            v.Equal("Detonation", events.GetValueOrDefault(NedDetonationD1) ?? "-", "event log entry of the detonation");
            v.Note($"{journal.Count} records and {journal.EventCount} events retained, among them the unload record and the events of acts 5 and 9");
            return v.Result();
        });
    }

    /// <summary>S-10.2 / S-11.3: the repository and every map are empty, every allocator has its ids back, the session is Idle.</summary>
    private static CheckResult EmptyState(ScenarioContext ctx, int releasedDisNumbers, double unloadedAt)
    {
        var v = new Verdict();
        v.Equal(0, ctx.Engine.State.Count, "entities in the repository");
        foreach (var map in ctx.Engine.Identities.All)
        {
            v.Equal(0, map.Count, $"bindings of {map.Name}");
        }

        var range = ctx.NedRange;
        v.Equal(0UL, range.InUse, "NED ids in use");
        v.Equal(range.Capacity, range.Available, "NED ids available");
        var pool = ctx.DisPool;
        v.Equal(0, pool.InUse, "DIS numbers in use");
        v.Equal(releasedDisNumbers, pool.InQuarantine, "DIS numbers in quarantine (the numbers of the entities just unloaded)");
        v.Equal(pool.Capacity, pool.InUse + pool.InQuarantine + pool.Available, "DIS numbers: in use + in quarantine + available");
        v.Equal(SessionState.Idle, ctx.Engine.Session.State, "session state");
        v.Equal(SessionMode.Live, ctx.Engine.Session.Mode, "session mode");
        v.That(Math.Abs(ctx.SimTime - (ctx.RunnerTime - unloadedAt)) < 0.02,
            F($"sim time {ctx.SimTime:F3} s was reset by the unload (expected {ctx.RunnerTime - unloadedAt:F3} s: Idle runs free again)"));
        v.Note($"0 entities, all maps empty, NED ids {range.Available}/{range.Capacity} back, DIS numbers {pool.InQuarantine} in quarantine and {pool.Available} available, Idle");
        return v.Result();
    }

    // ---- Act 11 - debrief replay ---------------------------------------------------------------------------------------------

    private void ReplayNed(NedRprHost h, int index)
    {
        switch (_recording[index])
        {
            case NedFormation m: h.SendNed(m); break;
            case NedEntityMaster m: h.SendNed(m); break;
            case NedGeoSpatial m: h.SendNed(m); break;
            case NedWeaponStatus m: h.SendNed(m); break;
            case NedDetonation m: h.SendNed(m); break;
            case NedWeaponFire m: h.SendNed(m); break;
            case NedFireAmmunition m: h.SendNed(m); break;
            default: throw new InvalidOperationException("Cannot replay " + _recording[index].GetType().Name);
        }
    }

    private void Act11(ActBuilder a)
    {
        a.Do(126.9, "note the correlation at the end of the live session", ctx => ctx.Remember("live-end", ctx.Probe.Dispatches[^1].Correlation));
        a.Inject(ReplayLoadAt, "NED control: LoadDebrief 'Ambush-recording'", h => h.SendNed(new NedSimControl(NedControlKind.LoadDebrief, "Ambush-recording")));
        a.Check(127.5, "S-11.1", ctx =>
        {
            var v = new Verdict();
            v.Equal(SessionState.Loaded, ctx.Engine.Session.State, "session state");
            v.Equal(SessionMode.Replay, ctx.Engine.Session.Mode, "session mode");
            v.Equal("Ambush-recording", ctx.Engine.Session.Scenario, "scenario");
            v.Equal(0.0, ctx.SimTime, "sim time");
            v.Note("Loaded, Mode = Replay");
            return v.Result();
        });
        a.Inject(128.0, "NED control: Start", h => h.SendNed(new NedSimControl(NedControlKind.Start, null)));

        // the recorded stream: the NED inputs of acts 3 - 5 (the convoy, the checkpoint, the weapon status, the detonation, the split fire F1)
        a.Inject(128.5, "replay: Viper formation", h => ReplayNed(h, 0));
        a.Inject(128.5, "replay: Gate master", h => ReplayNed(h, 1));
        a.Inject(128.5, "replay: Gate geo-spatial", h => ReplayNed(h, 2));
        a.Inject(129.5, "replay: weapon status of Viper member 2", h => ReplayNed(h, 3));
        a.Inject(130.5, "replay: detonation on Viper member 1", h => ReplayNed(h, 4));
        a.Inject(131.5, "replay: fire F1, first part", h => ReplayNed(h, 5));
        a.Inject(131.6167, "replay: fire F1, ammunition part", h => ReplayNed(h, 6));
        a.Check(129.0, "S-11.1", ctx =>
        {
            var v = new Verdict();
            var go = ctx.RprOf<RprSimulationControl>(128.0, 129.0);
            v.That(go.Count == 1 && go[0].Message.Kind == RprControlKind.StartResume, "RPR did not get exactly one StartResume at Start");
            var updates = ctx.RprOf<RprObjectUpdate>(128.5, 129.0);
            v.Equal(4, updates.Count, "full RPR updates (3 members + Gate)");
            v.That(updates.All(u => IsHeartbeat(u.Message)), "the updates are not complete");
            v.Equal(4, ctx.RprOf<RprRegisterObject>(128.5, 129.0).Count, "registrations");
            var labels = ctx.DisplayOf<DisplayEntityPosition>(128.5, 129.0).Select(p => p.Message.Label).Distinct().ToList();
            foreach (string label in new[] { "Viper-0", "Viper-1", "Viper-2", "Gate" })
            {
                v.That(labels.Contains(label), $"no display position for '{label}'");
            }

            v.That(ctx.NedMember(ViperId, 2) is not null && ctx.NedEntity(GateId) is not null, "the replayed entities do not exist");
            v.Note("entities and their states reach RPR and the display as in the live session");
            return v.Result();
        });
        a.Check(131.0, "S-11.1", ctx =>
        {
            var v = new Verdict();
            var handle = MemberHandle(ctx, 1);
            var damage = ctx.RprOf<RprObjectUpdate>(130.5, 131.0).Where(u => u.Message.Handle == handle).ToList();
            v.Equal(1, damage.Count, "RPR updates of member 1 after the replayed detonation");
            // the operator override of act 6 is still in force (rule states are not session state): damage goes out in percent
            v.That(damage.Count == 1 && damage[0].Message.DamagePercent == 100 && Shape(damage[0].Message) == "DamagePercent",
                "member 1's destroyed state (100 percent: the act 6 override is still active) did not reach RPR");
            v.Note("the replayed detonation's damage reaches RPR as a state (as 100 percent, the override of act 6 is still active)");
            return v.Result();
        });
        a.Check(133.0, "S-11.2", ctx =>
        {
            var v = new Verdict();
            v.Equal(0, ctx.RprOf<RprWeaponFire>(ReplayLoadAt, 133.0).Count, "RprWeaponFire messages during the replay");
            v.Equal(0, ctx.RprOf<RprMunitionDetonation>(ReplayLoadAt, 133.0).Count, "RprMunitionDetonation messages during the replay");
            long boundary = ctx.Recall<long>("live-end");
            var seen = new Dictionary<Guid, string>();
            foreach (var ev in ctx.Engine.State.Journal.RecentEvents)
            {
                if (ev.Correlation.Value > boundary && ev.Payload.TryGet(ctx.Schema.EventId, out var id))
                {
                    seen[id.AsObject<Guid>()] = ctx.Engine.EventTypes.NameOf(ev.Type);
                }
            }

            v.Equal("WeaponFire", seen.GetValueOrDefault(NedFireF1) ?? "-", "the replayed fire F1 in the event log");
            v.Equal("Detonation", seen.GetValueOrDefault(NedDetonationD1) ?? "-", "the replayed detonation in the event log");
            var members = Enumerable.Range(0, 3).Select(i => ctx.NedMember(ViperId, i)).ToList();
            v.Equal(28L, members[2] is not null && members[2]!.TryGet(ctx.Schema.Ammo, out var ammo) ? ammo.AsInt() : -1, "ammunition of member 2 after the replayed fire (the events still change state)");
            v.Note("the replayed fire and detonation were dispatched and logged, nothing weapon-related reached RPR");
            return v.Result();
        });

        a.Do(134.9, "note the entities the second unload releases", ctx =>
            ctx.Remember("dis-published", ctx.Entities().Count(e => e.Origin == ctx.Host.NedSource && ctx.DisIdOf(e.Id) is not null)));
        a.Inject(ReplayUnloadAt, "NED control: Unload", h => h.SendNed(new NedSimControl(NedControlKind.Unload, null)));
        a.Check(136.5, "S-11.3", ctx => EmptyState(ctx, ctx.Recall<int>("dis-published"), ReplayUnloadAt));
    }
}
