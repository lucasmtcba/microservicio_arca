# Tareas habituales del proyecto (Windows). Uso: .\scripts\tareas.ps1 <tarea>
param([string]$Tarea = 'ayuda')
$ErrorActionPreference = 'Stop'
Set-Location (Join-Path $PSScriptRoot '..')
switch ($Tarea) {
  'build'      { dotnet build -c Release }
  'test'       { dotnet test -c Release }
  'cobertura'  { dotnet test -c Release --collect:"XPlat Code Coverage" --results-directory TestResults }
  'run'        { $env:ASPNETCORE_ENVIRONMENT = 'Development'; dotnet run --project src/Arca.Facturacion.Api }
  'simulacion' { $env:ASPNETCORE_ENVIRONMENT = 'Development'; $env:Arca__ModoSimulacion = 'true'; dotnet run --project src/Arca.Facturacion.Api }
  'verificar'  { dotnet run --project src/Arca.Facturacion.Api -- verificar }
  'clave'      { dotnet run --project src/Arca.Facturacion.Api -- generar-clave }
  default      { 'Tareas: build | test | cobertura | run | simulacion | verificar | clave' }
}
