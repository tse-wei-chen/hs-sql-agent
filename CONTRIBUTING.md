# Contributing to hs-sql-agent

Thank you for your interest in contributing! Please read this guide before submitting issues or pull requests.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A supported database (SQLite, PostgreSQL, or MySQL) for local testing

## Development Setup

```bash
git clone https://github.com/your-org/hs-sql-agent.git
cd hs-sql-agent
dotnet restore
```

Copy `appsettings.Local.json` and configure your local database connection, then run:

```bash
cd src/ToolBox
dotnet run
```

## Project Structure

- `src/Common` — shared models used across modules
- `src/ToolBox` — MCP server, tools, strategies, middleware

## Adding a New Database Strategy

1. Create a class in `src/ToolBox/Strategies/` that extends `BaseSqlStrategy`
2. Implement `CreateConnection` and `CreateCompiler`
3. Add the new value to `SqlAgentToolType` enum in `src/ToolBox/Enums/`
4. Register the strategy in `SqlStrategyFactory`

## Adding a New Service Module

```bash
cd src/Modules
dotnet new classlib -n {name}.Service
cd ../..
dotnet sln hs-sql-agent.slnx add ./src/Modules/{name}.Service/{name}.Service.csproj
dotnet add ./src/ToolBox/ToolBox.csproj reference ./src/Modules/{name}.Service/{name}.Service.csproj
```

## Pull Request Guidelines

- Keep changes focused — one feature or fix per PR
- Follow existing code style (C# nullable enabled, implicit usings)
- Add or update tests where applicable
- Ensure `dotnet build` succeeds before submitting

## Reporting Issues

Please include:
- Database provider and version
- Steps to reproduce
- Expected vs actual behaviour
- Any relevant MCP tool input/output
