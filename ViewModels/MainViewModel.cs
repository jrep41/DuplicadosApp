using System.Collections;
using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Data;
using System.Windows.Threading;
using DuplicadosApp.Models;
using DuplicadosApp.Services;

namespace DuplicadosApp.ViewModels;

/// <summary>Opción legible para mostrar en los selectores de la interfaz.</summary>
public sealed record NamedOption<T>(T Value, string Label);

/// <summary>
/// Modelo de vista principal: orquesta el escaneo y el borrado en hilos de fondo,
/// aplica filtrado/ordenación instantáneos y actualiza la interfaz sin bloqueos.
/// </summary>
public sealed class MainViewModel : ObservableObject, IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _heartbeat;
    private readonly DispatcherTimer _phaseTimer;
    private readonly ListCollectionView _resultsView;

    private CancellationTokenSource? _cancellation;
    private PauseGate? _pauseGate;
    private bool _isBusy;
    private bool _isPausedUi;
    private bool _suppressSelectionNotifications;
    private string _folderPath = string.Empty;
    private string _progressText = "Listo";
    private double _progressValue;
    private bool _isProgressIndeterminate;
    private string _statusText = "Selecciona o arrastra una carpeta para empezar.";
    private bool _verbose = true;
    private bool _ignoreEmptyFiles = true;
    private bool _requireSameExtension = true;
    private bool _ignoreHiddenAndSystem;
    private bool _isActivityExpanded = true;
    private bool _isDarkTheme;
    private bool _hasResults;
    private int _selectedCount;
    private long _selectedBytes;
    private string _searchFilter = string.Empty;
    private SortOption _selectedSortOption = SortOption.RecoverableSpaceDesc;
    private SelectionStrategy _selectedStrategy = SelectionStrategy.KeepOldest;
    private string _lastScanStatsText = string.Empty;
    private int _visibleGroupCount;
    private int _totalDuplicateFilesCount;
    private long _totalRecoverableBytes;

    private int _nextGroupIndex;
    private volatile string _activity = string.Empty;
    private readonly Stopwatch _operationClock = new();

    private string _phaseLabel = "Sin actividad";
    private double _phaseValue;
    private bool _isPhaseIndeterminate;

    private readonly Queue<PhaseProgress> _phaseQueue = new();
    private PhaseProgress? _phaseLive;
    private string _phaseLastAction = "Sin actividad";
    private PhaseProgress? _phaseCurrent;
    private long _phaseShownAtTicks;
    private const int MinPhaseDisplayMs = 200;

    private double _pendingProgressValue;
    private string? _pendingProgressText;
    private bool _progressPostPending;
    private readonly object _progressLock = new();

    // Cola por lotes para el registro de actividad: evita saturar el Dispatcher con miles de posts.
    private readonly ConcurrentQueue<LogLine> _pendingLogs = new();
    private int _logFlushScheduled;

    private bool _disposed;

    public MainViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;

        _resultsView = (ListCollectionView)CollectionViewSource.GetDefaultView(Groups);
        _resultsView.Filter = FilterGroup;
        _resultsView.CustomSort = new GroupComparer(_selectedSortOption);

        SortOptions =
        [
            new(SortOption.RecoverableSpaceDesc, "Mayor espacio recuperable"),
            new(SortOption.FileSizeDesc, "Mayor tamaño de archivo"),
            new(SortOption.FileCountDesc, "Más copias"),
            new(SortOption.NameAsc, "Nombre (A-Z)"),
        ];

        SelectionStrategies =
        [
            new(SelectionStrategy.KeepOldest, "Conservar el más antiguo"),
            new(SelectionStrategy.KeepNewest, "Conservar el más reciente"),
            new(SelectionStrategy.KeepShortestPath, "Conservar ruta más corta"),
            new(SelectionStrategy.KeepFirst, "Conservar el primero"),
        ];

        SelectFolderCommand = new RelayCommand(SelectFolder, () => !IsBusy);
        StartSearchCommand = new RelayCommand(StartSearch, () => !IsBusy && HasFolder);
        TogglePauseCommand = new RelayCommand(TogglePause, () => IsBusy);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        SelectCopiesCommand = new RelayCommand(() => SelectByStrategy(SelectedStrategy), () => HasResults && !IsBusy);
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => SelectedCount > 0 && !IsBusy);
        DeleteSelectedCommand = new RelayCommand(DeleteSelected, () => SelectedCount > 0 && !IsBusy);
        ExportCsvCommand = new RelayCommand(ExportCsv, () => HasResults && !IsBusy);
        AbrirPapeleraPropiaCommand = new RelayCommand(AbrirPapeleraPropia);
        ToggleThemeCommand = new RelayCommand(() => IsDarkTheme = !IsDarkTheme);
        ToggleActivityCommand = new RelayCommand(() => IsActivityExpanded = !IsActivityExpanded);
        ClearFilterCommand = new RelayCommand(() => SearchFilter = string.Empty, () => !string.IsNullOrEmpty(SearchFilter));

        OpenFileCommand = new RelayCommand<FileItemViewModel>(AbrirArchivo);
        RevealInExplorerCommand = new RelayCommand<FileItemViewModel>(MostrarEnExplorador);
        CopyPathCommand = new RelayCommand<FileItemViewModel>(CopiarRuta);
        KeepOnlyCommand = new RelayCommand<FileItemViewModel>(ConservarSoloEste, _ => !IsBusy);

        _heartbeat = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromSeconds(2),
        };
        _heartbeat.Tick += (_, _) =>
        {
            if (!IsBusy)
            {
                return;
            }

            var estado = string.IsNullOrWhiteSpace(_activity) ? "Sin novedades" : _activity;
            var avance = BuildPhaseSummary();
            AppendLog(_pauseGate?.IsPaused == true
                ? $"…en pausa — {avance} · {estado} ({_operationClock.Elapsed.TotalSeconds:F1}s)"
                : $"…{avance} · {estado} ({_operationClock.Elapsed.TotalSeconds:F1}s)");
        };

        _phaseTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _phaseTimer.Tick += (_, _) => AdvancePhase();
    }

    public Func<DeletePrompt, bool>? ConfirmDelete { get; set; }

    public Action<string, string, Action?>? ShowMessage { get; set; }

    public Action<string>? OpenFolder { get; set; }

    public Action<string>? OpenFile { get; set; }

    public Action<string>? RevealFile { get; set; }

    public Action<string>? CopyText { get; set; }

    public Action<bool>? ThemeChanged { get; set; }

    public static string RutaPapeleraPropia => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DuplicadosApp",
        "Borrados");

    /// <summary>Se dispara por lotes cuando hay líneas nuevas para el panel Actividad.</summary>
    public event EventHandler<IReadOnlyList<LogLine>>? LogsAppended;

    public event EventHandler? LogCleared;

    public BulkObservableCollection<DuplicateGroupViewModel> Groups { get; } = [];

    /// <summary>Vista filtrable, ordenable y virtualizable de <see cref="Groups"/>.</summary>
    public ICollectionView ResultsView => _resultsView;

    public IReadOnlyList<NamedOption<SortOption>> SortOptions { get; }

    public IReadOnlyList<NamedOption<SelectionStrategy>> SelectionStrategies { get; }

    public RelayCommand SelectFolderCommand { get; }

    public RelayCommand StartSearchCommand { get; }

    public RelayCommand TogglePauseCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand SelectCopiesCommand { get; }

    public RelayCommand ClearSelectionCommand { get; }

    public RelayCommand DeleteSelectedCommand { get; }

    public RelayCommand ExportCsvCommand { get; }

    public RelayCommand AbrirPapeleraPropiaCommand { get; }

    public RelayCommand ToggleThemeCommand { get; }

    public RelayCommand ToggleActivityCommand { get; }

    public RelayCommand ClearFilterCommand { get; }

    public RelayCommand<FileItemViewModel> OpenFileCommand { get; }

    public RelayCommand<FileItemViewModel> RevealInExplorerCommand { get; }

    public RelayCommand<FileItemViewModel> CopyPathCommand { get; }

    public RelayCommand<FileItemViewModel> KeepOnlyCommand { get; }

    public string FolderPath
    {
        get => _folderPath;
        set
        {
            var trimmed = value?.Trim() ?? string.Empty;
            if (SetProperty(ref _folderPath, trimmed))
            {
                OnPropertyChanged(nameof(HasFolder));
                RefreshCommands();
            }
        }
    }

    public bool HasFolder => !string.IsNullOrWhiteSpace(FolderPath);

    public bool Verbose
    {
        get => _verbose;
        set => SetProperty(ref _verbose, value);
    }

    public bool IgnoreEmptyFiles
    {
        get => _ignoreEmptyFiles;
        set => SetProperty(ref _ignoreEmptyFiles, value);
    }

    public bool RequireSameExtension
    {
        get => _requireSameExtension;
        set => SetProperty(ref _requireSameExtension, value);
    }

    public bool IgnoreHiddenAndSystem
    {
        get => _ignoreHiddenAndSystem;
        set => SetProperty(ref _ignoreHiddenAndSystem, value);
    }

    public bool IsActivityExpanded
    {
        get => _isActivityExpanded;
        set => SetProperty(ref _isActivityExpanded, value);
    }

    public bool IsDarkTheme
    {
        get => _isDarkTheme;
        set
        {
            if (SetProperty(ref _isDarkTheme, value))
            {
                OnPropertyChanged(nameof(ThemeToggleTooltip));
                ThemeChanged?.Invoke(value);
            }
        }
    }

    public string ThemeToggleTooltip => IsDarkTheme ? "Cambiar a tema claro" : "Cambiar a tema oscuro";

    public string SearchFilter
    {
        get => _searchFilter;
        set
        {
            if (SetProperty(ref _searchFilter, value ?? string.Empty))
            {
                OnPropertyChanged(nameof(HasSearchFilter));
                ClearFilterCommand.RaiseCanExecuteChanged();
                ApplyFilter();
            }
        }
    }

    public bool HasSearchFilter => !string.IsNullOrEmpty(SearchFilter);

    public SortOption SelectedSortOption
    {
        get => _selectedSortOption;
        set
        {
            if (SetProperty(ref _selectedSortOption, value))
            {
                _resultsView.CustomSort = new GroupComparer(value);
            }
        }
    }

    public SelectionStrategy SelectedStrategy
    {
        get => _selectedStrategy;
        set => SetProperty(ref _selectedStrategy, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                RefreshCommands();

                if (value)
                {
                    _phaseTimer.Start();
                }
                else
                {
                    _phaseTimer.Stop();
                }
            }
        }
    }

    public bool IsIdle => !IsBusy;

    public bool IsPaused
    {
        get => _isPausedUi;
        private set
        {
            if (SetProperty(ref _isPausedUi, value))
            {
                OnPropertyChanged(nameof(PauseButtonText));
            }
        }
    }

    public string PauseButtonText => IsPaused ? "Reanudar" : "Pausar";

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    public bool IsProgressIndeterminate
    {
        get => _isProgressIndeterminate;
        private set => SetProperty(ref _isProgressIndeterminate, value);
    }

    public string ProgressText
    {
        get => _progressText;
        private set => SetProperty(ref _progressText, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string LastScanStatsText
    {
        get => _lastScanStatsText;
        private set
        {
            if (SetProperty(ref _lastScanStatsText, value))
            {
                OnPropertyChanged(nameof(HasScanStats));
            }
        }
    }

    public bool HasScanStats => !string.IsNullOrWhiteSpace(LastScanStatsText);

    public string Activity
    {
        get => _activity;
        private set
        {
            _activity = value;
            OnPropertyChanged();
        }
    }

    private void ReportActivity(string text)
    {
        _activity = text;
        if (_dispatcher.CheckAccess())
        {
            OnPropertyChanged(nameof(Activity));
        }
    }

    public string PhaseLabel
    {
        get => _phaseLabel;
        private set => SetProperty(ref _phaseLabel, value);
    }

    public double PhaseValue
    {
        get => _phaseValue;
        private set => SetProperty(ref _phaseValue, value);
    }

    public bool IsPhaseIndeterminate
    {
        get => _isPhaseIndeterminate;
        private set => SetProperty(ref _isPhaseIndeterminate, value);
    }

    public bool HasResults
    {
        get => _hasResults;
        private set
        {
            if (SetProperty(ref _hasResults, value))
            {
                OnPropertyChanged(nameof(ShowEmptyPlaceholder));
                OnPropertyChanged(nameof(ShowNoFilterMatch));
                RefreshCommands();
            }
        }
    }

    public int VisibleGroupCount
    {
        get => _visibleGroupCount;
        private set
        {
            if (SetProperty(ref _visibleGroupCount, value))
            {
                OnPropertyChanged(nameof(ShowNoFilterMatch));
            }
        }
    }

    public bool ShowEmptyPlaceholder => !HasResults;

    public bool ShowNoFilterMatch => HasResults && VisibleGroupCount == 0;

    public int TotalDuplicateFilesCount
    {
        get => _totalDuplicateFilesCount;
        private set => SetProperty(ref _totalDuplicateFilesCount, value);
    }

    public long TotalRecoverableBytes
    {
        get => _totalRecoverableBytes;
        private set
        {
            if (SetProperty(ref _totalRecoverableBytes, value))
            {
                OnPropertyChanged(nameof(TotalRecoverableText));
            }
        }
    }

    public string TotalRecoverableText => FileItemViewModel.FormatSize(TotalRecoverableBytes);

    public int SelectedCount
    {
        get => _selectedCount;
        private set
        {
            if (SetProperty(ref _selectedCount, value))
            {
                OnPropertyChanged(nameof(SelectionSummary));
                RefreshCommands();
            }
        }
    }

    public long SelectedBytes
    {
        get => _selectedBytes;
        private set
        {
            if (SetProperty(ref _selectedBytes, value))
            {
                OnPropertyChanged(nameof(SelectionSummary));
            }
        }
    }

    public string SelectionSummary => SelectedCount == 0
        ? "Ningún archivo seleccionado"
        : $"{SelectedCount:N0} archivo(s) · {FileItemViewModel.FormatSize(SelectedBytes)} a liberar";

    public void SelectFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Selecciona la carpeta donde buscar duplicados",
            Multiselect = false,
            InitialDirectory = Directory.Exists(FolderPath) ? FolderPath : string.Empty,
        };

        if (dialog.ShowDialog() == true)
        {
            SetFolder(dialog.FolderName);
        }
    }

    /// <summary>Acepta una carpeta (o un archivo, usando su carpeta padre) arrastrada a la ventana.</summary>
    public bool TrySetFolderFromDrop(string droppedPath)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(droppedPath))
        {
            return false;
        }

        if (Directory.Exists(droppedPath))
        {
            SetFolder(droppedPath);
            return true;
        }

        if (File.Exists(droppedPath) && Path.GetDirectoryName(droppedPath) is { } parent && Directory.Exists(parent))
        {
            SetFolder(parent);
            return true;
        }

        return false;
    }

    public void SetFolder(string path)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        FolderPath = path;
        ClearResults();

        ProgressValue = 0;
        IsProgressIndeterminate = false;
        ProgressText = "Listo";
        Activity = string.Empty;
        LastScanStatsText = string.Empty;
        ResetPhase();
        ClearLog();

        StatusText = "Carpeta lista. Pulsa «Buscar duplicados» (F5).";
    }

    public ScanOptions BuildCurrentScanOptions() => new()
    {
        MinFileSizeBytes = IgnoreEmptyFiles ? 1 : 0,
        RequireSameExtension = RequireSameExtension,
        IgnoreHiddenAndSystem = IgnoreHiddenAndSystem,
        SkipCloudOnlyFiles = true,
    };

    public void StartSearch()
    {
        if (IsBusy || !HasFolder)
        {
            return;
        }

        var folder = FolderPath.Trim().Trim('"');
        if (!Directory.Exists(folder))
        {
            ShowMessage?.Invoke(
                "Carpeta no encontrada",
                $"La carpeta indicada no existe o no es accesible:\n\n{folder}",
                null);
            return;
        }

        FolderPath = folder;
        ClearResults();
        ClearLog();

        _pauseGate?.Dispose();
        _pauseGate = new PauseGate();
        IsPaused = false;
        IsBusy = true;
        _operationClock.Restart();
        _heartbeat.Start();
        ProgressValue = 0;
        IsProgressIndeterminate = true;
        ProgressText = "Preparando el escaneo…";
        StatusText = "Buscando duplicados…";
        Activity = "Preparando el escaneo";
        LastScanStatsText = string.Empty;
        ResetPhase();

        var options = BuildCurrentScanOptions();
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        var pauseGate = _pauseGate;

        Task.Run(() =>
        {
            var finder = new DuplicateFinder(
                AppendLog,
                QueueProgress,
                () => pauseGate.IsPaused,
                () => token.IsCancellationRequested,
                ReportActivity,
                ReportPhase,
                options,
                () => pauseGate.WaitIfPaused(token));

            ScanResult result;
            try
            {
                result = finder.Run(folder);
            }
            catch (Exception ex)
            {
                result = new ScanResult { GeneralError = ex.Message };
                AppendLog($"✖ ERROR inesperado: {ex}");
            }

            Post(() => FinishSearch(result));
        });
    }

    private void FinishSearch(ScanResult result)
    {
        StopOperationTimers();

        if (result.Cancelled)
        {
            ClearResults();
            ProgressValue = 0;
            ProgressText = "Cancelado";
            StatusText = "Búsqueda cancelada. Puedes ajustar las opciones y volver a buscar.";
            return;
        }

        if (!string.IsNullOrEmpty(result.GeneralError))
        {
            ClearResults();
            StatusText = $"No se pudo completar la búsqueda: {result.GeneralError}";
            ProgressText = "Error";
            return;
        }

        ProgressValue = 100;
        ProgressText = $"Completado en {result.Elapsed.TotalSeconds:F1}s";

        var viewModels = new List<DuplicateGroupViewModel>(result.Groups.Count);
        foreach (var group in result.Groups)
        {
            viewModels.Add(new DuplicateGroupViewModel(group, _nextGroupIndex++, OnItemSelectionChanged));
        }

        // Una sola notificación Reset para WPF: instantáneo incluso con miles de grupos.
        Groups.ReplaceAll(viewModels);
        _resultsView.CustomSort = new GroupComparer(SelectedSortOption);
        UpdateAggregateMetrics();

        var stats = new List<string>(4)
        {
            $"{result.TotalFilesScanned:N0} archivos en {result.TotalFoldersScanned:N0} carpetas",
            $"{result.Elapsed.TotalSeconds:F1}s",
        };
        if (result.SkippedHardLinks > 0)
        {
            stats.Add($"{result.SkippedHardLinks:N0} enlace(s) duro(s) omitido(s)");
        }

        if (result.SkippedCloudFiles > 0)
        {
            stats.Add($"{result.SkippedCloudFiles:N0} solo en nube omitido(s)");
        }

        LastScanStatsText = string.Join(" · ", stats);

        if (Groups.Count == 0)
        {
            StatusText = result.Errors > 0
                ? $"No se encontraron duplicados entre los archivos legibles ({result.Errors:N0} error(es) de lectura)."
                : "No se encontraron archivos duplicados.";
        }
        else
        {
            StatusText = BuildResultsStatusText(result.Errors);
        }
    }

    public void TogglePause()
    {
        if (!IsBusy || _pauseGate is null)
        {
            return;
        }

        if (_pauseGate.IsPaused)
        {
            _pauseGate.Resume();
            IsPaused = false;
            ProgressText = "Reanudando…";
            AppendLog("Reanudado por el usuario");
        }
        else
        {
            _pauseGate.Pause();
            IsPaused = true;
            ProgressText = "En pausa";
            AppendLog("Pausado por el usuario");
        }
    }

    public void Cancel()
    {
        if (!IsBusy)
        {
            return;
        }

        _pauseGate?.Resume();
        IsPaused = false;
        _cancellation?.Cancel();
        AppendLog("Cancelación solicitada por el usuario");
        ProgressText = "Cancelando…";
    }

    /// <summary>Marca todas las copias siguiendo la estrategia elegida (más antiguo, más reciente, ruta más corta o primero).</summary>
    public void SelectByStrategy(SelectionStrategy strategy)
    {
        _suppressSelectionNotifications = true;
        try
        {
            foreach (var group in Groups)
            {
                group.SelectByStrategy(strategy);
            }
        }
        finally
        {
            _suppressSelectionNotifications = false;
            RecalcularSeleccion();
        }
    }

    /// <summary>Compatibilidad directa: marca todas las copias dejando el primer archivo de cada grupo sin marcar.</summary>
    public void SelectAllCopies() => SelectByStrategy(SelectionStrategy.KeepFirst);

    public void ClearSelection()
    {
        _suppressSelectionNotifications = true;
        try
        {
            foreach (var item in AllItems())
            {
                item.IsSelected = false;
            }
        }
        finally
        {
            _suppressSelectionNotifications = false;
            _selectedCount = 0;
            _selectedBytes = 0;
            NotifySelectionTotalsChanged();
        }
    }

    /// <summary>Conserva únicamente este archivo dentro de su grupo y marca el resto para borrar.</summary>
    public void ConservarSoloEste(FileItemViewModel item)
    {
        if (IsBusy)
        {
            return;
        }

        _suppressSelectionNotifications = true;
        try
        {
            item.Group.KeepOnly(item);
        }
        finally
        {
            _suppressSelectionNotifications = false;
            RecalcularSeleccion();
        }
    }

    public void AbrirArchivo(FileItemViewModel item)
    {
        if (!File.Exists(item.Path))
        {
            ShowMessage?.Invoke(
                "Aviso",
                $"El archivo ya no está en su ruta:\n\n{item.Path}\n\n" +
                "Es posible que se haya movido o enviado a la papelera desde otra ventana.",
                null);
            return;
        }

        OpenFile?.Invoke(item.Path);
    }

    public void MostrarEnExplorador(FileItemViewModel item)
    {
        if (File.Exists(item.Path))
        {
            RevealFile?.Invoke(item.Path);
            return;
        }

        var dir = Path.GetDirectoryName(item.Path);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            OpenFolder?.Invoke(dir);
            return;
        }

        ShowMessage?.Invoke("Aviso", $"Ni el archivo ni su carpeta existen ya:\n\n{item.Path}", null);
    }

    public void CopiarRuta(FileItemViewModel item) => CopyText?.Invoke(item.Path);

    public void ExportCsv()
    {
        if (!HasResults || IsBusy)
        {
            return;
        }

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Exportar informe de duplicados",
            Filter = "Archivo CSV (*.csv)|*.csv|Todos los archivos (*.*)|*.*",
            FileName = $"duplicados-{DateTime.Now:yyyyMMdd-HHmmss}.csv",
            DefaultExt = ".csv",
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var sb = new StringBuilder(capacity: 4096);
            sb.AppendLine("Grupo;Nombre;TamanoBytes;TamanoLegible;Modificado;Seleccionado;Ruta");
            var groupNum = 0;
            foreach (var group in Groups)
            {
                groupNum++;
                foreach (var file in group.Files)
                {
                    sb.Append(groupNum).Append(';')
                      .Append(EscapeCsv(group.DisplayName)).Append(';')
                      .Append(file.Size).Append(';')
                      .Append(EscapeCsv(file.SizeText)).Append(';')
                      .Append(EscapeCsv(file.ModifiedText)).Append(';')
                      .Append(file.IsSelected ? "Si" : "No").Append(';')
                      .AppendLine(EscapeCsv(file.Path));
                }
            }

            File.WriteAllText(dialog.FileName, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            AppendLog($"Informe exportado en: {dialog.FileName}");
            ShowMessage?.Invoke("Informe exportado", $"Se guardó el informe en:\n\n{dialog.FileName}", null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage?.Invoke("Error al exportar", $"No se pudo guardar el informe:\n\n{ex.Message}", null);
        }
    }

    private static string EscapeCsv(string value)
    {
        if (value.Contains(';') || value.Contains('"') || value.Contains('\n') || value.Contains('\r'))
        {
            return $"\"{value.Replace("\"", "\"\"")}\"";
        }

        return value;
    }

    public void AbrirPapeleraPropia()
    {
        try
        {
            Directory.CreateDirectory(RutaPapeleraPropia);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage?.Invoke("Aviso", $"No se pudo abrir la carpeta: {ex.Message}", null);
            return;
        }

        OpenFolder?.Invoke(RutaPapeleraPropia);
    }

    private void AbrirPapeleraPropiaSiTieneContenido(string carpeta)
    {
        if (string.IsNullOrWhiteSpace(carpeta) || !Directory.Exists(carpeta))
        {
            return;
        }

        try
        {
            if (Directory.EnumerateFileSystemEntries(carpeta).Any())
            {
                OpenFolder?.Invoke(carpeta);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    private void OnItemSelectionChanged(FileItemViewModel item, bool isSelected, long size)
    {
        if (_suppressSelectionNotifications)
        {
            _selectedCount += isSelected ? 1 : -1;
            _selectedBytes += isSelected ? size : -size;
            return;
        }

        SelectedCount += isSelected ? 1 : -1;
        SelectedBytes += isSelected ? size : -size;
    }

    private void NotifySelectionTotalsChanged()
    {
        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(SelectedBytes));
        OnPropertyChanged(nameof(SelectionSummary));
        RefreshCommands();
    }

    public void DeleteSelected()
    {
        if (IsBusy)
        {
            return;
        }

        var marcados = AllItems().Where(item => item.IsSelected).ToList();
        if (marcados.Count == 0)
        {
            ShowMessage?.Invoke("Aviso", "No has seleccionado ningún archivo para eliminar.", null);
            return;
        }

        // Si en un grupo están marcadas TODAS las copias, se conserva la mejor copia según la estrategia.
        var conservar = new HashSet<FileItemViewModel>();
        foreach (var grupo in Groups)
        {
            if (grupo.Files.Count > 0 && grupo.Files.All(item => item.IsSelected))
            {
                conservar.Add(grupo.Files[0]);
            }
        }

        var aEnviar = marcados.Where(item => !conservar.Contains(item)).ToList();
        if (aEnviar.Count == 0)
        {
            ShowMessage?.Invoke(
                "Aviso",
                "No hay nada que enviar: de cada grupo marcado se conserva al menos una copia.",
                null);
            return;
        }

        var bytesToFree = aEnviar.Sum(item => item.Size);
        if (ConfirmDelete?.Invoke(new DeletePrompt(aEnviar.Count, bytesToFree, conservar.Count)) != true)
        {
            return;
        }

        var selected = aEnviar.Select(item => item.ToCandidate()).ToList();
        var all = AllItems().Select(item => item.ToCandidate()).ToList();

        _pauseGate?.Dispose();
        _pauseGate = new PauseGate();
        IsPaused = false;
        IsBusy = true;
        _operationClock.Restart();
        _heartbeat.Start();
        ProgressValue = 0;
        IsProgressIndeterminate = false;
        ProgressText = "Guardando en la papelera…";
        StatusText = "Enviando los archivos seleccionados a la papelera…";
        Activity = "Iniciando el envío a la papelera";
        ResetPhase();

        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;
        var pauseGate = _pauseGate;
        var folder = FolderPath;

        Task.Run(() =>
        {
            var deleter = new FileDeleter(
                AppendLog,
                () => pauseGate.IsPaused,
                () => token.IsCancellationRequested,
                recycleDisponible: () => RecycleBin.IsAvailable(folder),
                papeleraPropia: () => new TrashService(RutaPapeleraPropia, DateTime.Now),
                activity: ReportActivity,
                phase: ReportPhase,
                progress: QueueProgress,
                waitIfPaused: () => pauseGate.WaitIfPaused(token));

            DeleteOutcome outcome;
            try
            {
                outcome = deleter.Delete(selected, all);
            }
            catch (Exception ex)
            {
                outcome = new DeleteOutcome { Errors = selected.Count };
                AppendLog($"✖ ERROR inesperado: {ex}");
            }

            Post(() => FinishDelete(outcome));
        });
    }

    private void FinishDelete(DeleteOutcome outcome)
    {
        StopOperationTimers();

        // Quita los archivos movidos y los grupos que ya no tienen duplicados (< 2 archivos).
        // Así la lista se actualiza al instante sin reescanear todo el disco ni borrar el registro.
        QuitarDeLosResultados(outcome.Movidos);

        var movidos = outcome.Deleted + outcome.SinVerificar;

        if (outcome.Cancelled)
        {
            StatusText = movidos > 0
                ? $"Envío cancelado: {movidos:N0} archivo(s) movido(s). Quedan {Groups.Count:N0} grupo(s)."
                : "Envío a la papelera cancelado.";
            ProgressText = "Cancelado";
        }
        else if (outcome.Errors > 0)
        {
            StatusText = $"Terminado con {outcome.Errors:N0} error(es) y {movidos:N0} archivo(s) movido(s). Quedan {Groups.Count:N0} grupo(s).";
            ProgressText = "Con avisos";
        }
        else if (outcome.SinVerificar > 0)
        {
            StatusText = $"Terminado: {movidos:N0} archivo(s) movido(s), {outcome.SinVerificar:N0} sin verificar.";
            ProgressText = "Con avisos";
        }
        else
        {
            StatusText = Groups.Count == 0
                ? $"¡Todo limpio! {movidos:N0} archivo(s) enviado(s) a la papelera; no quedan duplicados."
                : $"Movidos {movidos:N0} archivo(s) a la papelera · Quedan {BuildResultsStatusText(0)}";
            ProgressValue = 100;
            ProgressText = "Listo";
        }

        MostrarResultadoDelBorrado(outcome, movidos);
    }

    /// <summary>
    /// Quita de los resultados los archivos movidos y descarta los grupos que se quedan
    /// con menos de 2 archivos (ya no son duplicados).
    /// </summary>
    public void QuitarDeLosResultados(IReadOnlyCollection<string> rutas)
    {
        if (rutas.Count == 0)
        {
            return;
        }

        var movidas = new HashSet<string>(rutas, StringComparer.OrdinalIgnoreCase);

        _suppressSelectionNotifications = true;
        try
        {
            for (var i = Groups.Count - 1; i >= 0; i--)
            {
                var grupo = Groups[i];
                for (var j = grupo.Files.Count - 1; j >= 0; j--)
                {
                    if (movidas.Contains(grupo.Files[j].Path))
                    {
                        grupo.Quitar(grupo.Files[j]);
                    }
                }

                if (grupo.Files.Count < 2)
                {
                    Groups.RemoveAt(i);
                }
            }
        }
        finally
        {
            _suppressSelectionNotifications = false;
            RecalcularSeleccion();
            UpdateAggregateMetrics();
        }
    }

    private void MostrarResultadoDelBorrado(DeleteOutcome outcome, int movidos)
    {
        var partes = new List<string>(3);

        if (outcome.Errors > 0)
        {
            partes.Add(
                $"{outcome.Errors:N0} archivo(s) no se pudieron mover porque cambiaron o no se " +
                "pudieron procesar. Siguen en su sitio.");
        }

        if (outcome.SinVerificar > 0)
        {
            partes.Add(
                $"{outcome.SinVerificar:N0} archivo(s) ya no están en su carpeta original, pero no " +
                "se pudo confirmar que estén en la Papelera de reciclaje. Compruébalo en la " +
                "Papelera antes de darlos por perdidos.");
        }

        if (movidos > 0)
        {
            var verificados = outcome.Destino == DeleteDestination.PapeleraDeLaApp
                ? movidos
                : outcome.Deleted;

            partes.Add(outcome.Destino == DeleteDestination.PapeleraDeLaApp
                ? $"Se guardaron {verificados:N0} archivo(s) en la papelera propia de la aplicación:\n\n" +
                  $"{outcome.CarpetaPapeleraPropia}\n\n" +
                  "Cada archivo conserva su ruta original en manifiesto.txt, así que puedes " +
                  "devolverlos a su sitio si te arrepientes."
                : $"Se enviaron {verificados:N0} archivo(s) a la Papelera de reciclaje. Si te " +
                  "arrepientes, puedes restaurarlos desde la Papelera.");
        }

        if (partes.Count == 0)
        {
            return;
        }

        var titulo = outcome.Errors > 0 || outcome.SinVerificar > 0 ? "Aviso" : "Listo";
        var mensaje = string.Join("\n\n", partes);
        var abrirCarpeta = outcome.Destino == DeleteDestination.PapeleraDeLaApp
                           && outcome.CarpetaPapeleraPropia.Length > 0
            ? () => AbrirPapeleraPropiaSiTieneContenido(outcome.CarpetaPapeleraPropia)
            : (Action?)null;

        if (abrirCarpeta is not null)
        {
            mensaje += "\n\nSe abrirá esa carpeta al cerrar este aviso.";
        }

        ShowMessage?.Invoke(titulo, mensaje, abrirCarpeta);
    }

    private void StopOperationTimers()
    {
        _heartbeat.Stop();
        _operationClock.Stop();
        lock (_progressLock)
        {
            _progressPostPending = false;
            _pendingProgressText = null;
        }

        IsProgressIndeterminate = false;
        IsBusy = false;
        IsPaused = false;
        _pauseGate?.Dispose();
        _pauseGate = null;
        _cancellation?.Dispose();
        _cancellation = null;
        ResetPhase();
    }

    private bool FilterGroup(object obj)
    {
        if (string.IsNullOrWhiteSpace(_searchFilter))
        {
            return true;
        }

        return obj is DuplicateGroupViewModel group && group.MatchesFilter(_searchFilter);
    }

    private void ApplyFilter()
    {
        _resultsView.Refresh();
        VisibleGroupCount = _resultsView.Count;
    }

    private void UpdateAggregateMetrics()
    {
        var filesCount = 0;
        long recoverable = 0;
        foreach (var group in Groups)
        {
            filesCount += group.FileCount;
            recoverable += group.RecoverableBytes;
        }

        TotalDuplicateFilesCount = filesCount;
        TotalRecoverableBytes = recoverable;
        HasResults = Groups.Count > 0;
        VisibleGroupCount = _resultsView.Count;
    }

    private string BuildResultsStatusText(int errors)
    {
        return $"{Groups.Count:N0} grupo(s) · {TotalDuplicateFilesCount:N0} archivos · " +
               $"{TotalRecoverableText} recuperables" +
               (errors > 0 ? $" · {errors:N0} error(es) de lectura" : string.Empty);
    }

    private void RecalcularSeleccion()
    {
        var count = 0;
        long bytes = 0;
        foreach (var item in AllItems())
        {
            if (item.IsSelected)
            {
                count++;
                bytes += item.Size;
            }
        }

        _selectedCount = count;
        _selectedBytes = bytes;
        NotifySelectionTotalsChanged();
    }

    private IEnumerable<FileItemViewModel> AllItems() => Groups.SelectMany(group => group.Files);

    private void ClearResults()
    {
        Groups.ReplaceAll([]);
        _nextGroupIndex = 0;
        _searchFilter = string.Empty;
        OnPropertyChanged(nameof(SearchFilter));
        OnPropertyChanged(nameof(HasSearchFilter));
        ClearFilterCommand.RaiseCanExecuteChanged();
        TotalDuplicateFilesCount = 0;
        TotalRecoverableBytes = 0;
        HasResults = false;
        VisibleGroupCount = 0;
        SelectedCount = 0;
        SelectedBytes = 0;
    }

    private void ClearLog()
    {
        while (_pendingLogs.TryDequeue(out _))
        {
        }

        LogCleared?.Invoke(this, EventArgs.Empty);
    }

    private static LogSeverity Clasificar(string text)
    {
        if (text.StartsWith('✖'))
        {
            return LogSeverity.Error;
        }

        return text.StartsWith('⚠') ? LogSeverity.Warning : LogSeverity.Normal;
    }

    private void AppendLog(string text)
    {
        if (!Verbose)
        {
            return;
        }

        _pendingLogs.Enqueue(new LogLine(
            Clasificar(text),
            $"[{DateTime.Now:HH:mm:ss}] {text}"));

        if (Interlocked.CompareExchange(ref _logFlushScheduled, 1, 0) == 0)
        {
            Post(FlushPendingLogs);
        }
    }

    private void FlushPendingLogs()
    {
        Interlocked.Exchange(ref _logFlushScheduled, 0);

        if (_pendingLogs.IsEmpty)
        {
            return;
        }

        var batch = new List<LogLine>(32);
        while (batch.Count < 256 && _pendingLogs.TryDequeue(out var line))
        {
            batch.Add(line);
        }

        if (batch.Count > 0)
        {
            LogsAppended?.Invoke(this, batch);
        }

        if (!_pendingLogs.IsEmpty && Interlocked.CompareExchange(ref _logFlushScheduled, 1, 0) == 0)
        {
            Post(FlushPendingLogs);
        }
    }

    private void ReportPhase(PhaseProgress phase)
    {
        lock (_phaseQueue)
        {
            _phaseLive = phase;

            var action = ActionKey(phase.Phase);
            if (string.Equals(action, _phaseLastAction, StringComparison.Ordinal))
            {
                return;
            }

            _phaseLastAction = action;
            _phaseQueue.Enqueue(phase);
        }
    }

    private void AdvancePhase()
    {
        PhaseProgress? live;
        lock (_phaseQueue)
        {
            live = _phaseLive;
        }

        var elapsedShown = Environment.TickCount64 - _phaseShownAtTicks;
        if (_phaseCurrent is not null && elapsedShown < MinPhaseDisplayMs)
        {
            AnimateToward(_phaseCurrent, live);
            return;
        }

        if (_phaseCurrent is not null && live is not null
            && !string.Equals(ActionKey(live.Phase), ActionKey(_phaseCurrent.Phase), StringComparison.Ordinal))
        {
            if (!_phaseCurrent.IsIndeterminate && PhaseValue < 100)
            {
                PhaseValue = Math.Min(100, PhaseValue + Math.Max(4, (100 - PhaseValue) * 0.35));
                AnimateToward(_phaseCurrent, live);
                return;
            }
        }

        PhaseProgress? next = null;
        lock (_phaseQueue)
        {
            if (_phaseQueue.Count > 0)
            {
                next = _phaseQueue.Dequeue();
            }
        }

        if (next is not null)
        {
            _phaseCurrent = next;
            _phaseShownAtTicks = Environment.TickCount64;
            PhaseLabel = next.Phase;
            IsPhaseIndeterminate = next.IsIndeterminate;
            PhaseValue = next.Percentage;
            return;
        }

        if (live is null)
        {
            return;
        }

        _phaseCurrent = live;
        AnimateToward(live, live);
    }

    private void AnimateToward(PhaseProgress shown, PhaseProgress? live)
    {
        PhaseLabel = shown.Phase;
        IsPhaseIndeterminate = shown.IsIndeterminate;

        if (shown.IsIndeterminate)
        {
            return;
        }

        var isLiveAction = live is not null
            && string.Equals(ActionKey(live.Phase), ActionKey(shown.Phase), StringComparison.Ordinal);

        var target = isLiveAction ? live!.Percentage : 100;

        var current = PhaseValue;
        var difference = target - current;
        if (difference <= 0.5)
        {
            PhaseValue = target;
            return;
        }

        PhaseValue = current + Math.Max(2, difference * 0.25);
    }

    private static string ActionKey(string phaseName)
    {
        var index = phaseName.IndexOf('(');
        return (index > 0 ? phaseName[..index] : phaseName).Trim();
    }

    private string BuildPhaseSummary()
    {
        if (string.IsNullOrWhiteSpace(PhaseLabel) || PhaseLabel == "Sin actividad")
        {
            return string.IsNullOrWhiteSpace(_activity) ? "Sin novedades" : _activity;
        }

        return IsPhaseIndeterminate
            ? PhaseLabel
            : $"{PhaseLabel} {PhaseValue:0}%";
    }

    private void ResetPhase()
    {
        lock (_phaseQueue)
        {
            _phaseQueue.Clear();
            _phaseLive = null;
            _phaseLastAction = "Sin actividad";
        }

        _phaseCurrent = null;
        PhaseLabel = "Sin actividad";
        IsPhaseIndeterminate = false;
        PhaseValue = 0;

        if (IsBusy)
        {
            _phaseTimer.Start();
        }
        else
        {
            _phaseTimer.Stop();
        }
    }

    private void QueueProgress(ScanProgress progress)
    {
        lock (_progressLock)
        {
            _pendingProgressValue = progress.Percentage;
            _pendingProgressText = progress.Text;

            if (_progressPostPending)
            {
                return;
            }

            _progressPostPending = true;
        }

        Post(() =>
        {
            double value;
            string? text;
            lock (_progressLock)
            {
                _progressPostPending = false;
                value = _pendingProgressValue;
                text = _pendingProgressText;
                _pendingProgressText = null;
            }

            if (text is null)
            {
                return;
            }

            IsProgressIndeterminate = false;
            ProgressValue = value;
            ProgressText = text;
        });
    }

    private void Post(Action action)
    {
        if (_dispatcher.HasShutdownStarted || _dispatcher.HasShutdownFinished)
        {
            return;
        }

        if (_dispatcher.CheckAccess())
        {
            action();
            return;
        }

        try
        {
            _dispatcher.BeginInvoke(action, DispatcherPriority.Background);
        }
        catch (TaskCanceledException)
        {
        }
        catch (InvalidOperationException)
        {
        }
    }

    private void RefreshCommands()
    {
        SelectFolderCommand.RaiseCanExecuteChanged();
        StartSearchCommand.RaiseCanExecuteChanged();
        TogglePauseCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        SelectCopiesCommand.RaiseCanExecuteChanged();
        ClearSelectionCommand.RaiseCanExecuteChanged();
        DeleteSelectedCommand.RaiseCanExecuteChanged();
        ExportCsvCommand.RaiseCanExecuteChanged();
        KeepOnlyCommand.RaiseCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Cancel();
        _heartbeat.Stop();
        _phaseTimer.Stop();
        _pauseGate?.Dispose();
        _cancellation?.Dispose();
    }

    private sealed class GroupComparer(SortOption option) : IComparer
    {
        public int Compare(object? x, object? y)
        {
            if (x is not DuplicateGroupViewModel a || y is not DuplicateGroupViewModel b)
            {
                return 0;
            }

            return option switch
            {
                SortOption.RecoverableSpaceDesc => CompareFallback(b.RecoverableBytes.CompareTo(a.RecoverableBytes), a, b),
                SortOption.FileSizeDesc => CompareFallback(b.Size.CompareTo(a.Size), a, b),
                SortOption.FileCountDesc => CompareFallback(b.FileCount.CompareTo(a.FileCount), a, b),
                SortOption.NameAsc => string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase),
                _ => 0,
            };
        }

        private static int CompareFallback(int primary, DuplicateGroupViewModel a, DuplicateGroupViewModel b)
            => primary != 0 ? primary : string.Compare(a.DisplayName, b.DisplayName, StringComparison.OrdinalIgnoreCase);
    }
}
