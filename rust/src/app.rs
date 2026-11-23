//! Interfaz y orquestación: replica el comportamiento de `MainViewModel` y
//! `MainWindow` del original en C#/WPF, con egui.

use std::any::Any;
use std::collections::{BTreeMap, HashSet};
use std::fs;
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::thread;
use std::time::{Duration, Instant};

use chrono::Local;
use crossbeam_channel::{Receiver, Sender, unbounded};
use eframe::egui::{
    self, Align, Button, Color32, CornerRadius, CursorIcon, Frame, Label, Layout, Margin,
    ProgressBar, RichText, ScrollArea, Sense, Stroke, TextStyle, Ui, UiBuilder, vec2,
};
use rfd::{MessageButtons, MessageDialog, MessageDialogResult, MessageLevel};

use crate::deleter;
use crate::finder::DuplicateFinder;
use crate::model::{
    Control, DeleteCandidate, DeleteDestination, DeleteOutcome, DuplicateGroup, EngineEvent,
    EventSink, FileIdentity, PhaseProgress, ScanResult, format_size,
};
use crate::recycle_bin;
use crate::shell;

const WINDOW_BG: Color32 = Color32::from_rgb(0xF3, 0xF3, 0xF3);
const SURFACE: Color32 = Color32::from_rgb(0xFF, 0xFF, 0xFF);
const SURFACE_ALT: Color32 = Color32::from_rgb(0xF8, 0xF9, 0xFA);
const BORDER: Color32 = Color32::from_rgb(0xDD, 0xE2, 0xE8);
const ACCENT: Color32 = Color32::from_rgb(0x00, 0x67, 0xC0);
const DANGER: Color32 = Color32::from_rgb(0xC4, 0x2B, 0x1C);
const DANGER_DISABLED: Color32 = Color32::from_rgb(0xE7, 0xD3, 0xD1);
const TEXT_PRIMARY: Color32 = Color32::from_rgb(0x1B, 0x1B, 0x1B);
const TEXT_SECONDARY: Color32 = Color32::from_rgb(0x56, 0x63, 0x6F);
const TEXT_MUTED: Color32 = Color32::from_rgb(0x75, 0x7F, 0x8A);
const LOG_WARNING: Color32 = Color32::from_rgb(0x9A, 0x5B, 0x00);

const ROW_HEIGHT: f32 = 44.0;
const MAX_LOG_CHARS: usize = 512 * 1024;
const CRITERION_TEXT: &str = "Criterio: mismo tamaño y misma extensión, también en todas las \
                              subcarpetas. Solo esas coincidencias se comparan después byte a byte.";

#[derive(Clone, Copy, PartialEq, Eq)]
enum LogSeverity {
    Normal,
    Warning,
    Error,
}

struct LogLine {
    severity: LogSeverity,
    text: String,
}

struct FileView {
    path: PathBuf,
    size: u64,
    identity: Option<FileIdentity>,
    selected: bool,
}

struct GroupView {
    /// Índice estable del grupo; no cambia aunque otros grupos desaparezcan.
    index: usize,
    size: u64,
    size_text: String,
    header: String,
    summary: String,
    files: Vec<FileView>,
}

impl GroupView {
    fn new(index: usize, group: DuplicateGroup) -> Self {
        let size_text = format_size(group.size);
        let mut view = Self {
            index,
            size: group.size,
            size_text,
            header: format!("Grupo de duplicados {}: {}", index + 1, group.display_name),
            summary: String::new(),
            files: group
                .files
                .into_iter()
                .map(|entry| FileView {
                    path: entry.path,
                    size: entry.size,
                    identity: entry.identity,
                    selected: false,
                })
                .collect(),
        };
        view.recalculate_summary();
        view
    }

    fn recalculate_summary(&mut self) {
        let recoverable = if self.files.len() > 1 {
            self.size * (self.files.len() as u64 - 1)
        } else {
            0
        };
        self.summary = format!(
            "{} archivo(s) · {} cada uno · espacio recuperable {}",
            self.files.len(),
            self.size_text,
            format_size(recoverable)
        );
    }

    fn to_candidates(&self) -> Vec<DeleteCandidate> {
        self.files
            .iter()
            .map(|file| DeleteCandidate {
                path: file.path.clone(),
                size: file.size,
                identity: file.identity,
                group_index: self.index,
            })
            .collect()
    }
}

enum Row {
    Header(usize),
    File(usize, usize),
}

enum UiMsg {
    Log(String),
    Progress { percentage: f64, text: String },
    Activity(String),
    Phase(PhaseProgress),
    SearchDone(ScanResult),
    DeleteDone(DeleteOutcome),
}

enum RowAction {
    Toggle(usize, usize, bool),
    Open(usize, usize),
}

/// Reproduce el suavizado de la barra de fase del ViewModel original: cada acción
/// permanece visible un mínimo de 250 ms y el valor avanza hacia el objetivo.
struct PhaseSmoother {
    queue: std::collections::VecDeque<PhaseProgress>,
    live: Option<PhaseProgress>,
    last_action: String,
    current: Option<PhaseProgress>,
    shown_at: Instant,
    value: f64,
    last_tick: Instant,
}

impl PhaseSmoother {
    fn new() -> Self {
        Self {
            queue: std::collections::VecDeque::new(),
            live: None,
            last_action: "Sin actividad".to_string(),
            current: None,
            shown_at: Instant::now(),
            value: 0.0,
            last_tick: Instant::now(),
        }
    }

    fn push(&mut self, phase: PhaseProgress) {
        self.live = Some(phase.clone());
        let action = action_key(&phase.phase);
        if action == self.last_action {
            return;
        }
        self.last_action = action;
        self.queue.push_back(phase);
    }

    fn tick(&mut self) {
        if self.last_tick.elapsed() < Duration::from_millis(50) {
            return;
        }
        self.last_tick = Instant::now();

        if let Some(current) = self.current.clone()
            && self.shown_at.elapsed() < Duration::from_millis(250)
        {
            self.animate_toward(&current);
            return;
        }

        if let (Some(current), Some(live)) = (self.current.clone(), self.live.clone())
            && action_key(&live.phase) != action_key(&current.phase)
            && !current.indeterminate
            && self.value < 100.0
        {
            self.value = (self.value + (100.0 - self.value).max(4.0) * 0.35).min(100.0);
            self.animate_toward(&current);
            return;
        }

        if let Some(next) = self.queue.pop_front() {
            self.current = Some(next.clone());
            self.shown_at = Instant::now();
            self.value = next.percentage;
            return;
        }

        if let Some(live) = self.live.clone() {
            self.current = Some(live.clone());
            self.animate_toward(&live);
        }
    }

