"""Deterministic observed SQL corpus ledger for the six supported providers.

This checks checked-in compiler regression fixtures, not SQL standard coverage,
a real database's acceptance, transaction safety or native execution parity.
No dependencies beyond the Python standard library.
"""
import argparse
from collections import Counter
import json
from pathlib import Path


PROVIDERS = ("Postgres", "MySQL", "MsSqlServer", "Sqlite", "Oracle", "Firebird")
FAMILIES = ("DQL", "DML", "DDL", "DCL", "TCL", "Session", "Utility", "Procedural")


class CoverageLedgerError(ValueError):
    """Invalid or unverified coverage evidence."""


def load_manifest(path):
    try:
        return json.loads(Path(path).read_text(encoding="utf-8"))
    except (OSError, json.JSONDecodeError) as error:
        raise CoverageLedgerError(f"Invalid coverage manifest {path}: {error}") from error


def _validate_manifest(manifest):
    if not isinstance(manifest, dict) or manifest.get("schemaVersion") != 1:
        raise CoverageLedgerError("Expected schemaVersion 1 coverage manifest")
    suites = manifest.get("suites")
    if not isinstance(suites, list) or not suites:
        raise CoverageLedgerError("Coverage manifest must define at least one suite")

    seen_ids = set()
    seen_files = set()
    for suite in suites:
        if not isinstance(suite, dict):
            raise CoverageLedgerError("Coverage suite must be an object")
        suite_id = suite.get("id")
        filename = suite.get("file")
        if not isinstance(suite_id, str) or not suite_id or suite_id in seen_ids:
            raise CoverageLedgerError("Coverage suite id is missing or duplicate")
        if (not isinstance(filename, str)
                or not filename.endswith(".json")
                or Path(filename).name != filename
                or "\\" in filename
                or filename in seen_files):
            raise CoverageLedgerError(f"{suite_id}: invalid or repeated corpus file")
        if suite.get("statementFamily") not in FAMILIES:
            raise CoverageLedgerError(f"{suite_id}: invalid statementFamily")
        if not isinstance(suite.get("requiresVersionProfile"), bool):
            raise CoverageLedgerError(f"{suite_id}: requiresVersionProfile must be boolean")
        if not isinstance(suite.get("nativeOnly"), bool):
            raise CoverageLedgerError(f"{suite_id}: nativeOnly must be boolean")

        expected = suite.get("expectedByProvider")
        if (not isinstance(expected, dict)
                or set(expected) != set(PROVIDERS)
                or any(type(count) is not int or count < 0 for count in expected.values())
                or sum(expected.values()) == 0):
            raise CoverageLedgerError(f"{suite_id}: expectedByProvider must enumerate all six providers")
        seen_ids.add(suite_id)
        seen_files.add(filename)
    return suites


def build_report(corpus_directory, manifest):
    """Validate every declared case and return a deterministic observed ledger.

    No percentage is calculated: there is no official grammar denominator,
    and the listed corpora are not a proof of Host execution.
    """
    suites = _validate_manifest(manifest)
    root = Path(corpus_directory)
    providers = {
        provider: {
            "totalCases": 0,
            "versionProfiledCases": 0,
            "byFamily": {family: 0 for family in FAMILIES},
        }
        for provider in PROVIDERS
    }
    suite_reports = []

    for suite in suites:
        try:
            cases = json.loads((root / suite["file"]).read_text(encoding="utf-8"))
        except (OSError, json.JSONDecodeError) as error:
            raise CoverageLedgerError(f"{suite['id']}: corpus cannot be read: {error}") from error
        if not isinstance(cases, list) or not cases:
            raise CoverageLedgerError(f"{suite['id']}: corpus must be a nonempty JSON array")

        counts = Counter()
        versioned = Counter()
        seen_names = set()
        for index, case in enumerate(cases):
            label = f"{suite['id']}[{index}]"
            if not isinstance(case, dict):
                raise CoverageLedgerError(f"{label}: case must be an object")
            name = case.get("name")
            if not isinstance(name, str) or not name.strip():
                raise CoverageLedgerError(f"{label}: missing case name")
            if name in seen_names:
                raise CoverageLedgerError(f"{label}: duplicate case name '{name}'")
            seen_names.add(name)
            dialect = case.get("dialect")
            if dialect not in PROVIDERS:
                raise CoverageLedgerError(f"{label}: invalid dialect '{dialect}'")
            if not isinstance(case.get("sql"), str) or not case["sql"].strip():
                raise CoverageLedgerError(f"{label}: missing SQL text")
            target = case.get("targetDialect", dialect)
            if target not in PROVIDERS:
                raise CoverageLedgerError(f"{label}: invalid targetDialect '{target}'")
            if suite["nativeOnly"] and target != dialect:
                raise CoverageLedgerError(
                    f"{label}: targetDialect '{target}' differs from native source '{dialect}'")
            source_version = case.get("sourceVersion")
            target_version = case.get("targetVersion")
            if suite["requiresVersionProfile"]:
                if not isinstance(source_version, str) or not source_version.strip():
                    raise CoverageLedgerError(f"{label}: sourceVersion required")
                if not isinstance(target_version, str) or not target_version.strip():
                    raise CoverageLedgerError(f"{label}: targetVersion required")
            if (isinstance(source_version, str) and source_version.strip()
                    and isinstance(target_version, str) and target_version.strip()):
                versioned[dialect] += 1
            counts[dialect] += 1

        expected = suite["expectedByProvider"]
        if any(counts[provider] != expected[provider] for provider in PROVIDERS):
            raise CoverageLedgerError(
                f"{suite['id']}: expectedByProvider drift: "
                f"declared={expected}, observed="
                f"{ {provider: counts[provider] for provider in PROVIDERS} }")

        family = suite["statementFamily"]
        for provider in PROVIDERS:
            providers[provider]["totalCases"] += counts[provider]
            providers[provider]["byFamily"][family] += counts[provider]
            providers[provider]["versionProfiledCases"] += versioned[provider]
        suite_reports.append({
            "id": suite["id"],
            "file": suite["file"],
            "statementFamily": family,
            "cases": len(cases),
            "byProvider": {provider: counts[provider] for provider in PROVIDERS},
            "versionProfiledCases": sum(versioned.values()),
        })

    return {
        "schemaVersion": 1,
        "evidenceLevel": "observed-compiler-regression-corpus",
        "description": "Case inventory only; existing compiler matrix tests run separately in CI.",
        "officialGrammarDenominator": None,
        "liveExecutionProven": False,
        "totalCases": sum(provider["totalCases"] for provider in providers.values()),
        "providers": providers,
        "suites": suite_reports,
    }


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--manifest", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--check", action="store_true",
                        help="Fail on any missing, unexpected or drifted corpus evidence (the default)")
    options = parser.parse_args()
    try:
        manifest = load_manifest(options.manifest)
        report = build_report(options.manifest.parent, manifest)
    except CoverageLedgerError as error:
        parser.error(str(error))
    options.output.parent.mkdir(parents=True, exist_ok=True)
    options.output.write_text(
        json.dumps(report, ensure_ascii=False, indent=2, sort_keys=True) + "\n",
        encoding="utf-8")
    print(f"Verified {len(report['suites'])} observed SQL corpora / "
          f"{report['totalCases']} cases across {len(report['providers'])} providers")
    print(f"Evidence artifact: {options.output} (no official coverage percentage)")


if __name__ == "__main__":
    main()
