# Durable DML Approval Lifecycle test ownership

This feature uses one **partial xUnit class** across four files. The
class's fully qualified name remains
`HsSqlAgent.Server.Test.Services.DurableDmlApprovalLifecycleTests`;
the test methods, data sets, fixtures and assertions retain their original
source text. All tests still run from the existing `server-test` job.

| File | Responsibility |
| --- | --- |
| `DurableDmlApprovalLifecycleTests.cs` | Durable approval admission, restart, versioned encrypted payload and target execution fence |
| `DurableDmlApprovalLifecycleTests.Witness.cs` | Target commit witness, independent attestation, schema/target mismatch |
| `DurableDmlApprovalLifecycleTests.Outcomes.cs` | Audit failure, uncertain outcome, rollback, replay, revoked key and invalid fingerprint |
| `Support/DurableDmlApprovalLifecycleTests.Fixture.cs` | Shared SQLite/AdminStore fixture, provisioning helpers, setup and cleanup |

The file split preserves all **25 original Fact/Theory entry points**,
including their original parameterized inline data. It does not create
additional xUnit collections, alter parallelism, or change SQL, queries,
production code, or test setup. The private nested fixture remains shared
by the four partial source files.

## Test maintenance rule

New replay/admission cases go in the relevant domain partial file. Provider
physical DML query/transaction tests belong in `../Runtime/`, not in this
AdminStore durable lifecycle suite. Prefer splitting another large test
by behavior and keeping the fixture shared, rather than introducing a giant
helper abstraction that hides the evidence each test is supposed to assert.