    fn animate_toward(&mut self, shown: &PhaseProgress) {
        if shown.indeterminate {
            return;
        }

        let live_is_same = self
            .live
            .as_ref()
            .is_some_and(|live| action_key(&live.phase) == action_key(&shown.phase));
        let target = if live_is_same {
            self.live
                .as_ref()
                .map(|live| live.percentage)
                .unwrap_or(100.0)
        } else {
            100.0
        };

        let difference = target - self.value;
        if difference <= 0.5 {
            self.value = target;
        } else {
            self.value += (difference * 0.25).max(2.0);
        }
    }

    fn reset(&mut self) {
        self.queue.clear();
        self.live = None;
        self.last_action = "Sin actividad".to_string();
        self.current = None;
        self.value = 0.0;
        self.shown_at = Instant::now();
        self.last_tick = Instant::now();
    }

    fn label(&self) -> &str {
        self.current
            .as_ref()
            .map(|phase| phase.phase.as_str())
            .unwrap_or("Sin actividad")
    }

    fn indeterminate(&self) -> bool {
        self.current
            .as_ref()
            .map(|phase| phase.indeterminate)
            .unwrap_or(false)
    }
}

fn action_key(phase: &str) -> String {
    match phase.find('(') {
        Some(index) if index > 0 => phase[..index].trim().to_string(),
        _ => phase.trim().to_string(),
    }
}

pub struct DuplicadosApp {
    folder_path: Option<PathBuf>,
    verbose: bool,
    busy: bool,
    paused: bool,
    activity: String,
    progress_value: f64,
    progress_indeterminate: bool,
    progress_text: String,
    status_text: String,
    groups: Vec<GroupView>,
    rows: Vec<Row>,
    selected_count: usize,
    selected_bytes: u64,
    log: Vec<LogLine>,
    log_chars: usize,
    phase: PhaseSmoother,
    control: Control,
    tx: Sender<UiMsg>,
    rx: Receiver<UiMsg>,
    operation_start: Option<Instant>,
    heartbeat_at: Instant,
}

impl DuplicadosApp {
    pub fn new(creation: &eframe::CreationContext<'_>) -> Self {
        configure_style(&creation.egui_ctx);
        Self::from_scratch()
    }

    fn from_scratch() -> Self {
        let (tx, rx) = unbounded();

        Self {
            folder_path: None,
            verbose: true,
            busy: false,
            paused: false,
            activity: String::new(),
            progress_value: 0.0,
            progress_indeterminate: false,
            progress_text: "Listo".to_string(),
            status_text: "Selecciona una carpeta para empezar.".to_string(),
            groups: Vec::new(),
            rows: Vec::new(),
            selected_count: 0,
            selected_bytes: 0,
            log: Vec::new(),
            log_chars: 0,
            phase: PhaseSmoother::new(),
            control: Control::new(),
            tx,
            rx,
            operation_start: None,
            heartbeat_at: Instant::now(),
        }
    }

    fn has_folder(&self) -> bool {
        self.folder_path.is_some()
    }

    fn has_results(&self) -> bool {
        !self.groups.is_empty()
    }

    fn folder_display(&self) -> String {
        self.folder_path
            .as_ref()
            .map(|path| path.display().to_string())
            .unwrap_or_default()
    }

    fn selection_summary(&self) -> String {
        if self.selected_count == 0 {
            "Ningún archivo seleccionado".to_string()
        } else {
            format!(
                "{} archivo(s) seleccionado(s) · {} a liberar",
                self.selected_count,
                format_size(self.selected_bytes)
            )
        }
    }

    fn phase_summary(&self) -> String {
        let label = self.phase.label();
        if label.is_empty() || label == "Sin actividad" {
            if self.activity.trim().is_empty() {
                "Sin novedades".to_string()
            } else {
                self.activity.clone()
            }
        } else if self.phase.indeterminate() {
            label.to_string()
        } else {
            format!("{label} {:.0}%", self.phase.value)
        }
    }

    fn append_log(&mut self, text: &str) {
        if !self.verbose {
            return;
        }

        let severity = if text.starts_with('✖') {
            LogSeverity::Error
        } else if text.starts_with('⚠') {
            LogSeverity::Warning
        } else {
            LogSeverity::Normal
        };
        let stamp = Local::now().format("%H:%M:%S");
        let text = format!("[{stamp}] {text}");
        self.log_chars += text.chars().count() + 1;
        self.log.push(LogLine { severity, text });
        self.trim_log();
    }

    fn trim_log(&mut self) {
        if self.log_chars <= MAX_LOG_CHARS {
            return;
        }

        let remove = (self.log.len() / 2).max(1);
        for line in self.log.drain(0..remove.min(self.log.len())) {
            self.log_chars = self.log_chars.saturating_sub(line.text.chars().count() + 1);
        }
    }

    fn clear_log(&mut self) {
        self.log.clear();
        self.log_chars = 0;
    }

    fn clear_results(&mut self) {
        self.groups.clear();
        self.rows.clear();
        self.selected_count = 0;
        self.selected_bytes = 0;
    }

    fn rebuild_rows(&mut self) {
        self.rows.clear();
        for (group_index, group) in self.groups.iter().enumerate() {
            self.rows.push(Row::Header(group_index));
            for file_index in 0..group.files.len() {
                self.rows.push(Row::File(group_index, file_index));
            }
        }
    }

    fn recalculate_selection(&mut self) {
        self.selected_count = 0;
        self.selected_bytes = 0;
        for group in &self.groups {
            for file in &group.files {
                if file.selected {
                    self.selected_count += 1;
                    self.selected_bytes += file.size;
                }
            }
        }
    }

    fn reconfigure_control(&mut self) {
        self.control = Control::new();
    }

    fn request_repaint(&self, ctx: &egui::Context) {
        if self.busy {
            ctx.request_repaint_after(Duration::from_millis(100));
        }
    }

    fn drain_messages(&mut self) {
        while let Ok(message) = self.rx.try_recv() {
            match message {
                UiMsg::Log(text) => self.append_log(&text),
                UiMsg::Progress { percentage, text } => {
                    self.progress_value = percentage;
                    self.progress_text = text;
                    self.progress_indeterminate = false;
                }
                UiMsg::Activity(text) => self.activity = text,
                UiMsg::Phase(phase) => self.phase.push(phase),
                UiMsg::SearchDone(result) => self.finish_search(result),
                UiMsg::DeleteDone(outcome) => self.finish_delete(outcome),
            }
        }
    }

