"""TDD for fail-closed CI test ownership and actual coverage artifacts."""
import json
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parent))
from ci_test_evidence import EvidenceError, audit_repository, validate_evidence


TRX = """<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
<ResultSummary outcome="Completed">
<Counters total="{total}" executed="{executed}" passed="{passed}"
 failed="{failed}" notExecuted="{not_executed}" />
</ResultSummary></TestRun>"""
COBERTURA = '<coverage lines-covered="{covered}" lines-valid="{valid}" line-rate="0.5" />'


def fake_evidence(path, *, total=2, executed=2, passed=2, failed=0,
                  not_executed=0, covered=3, valid=6):
    path.mkdir(parents=True)
    (path / "run.trx").write_text(
        TRX.format(total=total, executed=executed, passed=passed,
                   failed=failed, not_executed=not_executed),
        encoding="utf-8",
    )
    reports = path / "random-run-identifier"
    reports.mkdir()
    (reports / "coverage.cobertura.xml").write_text(
        COBERTURA.format(covered=covered, valid=valid), encoding="utf-8"
    )


class EvidenceValidationTests(unittest.TestCase):
    def test_genuine_trx_and_coverage_are_reported_with_denominator(self):
        with tempfile.TemporaryDirectory() as name:
            path = Path(name)
            fake_evidence(path)
            report = validate_evidence(path, "Common.Test")
            self.assertEqual(report["passed"], 2)
            self.assertEqual(report["lineCoveragePercent"], 50.0)
            self.assertEqual(report["linesValid"], 6)
            self.assertIn("not whole-repository", report["scope"])

    def test_missing_trx_fails_instead_of_claiming_success(self):
        with tempfile.TemporaryDirectory() as name:
            with self.assertRaisesRegex(EvidenceError, "exactly one.*trx"):
                validate_evidence(name, "Common.Test")

    def test_missing_coverage_fails_instead_of_reporting_zero(self):
        with tempfile.TemporaryDirectory() as name:
            path = Path(name)
            fake_evidence(path)
            next(path.rglob("coverage.cobertura.xml")).unlink()
            with self.assertRaisesRegex(EvidenceError, "coverage.cobertura.xml"):
                validate_evidence(path, "Common.Test")

    def test_empty_or_skipped_only_test_suites_fail_closed(self):
        for kwargs in (
            {"total": 0, "executed": 0, "passed": 0},
            {"total": 3, "executed": 0, "passed": 0, "not_executed": 3},
            {"total": 1, "executed": 1, "passed": 0, "failed": 1},
        ):
            with self.subTest(kwargs=kwargs), tempfile.TemporaryDirectory() as name:
                path = Path(name)
                fake_evidence(path, **kwargs)
                with self.assertRaisesRegex(EvidenceError, "empty or failed"):
                    validate_evidence(path, "Common.Test")

    def test_uninstrumented_production_code_is_not_a_valid_coverage_report(self):
        with tempfile.TemporaryDirectory() as name:
            path = Path(name)
            fake_evidence(path, covered=0, valid=0)
            with self.assertRaisesRegex(EvidenceError, "no measurable production"):
                validate_evidence(path, "Common.Test")

    def test_duplicate_trx_files_fail_ambiguity_gate(self):
        with tempfile.TemporaryDirectory() as name:
            path = Path(name)
            fake_evidence(path)
            (path / "old.trx").write_text((path / "run.trx").read_text(), encoding="utf-8")
            with self.assertRaisesRegex(EvidenceError, "exactly one.*trx"):
                validate_evidence(path, "Common.Test")

    def test_genuine_skipped_test_is_counted_not_hidden(self):
        with tempfile.TemporaryDirectory() as name:
            path = Path(name)
            fake_evidence(path, total=3, executed=2, passed=2, not_executed=1)
            report = validate_evidence(path, "Common.Test")
            self.assertEqual(report["notExecuted"], 1)


class SuiteOwnershipTests(unittest.TestCase):
    def test_project_without_ci_owner_is_detected(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            p = root / "backend/src/UnitTest/Common.Test/Common.Test.csproj"
            p.parent.mkdir(parents=True)
            p.write_text("<Project/>", encoding="utf-8")
            workflow = root / ".github/workflows/test.yml"
            workflow.parent.mkdir(parents=True)
            workflow.write_text("jobs:\n  auth-test:\n    steps: []\n", encoding="utf-8")
            with self.assertRaisesRegex(EvidenceError, "No CI test invocation"):
                audit_repository(root)

    def test_noop_assertion_is_detected_even_if_project_is_invoked(self):
        with tempfile.TemporaryDirectory() as name:
            root = Path(name)
            p = root / "backend/src/UnitTest/Common.Test/Common.Test.csproj"
            p.parent.mkdir(parents=True)
            p.write_text("<Project/>", encoding="utf-8")
            (p.parent / "SanityTests.cs").write_text(
                "Assert.True( true );", encoding="utf-8"
            )
            workflow = root / ".github/workflows/test.yml"
            workflow.parent.mkdir(parents=True)
            sections = ["jobs:"]
            for job in ("common-test", "infrastructure-test", "auth-test", "admin-test"):
                sections += [
                    f"  {job}:",
                    "    steps:",
                    "      - run: dotnet test --project backend/src/UnitTest/Common.Test/Common.Test.csproj "
                    '--logger trx --collect:"XPlat Code Coverage" '
                    f"--results-directory artifacts/test-evidence/{job}",
                    "      - run: python3 .github/scripts/ci_test_evidence.py verify "
                    f"--directory artifacts/test-evidence/{job}",
                ]
            workflow.write_text("\n".join(sections), encoding="utf-8")
            with self.assertRaisesRegex(EvidenceError, "No-op test assertion"):
                audit_repository(root)


if __name__ == "__main__":
    unittest.main()
