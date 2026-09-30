# Development workflow

Keep net8.0, EF Core and MassTransit 8. Dependencies are exact versions in project files. SDK 10 is selected by global.json locally, SDK 10 images in Docker, and setup-dotnet 10.0.x in CI; install the net8 ASP.NET runtime locally for tests.

```bash
dotnet tool restore
dotnet restore ChaosLab.NET.slnx
dotnet build ChaosLab.NET.slnx -c Release --no-restore
dotnet test ChaosLab.NET.slnx -c Release --no-build
docker compose config --quiet
```

Payments migrations belong to PaymentsDb, never SimulatedGatewayDb. The latter intentionally uses an independent transaction but shares the schema managed by PaymentsDb. Use the design-time factory with dotnet ef and review generated SQL; production-like data requires backup and a deliberate migration run.

A change is reviewable when its failure mode has a runnable check, appropriate tests pass, Compose remains healthy, and English/Portuguese documentation matches the implementation. Keep IDs in structured logs, not metric labels. Do not commit .env, artifacts or secrets.

GitHub Actions runs unit/integration/chaos categories and the full Compose demo. The demo intentionally exercises the kill switch at the end; restart Payments and Worker to reset it.