    fn event_sink(&self) -> EventSink {
        let tx = self.tx.clone();
        Arc::new(move |event| {
            let message = match event {
                EngineEvent::Log(text) => UiMsg::Log(text),
                EngineEvent::Progress(progress) => UiMsg::Progress {
                    percentage: progress.percentage,
                    text: progress.text,
                },
                EngineEvent::Activity(text) => UiMsg::Activity(text),
                EngineEvent::Phase(phase) => UiMsg::Phase(phase),
            };
            let _ = tx.send(message);
        })
    }

    fn set_folder(&mut self, path: PathBuf) {
        if self.busy {
            return;
        }

        self.folder_path = Some(path);
        self.clear_results();
        self.progress_value = 0.0;
        self.progress_indeterminate = false;
        self.progress_text = "Listo".to_string();
        self.activity.clear();
        self.phase.reset();
        self.clear_log();
        self.status_text = "Carpeta lista. Pulsa «Buscar duplicados».".to_string();
    }

    fn select_folder(&mut self) {
        if self.busy {
            return;
        }

        if let Some(path) = rfd::FileDialog::new()
            .set_title("Selecciona la carpeta donde buscar duplicados")
            .pick_folder()
        {
            self.set_folder(path);
        }
    }

    fn start_search(&mut self) {
        if self.busy {
            return;
        }
        let Some(folder) = self.folder_path.clone() else {
            return;
        };

        self.clear_results();
        self.clear_log();
        self.busy = true;
        self.paused = false;
        self.reconfigure_control();
        self.operation_start = Some(Instant::now());
        self.heartbeat_at = Instant::now();
        self.progress_value = 0.0;
        self.progress_indeterminate = true;
        self.progress_text = "Preparando el escaneo…".to_string();
        self.status_text = "Buscando duplicados…".to_string();
        self.activity = "Preparando el escaneo".to_string();
        self.phase.reset();

        let events = self.event_sink();
        let control = self.control.clone();
        let tx = self.tx.clone();
        thread::spawn(move || {
            let finder = DuplicateFinder::new(events, control);
            let result = catch_unwind(AssertUnwindSafe(|| finder.run(&folder)));
            let result = match result {
                Ok(result) => result,
                Err(payload) => {
                    let _ = tx.send(UiMsg::Log(format!(
                        "✖ ERROR inesperado: {}",
                        panic_text(payload)
                    )));
                    ScanResult {
                        general_error: Some(
                            "Error inesperado en el motor de búsqueda.".to_string(),
                        ),
                        ..Default::default()
                    }
                }
            };
            let _ = tx.send(UiMsg::SearchDone(result));
        });
    }

    fn finish_search(&mut self, result: ScanResult) {
        self.busy = false;
        self.paused = false;
        self.operation_start = None;
        self.phase.reset();
        self.progress_indeterminate = false;

        if result.cancelled {
            self.reset_to_initial_state();
            return;
        }

        if let Some(error) = result.general_error {
            self.clear_results();
            self.status_text = format!("No se pudo completar la búsqueda: {error}");
            self.progress_text = "Error".to_string();
            return;
        }

        self.progress_value = 100.0;
        self.progress_text = "Listo".to_string();
        self.groups = result
            .groups
            .into_iter()
            .enumerate()
            .map(|(index, group)| GroupView::new(index, group))
            .collect();
        self.rebuild_rows();

        if self.groups.is_empty() {
            self.status_text = if result.errors > 0 {
                "No se encontraron duplicados entre los archivos que se pudieron leer.".to_string()
            } else {
                "No se encontraron archivos duplicados.".to_string()
            };
        } else {
            let recoverable: u64 = self
                .groups
                .iter()
                .map(|group| group.size * (group.files.len() as u64).saturating_sub(1))
                .sum();
            let mut status = format!(
                "{} grupo(s) de duplicados · {} recuperables",
                self.groups.len(),
                format_size(recoverable)
            );
            if result.errors > 0 {
                status.push_str(&format!(" · {} archivo(s) con error", result.errors));
            }
            self.status_text = status;
        }
    }

    fn reset_to_initial_state(&mut self) {
        self.clear_results();
        self.clear_log();
        self.folder_path = None;
        self.verbose = true;
        self.progress_indeterminate = false;
        self.progress_value = 0.0;
        self.progress_text = "Listo".to_string();
        self.status_text = "Selecciona una carpeta para empezar.".to_string();
        self.activity.clear();
        self.phase.reset();
    }

    fn toggle_pause(&mut self) {
        if !self.busy {
            return;
        }

        self.paused = !self.paused;
        self.control.set_paused(self.paused);
        self.progress_text = if self.paused {
            "En pausa".to_string()
        } else {
            "Reanudando…".to_string()
        };
        self.append_log(if self.paused {
            "Pausado por el usuario"
        } else {
            "Reanudado por el usuario"
        });
    }

    fn cancel(&mut self) {
        if !self.busy {
            return;
        }

        self.paused = false;
        self.control.cancel();
        self.append_log("Cancelación solicitada por el usuario");
        self.progress_text = "Cancelando…".to_string();
    }

    fn select_all_copies(&mut self) {
        if self.busy || !self.has_results() {
            return;
        }

        for group in &mut self.groups {
            for (index, file) in group.files.iter_mut().enumerate() {
                file.selected = index > 0;
            }
        }
        self.recalculate_selection();
    }

    fn clear_selection(&mut self) {
        if self.busy || !self.has_results() {
            return;
        }

        for group in &mut self.groups {
            for file in &mut group.files {
                file.selected = false;
            }
        }
        self.selected_count = 0;
        self.selected_bytes = 0;
    }

