using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using DuplicadosApp.Models;
using DuplicadosApp.ViewModels;

namespace DuplicadosApp;

public partial class MainWindow : Window
{
    /// <summary>Si el registro supera este tamaño, se recortan las líneas más antiguas.</summary>
    private const int MaxLogChars = 512 * 1024;

    private readonly MainViewModel _viewModel;
    private readonly Queue<int> _paragraphCharCounts = new();

    private int _logChars;
    private Brush _logNormalBrush = Brushes.Black;
    private Brush _logWarningBrush = Brushes.DarkOrange;
    private Brush _logErrorBrush = Brushes.Crimson;

    public MainWindow()
    {
        InitializeComponent();

        var initialDark = Application.Current is App app && app.IsDarkTheme;
        RefreshLogBrushes();

        _viewModel = new MainViewModel(Dispatcher)
        {
            IsDarkTheme = initialDark,
            ConfirmDelete = ConfirmDelete,
            ShowMessage = ShowMessage,
            OpenFolder = AbrirCarpeta,
            OpenFile = AbrirArchivoConAplicacionAsociada,
            RevealFile = MostrarArchivoEnExplorador,
            CopyText = CopiarAlPortapapeles,
            ThemeChanged = OnThemeChanged,
        };

        _viewModel.LogsAppended += OnLogsAppended;
        _viewModel.LogCleared += OnLogCleared;

        DataContext = _viewModel;
        SourceInitialized += (_, _) => App.ApplyWindowTitleBarTheme(this, _viewModel.IsDarkTheme);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        _viewModel.LogsAppended -= OnLogsAppended;
        _viewModel.LogCleared -= OnLogCleared;
        _viewModel.Dispose();
        base.OnClosing(e);
    }

    private void OnThemeChanged(bool isDark)
    {
        if (Application.Current is App app)
        {
            app.ApplyTheme(isDark);
        }

        RefreshLogBrushes();
    }

    private void RefreshLogBrushes()
    {
        _logNormalBrush = TryFindResource("TextPrimaryBrush") as Brush ?? Brushes.Black;
        _logWarningBrush = TryFindResource("LogWarningBrush") as Brush ?? Brushes.DarkOrange;
        _logErrorBrush = TryFindResource("LogErrorBrush") as Brush ?? Brushes.Crimson;
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.F && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control && _viewModel.HasResults)
        {
            SearchFilterBox.Focus();
            SearchFilterBox.SelectAll();
            e.Handled = true;
        }
    }

    private void FolderPathBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && _viewModel.StartSearchCommand.CanExecute(null))
        {
            e.Handled = true;
            _viewModel.StartSearch();
        }
    }

    private void Window_PreviewDragOver(object sender, DragEventArgs e)
    {
        if (!_viewModel.IsBusy && e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
        else
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
        }
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (_viewModel.IsBusy || !e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            return;
        }

        if (e.Data.GetData(DataFormats.FileDrop) is string[] { Length: > 0 } paths)
        {
            _viewModel.TrySetFolderFromDrop(paths[0]);
        }
    }

    private bool ConfirmDelete(DeletePrompt prompt)
    {
        var partes = new List<string>(4)
        {
            $"Se enviarán {prompt.Count:N0} archivo(s) ({FileItemViewModel.FormatSize(prompt.Bytes)}) a la papelera, donde podrás restaurarlos si te arrepientes.",
        };

        if (prompt.GroupsKept > 0)
        {
            partes.Add(
                $"En {prompt.GroupsKept:N0} grupo(s) has marcado todas las copias: se conservará la " +
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

    private void ShowMessage(string title, string message, Action? alCerrar)
    {
        MessageBox.Show(this, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        alCerrar?.Invoke();
    }

    private void AbrirCarpeta(string ruta)
    {
        try
        {
            Process.Start(new ProcessStartInfo
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

    private void MostrarArchivoEnExplorador(string ruta)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{ruta}\"",
                UseShellExecute = false,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            MessageBox.Show(
                this,
                $"No se pudo mostrar el archivo en el Explorador:\n\n{ex.Message}",
                "Aviso",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    private void CopiarAlPortapapeles(string texto)
    {
        try
        {
            Clipboard.SetText(texto);
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.ExternalException)
        {
            // El portapapeles puede estar bloqueado momentáneamente por otra aplicación.
        }
    }

    private void RutaArchivo_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2
            || sender is not FrameworkElement { DataContext: FileItemViewModel item })
        {
            return;
        }

        e.Handled = true;
        _viewModel.AbrirArchivo(item);
    }

    private void AbrirArchivoConAplicacionAsociada(string ruta)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = ruta,
                UseShellExecute = true,
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception
                                       or InvalidOperationException
                                       or System.IO.FileNotFoundException)
        {
            MessageBox.Show(
                this,
                $"No se pudo abrir el archivo:\n\n{ruta}\n\n{ex.Message}",
                "Aviso",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Añade un lote de líneas al final del registro en una sola pasada y desplaza el scroll una vez.
    /// </summary>
    private void OnLogsAppended(object? sender, IReadOnlyList<LogLine> lines)
    {
        var documento = RegistroTexto.Document;

        if (_paragraphCharCounts.Count == 0 && documento.Blocks.Count == 1)
        {
            documento.Blocks.Clear();
        }

        foreach (var line in lines)
        {
            var brush = line.Severity switch
            {
                LogSeverity.Error => _logErrorBrush,
                LogSeverity.Warning => _logWarningBrush,
                _ => _logNormalBrush,
            };

            var parrafo = new Paragraph(new Run(line.Text))
            {
                Foreground = brush,
                Margin = new Thickness(0),
            };

            documento.Blocks.Add(parrafo);
            var len = line.Text.Length + 1;
            _paragraphCharCounts.Enqueue(len);
            _logChars += len;
        }

        RecortarRegistro();
        RegistroTexto.ScrollToEnd();
    }

    private void OnLogCleared(object? sender, EventArgs e)
    {
        RegistroTexto.Document.Blocks.Clear();
        _paragraphCharCounts.Clear();
        _logChars = 0;
    }

    /// <summary>Recorta los párrafos más antiguos en O(1) por párrafo sin crear objetos TextRange.</summary>
    private void RecortarRegistro()
    {
        if (_logChars <= MaxLogChars)
        {
            return;
        }

        var documento = RegistroTexto.Document;
        var aQuitar = Math.Max(1, _paragraphCharCounts.Count / 2);
        for (var i = 0; i < aQuitar; i++)
        {
            if (documento.Blocks.FirstBlock is not { } primero || !_paragraphCharCounts.TryDequeue(out var len))
            {
                break;
            }

            _logChars -= len;
            documento.Blocks.Remove(primero);
        }

        _logChars = Math.Max(0, _logChars);
    }
}
