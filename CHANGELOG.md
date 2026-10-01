# Changelog

## 1.4.1 (01/10/2026)

Revisión adversaria completa y corrección automática. Detalle en la sección 26 de la especificación.

- **Alta / Emisión**: Dos claves podían tomar el mismo número si una quedaba pendiente (y una reconciliación podía asignar el CAE de otra clave). → Antes de numerar se resuelven en ARCA los pendientes del mismo PV/tipo; si no se puede, 503 sin llamar a FECAESolicitar. Una reconciliación no asigna un comprobante ya autorizado para otra clave.
- **Alta / Emisión**: Reconciliador y resolución manual sin bloqueo entre procesos (doble emisión con varios servidores o la CLI). → Toman el bloqueo distribuido/archivo; la CLI reconciliar respeta una antigüedad mínima segura.
- **Alta / Persistencia**: Migración JSON→SQLite ignoraba el cifrado y perdía pendientes. → Descifra, cuenta ilegibles (MigracionIlegibles) y avisa.
- **Alta / Seguridad**: Un cliente podía emitir NC, NC parcial o duplicar sobre comprobantes de otro. → Filtro [ComprobanteDelCliente] (404, sin consumir cuota; admin exento).
- **Alta / Seguridad**: Un cliente podía leer comprobantes de otro (por número y por rango). → Mismo filtro y rango filtrado por dueño, con índice en memoria.
- **Media / Emisión**: El 10016 sólo se buscaba en Errores (ARCA lo informa en Observaciones). → Se busca en ambos; el simulador lo devuelve como ARCA; renumeración efectiva.
- **Media / Resiliencia**: Cancelar la primera llamada compartida cancelaba a todos los que esperaban. → La llamada compartida no usa el token del primero; cada uno espera con el suyo.
- **Media / Idempotencia**: Claves de distintos clientes podían colisionar usando ':'. → Idempotency-Key con ':' se rechaza; largo total ≤ 191.
- **Media / Idempotencia**: La purga del registro reabría la idempotencia. → La purga deja un registro compacto (número, CAE, QR) que sigue respondiendo.
- **Media / Notas de crédito**: NC parciales podían acreditar más que el total; anular dos veces. → Control de saldo acumulado; anular se rechaza si ya hay NC.
- **Media / Notas de crédito**: La anulación copiaba opcionales no válidos en NC. → Se descartan 2101, 2102 y 27; NC FCE con opcional 22.
- **Media / Notas de crédito**: Duplicar en moneda extranjera dejaba MonCotiz en 0. → Consulta la cotización a ARCA.
- **Media / Validación**: Clase B aceptaba receptor RI/monotributista (RG 5616). → Rechazo local.
- **Media / Validación**: FCE sin opcional 27 (SCA/ADC). → Exigido.
- **Media / Cálculo**: Con IVA incluido, 100 daba total 99,99. → Cálculo por bruto: total igual a lo cobrado.
- **Media / Seguridad**: Lotes cortados a mitad no descontaban cuota; devolución en otro día. → Consumo contado ítem a ítem; devolución al día de la reserva.
- **Media / Seguridad**: /metrics y salud detallada no validaban vencimiento, IP ni firma. → Validación completa compartida con el middleware; admite Bearer.
- **Media / Seguridad**: Detrás de un ingress: bloqueo por fuerza bruta de todos los clientes y listas de IP inútiles. → Api:RedesConfiables (CIDR), aviso al arrancar, sin clave no cuenta como fallo.
- **Media / Seguridad**: Firma HMAC fallaba con ids con ':', espacios o prefijo de ruta. → Se firma el destino crudo; SDK .NET y PHP firman lo que envían.
- **Media / Operación**: Una URL de webhook inválida detenía el servicio. → Validación al iniciar y captura de errores en el envío; /admin/config no se rompe.
- **Media / Despliegue**: Plantillas/receptores/suscripciones en ruta de sólo lectura en contenedor. → Rutas en /data en Dockerfile, compose y k8s.
- **Baja / Emisión**: Pendiente liberado entre lecturas se reenviaba con detalle viejo. → Se reinicia el flujo completo.
- **Baja / Persistencia**: Filas ilegibles: excepciones en SQLite y null en MySQL (reabría la clave). → Error claro en ambos; plantillas y repositorios toleran archivos dañados.
- **Baja / Resiliencia**: El circuito semiabierto dejaba pasar todo. → Una sola llamada de prueba.
- **Baja / Validación**: Clase M sin exigir CUIT; NC FCE asociando no FCE; tributos con >2 decimales; ImpIVA sin detalle. → Reglas agregadas.
- **Baja / QR**: Base64 sin escapar en la URL del QR. → Escapado.
- **Baja / Impresión**: «SON PESOS UN» / «VEINTIUN» al final. → UNO / VEINTIUNO al final; UN/VEINTIUN delante de MIL/MILLONES.
- **Baja / Seguridad**: Marca de tiempo fuera de rango daba 500; un acierto reiniciaba el contador de fuerza bruta; caché anti-repetición recorrida en cada solicitud. → 401; contador por ventana; limpieza periódica.
- **Baja / Despliegue**: Prometheus en k8s recibía 401. → Scrape con credencial documentado (métricas siguen protegidas).
- **Baja / Calidad**: Prueba de auditoría intermitente por archivo compartido. → Cada fábrica de pruebas usa su propio directorio.