    fn delete_selected(&mut self) {
        if self.busy {
            return;
        }

        let mut marked: Vec<(usize, usize)> = Vec::new();
        for (group_index, group) in self.groups.iter().enumerate() {
            for (file_index, file) in group.files.iter().enumerate() {
                if file.selected {
                    marked.push((group_index, file_index));
                }
            }
        }

        if marked.is_empty() {
            self.show_message(
                "Aviso",
                "No has seleccionado ningún archivo para eliminar.",
                MessageLevel::Info,
            );
            return;
        }

        // Si en un grupo están marcadas TODAS las copias, se conserva la primera:
        // enviar el grupo entero dejaría el último archivo sin hermano con el que
        // revalidarse y se perdería la última copia buena.
        let mut keep: HashSet<(usize, usize)> = HashSet::new();
        for (group_index, group) in self.groups.iter().enumerate() {
            if !group.files.is_empty() && group.files.iter().all(|file| file.selected) {
                keep.insert((group_index, 0));
            }
        }
        let to_send: Vec<(usize, usize)> = marked
            .into_iter()
            .filter(|position| !keep.contains(position))
            .collect();

        if to_send.is_empty() {
            self.show_message(
                "Aviso",
                "No hay nada que enviar: de cada grupo marcado se conserva al menos una copia.",
                MessageLevel::Info,
            );
            return;
        }

        if !self.confirm_delete(to_send.len(), keep.len()) {
            return;
        }

        let selected: Vec<DeleteCandidate> = to_send
            .iter()
            .map(|(group_index, file_index)| {
                let group = &self.groups[*group_index];
                let file = &group.files[*file_index];
                DeleteCandidate {
                    path: file.path.clone(),
                    size: file.size,
                    identity: file.identity,
                    group_index: group.index,
                }
            })
            .collect();
        let all: Vec<DeleteCandidate> = self
            .groups
            .iter()
            .flat_map(GroupView::to_candidates)
            .collect();
        let total = selected.len();

        self.busy = true;
        self.paused = false;
        self.reconfigure_control();
        self.operation_start = Some(Instant::now());
        self.heartbeat_at = Instant::now();
        self.progress_value = 0.0;
        self.progress_indeterminate = false;
        self.progress_text = "Guardando en la papelera…".to_string();
        self.status_text = "Enviando los archivos seleccionados a la papelera…".to_string();
        self.clear_log();
        self.activity = "Iniciando el envío a la papelera".to_string();
        self.phase.reset();

        let events = self.event_sink();
        let control = self.control.clone();
        let tx = self.tx.clone();
        let folder = self.folder_path.clone().unwrap_or_default();
        let trash_root = app_trash_root();

        thread::spawn(move || {
            let outcome = catch_unwind(AssertUnwindSafe(|| {
                let recycle_available = recycle_bin::is_available(Some(&folder));
                deleter::delete(
                    &events,
                    &control,
                    &selected,
                    &all,
                    recycle_available,
                    &trash_root,
                )
            }));

            let outcome = match outcome {
                Ok(outcome) => outcome,
                Err(payload) => {
                    let _ = tx.send(UiMsg::Log(format!(
                        "✖ ERROR inesperado: {}",
                        panic_text(payload)
                    )));
                    DeleteOutcome {
                        errors: total,
                        ..Default::default()
                    }
                }
            };
            let _ = tx.send(UiMsg::DeleteDone(outcome));
        });
    }

    fn finish_delete(&mut self, outcome: DeleteOutcome) {
        self.busy = false;
        self.paused = false;
        self.control.set_paused(false);
        self.operation_start = None;
        self.phase.reset();

        self.remove_moved(&outcome.moved);
        let moved = outcome.deleted + outcome.unverified;

        if outcome.cancelled {
            self.status_text = if moved > 0 {
                format!("Envío cancelado: {moved} archivo(s) movido(s) antes de detenerse.")
            } else {
                "Envío a la papelera cancelado.".to_string()
            };
            self.progress_text = "Cancelado".to_string();
        } else if outcome.errors > 0 {
            self.status_text = format!(
                "Terminado con {} error(es) y {moved} archivo(s) movido(s).",
                outcome.errors
            );
            self.progress_text = "Con avisos".to_string();
        } else if outcome.unverified > 0 {
            self.status_text = format!(
                "Terminado: {moved} archivo(s) movido(s), {} sin verificar.",
                outcome.unverified
            );
            self.progress_text = "Con avisos".to_string();
        } else {
            self.status_text = format!("Terminado: {moved} archivo(s) movido(s) a la papelera.");
            self.progress_text = "Listo".to_string();
        }

        self.show_delete_result(&outcome, moved);

        // Solo se relanza el escaneo si efectivamente se movió algún archivo.
        if moved > 0 {
            self.start_search();
        }
    }

    fn remove_moved(&mut self, paths: &[PathBuf]) {
        if paths.is_empty() {
            return;
        }

        let moved: HashSet<String> = paths
            .iter()
            .map(|path| path.to_string_lossy().to_lowercase())
            .collect();

        for group in &mut self.groups {
            group
                .files
                .retain(|file| !moved.contains(&file.path.to_string_lossy().to_lowercase()));
            group.recalculate_summary();
        }
        self.groups.retain(|group| !group.files.is_empty());
        self.rebuild_rows();
        self.recalculate_selection();
    }

    fn show_delete_result(&self, outcome: &DeleteOutcome, moved: usize) {
        let mut parts = Vec::new();

        if outcome.errors > 0 {
            parts.push(format!(
                "{} archivo(s) no se pudieron mover porque cambiaron o no se pudieron \
                 procesar. Siguen en su sitio.",
                outcome.errors
            ));
        }

        if outcome.unverified > 0 {
            parts.push(format!(
                "{} archivo(s) ya no están en su carpeta original, pero no se pudo confirmar \
                 que estén en la Papelera de reciclaje. Compruébalo en la Papelera antes de \
                 darlos por perdidos.",
                outcome.unverified
            ));
        }

        if moved > 0 {
            let verified = if outcome.destination == DeleteDestination::AppTrash {
                moved
            } else {
                outcome.deleted
            };
            parts.push(if outcome.destination == DeleteDestination::AppTrash {
                format!(
                    "Se guardaron {verified} archivo(s) en la papelera propia de la aplicación:\n\n{}\n\n\
                     Cada archivo conserva su ruta original en manifiesto.txt, así que puedes \
                     devolverlos a su sitio si te arrepientes.",
                    outcome
                        .app_trash_folder
                        .as_ref()
                        .map(|folder| folder.display().to_string())
                        .unwrap_or_default()
                )
            } else {
                format!(
                    "Se enviaron {verified} archivo(s) a la Papelera de reciclaje. Si te \
                     arrepientes, puedes restaurarlos desde la Papelera."
                )
            });
        }

        if parts.is_empty() {
            return;
        }

        let title = if outcome.errors > 0 || outcome.unverified > 0 {
            "Aviso"
        } else {
            "Listo"
        };
        let mut message = parts.join("\n\n");
        let folder = if outcome.destination == DeleteDestination::AppTrash {
            outcome.app_trash_folder.clone()
        } else {
            None
        };
        if folder.is_some() {
            message.push_str("\n\nSe abrirá esa carpeta al cerrar este aviso.");
        }

        self.show_message(title, &message, MessageLevel::Info);

        if let Some(folder) = folder {
            self.open_folder_if_has_content(&folder);
        }
    }

