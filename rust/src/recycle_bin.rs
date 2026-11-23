//! Envío de archivos a la Papelera de reciclaje de Windows y verificación de que
//! llegaron allí de verdad.
//!
//! `IFileOperation` devuelve éxito aunque Windows acabe borrando el archivo para
//! siempre (por ejemplo, dentro de un AppContainer). Por eso el envío se comprueba
//! leyendo los metadatos `$I` de la Papelera y contando los `$R` antes y después.

use std::ffi::c_void;
use std::fs;
use std::path::{Path, PathBuf};
use std::sync::{Mutex, OnceLock};
use std::time::{Duration, SystemTime};

use windows::Win32::Foundation::{CloseHandle, HANDLE, HLOCAL, LocalFree};
use windows::Win32::Security::Authorization::ConvertSidToStringSidW;
use windows::Win32::Security::{GetTokenInformation, TOKEN_QUERY, TOKEN_USER, TokenUser};
use windows::Win32::System::Com::{
    CLSCTX_ALL, COINIT_APARTMENTTHREADED, CoCreateInstance, CoInitializeEx, CoUninitialize,
};
use windows::Win32::System::Threading::{GetCurrentProcess, OpenProcessToken};
use windows::Win32::UI::Shell::{
    FILEOPERATION_FLAGS, FOF_ALLOWUNDO, FOF_NOCONFIRMATION, FOF_NOERRORUI, FOF_SILENT,
    FileOperation, IFileOperation, IShellItem, SHCreateItemFromParsingName,
};
use windows::core::{HSTRING, PWSTR};

use crate::model::unique_token;

/// Margen restado al inicio de la operación al filtrar los metadatos `$I`. Cubre el
/// redondeo de la fecha de escritura en NTFS (~2 s) y el desfase con el reloj.
const DATE_MARGIN: Duration = Duration::from_secs(15);

static CACHE: OnceLock<Mutex<Option<(PathBuf, bool)>>> = OnceLock::new();

struct ComGuard {
    initialized: bool,
}

impl ComGuard {
    fn new() -> Self {
        // SAFETY: inicializa COM en este hilo; el guardia lo desinicializa al salir.
        let result = unsafe { CoInitializeEx(None, COINIT_APARTMENTTHREADED) };
        Self {
            initialized: result.is_ok(),
        }
    }
}

impl Drop for ComGuard {
    fn drop(&mut self) {
        if self.initialized {
            // SAFETY: solo se desinicializa lo que este hilo inicializó.
            unsafe { CoUninitialize() };
        }
    }
}

/// Comprueba si este entorno puede reciclar de verdad. El resultado se recuerda por
/// ubicación. Es una operación costosa (envía y limpia una sonda), así que se llama
/// una vez por borrado, no por archivo.
pub fn is_available(working_folder: Option<&Path>) -> bool {
    let mut candidates = Vec::with_capacity(2);
    if let Some(folder) = working_folder
        && folder.is_dir()
    {
        candidates.push(folder.to_path_buf());
    }
    candidates.push(std::env::temp_dir());

    let cache = CACHE.get_or_init(|| Mutex::new(None));
    let mut cached = cache
        .lock()
        .unwrap_or_else(|poisoned| poisoned.into_inner());

    for candidate in candidates {
        if let Some((known_folder, known)) = cached.as_ref()
            && same_path(known_folder, &candidate)
        {
            return *known;
        }

        if let Some(available) = probe(&candidate) {
            *cached = Some((candidate, available));
            return available;
        }
    }

    false
}

/// Mueve un archivo a la Papelera.
///
/// - `None`: el archivo no se movió (sigue en su ruta o la operación se abortó).
/// - `Some(true)`: se movió y se confirmó su presencia en la Papelera.
/// - `Some(false)`: desapareció de su ruta, pero no se pudo confirmar el destino.
pub fn move_to_recycle_bin(path: &Path) -> Option<bool> {
    if !path.is_file() {
        return None;
    }

    let start = SystemTime::now().checked_sub(DATE_MARGIN);
    if !send_to_shell(path) {
        return None;
    }

    Some(search_metadata(path, start).0)
}

fn probe(folder: &Path) -> Option<bool> {
    let probe_path = folder.join(format!("duplicados-sonda-{}.bin", unique_token()));
    if fs::write(&probe_path, [0u8; 64]).is_err() {
        return None;
    }

    let before = count_recycled(&probe_path);
    let Some(before) = before else {
        let _ = fs::remove_file(&probe_path);
        return Some(false);
    };

    let start = SystemTime::now().checked_sub(DATE_MARGIN);
    let sent = send_to_shell(&probe_path);
    let (found, metadata_path) = if sent {
        search_metadata(&probe_path, start)
    } else {
        (false, None)
    };
    let after = count_recycled(&probe_path);
    let success = sent && found && after.is_some_and(|after| after > before);

    // La sonda de prueba no debe quedarse en la Papelera del usuario.
    if success && let Some(metadata_path) = metadata_path.as_deref() {
        cleanup_probe(metadata_path);
    }

    if probe_path.exists() {
        let _ = fs::remove_file(&probe_path);
    }

    Some(success)
}

