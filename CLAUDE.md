# SimNetGateway - working instructions

Gateway between simulation protocols.

## Rule zero - re-learn before working (R-010)

At session start, on resume and **after every context compaction**, a SessionStart hook runs
`bash scripts/session-brief.sh` (a portable wrapper around `scripts/session_brief.py`) and injects a digest of rulings, open tasks, design docs,
recent commits and uncommitted changes. Before your first tool call:

1. Read the brief. It is a digest, not the canon - open `docs/RULINGS.md` and the owning design
   doc before making any design or implementation decision.
2. Open your first reply with the filled-in `RELEARNED` block the brief prints, then answer the
   user in the same reply.
3. If the brief did not arrive (no hook output in context) or the design context feels thin, run
   `/relearn` (or follow `.claude/commands/relearn.md` by hand).

Intent lives in the design docs and rulings, not in the code: the code says how it *is*, the
docs say how it was *meant*. When they disagree, raise it instead of silently following the code.

## Delegation (R-063)

Use **Sonnet subagents** (`Agent` with `model: "sonnet"`) for work that does not need Opus:
routine implementation against an approved design, scaffolding, writing tests from the test plan,
mechanical edits, searches and builds. Brief them with the design sections, rulings and files to
read. The main session stays the **reviewer**: it reads the subagent's diff against the design and
rulings and the tests against the success conditions, sends back what is wrong, and only then
commits. Keep design decisions, `P-n` proposals and design-doc writing in the main session.

The main session does **not rerun the build or the tests** (R-066): the implementing agent must
have run the full build and test suite as its last step and put the result lines (warnings,
errors, passed/failed per test project) in its report; the commit relies on that report. If a
report lacks them, or the state to be committed was never tested as a whole (e.g. merging the work
of parallel agents, or a correction), send a Sonnet agent to make the change and/or run the full
suite and report, instead of running it in the main session.

Success conditions (R-064): every brief states them as **invariants and properties**, not only
example cases - e.g. the DD-002 `G-*` invariants, index-equals-brute-force-scan, all-or-nothing
transactions, 0-byte steady state - checked after every operation in randomized/seeded tests where
possible, plus the design's edge cases (capacities, limits, interactions between features). The one
implementing agent writes those tests and runs until they pass; a risk it notices must be fixed and
tested, not just reported. There is no second test agent. The implementing agent also proves its
tests are effective (R-065): it deliberately breaks each important behaviour (mutation check),
confirms the relevant tests fail, restores the code, and lists the mutations and the failing tests
in its report. The main session does **not** run mutation checks itself: its review reads the tests
and that report and asks whether they **cover enough and really check what matters**, and checks
the brief/design itself for errors, which no test derived from it can catch.

## Where things are recorded (R-009)

| What | Where | Id format |
|---|---|---|
| Rulings (decisions made by the user) | `docs/RULINGS.md` | `R-001`, `R-002`, ... |
| Tasks (planned / in progress / done work) | `docs/TASKS.md` | `T-001`, `T-002`, ... (`T-004a` for sub-tasks) |
| Design documents | `docs/design/DD-NNN-<Title>.md` (from `docs/design/_TEMPLATE.md`) | `DD-001`, `DD-002`, ... |
| Reference / input documents | not kept in the repository (removed 2026-10-24); referred to by name only | - |
| Implementation plan (stages, exit criteria, current stage) | `docs/PLAN.md` | stages 0, 1, 2, ... |

Numbering rules: take the next free number, **never reuse or renumber an id** (ids get quoted in
commits, docs and code comments). Do not delete rows; mark them `done`, `dropped` or
`SUPERSEDED by R-NNN`. The "How to record" sections at the top of `RULINGS.md` and `TASKS.md`
are the detailed instructions.

- **Ruling**: record it when the user decides something (in chat or by approving a design
  proposal). Quote the decision, not your interpretation. Proposals of your own are `P-n` items in
  a design doc until approved - never write them into `RULINGS.md` on your own authority.
- **Task**: every piece of work you start gets a `T-` row first; keep its status current
  (`todo`, `in-progress`, `review`, `done`, `blocked`, `dropped`) in the same commit as the work.

## Design before implementation (R-008)

No non-trivial implementation starts without a design document in `docs/design/` containing:

