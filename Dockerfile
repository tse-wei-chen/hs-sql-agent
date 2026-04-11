FROM node:24-alpine AS frontend-builder
WORKDIR /app/frontend

RUN corepack enable && corepack prepare pnpm@latest --activate

COPY frontend/package.json frontend/pnpm-lock.yaml ./
RUN pnpm install

COPY frontend/ .
RUN pnpm run generate

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend-builder
WORKDIR /app

COPY backend/Directory.Packages.props ./backend/
COPY backend/src/Common/Common.csproj ./backend/src/Common/
COPY backend/src/Modules/Admin.Service/Admin.Service.csproj ./backend/src/Modules/Admin.Service/
COPY backend/src/Modules/SqlAgent.Service/SqlAgent.Service.csproj ./backend/src/Modules/SqlAgent.Service/
COPY backend/src/Modules/SqlKata.Service/Directory.Packages.props ./backend/src/Modules/SqlKata.Service/
COPY backend/src/Modules/SqlKata.Service/QueryBuilder/QueryBuilder.csproj ./backend/src/Modules/SqlKata.Service/QueryBuilder/
COPY backend/src/Modules/SqlKata.Service/SqlKata.Execution/SqlKata.Execution.csproj ./backend/src/Modules/SqlKata.Service/SqlKata.Execution/
COPY backend/src/ToolBox/ToolBox.csproj ./backend/src/ToolBox/
RUN dotnet restore ./backend/src/ToolBox/ToolBox.csproj

COPY backend/ ./backend/

COPY --from=frontend-builder /app/frontend/dist/ ./backend/src/ToolBox/wwwroot/

RUN dotnet publish ./backend/src/ToolBox/ToolBox.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

COPY --from=backend-builder /app/publish .

ENTRYPOINT ["dotnet", "ToolBox.dll"]