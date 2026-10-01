#!/usr/bin/env bash
# Genera clave privada + CSR para el certificado de ARCA (homologación) y, una vez descargado el .crt
# desde WSASS, lo empaqueta como .pfx para el microservicio.
#
# Uso:
#   ./generar-certificado.sh csr  <CUIT> <alias> "<razón social>"
#   ./generar-certificado.sh pfx  <alias> <archivo.crt>
set -euo pipefail
cmd=${1:-}
case "$cmd" in
  csr)
    cuit=$2; alias=$3; org=$4
    [[ "$cuit" =~ ^[0-9]{11}$ ]] || { echo "CUIT inválida (11 dígitos)"; exit 1; }
    umask 077
    openssl genrsa -out "$alias.key" 2048
    openssl req -new -key "$alias.key" -subj "/C=AR/O=$org/CN=$alias/serialNumber=CUIT $cuit" -out "$alias.csr"
    echo "Listo: $alias.key (PRIVADA, no compartir) y $alias.csr."
    echo "Subir $alias.csr en WSASS > Nuevo certificado, descargar el .crt y asociarlo al servicio wsfe."
    ;;
  pfx)
    alias=$2; crt=$3
    umask 077
    openssl pkcs12 -export -inkey "$alias.key" -in "$crt" -out "$alias.pfx" -name "$alias"
    echo "Listo: $alias.pfx. Copiarlo a certs/arca.pfx y cargar la contraseña en Arca__CertificadoPassword."
    openssl x509 -in "$crt" -noout -subject -enddate
    ;;
  *) sed -n '2,8p' "$0"; exit 1 ;;
esac
