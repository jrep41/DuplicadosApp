//! Papelera propia de la aplicación: mueve los archivos a una carpeta gestionada
//! por la app, conservando su ruta original en un manifiesto.
//!
//! Existe porque la Papelera de Windows no está disponible en todos los entornos:
//! dentro de un AppContainer el shell no puede escribir en ella y destruiría los
//! archivos de forma permanente.

use std::fs::{self, OpenOptions};
use std::io::{self, Write};
use std::os::windows::fs::MetadataExt;
use std::path::{Component, Path, PathBuf};

use chrono::{DateTime, Local};
use windows::Win32::Storage::FileSystem::{
    FILE_ATTRIBUTE_NORMAL, FILE_ATTRIBUTE_READONLY, FILE_FLAGS_AND_ATTRIBUTES, MOVE_FILE_FLAGS,
    MOVEFILE_COPY_ALLOWED, MoveFileExW, SetFileAttributesW,
};
use windows::core::HSTRING;

use crate::model::unique_token;

const MANIFEST_NAME: &str = "manifiesto.txt";

/// Papelera propia de una tanda de borrado.
pub struct TrashService {
    folder: PathBuf,
    manifest: String,
    saved: usize,
}

impl TrashService {
    /// Reserva la carpeta de esta tanda. El nombre tiene resolución de un segundo,
    /// así que dos tandas dentro del mismo segundo reciben sufijos distintos.
    pub fn new(root: &Path, moment: DateTime<Local>) -> io::Result<Self> {
        fs::create_dir_all(root)?;
        let folder = reserve_folder(root, moment)?;
        let manifest = format!(
            "Archivos enviados a la papelera de la aplicacion.\nFecha: {}\n\
             Cada linea es la ruta original de un archivo guardado aqui.\n\n",
            moment.format("%Y-%m-%d %H:%M:%S")
        );

        Ok(Self {
            folder,
            manifest,
            saved: 0,
        })
    }

    pub fn folder(&self) -> &Path {
        &self.folder
    }

    /// Guarda un archivo en la papelera propia. Devuelve false si no se pudo; en ese
    /// caso el archivo original sigue en su sitio.
    pub fn save(&mut self, original: &Path) -> bool {
        if original.as_os_str().is_empty() || !original.is_file() {
            return false;
        }

        let destination = self.destination(original);
        if let Some(parent) = destination.parent()
            && fs::create_dir_all(parent).is_err()
        {
            return false;
        }

        let original_attributes = fs::metadata(original)
            .map(|metadata| metadata.file_attributes())
            .unwrap_or(FILE_ATTRIBUTE_NORMAL.0);
        let was_read_only = original_attributes & FILE_ATTRIBUTE_READONLY.0 != 0;

        // Si el archivo es de solo lectura y se mueve entre volúmenes, MoveFileEx copia
        // y falla al borrar el origen. Se quita el atributo antes y se restaura después.
        if was_read_only {
            set_attributes(original, original_attributes & !FILE_ATTRIBUTE_READONLY.0);
        }

        let moved = move_file(original, &destination);
        if !moved {
            // Si el movimiento falló a medias y el original sigue intacto, se retira
            // cualquier copia parcial en el destino y se restauran los atributos.
            if original.is_file() {
                if was_read_only {
                    set_attributes(original, original_attributes);
                }

                if destination.is_file() {
                    set_attributes(&destination, FILE_ATTRIBUTE_NORMAL.0);
                    let _ = fs::remove_file(&destination);
                }
            }

            return false;
        }

        if was_read_only {
            set_attributes(&destination, original_attributes);
        }

        self.manifest.push_str(&original.display().to_string());
        self.manifest.push('\n');
        self.saved += 1;
        true
    }

    /// Escribe el manifiesto. Si no se guardó nada, retira la carpeta reservada para
    /// no dejar directorios vacíos.
    pub fn save_manifest(&self) {
        if self.saved == 0 {
            let _ = fs::remove_dir(&self.folder);
            return;
        }

        let _ = fs::create_dir_all(&self.folder);
        let path = self.folder.join(MANIFEST_NAME);
        let result = if path.exists() {
            OpenOptions::new()
                .append(true)
                .open(&path)
                .and_then(|mut file| file.write_all(self.manifest.as_bytes()))
        } else {
            fs::write(&path, self.manifest.as_bytes())
        };

        // El contenido ya está a salvo; el manifiesto es una comodidad, no un requisito.
        let _ = result;
    }

