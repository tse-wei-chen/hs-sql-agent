#!/usr/bin/env python3
"""Verify mandatory v3 key configuration in BOTH docker-compose deployment samples."""
import base64
import json
import os
from pathlib import Path
import re
import subprocess

root = Path(__file__).resolve().parents[2]
example = json.loads((root / "backend/src/ToolBox/appsettings.Example.json").read_text())
assert set(example["SecretEncryption"]) == {"Purposes"}
for purpose, short in (("DatabasePassword", "DB"), ("DurableDmlPayload", "DML")):
    ring = example["SecretEncryption"]["Purposes"][purpose]
    assert ring["Keys"][ring["ActiveKeyId"]] == "", "Never commit real keys in samples."

for env_name, compose in (
    (".env.example", "docker-compose.yml"),
    (".env.distributed.example", "docker-compose.distributed.yml"),
):
    values = dict(re.findall(
        r"^([A-Z][A-Z0-9_]*)=(.*)$", (root / env_name).read_text(), re.M))
    source = (root / compose).read_text()
    assert not any(name.startswith("SECRET_ENCRYPTION_V2_") for name in values)
    assert "SECRET_ENCRYPTION_ENABLED" not in values
    assert "SECRET_ENCRYPTION_ALL_NODES_DUAL_READ_CONFIRMED" not in values
    assert values["HS_SQL_AGENT_IMAGE"] == ""
    assert "${HS_SQL_AGENT_IMAGE:?" in source
    assert values["SECRET_ENCRYPTION_DB_KEY_B64"] == ""
    assert values["SECRET_ENCRYPTION_DML_KEY_B64"] == ""
    test_env = dict(os.environ)
    test_env.update(values)
    test_env.update({
        "HS_SQL_AGENT_IMAGE": "example.test/hs-sql-agent:v3-ci",
        "SECRET_ENCRYPTION_DB_ACTIVE_KEY_ID": "db_rotated_ci",
        "SECRET_ENCRYPTION_DML_ACTIVE_KEY_ID": "dml_rotated_ci",
        "SECRET_ENCRYPTION_DB_KEY_B64": base64.b64encode(bytes([1])*32).decode(),
        "SECRET_ENCRYPTION_DML_KEY_B64": base64.b64encode(bytes([2])*32).decode(),
    })
    result = subprocess.run(
        ["docker","compose","--env-file",env_name,"-f",compose,"config","--format","json"],
        cwd=root,env=test_env,check=True,capture_output=True,text=True)
    service = json.loads(result.stdout)["services"]["hs-sql-agent"]
    assert service["image"] == test_env["HS_SQL_AGENT_IMAGE"]
    config = service["environment"]
    for purpose, short in (("DatabasePassword","DB"),("DurableDmlPayload","DML")):
        kid=test_env[f"SECRET_ENCRYPTION_{short}_ACTIVE_KEY_ID"]
        key=test_env[f"SECRET_ENCRYPTION_{short}_KEY_B64"]
        prefix=f"SecretEncryption__Purposes__{purpose}"
        assert config[f"{prefix}__ActiveKeyId"]==kid
        assert config[f"{prefix}__Keys__{kid}"]==key
    assert all(not x.startswith("SecretEncryption__Enable") for x in config)
    assert config["McpKeySettings__HmacSecretKey"] == values["HMAC_KEY"]
print("v3 mandatory encryption, dynamic key IDs and both Compose mappings: OK")
