using Arca.Facturacion.Core.Configuracion;
using Arca.Facturacion.Core.Resiliencia;
using Arca.Facturacion.Core.Servicios;
using Arca.Facturacion.Core.Validacion;
using Arca.Facturacion.Core.Wsaa;
using Arca.Facturacion.Core.Wsfe;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Arca.Facturacion.Core;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddArcaFacturacion(this IServiceCollection services, IConfiguration config)
    {
        services.AddOptions<ArcaOptions>().Bind(config.GetSection(ArcaOptions.Seccion))
            .ValidateDataAnnotations()
            .Validate(o => Cuit.EsValida(o.Cuit), "Arca:Cuit no es una CUIT válida.")
            .ValidateOnStart();

                services.AddSingleton(TimeProvider.System);
        services.AddMemoryCache();
        services.AddSingleton<Microsoft.Extensions.Caching.Memory.IMemoryCache>(sp => new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions { SizeLimit = sp.GetRequiredService<IOptions<ArcaOptions>>().Value.CacheTamanoMaximo }));
        services.AddSingleton<EstadoArca>();
        services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<ArcaOptions>>().Value;
            return new CircuitBreaker(o.CircuitoUmbralFallas, TimeSpan.FromSeconds(o.CircuitoPausaSegundos), sp.GetRequiredService<TimeProvider>());
        });
        services.AddSingleton(sp =>
        {
            var o = sp.GetRequiredService<IOptions<ArcaOptions>>().Value;
            return new ComprobanteValidator(sp.GetRequiredService<TimeProvider>(), o.ToleranciaImportes)
            {
                MontoTopeConsumidorFinal = o.MontoTopeConsumidorFinalSinIdentificar,
                MontoMaximoComprobante = o.MontoMaximoComprobante,
                UmbralAdvertenciaImporte = o.UmbralAdvertenciaImporte,
                CondicionEmisor = o.CondicionIvaEmisor,
                ReceptoresBloqueados = o.ReceptoresBloqueados.ToHashSet(),
                CuitEmisor = o.CuitNumerico
            };
        });
        services.AddSingleton<IRegistroEmisiones>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<ArcaOptions>>().Value;
            if (o.ModoSimulacion)
            {
                // Simulación: nunca se mezclan CAE ficticios con el registro real (al apagarla, quedarían como "ya emitidos").
                sp.GetRequiredService<ILoggerFactory>().CreateLogger("Arca.Facturacion")
                    .LogWarning("Modo simulación: el registro de emisiones es en memoria y se ignoran las rutas persistentes.");
                return new RegistroEmisionesMemoria();
            }
            var cif = CifradorRegistro.DesdeBase64(o.ClaveCifradoRegistro);
            if (!string.IsNullOrWhiteSpace(o.RegistroEmisionesMySql))
            {
                var my = new RegistroEmisionesMySql(o.RegistroEmisionesMySql!, cif);
                if (o.MigrarRegistroAMySql && my.Cantidad == 0)
                {
                    IRegistroEmisiones? origen = !string.IsNullOrWhiteSpace(o.RegistroEmisionesSqlite) && File.Exists(o.RegistroEmisionesSqlite) ? new RegistroEmisionesSqlite(o.RegistroEmisionesSqlite!, null, cif)
                        : !string.IsNullOrWhiteSpace(o.RegistroEmisionesPath) && Directory.Exists(o.RegistroEmisionesPath) ? new RegistroEmisionesArchivo(o.RegistroEmisionesPath!, cif) : null;
                    if (origen != null)
                    {
                        var (cop, _) = my.MigrarDesde(origen);
                        sp.GetRequiredService<ILoggerFactory>().CreateLogger("Arca.Facturacion").LogInformation("Migrados {N} registros de emisión a MySQL.", cop);
                    }
                }
                return my;
            }
            if (!string.IsNullOrWhiteSpace(o.RegistroEmisionesSqlite)) return new RegistroEmisionesSqlite(o.RegistroEmisionesSqlite!, o.RegistroEmisionesPath, cif);
            if (!string.IsNullOrWhiteSpace(o.RegistroEmisionesPath)) return new RegistroEmisionesArchivo(o.RegistroEmisionesPath!, cif);
            sp.GetRequiredService<ILoggerFactory>().CreateLogger("Arca.Facturacion")
                .LogWarning("Arca:RegistroEmisionesPath vacío: la idempotencia no sobrevive a reinicios.");
            return new RegistroEmisionesMemoria();
        });
        services.AddSingleton<ICmsFirmante>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<ArcaOptions>>().Value;
            return new FirmanteDiferido(() => CertificadoCmsFirmante.DesdeArchivo(o.CertificadoPath, o.CertificadoPassword));
        });

        void Timeout(IServiceProvider sp, HttpClient c) =>
            c.Timeout = TimeSpan.FromSeconds(sp.GetRequiredService<IOptions<ArcaOptions>>().Value.TimeoutSegundos);

        // Pool de conexiones con renovación periódica (cambios de DNS), keep-alive, descompresión y proxy saliente opcional.
        HttpMessageHandler Handler(IServiceProvider sp)
        {
            var o = sp.GetRequiredService<IOptions<ArcaOptions>>().Value;
            var h = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(o.VidaConexionMinutos),
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
                KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests,
                AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                MaxConnectionsPerServer = Math.Max(2, o.MaxLlamadasConcurrentesArca)
            };
            if (o.HuellasTlsArca.Length > 0)
            {
                var huellas = o.HuellasTlsArca.Select(x => x.Replace(":", "", StringComparison.Ordinal).ToUpperInvariant()).ToHashSet();
                h.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, errores) =>
                    errores == System.Net.Security.SslPolicyErrors.None && cert != null
                    && huellas.Contains(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(cert.GetRawCertData())));
            }
            if (!string.IsNullOrWhiteSpace(o.ProxySaliente))
            {
                h.Proxy = new System.Net.WebProxy(o.ProxySaliente);
                h.UseProxy = true;
            }
            return h;
        }

        services.AddHttpClient(WsaaClient.NombreHttpClient, Timeout).ConfigurePrimaryHttpMessageHandler(Handler)
            .SetHandlerLifetime(System.Threading.Timeout.InfiniteTimeSpan);
        services.AddHttpClient(WsfeClient.NombreHttpClient, Timeout).ConfigurePrimaryHttpMessageHandler(Handler)
            .SetHandlerLifetime(System.Threading.Timeout.InfiniteTimeSpan);
        services.AddHttpClient(Padron.PadronA13Client.NombreHttpClient, Timeout).ConfigurePrimaryHttpMessageHandler(Handler)
            .SetHandlerLifetime(System.Threading.Timeout.InfiniteTimeSpan);
        services.AddSingleton<Padron.IPadronClient>(sp =>
        {
            var o = sp.GetRequiredService<IOptions<ArcaOptions>>();
            Padron.IPadronClient interno = o.Value.ModoSimulacion ? new Padron.PadronSimulado()
                : new Padron.PadronA13Client(sp.GetRequiredService<IHttpClientFactory>(), sp.GetRequiredService<ITicketAccesoProvider>(), o);
            return new Padron.PadronCacheado(interno, sp.GetRequiredService<TimeProvider>(), TimeSpan.FromHours(Math.Max(0.01, o.Value.PadronCacheHoras)));
        });
        services.AddSingleton<IWsaaClient, WsaaClient>();
        services.AddSingleton<WsfeClient>(sp => new WsfeClient(sp.GetRequiredService<IHttpClientFactory>(),
            sp.GetRequiredService<ITicketAccesoProvider>(), sp.GetRequiredService<IOptions<ArcaOptions>>(),
            sp.GetRequiredService<ILogger<WsfeClient>>(), sp.GetRequiredService<EstadoArca>(), sp.GetRequiredService<TimeProvider>()));
        services.AddSingleton<Simulacion.WsfeSimulador>(sp => new Simulacion.WsfeSimulador(sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IOptions<ArcaOptions>>().Value.SimulacionFallasPorcentaje));
        services.AddSingleton<IWsfeClient>(sp => new WsfeClienteResiliente(
            sp.GetRequiredService<IOptions<ArcaOptions>>().Value.ModoSimulacion
                ? sp.GetRequiredService<Simulacion.WsfeSimulador>()
                : sp.GetRequiredService<WsfeClient>(),
            sp.GetRequiredService<Microsoft.Extensions.Caching.Memory.IMemoryCache>(), sp.GetRequiredService<CircuitBreaker>(),
            sp.GetRequiredService<IOptions<ArcaOptions>>(), sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<WsfeClienteResiliente>>()));
        services.AddSingleton<NotaCreditoService>();
        services.AddSingleton<ConsultasService>();
        services.AddSingleton<CaeaService>();
        services.AddSingleton<IRepositorioPlantillas>(sp => new RepositorioPlantillas(sp.GetRequiredService<IOptions<ArcaOptions>>().Value.PlantillasPath));
        services.AddSingleton<IAuditoria>(sp => new AuditoriaArchivo(sp.GetRequiredService<IOptions<ArcaOptions>>().Value.AuditoriaPath));
        services.AddSingleton<ITicketAccesoProvider, TicketAccesoProvider>();
        services.AddSingleton<RegistroRechazos>();
        services.AddSingleton<ReportesService>();
        static string? Ruta(ArcaOptions o, string? r) => o.ModoSimulacion ? null : r;
        services.AddSingleton(sp => new AgendaReceptores(Ruta(sp.GetRequiredService<IOptions<ArcaOptions>>().Value, sp.GetRequiredService<IOptions<ArcaOptions>>().Value.ReceptoresPath)));
        services.AddSingleton(sp => new SuscripcionesService(Ruta(sp.GetRequiredService<IOptions<ArcaOptions>>().Value, sp.GetRequiredService<IOptions<ArcaOptions>>().Value.SuscripcionesPath),
            sp.GetRequiredService<IFacturacionService>(), sp.GetRequiredService<ComprobanteValidator>(), sp.GetRequiredService<ILogger<SuscripcionesService>>())
        {
            MaxPeriodosAtrasados = sp.GetRequiredService<IOptions<ArcaOptions>>().Value.SuscripcionesMaxPeriodosAtrasados
        });
        services.AddSingleton<IFacturacionService>(sp => new FacturacionService(
            sp.GetRequiredService<IWsfeClient>(), sp.GetRequiredService<ComprobanteValidator>(),
            sp.GetRequiredService<IRegistroEmisiones>(), sp.GetRequiredService<ILogger<FacturacionService>>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<IOptions<ArcaOptions>>().Value.ReconciliacionEsperasSegundos.Select(x => TimeSpan.FromSeconds(x)).ToArray(),
            OpcionesDe(sp.GetRequiredService<IOptions<ArcaOptions>>().Value),
            sp.GetService<INotificadorEmisiones>())
        {
            Rechazos = sp.GetRequiredService<RegistroRechazos>(),
            Bloqueo = ElegirBloqueo(sp.GetRequiredService<IOptions<ArcaOptions>>().Value)
        });
        return services;
    }

    private static IBloqueoNumeracion? ElegirBloqueo(ArcaOptions o)
    {
        if (!o.ModoSimulacion && o.BloqueoDistribuidoMySql && !string.IsNullOrWhiteSpace(o.RegistroEmisionesMySql))
            return new BloqueoNumeracionMySql(o.RegistroEmisionesMySql!, prefijo: "arca:" + o.Cuit);
        return string.IsNullOrWhiteSpace(o.BloqueoEntreProcesosPath) ? null : new BloqueoNumeracionArchivo(o.BloqueoEntreProcesosPath!);
    }

    private static OpcionesEmision OpcionesDe(ArcaOptions o) =>
        new(o.CuitNumerico, o.CompletarFechaAutomatica, o.CotizacionAutomatica, o.ValidarContraTablasArca);
}

