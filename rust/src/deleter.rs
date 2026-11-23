//! Borrado seguro: antes de mover cada archivo se revalida que siga siendo el mismo
//! (identidad y tamaño) y que algún hermano del grupo conserve el mismo contenido.
//! Nunca se borra de forma permanente: se usa la Papelera del sistema o la propia.

use std::collections::HashMap;
use std::path::Path;
use std::time::Instant;

use chrono::Local;

use crate::fs_service;
use crate::model::{
    Control, DeleteCandidate, DeleteDestination, DeleteOutcome, EngineEvent, EventSink,
    PhaseProgress,
};
use crate::recycle_bin;
use crate::trash::TrashService;

/// Ejecuta el borrado de `selected`. `all` contiene todos los archivos de los grupos,
/// para poder comprobar que queda una copia buena de cada uno.
pub fn delete(
    events: &EventSink,
    control: &Control,
    selected: &[DeleteCandidate],
    all: &[DeleteCandidate],
    recycle_available: bool,
    app_trash_root: &Path,
) -> DeleteOutcome {
    let mut outcome = DeleteOutcome::default();
    let total = selected.len();
    if total == 0 {
        return outcome;
    }

    let start = Instant::now();
    let mut trash: Option<TrashService> = None;

    if recycle_available {
        outcome.destination = DeleteDestination::SystemRecycleBin;
        log(
            events,
            format!("Enviando {total} archivo(s) a la Papelera de reciclaje…"),
        );
    } else {
        match TrashService::new(app_trash_root, Local::now()) {
            Ok(service) => {
                log(
                    events,
                    format!(
                        "⚠ La Papelera de reciclaje no está disponible en este entorno; se usará \
                         la papelera propia de la aplicación en {}",
                        service.folder().display()
                    ),
                );
                log(
                    events,
                    format!(
                        "Enviando {total} archivo(s) a la papelera propia de la aplicación, en {}…",
                        service.folder().display()
                    ),
                );
                outcome.destination = DeleteDestination::AppTrash;
                outcome.app_trash_folder = Some(service.folder().to_path_buf());
                trash = Some(service);
            }
            Err(error) => {
                log(
                    events,
                    format!(
                        "✖ No se pudo crear la papelera propia ({error}). No se destruyen \
                         archivos sin deshacer."
                    ),
                );
                outcome.errors = total;
                return outcome;
            }
        }
    }

    // Se indexan los candidatos por grupo una sola vez para no recorrer la lista
    // entera en cada archivo durante la verificación de hermanos.
    let mut by_group: HashMap<usize, Vec<&DeleteCandidate>> = HashMap::new();
    for candidate in all {
        by_group
            .entry(candidate.group_index)
            .or_default()
            .push(candidate);
    }

    activity(
        events,
        format!("Preparando el envío de {total} archivo(s) a la papelera"),
    );
    let destination_name = if outcome.destination == DeleteDestination::AppTrash {
        "papelera de la aplicación"
    } else {
        "Papelera de reciclaje"
    };
    phase(
        events,
        format!("Enviando a la {destination_name} (0 de {total})"),
        0.0,
    );

    for index in 1..=total {
        if control.is_cancelled() {
            outcome.cancelled = true;
            break;
        }

        control.wait_while_paused();
        let item = &selected[index - 1];
        let name = item
            .path
            .file_name()
            .map(|name| name.to_string_lossy().into_owned())
            .unwrap_or_default();
        let before = (index - 1) as f64 * 100.0 / total.max(1) as f64;
        phase(
            events,
            format!("Comprobando «{name}» ({index} de {total})"),
            before,
        );
        activity(events, format!("Comprobando «{name}» ({index} de {total})"));

        let Some(identity) = item.identity else {
            outcome.errors += 1;
            continue;
        };

        let current = fs_service::try_get_entry(&item.path);
        let valid = current
            .as_ref()
            .is_some_and(|entry| entry.identity == Some(identity) && entry.size == item.size);
        if !valid {
            outcome.errors += 1;
            continue;
        }

        // Solo se envía a la papelera si algún hermano del grupo sigue teniendo el
        // mismo contenido: así nunca se pierde la última copia buena.
        if !has_matching_sibling(item, &by_group, control)
            || !fs_service::matches_identity(&item.path, identity, item.size)
        {
            outcome.errors += 1;
            continue;
        }

        activity(events, format!("Guardando «{name}» ({index} de {total})"));
        phase(
            events,
            format!("Guardando «{name}» ({index} de {total})"),
            before,
        );

        // None = el archivo sigue en su sitio; Some(true) = movido y verificado;
        // Some(false) = movido pero sin poder confirmar dónde.
        let moved = match trash.as_mut() {
            Some(service) => {
                if service.save(&item.path) {
                    Some(true)
                } else {
                    None
                }
            }
            None => recycle_bin::move_to_recycle_bin(&item.path),
        };

        match moved {
            None => outcome.errors += 1,
            Some(verified) => {
                outcome.moved.push(item.path.clone());
                if verified {
                    outcome.deleted += 1;
                } else {
                    outcome.unverified += 1;
                }
            }
        }

        if index % 25 == 0 || index == total {
            log(
                events,
                format!(
                    "Guardados {index} de {total} ({} verificados, {} sin verificar, {} errores, {:.1}s)",
                    outcome.deleted,
                    outcome.unverified,
                    outcome.errors,
                    start.elapsed().as_secs_f64()
                ),
            );
        }
    }

    // Se guarda siempre: cada archivo ya movido necesita su ruta original en el
    // manifiesto para poder volver a su sitio.
    if let Some(service) = trash.as_mut() {
        service.save_manifest();
    }

    activity(events, "Guardando el manifiesto de la papelera");
    phase(events, "Guardando el manifiesto de la papelera", 100.0);

    let destination_name = if outcome.destination == DeleteDestination::AppTrash {
        "papelera de la aplicación"
    } else {
        "Papelera de reciclaje"
    };
    log(
        events,
        if outcome.errors > 0 || outcome.unverified > 0 {
            format!(
                "⚠ Envío a la {destination_name} terminado: {} verificado(s), {} sin verificar, \
                 {} error(es) en {:.1}s",
                outcome.deleted,
                outcome.unverified,
                outcome.errors,
                start.elapsed().as_secs_f64()
            )
        } else {
            format!(
                "Envío a la {destination_name} terminado: {} movido(s), sin errores en {:.1}s",
                outcome.deleted,
                start.elapsed().as_secs_f64()
            )
        },
    );

    outcome
}

