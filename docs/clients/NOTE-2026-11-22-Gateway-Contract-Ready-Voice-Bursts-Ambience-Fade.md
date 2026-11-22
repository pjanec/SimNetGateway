# Note: the DPS contract for voice, bursts, ambience, weather and the live fade / subtitle is IN THE IDL now — prepare against it

| | |
|---|---|
| **From** | the gateway / DPS session, 2026-11-22 |
| **For** | the HROT `unreal-ig` session (the Unreal client) |
| **IDL** | [`docs/clients/idl/DpsTopics.idl`](idl/DpsTopics.idl), [`DpsTypes.idl`](idl/DpsTypes.idl) — regenerated; every change is APPENDED (R-133): an old client keeps working |
| **Designs** | [DD-026 voice](../design/DD-026-Dps-Voice-Events.md) · [DD-027 bursts](../design/DD-027-Dps-Fire-Bursts.md) · [DD-028 ambience and weather](../design/DD-028-Dps-Ambience-And-Weather.md) · [DD-022 P-8 live fade and subtitle](../design/DD-022-Dps-Director-Rendering.md) |
| **User, verbatim** | *"write a note to the Unreal about this so it could work on that in parallel … maybe you can update the IDL right away before writing the note"* |

⭐ **The topics and fields below exist on the wire now, but nothing is written to them yet** — the gateway fills them one by one as HROT's
side lands (order below). Build and test your side against them now; a fake publisher (or `SimNetGateway.DpsClient`'s style of a small
writer) is enough to drive your code. I will add a line here as each starts flowing.

## 1. What is new

| topic | class / QoS | fields | the client |
|---|---|---|---|
| `DPS_Utterance` *(new)* | event · reliable, volatile, keep-last 32 | `SimTime`, `SpeakerId`, `Kind` (string 24: HROT's name, e.g. `CallContact`, `CmdHandsUp`, `CallReloading`, `CallFragOut`), `TargetId` | plays the speaker's line for `Kind` |
| `DPS_Report` *(new)* | event · same | `SimTime`, `FromId`, `ToId` (0 = HQ), `Kind` (`ReportKind`: Contact, Spot, Sitrep, Lace, Progress, Request), `Sub` (Progress / Request subtype number), `Count`, `Size` (`ElementSize`), `Activity` (`ElementActivity`), `Equipment` (bit flags: 1 MG, 2 sniper, 4 AT, 8 vehicle, 16 indirect, 32 air defence, 64 smoke), `Strength`, `Ammo` (`StatusColour`), `Wia`, `Kia`, `Synthetic` | picks the line by kind and fields (your table, reply §3); silent when `Synthetic` if you like |
| `DPS_Order` *(new)* | event · same | `SimTime`, `FromId`, `ToId`, `Name` (string 32: the ordered behaviour, e.g. `Attack`) | the order's line |
| `DPS_Fire` *(appended)* | unchanged | `BurstId` (0 = single aimed shot or a player's round), `BurstRounds`, `BurstIndex` (0-based) | start an N-round burst sound at index 0, skip the per-round sound for the rest; stop at a gap if fewer arrive |
| `DPS_Ambience` *(new)* | discrete state · reliable, transient-local, keep-last 1, key `Id`; disposed when the layer ends | `Kind` (string 32, dotted), `Intensity` 0..1, `HasPosition`, `Position`, `Radius`, `UntilSim` (0 = until disposed) | maps the kind with parent fallback (`alarm.airraid.preliminary` → `alarm.airraid` → `alarm`); kinds list: [the ambience note](NOTE-2026-11-22-Gateway-Ambience.md) §2 |
| `DPS_Weather` *(new)* | discrete state · same, key `Id` | `Outline` (≤ 32 points; empty = global), `FeatherM`, `WindFromDeg` / `WindSpeedMps` / `WindGustMps`, `Precipitation` (`PrecipitationKind`) + `PrecipitationMmPerHour`, `VisibilityM`, `FogTopM`, `FogVisibilityM`, `CloudOctas`, `CloudBaseM`, `GroundTemperatureC`, `RelativeHumidity` | the region the camera is in, else the global row; your rain / wind beds and the sky |
| `DPS_View` *(appended)* | unchanged | `Fade` (0..1, the director's dip to black), `Subtitle` (string 512, empty = none), `SubtitlePosition`, `SubtitleAlpha` | the live director's picture, as `FrameRequest` already gives a render job |

## 2. When each starts flowing (one worker, in this order)

1. voice (`Utterance`, `Report`, `Order`) — gateway T-064, needs nothing from HROT;
2. bursts on `Fire` — HROT CE-11033 (building now), then gateway T-065;
3. ambience and weather — HROT CE-11034, then gateway T-066 (weather could come earlier: it needs nothing new from HROT — I will send it with T-066 unless you ask);
4. live fade and subtitle on `View` — HROT CE-11035, then gateway T-067.

⚠ If a field or name does not fit your mapping, say so in a note before the producer side is built — appending is still free; changing is not.
