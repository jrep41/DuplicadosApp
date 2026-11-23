//! Operaciones de sistema de archivos: identidad, tamaño, cabecera y comparación
//! byte a byte, equivalentes a las del original en C#.

use std::fs::{File, OpenOptions};
use std::io::{self, Read, Seek, SeekFrom};
use std::os::windows::fs::OpenOptionsExt;
use std::os::windows::io::AsRawHandle;
use std::path::Path;

use windows::Win32::Foundation::HANDLE;
use windows::Win32::Storage::FileSystem::{BY_HANDLE_FILE_INFORMATION, GetFileInformationByHandle};

use crate::model::{FileEntry, FileIdentity};

/// Cabecera que se compara primero. Si dos archivos difieren en sus primeros 8 KB
/// es imposible que sean idénticos, así que no hace falta recorrer el resto.
pub const HEADER_SIZE: usize = 8 * 1024;

const BUFFER_SIZE: usize = 1024 * 1024;
const FILE_SHARE_ALL: u32 = 0x1 | 0x2 | 0x4;
const FILE_FLAG_SEQUENTIAL_SCAN: u32 = 0x0800_0000;
const FILE_ATTRIBUTE_REPARSE_POINT: u32 = 0x0400;

/// Abre el archivo en modo lectura permitiendo que otros procesos lo usen y lo muevan.
fn open_read(path: &Path) -> io::Result<File> {
    OpenOptions::new()
        .read(true)
        .share_mode(FILE_SHARE_ALL)
        .custom_flags(FILE_FLAG_SEQUENTIAL_SCAN)
        .open(path)
}

/// Identidad (volumen + índice) del archivo abierto.
pub fn identity_of(file: &File) -> Option<FileIdentity> {
    let mut info = BY_HANDLE_FILE_INFORMATION::default();
    // SAFETY: el handle pertenece a `file`, que sigue vivo durante la llamada.
    unsafe {
        GetFileInformationByHandle(HANDLE(file.as_raw_handle()), &mut info).ok()?;
    }

    Some(FileIdentity {
        volume_serial: info.dwVolumeSerialNumber,
        file_index: ((info.nFileIndexHigh as u64) << 32) | info.nFileIndexLow as u64,
    })
}

/// Obtiene la identidad del archivo y su tamaño. Devuelve `None` si la ruta no es un
/// archivo regular o no se puede abrir.
pub fn try_get_entry(path: &Path) -> Option<FileEntry> {
    let file = open_read(path).ok()?;
    let metadata = file.metadata().ok()?;
    if !metadata.is_file() {
        return None;
    }

    Some(FileEntry {
        path: path.to_path_buf(),
        size: metadata.len(),
        identity: identity_of(&file),
    })
}

/// Abre el archivo una sola vez para obtener su identidad, verificar su tamaño y leer
/// su cabecera. Devuelve `None` si cambió de tamaño o no se pudo leer entera.
pub fn try_get_entry_and_header(
    path: &Path,
    expected_size: u64,
    header: &mut [u8],
) -> Option<(FileEntry, usize)> {
    let mut file = open_read(path).ok()?;
    let metadata = file.metadata().ok()?;
    if !metadata.is_file() || metadata.len() != expected_size {
        return None;
    }

    let to_read = header.len().min(expected_size as usize);
    if to_read > 0 {
        let read = read_full(&mut file, &mut header[..to_read]).ok()?;
        if read != to_read {
            return None;
        }
    }

    Some((
        FileEntry {
            path: path.to_path_buf(),
            size: metadata.len(),
            identity: identity_of(&file),
        },
        to_read,
    ))
}

/// Comprueba que la ruta siga apuntando al mismo archivo con el mismo tamaño.
pub fn matches_identity(path: &Path, identity: FileIdentity, size: u64) -> bool {
    try_get_entry(path).is_some_and(|entry| entry.identity == Some(identity) && entry.size == size)
}

