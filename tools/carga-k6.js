// Prueba de carga con k6 (https://k6.io) — SOLO contra el entorno de Testing/homologación.
//   k6 run -e BASE=http://localhost:8080 -e KEY=clave -e PV=3 tools/carga-k6.js
import http from 'k6/http';
import { check, sleep } from 'k6';
import { uuidv4 } from 'https://jslib.k6.io/k6-utils/1.4.0/index.js';

export const options = {
  scenarios: {
    consultas: { executor: 'constant-arrival-rate', rate: 20, timeUnit: '1s', duration: '1m', preAllocatedVUs: 20, exec: 'consultas' },
    emisiones: { executor: 'constant-arrival-rate', rate: 2, timeUnit: '1s', duration: '1m', preAllocatedVUs: 10, exec: 'emisiones' },
  },
  thresholds: { http_req_failed: ['rate<0.01'], 'http_req_duration{scenario:consultas}': ['p(95)<300'] },
};

const BASE = __ENV.BASE || 'http://localhost:8080';
const H = { 'X-Api-Key': __ENV.KEY, 'Content-Type': 'application/json' };
const PV = parseInt(__ENV.PV || '3');

export function consultas() {
  const r = http.get(`${BASE}/api/v1/parametros/tipos-iva`, { headers: H });
  check(r, { '200/304': (x) => x.status === 200 || x.status === 304 });
}

export function emisiones() {
  const hoy = new Date(Date.now() - 3 * 3600 * 1000).toISOString().slice(0, 10).replace(/-/g, '');
  const body = JSON.stringify({ ptoVta: PV, cbteTipo: 6, comprobante: {
    concepto: 1, docTipo: 99, docNro: 0, cbteFch: hoy, impTotal: 121, impNeto: 100, impIVA: 21,
    monId: 'PES', monCotiz: 1, condicionIVAReceptorId: 5, iva: [{ id: 5, baseImp: 100, importe: 21 }] } });
  const r = http.post(`${BASE}/api/v1/comprobantes`, body, { headers: Object.assign({ 'Idempotency-Key': uuidv4() }, H) });
  check(r, { 'aprobado': (x) => x.status === 200 && x.json('resultado') === 'A' });
  sleep(0.1);
}
