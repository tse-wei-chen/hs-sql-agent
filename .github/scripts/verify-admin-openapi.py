#!/usr/bin/env python3
"""Strict contract export smoke test: source-backed paths, machine-readable schemas.

Every Controller action is discovered by ASP.NET Core ApiExplorer; DTO-only route
catalogues cannot pass this check. Fails if Management API Key payload contracts
are missing. A separate response-coverage gate can become strict as legacy
IActionResult actions receive typed response metadata.
"""
import json
import sys

with open(sys.argv[1], encoding="utf-8") as fh:
    spec = json.load(fh)

paths = spec.get("paths", {})
operations = {
    (method.upper(), path): op
    for path, methods in paths.items()
    for method, op in methods.items()
    if method.lower() in {"get", "put", "post", "delete", "patch"}
}
assert len(operations) >= 60, f"Expected all Admin controller actions, found only {len(operations)}"
for method, path in [
    ("GET", "/api/runtime/management-api-keys"),
    ("GET", "/api/runtime/management-api-keys/permissions"),
    ("POST", "/api/runtime/management-api-keys"),
    ("POST", "/api/runtime/management-api-keys/{id}/rotate"),
    ("POST", "/api/runtime/management-api-keys/{id}/revoke"),
    ("GET", "/api/CustomSqlTool"),
    ("POST", "/api/CustomSqlTool"),
    ("POST", "/api/CustomSqlTool/{id}/publish"),
    ("POST", "/api/runtime/mcp-keys"),
    ("POST", "/api/runtime/security/sql-explain"),
    ("GET", "/api/runtime/operability/doctor"),
]:
    assert (method, path) in operations, f"Missing {method} {path}"

for method, path in [
    ("POST", "/api/runtime/management-api-keys"),
    ("POST", "/api/runtime/mcp-keys"),
    ("POST", "/api/CustomSqlTool"),
]:
    op = operations[method, path]
    assert "requestBody" in op, f"Missing request schema for {method} {path}"
    assert op.get("responses"), f"Missing responses for {method} {path}"

for method, path in [
    ("POST", "/api/runtime/management-api-keys"),
    ("POST", "/api/runtime/mcp-keys"),
    ("POST", "/api/CustomSqlTool"),
]:
    op = operations[method, path]
    success = [v for k,v in op["responses"].items() if k.startswith("2")]
    assert any("content" in x for x in success), f"Missing typed success schema for {method} {path}"

schemes = spec.get("components", {}).get("securitySchemes", {})
assert schemes.get("ManagementApiKey", {}).get("name") == "X-API-Key"
assert schemes.get("AdminBearer", {}).get("scheme") == "bearer"
def requires(op, scheme):
    return any(scheme in requirement for requirement in op.get("security", []))
assert requires(operations["POST", "/api/CustomSqlTool"], "ManagementApiKey")
assert requires(operations["POST", "/api/CustomSqlTool"], "AdminBearer")
assert requires(operations["POST", "/api/runtime/management-api-keys"], "AdminBearer")
assert not requires(operations["POST", "/api/runtime/management-api-keys"], "ManagementApiKey")
assert not requires(operations["GET", "/api/Auth/first-run"], "ManagementApiKey")
print(f"Verified {len(operations)} Admin REST operations, typed contracts and credential metadata.")
