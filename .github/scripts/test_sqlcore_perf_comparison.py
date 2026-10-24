import contextlib
import copy
import importlib.util
import io
import json
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location("comparison", Path(__file__).with_name("compare-sqlcore-perf.py"))
comparison = importlib.util.module_from_spec(spec)
spec.loader.exec_module(comparison)


class ComparisonTests(unittest.TestCase):
    def report(self):
        return {"schemaVersion": 2, "iterations": 300, "warmup": 100,
                "framework": "test-runtime", "os": "test-os", "processArchitecture": "X64",
                "results": [{"Provider": provider, "Case": case, "Iterations": 300,
                             "MeanMicroseconds": 100, "P95Microseconds": 200,
                             "MeanAllocatedBytes": 10000, "OutputDigest": "A" * 64}
                            for provider, case in sorted(comparison.EXPECTED_KEYS)]}

    def run_comparison(self, change=lambda report: None):
        with tempfile.TemporaryDirectory() as directory:
            folder = Path(directory)
            base = self.report()
            head = copy.deepcopy(base)
            change(head)
            for round_number in range(1, 4):
                for prefix, report in (("base", base), ("head", head)):
                    (folder / f"{prefix}-{round_number}.json").write_text(json.dumps(report), encoding="utf-8")
            with contextlib.redirect_stdout(io.StringIO()):
                comparison.compare(folder)
            return json.loads((folder / "comparison.json").read_text(encoding="utf-8"))

    def test_equal_corpus_passes(self):
        self.assertEqual([], self.run_comparison()["failures"])

    def test_missing_case_rejected(self):
        with self.assertRaisesRegex(ValueError, "all 6 providers"):
            self.run_comparison(lambda report: report["results"].pop())

    def test_replaced_case_rejected_even_with_same_count(self):
        with self.assertRaisesRegex(ValueError, "all 6 providers"):
            self.run_comparison(lambda report: report["results"][0].update(Case="unsupported-substitute"))

    def test_output_drift_rejected(self):
        with self.assertRaisesRegex(ValueError, "emitted SQL"):
            self.run_comparison(lambda report: report["results"][0].update(OutputDigest="B" * 64))

    def test_large_regressions_rejected(self):
        for metric in ("MeanMicroseconds", "P95Microseconds", "MeanAllocatedBytes"):
            with self.subTest(metric=metric), self.assertRaisesRegex(ValueError, "exceeds"):
                self.run_comparison(lambda report: report["results"][0].update({metric: 1_000_000}))

    def test_mismatched_runtime_rejected(self):
        with self.assertRaisesRegex(ValueError, "Runtime/settings"):
            self.run_comparison(lambda report: report.update(framework="other-runtime"))

    def test_invalid_numbers_rejected(self):
        for value in (float("nan"), float("inf"), -1, 0):
            with self.subTest(value=value), self.assertRaisesRegex(ValueError, "Invalid"):
                self.run_comparison(lambda report: report["results"][0].update(MeanMicroseconds=value))


if __name__ == "__main__":
    unittest.main()
