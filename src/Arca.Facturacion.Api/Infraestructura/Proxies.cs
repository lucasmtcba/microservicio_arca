namespace Arca.Facturacion.Api.Infraestructura;

/// <summary>Configuración de proxies inversos confiables: Api:ProxiesConfiables (IPs exactas) y Api:RedesConfiables (CIDR).
/// Sólo se acepta X-Forwarded-For de esos orígenes; sin ninguno, la IP vista es la del proxy y la protección por IP
/// (fuerza bruta, IpsPermitidas, rate limit por IP) trata a todos los clientes como uno solo.</summary>
public static class RedesConfiables
{
    /// <summary>Redes CIDR válidas de Api:RedesConfiables (las inválidas se ignoran y se informan en <see cref="Advertencias"/>).</summary>
    public static IReadOnlyList<Microsoft.AspNetCore.HttpOverrides.IPNetwork> Leer(IConfiguration cfg)
    {
        var res = new List<Microsoft.AspNetCore.HttpOverrides.IPNetwork>();
        foreach (var r in Configuradas(cfg))
            if (System.Net.IPNetwork.TryParse(r.Trim(), out var red))
                res.Add(new Microsoft.AspNetCore.HttpOverrides.IPNetwork(red.BaseAddress, red.PrefixLength));
        return res;
    }

    private static string[] Configuradas(IConfiguration cfg) =>
        (cfg.GetSection("Api:RedesConfiables").Get<string[]>() ?? Array.Empty<string>()).Where(r => !string.IsNullOrWhiteSpace(r)).ToArray();

    /// <summary>Advertencias de inicio sobre la configuración de proxies.</summary>
    public static IEnumerable<string> Advertencias(IConfiguration cfg)
    {
        var ips = (cfg.GetSection("Api:ProxiesConfiables").Get<string[]>() ?? Array.Empty<string>()).Where(i => !string.IsNullOrWhiteSpace(i)).ToArray();
        foreach (var i in ips.Where(i => !System.Net.IPAddress.TryParse(i.Trim(), out _)))
            yield return $"Api:ProxiesConfiables contiene un valor que no es una IP ('{Sanitizar.Log(i, 60)}'); para redes usar Api:RedesConfiables (CIDR).";
        var redes = Configuradas(cfg);
        foreach (var r in redes.Where(r => !System.Net.IPNetwork.TryParse(r.Trim(), out _)))
            yield return $"Api:RedesConfiables contiene una red CIDR inválida ('{Sanitizar.Log(r, 60)}').";
        if (ips.Length == 0 && redes.Length == 0)
            yield return "No hay proxies confiables (Api:ProxiesConfiables / Api:RedesConfiables): se ignora X-Forwarded-For. " +
                         "Detrás de un proxy o balanceador, todas las solicitudes parecen venir de la IP del proxy (fuerza bruta, IpsPermitidas y límites por IP).";
    }
}
