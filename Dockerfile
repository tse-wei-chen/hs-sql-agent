FROM mcr.microsoft.com/dotnet/sdk:10.0 AS build
WORKDIR /src

# Copy project files first for better restore layer caching.
COPY Directory.Packages.props ./
COPY src/Common/Common.csproj src/Common/
COPY src/ToolBox/ToolBox.csproj src/ToolBox/
RUN dotnet restore src/ToolBox/ToolBox.csproj

# Copy the remaining source and publish.
COPY . .
RUN dotnet publish src/ToolBox/ToolBox.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:10.0 AS runtime
WORKDIR /app

ENV ASPNETCORE_URLS=http://+:8080
EXPOSE 8080

COPY --from=build /app/publish .

ENTRYPOINT ["dotnet", "ToolBox.dll"]