# Development workflow

The solution uses net10.0, EF Core/tools 10.0.12 and MassTransit 8.5.10. Versions are centralized in Directory.Packages.props. global.json and CI use SDK minimum 10.0.112; Docker pins SDK 10.0.401 by digest, permitted by latestFeature roll-forward. Install the ASP.NET Core 10 runtime for local tests.

```bash
dotnet tool restore
dotnet restore ChaosLab.NET.slnx
dotnet build ChaosLab.NET.slnx -c Release --no-restore
dotnet test ChaosLab.NET.slnx -c Release --no-build
dotnet test tests/Chaos.UnitTests -c Release --no-build --filter Category=Architecture
dotnet ef migrations has-pending-model-changes --project src/Payments.Api --context PaymentsDb --no-build --configuration Release
dotnet list ChaosLab.NET.slnx package --vulnerable --include-transitive --no-restore
docker compose config --quiet
```

Payments migrations belong to PaymentsDb, never SimulatedGatewayDb. The latter intentionally uses an independent transaction but shares the schema managed by PaymentsDb. Use the design-time factory with dotnet ef and review generated SQL; production-like data requires backup and a deliberate migration run.

A change is reviewable when its failure mode has a runnable check, appropriate tests pass, Compose remains healthy, and English/Portuguese documentation matches the implementation. Keep IDs in structured logs, not metric labels. Do not commit .env, artifacts or secrets.

GitHub Actions runs unit/integration/chaos categories and the full Compose demo. The demo intentionally exercises the kill switch at the end; restart Payments and Worker to reset it.

The EF 10 upgrade does not change the schema. Both existing migrations and their data are preserved; tests verify no model differences and read historical gateway values. Do not generate empty migrations for namespace changes.
