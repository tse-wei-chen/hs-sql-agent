"""TDD contract for the 3.1 SQL compiler evidence ledger (standard library only)."""
import copy
import json
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from sqlcore_coverage_ledger import CoverageLedgerError, build_report


DIALECTS = ("Postgres", "MySQL", "MsSqlServer", "Sqlite", "Oracle", "Firebird")


class CoverageLedgerTests(unittest.TestCase):
    def setUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.addCleanup(self.tmp.cleanup)
        self.directory = Path(self.tmp.name)
        self.query = [
            {
                "name": f"{provider}-query",
                "dialect": provider,
                "targetDialect": provider,
                "sourceVersion": "16.0",
                "targetVersion": "16.0",
                "sql": "SELECT 1",
            }
            for provider in DIALECTS
        ]
        self.dml = [
            {"name": f"{provider}-dml", "dialect": provider, "sql": "DELETE FROM t WHERE id = 1"}
            for provider in DIALECTS
        ]
        self.manifest = {
            "schemaVersion": 1,
            "suites": [
                {
                    "id": "native-versioned-query",
                    "file": "query.json",
                    "statementFamily": "DQL",
                    "requiresVersionProfile": True,
                    "nativeOnly": True,
                    "expectedByProvider": {provider: 1 for provider in DIALECTS},
                },
                {
                    "id": "ordinary-dml",
                    "file": "dml.json",
                    "statementFamily": "DML",
                    "requiresVersionProfile": False,
                    "nativeOnly": True,
                    "expectedByProvider": {provider: 1 for provider in DIALECTS},
                },
            ],
        }
        self.write_fixtures()

    def write_fixtures(self):
        (self.directory / "query.json").write_text(json.dumps(self.query), encoding="utf-8")
        (self.directory / "dml.json").write_text(json.dumps(self.dml), encoding="utf-8")

    def test_six_provider_family_counts_derive_from_real_fixtures(self):
        report = build_report(self.directory, self.manifest)
        self.assertEqual(12, report["totalCases"])
        self.assertEqual(set(DIALECTS), set(report["providers"]))
        for provider in DIALECTS:
            result = report["providers"][provider]
            self.assertEqual(2, result["totalCases"])
            self.assertEqual(1, result["byFamily"]["DQL"])
            self.assertEqual(1, result["byFamily"]["DML"])
            self.assertEqual(1, result["versionProfiledCases"])
        self.assertEqual(12, sum(suite["cases"] for suite in report["suites"]))

    def test_does_not_invent_official_denominator_or_host_parity(self):
        report = build_report(self.directory, self.manifest)
        self.assertEqual("observed-compiler-regression-corpus", report["evidenceLevel"])
        self.assertIsNone(report["officialGrammarDenominator"])
        self.assertFalse(report["liveExecutionProven"])
        self.assertNotIn("coveragePercent", report)

    def test_missing_case_fails_instead_of_silently_reducing_baseline(self):
        self.query.pop()
        self.write_fixtures()
        with self.assertRaisesRegex(CoverageLedgerError, "expectedByProvider"):
            build_report(self.directory, self.manifest)

    def test_duplicate_case_name_fails_even_when_counts_match(self):
        self.query[1]["name"] = self.query[0]["name"]
        self.write_fixtures()
        with self.assertRaisesRegex(CoverageLedgerError, "duplicate"):
            build_report(self.directory, self.manifest)

    def test_wrong_native_target_fails_closed(self):
        self.query[1]["targetDialect"] = "Postgres"
        self.write_fixtures()
        with self.assertRaisesRegex(CoverageLedgerError, "targetDialect"):
            build_report(self.directory, self.manifest)

    def test_missing_required_version_profile_fails_closed(self):
        del self.query[1]["targetVersion"]
        self.write_fixtures()
        with self.assertRaisesRegex(CoverageLedgerError, "targetVersion"):
            build_report(self.directory, self.manifest)

    def test_undeclared_provider_is_not_counted(self):
        self.dml[0]["dialect"] = "UnknownDB"
        self.write_fixtures()
        with self.assertRaisesRegex(CoverageLedgerError, "dialect"):
            build_report(self.directory, self.manifest)

    def test_manifest_rejects_untracked_provider_shape(self):
        invalid = copy.deepcopy(self.manifest)
        del invalid["suites"][0]["expectedByProvider"]["Firebird"]
        with self.assertRaisesRegex(CoverageLedgerError, "expectedByProvider"):
            build_report(self.directory, invalid)


if __name__ == "__main__":
    unittest.main()
