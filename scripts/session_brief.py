"""Session brief: re-learn the project state at session start and after context compaction.

Invoked through scripts/session-brief.sh (picks python3 or python, so it works on Linux and
Windows) by the SessionStart hook (.claude/settings.json, matchers startup|resume|compact) and
by the /relearn command. Its stdout is injected into the model's context.

Compaction keeps conclusions but drops the design context, so a resumed session tends to act
confidently on a wrong base. This script puts the canon back in front of the model: rulings,
open tasks, design documents, recent commits and uncommitted work.

Output is kept small on purpose (target < 8 KB): a large hook payload gets truncated to a short
preview, so the ACTION block comes first and everything else is a digest, not the full files.
"""

import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
RULINGS = ROOT / "docs" / "RULINGS.md"
TASKS = ROOT / "docs" / "TASKS.md"
DESIGN_DIR = ROOT / "docs" / "design"
OPEN_STATUSES = {"todo", "in-progress", "review", "blocked"}


def out(text=""):
    print(text)


def git(*args):
    try:
        res = subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True,
                             encoding="utf-8", errors="replace", timeout=10)
        return res.stdout.strip() if res.returncode == 0 else ""
    except (OSError, subprocess.SubprocessError):
        return ""


def clean(cell):
    cell = re.sub(r"\*\*|`", "", cell)
    cell = re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", cell)
    return re.sub(r"\s+", " ", cell).strip()


def table_rows(path, id_pattern):
    """Yield the cells of every markdown table row whose first cell is a bold id."""
    try:
        lines = path.read_text(encoding="utf-8").splitlines()
    except OSError:
        out(f"  !! {path.relative_to(ROOT)} unreadable - say so in any claim made this session.")
        return
    for line in lines:
        cells = [c.strip() for c in line.strip().strip("|").split("|")]
        if cells and re.fullmatch(r"\*\*" + id_pattern + r"\*\*", cells[0]):
            yield [clean(c) for c in cells]


def short(text, width):
    return text if len(text) <= width else text[: width - 3] + "..."


def main():
    if hasattr(sys.stdout, "reconfigure"):
        sys.stdout.reconfigure(encoding="utf-8", errors="replace")

    out("==============================================================")
    out(" SESSION BRIEF - re-learn before your first tool call (R-010)")
    out("==============================================================")
    out("1. The digest below is NOT the canon. Before any design or")
    out("   implementation decision, Read docs/RULINGS.md in full and the")
    out("   design doc (docs/design/DD-*.md) that owns the topic.")
    out("2. Continue from docs/TASKS.md: the in-progress/review rows are")
    out("   where work stopped. Check the last commits below to confirm.")
    out("3. No implementation without a design doc with mermaid block,")
    out("   sequence and class diagrams (R-008). Commit after every round")
    out("   (R-007), referencing T-/R-/DD- ids.")
    out("4. Your first reply opens with the RELEARNED block (format at the")
    out("   end), then answers the user in the same reply.")
    out()

    plan = ROOT / "docs" / "PLAN.md"
    try:
        stage = next((l.strip("* ") for l in plan.read_text(encoding="utf-8").splitlines()
                      if l.startswith("**Current stage:")), "(no current-stage line)")
    except OSError:
        stage = "(docs/PLAN.md missing)"
    out("== PLAN (docs/PLAN.md)")
    out("  " + stage.replace("**", ""))
    out()

    out("== RULINGS (docs/RULINGS.md) - headlines only, open the file to quote")
    count = 0
    for cells in table_rows(RULINGS, r"R-\d+"):
        count += 1
        out(f"  {cells[0]:<6} {short(cells[1], 50)}")
    out(f"  ({count} rulings)")
    out()

    out("== OPEN TASKS (docs/TASKS.md)")
    open_count = done_count = 0
    for cells in table_rows(TASKS, r"T-\d+[a-z]?"):
        status = cells[1].lower() if len(cells) > 1 else "?"
        if status in OPEN_STATUSES:
            open_count += 1
            design = cells[3] if len(cells) > 3 else "-"
            out(f"  {cells[0]:<7} [{status}] {short(cells[2], 64)} ({design})")
        else:
            done_count += 1
    out(f"  ({open_count} open, {done_count} done/dropped)")
    out()

    out("== DESIGN DOCS (docs/design) - newest change first")
    docs = []
    for path in sorted(DESIGN_DIR.glob("DD-*.md")):
        status = "?"
        try:
            for line in path.read_text(encoding="utf-8").splitlines()[:12]:
                m = re.match(r"\|\s*\*\*Status\*\*\s*\|(.*)\|", line)
                if m:
                    status = clean(m.group(1))
                    break
        except OSError:
            pass
        when = git("log", "-1", "--format=%cs", "--", str(path.relative_to(ROOT))) or "uncommitted"
        docs.append((when, path.name, status))
    for when, name, status in sorted(docs, reverse=True):
        out(f"  {when}  {name}  - {short(status, 60)}")
    if not docs:
        out("  (none)")
    out()

    out("== LAST COMMITS")
    log = git("log", "-10", "--format=%cs %h %s")
    out("\n".join("  " + short(l, 90) for l in log.splitlines()) if log else "  (no commits)")
    out()

    status = git("status", "--short")
    out("== UNCOMMITTED CHANGES")
    if status:
        lines = status.splitlines()
        for l in lines[:15]:
            out("  " + l)
        if len(lines) > 15:
            out(f"  ... and {len(lines) - 15} more")
    else:
        out("  (clean)")
    out()

    out("==============================================================")
    out(" REQUIRED: your first reply opens with this block")
    out("==============================================================")
    out("RELEARNED (session start / post-compaction)")
    out("  working on : <task id(s) + one line, from TASKS.md and commits>")
    out("  design     : <DD id(s) governing it, which section I start from>")
    out("  binding    : <ruling ids that constrain the next step, one line each>")
    out("  next step  : <the concrete next action>")
    out("  unsure     : <anything the docs do not settle, or 'nothing'>")


if __name__ == "__main__":
    main()
