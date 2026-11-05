"""Six-provider observed SQL corpus ledger; TDD RED scaffold.

Only checked-in, actually exercised compiler corpus fixtures count as
observed evidence. This never asserts official SQL language coverage.
"""


class CoverageLedgerError(ValueError):
    """Invalid or unverified coverage evidence."""


def build_report(corpus_directory, manifest):
    # Intentionally RED: behavior implemented after the tests demonstrate
    # the missing capability on a real pull_request CI run.
    return {"providers": {}, "totalCases": 0}
