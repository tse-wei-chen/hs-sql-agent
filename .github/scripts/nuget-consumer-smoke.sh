#!/usr/bin/env bash
set -euo pipefail

package_source="$(cd "$1" && pwd)"

# Server now has a package dependency on the transport-neutral approval contracts. Pack that
# dependency into the same local source even when a calling workflow still has an older inline
# public-package list, so this smoke test exercises the real NuGet dependency boundary.
if ! find "$package_source" -maxdepth 1 -name 'HsSqlAgent.Approvals.Abstractions.*.nupkg' ! -name '*.symbols.nupkg' -print -quit | grep -q .; then
  dotnet pack backend/src/Modules/HsSqlAgent.Approvals.Abstractions/HsSqlAgent.Approvals.Abstractions.csproj \
    --configuration Release \
    --output "$package_source"
fi

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
using System.Linq;
using HsSqlAgent.Approvals;
using HsSqlAgent.Server.Extensions;
using HsSqlAgent.SqlCore;
using HsSqlAgent.SqlCore.Core.Pipeline;
using HsSqlAgent.SqlCore.Enums;
using Microsoft.Extensions.DependencyInjection;

string[] expectedAssemblies =
[
    "HsSqlAgent.Server",
    "HsSqlAgent.Approvals.Abstractions",
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

var services = new ServiceCollection();
services.AddHsSqlAgentCore().AddHsSqlAgentDmlApproval<SmokeApprovalProvider>();

// Keep the asynchronous approval completion API on the public NuGet consumer surface.
_ = typeof(IDmlApprovalCompletionSink);
var durableCompletion = DmlApprovalCompletion.Approve(
    "dml_smoke",
    new string('A', 64),
    "smoke-reviewer",
    "EXT-SMOKE");
if (durableCompletion.Decision != DmlApprovalDecision.Approved)
    throw new InvalidOperationException("Packed approval contracts did not preserve the completion decision.");

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

if (!command.Parameters.Any(parameter =>
        parameter.Value is IConvertible
        && Convert.ToInt64(parameter.Value) == 1L))
    throw new InvalidOperationException("Packed SqlCore facade did not preserve the predicate literal as a parameter.");

if (command.Sql.Contains("= 1", StringComparison.Ordinal))
    throw new InvalidOperationException("Packed SqlCore facade inlined a predicate literal that must remain parameterized.");

Console.WriteLine($"Compiled public SqlCore query via packed Server dependency: {command.Sql}");

sealed class SmokeApprovalProvider : IDmlApprovalProvider
{
    public ValueTask<DmlApprovalResult> RequestApprovalAsync(
        DmlApprovalRequest request,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(DmlApprovalResult.Reject(request));
}
EOF

dotnet restore "$consumer_dir" --configfile "$consumer_dir/NuGet.Config" --no-cache
dotnet build "$consumer_dir" --configuration Release --no-restore
dotnet run --project "$consumer_dir" --configuration Release --no-build