/// Compara dos archivos por su contenido exacto, sin calcular hashes. Si
/// `start_offset` es 0 compara primero los primeros 8 KB; después continúa desde ahí.
/// `is_cancelled` se consulta en cada bloque leído.
pub fn compare_byte_by_byte(
    path_a: &Path,
    path_b: &Path,
    is_cancelled: &dyn Fn() -> bool,
    start_offset: u64,
) -> io::Result<bool> {
    let mut file_a = open_read(path_a)?;
    let mut file_b = open_read(path_b)?;

    if start_offset == 0 {
        if is_cancelled() {
            return Ok(false);
        }

        let mut header_a = vec![0u8; HEADER_SIZE];
        let mut header_b = vec![0u8; HEADER_SIZE];
        let read_a = read_full(&mut file_a, &mut header_a)?;
        let read_b = read_full(&mut file_b, &mut header_b)?;
        if read_a != read_b || header_a[..read_a] != header_b[..read_b] {
            return Ok(false);
        }

        if read_a < HEADER_SIZE {
            return Ok(true);
        }
    } else {
        file_a.seek(SeekFrom::Start(start_offset))?;
        file_b.seek(SeekFrom::Start(start_offset))?;
    }

    let mut buffer_a = vec![0u8; BUFFER_SIZE];
    let mut buffer_b = vec![0u8; BUFFER_SIZE];
    loop {
        if is_cancelled() {
            return Ok(false);
        }

        let read_a = read_full(&mut file_a, &mut buffer_a)?;
        let read_b = read_full(&mut file_b, &mut buffer_b)?;
        if read_a != read_b {
            return Ok(false);
        }

        if read_a == 0 {
            return Ok(true);
        }

        if buffer_a[..read_a] != buffer_b[..read_b] {
            return Ok(false);
        }
    }
}

/// Lee hasta llenar `buffer` o llegar al final del archivo.
fn read_full(file: &mut File, buffer: &mut [u8]) -> io::Result<usize> {
    let mut total = 0;
    while total < buffer.len() {
        match file.read(&mut buffer[total..]) {
            Ok(0) => break,
            Ok(read) => total += read,
            Err(error) if error.kind() == io::ErrorKind::Interrupted => continue,
            Err(error) => return Err(error),
        }
    }

    Ok(total)
}

/// Devuelve true si el archivo tiene el atributo de punto de reanudación (enlace,
/// unión de directorios…). Se usa para no recorrer ciclos.
pub fn is_reparse_point(path: &Path) -> bool {
    use std::os::windows::fs::MetadataExt;
    std::fs::symlink_metadata(path)
        .map(|metadata| metadata.file_attributes() & FILE_ATTRIBUTE_REPARSE_POINT != 0)
        .unwrap_or(false)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::fs;
    use std::io::Write;
    use std::path::PathBuf;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn temp_dir(name: &str) -> PathBuf {
        let stamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("reloj del sistema")
            .as_nanos();
        let dir = std::env::temp_dir().join(format!(
            "duplicados-rs-{name}-{}-{stamp}",
            std::process::id()
        ));
        fs::create_dir_all(&dir).expect("crear temporal");
        dir
    }

    #[test]
    fn identidad_detecta_el_mismo_archivo() {
        let dir = temp_dir("identidad");
        let path = dir.join("a.bin");
        fs::write(&path, b"contenido").expect("escribir");

        let entry = try_get_entry(&path).expect("entrada");
        assert!(matches_identity(
            &path,
            entry.identity.expect("identidad"),
            entry.size
        ));

        fs::remove_dir_all(&dir).expect("limpiar");
    }

    #[test]
    fn comparacion_byte_a_byte() {
        let dir = temp_dir("comparar");
        let a = dir.join("a.bin");
        let b = dir.join("b.bin");
        let c = dir.join("c.bin");

        let mut datos = vec![7u8; 20_000];
        fs::write(&a, &datos).expect("escribir a");
        fs::write(&b, &datos).expect("escribir b");
        datos[15_000] = 9;
        fs::write(&c, &datos).expect("escribir c");

        assert!(compare_byte_by_byte(&a, &b, &|| false, 0).expect("comparar iguales"));
        assert!(!compare_byte_by_byte(&a, &c, &|| false, 0).expect("comparar distintos"));
        assert!(
            !compare_byte_by_byte(&a, &c, &|| false, HEADER_SIZE as u64)
                .expect("comparar desde cabecera")
        );

        fs::remove_dir_all(&dir).expect("limpiar");
    }

    #[test]
    fn cabecera_y_tamano_deben_coincidir() {
        let dir = temp_dir("cabecera");
        let path = dir.join("a.bin");
        let mut file = fs::File::create(&path).expect("crear");
        file.write_all(b"12345678").expect("escribir");
        drop(file);

        let mut header = [0u8; 4];
        assert!(try_get_entry_and_header(&path, 8, &mut header).is_some());
        assert_eq!(&header, b"1234");
        assert!(try_get_entry_and_header(&path, 9, &mut header).is_none());

        fs::remove_dir_all(&dir).expect("limpiar");
    }
}