1. a **block diagram** (mermaid `flowchart`) of components and their relations,
2. **sequence diagrams** (mermaid `sequenceDiagram`) for the important flows, including the
   interesting edge cases,
3. **class diagrams** (mermaid `classDiagram`) of the main types,
4. **prose describing what happens in each diagram**, the decisions with rejected alternatives
   (one line each), edge cases, a test plan and `P-n` proposals awaiting approval.

Workflow: write the design -> the user reviews (task status `review`) -> approved proposals become
rulings -> implement -> fill in the design's **As-built notes** with any deviation and why. A
deviation discovered during implementation is reported, not silently chosen. Trivial changes
(a bug fix, a rename, a test) need no new design doc but must still respect the existing ones.

## Git (R-007)

- The repository is a local git repository (no remote yet). **Commit after every round of work**, i.e.
  at the end of every reply that changed files.
- Commit messages start with the task id(s): `T-003: state repository and change sets`, and
  mention rulings/designs touched. Docs and the code they describe go in the same commit.
- Before committing code, make sure the solution builds and the tests pass - by the implementing
  agent's reported full build and test run (R-066), not by rerunning them; if not green, say so in
  the commit message and in the reply.

## Build and test

- `bash scripts/setup-dotnet.sh` - installs a .NET 8 SDK into `~/.dotnet` if none is present
  (fresh Linux VMs); then `export PATH="$HOME/.dotnet:$PATH"`.
- `bash scripts/build.sh` (= `dotnet build SimNetGateway.sln`) - `global.json` requires any
  .NET 8 SDK (8.0.100 or newer 8.0.x). Both scripts put `~/.dotnet` on PATH when needed and pass
  extra arguments through.
- `bash scripts/test.sh` (= `dotnet test SimNetGateway.sln`) - xUnit, one test project per layer
  (DD-003): `tests/SimNetGateway.Engine.Tests`, `.Common.Tests`, `.Gateway.NedRpr.Tests` and
  `tests/SimNetGateway.Architecture.Tests` (dependency rules and conventions of DD-003 section 3;
  a forbidden project reference fails it). Deterministic, `ManualClock`, no sleeping.
- `dotnet run --project src/Hosts/SimNetGateway.Demo` - console demo; `-- --bench` for the 10k
  benchmark (from stage 6).
- Layout (DD-003 section 2): `src/Engine`, `src/Common`, `src/Protocols`, `src/Gateways`,
  `src/Transports` (later), `src/Showcase`, `src/Hosts`; folder name = project name = root
  namespace; package versions only in `Directory.Packages.props`; test-project packages in
  `tests/Directory.Build.props`.
- Helper scripts are Python 3 (`scripts/*.py`) started through bash wrappers (`scripts/*.sh`)
  that pick `python3` or `python`, whichever is a real Python 3 on the machine.

## Cross-platform (R-012)

Everything must run unchanged on Linux (cloud VM) and Windows.

- No Windows-only APIs, P/Invoke, registry, `\` path separators or drive letters; build paths
  with `Path.Combine` / `Path.Join`; no absolute paths in code, scripts or docs.
- Remember Linux is **case-sensitive**: file names, project references and `using` paths must
  match their exact casing.
- Scripts are bash or Python 3, never `.bat` / `.ps1` only. Hooks are invoked via `bash`.
- Line endings are LF (`.gitattributes`); do not commit CRLF.
- Native dependencies (raylib for the later viewer) must have Linux binaries; prefer NuGet
  packages that ship runtimes for `linux-x64` and `win-x64`.
- Time uses `Stopwatch` / monotonic clocks, not OS-specific timers.

## Code conventions

- C# 12, nullable enabled, implicit usings, `net8.0` (from `Directory.Build.props`).
- Engine (`src/SimNetGateway.Engine`) has **no protocol or network knowledge**; protocols live in
  modules, networks behind `IInboundChannel` / `IOutboundChannel<T>` (R-002).
- New message kinds are new small modules; do not grow existing ones to handle unrelated
  messages (R-003).
- "NED" is the name of the DDS-based simulation protocol on the internal side. Do not use `Ned`
  for the north-east-down frame in code; call that frame `LocalTangent` / `NorthEastDown` instead.
- Use ASCII in comments and string literals where ASCII expresses it equally well.
- Preserve existing comments when moving or editing code unless they are wrong.