## 1.4.0 (01/10/2026)

150 mejoras (251 a 400). Detalle y cambios incompatibles en `docs/especificacion-requerimientos.html` (secciones 24 y 25).

251. **Persistencia**: Registro de emisiones en MySQL/MariaDB (Arca:RegistroEmisionesMySql).
252. **Persistencia**: Varios servidores de API pueden compartir el mismo registro de emisiones.
253. **Persistencia**: Tabla InnoDB utf8mb4 creada automáticamente al iniciar.
254. **Persistencia**: Índice por punto de venta, tipo y número.
255. **Persistencia**: Índice por fecha del comprobante.
256. **Persistencia**: Índice por estado (búsqueda rápida de pendientes).
257. **Persistencia**: Índice por referencia externa.
258. **Persistencia**: Índice por cliente.
259. **Persistencia**: Columna de última actualización mantenida por la base.
260. **Persistencia**: Alta o actualización atómica con ON DUPLICATE KEY UPDATE.
261. **Persistencia**: Reintento automático ante deadlock y timeout de bloqueo (hasta 4 intentos con espera creciente).
262. **Persistencia**: Reintento ante errores transitorios de conexión.
263. **Persistencia**: Nombre de tabla configurable y validado (sin inyección SQL).
264. **Persistencia**: Consultas siempre parametrizadas.
265. **Persistencia**: Cifrado AES-GCM en reposo también en MySQL.
266. **Persistencia**: Filas ilegibles en MySQL se omiten y se cuentan (UltimasIlegibles).
267. **Persistencia**: Listado de pendientes por índice en MySQL.
268. **Persistencia**: Recorte seguro de cliente y referencia al largo de columna indexada.
269. **Persistencia**: Ping de conectividad para diagnóstico.
270. **Persistencia**: Migración automática desde SQLite al primer arranque con MySQL vacío.
271. **Persistencia**: Migración automática desde el registro JSON si no hay SQLite.
272. **Persistencia**: La migración nunca pisa claves existentes y devuelve copiados/omitidos.
273. **Persistencia**: Opción MigrarRegistroAMySql para desactivar la migración.
274. **Persistencia**: Prioridad de backends: MySQL, SQLite, JSON, memoria.
275. **Concurrencia**: Bloqueo de numeración distribuido con GET_LOCK entre servidores.
276. **Concurrencia**: Nombre de bloqueo por CUIT, punto de venta y tipo (no bloquea otros tipos).
277. **Concurrencia**: Espera máxima del bloqueo distribuido acotada (1 a 3600 s).
278. **Concurrencia**: Liberación con RELEASE_LOCK e idempotente ante doble Dispose.
279. **Concurrencia**: Si se corta la conexión, el servidor libera el bloqueo solo.
280. **Concurrencia**: Conexión cerrada si falla la adquisición (sin fugas).
281. **Concurrencia**: Opción BloqueoDistribuidoMySql para elegir el bloqueo por archivo.
282. **Concurrencia**: En simulación no se usa el bloqueo distribuido.
283. **Padrón**: Cliente del padrón A13 de ARCA (ws_sr_padron_a13, getPersona).
284. **Padrón**: Ticket de acceso propio del servicio de padrón, cacheado por WSAA.
285. **Padrón**: URL del padrón por ambiente con override PadronUrl.
286. **Padrón**: Parseo tolerante a espacios de nombres y campos faltantes.
287. **Padrón**: Denominación: razón social o "Apellido, Nombre".
288. **Padrón**: Estado de clave y marca Activo.
289. **Padrón**: Lista de domicilios con tipo, dirección, localidad, código postal y provincia.
290. **Padrón**: Domicilio fiscal resumido en una línea.
291. **Padrón**: "No existe persona" se informa como 404 y no como error.
292. **Padrón**: Caché de consultas con vencimiento configurable (PadronCacheHoras).
293. **Padrón**: Caché negativa más corta (10 min) para CUIT inexistentes.
294. **Padrón**: Consultas simultáneas de la misma CUIT se unifican (single-flight).
295. **Padrón**: Caché acotada en memoria.
296. **Padrón**: Padrón simulado en ModoSimulacion.
297. **Padrón**: El padrón simulado devuelve inactivas las CUIT terminadas en 0 para probar el caso.
298. **Padrón**: Endpoint GET /api/v1/contribuyentes/{cuit}.
299. **Padrón**: Validación de dígito verificador antes de consultar (400).
300. **Padrón**: Endpoint POST /api/v1/contribuyentes/{cuit}/agenda: alta del receptor desde el padrón.
301. **Padrón**: El alta en agenda exige condicionIva válida (1..16).
302. **Padrón**: El alta en agenda rechaza CUIT con clave inactiva (422).
303. **Padrón**: El alta en agenda actualiza el receptor si ya existía (id cuit-NNN).
304. **Padrón**: Alta en agenda requiere permiso emitir; la consulta, consultar.
305. **Padrón**: Cliente HTTP del padrón con los mismos proxy, timeout y anclaje TLS que WSFE.
306. **Suscripciones**: Fin de la facturación repetida del mismo período tras una caída.
307. **Suscripciones**: Cada ejecución emite como máximo un período por suscripción.
308. **Suscripciones**: Tope de períodos atrasados facturables solos (SuscripcionesMaxPeriodosAtrasados).
309. **Suscripciones**: Suscripción retenida cuando hay más atrasos que el tope.
310. **Suscripciones**: Endpoint POST /suscripciones/{id}/confirmar-atrasos.
311. **Suscripciones**: Destrabado automático tras conflicto de idempotencia (resuelve el pendiente).
312. **Suscripciones**: Detalle congelado antes de emitir (el reintento manda exactamente lo mismo).
313. **Suscripciones**: Período en curso guardado para recuperarse después de un reinicio.
314. **Suscripciones**: FchServDesde/Hasta siempre del período facturado.
315. **Suscripciones**: Fecha del comprobante dentro de la ventana permitida por ARCA.
316. **Suscripciones**: Mensual anclada al día de inicio (sin corrimiento 31→28→28).
317. **Suscripciones**: Quincenal fija en días 1 y 16.
318. **Suscripciones**: Respetan el modo mantenimiento.
319. **Suscripciones**: Consumen la cuota diaria del cliente.
320. **Suscripciones**: Ejecuciones concurrentes serializadas con lock.
321. **Suscripciones**: Alta, modificación y baja serializadas con la ejecución.
322. **Suscripciones**: Una suscripción con error no frena a las demás.
323. **Suscripciones**: Una suscripción corrupta en disco se omite y se cuenta.
324. **Suscripciones**: Máximo de suscripciones por cliente.
325. **Suscripciones**: El historial lo conserva el servicio, no el controlador.
326. **Suscripciones**: Servicio desechable (libera el semáforo).
327. **Datos**: Prefijo de cliente por hash: "acme-1" y "acme1" ya no comparten archivos.
328. **Datos**: Mismo prefijo por hash en plantillas, receptores y suscripciones.
329. **Datos**: Listados tolerantes a archivos dañados.
330. **Datos**: Reemplazo de archivo con reintentos (antivirus o indexador en Windows).
331. **Datos**: Registro SQLite tolerante a filas que no se pueden descifrar.
332. **Datos**: Registro JSON cuenta ilegibles y no se corta por clave de cifrado errónea.
333. **Datos**: Importación sólo de emisiones autorizadas con CAE, hash y número.
334. **Datos**: Importación rechaza pendientes (evita reenviar números a ARCA).
335. **Datos**: En simulación el registro es en memoria: nunca se mezclan CAE ficticios con los reales.
336. **Datos**: En simulación se ignoran las rutas de receptores y suscripciones.
337. **Auditoría**: Ancla de la cadena de hashes en archivo aparte.
338. **Auditoría**: Detecta borrado de líneas al final del archivo.
339. **Auditoría**: Detecta borrado de archivos diarios completos.
340. **Auditoría**: La purga registra el hash de corte y la verificación encadena desde allí.
341. **Auditoría**: Total de eventos purgados conservado en el ancla.
342. **Auditoría**: Selección del archivo actual por fecha de escritura.
343. **Auditoría**: El escritor de auditoría sobrevive a cualquier excepción, no sólo de E/S.
344. **Auditoría**: Líneas dañadas toleradas al verificar y consultar.
345. **Seguridad**: Firma HMAC v2: cubre método, ruta y query además de cuerpo y hora.
346. **Seguridad**: Protección contra repetición de firmas (caché de firmas usadas).
347. **Seguridad**: Lectura del cuerpo para firmar limitada a 1 MB.
348. **Seguridad**: Cliente .NET firma con v2 automáticamente.
349. **Seguridad**: Cliente PHP de ejemplo actualizado a la firma v2.
350. **Seguridad**: Rate limiting por cliente resuelto, no por la clave enviada (no se evade con claves inventadas).
351. **Seguridad**: Rate limiting anónimo por IP, IPv6 agrupado por /64.
352. **Seguridad**: /metrics exige clave admin (salvo Api:MetricasPublicas=true).
353. **Seguridad**: QR PNG e impresión HTML verifican que el comprobante sea del cliente.
354. **Seguridad**: El endpoint /qr también verifica la pertenencia.
355. **Seguridad**: Los administradores conservan acceso a todos los comprobantes.
356. **Seguridad**: Cuota: reserva atómica antes de emitir (sin carrera).
357. **Seguridad**: Cuota: un lote reserva tantas unidades como comprobantes.
358. **Seguridad**: Cuota: se devuelven las unidades no usadas.
359. **Seguridad**: Cuota: las respuestas repetidas (idempotentes) no consumen.
360. **Seguridad**: Código de error CUOTA_AGOTADA.
361. **Validación**: Catálogo de tipos de documento completo (0 a 24, 30, 80, 86 a 96, 99).
362. **Reportes**: El cierre diario resta las notas de crédito.
363. **Impresión**: Importe en letras con resguardo numérico para montos de más de 12 cifras.
364. **Observabilidad**: Etiquetas de métricas escapan saltos de línea.
365. **Observabilidad**: Gauge de pendientes recalculado como mucho cada 30 s.
366. **Observabilidad**: Gauges a prueba de reinicio del host: no rompen /metrics con un contenedor ya liberado.
367. **Observabilidad**: Un gauge defectuoso no tumba la exposición de métricas.
368. **Calidad**: Pruebas de MySQL contra MariaDB real (alta, upsert, baja, pendientes).
369. **Calidad**: Prueba de cifrado y filas ilegibles en MySQL.
370. **Calidad**: Prueba de migración a MySQL sin pisar claves.
371. **Calidad**: Prueba de rechazo de nombre de tabla peligroso.
372. **Calidad**: Prueba de GET_LOCK entre dos instancias.
373. **Calidad**: Las pruebas de MySQL se omiten solas si no hay servidor (variable ARCA_TEST_MYSQL).
374. **Calidad**: Pruebas de dígito verificador de CUIT.
375. **Calidad**: Prueba de parseo de getPersona.
376. **Calidad**: Prueba de denominación de persona física.
377. **Calidad**: Prueba de caché, single-flight y caché negativa del padrón.
378. **Calidad**: Prueba del padrón simulado.
379. **Calidad**: Prueba de API del padrón y alta en agenda.
380. **Calidad**: Prueba de 401 en el padrón sin clave.
381. **Calidad**: Prueba de prefijo de cliente sin colisiones.
382. **Calidad**: Prueba de importe gigante en letras.
383. **Calidad**: Prueba del catálogo de documentos.
384. **Calidad**: Prueba de agrupación IPv6.
385. **Calidad**: Prueba de reserva y devolución de cuota.
386. **Calidad**: Prueba de importación que rechaza pendientes.
387. **Calidad**: Prueba de firma v2.
388. **Calidad**: Prueba de purga y verificación de auditoría con ancla.
389. **Calidad**: Prueba de /metrics protegido.
390. **Calidad**: Contrato OpenAPI regenerado y verificado por prueba.
391. **Calidad**: 275 pruebas en verde, build sin advertencias.
392. **Documentación**: OpenAPI JSON y YAML regenerados.
393. **Documentación**: Swagger estático regenerado.
394. **Documentación**: Colección Postman 1.4 con los endpoints nuevos.
395. **Documentación**: Guía de actualización a 1.4 con cambios incompatibles.
396. **Documentación**: Configuración de MySQL documentada.
397. **Documentación**: Changelog 1.4.0.
398. **Documentación**: README con MySQL y padrón.
399. **Seguridad**: Consulta de pertenencia de comprobante por cliente reutilizable (EsDelCliente).
400. **Calidad**: Versión 1.4.0 en todos los ensamblados.

