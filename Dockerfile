FROM node:24-alpine AS frontend-builder
WORKDIR /app/frontend
ENV NODE_ENV=production
RUN corepack enable && corepack prepare pnpm@latest --activate

COPY frontend/package.json frontend/pnpm-lock.yaml ./
RUN pnpm install

COPY frontend/ .
RUN pnpm run generate

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-builder
WORKDIR /app

COPY backend/Directory.Packages.props ./backend/
COPY backend/Directory.Build.props ./backend/
COPY backend/src/Common/Common.csproj ./backend/src/Common/
COPY backend/src/Modules/Admin.Service/Admin.Service.csproj ./backend/src/Modules/Admin.Service/
COPY backend/src/Modules/SqlAgent.Service/SqlAgent.Service.csproj ./backend/src/Modules/SqlAgent.Service/
COPY backend/src/Modules/HsSqlAgent.SqlCore/HsSqlAgent.SqlCore.fsproj ./backend/src/Modules/HsSqlAgent.SqlCore.FSharp/
COPY backend/src/Modules/HsSqlAgent.Provider.Abstractions/HsSqlAgent.Provider.Abstractions.csproj ./backend/src/Modules/HsSqlAgent.Provider.Abstractions/
COPY backend/src/Modules/HsSqlAgent.Provider.PostgreSql/HsSqlAgent.Provider.PostgreSql.csproj ./backend/src/Modules/HsSqlAgent.Provider.PostgreSql/
COPY backend/src/Modules/HsSqlAgent.Provider.MySql/HsSqlAgent.Provider.MySql.csproj ./backend/src/Modules/HsSqlAgent.Provider.MySql/
COPY backend/src/Modules/HsSqlAgent.Provider.Sqlite/HsSqlAgent.Provider.Sqlite.csproj ./backend/src/Modules/HsSqlAgent.Provider.Sqlite/
COPY backend/src/Modules/HsSqlAgent.Provider.SqlServer/HsSqlAgent.Provider.SqlServer.csproj ./backend/src/Modules/HsSqlAgent.Provider.SqlServer/
COPY backend/src/Modules/HsSqlAgent.Provider.Oracle/HsSqlAgent.Provider.Oracle.csproj ./backend/src/Modules/HsSqlAgent.Provider.Oracle/
COPY backend/src/Modules/HsSqlAgent.Provider.Firebird/HsSqlAgent.Provider.Firebird.csproj ./backend/src/Modules/HsSqlAgent.Provider.Firebird/
COPY backend/src/Infrastructure/Infrastructure.csproj ./backend/src/Infrastructure/
COPY backend/src/Modules/Auth.Service/Auth.Service.csproj ./backend/src/Modules/Auth.Service/
COPY backend/src/Modules/HsSqlAgent.PostgresMigrations/HsSqlAgent.PostgresMigrations.csproj ./backend/src/Modules/HsSqlAgent.PostgresMigrations/
COPY backend/src/Modules/HsSqlAgent.SqliteMigrations/HsSqlAgent.SqliteMigrations.csproj ./backend/src/Modules/HsSqlAgent.SqliteMigrations/
COPY backend/src/Modules/HsSqlAgent.Server/HsSqlAgent.Server.csproj ./backend/src/Modules/HsSqlAgent.Server/
COPY backend/src/ToolBox/ToolBox.csproj ./backend/src/ToolBox/
RUN dotnet restore ./backend/src/ToolBox/ToolBox.csproj

COPY backend/ ./backend/

COPY --from=frontend-builder /app/frontend/dist/ ./backend/src/Modules/HsSqlAgent.Server/wwwroot/

RUN dotnet publish ./backend/src/ToolBox/ToolBox.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

COPY --from=backend-builder /app/publish .

ENTRYPOINT ["dotnet", "ToolBox.dll"]
