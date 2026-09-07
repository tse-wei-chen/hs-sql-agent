# Admin JSON formatting

HsSqlAgent Admin controllers use endpoint-scoped System.Text.Json formatters so their public wire contract remains stable without changing the host application's MVC JSON options.

The Admin contract uses camelCase property names, case-insensitive input property matching, and string enum values. Host controllers continue to use the host application's own configured formatters and serializer options.