    /// Calcula el destino conservando la estructura de carpetas y añade un sufijo si
    /// ya existe, para que dos archivos no se pisen.
    fn destination(&self, original: &Path) -> PathBuf {
        let destination = self.folder.join(relative_path(original));
        if !destination.exists() {
            return destination;
        }

        let parent = destination.parent().unwrap_or(&self.folder);
        let file_name = destination
            .file_name()
            .map(|name| name.to_string_lossy().into_owned())
            .unwrap_or_default();
        let (stem, extension) = split_name(&file_name);

        let mut counter = 2;
        loop {
            let candidate = parent.join(format!("{stem} ({counter}){extension}"));
            if !candidate.exists() {
                return candidate;
            }
            counter += 1;
        }
    }
}

fn reserve_folder(root: &Path, moment: DateTime<Local>) -> io::Result<PathBuf> {
    let base = moment.format("%Y-%m-%d_%H-%M-%S").to_string();

    for attempt in 1..100 {
        let name = if attempt == 1 {
            base.clone()
        } else {
            format!("{base}-{attempt}")
        };
        let destination = root.join(name);
        if destination.exists() {
            continue;
        }

        // Se reserva creando un directorio temporal y renombrándolo: si el destino ya
        // existe (otra tanda u otra instancia), el movimiento falla y se prueba el
        // siguiente sufijo.
        let reservation = root.join(format!(".reserva-{}", unique_token()));
        if fs::create_dir(&reservation).is_err() {
            continue;
        }

        match fs::rename(&reservation, &destination) {
            Ok(()) => return Ok(destination),
            Err(_) => {
                let _ = fs::remove_dir(&reservation);
            }
        }
    }

    let alternative = root.join(format!("{base}-{}", unique_token()));
    fs::create_dir(&alternative)?;
    Ok(alternative)
}

/// Convierte una ruta absoluta en relativa al volumen, sin letra de unidad ni barra
/// inicial, sustituyendo los dos puntos para que sea válida como nombre.
fn relative_path(original: &Path) -> PathBuf {
    let absolute = std::path::absolute(original).unwrap_or_else(|_| original.to_path_buf());
    let mut relative = PathBuf::new();
    let mut skipping_root = true;

    for component in absolute.components() {
        if skipping_root {
            match component {
                Component::Prefix(_) | Component::RootDir => continue,
                _ => skipping_root = false,
            }
        }

        match component {
            Component::Normal(part) => {
                relative.push(part.to_string_lossy().replace(':', "_"));
            }
            other => relative.push(other.as_os_str()),
        }
    }

    relative
}

/// Separa nombre y extensión como `Path.GetFileNameWithoutExtension`/`GetExtension`.
fn split_name(name: &str) -> (String, String) {
    match name.rfind('.') {
        Some(0) => (String::new(), name.to_string()),
        Some(index) if index + 1 < name.len() => {
            (name[..index].to_string(), name[index..].to_string())
        }
        _ => (name.to_string(), String::new()),
    }
}

fn move_file(source: &Path, destination: &Path) -> bool {
    // MOVEFILE_COPY_ALLOWED reproduce el comportamiento de File.Move: si el destino
    // está en otro volumen, copia y borra el origen; nunca sobrescribe un destino.
    // SAFETY: ambas rutas son cadenas válidas y viven hasta que termina la llamada.
    unsafe {
        MoveFileExW(
            &HSTRING::from(source.as_os_str()),
            &HSTRING::from(destination.as_os_str()),
            MOVE_FILE_FLAGS(MOVEFILE_COPY_ALLOWED.0),
        )
        .is_ok()
    }
}