## 1.3.0 (01/10/2026)

150 mejoras (101 a 250). Detalle en `docs/especificacion-requerimientos.html` (secciones 17 a 23).

101. **Validación**: Comprobantes asociados: punto de venta y número válidos.
102. **Validación**: Comprobantes asociados de la misma clase que la nota (A con A, B con B…).
103. **Validación**: Comprobantes asociados: fecha en formato yyyyMMdd.
104. **Validación**: Comprobantes asociados: CUIT válida.
105. **Validación**: Comprobantes asociados sin repetidos.
106. **Validación**: El tributo 99 (Otros) exige descripción.
107. **Validación**: Tributos: Id, base imponible y alícuota no negativos.
108. **Validación**: Opcionales con Id y Valor obligatorios y sin repetir.
109. **Validación**: El opcional 2101 debe ser un CBU de 22 dígitos.
110. **Validación**: Dígitos verificadores del CBU.
111. **Validación**: Compradores: porcentaje mayor a 0 y CUIT válida.
112. **Validación**: MonCotiz con hasta 6 decimales.
113. **Validación**: Importe máximo por comprobante configurable.
114. **Validación**: PeriodoAsoc no puede ser futuro.
115. **Validación**: Actividades válidas y sin repetir.
116. **Validación**: DocTipo dentro del catálogo de documentos.
117. **Validación**: Advertencias separadas de los errores (no impiden emitir).
118. **Validación**: Advertencia de autofacturación.
119. **Validación**: Advertencia de fecha distinta de hoy.
120. **Validación**: Advertencia de importe alto (umbral configurable).
121. **Validación**: Advertencia de cercanía al tope de consumidor final y de cancelación en moneda extranjera.
122. **Funcional**: Validación previa sin emitir (POST /comprobantes/validar).
123. **Funcional**: Modo simulación completo, sin certificado ni conexión con ARCA.
124. **Funcional**: Simulador con numeración correlativa, CAE identificable (empieza con 99) y CAEA.
125. **Funcional**: Inyección de fallas configurable en simulación.
126. **Funcional**: Suscripciones de facturación recurrente (alta, baja, consulta).
127. **Funcional**: Ejecutor automático de suscripciones en segundo plano.
128. **Funcional**: Idempotencia por período: nunca factura dos veces el mismo período.
129. **Funcional**: Pausar y reanudar suscripciones.
130. **Funcional**: Historial de ejecuciones de cada suscripción.
131. **Funcional**: QR fiscal en PNG.
132. **Funcional**: Representación imprimible en HTML (original, duplicado, triplicado).
133. **Funcional**: Datos del emisor configurables para la impresión.
134. **Funcional**: Leyenda de transparencia fiscal (Ley 27.743) en comprobantes B.
135. **Funcional**: Período facturado y vencimiento de pago en la impresión.
136. **Funcional**: Importe en letras (y endpoint propio).
137. **Funcional**: Respaldo del registro de emisiones en JSONL.
138. **Funcional**: Restauración de respaldo sin pisar el estado actual.
139. **Funcional**: Reporte de ventas por receptor.
140. **Funcional**: Serie diaria de ventas.
141. **Funcional**: Ventas por punto de venta.
142. **Funcional**: Registro de rechazos recientes de ARCA.
143. **Funcional**: Códigos de rechazo más frecuentes.
144. **Funcional**: Receptores bloqueados para facturación.
145. **Funcional**: Agenda de receptores con validación de CUIT y condición IVA.
146. **Funcional**: Emisión indicando el receptor de la agenda (receptorId).
147. **Funcional**: Búsqueda de receptores por texto.
148. **Funcional**: Referencia externa (p.ej. número de pedido) en cada emisión.
149. **Funcional**: Búsqueda de emisiones por referencia externa.
150. **Funcional**: Resolución manual de una emisión pendiente.
151. **Funcional**: Reenvío manual de webhook.
152. **Funcional**: Evento de rechazo por webhook (opcional).
153. **Funcional**: Varias URLs de webhook.
154. **Funcional**: Registro de entregas de webhook.
155. **Funcional**: Cierre diario por punto de venta y tipo.
156. **Funcional**: Control del último número del registro contra ARCA en el cierre.
157. **Funcional**: Próximo número a autorizar (informativo).
158. **Funcional**: Puntos de venta con estado legible.
159. **Funcional**: Condición frente al IVA del emisor configurable.
160. **Funcional**: Clases de comprobante permitidas según el emisor (RI: A/B; Monotributo/Exento: C).
161. **Rendimiento**: Registro de emisiones en SQLite (transaccional).
162. **Rendimiento**: Índices por número, fecha, estado y referencia.
163. **Rendimiento**: Migración automática del registro JSON a SQLite.
164. **Rendimiento**: SQLite en modo WAL con escritura sincrónica completa.
165. **Rendimiento**: Bloqueo de numeración entre procesos del mismo servidor.
166. **Rendimiento**: Serialización del registro generada en compilación.
167. **Rendimiento**: Caché de comprobantes autorizados (inmutables).
168. **Rendimiento**: Single-flight: solicitudes concurrentes de una tabla comparten una llamada.
169. **Rendimiento**: Tamaño de caché configurable.
170. **Rendimiento**: GC de servidor concurrente y PGO dinámico.
171. **Rendimiento**: Imagen Docker compilada con ReadyToRun.
172. **Rendimiento**: Límites de Kestrel configurables (conexiones, cabeceras, keep-alive).
173. **Rendimiento**: Descompresión de solicitudes gzip/brotli.
174. **Rendimiento**: Output cache de catálogos.
175. **Rendimiento**: Paginación con cabeceras Link y X-Total-Count.
176. **Rendimiento**: Tamaño máximo de lote configurable.
177. **Rendimiento**: Auditoría con escritura asíncrona en cola.
178. **Rendimiento**: Rotación diaria de los archivos de auditoría.
179. **Rendimiento**: Retención y purga automática de la auditoría.
180. **Rendimiento**: Métrica de emisiones pendientes.
181. **Rendimiento**: Métrica de días al vencimiento del certificado.
182. **Rendimiento**: Métricas de estado del circuito y llamadas en curso.
183. **Rendimiento**: Histograma de duración total de emisión y contador de advertencias.
184. **Rendimiento**: Endpoint /metrics en formato Prometheus, sin dependencias.
185. **Rendimiento**: Logs de alto volumen generados en compilación (LoggerMessage).
186. **Seguridad**: Claves de API guardadas como hash SHA-256.
187. **Seguridad**: Comando para generar claves y sus hashes.
188. **Seguridad**: Bloqueo temporal de IP por intentos fallidos.
189. **Seguridad**: Cabeceras Permissions-Policy, COOP y CORP.
190. **Seguridad**: Idempotency-Key restringida a ASCII imprimible.
191. **Seguridad**: Sanitización de valores de usuario en logs.
192. **Seguridad**: Firma HMAC de solicitudes por cliente.
193. **Seguridad**: Protección anti-replay de la firma (±5 minutos).
194. **Seguridad**: Auditoría con IP, user-agent e importe.
195. **Seguridad**: Auditoría con hash encadenado y verificación de integridad.
196. **Seguridad**: Secretos desde archivos (convención _FILE).
197. **Seguridad**: Control de la CUIT del certificado frente a la configurada.
198. **Seguridad**: Clave RSA mínima de 2048 bits.
199. **Seguridad**: CORS por lista blanca.
200. **Seguridad**: Recarga en caliente de clientes y claves.
201. **Seguridad**: Profundidad máxima de JSON (32).
202. **Seguridad**: Rechazo opcional de campos JSON desconocidos.
203. **Seguridad**: Swagger protegido con permiso admin fuera de Development.
204. **Seguridad**: Cifrado en reposo AES-256-GCM del registro de emisiones.
205. **Seguridad**: Detalle de salud visible sólo para admin.
206. **Seguridad**: Timeout de lectura de cabeceras.
207. **Seguridad**: Advertencia de certificado sin contraseña.
208. **Seguridad**: Pinning opcional del certificado TLS de ARCA.
209. **Seguridad**: Modo mantenimiento (emisiones 503, consultas disponibles).
210. **Seguridad**: Endpoints para activar y desactivar el mantenimiento.
211. **Operación**: Comando verificar (configuración, certificado y FEDummy).
212. **Operación**: Comando probar-wsaa.
213. **Operación**: Comando ultimo (último comprobante autorizado).
214. **Operación**: Comando reconciliar.
215. **Operación**: Drenaje en el apagado (ready responde 503).
216. **Operación**: Probe /health/startup.
217. **Operación**: Commit y fecha de compilación en /info.
218. **Operación**: Tiempo activo y estado de mantenimiento en /info.
219. **Operación**: URI de tipo en ProblemDetails.
220. **Operación**: Códigos de error estables (VALIDACION, ARCA_RECHAZO…).
221. **Operación**: Cabecera Idempotency-Replayed en reintentos.
222. **Operación**: Cabecera Location del comprobante emitido.
223. **Operación**: Estado de varias emisiones en una sola llamada.
224. **Operación**: Webhook de prueba.
225. **Operación**: Etiquetas en español en OpenAPI.
226. **Operación**: Servidores configurables en OpenAPI.
227. **Integración**: SDK: anular, duplicar, lote y validar.
228. **Integración**: SDK: firma HMAC.
229. **Integración**: SDK: propagación del Correlation-ID.
230. **Integración**: Colección Postman regenerada (72 rutas).
231. **Integración**: Scripts de tareas (bash y PowerShell).
232. **Integración**: Script de respaldo con retención de 30 copias.
233. **Integración**: docker-compose con healthcheck, límites y secrets.
234. **Integración**: Manifiestos de Kubernetes (una réplica, probes, PVC, sin root).
235. **Integración**: .env.example.
236. **Integración**: Configuración de Dependabot.
237. **Integración**: Análisis CodeQL.
238. **Integración**: Cobertura de código en CI.
239. **Calidad**: Pruebas de propiedades de la calculadora (500 casos aleatorios).
240. **Calidad**: Prueba de concurrencia de SQLite.
241. **Documentación**: Diagrama de secuencia de la emisión.
242. **Documentación**: Glosario de ARCA.
243. **Documentación**: Preguntas frecuentes de integración.
244. **Documentación**: Catálogo de códigos de error.
245. **Documentación**: Guía de actualización a la versión 1.3.
246. **Documentación**: Ejemplos con curl.
247. **Integración**: Cliente PHP de ejemplo (stack del proyecto).
248. **Integración**: Cliente Dart de ejemplo.
249. **Corrección**: Swagger quedaba accesible sin clave fuera de Development (se evaluaba antes de la autenticación).
250. **Corrección**: Apagado ordenado de la auditoría: se vacía la cola antes de salir.