    fn open_folder_if_has_content(&self, folder: &Path) {
        if !folder.is_dir() {
            return;
        }

        let has_content = fs::read_dir(folder)
            .map(|mut entries| entries.next().is_some())
            .unwrap_or(false);
        if has_content {
            let _ = shell::open_path(folder);
        }
    }

    fn open_file(&self, group_index: usize, file_index: usize) {
        let Some(file) = self
            .groups
            .get(group_index)
            .and_then(|group| group.files.get(file_index))
        else {
            return;
        };

        if !file.path.is_file() {
            self.show_message(
                "Aviso",
                &format!(
                    "El archivo ya no está en su ruta:\n\n{}\n\nEs posible que se haya movido \
                     o enviado a la papelera desde otra ventana.",
                    file.path.display()
                ),
                MessageLevel::Info,
            );
            return;
        }

        if let Err(error) = shell::open_path(&file.path) {
            self.show_message(
                "Aviso",
                &format!(
                    "No se pudo abrir el archivo:\n\n{}\n\n{error}",
                    file.path.display()
                ),
                MessageLevel::Warning,
            );
        }
    }

    fn open_app_trash(&self) {
        let root = app_trash_root();
        if let Err(error) = fs::create_dir_all(&root) {
            self.show_message(
                "Aviso",
                &format!("No se pudo abrir la carpeta: {error}"),
                MessageLevel::Warning,
            );
            return;
        }

        if let Err(error) = shell::open_path(&root) {
            self.show_message(
                "Aviso",
                &format!("No se pudo abrir la carpeta:\n\n{error}"),
                MessageLevel::Warning,
            );
        }
    }

    fn confirm_delete(&self, count: usize, groups_kept: usize) -> bool {
        let mut parts = vec![format!(
            "Se enviarán {count} archivo(s) a la papelera, donde podrás restaurarlos si te \
             arrepientes."
        )];
        if groups_kept > 0 {
            parts.push(format!(
                "En {groups_kept} grupo(s) has marcado todas las copias: se conservará la \
                 primera de cada uno para no quedarte sin ninguna."
            ));
        }
        parts.push("¿Continuar?".to_string());

        MessageDialog::new()
            .set_title("Confirmar envío a la papelera")
            .set_description(parts.join("\n\n"))
            .set_buttons(MessageButtons::YesNo)
            .set_level(MessageLevel::Warning)
            .show()
            == MessageDialogResult::Yes
    }

    fn show_message(&self, title: &str, message: &str, level: MessageLevel) {
        MessageDialog::new()
            .set_title(title)
            .set_description(message)
            .set_buttons(MessageButtons::Ok)
            .set_level(level)
            .show();
    }

    fn heartbeat(&mut self) {
        if !self.busy {
            return;
        }
        let Some(start) = self.operation_start else {
            return;
        };

        let estado = if self.activity.trim().is_empty() {
            "Sin novedades".to_string()
        } else {
            self.activity.clone()
        };
        let avance = self.phase_summary();
        let seconds = start.elapsed().as_secs_f64();
        let text = if self.paused {
            format!("…en pausa — {avance} · {estado} ({seconds:.1}s)")
        } else {
            format!("…{avance} · {estado} ({seconds:.1}s)")
        };
        self.append_log(&text);
    }

    fn apply_row_action(&mut self, action: RowAction) {
        match action {
            RowAction::Toggle(group_index, file_index, selected) => {
                if let Some(group) = self.groups.get_mut(group_index)
                    && let Some(file) = group.files.get_mut(file_index)
                    && file.selected != selected
                {
                    file.selected = selected;
                    if selected {
                        self.selected_count += 1;
                        self.selected_bytes += file.size;
                    } else {
                        self.selected_count = self.selected_count.saturating_sub(1);
                        self.selected_bytes = self.selected_bytes.saturating_sub(file.size);
                    }
                }
            }
            RowAction::Open(group_index, file_index) => {
                self.open_file(group_index, file_index);
            }
        }
    }

    fn ui_header(&mut self, ui: &mut Ui) {
        Frame::new()
            .fill(SURFACE)
            .stroke(Stroke::new(1.0, BORDER))
            .corner_radius(CornerRadius::same(8))
            .inner_margin(Margin::symmetric(14, 12))
            .show(ui, |ui| {
                ui.set_min_width(ui.available_width());
                ui.horizontal(|ui| {
                    let select = Button::new(RichText::new("Seleccionar carpeta").size(13.0));
                    if ui
                        .add_enabled(!self.busy, select)
                        .on_hover_text(
                            "Abre un diálogo para elegir la carpeta que se analizará en busca \
                             de archivos duplicados.",
                        )
                        .clicked()
                    {
                        self.select_folder();
                    }

                    if self.has_folder() {
                        let folder = self.folder_display();
                        ui.add(
                            Label::new(RichText::new(&folder).color(ACCENT))
                                .truncate()
                                .sense(Sense::hover()),
                        )
                        .on_hover_text(folder);
                    }
                });

                ui.add_space(10.0);
                ui.horizontal(|ui| {
                    ui.checkbox(&mut self.verbose, "Modo detallado")
                        .on_hover_text(
                            "Al marcar, escribe información detallada del proceso y el latido de \
                         estado en el registro de actividad. Al desmarcar, pausa la escritura \
                         de nuevas líneas en el registro.",
                        );
                    ui.add_space(10.0);
                    ui.add(
                        Label::new(RichText::new(CRITERION_TEXT).small().color(TEXT_MUTED)).wrap(),
                    )
                    .on_hover_text(
                        "Los nombres no tienen por qué coincidir: dos copias pueden llamarse \
                         distinto. El contenido solo se compara cuando el tamaño y la extensión \
                         coinciden.",
                    );
                });
            });

        ui.add_space(10.0);
        ui.horizontal(|ui| {
            let width = (ui.available_width() - 282.0).max(120.0);
            let bar = ProgressBar::new(if self.progress_indeterminate {
                0.0
            } else {
                (self.progress_value / 100.0).clamp(0.0, 1.0) as f32
            })
            .desired_width(width)
            .desired_height(8.0)
            .fill(ACCENT)
            .animate(self.progress_indeterminate);
            ui.add(bar);
            ui.add_space(12.0);
            ui.add(
                Label::new(
                    RichText::new(&self.progress_text)
                        .small()
                        .color(TEXT_SECONDARY),
                )
                .truncate()
                .sense(Sense::hover()),
            )
            .on_hover_text(&self.progress_text);
        });

        ui.add_space(8.0);
        ui.add(
            Label::new(
                RichText::new(&self.status_text)
                    .small()
                    .color(TEXT_SECONDARY),
            )
            .wrap(),
        );
    }

