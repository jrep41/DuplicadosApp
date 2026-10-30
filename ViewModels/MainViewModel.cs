using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows.Data;
using System.Windows.Threading;
using DuplicadosApp.Models;
using DuplicadosApp.Services;

namespace DuplicadosApp.ViewModels;

/// <summary>
/// Modelo de vista principal: orquesta el escaneo y el borrado en hilos de fondo
/// y refleja el progreso en la interfaz, replicando el comportamiento de duplica2.py.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _heartbeat;
    private readonly ICollectionView _groupedResults;

    private CancellationTokenSource? _cancellation;
    private volatile bool _isPaused;
    private bool _isBusy;
    private bool _isPausedUi;
    private bool _suppressSelectionNotifications;
    private string _folderPath = string.Empty;
    private string _progressText = "Listo";
    private double _progressValue;
    private bool _isProgressIndeterminate;
    private string _statusText = "Selecciona una carpeta para empezar.";
    private bool _compareByteByByte;
    private bool _verbose = true;
    private bool _hasResults;
    private int _selectedCount;
    private long _selectedBytes;

    /// <summary>
    /// Índice del próximo grupo. Es un contador propio y no <c>Groups.Count</c> porque los
    /// grupos pueden desaparecer de la lista (archivos ya movidos a la papelera) y dos
    /// grupos distintos no deben compartir índice: es la clave con la que el borrado
    /// localiza a los hermanos de un archivo.
    /// </summary>
    private int _nextGroupIndex;
    private string _activity = string.Empty;
    private readonly Stopwatch _operationClock = new();

    private string _phaseLabel = "Sin actividad";
    private double _phaseValue;
    private bool _isPhaseIndeterminate;

    private readonly Queue<PhaseProgress> _phaseQueue = new();
    private PhaseProgress? _phaseLive;
    private string _phaseLastAction = "Sin actividad";
    private PhaseProgress? _phaseCurrent;
    private long _phaseShownAtTicks;
    private const int MinPhaseDisplayMs = 250;
    private readonly DispatcherTimer _phaseTimer;

    private double _pendingProgressValue;
    private string? _pendingProgressText;
    private bool _progressPostPending;

    /// <summary>Protege los campos de progreso: la fase de confirmación los publica desde varios hilos.</summary>
    private readonly object _progressLock = new();

    public MainViewModel(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;

        var viewSource = new CollectionViewSource { Source = Groups };
        viewSource.GroupDescriptions.Add(new PropertyGroupDescription(nameof(DuplicateGroupViewModel.GroupKey)));
        _groupedResults = viewSource.View;

        SelectFolderCommand = new RelayCommand(SelectFolder, () => !IsBusy);
        StartSearchCommand = new RelayCommand(StartSearch, () => !IsBusy && HasFolder);
        TogglePauseCommand = new RelayCommand(TogglePause, () => IsBusy);
        CancelCommand = new RelayCommand(Cancel, () => IsBusy);
        SelectCopiesCommand = new RelayCommand(SelectAllCopies, () => HasResults && !IsBusy);
        ClearSelectionCommand = new RelayCommand(ClearSelection, () => HasResults && !IsBusy);
        DeleteSelectedCommand = new RelayCommand(DeleteSelected, () => SelectedCount > 0 && !IsBusy);
        AbrirPapeleraPropiaCommand = new RelayCommand(AbrirPapeleraPropia);

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

            var estado = string.IsNullOrWhiteSpace(Activity) ? "Sin novedades" : Activity;
            var avance = BuildPhaseSummary();
            AppendLog(_isPaused
                ? $"…en pausa — {avance} · {estado} ({_operationClock.Elapsed.TotalSeconds:F1}s)"
                : $"…{avance} · {estado} ({_operationClock.Elapsed.TotalSeconds:F1}s)");
        };

        _phaseTimer = new DispatcherTimer(DispatcherPriority.Background, dispatcher)
        {
            Interval = TimeSpan.FromMilliseconds(50),
        };
        _phaseTimer.Tick += (_, _) => AdvancePhase();
    }

    /// <summary>
    /// Pregunta al usuario si desea enviar archivos a la papelera. La implementa la vista,
    /// que recibe el detalle necesario para advertir de lo que no se compara o se conserva.
    /// </summary>
    public Func<DeletePrompt, bool>? ConfirmDelete { get; set; }

    /// <summary>
    /// Notifica a la vista que debe mostrar un mensaje informativo. Puede recibir una
    /// acción que la vista ejecuta cuando el usuario lo cierra.
    /// </summary>
    public Action<string, string, Action?>? ShowMessage { get; set; }

    /// <summary>Abre una carpeta en el Explorador. La implementa la vista.</summary>
    public Action<string>? OpenFolder { get; set; }

    /// <summary>
    /// Carpeta de la papelera propia de la aplicación. Se usa cuando la Papelera de
    /// reciclaje de Windows no está disponible en el entorno.
    /// </summary>
    public static string RutaPapeleraPropia => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DuplicadosApp",
        "Borrados");

    /// <summary>Se dispara cuando hay una línea nueva para el panel Actividad.</summary>
    public event EventHandler<LogLine>? LogAppended;

    public ObservableCollection<DuplicateGroupViewModel> Groups { get; } = [];

    /// <summary>Vista agrupada y virtualizable de <see cref="Groups"/>.</summary>
    public ICollectionView GroupedResults => _groupedResults;

    public RelayCommand SelectFolderCommand { get; }

    public RelayCommand StartSearchCommand { get; }

    public RelayCommand TogglePauseCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand SelectCopiesCommand { get; }

    public RelayCommand ClearSelectionCommand { get; }

    public RelayCommand DeleteSelectedCommand { get; }

    public RelayCommand AbrirPapeleraPropiaCommand { get; }

    public string FolderPath
    {
        get => _folderPath;
        private set
        {
            if (SetProperty(ref _folderPath, value))
            {
                OnPropertyChanged(nameof(HasFolder));
                RefreshCommands();
            }
        }
    }

    public bool HasFolder => !string.IsNullOrWhiteSpace(FolderPath);

    public bool CompareByteByByte
    {
        get => _compareByteByByte;
        set => SetProperty(ref _compareByteByByte, value);
    }

    public bool Verbose
    {
        get => _verbose;
        set => SetProperty(ref _verbose, value);
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

    public string Activity
    {
        get => _activity;
        private set => SetProperty(ref _activity, value);
    }

    private void ReportActivity(string text)
    {
        if (_dispatcher.CheckAccess())
        {
            Activity = text;
        }
        else
        {
            _activity = text;
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
            var estado = string.IsNullOrWhiteSpace(Activity) ? "Sin novedades" : Activity;
            return estado;
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

    public bool HasResults
    {
        get => _hasResults;
        private set
        {
            if (SetProperty(ref _hasResults, value))
            {
                RefreshCommands();
            }
        }
    }

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

    public string SelectionSummary => SelectedCount == 0
        ? "Ningún archivo seleccionado"
        : $"{SelectedCount} archivo(s) seleccionado(s) · " +
          $"{FileItemViewModel.FormatSize(SelectedBytes)} a liberar";

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

    public void SelectFolder()
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Selecciona la carpeta donde buscar duplicados",
            Multiselect = false,
        };

        if (dialog.ShowDialog() == true)
        {
            SetFolder(dialog.FolderName);
        }
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
        ResetPhase();
        ClearLog();

        StatusText = "Carpeta lista. Pulsa «Buscar duplicados».";
    }

    public void StartSearch()
    {
        if (IsBusy || !HasFolder)
        {
            return;
        }

        ClearResults();
        ClearLog();
        _isPaused = false;
        IsPaused = false;
        IsBusy = true;
        _operationClock.Restart();
        _heartbeat.Start();
        ProgressValue = double.NaN;
        IsProgressIndeterminate = true;
        ProgressText = "Preparando el escaneo…";
        StatusText = "Buscando duplicados…";
        Activity = "Preparando el escaneo";
        ResetPhase();

        var folder = FolderPath;
        var compareByteByByte = CompareByteByByte;
        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;

        Task.Run(() =>
        {
            var finder = new DuplicateFinder(
                AppendLog,
                QueueProgress,
                () => _isPaused,
                () => token.IsCancellationRequested,
                ReportActivity,
                ReportPhase);

            ScanResult result;
            try
            {
                result = finder.Run(folder, compareByteByByte);
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
        _heartbeat.Stop();
        _operationClock.Stop();
        _progressPostPending = false;
        _pendingProgressText = null;
        IsProgressIndeterminate = false;
        IsBusy = false;
        IsPaused = false;
        _isPaused = false;
        _cancellation?.Dispose();
        _cancellation = null;
        ResetPhase();

        if (result.Cancelled)
        {
            ResetToInitialState();
            return;
        }

        if (!string.IsNullOrEmpty(result.GeneralError))
        {
            ClearResults();
            StatusText = $"No se pudo completar la búsqueda: {result.GeneralError}";
            ProgressText = "Error";
            return;
        }

        HasResults = result.Groups.Count > 0;
        ProgressValue = 100;
        ProgressText = "Listo";

        foreach (var group in result.Groups)
        {
            Groups.Add(new DuplicateGroupViewModel(group, _nextGroupIndex++, OnItemSelectionChanged));
        }

        if (Groups.Count == 0)
        {
            StatusText = result.Errors > 0
                ? "No se encontraron duplicados entre los archivos que se pudieron leer."
                : "No se encontraron archivos duplicados.";
        }
        else
        {
            var recoverable = Groups.Sum(g => g.Size * (g.Files.Count - 1));
            StatusText =
                $"{Groups.Count} grupo(s) de duplicados · " +
                $"{FileItemViewModel.FormatSize(recoverable)} recuperables" +
                (result.Errors > 0 ? $" · {result.Errors} archivo(s) con error" : string.Empty);
        }
    }

    public void TogglePause()
    {
        if (!IsBusy)
        {
            return;
        }

        _isPaused = !_isPaused;
        IsPaused = _isPaused;
        ProgressText = _isPaused ? "En pausa" : "Reanudando…";
        AppendLog(_isPaused ? "Pausado por el usuario" : "Reanudado por el usuario");
    }

    public void Cancel()
    {
        if (!IsBusy)
        {
            return;
        }

        _isPaused = false;
        IsPaused = false;
        _cancellation?.Cancel();
        AppendLog("Cancelación solicitada por el usuario");
        ProgressText = "Cancelando…";
    }

    /// <summary>Marca todas las copias dejando el primer archivo de cada grupo sin marcar.</summary>
    public void SelectAllCopies()
    {
        _suppressSelectionNotifications = true;
        try
        {
            foreach (var group in Groups)
            {
                for (var i = 0; i < group.Files.Count; i++)
                {
                    group.Files[i].IsSelected = i > 0;
                }
            }
        }
        finally
        {
            _suppressSelectionNotifications = false;
            NotifySelectionTotalsChanged();
        }
    }

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
            NotifySelectionTotalsChanged();
        }
    }

    /// <summary>Abre en el Explorador la carpeta de la papelera propia.</summary>
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

    /// <summary>
    /// Actualiza los totales de selección sin recorrer la lista. Cuando se ejecuta
    /// dentro de una operación masiva, acumula en silencio y notifica una sola vez al final.
    /// </summary>
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

        // Si en un grupo están marcadas TODAS las copias, se conserva la primera: enviar el
        // grupo entero dejaría el último archivo sin hermano con el que revalidarse y se
        // reportaría un error que el usuario no puede interpretar. Además, quedarse sin
        // ninguna copia de un grupo de duplicados nunca es lo que se pretende.
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

        // El aviso debe describir cómo se formaron REALMENTE los grupos que se van a
        // borrar, no la casilla tal como está ahora: cambiarla después de buscar no vuelve a
        // comparar nada, así que guiarse por ella silenciaría justo el aviso que importa.
        var comparaContenido = aEnviar.TrueForAll(item => item.ComparedByteByByte);
        if (ConfirmDelete?.Invoke(new DeletePrompt(aEnviar.Count, comparaContenido, conservar.Count)) != true)
        {
            return;
        }

        var selected = aEnviar.Select(item => item.ToCandidate()).ToList();
        var all = AllItems().Select(item => item.ToCandidate()).ToList();
        _isPaused = false;
        IsPaused = false;
        IsBusy = true;
        _operationClock.Restart();
        _heartbeat.Start();
        ProgressValue = 0;
        IsProgressIndeterminate = false;
        ProgressText = "Guardando en la papelera…";
        StatusText = "Enviando los archivos seleccionados a la papelera…";
        ClearLog();
        Activity = "Iniciando el envío a la papelera";
        ResetPhase();

        _cancellation = new CancellationTokenSource();
        var token = _cancellation.Token;

        Task.Run(() =>
        {
            var deleter = new FileDeleter(
                AppendLog,
                () => _isPaused,
                () => token.IsCancellationRequested,
                recycleDisponible: () => RecycleBin.IsAvailable(FolderPath),
                papeleraPropia: () => new TrashService(RutaPapeleraPropia, DateTime.Now),
                activity: ReportActivity,
                phase: ReportPhase);
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
        _heartbeat.Stop();
        _operationClock.Stop();
        _progressPostPending = false;
        _pendingProgressText = null;
        IsBusy = false;
        IsPaused = false;
        _isPaused = false;
        _cancellation?.Dispose();
        _cancellation = null;
        ResetPhase();

        // Los archivos que ya no están en su ruta original dejan de ser candidatos, se
        // hayan podido verificar en la Papelera o no. Mantenerlos en la lista permitía
        // volver a seleccionarlos y fallaban después por identidad, y dejaba el resumen
        // de bytes liberados contando archivos que ya no ocupan espacio.
        QuitarDeLosResultados(outcome.Movidos);

        var movidos = outcome.Deleted + outcome.SinVerificar;

        if (outcome.Cancelled)
        {
            StatusText = movidos > 0
                ? $"Envío cancelado: {movidos} archivo(s) movido(s) antes de detenerse."
                : "Envío a la papelera cancelado.";
            ProgressText = "Cancelado";
        }
        else if (outcome.Errors > 0)
        {
            StatusText = $"Terminado con {outcome.Errors} error(es) y {movidos} archivo(s) movido(s).";
            ProgressText = "Con avisos";
        }
        else if (outcome.SinVerificar > 0)
        {
            StatusText = $"Terminado: {movidos} archivo(s) movido(s), {outcome.SinVerificar} sin verificar.";
            ProgressText = "Con avisos";
        }
        else
        {
            StatusText = $"Terminado: {movidos} archivo(s) movido(s) a la papelera.";
            ProgressText = "Listo";
        }

        MostrarResultadoDelBorrado(outcome, movidos);

        // Solo se relanza el escaneo si efectivamente se movió algún archivo; de lo
        // contrario se conservan los resultados y el registro para consultar los errores.
        if (movidos > 0)
        {
            StartSearch();
        }
    }

    /// <summary>
    /// Redacta el aviso final separando los tres desenlaces posibles por archivo: no se
    /// pudo mover (sigue donde estaba), se movió sin poder verificar dónde, y se movió y
    /// se confirmó en la papelera. Mezclarlos hacía que el mensaje dijera «no se ha
    /// borrado nada» cuando el archivo ya no estaba en su sitio.
    /// </summary>
    private void MostrarResultadoDelBorrado(DeleteOutcome outcome, int movidos)
    {
        var partes = new List<string>(3);

        if (outcome.Errors > 0)
        {
            partes.Add(
                $"{outcome.Errors} archivo(s) no se pudieron mover porque cambiaron o no se " +
                "pudieron procesar. Siguen en su sitio.");
        }

        if (outcome.SinVerificar > 0)
        {
            partes.Add(
                $"{outcome.SinVerificar} archivo(s) ya no están en su carpeta original, pero no " +
                "se pudo confirmar que estén en la Papelera de reciclaje. Compruébalo en la " +
                "Papelera antes de darlos por perdidos.");
        }

        if (movidos > 0)
        {
            // Se distingue lo que se pudo confirmar en la papelera de lo que solo desapareció:
            // prometer «puedes restaurarlos» de un archivo sin verificar sería mentir.
            var verificados = outcome.Destino == DeleteDestination.PapeleraDeLaApp
                ? movidos
                : outcome.Deleted;

            partes.Add(outcome.Destino == DeleteDestination.PapeleraDeLaApp
                ? $"Se guardaron {verificados} archivo(s) en la papelera propia de la aplicación:\n\n" +
                  $"{outcome.CarpetaPapeleraPropia}\n\n" +
                  "Cada archivo conserva su ruta original en manifiesto.txt, así que puedes " +
                  "devolverlos a su sitio si te arrepientes."
                : $"Se enviaron {verificados} archivo(s) a la Papelera de reciclaje. Si te " +
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

    /// <summary>
    /// Quita de los resultados los archivos que ya no existen y descarta los grupos que se
    /// quedan sin archivos. Sin esto, la lista seguía mostrando archivos ya movidos a la
    /// papelera y los totales de selección no coincidían con lo que se ve.
    /// </summary>
    private void QuitarDeLosResultados(IReadOnlyCollection<string> rutas)
    {
        if (rutas.Count == 0)
        {
            return;
        }

        var movidas = new HashSet<string>(rutas, StringComparer.OrdinalIgnoreCase);

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

            if (grupo.Files.Count == 0)
            {
                Groups.RemoveAt(i);
            }
        }

        RecalcularSeleccion();
        HasResults = Groups.Count > 0;
    }

    /// <summary>Recuenta la selección tras una operación que haya retirado archivos de la lista.</summary>
    private void RecalcularSeleccion()
    {
        var seleccionados = AllItems().Where(item => item.IsSelected).ToList();
        _selectedCount = seleccionados.Count;
        _selectedBytes = seleccionados.Sum(item => item.Size);
        NotifySelectionTotalsChanged();
    }

    private IEnumerable<FileItemViewModel> AllItems() => Groups.SelectMany(group => group.Files);

    private void ClearResults()
    {
        // Vaciar la colección es imprescindible: sin esto los grupos de la búsqueda
        // anterior se quedaban en la lista y cada búsqueda acumulaba resultados, mientras
        // los contadores de selección volvían a cero y dejaban de coincidir con las
        // casillas que el usuario seguía viendo marcadas.
        Groups.Clear();
        _nextGroupIndex = 0;
        HasResults = false;
        SelectedCount = 0;
        SelectedBytes = 0;
    }

    private void ResetToInitialState()
    {
        ClearResults();
        ClearLog();
        FolderPath = string.Empty;
        CompareByteByByte = false;
        Verbose = true;
        IsProgressIndeterminate = false;
        ProgressValue = 0;
        ProgressText = "Listo";
        StatusText = "Selecciona una carpeta para empezar.";
    }

    private void ClearLog() => LogCleared?.Invoke(this, EventArgs.Empty);

    public event EventHandler? LogCleared;

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

        var line = new LogLine(
            Clasificar(text),
            $"[{DateTime.Now:HH:mm:ss}] {text}");

        Post(() => LogAppended?.Invoke(this, line));
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
        // Cerrar la ventana mientras una operación está en curso apaga el Dispatcher. Sin
        // esta comprobación, el hilo de fondo intentaba publicar en él y la excepción
        // quedaba sin observar (y la operación seguía corriendo hasta el final).
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
            // El Dispatcher se cerró entre la comprobación y el envío.
        }
        catch (InvalidOperationException)
        {
            // El Dispatcher ya no acepta trabajo.
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
    }
}