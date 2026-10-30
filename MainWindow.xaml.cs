using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using DuplicadosApp.Models;
using DuplicadosApp.ViewModels;

namespace DuplicadosApp;

public partial class MainWindow : Window
{
    /// <summary>Si el registro supera este tamaño, se recortan las líneas más antiguas.</summary>
    private const int MaxLogChars = 512 * 1024;

    private readonly MainViewModel _viewModel;

    /// <summary>Caracteres añadidos al registro, para no recalcular el texto entero.</summary>
    private int _logChars;

    public MainWindow()
    {
        InitializeComponent();

        _viewModel = new MainViewModel(Dispatcher)
        {
            ConfirmDelete = ConfirmDelete,
            ShowMessage = ShowMessage,
            OpenFolder = AbrirCarpeta,
        };

        _viewModel.LogAppended += OnLogAppended;
        _viewModel.LogCleared += OnLogCleared;

        DataContext = _viewModel;
    }

    /// <summary>
    /// Cancela la operación en curso al cerrar. Sin esto, la ventana desaparecía mientras
    /// el hilo de fondo seguía recorriendo el disco y publicando avisos a un Dispatcher
    /// ya apagado.
    /// </summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _viewModel.Cancel();
        base.OnClosing(e);
    }

    private bool ConfirmDelete(DeletePrompt prompt)
    {
        var partes = new List<string>(4)
        {
            $"Se enviarán {prompt.Count} archivo(s) a la papelera, donde podrás restaurarlos si te arrepientes.",
        };

        // En el modo «nombre y tamaño» no se ha comparado el contenido: dos archivos
        // distintos pueden coincidir en nombre y tamaño. Se avisa aquí, que es el último
        // punto en el que el usuario puede echarse atrás.
        if (!prompt.ComparesContent)
        {
            partes.Add(
                "Estás en el modo «nombre y tamaño»: los archivos se agruparon solo por nombre y " +
                "tamaño, sin comparar su contenido. Dos archivos distintos pueden coincidir en ambos.");
        }

        if (prompt.GroupsKept > 0)
        {
            partes.Add(
                $"En {prompt.GroupsKept} grupo(s) has marcado todas las copias: se conservará la " +
                "primera de cada uno para no quedarte sin ninguna.");
        }

        partes.Add("¿Continuar?");

        return MessageBox.Show(
                   this,
                   string.Join("\n\n", partes),
                   "Confirmar envío a la papelera",
                   MessageBoxButton.YesNo,
                   MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    /// <summary>
    /// Muestra un mensaje y, cuando el usuario lo cierra, ejecuta la acción pendiente
    /// (por ejemplo, abrir una carpeta). Así la ventana no queda detrás del diálogo.
    /// </summary>
    private void ShowMessage(string title, string message, Action? alCerrar)
    {
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        alCerrar?.Invoke();
    }

    /// <summary>Abre una carpeta en el Explorador de Windows.</summary>
    private void AbrirCarpeta(string ruta)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{ruta}\"",
                UseShellExecute = false,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(
                this,
                $"No se pudo abrir la carpeta:\n\n{ex.Message}",
                "Aviso",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Añade un solo párrafo al final del registro, coloreado según su gravedad.
    /// Esto es lo que evita que el registro cueste más cuanto más largo es: antes se
    /// reconstruía todo el texto en cada línea.
    /// </summary>
    private void OnLogAppended(object? sender, LogLine line)
    {
        var brush = line.Severity switch
        {
            LogSeverity.Error => (Brush)FindResource("LogErrorBrush"),
            LogSeverity.Warning => (Brush)FindResource("LogWarningBrush"),
            _ => (Brush)FindResource("TextPrimaryBrush"),
        };

        var documento = RegistroTexto.Document;
        var parrafo = new Paragraph(new Run(line.Text))
        {
            Foreground = brush,
            Margin = new Thickness(0),
        };

        // RichTextBox arranca con un párrafo vacío que hay que reaprovechar.
        var vacio = documento.Blocks.Count == 1 && documento.Blocks.FirstBlock is Paragraph p
            && string.IsNullOrEmpty(new TextRange(p.ContentStart, p.ContentEnd).Text);
        if (vacio)
        {
            documento.Blocks.Clear();
        }

        documento.Blocks.Add(parrafo);
        _logChars += line.Text.Length + 1;

        RecortarRegistro();
        RegistroTexto.ScrollToEnd();
    }

    private void OnLogCleared(object? sender, EventArgs e)
    {
        RegistroTexto.Document.Blocks.Clear();
        _logChars = 0;
    }

    /// <summary>
    /// Mantiene el registro acotado: cuando pasa del límite borra los párrafos más
    /// antiguos (no todo el registro), de modo que la actividad reciente sigue visible.
    /// </summary>
    private void RecortarRegistro()
    {
        if (_logChars <= MaxLogChars)
        {
            return;
        }

        var documento = RegistroTexto.Document;
        var aQuitar = Math.Max(1, documento.Blocks.Count / 2);
        for (var i = 0; i < aQuitar; i++)
        {
            if (documento.Blocks.FirstBlock is not { } primero)
            {
                break;
            }

            // Se descuentan los caracteres reales del párrafo que se va. Antes se partía el
            // contador por la mitad aunque se quitaran párrafos, no caracteres, así que el
            // contador se desviaba y el límite de 512 KB dejaba de corresponder al texto.
            _logChars -= new TextRange(primero.ContentStart, primero.ContentEnd).Text.Length + 1;
            documento.Blocks.Remove(primero);
        }

        _logChars = Math.Max(0, _logChars);
    }
}