    fn ui_activity(&mut self, ui: &mut Ui) {
        ui.horizontal(|ui| {
            ui.label(
                RichText::new("Actividad")
                    .size(12.0)
                    .strong()
                    .color(TEXT_SECONDARY),
            );
        });
        ui.add_space(4.0);

        let indeterminate = self.phase.indeterminate();
        let phase_value = if indeterminate {
            0.0
        } else {
            (self.phase.value / 100.0).clamp(0.0, 1.0) as f32
        };
        ui.horizontal(|ui| {
            let width = (ui.available_width() - 240.0).max(120.0);
            ui.add(
                ProgressBar::new(phase_value)
                    .desired_width(width)
                    .desired_height(6.0)
                    .fill(ACCENT)
                    .animate(indeterminate),
            );
            ui.add_space(10.0);
            let label = self.phase.label().to_string();
            ui.add(
                Label::new(RichText::new(&label).small().color(TEXT_MUTED))
                    .truncate()
                    .sense(Sense::hover()),
            )
            .on_hover_text(&label);
        });

        ui.add_space(6.0);
        Frame::new()
            .fill(SURFACE_ALT)
            .corner_radius(CornerRadius::same(4))
            .inner_margin(Margin::same(4))
            .show(ui, |ui| {
                ui.allocate_ui_with_layout(
                    vec2(ui.available_width(), 110.0),
                    Layout::top_down(Align::Min),
                    |ui| {
                        ScrollArea::vertical()
                            .stick_to_bottom(true)
                            .max_height(110.0)
                            .auto_shrink([false, false])
                            .show(ui, |ui| {
                                ui.set_min_height(100.0);
                                for line in &self.log {
                                    let color = match line.severity {
                                        LogSeverity::Normal => TEXT_PRIMARY,
                                        LogSeverity::Warning => LOG_WARNING,
                                        LogSeverity::Error => DANGER,
                                    };
                                    ui.add(
                                        Label::new(
                                            RichText::new(&line.text)
                                                .monospace()
                                                .size(11.0)
                                                .color(color),
                                        )
                                        .wrap(),
                                    );
                                }
                            });
                    },
                );
            });
    }

    fn ui_actions(&mut self, ui: &mut Ui) {
        ui.horizontal_wrapped(|ui| {
            if ui
                .add_enabled(
                    !self.busy && self.has_folder(),
                    Button::new(RichText::new("Buscar duplicados").size(13.0)),
                )
                .on_hover_text(
                    "Inicia el análisis de la carpeta seleccionada y muestra los grupos de \
                     archivos duplicados.",
                )
                .clicked()
            {
                self.start_search();
            }

            if ui
                .add_enabled(
                    !self.busy && self.has_results(),
                    Button::new(RichText::new("Seleccionar copias (automático)").size(13.0)),
                )
                .on_hover_text(
                    "Marca automáticamente los archivos sobrantes de cada grupo (conserva una \
                     sola copia) para poder eliminarlos.",
                )
                .clicked()
            {
                self.select_all_copies();
            }

            if ui
                .add_enabled(
                    !self.busy && self.has_results(),
                    Button::new(RichText::new("Quitar selección").size(13.0)),
                )
                .on_hover_text(
                    "Desmarca todos los archivos seleccionados y deja el resumen de selección \
                     a cero.",
                )
                .clicked()
            {
                self.clear_selection();
            }

            let pause_text = if self.paused { "Reanudar" } else { "Pausar" };
            if ui
                .add_enabled(self.busy, Button::new(RichText::new(pause_text).size(13.0)))
                .on_hover_text("Pausa o reanuda el análisis en curso sin perder el progreso.")
                .clicked()
            {
                self.toggle_pause();
            }

            ui.add(Label::new(
                RichText::new(self.selection_summary())
                    .small()
                    .color(TEXT_SECONDARY),
            ));

            if self.busy
                && ui
                    .add(Button::new(RichText::new("Cancelar").size(13.0)))
                    .on_hover_text(
                        "Detiene de inmediato la operación en curso (si es una búsqueda, \
                         devuelve la ventana a su estado inicial; si es un borrado, detiene el \
                         envío a la papelera).",
                    )
                    .clicked()
            {
                self.cancel();
            }

            let enabled = !self.busy && self.selected_count > 0;
            let text_color = if enabled { Color32::WHITE } else { TEXT_MUTED };
            let delete = Button::new(
                RichText::new("Eliminar archivos seleccionados")
                    .color(text_color)
                    .strong(),
            )
            .fill(if enabled { DANGER } else { DANGER_DISABLED })
            .corner_radius(CornerRadius::same(5));
            if ui
                .add_enabled(enabled, delete)
                .on_hover_text(
                    "Envía a la Papelera todos los archivos marcados con la casilla de \
                     selección.",
                )
                .clicked()
            {
                self.delete_selected();
            }
        });
    }

    fn ui_trash(&mut self, ui: &mut Ui) {
        if ui
            .add(
                Button::new(
                    RichText::new("Abrir papelera de la aplicación")
                        .size(12.0)
                        .color(TEXT_SECONDARY),
                )
                .frame(false),
            )
            .on_hover_text(
                "Abre la carpeta donde la aplicación guarda los archivos eliminados cuando la \
                 Papelera de Windows no está disponible.",
            )
            .clicked()
        {
            self.open_app_trash();
        }
    }

    fn ui_results(&mut self, ui: &mut Ui) {
        let available_height = ui.available_height();
        Frame::new()
            .fill(SURFACE)
            .stroke(Stroke::new(1.0, BORDER))
            .corner_radius(CornerRadius::same(8))
            .inner_margin(Margin::same(4))
            .show(ui, |ui| {
                ui.set_min_height((available_height - 10.0).max(0.0));

                if self.rows.is_empty() {
                    ui.centered_and_justified(|ui| {
                        ui.add(
                            Label::new(
                                RichText::new(
                                    "Selecciona una carpeta y pulsa «Buscar duplicados».",
                                )
                                .size(13.0)
                                .color(TEXT_MUTED),
                            )
                            .wrap(),
                        );
                    });
                    return;
                }

                let rows = &self.rows;
                let groups = &self.groups;
                let mut actions = Vec::new();

                ScrollArea::vertical()
                    .auto_shrink([false, false])
                    .show_rows(ui, ROW_HEIGHT, rows.len(), |ui, range| {
                        for row_index in range {
                            match &rows[row_index] {
                                Row::Header(group_index) => {
                                    if let Some(group) = groups.get(*group_index) {
                                        draw_group_header(ui, group);
                                    }
                                }
                                Row::File(group_index, file_index) => {
                                    if let Some(group) = groups.get(*group_index)
                                        && let Some(file) = group.files.get(*file_index)
                                    {
                                        draw_file_row(
                                            ui,
                                            group,
                                            *group_index,
                                            *file_index,
                                            file,
                                            &mut actions,
                                        );
                                    }
                                }
                            }
                        }
                    });

                for action in actions {
                    self.apply_row_action(action);
                }
            });
    }
}