fn send_to_shell(path: &Path) -> bool {
    let _com = ComGuard::new();
    let Ok(absolute) = std::path::absolute(path) else {
        return false;
    };

    let result = (|| -> windows::core::Result<bool> {
        // SAFETY: COM está inicializado en este hilo y los objetos viven hasta el final.
        unsafe {
            let operation: IFileOperation = CoCreateInstance(&FileOperation, None, CLSCTX_ALL)?;
            operation.SetOperationFlags(FILEOPERATION_FLAGS(
                FOF_ALLOWUNDO.0 | FOF_NOCONFIRMATION.0 | FOF_SILENT.0 | FOF_NOERRORUI.0,
            ))?;

            let item: IShellItem =
                SHCreateItemFromParsingName(&HSTRING::from(absolute.as_os_str()), None)?;
            operation.DeleteItem(&item, None)?;
            operation.PerformOperations()?;
            let aborted = operation.GetAnyOperationsAborted()?;

            Ok(!aborted.as_bool() && !absolute.exists())
        }
    })();

    result.unwrap_or(false)
}

/// Cuenta los elementos reciclados de la unidad del archivo dado.
fn count_recycled(reference: &Path) -> Option<usize> {
    let recycle_bin = recycle_bin_folder(reference)?;
    match fs::read_dir(&recycle_bin) {
        Ok(entries) => Some(
            entries
                .flatten()
                .filter(|entry| {
                    entry
                        .file_name()
                        .to_string_lossy()
                        .to_lowercase()
                        .starts_with("$r")
                        && entry.metadata().map(|meta| meta.is_file()).unwrap_or(false)
                })
                .count(),
        ),
        Err(error) if error.kind() == std::io::ErrorKind::NotFound => Some(0),
        Err(_) => None,
    }
}

/// Busca entre los metadatos `$I` de la Papelera de la unidad si alguno corresponde
/// a `original`. Devuelve si se encontró y la ruta del metadato.
fn search_metadata(original: &Path, start: Option<SystemTime>) -> (bool, Option<PathBuf>) {
    let Ok(absolute) = std::path::absolute(original) else {
        return (false, None);
    };
    let Some(recycle_bin) = recycle_bin_folder(&absolute) else {
        return (false, None);
    };
    let Ok(entries) = fs::read_dir(&recycle_bin) else {
        return (false, None);
    };

    let normalized = path_key(&absolute);
    let mut matches: Vec<(SystemTime, PathBuf)> = Vec::new();

    for entry in entries.flatten() {
        let name = entry.file_name().to_string_lossy().to_lowercase();
        if !name.starts_with("$i") {
            continue;
        }

        let Ok(metadata) = entry.metadata() else {
            continue;
        };
        if !metadata.is_file() {
            continue;
        }

        let Ok(modified) = metadata.modified() else {
            continue;
        };
        if start.is_some_and(|start| modified < start) {
            continue;
        }

        if let Some(path) = original_path_from_metadata(&entry.path())
            && path_key(Path::new(&path)) == normalized
        {
            matches.push((modified, entry.path()));
        }
    }

    matches.sort_by_key(|entry| std::cmp::Reverse(entry.0));
    let found = matches.first().map(|(_, path)| path.clone());
    (found.is_some(), found)
}

fn recycle_bin_folder(reference: &Path) -> Option<PathBuf> {
    let absolute = std::path::absolute(reference).ok()?;
    let root = absolute.ancestors().last()?.to_path_buf();
    let sid = user_sid()?;
    Some(root.join("$Recycle.Bin").join(sid))
}

fn user_sid() -> Option<String> {
    // SAFETY: los handles y búferes se liberan en esta misma función.
    unsafe {
        let mut token = HANDLE::default();
        OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &mut token).ok()?;

        let mut size = 0u32;
        let _ = GetTokenInformation(token, TokenUser, None, 0, &mut size);
        if size == 0 {
            let _ = CloseHandle(token);
            return None;
        }

        // El búfer debe estar alineado para TOKEN_USER (contiene punteros), no solo
        // para bytes: se reserva en palabras de 64 bits.
        let words = (size as usize).div_ceil(std::mem::size_of::<u64>());
        let mut buffer = vec![0u64; words];
        let result = GetTokenInformation(
            token,
            TokenUser,
            Some(buffer.as_mut_ptr() as *mut c_void),
            size,
            &mut size,
        );
        let _ = CloseHandle(token);
        result.ok()?;

        // SAFETY: GetTokenInformation escribió un TOKEN_USER completo y alineado al
        // principio del búfer, que sigue vivo durante la referencia.
        let token_user = &*(buffer.as_ptr() as *const TOKEN_USER);
        let mut sid_string = PWSTR::null();
        ConvertSidToStringSidW(token_user.User.Sid, &mut sid_string).ok()?;
        let sid = sid_string.to_string().ok();
        let _ = LocalFree(Some(HLOCAL(sid_string.0 as *mut c_void)));
        sid
    }
}

