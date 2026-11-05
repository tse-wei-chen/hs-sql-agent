"""Regression coverage for the test-layout CI gate."""
import sys
import tempfile
import unittest
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
from verify_test_suite_layout import flat_service_tests


class TestSuiteLayoutGateTests(unittest.TestCase):
    def test_rejects_legacy_flat_service_files(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            flat = root / "backend/src/UnitTest/SqlAgent.Test/Services/FlatTests.cs"
            flat.parent.mkdir(parents=True)
            flat.write_text("// fixture\n", encoding="utf-8")
            self.assertEqual([flat.relative_to(root).as_posix()], flat_service_tests(root))

    def test_nested_test_sources_are_allowed(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for project in ("SqlAgent.Test", "HsSqlAgent.Server.Test"):
                nested = root / "backend/src/UnitTest" / project / "Services/Compiler/NativeTests.cs"
                nested.parent.mkdir(parents=True)
                nested.write_text("// fixture\n", encoding="utf-8")
            self.assertEqual([], flat_service_tests(root))

    def test_other_test_project_roots_are_untouched(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            other = root / "backend/src/UnitTest/Auth.Test/Services/AuthServiceTests.cs"
            other.parent.mkdir(parents=True)
            other.write_text("// fixture\n", encoding="utf-8")
            self.assertEqual([], flat_service_tests(root))


if __name__ == "__main__":
    unittest.main()