impl eframe::App for DuplicadosApp {
    fn clear_color(&self, _visuals: &egui::Visuals) -> [f32; 4] {
        WINDOW_BG.to_normalized_gamma_f32()
    }

    fn ui(&mut self, ui: &mut Ui, _frame: &mut eframe::Frame) {
        let ctx = ui.ctx().clone();
        if ctx.input(|input| input.viewport().close_requested()) {
            self.control.cancel();
        }

        self.drain_messages();
        self.phase.tick();

        if self.busy && self.heartbeat_at.elapsed() >= Duration::from_secs(2) {
            self.heartbeat();
            self.heartbeat_at = Instant::now();
        }
        self.request_repaint(&ctx);

        let top_frame = Frame::NONE.inner_margin(Margin {
            left: 14,
            right: 14,
            top: 14,
            bottom: 0,
        });
        egui::Panel::top("cabecera")
            .frame(top_frame)
            .show(ui, |ui| self.ui_header(ui));

        let trash_frame = Frame::NONE.inner_margin(Margin {
            left: 14,
            right: 14,
            top: 8,
            bottom: 12,
        });
        egui::Panel::bottom("papelera")
            .frame(trash_frame)
            .show(ui, |ui| self.ui_trash(ui));

        let actions_frame = Frame::NONE.inner_margin(Margin {
            left: 14,
            right: 14,
            top: 8,
            bottom: 0,
        });
        egui::Panel::bottom("acciones")
            .frame(actions_frame)
            .show(ui, |ui| self.ui_actions(ui));

        let activity_frame = Frame::NONE.inner_margin(Margin {
            left: 14,
            right: 14,
            top: 10,
            bottom: 0,
        });
        egui::Panel::bottom("actividad")
            .frame(activity_frame)
            .show(ui, |ui| self.ui_activity(ui));

        let central_frame = Frame::NONE.inner_margin(Margin {
            left: 14,
            right: 14,
            top: 10,
            bottom: 10,
        });
        egui::CentralPanel::default()
            .frame(central_frame)
            .show(ui, |ui| self.ui_results(ui));
    }
}

impl Drop for DuplicadosApp {
    fn drop(&mut self) {
        self.control.cancel();
    }
}

fn draw_group_header(ui: &mut Ui, group: &GroupView) {
    ui.allocate_ui_with_layout(
        vec2(ui.available_width(), ROW_HEIGHT),
        Layout::top_down(Align::Min),
        |ui| {
            ui.spacing_mut().item_spacing.y = 2.0;
            ui.add_space(4.0);
            ui.add(
                Label::new(
                    RichText::new(&group.header)
                        .size(15.0)
                        .strong()
                        .color(TEXT_PRIMARY),
                )
                .truncate(),
            );
            ui.add(
                Label::new(RichText::new(&group.summary).size(11.0).color(TEXT_MUTED)).truncate(),
            );
        },
    );
}

fn draw_file_row(
    ui: &mut Ui,
    group: &GroupView,
    group_index: usize,
    file_index: usize,
    file: &FileView,
    actions: &mut Vec<RowAction>,
) {
    let (rect, _) = ui.allocate_exact_size(vec2(ui.available_width(), ROW_HEIGHT), Sense::hover());
    let card = rect.shrink2(vec2(6.0, 2.0));
    ui.painter().rect_filled(card, 6.0, SURFACE_ALT);
    let inner = card.shrink2(vec2(10.0, 0.0));

    ui.scope_builder(
        UiBuilder::new()
            .max_rect(inner)
            .layout(Layout::left_to_right(Align::Center)),
        |ui| {
            let mut selected = file.selected;
            if ui
                .checkbox(&mut selected, "")
                .on_hover_text(
                    "Al marcar, selecciona este archivo para eliminarlo (se envía a la \
                     Papelera al pulsar «Eliminar archivos seleccionados»). Al desmarcar, el \
                     archivo no se elimina.",
                )
                .changed()
            {
                actions.push(RowAction::Toggle(group_index, file_index, selected));
            }

            ui.add_space(8.0);
            let size_width = 72.0;
            let path_width = (ui.available_width() - size_width).max(60.0);
            let path_text = file.path.display().to_string();
            let response = ui
                .allocate_ui_with_layout(
                    vec2(path_width, 20.0),
                    Layout::left_to_right(Align::Center),
                    |ui| {
                        ui.add(
                            Label::new(RichText::new(&path_text).color(TEXT_PRIMARY))
                                .truncate()
                                .sense(Sense::click()),
                        )
                    },
                )
                .inner;
            let response = response
                .on_hover_text(&path_text)
                .on_hover_cursor(CursorIcon::PointingHand);
            if response.double_clicked() {
                actions.push(RowAction::Open(group_index, file_index));
            }

            ui.with_layout(Layout::right_to_left(Align::Center), |ui| {
                ui.add(Label::new(
                    RichText::new(&group.size_text).small().color(TEXT_MUTED),
                ));
            });
        },
    );
}