fn has_matching_sibling(
    item: &DeleteCandidate,
    by_group: &HashMap<usize, Vec<&DeleteCandidate>>,
    control: &Control,
) -> bool {
    let Some(siblings) = by_group.get(&item.group_index) else {
        return false;
    };

    for other in siblings {
        if control.is_cancelled() {
            return false;
        }

        if other.path == item.path || other.size != item.size {
            continue;
        }

        // Si un hermano no se puede comparar, se prueba el siguiente. Si ninguno se
        // puede comparar, el archivo no se borra.
        if let Ok(true) =
            fs_service::compare_byte_by_byte(&item.path, &other.path, &|| control.is_cancelled(), 0)
        {
            return true;
        }
    }

    false
}

fn log(events: &EventSink, text: impl Into<String>) {
    (events)(EngineEvent::Log(text.into()));
}

fn activity(events: &EventSink, text: impl Into<String>) {
    (events)(EngineEvent::Activity(text.into()));
}

fn phase(events: &EventSink, phase: impl Into<String>, percentage: f64) {
    (events)(EngineEvent::Phase(PhaseProgress {
        phase: phase.into(),
        percentage,
        indeterminate: false,
    }));
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::{Control, DeleteCandidate};
    use std::fs;
    use std::path::{Path, PathBuf};
    use std::sync::Arc;
    use std::time::{SystemTime, UNIX_EPOCH};

    fn temp_dir(name: &str) -> PathBuf {
        let stamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("reloj del sistema")
            .as_nanos();
        let dir = std::env::temp_dir().join(format!(
            "duplicados-delete-{name}-{}-{stamp}",
            std::process::id()
        ));
        fs::create_dir_all(&dir).expect("crear temporal");
        dir
    }

    fn candidate(path: &Path, group_index: usize) -> DeleteCandidate {
        let entry = fs_service::try_get_entry(path).expect("entrada");
        DeleteCandidate {
            path: entry.path,
            size: entry.size,
            identity: entry.identity,
            group_index,
        }
    }

    fn no_events() -> EventSink {
        Arc::new(|_| {})
    }

    #[test]
    fn borra_una_copia_si_queda_otra_buena() {
        let dir = temp_dir("hermano");
        let first = dir.join("a.txt");
        let second = dir.join("b.txt");
        fs::write(&first, b"mismo contenido").expect("a");
        fs::write(&second, b"mismo contenido").expect("b");

        let selected = vec![candidate(&first, 0)];
        let all = vec![candidate(&first, 0), candidate(&second, 0)];
        let trash_root = dir.join("papelera");

        let outcome = delete(
            &no_events(),
            &Control::new(),
            &selected,
            &all,
            false,
            &trash_root,
        );

        assert_eq!(outcome.deleted, 1, "debe moverse una copia");
        assert_eq!(outcome.errors, 0);
        assert!(!first.exists());
        assert!(second.exists(), "la copia buena debe conservarse");
        assert_eq!(outcome.moved.len(), 1);

        let folder = outcome.app_trash_folder.expect("carpeta de papelera");
        assert!(folder.join("manifiesto.txt").is_file());

        fs::remove_dir_all(&dir).expect("limpiar");
    }

    #[test]
    fn no_borra_si_no_queda_ningun_hermano() {
        let dir = temp_dir("sin-hermano");
        let file = dir.join("a.txt");
        fs::write(&file, b"contenido").expect("escribir");

        let selected = vec![candidate(&file, 0)];
        let all = vec![candidate(&file, 0)];

        let outcome = delete(
            &no_events(),
            &Control::new(),
            &selected,
            &all,
            false,
            &dir.join("papelera"),
        );

        assert_eq!(outcome.deleted, 0);
        assert_eq!(outcome.errors, 1);
        assert!(file.exists(), "el archivo no debe tocarse");

        fs::remove_dir_all(&dir).expect("limpiar");
    }

    #[test]
    fn no_borra_si_el_contenido_cambio() {
        let dir = temp_dir("cambio");
        let first = dir.join("a.txt");
        let second = dir.join("b.txt");
        fs::write(&first, b"contenido original").expect("a");
        fs::write(&second, b"contenido distinto").expect("b");

        let selected = vec![candidate(&first, 0)];
        let all = vec![candidate(&first, 0), candidate(&second, 0)];

        let outcome = delete(
            &no_events(),
            &Control::new(),
            &selected,
            &all,
            false,
            &dir.join("papelera"),
        );

        assert_eq!(outcome.deleted, 0);
        assert_eq!(outcome.errors, 1);
        assert!(first.exists());

        fs::remove_dir_all(&dir).expect("limpiar");
    }

    #[test]
    fn cancelado_antes_de_empezar_no_toca_nada() {
        let dir = temp_dir("cancelado");
        let file = dir.join("a.txt");
        fs::write(&file, b"x").expect("escribir");
        let selected = vec![candidate(&file, 0)];

        let control = Control::new();
        control.cancel();
        let outcome = delete(
            &no_events(),
            &control,
            &selected,
            &selected,
            false,
            &dir.join("papelera"),
        );

        assert!(outcome.cancelled);
        assert!(file.exists());

        fs::remove_dir_all(&dir).expect("limpiar");
    }
}
