"""G0 R0: stage telemetry corpus must be complete and never hide failures."""
import importlib.util
import json
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location(
    "stage_contract", Path(__file__).with_name("verify_sqlcore_stage_profile.py"))
stage_contract = importlib.util.module_from_spec(spec)
spec.loader.exec_module(stage_contract)

PROVIDERS = ("Sqlite", "MsSqlServer", "MySQL", "Postgres", "Oracle", "Firebird")
CASES = ("query-filter-order", "query-cte", "dml-update", "dml-delete",
         "dml-insert", "query-union", "query-derived-32", "query-scalar-32")
STAGES = ("Parsing", "Binding", "Normalization", "Validation", "Policy", "Rendering")


def fixture():
    return {
        "schemaVersion": 1, "iterations": 2, "warmup": 1,
        "framework": "test", "os": "test", "processArchitecture": "X64",
        "results": [
            {"Provider": provider, "Case": case, "OutputDigest": "A" * 64,
             "Stages": [{"Name": stage, "Samples": 2, "MedianMicroseconds": 4.0,
                         "MedianAllocatedBytes": 128.0, "MedianWorkUnits": 5.0,
                         "AllSucceeded": True} for stage in STAGES]}
            for provider in PROVIDERS for case in CASES]}


class StageProfileTests(unittest.TestCase):
    def verify(self, change=None):
        report = fixture()
        if change is not None:
            change(report)
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "profile.json"
            path.write_text(json.dumps(report), encoding="utf-8")
            return stage_contract.verify(path)

    def test_complete_48_case_corpus_passes(self):
        self.assertEqual(48, self.verify())

    def test_missing_case_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "48"):
            self.verify(lambda report: report["results"].pop())

    def test_missing_required_stage_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "stage"):
            self.verify(lambda report: report["results"][0]["Stages"].pop())

    def test_failed_stage_is_never_hidden(self):
        with self.assertRaisesRegex(ValueError, "failure"):
            self.verify(lambda report: report["results"][0]["Stages"][0].update(AllSucceeded=False))

    def test_missing_or_nonfinite_metrics_rejected(self):
        with self.assertRaisesRegex(ValueError, "metric"):
            self.verify(lambda report: report["results"][0]["Stages"][0].update(MedianAllocatedBytes=-1))

    def test_sample_count_mismatch_is_rejected(self):
        with self.assertRaisesRegex(ValueError, "sample"):
            self.verify(lambda report: report["results"][0]["Stages"][0].update(Samples=1))

    def test_duplicate_stages_rejected(self):
        with self.assertRaisesRegex(ValueError, "duplicate"):
            self.verify(lambda report: report["results"][0]["Stages"].append(
                dict(report["results"][0]["Stages"][0])))


if __name__ == "__main__":
    unittest.main()