/// Lee la ruta original de un metadato `$I`. Formato: 8 bytes de versión, 8 de
/// tamaño, 8 de fecha y la ruta en UTF-16. La versión 2 añade 4 bytes con la
/// longitud en caracteres antes de la ruta.
fn original_path_from_metadata(metadata_path: &Path) -> Option<String> {
    let bytes = fs::read(metadata_path).ok()?;
    if bytes.len() < 28 {
        return None;
    }

    let version = i64::from_le_bytes(bytes[0..8].try_into().ok()?);
    let start = if version >= 2 { 28 } else { 24 };
    if start >= bytes.len() {
        return None;
    }

    let utf16: Vec<u16> = bytes[start..]
        .as_chunks::<2>()
        .0
        .iter()
        .map(|pair| u16::from_le_bytes(*pair))
        .collect();
    let end = utf16
        .iter()
        .position(|character| *character == 0)
        .unwrap_or(utf16.len());
    String::from_utf16(&utf16[..end]).ok()
}

/// Elimina silenciosamente el par `$I`/`$R` generado por la sonda.
fn cleanup_probe(metadata_path: &Path) {
    let Some(name) = metadata_path.file_name() else {
        return;
    };
    let name = name.to_string_lossy();
    if !name.to_lowercase().starts_with("$i") {
        return;
    }

    let data_path = metadata_path.with_file_name(format!("$R{}", &name[2..]));
    if data_path.is_file() {
        let _ = fs::remove_file(&data_path);
    }
    let _ = fs::remove_file(metadata_path);
}

fn path_key(path: &Path) -> String {
    path.to_string_lossy().to_lowercase()
}

fn same_path(left: &Path, right: &Path) -> bool {
    path_key(left) == path_key(right)
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
            "duplicados-recycle-{name}-{}-{stamp}",
            std::process::id()
        ));
        fs::create_dir_all(&dir).expect("crear temporal");
        dir
    }

    fn write_metadata(path: &Path, version: i64, original: &str) {
        let mut bytes = Vec::new();
        bytes.extend_from_slice(&version.to_le_bytes());
        bytes.extend_from_slice(&0u64.to_le_bytes());
        bytes.extend_from_slice(&0u64.to_le_bytes());
        if version >= 2 {
            bytes.extend_from_slice(&((original.len() + 1) as u32).to_le_bytes());
        }
        for unit in original.encode_utf16() {
            bytes.extend_from_slice(&unit.to_le_bytes());
        }
        bytes.extend_from_slice(&0u16.to_le_bytes());
        fs::write(path, bytes).expect("escribir metadatos");
    }

    #[test]
    fn lee_la_ruta_original_de_los_metadatos() {
        let dir = temp_dir("metadatos");
        let metadata = dir.join("$IABC123.txt");
        write_metadata(&metadata, 2, r"G:\Fotos\vacaciones.jpg");

        assert_eq!(
            original_path_from_metadata(&metadata).as_deref(),
            Some(r"G:\Fotos\vacaciones.jpg")
        );

        fs::remove_dir_all(&dir).expect("limpiar");
    }

    #[test]
    fn lee_metadatos_de_la_version_1() {
        let dir = temp_dir("metadatos-v1");
        let metadata = dir.join("$IABC124.txt");
        write_metadata(&metadata, 1, r"C:\Temp\a.txt");

        assert_eq!(
            original_path_from_metadata(&metadata).as_deref(),
            Some(r"C:\Temp\a.txt")
        );

        fs::remove_dir_all(&dir).expect("limpiar");
    }

    #[test]
    fn metadatos_corruptos_no_panican() {
        let dir = temp_dir("metadatos-corruptos");
        let metadata = dir.join("$IABC125.txt");
        fs::write(&metadata, [1u8, 2, 3]).expect("escribir");

        assert_eq!(original_path_from_metadata(&metadata), None);

        fs::remove_dir_all(&dir).expect("limpiar");
    }

    /// Prueba de extremo a extremo del envío real. Se ejecuta a mano porque necesita
    /// una sesión de escritorio con Papelera y no debe correr en CI.
    #[test]
    #[ignore = "requiere una sesión de escritorio con Papelera de reciclaje"]
    fn envia_un_archivo_de_prueba_a_la_papelera() {
        let dir = temp_dir("envio");
        let file = dir.join("prueba-papelera-duplicados.txt");
        fs::write(&file, format!("prueba {}", unique_token())).expect("escribir prueba");
        let start = SystemTime::now().checked_sub(DATE_MARGIN);

        assert!(send_to_shell(&file), "el shell debe aceptar el envío");
        assert!(!file.exists(), "el archivo debe salir de su ruta");

        let (found, metadata_path) = search_metadata(&file, start);
        assert!(found, "debe aparecer el metadato $I del archivo enviado");
        if let Some(metadata_path) = metadata_path {
            cleanup_probe(&metadata_path);
        }

        assert!(
            is_available(Some(&dir)),
            "la sonda debe confirmar que el entorno puede reciclar"
        );

        fs::remove_dir_all(&dir).expect("limpiar");
    }
}