/// <summary>Revisa la configuración efectiva y devuelve advertencias de riesgo (se registran al iniciar).</summary>
public static class DiagnosticoConfiguracion
{
    public static IReadOnlyList<string> Advertencias(ArcaOptions o, bool swaggerHabilitado, int clientesConfigurados)
    {
        var a = new List<string>();
        if (o.ModoSimulacion) a.Add("MODO SIMULACIÓN activo: no se llama a ARCA y los CAE no tienen validez fiscal.");
        if (o.ModoSimulacion && o.Ambiente == ArcaAmbiente.Real) a.Add("ModoSimulacion con Ambiente=Real: revisar la configuración.");
        if (string.IsNullOrEmpty(o.CertificadoPassword) && !o.ModoSimulacion) a.Add("El certificado no tiene contraseña: proteger el archivo .pfx.");
        if (clientesConfigurados == 0) a.Add("No hay clientes/API keys configurados: todas las llamadas a la API serán rechazadas (401).");
        if (string.IsNullOrWhiteSpace(o.RegistroEmisionesPath)) a.Add("Arca:RegistroEmisionesPath vacío: la idempotencia no sobrevive reinicios.");
        if (string.IsNullOrWhiteSpace(o.CacheTicketPath)) a.Add("Arca:CacheTicketPath vacío: tras un reinicio WSAA puede negar un TA nuevo hasta 12 h.");
        if (o.Ambiente == ArcaAmbiente.Real && swaggerHabilitado) a.Add("Swagger habilitado con Ambiente=Real.");
        if (o.Ambiente == ArcaAmbiente.Real && o.PermitirSolicitudDirecta) a.Add("PermitirSolicitudDirecta habilitado con Ambiente=Real: no hay idempotencia en ese endpoint.");
        if (!string.IsNullOrWhiteSpace(o.WebhookUrl) && string.IsNullOrWhiteSpace(o.WebhookSecreto)) a.Add("Webhook sin secreto: los receptores no pueden verificar la firma.");
        if (!string.IsNullOrWhiteSpace(o.WebhookUrl) && o.WebhookUrl!.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) a.Add("Webhook sin HTTPS.");
        if (o.TimeoutEmisionSegundos > o.TimeoutSegundos) a.Add("TimeoutEmisionSegundos supera TimeoutSegundos (HttpClient): se corta antes por el HttpClient.");
        if (string.IsNullOrWhiteSpace(o.AuditoriaPath)) a.Add("Arca:AuditoriaPath vacío: la auditoría sólo se conserva en memoria.");
        return a;
    }

