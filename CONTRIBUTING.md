# Contributing to hs-sql-agent

Thank you for your interest in contributing! Please read this guide before submitting issues or pull requests.

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- A supported database (SQLite, PostgreSQL, or MySQL .......) for local testing

## Development Setup

```bash
git clone https://github.com/tse-wei-chen/hs-sql-agent.git
cd hs-sql-agent
dotnet restore
```

Copy `appsettings.sample.json` and configure your local database connection, then run:

```bash
cd backend/src/ToolBox
dotnet run
```

## Project Structure

- `src/Common` — shared models and services used across modules
- `src/Modules` — core business logic, including database strategies (see SqlAgent.Service)
- `src/ToolBox` — MCP server, tools, controllers, middleware

## Adding a New Database Strategy

1. Create a class in `src/Modules/SqlAgent.Service/Strategies/` that extends `BaseSqlStrategy`
2. Implement `CreateConnection` and `CreateCompiler`
3. Add the new value to `SqlAgentToolType` enum in `src/Modules/SqlAgent.Service/Enums/`
4. Register the strategy in `SqlStrategyFactory` in `src/Modules/SqlAgent.Service/Factories/`

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