fn set_attributes(path: &Path, attributes: u32) {
    // SAFETY: la ruta es una cadena válida y vive hasta que termina la llamada.
    unsafe {
        let _ = SetFileAttributesW(
            &HSTRING::from(path.as_os_str()),
            FILE_FLAGS_AND_ATTRIBUTES(attributes),
        );
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::os::windows::fs::MetadataExt;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn temp_dir(name: &str) -> PathBuf {
        let stamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("reloj del sistema")
            .as_nanos();
        let dir = std::env::temp_dir().join(format!(
            "duplicados-trash-{name}-{}-{stamp}",
            std::process::id()
        ));
        fs::create_dir_all(&dir).expect("crear temporal");
        dir
    }

    #[test]
    fn guarda_el_archivo_y_escribe_el_manifiesto() {
        let dir = temp_dir("guardar");
        let source = dir.join("origen").join("carpeta");
        fs::create_dir_all(&source).expect("carpeta origen");
        let file = source.join("dato.txt");
        fs::write(&file, b"contenido").expect("escribir");

        let mut trash = TrashService::new(&dir.join("papelera"), Local::now()).expect("papelera");
        let folder = trash.folder().to_path_buf();
        assert!(trash.save(&file));
        assert!(!file.exists());
        assert_eq!(trash.saved, 1);

        let destination = folder
            .join(relative_path(&file))
            .parent()
            .expect("carpeta destino")
            .join("dato.txt");
        assert!(
            destination.is_file(),
            "el archivo debe estar en la papelera"
        );

        trash.save_manifest();
        let manifest = fs::read_to_string(folder.join("manifiesto.txt")).expect("manifiesto");
        assert!(manifest.contains("dato.txt"));
        assert!(manifest.contains(&file.display().to_string()));

        fs::remove_dir_all(&dir).expect("limpiar");
    }

    #[test]
    fn conserva_el_atributo_de_solo_lectura() {
        let dir = temp_dir("readonly");
        let file = dir.join("solo-lectura.txt");
        fs::write(&file, b"x").expect("escribir");
        let mut permissions = fs::metadata(&file).expect("metadata").permissions();
        permissions.set_readonly(true);
        fs::set_permissions(&file, permissions).expect("atributo");

        let original_attributes = fs::metadata(&file).expect("metadata").file_attributes();
        let mut trash = TrashService::new(&dir.join("papelera"), Local::now()).expect("papelera");
        assert!(trash.save(&file));

        let folder = trash.folder().to_path_buf();
        trash.save_manifest();
        let destination = find_file(&folder, "solo-lectura.txt").expect("destino");
        let attributes = fs::metadata(destination)
            .expect("metadata")
            .file_attributes();
        assert_ne!(attributes & FILE_ATTRIBUTE_READONLY.0, 0);
        assert_eq!(
            attributes & FILE_ATTRIBUTE_READONLY.0,
            original_attributes & FILE_ATTRIBUTE_READONLY.0
        );

        fs::remove_dir_all(&dir).expect("limpiar");
    }

    #[test]
    fn dos_tandas_del_mismo_segundo_no_comparten_carpeta() {
        let dir = temp_dir("unica");
        let root = dir.join("papelera");
        let moment = Local::now();
        let first = TrashService::new(&root, moment).expect("primera");
        let second = TrashService::new(&root, moment).expect("segunda");
        assert_ne!(first.folder(), second.folder());

        fs::remove_dir_all(&dir).expect("limpiar");
    }

    #[test]
    fn sin_archivos_no_deja_carpeta() {
        let dir = temp_dir("vacia");
        let root = dir.join("papelera");
        let trash = TrashService::new(&root, Local::now()).expect("papelera");
        let folder = trash.folder().to_path_buf();
        trash.save_manifest();
        assert!(!folder.exists());

        fs::remove_dir_all(&dir).expect("limpiar");
    }

    fn find_file(root: &Path, name: &str) -> Option<PathBuf> {
        let mut stack = vec![root.to_path_buf()];
        while let Some(dir) = stack.pop() {
            for entry in fs::read_dir(dir).ok()?.flatten() {
                let path = entry.path();
                if path.is_dir() {
                    stack.push(path);
                } else if path.file_name().is_some_and(|file| file == name) {
                    return Some(path);
                }
            }
        }
        None
    }
}
