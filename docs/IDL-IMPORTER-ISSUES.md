# CycloneDDS.NET IDL importer - issues found in the first real use (legacy keylist-pragma IDL)

Collected while importing a legacy IDL set (DD-013). Versions: CycloneDDS.NET 1.2.20 NuGet (`tools/CycloneDDS.IdlImporter.dll`,
`tools/idlc` 11.0.1, linux-x64), source `github.com/pjanec/cyclonedds.net` @ `416c68b`. Examples below are **synthetic**, not
the proprietary IDL. Each issue has a status and, where the import script works around it, the workaround's tag.

| Id | Area | Issue | Seen as | Workaround (DD-013) | Status |
|---|---|---|---|---|---|
| I-1 | CLI / docs | `IDL-IMPORT.md` documents `CycloneDDS.IdlImporter <master-idl> <source-root> <output-root>`; the 1.2.20 CLI takes only `<master-idl>` and needs `--source-root` / `--output-root` options | "Unrecognized command or argument 'src'" | script uses the options | open |
| I-2 | QoS | QoS given by `#pragma topic ...` lines is not carried into the C#: no `[DdsQos]` is emitted although `CycloneDDS.Schema` has `DdsQosAttribute` | every topic gets the default QoS | post-pass adds `[DdsQos]` from the script's own pragma parse (tag `I-2`) | open |
| I-3 | topics | a `#pragma keylist T` **with no key fields** (a keyless topic) produces `[DdsStruct]`, not a topic; only structs with keys become `[DdsTopic]` | message topics (`struct Fire {...}; #pragma keylist Fire`) cannot be published | post-pass turns them into `[DdsTopic]` (tag `I-3`) | open |
| I-4 | idlc JSON plugin | the JSON's per-type `"QoS"` block is the same for every struct (reliable, transient_local, keep_last 1), including non-topic structs and topics whose pragma says `best_effort` | `"QoS"` cannot be trusted as the source of I-2's fix | script parses pragmas itself | open |
| I-5 | idlc | a file mixing annotations (`@bit_bound`, `@value`) with `#pragma keylist` is refused unless `-f keylist` | "Translation unit contains both annotations and #pragma keylist directives" | script always passes `--idlc-args "-f keylist"` | by design in idlc; importer could pass it when it sees keylists |
| I-6 | topic names | the default topic name is the fully scoped type name with `::` (`A::B::T`); a legacy system's real topic names may differ (often the bare or `_`-joined name) | wire match depends on the name | script profile sets the name explicitly (`[DdsTopic("...")]`) | needs the peer's convention |
| I-7 | comments | IDL comments (units, semantics) are not carried into the C# | generated structs lose units like "[deg]" | post-pass could copy trailing member comments as `///` (tag `I-7`) | open, nice-to-have |
| I-8 | includes | Windows `\` separators in `#include` fail on Linux | "Can't open include file" | script normalises to `/` | idlc/importer could normalise |

## Interoperability risks to verify (not importer bugs)

| Id | Risk | How to check |
|---|---|---|
| W-1 | the legacy peer may use XCDR1 / classic CDR and `final` extensibility defaults; CycloneDDS.NET's writer encoding and the generated `[DdsExtensibility(Final)]` must match what the peer expects | one sample exchanged with a real peer; `ddsmonitor` capture |
| W-2 | key hashing / instance identity with multi-field keylists (`EntityId PartId`) | dispose and re-create an instance across implementations |
