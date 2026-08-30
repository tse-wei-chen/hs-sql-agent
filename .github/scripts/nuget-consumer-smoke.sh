#!/usr/bin/env bash
set -euo pipefail

package_source="$(cd "$1" && pwd)"
server_package="$(find "$package_source" -maxdepth 1 -name 'HsSqlAgent.Server.*.nupkg' ! -name '*.symbols.nupkg' -print -quit)"
if [[ -z "$server_package" ]]; then
  echo "HsSqlAgent.Server package was not found in $package_source" >&2
  exit 1
fi

version="$(basename "$server_package")"
version="${version#HsSqlAgent.Server.}"
version="${version%.nupkg}"

major_version="${version%%.*}"
if ! [[ "$major_version" =~ ^[0-9]+$ ]] || (( major_version < 2 )); then
  echo "Breaking SqlCore F# rewrite must be packaged on major version 2 or later; got $version" >&2
  exit 1
fi

consumer_dir="$(mktemp -d)"
trap 'rm -rf "$consumer_dir"' EXIT

dotnet new console --framework net10.0 --no-restore --output "$consumer_dir"
cat > "$consumer_dir/NuGet.Config" <<EOF
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-packages" value="$package_source" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
EOF

dotnet add "$consumer_dir" package HsSqlAgent.Server --version "$version" --source "$package_source" --no-restore
cat > "$consumer_dir/Program.cs" <<'EOF'
using System;
using System.Collections.Generic;
using System.Reflection;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;

string[] expectedAssemblies =
[
    "HsSqlAgent.Server",
    "HsSqlAgent.SqlCore",
    "FSharp.Core",
    "HsSqlAgent.Provider.Abstractions",
    "HsSqlAgent.Provider.PostgreSql",
    "HsSqlAgent.Provider.MySql",
    "HsSqlAgent.Provider.Sqlite",
    "HsSqlAgent.Provider.SqlServer",
    "HsSqlAgent.Provider.Oracle",
    "HsSqlAgent.Provider.Firebird"
];

foreach (string assemblyName in expectedAssemblies)
{
    Assembly assembly = Assembly.Load(assemblyName);
    _ = assembly.GetExportedTypes();
    Console.WriteLine($"Loaded {assembly.GetName().Name} {assembly.GetName().Version}");
}

var validation = new SqlPlanValidationContext(
    "nuget-consumer-smoke-v2",
    new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "users" });

var command = SqlCoreFacade.CompileQuery(
    "SELECT id FROM users WHERE id = 1",
    SqlAgentToolType.Postgres,
    SqlAgentToolType.Postgres,
    validation,
    new SqlExecutionPlanPolicy(10));

if (!command.Sql.Contains("SELECT", StringComparison.OrdinalIgnoreCase))
    throw new InvalidOperationException("Packed SqlCore facade did not produce SQL.");

if (command.Parameters.Length != 1
    || Convert.ToInt64(command.Parameters[0].Value) != 1L)
    throw new InvalidOperationException("Packed SqlCore facade did not preserve parameterization.");

Console.WriteLine($"Compiled public SqlCore query via packed Server dependency: {command.Sql}");
EOF

dotnet restore "$consumer_dir" --configfile "$consumer_dir/NuGet.Config" --no-cache
dotnet build "$consumer_dir" --configuration Release --no-restore
dotnet run --project "$consumer_dir" --configuration Release --no-build
