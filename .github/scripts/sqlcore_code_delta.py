"""Auditable per-PR source line delta, separate from SqlCore performance evidence.

Use git's exact two commits, not the working tree or auto-renames. A moved file
counts separately in each destination/source category; binaries are flagged.
This is an informational code-budget report, never a safety or performance gate.
"""
import json
import subprocess
import sys
from pathlib import Path

CATEGORIES = ("production", "tests", "generatedMigrations", "docs", "other")
SOURCE_EXTENSIONS = {".fs", ".fsi", ".fsx", ".cs"}
DOC_EXTENSIONS = {".md", ".mdx", ".rst", ".txt"}


def classify(path):
    """Partition changed repository paths without accidentally counting docs as code."""
    normalized = path.replace("\\", "/")
    segments = normalized.casefold().split("/")
    name = segments[-1]
    if name.endswith((".test.cs", ".spec.cs", ".test.ts", ".spec.ts", ".test.tsx", ".spec.tsx")):
        return "tests"
    if (normalized.startswith("backend/src/UnitTest/")
            or normalized.startswith("backend/tests/")
            or "/__tests__/" in normalized
            or "/test/" in normalized.lower()
            or "/tests/" in normalized.lower()):
        return "tests"
    if Path(name).suffix.lower() in DOC_EXTENSIONS or segments[0] == "docs":
        return "docs"
    if normalized.startswith("backend/src/Modules/"):
        if (any(s in {"migrations", "generated", "obj", "bin"} for s in segments)
                or name.endswith((".g.cs", ".g.fs", ".designer.cs", ".assemblyinfo.cs"))):
            return "generatedMigrations"
        if Path(name).suffix.lower() in SOURCE_EXTENSIONS:
            return "production"
    return "other"


def git(repo, *args):
    return subprocess.check_output(
        ["git", "-C", str(repo), *args], stderr=subprocess.PIPE)


def resolve_commit(repo, revision):
    return git(repo, "rev-parse", "--verify", f"{revision}^{{commit}}").decode("ascii").strip()


def summarize(repo, base, head):
    """Count committed added/deleted lines in an explicitly versioned comparison."""
    base_sha = resolve_commit(repo, base)
    head_sha = resolve_commit(repo, head)
    raw = git(repo, "diff", "--numstat", "-z", "--no-renames", base_sha, head_sha, "--")
    categories = {key: {"added": 0, "deleted": 0, "files": 0}
                  for key in CATEGORIES}
    binary = 0
    for record in raw.split(b"\x00"):
        if not record:
            continue
        parts = record.split(b"\t", 2)
        if len(parts) != 3:
            raise ValueError("Malformed git --numstat -z record")
        added, deleted, raw_path = parts
        path = raw_path.decode("utf-8", "surrogateescape")
        bucket = categories[classify(path)]
        bucket["files"] += 1
        if added == b"-" and deleted == b"-":
            binary += 1
        else:
            bucket["added"] += int(added)
            bucket["deleted"] += int(deleted)
    for bucket in categories.values():
        bucket["net"] = bucket["added"] - bucket["deleted"]
    result = {
        "schemaVersion": 1,
        "baseCommit": base_sha,
        "headCommit": head_sha,
        "method": "git diff --numstat -z --no-renames, committed revisions only",
        "categories": categories,
        "binaryFiles": binary,
        "total": {
            "added": sum(b["added"] for b in categories.values()),
            "deleted": sum(b["deleted"] for b in categories.values()),
            "files": sum(b["files"] for b in categories.values()),
        },
    }
    result["total"]["net"] = result["total"]["added"] - result["total"]["deleted"]
    return result


def markdown(report):
    lines = [
        "## SqlCore PR code delta (source lines, not performance)",
        f"Base: `{report['baseCommit']}`",
        f"Head: `{report['headCommit']}`",
        "",
        "| Category | Added | Deleted | Net | Files |",
        "|---|---:|---:|---:|---:|",
    ]
    for key in CATEGORIES:
        b = report["categories"][key]
        lines.append(f"| {key} | +{b['added']} | -{b['deleted']} | "
                     f"{b['net']:+d} | {b['files']} |")
    b = report["total"]
    lines.append(f"| **Total** | **+{b['added']}** | **-{b['deleted']}** | "
                 f"**{b['net']:+d}** | **{b['files']}** |")
    if report["binaryFiles"]:
        lines.append(f"\n**Caution:** {report['binaryFiles']} binary changed file(s) "
                     "have unknown line counts, not zero-size change.")
    lines.extend([
        "",
        "Generated/migrations and tests/docs are excluded from production totals.",
        "Moves across categories count as deletion + addition; no rename hiding.",
        "This report does not establish safety, semantic complexity, or speedup. "
        "See the paired 48-workload SqlCore comparison artifact for performance.",
    ])
    return "\n".join(lines)


def main(argv):
    if len(argv) != 5:
        raise SystemExit(
            "Usage: sqlcore_code_delta.py <repository-dir> <base-sha> <head-sha> <report-json>")
    _, repo, base, head, output = argv
    report = summarize(Path(repo), base, head)
    target = Path(output)
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_text(json.dumps(report, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
    print(markdown(report))


if __name__ == "__main__":
    main(sys.argv)
