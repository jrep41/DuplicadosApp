namespace DuplicadosApp.ViewModels;

/// <summary>Nivel de una línea del panel Actividad, para colorearla.</summary>
public enum LogSeverity
{
    Normal,
    Warning,
    Error,
}

/// <summary>
/// Línea ya formateada del registro de actividad.
/// Es una clase a propósito: se compara por referencia, de modo que dos líneas
/// con el mismo texto siguen siendo eventos distintos para la vista.
/// </summary>
public sealed class LogLine(LogSeverity severity, string text)
{
    public LogSeverity Severity { get; } = severity;

    public string Text { get; } = text;
}
