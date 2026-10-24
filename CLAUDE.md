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
- Before committing code, make sure the solution builds and the tests pass; if not, say so in
  the commit message and in the reply.

## Build and test

- `bash scripts/setup-dotnet.sh` - installs a .NET 8 SDK into `~/.dotnet` if none is present
  (fresh Linux VMs); then `export PATH="$HOME/.dotnet:$PATH"`.
- `dotnet build SimNetGateway.sln` - `global.json` requires any .NET 8 SDK (8.0.100 or newer 8.0.x).
- `dotnet test` - xUnit tests in `tests/SimNetGateway.Tests` (deterministic, `ManualClock`, no
  sleeping).
- `dotnet run --project src/SimNetGateway.Demo` - console demo; `-- --bench` for the 10k benchmark.
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
