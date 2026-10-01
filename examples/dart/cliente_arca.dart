// Cliente Dart mínimo (paquete http). En una app Flutter la emisión debería hacerse desde el backend;
// este cliente sirve para paneles internos o herramientas de escritorio con una clave de permiso "consultar".
import 'dart:convert';
import 'package:http/http.dart' as http;

class ErrorArca implements Exception {
  final int estado;
  final String mensaje;
  final List<dynamic> errores;
  ErrorArca(this.estado, this.mensaje, this.errores);
  @override
  String toString() => '[$estado] $mensaje';
}

class ClienteArca {
  final String baseUrl;
  final String apiKey;
  final http.Client _http;
  ClienteArca(this.baseUrl, this.apiKey, {http.Client? cliente}) : _http = cliente ?? http.Client();

  Map<String, String> get _cab => {'X-Api-Key': apiKey, 'Content-Type': 'application/json'};

  Future<Map<String, dynamic>> emitir(int ptoVta, int cbteTipo, Map<String, dynamic> comprobante, String idempotencyKey) async {
    for (var intento = 0; ; intento++) {
      final r = await _http.post(Uri.parse('$baseUrl/api/v1/comprobantes'),
          headers: {..._cab, 'Idempotency-Key': idempotencyKey},
          body: jsonEncode({'ptoVta': ptoVta, 'cbteTipo': cbteTipo, 'comprobante': comprobante}));
      if ((r.statusCode == 503 || r.statusCode == 429) && intento < 3) {
        await Future.delayed(Duration(seconds: int.tryParse(r.headers['retry-after'] ?? '') ?? 2));
        continue; // misma clave: nunca duplica
      }
      return _resultado(r);
    }
  }

  Future<Map<String, dynamic>?> consultar(int ptoVta, int cbteTipo, int nro) async {
    final r = await _http.get(Uri.parse('$baseUrl/api/v1/comprobantes/$ptoVta/$cbteTipo/$nro'), headers: _cab);
    return r.statusCode == 404 ? null : _resultado(r);
  }

  Future<String> urlQr(int ptoVta, int cbteTipo, int nro) async {
    final r = await _http.get(Uri.parse('$baseUrl/api/v1/comprobantes/$ptoVta/$cbteTipo/$nro/qr'), headers: _cab);
    return _resultado(r)['url'] as String;
  }

  Map<String, dynamic> _resultado(http.Response r) {
    final datos = r.body.isEmpty ? <String, dynamic>{} : jsonDecode(utf8.decode(r.bodyBytes)) as Map<String, dynamic>;
    if (r.statusCode >= 200 && r.statusCode < 300) return datos;
    throw ErrorArca(r.statusCode, (datos['detail'] ?? datos['title'] ?? 'Error').toString(), (datos['errores'] as List?) ?? const []);
  }
}
