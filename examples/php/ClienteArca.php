<?php
/**
 * Cliente PHP mínimo del microservicio de facturación ARCA (PHP 8.1+, extensión curl).
 * Regla de oro: una misma venta = una misma Idempotency-Key, también en los reintentos.
 * Guardar la clave junto a la venta en MySQL ANTES de llamar, y reutilizarla si hay que reintentar.
 */
final class ClienteArca
{
    public function __construct(
        private string $baseUrl,
        private string $apiKey,
        private ?string $secretoFirma = null,
        private int $reintentos = 3,
    ) {}

    /** Emite un comprobante. $idempotencyKey debe persistirse con la venta. */
    public function emitir(int $ptoVta, int $cbteTipo, array $comprobante, string $idempotencyKey): array
    {
        return $this->enviar('POST', '/api/v1/comprobantes',
            ['ptoVta' => $ptoVta, 'cbteTipo' => $cbteTipo, 'comprobante' => $comprobante], $idempotencyKey);
    }

    public function consultar(int $ptoVta, int $cbteTipo, int $nro): ?array
    {
        try { return $this->enviar('GET', "/api/v1/comprobantes/$ptoVta/$cbteTipo/$nro"); }
        catch (ErrorArca $e) { if ($e->estado === 404) return null; throw $e; }
    }

    public function estado(string $idempotencyKey): ?array
    {
        try { return $this->enviar('GET', '/api/v1/comprobantes/emisiones/' . rawurlencode($idempotencyKey)); }
        catch (ErrorArca $e) { if ($e->estado === 404) return null; throw $e; }
    }

    public function calcular(array $solicitud): array
    {
        return $this->enviar('POST', '/api/v1/comprobantes/calcular', $solicitud);
    }

    /** Ruta + query de una URL absoluta, sin decodificar (lo que viaja en la línea de solicitud HTTP). */
    private static function destino(string $url): string
    {
        $p = parse_url($url);
        $ruta = ($p['path'] ?? '') === '' ? '/' : $p['path'];
        return isset($p['query']) ? $ruta . '?' . $p['query'] : $ruta;
    }

    private function enviar(string $metodo, string $ruta, ?array $cuerpo = null, ?string $clave = null): array
    {
        $json = $cuerpo === null ? '' : json_encode($cuerpo, JSON_UNESCAPED_UNICODE | JSON_PRESERVE_ZERO_FRACTION);
        for ($intento = 0; ; $intento++) {
            $cab = ['X-Api-Key: ' . $this->apiKey, 'Accept: application/json'];
            if ($json !== '') $cab[] = 'Content-Type: application/json';
            if ($clave !== null) $cab[] = 'Idempotency-Key: ' . $clave;
            $url = rtrim($this->baseUrl, '/') . $ruta;
            if ($this->secretoFirma !== null) {
                $t = (string) time();
                $cab[] = 'X-Firma-Tiempo: ' . $t;
                // Firma v2: "v2\n{tiempo}\n{MÉTODO}\n{ruta?query}\n{cuerpo}". Se firma EXACTAMENTE el destino que curl
                // envía en la línea de solicitud: ruta (con el prefijo de baseUrl, si lo hay) + query, tal cual codificadas
                // (rawurlencode en los segmentos variables). El servicio verifica contra el destino crudo, sin decodificar.
                $destino = self::destino($url);
                $cab[] = 'X-Firma: ' . hash_hmac('sha256', "v2\n$t\n" . strtoupper($metodo) . "\n$destino\n" . $json, $this->secretoFirma);
            }
            $ch = curl_init($url);
            curl_setopt_array($ch, [
                CURLOPT_CUSTOMREQUEST => $metodo, CURLOPT_RETURNTRANSFER => true, CURLOPT_HTTPHEADER => $cab,
                CURLOPT_TIMEOUT => 120, CURLOPT_HEADER => true,
            ]);
            if ($json !== '') curl_setopt($ch, CURLOPT_POSTFIELDS, $json);
            $resp = curl_exec($ch);
            $errorRed = $resp === false;
            $estado = (int) curl_getinfo($ch, CURLINFO_RESPONSE_CODE);
            $tamCab = (int) curl_getinfo($ch, CURLINFO_HEADER_SIZE);
            curl_close($ch);

            // Reintentos SOLO ante red/503/429/504 y SIEMPRE con la misma Idempotency-Key.
            if (($errorRed || in_array($estado, [429, 503, 504], true)) && $intento < $this->reintentos) {
                $espera = 1;
                if (!$errorRed && preg_match('/^Retry-After:\s*(\d+)/mi', substr($resp, 0, $tamCab), $m)) $espera = min(30, (int) $m[1]);
                sleep(max($espera, 2 ** $intento));
                continue;
            }
            if ($errorRed) throw new ErrorArca(0, 'Sin conexión con el microservicio', []);
            $datos = json_decode(substr($resp, $tamCab), true) ?? [];
            if ($estado >= 200 && $estado < 300) return $datos;
            throw new ErrorArca($estado, $datos['detail'] ?? $datos['title'] ?? 'Error', $datos['errores'] ?? [], $datos['codigo'] ?? null);
        }
    }
}

final class ErrorArca extends RuntimeException
{
    public function __construct(public int $estado, string $mensaje, public array $errores, public ?string $codigo = null)
    {
        parent::__construct("[$estado] $mensaje");
    }
}

/* Ejemplo:
$arca = new ClienteArca('http://localhost:8080', getenv('ARCA_API_KEY'));
$clave = 'venta-' . $ventaId;               // guardada en la tabla de ventas
$r = $arca->emitir(3, 6, [
    'concepto' => 1, 'docTipo' => 99, 'docNro' => 0, 'impTotal' => 121, 'impNeto' => 100, 'impIVA' => 21,
    'monId' => 'PES', 'monCotiz' => 1, 'condicionIVAReceptorId' => 5,
    'iva' => [['id' => 5, 'baseImp' => 100, 'importe' => 21]],
    'referenciaExterna' => "venta:$ventaId",
], $clave);
echo $r['descripcion'] . ' CAE ' . $r['cae'];
*/
