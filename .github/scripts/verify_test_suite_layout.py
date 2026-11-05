#!/usr/bin/env python3
"""Guard against re-creating the 500+ file flat Services test directories.

Only layout is checked; CLR test names and xUnit filters remain unchanged.
"""
from __future__ import annotations

import argparse
from pathlib import Path

TEST_PROJECTS = ("SqlAgent.Test", "HsSqlAgent.Server.Test")


def flat_service_tests(repository: Path) -> list[str]:
    root = repository / "backend" / "src" / "UnitTest"
    flat: list[str] = []
    for project in TEST_PROJECTS:
        directory = root / project / "Services"
        if directory.is_dir():
            flat.extend(str(path.relative_to(repository).as_posix())
                        for path in directory.glob("*.cs") if path.is_file())
    return sorted(flat)


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--check", action="store_true", required=True)
    parser.add_argument("repository", type=Path, nargs="?", default=Path("."))
    args = parser.parse_args()
    flat = flat_service_tests(args.repository.resolve())
    if flat:
        print(f"Test layout violation: {len(flat)} file(s) in flat Services/ directories.")
        for path in flat[:25]:
            print(" - " + path)
        if len(flat) > 25:
            print(f" ... and {len(flat) - 25} more.")
        print("Choose a domain subdirectory; keep existing namespaces and test identities.")
        return 1
    print("Test layout OK: no C# sources directly under the legacy Services/ buckets.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
