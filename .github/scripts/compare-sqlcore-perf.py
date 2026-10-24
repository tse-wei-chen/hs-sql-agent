"""Compare paired Release runs; never drop missing/unsupported cases."""
import json
import math
import statistics
import sys
from pathlib import Path


def load_runs(folder, prefix):
    runs = []
    for path in sorted(folder.glob(f"{prefix}-*.json")):
        report = json.loads(path.read_text(encoding="utf-8"))
        if report["schemaVersion"] != 2 or report["iterations"] < 100 or report["warmup"] < 20:
            raise ValueError(f"Non-comparable benchmark settings: {path}")
        rows = {(r["Provider"], r["Case"]): r for r in report["results"]}
        if len(rows) != 48 or len(rows) != len(report["results"]):
            raise ValueError(f"Expected all 6 providers x 8 workloads: {path}")
        for row in rows.values():
            for metric in ("MeanMicroseconds", "P95Microseconds", "MeanAllocatedBytes"):
                if not math.isfinite(row[metric]) or row[metric] <= 0:
                    raise ValueError(f"Invalid {metric}: {path}")
        runs.append((report, rows))
    if len(runs) != 3:
        raise ValueError(f"Expected three {prefix} runs, got {len(runs)}")
    return runs


def compare(folder):
    baseline, candidate = load_runs(folder, "base"), load_runs(folder, "head")
    settings = ("framework", "os", "processArchitecture", "iterations", "warmup")
    reference = baseline[0][0]
    keys = baseline[0][1].keys()
    for report, rows in baseline + candidate:
        if any(report[k] != reference[k] for k in settings) or rows.keys() != keys:
            raise ValueError("Runtime/settings/workload mismatch")
    failures, results = [], []
    for key in sorted(keys):
        base_rows = [rows[key] for _, rows in baseline]
        head_rows = [rows[key] for _, rows in candidate]
        if len({row["OutputDigest"] for row in base_rows + head_rows}) != 1:
            failures.append(f"{key}: emitted SQL/typed parameter values changed")
        values = {}
        for metric in ("MeanMicroseconds", "P95Microseconds", "MeanAllocatedBytes"):
            before = statistics.median(row[metric] for row in base_rows)
            after = statistics.median(row[metric] for row in head_rows)
            values[metric] = {"base": before, "head": after, "ratio": round(after / before, 4)}
        # Initial broad regression ceilings, not a speedup claim. Absolute
        # slack avoids treating tiny clock/allocation noise as a regression.
        for metric, factor, slack in (("MeanAllocatedBytes", 2.0, 4096),
                                      ("MeanMicroseconds", 3.0, 50),
                                      ("P95Microseconds", 3.0, 100)):
            value = values[metric]
            if value["head"] > factor * value["base"] + slack:
                failures.append(f"{key}: {metric} exceeds {factor}x baseline + {slack}")
        results.append({"provider": key[0], "case": key[1], **values})
    result = {"schemaVersion": 1, "results": results, "failures": failures}
    (folder / "comparison.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
    for row in results:
        print(f"{row['provider']}/{row['case']}: "
              f"allocation {row['MeanAllocatedBytes']['ratio']}x, "
              f"mean {row['MeanMicroseconds']['ratio']}x, "
              f"p95 {row['P95Microseconds']['ratio']}x")
    if failures:
        raise ValueError("\n".join(failures))


if __name__ == "__main__":
    compare(Path(sys.argv[1]))
