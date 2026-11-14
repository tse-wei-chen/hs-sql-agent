# Admin REST OpenAPI export

This is a **build-only metadata host**, not a production web service or Swagger UI.

Run from the repository root:

```bash
dotnet build backend/src/Tools/HsSqlAgent.ApiSpec/HsSqlAgent.ApiSpec.csproj -c Release
dotnet run --framework net10.0 --project backend/tools/HsSqlAgent.TestGovernance -- admin-openapi --file artifacts/openapi/HsSqlAgent.ApiSpec_admin.json
```

The generated OpenAPI JSON is uploaded as the `hs-sql-agent-admin-openapi` artifact by the Tests workflow. Release specifications can be copied to the documentation site's static assets and exposed as downloadable contracts.

The exporter discovers all Admin MVC controllers directly from `HsSqlAgent.Server`; it does not start the production runtime, connect to databases, map MCP or expose a Swagger endpoint. OpenAPI schemas come from real DTOs and controller annotations. **Legacy actions returning untyped `IActionResult` may still have incomplete success schemas**; annotate these with `[ProducesResponseType]` before advertising full Request/Response coverage. Never fabricate schemas or publish the document as a complete SDK contract while that coverage is missing.