fn configure_style(ctx: &egui::Context) {
    let mut fonts = egui::FontDefinitions::default();
    if let Ok(bytes) = fs::read(r"C:\Windows\Fonts\segoeui.ttf") {
        fonts.font_data.insert(
            "segoe_ui".to_string(),
            Arc::new(egui::FontData::from_owned(bytes)),
        );
        fonts
            .families
            .entry(egui::FontFamily::Proportional)
            .or_default()
            .insert(0, "segoe_ui".to_string());
    }
    if let Ok(bytes) = fs::read(r"C:\Windows\Fonts\consola.ttf") {
        fonts.font_data.insert(
            "consolas".to_string(),
            Arc::new(egui::FontData::from_owned(bytes)),
        );
        fonts
            .families
            .entry(egui::FontFamily::Monospace)
            .or_default()
            .insert(0, "consolas".to_string());
    }
    ctx.set_fonts(fonts);
    ctx.set_theme(egui::Theme::Light);

    let mut style = (*ctx.style_of(egui::Theme::Light)).clone();
    style.visuals = egui::Visuals::light();
    style.visuals.panel_fill = WINDOW_BG;
    style.visuals.window_fill = SURFACE;
    style.visuals.extreme_bg_color = SURFACE;
    style.visuals.faint_bg_color = SURFACE_ALT;
    style.visuals.override_text_color = Some(TEXT_PRIMARY);
    style.visuals.hyperlink_color = ACCENT;
    style.visuals.selection.bg_fill = ACCENT;
    style.visuals.selection.stroke = Stroke::new(1.0, Color32::WHITE);
    style.visuals.widgets.noninteractive.bg_stroke = Stroke::new(1.0, BORDER);
    style.visuals.widgets.noninteractive.fg_stroke = Stroke::new(1.0, TEXT_SECONDARY);
    style.visuals.widgets.inactive.bg_fill = SURFACE;
    style.visuals.widgets.inactive.weak_bg_fill = SURFACE;
    style.visuals.widgets.inactive.bg_stroke =
        Stroke::new(1.0, Color32::from_rgb(0xC9, 0xD0, 0xD8));
    style.visuals.widgets.inactive.fg_stroke = Stroke::new(1.0, TEXT_PRIMARY);
    style.visuals.widgets.inactive.corner_radius = CornerRadius::same(5);
    style.visuals.widgets.hovered.bg_fill = Color32::from_rgb(0xF2, 0xF5, 0xF8);
    style.visuals.widgets.hovered.weak_bg_fill = Color32::from_rgb(0xF2, 0xF5, 0xF8);
    style.visuals.widgets.hovered.bg_stroke = Stroke::new(1.0, Color32::from_rgb(0xC9, 0xD0, 0xD8));
    style.visuals.widgets.hovered.fg_stroke = Stroke::new(1.0, TEXT_PRIMARY);
    style.visuals.widgets.hovered.corner_radius = CornerRadius::same(5);
    style.visuals.widgets.active.bg_fill = Color32::from_rgb(0xE3, 0xE9, 0xEF);
    style.visuals.widgets.active.weak_bg_fill = Color32::from_rgb(0xE3, 0xE9, 0xEF);
    style.visuals.widgets.active.bg_stroke = Stroke::new(1.0, Color32::from_rgb(0xC9, 0xD0, 0xD8));
    style.visuals.widgets.active.fg_stroke = Stroke::new(1.0, TEXT_PRIMARY);
    style.visuals.widgets.active.corner_radius = CornerRadius::same(5);
    style.spacing.item_spacing = vec2(8.0, 8.0);
    style.spacing.button_padding = vec2(12.0, 7.0);
    style.text_styles = BTreeMap::from([
        (TextStyle::Heading, egui::FontId::proportional(15.0)),
        (TextStyle::Body, egui::FontId::proportional(13.0)),
        (TextStyle::Button, egui::FontId::proportional(13.0)),
        (TextStyle::Small, egui::FontId::proportional(11.0)),
        (TextStyle::Monospace, egui::FontId::monospace(11.0)),
    ]);
    ctx.set_style_of(egui::Theme::Light, style.clone());
    ctx.set_global_style(style);
}

fn app_trash_root() -> PathBuf {
    let base = std::env::var_os("LOCALAPPDATA")
        .map(PathBuf::from)
        .unwrap_or_else(std::env::temp_dir);
    base.join("DuplicadosApp").join("Borrados")
}

fn panic_text(payload: Box<dyn Any + Send>) -> String {
    if let Some(text) = payload.downcast_ref::<&str>() {
        (*text).to_string()
    } else if let Some(text) = payload.downcast_ref::<String>() {
        text.clone()
    } else {
        "error desconocido".to_string()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn temp_dir(name: &str) -> PathBuf {
        let stamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("reloj del sistema")
            .as_nanos();
        let dir = std::env::temp_dir().join(format!(
            "duplicados-ui-{name}-{}-{stamp}",
            std::process::id()
        ));
        fs::create_dir_all(&dir).expect("crear temporal");
        dir
    }

    fn wait_until_idle(app: &mut DuplicadosApp) {
        let deadline = Instant::now() + Duration::from_secs(15);
        while app.busy {
            app.drain_messages();
            if !app.busy {
                break;
            }
            assert!(Instant::now() < deadline, "la búsqueda no terminó");
            thread::sleep(Duration::from_millis(10));
        }
        app.drain_messages();
    }

    #[test]
    fn busqueda_completa_desde_el_estado_de_la_ui() {
        let dir = temp_dir("busqueda");
        let sub = dir.join("sub");
        fs::create_dir_all(&sub).expect("subcarpeta");
        fs::write(dir.join("a.txt"), b"hola").expect("a");
        fs::write(sub.join("b.txt"), b"hola").expect("b");
        fs::write(dir.join("c.txt"), b"contenido distinto").expect("c");

        let mut app = DuplicadosApp::from_scratch();
        app.set_folder(dir.clone());
        assert!(app.status_text.contains("Carpeta lista"));

        app.start_search();
        assert!(app.busy);
        wait_until_idle(&mut app);

        assert!(!app.busy);
        assert_eq!(app.groups.len(), 1);
        assert_eq!(app.groups[0].files.len(), 2);
        assert_eq!(app.rows.len(), 3, "cabecera + dos archivos");
        assert_eq!(app.progress_text, "Listo");
        assert!(app.status_text.contains("1 grupo(s) de duplicados"));

        app.select_all_copies();
        assert_eq!(app.selected_count, 1, "se conserva la primera copia");
        assert_eq!(app.selected_bytes, 4);

        app.clear_selection();
        assert_eq!(app.selected_count, 0);
        assert_eq!(app.selected_bytes, 0);

        fs::remove_dir_all(&dir).expect("limpiar");
    }

    #[test]
    fn carpeta_sin_duplicados_deja_el_estado_esperado() {
        let dir = temp_dir("vacia");
        fs::write(dir.join("unico.txt"), b"contenido unico").expect("archivo");

        let mut app = DuplicadosApp::from_scratch();
        app.set_folder(dir.clone());
        app.start_search();
        wait_until_idle(&mut app);

        assert!(app.groups.is_empty());
        assert!(app.rows.is_empty());
        assert_eq!(app.status_text, "No se encontraron archivos duplicados.");
        assert_eq!(app.progress_value, 100.0);

        fs::remove_dir_all(&dir).expect("limpiar");
    }
}
