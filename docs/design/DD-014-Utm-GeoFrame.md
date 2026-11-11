# DD-014: GeoFrame = HROT's UTM frame (geo reference protocol version 2)

| | |
|---|---|
| **Status** | approved (R-131) - short design |
| **Tasks** | T-031 |
| **Rulings honoured** | R-084 (GeoFrame from HROT's geo reference; refuse unknown version / frame; nothing positional before it; re-base on a new generation), R-131 |
| **Inputs** | HROT `coordinator` `docs/DESIGN_Geo_Origin.md` section 6.3 (UTM everywhere, CE-4396): `Fdp.Modules.Geographic.UtmTransform`, `Utm`, `LocalOnlyFrame`; wire `GeoReference` v2 (`NedWire.ProtocolVersion = 2`, `FrameUtm = 1`, `FrameNone = 2`) |
| **Date** | 2026-11-11 |

## 1. Why

HROT no longer has a tangent plane. Its local frame is UTM grid metres minus an origin: x = E - E0, y = N - N0, z = h - H0, axes grid
east / grid north / up, body FLU. The gateway's `GeoFrame` still builds an ENU tangent plane at the origin's lat / lon, which is wrong by
the grid convergence (about 30 m per km at 1.7 deg) and by the projection itself. Every side (DIS, HLA, private sides) reads the pivot
(ECEF + geocentric Euler), so fixing `GeoFrame` fixes all of them; the pivot does not change.

## 2. Decisions

- **D-1 `HrotOrigin` carries the v2 geo reference**: `HrotOrigin(ProtocolVersion, Frame, LatitudeDeg, LongitudeDeg, AltitudeM,
  UtmZoneOverride, UtmSouthOverride, Generation)`; factories `HrotOrigin.Utm(...)`, `HrotOrigin.None(generation)`.
- **D-2 The frame rule is HROT's** (`UtmTransform.AtOrigin`): zone = the origin longitude's band unless `UtmZoneOverride != 0`; south =
  latitude < 0 unless an override zone is given (then `UtmSouthOverride`); (E0, N0) = the origin projected in that zone; H0 = altitude.
- **D-3 Refusals**: `ProtocolVersion != 2` or `Frame` not 1 / 2 -> refused with a problem text, the frame stays as it was (R-084).
  `Frame == 2` (local-only terrain) -> accepted, `IsValid = false`, `IsLocalOnly = true`: nothing positional crosses to any side.
- **D-4 Projection**: `SimNetGateway.Common.Geo.Utm` - Snyder's transverse Mercator series on WGS84 (k0 0.9996), the same formulas as
  HROT's `Utm`, checked against HROT's numbers in tests (rails generated once from HROT, generic places only). SimNetGateway references
  nothing from HROT (R-128), hence its own copy.
- **D-5 Orientation and vectors are position-aware**: a grid frame turns against true north by the convergence at each point. The
  conversions take the local position: grid ENU is rotated about Up by the point's convergence into true ENU, then the existing
  `LocalToGeocentricOrientation` runs with a `LocalTangentFrame` at that point. The UTM scale factor (< 0.05 % inside a zone) is ignored for
  vectors; positions are exact (projection).
- **D-6** `LocalTangentFrame` / `LocalToGeocentricOrientation` stay in Common (other users); they leave the HROT path only as described in D-5.

## 3. Tests

Round trip of the projection (< 1e-9 deg); reference rails from HROT's `Utm` (north, south, 2 km past a zone edge with the override); the
frame rule; a vector equals the finite difference of two projected points (pins the convergence sign); Euler round trip at random points
(< 0.01 deg); refusals of v1 / unknown frame; `Frame 2`. DIS / HLA suites green with the harnesses on the new origin.

## 4. As-built notes

- **Namespace**: `Utm` and `UtmFrame` are in `SimNetGateway.Common.Geo` (a new namespace; the older Geo files stay in `SimNetGateway.Common`).
- **`Utm` is HROT's series, verbatim**, so its inverse is off by up to a few 1e-9 degrees (0.3 mm) inside a zone and by metres at 9 degrees from the
  central meridian near the poles. The round-trip test is therefore 1e-8 degrees inside the zone (3 degrees) and 2e-5 degrees in the neighbouring
  zones; the rails equal HROT's numbers (forward and inverse). Longitudes are wrapped to the central meridian +- 180 degrees (a zone next to the date
  line), which HROT's formula does not do.
- **`UtmFrame` refines the inverse**: local to geodetic runs the series, then a few fixed-point steps on the forward projection, so the origin is
  exactly its latitude / longitude and a point survives local -> ECEF -> local to well below a micrometre. It differs from HROT's own
  `UtmTransform.ToGeodetic` by the series error above (under a millimetre inside a zone).
- **Scale**: a vector's horizontal length is not rescaled (design D-5); positions are exact, so grid metres and true metres differ by the UTM scale
  factor (0.9996 on the central meridian, about 1.0004 at 3 degrees). Tests that compared metres between a local offset and ECEF distances now allow
  that (a few millimetres at 1 to 10 m, tolerance 1e-2 or looser where the offset is hundreds of metres).
- **Angular velocity** is converted through true ENU like the other directions; the convergence cancels (body-axis rates do not depend on it).
- **`GeoFrame.TryApply`**: version != 2 or a frame other than 1 / 2 is refused; frame 2 is accepted (`IsValid` false, `IsLocalOnly` true, a change
  from or to a valid frame counts as a re-base and publishes `Hrot.FrameRebased`); a UTM origin keeps the old checks (finite, in range, (0, 0, 0) =
  not set unless allowed) plus a zone override in 0 .. 60. `HrotOriginIngress` reports a refusal once per generation.
- **`HrotDisServices.TimeScale`** (internal setter) is filled by `HrotSessionIngress` for the private repository's sides.
- **Tests**: the harness oracle is `TestFrame` (Common only: `UtmFrame`, `LocalTangentFrame`, `LocalToGeocentricOrientation`), no longer a
  `LocalTangentFrame`; call sites pass the local position the direction belongs to. The old "equator" cases moved to the central meridian of zone 31
  (grid = true axes, scale k0), with hand-written ECEF values.
