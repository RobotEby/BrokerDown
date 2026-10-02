FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:35d40304542c8689331f8cab17c65926cdf48fe711e289321d71924b230a7d29 AS build
ARG PROJECT
WORKDIR /src
COPY global.json Directory.Build.props Directory.Packages.props ./
COPY src/Shared.Contracts/Shared.Contracts.csproj src/Shared.Contracts/
COPY src/Shared.Infrastructure/Shared.Infrastructure.csproj src/Shared.Infrastructure/
COPY src/Orders.Api/Orders.Api.csproj src/Orders.Api/
COPY src/Payments.Api/Payments.Api.csproj src/Payments.Api/
COPY src/Chaos.Worker/Chaos.Worker.csproj src/Chaos.Worker/
RUN dotnet restore src/${PROJECT}/${PROJECT}.csproj
COPY src/ src/
RUN dotnet publish src/${PROJECT}/${PROJECT}.csproj -c Release --no-restore -o /app

FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:2d584d8147faddb0d678c5748d47953e5b8e18621ed4fb7049a91381d9d7746f
ARG PROJECT
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*
WORKDIR /app
COPY --from=build /app .
ENV ASPNETCORE_URLS=http://+:8080 APP_DLL=${PROJECT}.dll
USER app
HEALTHCHECK --interval=5s --timeout=4s --start-period=30s --retries=12 CMD curl --fail --silent http://localhost:8080/health/ready || exit 1
ENTRYPOINT ["sh", "-c", "exec dotnet $APP_DLL \"$@\"", "--"]
