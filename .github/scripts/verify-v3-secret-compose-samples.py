#!/usr/bin/env python3
"""Check that both Compose entry points match v3 host SecretEncryption options."""
import base64
import json
import os
import pathlib
import re
import subprocess

root = pathlib.Path(__file__).resolve().parents[2]
settings = json.loads((root / "backend/src/ToolBox/appsettings.Example.json").read_text())
options = settings["SecretEncryption"]
bools = {
    "SECRET_ENCRYPTION_ENABLED": "Enabled",
    "SECRET_ENCRYPTION_ALL_NODES_DUAL_READ_CONFIRMED": "AllNodesDualReadConfirmed",
    "SECRET_ENCRYPTION_V2_DATABASE_PASSWORD_WRITES": "EnableV2DatabasePasswordWrites",
    "SECRET_ENCRYPTION_V2_DURABLE_DML_WRITES": "EnableV2DurableDmlWrites",
}
purposes = {
    "DatabasePassword": "DB",
    "DurableDmlPayload": "DML",
}
assert all(options[name] is False for name in bools.values()), "v3 example switches must be off"
for name, purpose in purposes.items():
    obj = options["Purposes"][name]
    assert obj["ActiveKeyId"] in obj["Keys"]
    assert obj["Keys"][obj["ActiveKeyId"]] == "", "Sample must not contain a usable key"

for envname, compose in (
    (".env.example", "docker-compose.yml"),
    (".env.distributed.example", "docker-compose.distributed.yml"),
):
    env_src = (root / envname).read_text()
    docker_src = (root / compose).read_text()
    env = dict(re.findall(r"^([A-Z][A-Z0-9_]*)=(.*)$", env_src, re.M))
    for name, prop in bools.items():
        assert env.get(name) == "false", f"{envname}: {name} must default false"
        wire = f"SecretEncryption__{prop}=${{{name}:-false}}"
        assert wire in docker_src, f"{compose}: missing {wire}"

    for purpose, short in purposes.items():
        idvar = f"SECRET_ENCRYPTION_{short}_ACTIVE_KEY_ID"
        keyvar = f"SECRET_ENCRYPTION_{short}_KEY_B64"
        kid = options["Purposes"][purpose]["ActiveKeyId"]
        assert env.get(idvar) == kid
        assert env.get(keyvar) == "", "Example secret must be empty"
        assert (
            f"SecretEncryption__Purposes__{purpose}__ActiveKeyId=${{{idvar}:-{kid}}}"
            in docker_src
        ), f"{compose}: missing ActiveKeyId mapping"
        assert (
            f"SecretEncryption__Purposes__{purpose}__Keys__${{{idvar}:-{kid}}}"
            f"=${{{keyvar}:-}}"
            in docker_src
        ), f"{compose}: missing dynamic key-ID binding"
    assert env.get("HS_SQL_AGENT_IMAGE") == "ghcr.io/tse-wei-chen/hs-sql-agent:v2.0.6"
    assert "HS_SQL_AGENT_IMAGE:-ghcr.io/tse-wei-chen/hs-sql-agent:v2.0.6" in docker_src
    assert env.get("HMAC_KEY"), "Original MCP HMAC key must stay available"
    assert "McpKeySettings__HmacSecretKey=${HMAC_KEY}" in docker_src
    # Resolve the actual Compose output with distinct active IDs and
    # deterministic test-only key material. This catches accidental use of
    # the wrong config path, key-ID segment or rollout switch.
    test_values = {
        "SECRET_ENCRYPTION_ENABLED": "true",
        "SECRET_ENCRYPTION_ALL_NODES_DUAL_READ_CONFIRMED": "true",
        "SECRET_ENCRYPTION_V2_DATABASE_PASSWORD_WRITES": "true",
        "SECRET_ENCRYPTION_V2_DURABLE_DML_WRITES": "true",
        "SECRET_ENCRYPTION_DB_ACTIVE_KEY_ID": "db_next_test",
        "SECRET_ENCRYPTION_DB_KEY_B64": base64.b64encode(bytes([1]) * 32).decode(),
        "SECRET_ENCRYPTION_DML_ACTIVE_KEY_ID": "dml_next_test",
        "SECRET_ENCRYPTION_DML_KEY_B64": base64.b64encode(bytes([2]) * 32).decode(),
        "HS_SQL_AGENT_IMAGE": "ghcr.io/tse-wei-chen/hs-sql-agent:v3-test-only",
    }
    expanded = subprocess.run(
        ["docker", "compose", "--env-file", envname, "-f", compose,
         "config", "--format", "json"],
        cwd=root,
        env={**os.environ, **test_values},
        text=True,
        check=True,
        capture_output=True,
    )
    parsed = json.loads(expanded.stdout)["services"]["hs-sql-agent"]
    config = parsed["environment"]
    assert parsed["image"] == test_values["HS_SQL_AGENT_IMAGE"]
    for variable, property_name in bools.items():
        assert config[f"SecretEncryption__{property_name}"] == "true"
    for purpose, short in purposes.items():
        key_id = test_values[f"SECRET_ENCRYPTION_{short}_ACTIVE_KEY_ID"]
        key_b64 = test_values[f"SECRET_ENCRYPTION_{short}_KEY_B64"]
        prefix = f"SecretEncryption__Purposes__{purpose}"
        assert config[f"{prefix}__ActiveKeyId"] == key_id
        assert config[f"{prefix}__Keys__{key_id}"] == key_b64
    assert config["McpKeySettings__HmacSecretKey"] == env["HMAC_KEY"]

print("v3 SecretEncryption samples and both Docker Compose mappings: OK")
