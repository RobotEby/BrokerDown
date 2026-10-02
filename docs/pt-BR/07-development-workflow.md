# Desenvolvimento

A solução usa net10.0, EF Core/ferramentas 10.0.12 e MassTransit 8.5.10. As versões estão centralizadas em Directory.Packages.props. global.json e CI usam SDK mínimo 10.0.112; o Docker usa SDK 10.0.401 fixado por digest, permitido pelo rollForward latestFeature. Instale o runtime ASP.NET Core 10 para testes locais.

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

Migrations pertencem a PaymentsDb, nunca SimulatedGatewayDb. Este usa transação independente sobre schema gerenciado por PaymentsDb. Use a factory de design com dotnet ef e revise o SQL; dados persistentes exigem backup e migração deliberada.

Uma mudança pode ser revisada quando há verificação executável de sua falha, testes pertinentes passam, Compose continua saudável e documentação EN/PT corresponde ao código. IDs ficam em logs estruturados, nunca em labels. Não versione .env, artifacts ou segredos.

GitHub Actions executa todas as categorias e o demo completo. O demo verifica kill switch ao terminar; reinicie Payments e Worker para limpar esse bloqueio.

A migração para EF 10 não modifica o schema. As duas migrations existentes e seus dados são preservados; testes verificam ausência de diferenças no modelo e leitura dos gateways antigos. Não gere migrations vazias para mudanças de namespaces.
