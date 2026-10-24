#!/usr/bin/env python3
"""Check that both Compose entry points match v3 host SecretEncryption options."""
import json
import pathlib
import re

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
    assert "HS_SQL_AGENT_IMAGE:-ghcr.io/tse-wei-chen/hs-sql-agent:v2.0.6" in docker_src
    assert env.get("HMAC_KEY"), "Original MCP HMAC key must stay available"
    assert "McpKeySettings__HmacSecretKey=${HMAC_KEY}" in docker_src
print("v3 SecretEncryption samples and both Docker Compose mappings: OK")
