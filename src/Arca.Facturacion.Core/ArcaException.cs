namespace Arca.Facturacion.Core;

/// <summary>Error/evento/observación devuelto por ARCA.</summary>
public sealed record ArcaMensaje(int Code, string Msg);

/// <summary>Error de negocio devuelto por ARCA (bloque Errors / soap:Fault).</summary>
public class ArcaException : Exception
{
    public IReadOnlyList<ArcaMensaje> Errores { get; }
    public ArcaException(string mensaje, IReadOnlyList<ArcaMensaje>? errores = null, Exception? inner = null)
        : base(mensaje, inner) => Errores = errores ?? Array.Empty<ArcaMensaje>();
}

/// <summary>Fallo de comunicación con ARCA (timeout, HTTP 5xx, respuesta ilegible). El resultado de la operación es DESCONOCIDO.</summary>
public sealed class ArcaComunicacionException : ArcaException
{
    public ArcaComunicacionException(string mensaje, Exception? inner = null) : base(mensaje, null, inner) { }
}

/// <summary>Fallo de autenticación contra WSAA.</summary>
public sealed class WsaaException : ArcaException
{
    public string? CodigoFault { get; }
    public WsaaException(string mensaje, string? codigoFault = null, Exception? inner = null) : base(mensaje, null, inner)
        => CodigoFault = codigoFault;
}

/// <summary>El comprobante no pasa la validación local previa al envío.</summary>
public sealed class ValidacionException : Exception
{
    public IReadOnlyList<string> Errores { get; }
    public ValidacionException(IReadOnlyList<string> errores) : base("El comprobante no es válido: " + string.Join(" | ", errores))
        => Errores = errores;
}
