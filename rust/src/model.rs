//! Tipos de datos compartidos por el motor y la interfaz.

use std::path::PathBuf;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::thread;
use std::time::{Duration, SystemTime, UNIX_EPOCH};

/// Identificador corto y único para nombres de archivos y carpetas temporales.
pub fn unique_token() -> String {
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .map(|elapsed| elapsed.as_nanos())
        .unwrap_or_default();
    let count = COUNTER.fetch_add(1, Ordering::Relaxed);
    format!("{:x}-{:x}-{:x}", std::process::id(), nanos, count)
}

/// Equivalente a la pareja (st_dev, st_ino) de `os.stat`: identifica de forma única
/// un archivo dentro del volumen, de modo que se detecta si la ruta fue sustituida.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub struct FileIdentity {
    pub volume_serial: u32,
    pub file_index: u64,
}

/// Archivo encontrado durante el escaneo, con su tamaño e identidad de volumen.
#[derive(Clone, Debug)]
pub struct FileEntry {
    pub path: PathBuf,
    pub size: u64,
    /// `None` hasta que la fase de confirmación abre el archivo.
    pub identity: Option<FileIdentity>,
}

/// Grupo de archivos considerados duplicados entre sí.
#[derive(Clone, Debug)]
pub struct DuplicateGroup {
    pub size: u64,
    /// Nombre representativo del grupo (el del primer archivo encontrado).
    pub display_name: String,
    pub files: Vec<FileEntry>,
}

/// Avance global de la búsqueda (barra superior).
#[derive(Clone, Debug)]
pub struct ScanProgress {
    pub percentage: f64,
    pub text: String,
}

/// Avance de la acción concreta en curso (barra del panel «Actividad»).
#[derive(Clone, Debug)]
pub struct PhaseProgress {
    pub phase: String,
    pub percentage: f64,
    pub indeterminate: bool,
}

/// Resultado completo de una búsqueda.
#[derive(Default)]
pub struct ScanResult {
    pub groups: Vec<DuplicateGroup>,
    pub errors: usize,
    pub general_error: Option<String>,
    pub cancelled: bool,
}

/// Archivo candidato a borrado, con los datos necesarios para revalidarlo.
#[derive(Clone, Debug)]
pub struct DeleteCandidate {
    pub path: PathBuf,
    pub size: u64,
    pub identity: Option<FileIdentity>,
    pub group_index: usize,
}

/// Dónde acabó un archivo al borrarlo.
#[derive(Clone, Copy, Debug, Default, PartialEq, Eq)]
pub enum DeleteDestination {
    /// Papelera de reciclaje de Windows.
    #[default]
    SystemRecycleBin,
    /// Papelera propia de la aplicación.
    AppTrash,
}

/// Resultado de un borrado.
#[derive(Default)]
pub struct DeleteOutcome {
    /// Archivos movidos y verificados en la papelera.
    pub deleted: usize,
    /// Archivos que no se tocaron.
    pub errors: usize,
    /// Archivos que ya no están en su ruta pero cuyo destino no se pudo confirmar.
    pub unverified: usize,
    pub cancelled: bool,
    /// Rutas que ya no existen porque se movieron a la papelera, verificadas o no.
    pub moved: Vec<PathBuf>,
    pub destination: DeleteDestination,
    /// Carpeta de la papelera propia usada, si fue el caso.
    pub app_trash_folder: Option<PathBuf>,
}

/// Eventos que el motor envía a la interfaz.
pub enum EngineEvent {
    Log(String),
    Progress(ScanProgress),
    Activity(String),
    Phase(PhaseProgress),
}

pub type EventSink = Arc<dyn Fn(EngineEvent) + Send + Sync>;

/// Bandera de cancelación y pausa compartida con los hilos del motor.
#[derive(Clone, Default)]
pub struct Control {
    cancelled: Arc<AtomicBool>,
    paused: Arc<AtomicBool>,
}

impl Control {
    pub fn new() -> Self {
        Self::default()
    }

    pub fn cancel(&self) {
        self.cancelled.store(true, Ordering::SeqCst);
        self.paused.store(false, Ordering::SeqCst);
    }

    pub fn set_paused(&self, paused: bool) {
        self.paused.store(paused, Ordering::SeqCst);
    }

    pub fn is_paused(&self) -> bool {
        self.paused.load(Ordering::SeqCst)
    }

    pub fn is_cancelled(&self) -> bool {
        self.cancelled.load(Ordering::SeqCst)
    }

    /// Detiene el hilo mientras la búsqueda esté en pausa. Sin espera activa.
    pub fn wait_while_paused(&self) {
        while self.is_paused() && !self.is_cancelled() {
            thread::sleep(Duration::from_millis(100));
        }
    }
}

/// Formato de tamaño con hasta dos decimales (el que usa la interfaz).
pub fn format_size(bytes: u64) -> String {
    format_size_impl(bytes, 2)
}

/// Formato de tamaño con hasta un decimal (el que usan los registros del motor).
pub fn format_size_short(bytes: u64) -> String {
    format_size_impl(bytes, 1)
}

fn format_size_impl(bytes: u64, decimals: usize) -> String {
    const UNITS: [&str; 5] = ["B", "KB", "MB", "GB", "TB"];
    let mut value = bytes as f64;
    let mut unit = 0;
    while value >= 1024.0 && unit < UNITS.len() - 1 {
        value /= 1024.0;
        unit += 1;
    }

    if unit == 0 {
        return format!("{bytes} B");
    }

    let text = match decimals {
        1 => format!("{value:.1}"),
        _ => format!("{value:.2}"),
    };
    let text = text.trim_end_matches('0').trim_end_matches('.').to_string();
    format!("{text} {}", UNITS[unit])
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn formatos_de_tamano() {
        assert_eq!(format_size(0), "0 B");
        assert_eq!(format_size(1023), "1023 B");
        assert_eq!(format_size(1024), "1 KB");
        assert_eq!(format_size(1536), "1.5 KB");
        assert_eq!(format_size_short(1536), "1.5 KB");
        assert_eq!(format_size(10 * 1024 * 1024 + 512 * 1024), "10.5 MB");
    }
}
