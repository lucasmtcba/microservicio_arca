# ---- build ----
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY Directory.Build.props .editorconfig Arca.Facturacion.sln ./
COPY src/Arca.Facturacion.Core/Arca.Facturacion.Core.csproj src/Arca.Facturacion.Core/
COPY src/Arca.Facturacion.Api/Arca.Facturacion.Api.csproj src/Arca.Facturacion.Api/
COPY src/Arca.Facturacion.Cliente/Arca.Facturacion.Cliente.csproj src/Arca.Facturacion.Cliente/
RUN dotnet restore src/Arca.Facturacion.Api/Arca.Facturacion.Api.csproj -r linux-x64
COPY src/ src/
RUN dotnet publish src/Arca.Facturacion.Api/Arca.Facturacion.Api.csproj -c Release -o /app -p:PublishReadyToRun=true -r linux-x64 --self-contained false

# ---- runtime ----
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
RUN apt-get update && apt-get install -y --no-install-recommends tzdata curl && rm -rf /var/lib/apt/lists/*
ARG APP_COMMIT=desconocido
ARG APP_BUILD_DATE=desconocida
# Todas las rutas de datos son ABSOLUTAS bajo /data (volumen escribible): las relativas de appsettings.json
# ("data/...") se resolverían en /app/data, que en el contenedor es de sólo lectura (readOnlyRootFilesystem / usuario app).
ENV TZ=America/Argentina/Buenos_Aires \
    APP_COMMIT=$APP_COMMIT \
    APP_BUILD_DATE=$APP_BUILD_DATE \
    ASPNETCORE_URLS=http://+:8080 \
    DOTNET_RUNNING_IN_CONTAINER=true \
    Logging__Json=true \
    Arca__CacheTicketPath=/data/ta \
    Arca__RegistroEmisionesPath=/data/emisiones \
    Arca__RegistroEmisionesSqlite=/data/registro.db \
    Arca__BloqueoEntreProcesosPath=/data/bloqueos \
    Arca__AuditoriaPath=/data/auditoria/auditoria.jsonl \
    Arca__PlantillasPath=/data/plantillas \
    Arca__ReceptoresPath=/data/receptores \
    Arca__SuscripcionesPath=/data/suscripciones \
    Arca__CertificadoPath=/certs/arca.pfx
WORKDIR /app
COPY --from=build /app .
RUN mkdir -p /data && chown -R app:app /data
USER app
VOLUME ["/data"]
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --retries=3 CMD curl -fsS http://localhost:8080/health/live || exit 1
ENTRYPOINT ["dotnet", "Arca.Facturacion.Api.dll"]
