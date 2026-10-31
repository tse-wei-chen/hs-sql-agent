"""Fail-closed contract for six-provider stage profiling of the existing SQL corpus."""
import json
import math
import sys
from pathlib import Path

PROVIDERS = {"Sqlite", "MsSqlServer", "MySQL", "Postgres", "Oracle", "Firebird"}
CASES = {"query-filter-order", "query-cte", "dml-update", "dml-delete", "dml-insert",
         "query-union", "query-derived-32", "query-scalar-32"}
REQUIRED_STAGES = {"Parsing", "Binding", "Normalization", "Validation", "Policy", "Rendering"}
EXPECTED = {(provider, case) for provider in PROVIDERS for case in CASES}


def verify(path):
    report = json.loads(Path(path).read_text(encoding="utf-8"))
    if report.get("schemaVersion") != 1:
        raise ValueError("Unrecognized stage-profile schema")
    iterations = report.get("iterations")
    if not isinstance(iterations, int) or isinstance(iterations, bool) or iterations < 2:
        raise ValueError("Stage-profile sample count requires at least two iterations")
    if report.get("warmup", -1) < 1:
        raise ValueError("Stage profile must have warmup")
    rows = report.get("results", [])
    if len(rows) != 48:
        raise ValueError("Stage profile requires all 48 provider/workload cases")
    seen = set()
    for row in rows:
        key = (row.get("Provider"), row.get("Case"))
        if key not in EXPECTED or key in seen:
            raise ValueError("Missing/duplicate/unsupported provider case in 48-case corpus")
        seen.add(key)
        digest = row.get("OutputDigest")
        if not isinstance(digest, str) or len(digest) != 64:
            raise ValueError("Missing typed output digest")
        stages = row.get("Stages", [])
        names = [stage.get("Name") for stage in stages]
        if len(set(names)) != len(names):
            raise ValueError("duplicate compiler stage telemetry")
        if not REQUIRED_STAGES.issubset(set(names)):
            raise ValueError("Missing required compiler stage telemetry")
        for stage in stages:
            samples = stage.get("Samples")
            if not isinstance(samples, int) or samples != iterations:
                raise ValueError(f"Invalid compiler-stage sample count: {key}/{stage.get('Name')}")
            if stage.get("AllSucceeded") is not True:
                raise ValueError(f"Compiler-stage failure: {key}/{stage.get('Name')}")
            for metric in ("MedianMicroseconds", "MedianAllocatedBytes", "MedianWorkUnits"):
                value = stage.get(metric)
                if isinstance(value, bool) or not isinstance(value, (float, int)) \
                        or not math.isfinite(value) or value < 0:
                    raise ValueError(f"Invalid stage metric {metric}: {key}/{stage.get('Name')}")
    if seen != EXPECTED:
        raise ValueError("Missing 48-case compiler stage corpus entries")
    return len(seen)


if __name__ == "__main__":
    count = verify(sys.argv[1])
    print(f"Verified {count} complete compiler-stage provider/workload cases.")