## 1.2.0 (01/10/2026)

50 mejoras adicionales (numeradas 51 a 100). Detalle en `docs/especificacion-requerimientos.html` (secciones 15 y 16).

51. **Funcional**: Solicitud automática del CAEA de la próxima quincena (contingencia), dentro de la ventana de 5 días.
52. **Funcional**: Consulta del CAEA vigente de la quincena en curso.
53. **Funcional**: Nota de crédito parcial con tope en el total del comprobante original.
54. **Funcional**: Duplicar un comprobante (facturación recurrente), con el período de servicio corrido a hoy.
55. **Funcional**: Exportación CSV para Excel de los comprobantes autorizados.
56. **Funcional**: Resumen de IVA por alícuota del período (las notas de crédito restan, en pesos).
57. **Funcional**: Búsqueda de emisiones con filtros y paginación.
58. **Funcional**: Consulta de un rango de comprobantes en ARCA (máx. 100, concurrencia limitada).
59. **Funcional**: Auditoría de numeración: detecta números emitidos por otro sistema en el mismo punto de venta.
60. **Funcional**: Plantillas de comprobantes frecuentes por cliente (crear, listar, obtener, eliminar).
61. **Funcional**: Emisión desde plantilla con importes y receptor opcionales.
62. **Funcional**: Calculadora: cantidad × precio unitario.
63. **Funcional**: Calculadora: bonificación porcentual por línea.
64. **Funcional**: Calculadora: precios con IVA incluido (desglose automático del neto).
65. **Funcional**: Número fiscal formateado (PPPPP-NNNNNNNN) y descripción legible en cada resultado.
66. **Funcional**: Catálogos locales: tipos de comprobante, documentos, condiciones IVA y alícuotas.
67. **Funcional**: Normalización de la entrada (espacios y mayúsculas en códigos).
68. **Validación**: DNI dentro de rango (1 a 8 dígitos).
69. **Validación**: Receptor Responsable Inscripto debe identificarse con CUIT.
70. **Validación**: Vencimiento de pago no anterior a la fecha del comprobante.
71. **Rendimiento**: Índice en memoria del registro en disco: lecturas y listados sin I/O, limpieza de temporales al iniciar.
72. **Rendimiento**: Pool de conexiones con renovación periódica, keep-alive y timeout de conexión.
73. **Rendimiento**: Descompresión automática (gzip/deflate) de respuestas de ARCA.
74. **Rendimiento**: Proxy saliente configurable para llegar a ARCA.
75. **Rendimiento**: Timeouts diferenciados para lecturas y emisiones.
76. **Rendimiento**: Bulkhead: tope de llamadas simultáneas a ARCA.
77. **Rendimiento**: Precarga de tablas de parámetros al iniciar.
78. **Rendimiento**: ETag y 304 Not Modified en parámetros y catálogos.
79. **Rendimiento**: Respuestas JSON sin campos nulos (menor tamaño).
80. **Rendimiento**: Timeout global por solicitud HTTP (504).
81. **Seguridad**: Sólo se aceptan cuerpos application/json (415 en otro caso).
82. **Seguridad**: Rate limit propio por cliente.
83. **Seguridad**: Cuota diaria de emisiones por cliente (429 al agotarse).
84. **Seguridad**: Auditoría append-only (JSONL) de toda operación que modifica estado.
85. **Seguridad**: Consulta de la auditoría por fecha y cliente.
86. **Seguridad**: Archivos del registro con permisos 0600.
87. **Seguridad**: Diagnóstico de configuración al iniciar con advertencias de riesgo.
88. **Seguridad**: Configuración efectiva sin secretos para soporte.
89. **Seguridad**: Lista de IPs/redes CIDR permitidas por cliente.
90. **Seguridad**: Vencimiento de claves de API por cliente.
91. **Integración**: Ejemplos de request en OpenAPI.
92. **Integración**: Cabeceras de respuesta y X-Correlation-Id documentadas en OpenAPI.
93. **Integración**: SDK .NET con reintentos que conservan la Idempotency-Key.
94. **Integración**: Colección Postman generada desde el contrato (49 requests).
95. **Integración**: Scripts de certificado ARCA (CSR y .pfx) para bash y PowerShell.
96. **Integración**: Prueba de carga k6 con umbrales.
97. **Integración**: Guía de operación (runbook) para incidentes frecuentes.
98. **Integración**: Configuración de ejemplo para el entorno de Testing.
99. **Calidad**: Prueba de contrato: falla si la API cambia sin actualizar docs/openapi.json.
100. **Calidad**: Versión de esquema en el registro de emisiones (migraciones futuras).

