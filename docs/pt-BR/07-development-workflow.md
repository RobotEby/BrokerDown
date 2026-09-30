# Desenvolvimento

Mantenha net8.0, EF Core e MassTransit 8. Os projetos fixam versões exatas. SDK 10 é selecionado pelo global.json, imagem Docker e setup-dotnet 10.0.x na CI; testes locais também precisam do ASP.NET runtime net8.

```bash
dotnet tool restore
dotnet restore ChaosLab.NET.slnx
dotnet build ChaosLab.NET.slnx -c Release --no-restore
dotnet test ChaosLab.NET.slnx -c Release --no-build
docker compose config --quiet
```

Migrations pertencem a PaymentsDb, nunca SimulatedGatewayDb. Este usa transação independente sobre schema gerenciado por PaymentsDb. Use a factory de design com dotnet ef e revise o SQL; dados persistentes exigem backup e migração deliberada.

Uma mudança pode ser revisada quando há verificação executável de sua falha, testes pertinentes passam, Compose continua saudável e documentação EN/PT corresponde ao código. IDs ficam em logs estruturados, nunca em labels. Não versione .env, artifacts ou segredos.

GitHub Actions executa todas as categorias e o demo completo. O demo verifica kill switch ao terminar; reinicie Payments e Worker para limpar esse bloqueio.
