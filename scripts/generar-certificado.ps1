# Genera clave privada + CSR para el certificado de ARCA y empaqueta el .pfx (requiere OpenSSL en el PATH,
# por ejemplo el incluido con Git for Windows).
#   .\generar-certificado.ps1 -Accion csr -Cuit 20111111112 -Alias valex -RazonSocial "Mi Empresa SA"
#   .\generar-certificado.ps1 -Accion pfx -Alias valex -Crt valex.crt
param(
  [Parameter(Mandatory)][ValidateSet('csr','pfx')][string]$Accion,
  [string]$Cuit, [Parameter(Mandatory)][string]$Alias, [string]$RazonSocial, [string]$Crt
)
$ErrorActionPreference = 'Stop'
if ($Accion -eq 'csr') {
  if ($Cuit -notmatch '^\d{11}$') { throw 'CUIT inválida (11 dígitos).' }
  openssl genrsa -out "$Alias.key" 2048
  openssl req -new -key "$Alias.key" -subj "/C=AR/O=$RazonSocial/CN=$Alias/serialNumber=CUIT $Cuit" -out "$Alias.csr"
  icacls "$Alias.key" /inheritance:r /grant:r "$($env:USERNAME):(R,W)" | Out-Null
  Write-Host "Listo: $Alias.key (PRIVADA) y $Alias.csr. Subir el CSR en WSASS y asociar el certificado al servicio wsfe."
} else {
  if (-not $Crt) { throw 'Indicar -Crt con el certificado descargado de WSASS.' }
  openssl pkcs12 -export -inkey "$Alias.key" -in $Crt -out "$Alias.pfx" -name $Alias
  openssl x509 -in $Crt -noout -subject -enddate
  Write-Host "Listo: $Alias.pfx. Copiarlo a certs\arca.pfx y cargar la contraseña en Arca__CertificadoPassword."
}