## 1.1.0 (01/10/2026)

50 mejoras técnicas y funcionales. Detalle completo en `docs/especificacion-requerimientos.html` (sección 14).

1. **Resiliencia**: Caché de tablas FEParamGet* (TTL configurable, 12 h por defecto).
2. **Resiliencia**: Caché de cotizaciones por moneda y fecha (10 min).
3. **Resiliencia**: Reintentos con backoff sólo en operaciones de lectura.
4. **Resiliencia**: Las operaciones que generan comprobantes nunca se reintentan a ciegas.
5. **Resiliencia**: Circuit breaker hacia ARCA (falla rápida y recuperación semiabierta).
6. **Resiliencia**: Los errores de negocio no abren el circuito.
7. **Resiliencia**: Carga diferida del certificado: el servicio arranca y reporta el problema en lugar de caerse.
8. **Resiliencia**: Renovación proactiva y precarga del TA en segundo plano.
9. **Resiliencia**: Reconciliador automático de emisiones pendientes.
10. **Resiliencia**: Retención y purga del registro de emisiones (90 días).
11. **Observabilidad**: Métricas: llamadas, duración, emisiones, reconciliaciones, aperturas del circuito, aciertos de caché.
12. **Observabilidad**: Trazas distribuidas por llamada a ARCA (ActivitySource, compatible con OpenTelemetry).
13. **Observabilidad**: Detección del desfasaje de reloj con ARCA (cabecera Date).
14. **Observabilidad**: Correlation-ID (X-Correlation-Id) propagado a logs y respuestas.
15. **Observabilidad**: Logs HTTP con duración y sin cabeceras sensibles.
16. **Observabilidad**: Logs en JSON opcionales para agregadores.
17. **Observabilidad**: Health checks separados: /health/live y /health/ready.
18. **Observabilidad**: Health check del certificado: degradado si vence en menos de 30 días.
19. **Observabilidad**: Health check del registro de emisiones (escritura).
20. **Observabilidad**: Health check del canal con ARCA (circuito y reloj).
21. **Observabilidad**: Endpoint /api/v1/info: versión, ambiente, CUIT enmascarada, vencimiento del certificado.
22. **Funcional**: QR fiscal (RG 4892) incluido en cada emisión aprobada.
23. **Funcional**: Endpoint del QR de un comprobante ya emitido.
24. **Funcional**: Emisión por lote de hasta 50 comprobantes con resultado individual.
25. **Funcional**: Calculadora de importes: IVA por alícuota, redondeo y totales.
26. **Funcional**: Anulación con Nota de Crédito automática asociada al original.
27. **Funcional**: Consulta del estado de una emisión por Idempotency-Key.
28. **Funcional**: Fecha del comprobante automática (hora argentina) si no se informa.
29. **Funcional**: Cotización automática desde ARCA para moneda extranjera.
30. **Funcional**: El detalle enviado se persiste: los reenvíos son idénticos aunque cambie el día.
31. **Funcional**: Notificación por webhook de cada comprobante autorizado.
32. **Funcional**: Webhook firmado con HMAC-SHA256 y reintentos con espera creciente.
33. **Funcional**: Sugerencias de corrección para errores conocidos de ARCA (campo ayuda).
34. **Validación**: Condición IVA del receptor válida y coherente con la clase A/M (RG 5616).
35. **Validación**: Tope configurable de consumidor final sin identificar.
36. **Validación**: FCE MiPyME exige el opcional CBU o alias.
37. **Validación**: Validación opcional contra ARCA: punto de venta habilitado y no bloqueado.
38. **Validación**: Validación opcional contra ARCA: moneda existente.
39. **Seguridad**: Clientes con nombre y varias claves: rotación sin cortes y sin perder la idempotencia.
40. **Seguridad**: Permisos por cliente: emitir, consultar y admin.
41. **Seguridad**: Cabeceras de seguridad (nosniff, DENY, CSP, no-store) y sin cabecera Server.
42. **Seguridad**: HTTPS forzado y HSTS opcionales.
43. **Seguridad**: Soporte de proxy inverso (X-Forwarded-For) con proxies de confianza.
44. **Operación**: Endpoints admin: pendientes, reconciliar, estadísticas y limpieza de caché.
45. **Operación**: Rate limit, tamaño máximo del body y concurrencia de emisión configurables.
46. **Operación**: Retry-After en respuestas 429 y 503.
47. **Operación**: Compresión de respuestas (Brotli y Gzip).
48. **Operación**: Apagado ordenado: espera las emisiones en curso (90 s).
49. **DevOps**: Dockerfile multi-etapa sin root, con HEALTHCHECK y docker-compose para Testing.
50. **DevOps**: CI en GitHub Actions; analizadores de calidad con warnings como errores; .editorconfig.

## 1.0.0 (30/09/2026)

Versión inicial: WSAA + WSFEv1 completos, emisión idempotente con reconciliación y revisión adversaria.
