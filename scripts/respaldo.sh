#!/usr/bin/env bash
# Respaldo consistente del volumen de datos (registro SQLite, TA, auditoría, plantillas, suscripciones, receptores).
# Uso: ./scripts/respaldo.sh <directorio_datos> <directorio_destino>
set -euo pipefail
origen=${1:?directorio de datos}; destino=${2:?directorio destino}
marca=$(date -u +%Y%m%d-%H%M%S)
mkdir -p "$destino"
tmp=$(mktemp -d)
# SQLite: copia en caliente consistente con el comando .backup (requiere sqlite3); si no está, copia el archivo.
if [ -f "$origen/registro.db" ]; then
  if command -v sqlite3 >/dev/null; then sqlite3 "$origen/registro.db" ".backup '$tmp/registro.db'"; else cp "$origen/registro.db" "$tmp/"; fi
fi
rsync -a --exclude 'registro.db*' --exclude 'bloqueos' "$origen/" "$tmp/datos/"
umask 077
tar -czf "$destino/arca-datos-$marca.tar.gz" -C "$tmp" .
rm -rf "$tmp"
echo "Respaldo: $destino/arca-datos-$marca.tar.gz"
# Retención: conserva los últimos 30.
ls -1t "$destino"/arca-datos-*.tar.gz | tail -n +31 | xargs -r rm --
