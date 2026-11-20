# Mulse

Mulse is an in-development .NET integration platform. Build flows in a browser-based designer or through an API to fetch, parse, transform, render, and deliver data. Use built-in modules or add plugins for Excel, HL7, SQL Server, or your own integrations.

The BizTalk importer creates disabled draft flows for review. It does not guarantee a complete migration or equivalent behavior.

**Status:** Not production-ready. Test flows with sample data before connecting real systems. The inbound HTTP endpoint is anonymous; do not expose it to untrusted networks.

## Screenshots

![Mulse dashboard](docs/images/dashboard.png)

See the [screenshot gallery](docs/screenshots.md) for the flow designer, BizTalk import review, native orchestration, execution history, modules, configuration, and storage views.

## Run locally

Install the **.NET 11 SDK** and open `Mulse.slnx` in Visual Studio, or follow these steps from the repository root.

1. Set up [local Keycloak](docs/feature-reference.md#local-identity-provider-sandbox) and [OIDC clients and secrets](docs/feature-reference.md#mulse-oidc-authentication). If you already have this setup, start the existing Keycloak stack without replacing its database volume.
2. Build the solution and trust the local HTTPS development certificate:

   ```powershell
   dotnet build .\Mulse.slnx
   dotnet dev-certs https --trust
   ```

3. From `Service`, run `dotnet run --launch-profile Service`. The runtime uses SQLite by default. Its [Development OpenAPI document](http://localhost:5210/openapi/v1.json) is on port 5210.
4. From `Mulse.Ui` in a second terminal, run `dotnet run --launch-profile https` and open [https://localhost:7021](https://localhost:7021). The UI uses `http://localhost:5210` for the API by default; override it with `MulseApi__BaseUrl` if needed.

## Documentation

- [Screenshot gallery](docs/screenshots.md) - dashboard, designer, and administration views
- [Feature reference](docs/feature-reference.md) - flow design, execution, storage, and API behavior
- [Module developer guide](Module-Developer-Guide.md) - build and load custom modules
- [BizTalk import compatibility plan](BizTalk-Import-Compatibility-Plan.md) - supported behavior and known gaps
- [Service container publishing](Service/README.md)
