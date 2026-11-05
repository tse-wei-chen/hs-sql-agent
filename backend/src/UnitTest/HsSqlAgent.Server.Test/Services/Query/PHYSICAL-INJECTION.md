# Physical SQLite Host injection regression

This test exercises **actual MCP Host Query -> F# SqlCore -> first-party SQLite
provider -> on-disk SQLite**, rather than only asserting the compiler's AST or
mocked provider calls.

- 32 stacked query-to-mutation injection attempts (4 separator shapes x 8
  second statement kinds) must fail and preserve the full physical snapshot.
- 5 unauthorized `secrets`-table paths (direct, CTE, JOIN, UNION and EXISTS)
  must fail with no rows returned and no physical state change.
- A SQL injection-shaped **valid string literal** must remain a single bound
  parameter and remain queryable; legitimate `OR` and CTE forms must work.
- Each test uses an isolated SQLite file, disables connection pooling and
  deletes the file during cleanup. No Docker, network access or privileged
  database credentials are needed.

This is a focused SQLite runtime proof, **not** a claim that all six provider
executors or the DML approval/replay pipeline have received equivalent
physical coverage. Those are separate follow-up gates.
