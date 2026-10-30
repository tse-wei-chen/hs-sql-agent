"""Contract tests for PR production/test/docs/generated code delta evidence."""
import importlib.util
import subprocess
import tempfile
import unittest
from pathlib import Path

spec = importlib.util.spec_from_file_location(
    "code_delta", Path(__file__).with_name("sqlcore_code_delta.py"))
code_delta = importlib.util.module_from_spec(spec)
spec.loader.exec_module(code_delta)


class CodeDeltaTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.git("init", "-q")
        self.git("config", "user.name", "CI Fixture")
        self.git("config", "user.email", "ci-fixture@example.invalid")

    def git(self, *args):
        return subprocess.check_output(
            ["git", "-C", str(self.root), *args], stderr=subprocess.STDOUT,
            text=True).strip()

    def put(self, path, data):
        target = self.root / path
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_text(data, encoding="utf-8")

    def commit(self):
        self.git("add", "-A")
        self.git("commit", "--allow-empty", "-qm", "fixture")
        return self.git("rev-parse", "HEAD")

    def test_all_buckets_and_actual_added_deleted_lines(self):
        self.put("backend/src/Modules/HsSqlAgent.SqlCore/RewriteParser.fs", "old\nkeep\n")
        base = self.commit()
        self.put("backend/src/Modules/HsSqlAgent.SqlCore/RewriteParser.fs", "new\nkeep\nextra\n")
        self.put("backend/src/UnitTest/SqlAgent.Test/Smoke.cs", "test1\ntest2\n")
        self.put(".github/scripts/test_code_delta_contract.py", "assert True\n")
        self.put("backend/src/Modules/HsSqlAgent.SqlCore/README.md", "documentation\n")
        self.put("backend/src/Modules/SqlAgent.Service/Migrations/Initial.cs", "up\ndown\n")
        self.put(".github/workflows/test.yml", "workflow\n")
        head = self.commit()
        result = code_delta.summarize(self.root, base, head)
        self.assertEqual(1, result["schemaVersion"])
        self.assertEqual(base, result["baseCommit"])
        self.assertEqual(head, result["headCommit"])
        self.assertEqual({"added": 2, "deleted": 1, "files": 1, "net": 1}, result["categories"]["production"])
        self.assertEqual({"added": 3, "deleted": 0, "files": 2, "net": 3}, result["categories"]["tests"])
        self.assertEqual({"added": 1, "deleted": 0, "files": 1, "net": 1}, result["categories"]["docs"])
        self.assertEqual({"added": 2, "deleted": 0, "files": 1, "net": 2}, result["categories"]["generatedMigrations"])
        self.assertEqual({"added": 1, "deleted": 0, "files": 1, "net": 1}, result["categories"]["other"])
        self.assertEqual(0, result["binaryFiles"])

    def test_move_from_test_to_production_is_not_hidden_as_rename(self):
        old_path = "backend/src/UnitTest/SqlAgent.Test/Moved.cs"
        new_path = "backend/src/Modules/HsSqlAgent.SqlCore/Moved.cs"
        self.put(old_path, "one\ntwo\n")
        base = self.commit()
        (self.root / new_path).parent.mkdir(parents=True, exist_ok=True)
        self.git("mv", old_path, new_path)
        head = self.commit()
        result = code_delta.summarize(self.root, base, head)
        self.assertEqual(2, result["categories"]["production"]["added"])
        self.assertEqual(2, result["categories"]["tests"]["deleted"])

    def test_uncommitted_changes_are_not_counted(self):
        path = "backend/src/Modules/HsSqlAgent.SqlCore/RewriteParser.fs"
        self.put(path, "before\n")
        base = self.commit()
        self.put(path, "after\n")
        head = self.commit()
        self.put(path, "uncommitted\n")
        self.assertEqual(1, code_delta.summarize(self.root, base, head)["categories"]["production"]["added"])

    def test_binary_changes_are_visible_not_silently_zero(self):
        base = self.commit()
        target = self.root / "backend/src/Modules/HsSqlAgent.SqlCore/Binary.fs"
        target.parent.mkdir(parents=True, exist_ok=True)
        target.write_bytes(b"\x00\x01\x02")
        head = self.commit()
        result = code_delta.summarize(self.root, base, head)
        self.assertEqual(1, result["binaryFiles"])
        self.assertEqual(1, result["categories"]["production"]["files"])

    def test_rejects_unknown_git_revisions(self):
        base = self.commit()
        with self.assertRaises(subprocess.CalledProcessError):
            code_delta.summarize(self.root, base, "nonexistent-revision")


if __name__ == "__main__":
    unittest.main()
