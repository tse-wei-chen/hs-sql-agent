# HsSqlAgent Admin JSON contract test note

These tests intentionally verify two independent guarantees:

1. Host MVC JSON options remain untouched when `AddHsSqlAgentAdminApi()` is composed into an existing ASP.NET Core application.
2. HsSqlAgent Admin controllers still use the product's stable Web JSON contract (camelCase property names, case-insensitive input matching, and string enum values) regardless of the host application's MVC JSON policy.

The second guarantee prevents the embedded Admin frontend and external Admin API clients from inheriting incompatible host serializer conventions.
