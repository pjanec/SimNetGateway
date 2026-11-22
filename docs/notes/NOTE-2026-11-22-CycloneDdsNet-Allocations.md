# CycloneDDS.NET 1.2.20: where the allocations are (T-056)

Measured 2026-11-22 with the NuGet package `CycloneDDS.NET` 1.2.20 (the one the gateway references). Its source is tag `v1.2.20` = commit 416c68b (`/home/user/pjanec/cyclonedds.net`); the generated marshalling code in the gateway's `obj/` is character-for-character what `tools/CycloneDDS.CodeGen/SerializerEmitter.cs` emits at that commit, so package and source agree.
Method: `GC.GetAllocatedBytesForCurrentThread()` over 300-2000 iterations after a warm-up, .NET 8, Linux.

## 1. Empty `DdsReader<T>.Take` / `Read`: 0 B (no upstream issue)

| case | bytes per call |
|---|---|
| empty `Take(32)` / `Take(1)` / `Read(32)`, unmanaged `Leave`, managed `JoinRequest`, managed `GizmoFrame` | 0 |
| reader with `DdsReaderOptions.LivelinessLeaseDuration` | 0 |
| plus a `LivelinessChanged` handler (listener attached) | 0 |
| with a matched writer, after a data take, 3 500 takes over 4 s with liveliness traffic | 0.01 |
| `ArrayPool<IntPtr>` + `ArrayPool<DdsSampleInfo>` rent/clear/return | 0 |
| `DdsWriter.CurrentStatus.CurrentCount` | 0 |
| Write + Take of an unmanaged `Leave` | 0 |

`DdsReader.ReadOrTake` (`src/CycloneDDS.Runtime/DdsReader.cs:207-234`), `DdsLoan` (ref struct) and its `Enumerator` do not allocate, as the README says. The "16 B per empty Take" in our TASKS (T-055/T-056) was a mis-attribution.

**The real allocator was our own call site.** Bisecting the gateway's steady engine tick (3 seats, 144 B per tick): with `DpsInbound.Pump`'s three reads disabled the tick was still 144 B; with only `HandleLiveliness` it was 144 B; with only the reads, 0 B. `HandleLiveliness` has `while (_liveliness.TryDequeue(out var e)) { ... _ownerHandles.Any(h => h.Topic == e.Topic) ... }`: the lambda captures `e`, which the compiler hoists into a display class allocated each time the `while` condition is entered, including the final failing evaluation on an empty queue: 16 B header + 32 B record struct = 48 B per seat per tick = 144 B for 3 seats. Fixed in the gateway (`DpsInbound.OwnsTopic`, a loop), rail `A_steady_engine_tick_of_three_playing_seats_allocates_nothing` asserts 0 and is red with the lambda restored.

## 2. Writing a managed sample: 32 B per non-empty string (this one is the binding's)

`DdsWriter<GizmoFrame>.Write`, steady value, nothing else changing:

| primitive content | bytes per primitive |
|---|---|
| empty text, 0 / 1 / 2 / 30 points, null points | 0 |
| text `"x"` | 32 |
| text of 40 chars | 64 |
| fixed per frame: two key strings `Producer`, `Layer` (1 char each) | 64 per write |

So it is not per primitive and not per point (points go through the `NativeArena`, 0 B): it is one `byte[]` per non-empty bounded string, sized 24 B array header + UTF-8 length rounded up to 8 (1 byte -> 32 B, 40 bytes -> 64 B). Our "about 16 B per primitive" was an average over cards with mostly short or empty texts.

**The line responsible:** `tools/CycloneDDS.CodeGen/SerializerEmitter.cs:483` emits, for every `[MaxLength(n)] string`,
```csharp
var __bytes = System.Text.Encoding.UTF8.GetBytes(source.Text);          // GeneratedMarshal.MarshalToNative, e.g. GizmoPrimitive.g.cs:77
if (__bytes.Length > 64) throw new InvalidOperationException("String exceeds MaxLength 64");
fixed (byte* __dest = target.Text) { Marshal.Copy(__bytes, 0, (IntPtr)__dest, __bytes.Length); __dest[__bytes.Length] = 0; }
```
(Unbounded strings use `arena.CreateString`, which already writes without a temporary: `src/CycloneDDS.Core/NativeArena.cs:63`.)

**Proposed fix** (emit this instead; no temporary array, same exception, same bytes):
```csharp
int __n = System.Text.Encoding.UTF8.GetByteCount(source.Text);          // 0 B
if (__n > 64) throw new InvalidOperationException("String exceeds MaxLength 64");
fixed (byte* __dest = target.Text)
{
    System.Text.Encoding.UTF8.GetBytes(source.Text, new Span<byte>(__dest, __n));   // 0 B
    __dest[__n] = 0;
}
```
Verified to be 0 B per call in a stand-alone loop of the same three statements over a `fixed byte[65]` struct field. (`Encoding.GetBytes(string, Span<byte>)` needs net5+; for netstandard2.0 targets use `GetBytes(char*, int, byte*, int)`.)

## 3. Minimal repro (xunit, only CycloneDDS.NET types)

```csharp
[DdsTopic("Repro"), DdsManaged] public partial struct Repro { [DdsKey, MaxLength(32)] public string Key; [MaxLength(64)] public string Text; }

[Fact] public void Marshalling_a_bounded_string_allocates()
{
    using var p = new DdsParticipant(0);
    using var w = new DdsWriter<Repro>(p);
    var v = new Repro { Key = "k", Text = "x" };
    for (int i = 0; i < 200; i++) w.Write(v);                 // warm up
    long before = GC.GetAllocatedBytesForCurrentThread();
    for (int i = 0; i < 1000; i++) w.Write(v);
    long perWrite = (GC.GetAllocatedBytesForCurrentThread() - before) / 1000;
    Assert.Equal(0, perWrite);                                // 64 today: two 32 B byte[] (Key, Text)
}
```
An empty-`Take` companion (passes today, guards the README's 0 B claim): `using var r = new DdsReader<Repro>(p); ... { using var l = r.Take(32); }` x 1000, assert 0.

## 4. Impact for us

A card frame with a text per primitive costs 32 B per card (about 24 KB per pump for 1 500 primitives, as measured in T-059); the fix would take it to 0. Not urgent; the gateway's own paths are at 0 B.
