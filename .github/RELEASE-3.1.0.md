# v3.1.0 release checklist (maintainer-controlled)

This is a release-candidate checklist, not a release announcement. Only merge feature PRs into `develop` through CI. Promotion to `main`, `master` or any `release` ref, and creating/publishing the GitHub Release, are **manual maintainer actions**.

## Freeze and integration

- [ ] Stop speculative G9/G10 grammar expansion for this version; keep new SQL shapes fail-closed until separately proven.
- [ ] Verify the exact final `develop` candidate via a PR containing the latest direct commits: all **18 Tests jobs** and **SqlCore baseline comparison** green at the same PR-head SHA. Check measured coverage, six-provider differential parity, 256 KiB stack/resource budget and C# NuGet consumer smoke.
- [ ] Confirm DQL remains read-only; DML and DDL still require independent permissions, impact preview, human approval and execution-time live proof. Four existing read-only tools selected by default; neither DML nor DDL selected.
- [ ] Review changes to approval and durable-state versions, migration/recovery implications, independent MySQL/Oracle implicit-commit DDL contracts and `OutcomeUnknown`. Preserve unknown as unknown, never claim cross-provider DDL atomicity.
- [ ] Review source/binary CLR facade compatibility, packaging, docs site and samples. Do not advertise unsupported six-provider grammar as executable.

## Release (maintainer only)

- [ ] Confirm `backend/Directory.Build.props` `VersionPrefix=3.1.0`, first `CHANGELOG.md` heading is 3.1.0, and release notes accurately describe limitations. Set the release date in the changelog when actually publishing.
- [ ] Manually promote reviewed `develop` into `main` using the project's chosen release process. Do not auto-merge or cherry-pick blindly.
- [ ] Create/publish `v3.1.0` only from that verified commit. The NuGet and Docker publisher workflows run the shared version gate *before* packaging/pushing and reject mismatched tags. Untagged manual stable NuGet publishing is blocked; a manual pre-release must include a `version_suffix`.
- [ ] Check published NuGet package IDs/versions and .NET Hosting consumer smoke, Docker image digest/signature, deployment configuration, schema migrations and the website. Roll out in stages and monitor denied SQL, approval challenges, audit evidence and outcome-unknown recoveries.
- [ ] If any release gate fails: stop publication and investigate; do not rename a release tag to conceal a mismatch.
