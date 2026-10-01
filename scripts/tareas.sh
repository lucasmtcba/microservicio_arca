#!/usr/bin/env bash
# Tareas habituales del proyecto. Uso: ./scripts/tareas.sh <tarea>
set -euo pipefail
cd "$(dirname "$0")/.."
case "${1:-ayuda}" in
  build)      dotnet build -c Release ;;
  test)       dotnet test -c Release ;;
  cobertura)  dotnet test -c Release --collect:"XPlat Code Coverage" --results-directory TestResults ;;
  run)        ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Arca.Facturacion.Api ;;
  simulacion) ASPNETCORE_ENVIRONMENT=Development Arca__ModoSimulacion=true dotnet run --project src/Arca.Facturacion.Api ;;
  openapi)    # Regenera docs/openapi.json (requerido por la prueba de contrato)
              ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS=http://127.0.0.1:5099 Arca__ModoSimulacion=true Seguridad__ApiKeys__0=x \
                dotnet run --project src/Arca.Facturacion.Api & pid=$!; sleep 12
              curl -fsS http://127.0.0.1:5099/swagger/v1/swagger.json -o docs/openapi.json; kill $pid; echo "docs/openapi.json actualizado" ;;
  docker)     docker build --build-arg APP_COMMIT="$(git rev-parse --short HEAD 2>/dev/null || echo local)" --build-arg APP_BUILD_DATE="$(date -u +%FT%TZ)" -t arca-facturacion:1.3.0 . ;;
  verificar)  dotnet run --project src/Arca.Facturacion.Api -- verificar ;;
  clave)      dotnet run --project src/Arca.Facturacion.Api -- generar-clave ;;
  *) echo "Tareas: build | test | cobertura | run | simulacion | openapi | docker | verificar | clave" ;;
esac