    /// <summary>Configuración efectiva SIN secretos (contraseña del certificado, secreto del webhook).</summary>
    public static object Resumen(ArcaOptions o) => new
    {
        o.Ambiente, Cuit = o.Cuit.Length == 11 ? o.Cuit[..2] + "-*****" + o.Cuit[8..] : "?",
        o.CertificadoPath, CertificadoPassword = string.IsNullOrEmpty(o.CertificadoPassword) ? "(vacía)" : "(configurada)",
        o.CacheTicketPath, o.RegistroEmisionesPath, o.PlantillasPath, o.AuditoriaPath, o.PermitirSolicitudDirecta,
        o.ToleranciaImportes, o.ReconciliacionEsperasSegundos, o.ReintentosLecturaMs, o.CacheParametrosMinutos, o.CacheCotizacionMinutos,
        o.CircuitoUmbralFallas, o.CircuitoPausaSegundos, o.MontoTopeConsumidorFinalSinIdentificar, o.ValidarContraTablasArca,
        o.CompletarFechaAutomatica, o.CotizacionAutomatica, o.ReconciliadorIntervaloSegundos, o.RetencionRegistroDias,
        o.RenovacionProactivaTicket, WebhookUrl = string.IsNullOrEmpty(o.WebhookUrl) ? null : Uri.TryCreate(o.WebhookUrl, UriKind.Absolute, out var wu) ? wu.GetLeftPart(UriPartial.Authority) : "(URL inválida)",
        WebhookSecreto = string.IsNullOrEmpty(o.WebhookSecreto) ? "(vacío)" : "(configurado)",
        o.TimeoutSegundos, o.TimeoutLecturaSegundos, o.TimeoutEmisionSegundos, o.MaxLlamadasConcurrentesArca,
        ProxySaliente = string.IsNullOrEmpty(o.ProxySaliente) ? null : "(configurado)", o.VidaConexionMinutos,
        o.SolicitarCaeaAutomatico, o.PrecargarParametros, o.WsaaUrlEfectiva, o.WsfeUrlEfectiva
    };
}
