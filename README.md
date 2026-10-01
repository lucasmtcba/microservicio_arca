# Microservicio de Facturación Electrónica ARCA (C# / .NET 8)

Fachada REST sobre WSAA + WSFEv1 de ARCA (ex AFIP).

## Ejecutar
```bash
dotnet test                                   # 352 pruebas (las de MySQL usan ARCA_TEST_MYSQL o localhost; se omiten si no hay servidor)
docker compose up --build                     # contenedor contra homologación (ver docker-compose.yml)
export Arca__Cuit=20XXXXXXXXX Arca__CertificadoPath=certs/arca.pfx Arca__CertificadoPassword=***
export Seguridad__ApiKeys__0=una-clave-larga
ASPNETCORE_ENVIRONMENT=Development dotnet run --project src/Arca.Facturacion.Api   # Swagger en /swagger
```

## Documentación
- `docs/especificacion-requerimientos.html`: ERS, flujo de emisión, revisión adversaria y limitaciones.
- `docs/openapi.json`, `docs/openapi.yaml`, `docs/swagger.html`: contrato OpenAPI 3.

## Importante
- `Idempotency-Key` es obligatoria en `POST /api/v1/comprobantes`. Ante un 503, reintentar con la MISMA clave.
- Varios servidores: definir `Arca__RegistroEmisionesMySql` (MySQL/MariaDB) en todos. La numeración se serializa con GET_LOCK y el registro local (SQLite/JSON) se migra solo al primer arranque.
- Sin MySQL, una sola instancia emisora por punto de venta (bloqueo en memoria + `BloqueoEntreProcesosPath` en el mismo servidor).
- Firma HMAC v2 (método + ruta + cuerpo) y `/metrics` con clave admin desde 1.4: ver la guía de actualización en la especificación.

## Padrón A13
`GET /api/v1/contribuyentes/{cuit}` consulta ws_sr_padron_a13 (el certificado debe tener ese servicio asociado). `POST /api/v1/contribuyentes/{cuit}/agenda?condicionIva=1` crea el receptor en la agenda.

## Probar sin certificado
`Arca__ModoSimulacion=true`: numeración y CAE ficticios (empiezan con 99), sin conexión con ARCA.

## Herramientas
- `scripts/tareas.sh|.ps1`: build, test, cobertura, simulación, openapi, docker, verificar, clave.
- `scripts/respaldo.sh`: respaldo del volumen de datos.
- `deploy/k8s/`: manifiestos de Kubernetes. `ci/`: CI, CodeQL y Dependabot (mover a `.github/`).
- `examples/php`, `examples/dart`: clientes de ejemplo.
- `src/Arca.Facturacion.Cliente`: SDK .NET (reintentos con la misma Idempotency-Key).
- `tools/arca-facturacion.postman_collection.json`: colección Postman (variables `baseUrl` y `apiKey`).
- `tools/carga-k6.js`: prueba de carga, sólo contra Testing u homologación.
- `scripts/generar-certificado.sh|.ps1`: CSR y .pfx para ARCA.
- `ci/github-actions-ci.yml`: mover a `.github/workflows/ci.yml`.
- Si cambia la API, regenerar `docs/openapi.json`: la prueba de contrato lo exige.
