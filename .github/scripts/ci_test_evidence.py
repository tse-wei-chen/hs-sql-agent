"""Audit backend CI test ownership and validate genuine xUnit/Coverlet artifacts.

The SQL syntax corpus ledger is separate: it counts grammar fixtures, not code
coverage. This script only reports the suites whose TRX/Cobertura files exist.
Standard library only, so the guard works before .NET or third-party install.
"""
import argparse
import json
from pathlib import Path
import re
import sys
import xml.etree.ElementTree as ET


TEST_ROOT = Path("backend/src/UnitTest")
EVIDENCE_JOBS = ("common-test", "infrastructure-test", "auth-test", "admin-test")


class EvidenceError(ValueError):
    pass


def _job_section(workflow, name):
    match = re.search(
        rf"^  {re.escape(name)}:\s*\n(?P<body>(?:(?!^  [a-z][\w-]*:).|\n)*)",
        workflow,
        re.MULTILINE,
    )
    return match.group("body") if match else None


def audit_repository(root):
    """Reject tests that have no CI owner, empty assertions, or missing evidence gates."""
    root = Path(root)
    projects = sorted(p.relative_to(root).as_posix()
                      for p in (root / TEST_ROOT).glob("*/*.csproj"))
    if not projects:
        raise EvidenceError("No backend xUnit projects were found")

    workflow_path = root / ".github/workflows/test.yml"
    workflow = workflow_path.read_text(encoding="utf-8")
    invoked = set(re.findall(
        r"dotnet test\s+--project\s+(backend/src/UnitTest/[\w.]+/[\w.]+\.csproj)",
        workflow,
    ))
    errors = [f"No CI test invocation for: {project}"
              for project in projects if project not in invoked]

    for job in EVIDENCE_JOBS:
        section = _job_section(workflow, job)
        if section is None:
            errors.append(f"Missing measured offline test job: {job}")
        elif ('--coverlet' not in section
              or "--report-xunit-trx" not in section
              or f"--results-directory artifacts/test-evidence/{job}" not in section
              or f"ci_test_evidence.py verify --directory artifacts/test-evidence/{job}" not in section):
            errors.append(f"{job}: missing actual TRX/Cobertura collection or validation")

    for source in (root / TEST_ROOT).rglob("*.cs"):
        if re.search(r"\bAssert\s*\.\s*True\s*\(\s*true\s*\)", source.read_text(encoding="utf-8")):
            errors.append(f"No-op test assertion: {source.relative_to(root).as_posix()}")

    if errors:
        raise EvidenceError("\n".join(errors))
    return {"projectsWithCiOwner": projects, "measuredJobs": list(EVIDENCE_JOBS)}


def _xml(path):
    try:
        return ET.parse(path).getroot()
    except (OSError, ET.ParseError) as error:
        raise EvidenceError(f"Invalid XML: {path}: {error}") from error


def _one(directory, pattern):
    matches = sorted(Path(directory).rglob(pattern))
    if len(matches) != 1:
        raise EvidenceError(
            f"Expected exactly one {pattern} in {directory}, found {len(matches)}"
        )
    return matches[0]


def validate_evidence(directory, suite):
    """Fail closed on zero-test runs or missing actual instrumentation output."""
    directory = Path(directory)
    trx = _xml(_one(directory, "*.trx"))
    counters = trx.find(".//{*}ResultSummary/{*}Counters")
    if counters is None:
        raise EvidenceError("TRX is missing ResultSummary/Counters")

    try:
        total = int(counters.attrib["total"])
        executed = int(counters.attrib["executed"])
        passed = int(counters.attrib["passed"])
        failed = int(counters.attrib["failed"])
        not_executed = int(counters.attrib.get("notExecuted", "0"))
    except (KeyError, ValueError) as error:
        raise EvidenceError("TRX counters missing or invalid") from error
    if not (total > 0 and executed > 0 and passed > 0
            and failed == 0 and total >= executed and executed >= passed):
        raise EvidenceError(
            f"{suite}: empty or failed test run (total={total}, "
            f"executed={executed}, passed={passed}, failed={failed})"
        )

    cobertura = _xml(_one(directory, "coverage.cobertura*.xml"))
    if cobertura.tag != "coverage":
        raise EvidenceError("Expected a Cobertura coverage root")
    try:
        lines_valid = int(cobertura.attrib["lines-valid"])
        lines_covered = int(cobertura.attrib["lines-covered"])
    except (KeyError, ValueError) as error:
        raise EvidenceError("Cobertura line counts missing or invalid") from error
    if lines_valid <= 0 or lines_covered < 0 or lines_covered > lines_valid:
        raise EvidenceError("Cobertura has no measurable production lines or invalid counts")

    return {
        "suite": suite,
        "total": total,
        "executed": executed,
        "passed": passed,
        "failed": failed,
        "notExecuted": not_executed,
        "linesCovered": lines_covered,
        "linesValid": lines_valid,
        "lineCoveragePercent": round(100 * lines_covered / lines_valid, 2),
        "scope": "This suite's instrumented assemblies only; not whole-repository coverage",
    }


def _write_summary(result):
    summary_path = Path(__import__("os").environ.get("GITHUB_STEP_SUMMARY", ""))
    if summary_path.is_file():
        with summary_path.open("a", encoding="utf-8") as summary:
            summary.write(
                f"\n### {result['suite']} — measured test evidence\n"
                f"Passed: {result['passed']} / {result['total']} "
                f"(not executed: {result['notExecuted']}). "
                f"Instrumented line coverage: {result['lineCoveragePercent']}% "
                f"({result['linesCovered']}/{result['linesValid']}).\n\n"
                "This is **suite-scoped**, not whole-repository code coverage.\n"
            )


def main(argv=None):
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest="action", required=True)
    audit = sub.add_parser("audit", help="Verify all test projects have CI owners")
    audit.add_argument("--root", type=Path, default=Path("."))
    verify = sub.add_parser("verify", help="Validate a real xUnit TRX/Cobertura pair")
    verify.add_argument("--directory", type=Path, required=True)
    verify.add_argument("--suite", required=True)

    args = parser.parse_args(argv)
    try:
        if args.action == "audit":
            result = audit_repository(args.root)
        else:
            result = validate_evidence(args.directory, args.suite)
            (args.directory / "summary.json").write_text(
                json.dumps(result, ensure_ascii=False, indent=2) + "\n",
                encoding="utf-8",
            )
            _write_summary(result)
        print(json.dumps(result, indent=2, ensure_ascii=False))
        return 0
    except (EvidenceError, OSError) as error:
        print(f"test evidence gate failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
