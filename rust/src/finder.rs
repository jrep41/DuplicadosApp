//! Búsqueda de duplicados, portada de `duplica2.py` y del original en C#.

use std::collections::HashMap;
use std::fs;
use std::path::Path;
use std::sync::Mutex;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::thread;
use std::time::Instant;

use crate::fs_service::{self, HEADER_SIZE};
use crate::model::{
    Control, DuplicateGroup, EngineEvent, EventSink, FileEntry, PhaseProgress, ScanProgress,
    ScanResult, format_size_short,
};

const COMPARISON_TEXT: &str = "Comparando contenidos…";

/// Contadores compartidos por la fase de comparación de contenido.
struct CompareCounters<'a> {
    entries_processed: &'a AtomicUsize,
    total_entries: usize,
    errors: &'a AtomicUsize,
    phase_name: &'a str,
}

/// Lógica de búsqueda de duplicados.
pub struct DuplicateFinder {
    events: EventSink,
    control: Control,
}

impl DuplicateFinder {
    pub fn new(events: EventSink, control: Control) -> Self {
        Self { events, control }
    }

    pub fn run(&self, folder: &Path) -> ScanResult {
        let mut result = ScanResult::default();
        let start = Instant::now();
        let mut errors = 0usize;

        self.activity("Preparando el escaneo");
        self.phase("Preparando el escaneo", 0.0, true);
        self.log(format!("Iniciando búsqueda en: {}", folder.display()));
        self.log("Modo: mismo tamaño y misma extensión; el contenido se confirma byte a byte");

        let mut by_size: HashMap<u64, Vec<FileEntry>> = HashMap::new();
        let mut folders = 0usize;
        let mut total = 0usize;
        let mut log_every = 200usize;

        let mut stack: Vec<fs::ReadDir> = Vec::new();
        match fs::read_dir(folder) {
            Ok(reader) => stack.push(reader),
            Err(error) => {
                errors += 1;
                self.log(format!(
                    "⚠ No se pudo leer el contenido de {}: {error}",
                    folder.display()
                ));
            }
        }

        while !stack.is_empty() {
            if self.control.is_cancelled() {
                result.cancelled = true;
                result.errors = errors;
                return result;
            }

            self.control.wait_while_paused();

            let next = match stack.last_mut() {
                Some(reader) => reader.next(),
                None => break,
            };

            match next {
                Some(Ok(entry)) => {
                    let path = entry.path();
                    let file_type = match entry.file_type() {
                        Ok(file_type) => file_type,
                        Err(error) => {
                            errors += 1;
                            self.log(format!("⚠ No se pudo leer {}: {error}", path.display()));
                            continue;
                        }
                    };

                    if file_type.is_dir() {
                        // Los puntos de reanudación (enlaces y uniones) no se recorren:
                        // podrían formar ciclos o duplicar el mismo árbol.
                        if fs_service::is_reparse_point(&path) {
                            continue;
                        }

                        folders += 1;

                        self.activity(format!("Recorriendo {}", path.display()));
                        self.phase("Recorriendo carpetas", 0.0, true);
                        self.progress(
                            (folders / 20).min(45) as f64,
                            format!(
                                "Recorriendo carpetas… {folders} carpetas, {total} archivos ({})",
                                elapsed_text(start)
                            ),
                        );

                        if folders >= log_every {
                            self.log(format!("Leyendo {}", path.display()));
                            self.log(format!(
                                "Recorridas {folders} carpetas, {total} archivos encontrados"
                            ));
                            log_every += 200;
                        }

                        match fs::read_dir(&path) {
                            Ok(reader) => stack.push(reader),
                            Err(error) => {
                                errors += 1;
                                self.log(format!("⚠ No se pudo leer {}: {error}", path.display()));
                            }
                        }
                    } else {
                        let metadata = match entry.metadata() {
                            Ok(metadata) => metadata,
                            Err(error) => {
                                errors += 1;
                                self.log(format!("⚠ No se pudo leer {}: {error}", path.display()));
                                continue;
                            }
                        };

                        if metadata.is_dir() {
                            continue;
                        }

                        let size = metadata.len();
                        total += 1;
                        by_size.entry(size).or_default().push(FileEntry {
                            path,
                            size,
                            identity: None,
                        });

                        if total.is_multiple_of(5000) {
                            self.progress(
                                (folders / 20).min(45) as f64,
                                format!(
                                    "Agrupando por tamaño… {total} archivos ({})",
                                    elapsed_text(start)
                                ),
                            );
                            self.log(format!(
                                "Leídos {total} archivos ({} tamaños distintos, {errors} errores)",
                                by_size.len()
                            ));
                        }
                    }
                }
                Some(Err(error)) => {
                    errors += 1;
                    self.log(format!("⚠ No se pudo leer una entrada de carpeta: {error}"));
                }
                None => {
                    stack.pop();
                }
            }
        }

        if self.control.is_cancelled() {
            result.cancelled = true;
            result.errors = errors;
            return result;
        }

        self.log(format!(
            "Recorrido terminado: {folders} carpetas y {total} archivos en {}",
            elapsed_text(start)
        ));
        self.activity(format!("Agrupando por tamaño {total} archivos"));
        self.phase(
            format!("Agrupando por tamaño ({total} archivos)"),
            0.0,
            true,
        );
        self.progress(
            45.0,
            format!("Leídos {total} archivos, agrupando por tamaño…"),
        );

        self.log(if errors > 0 {
            format!(
                "⚠ Tamaños agrupados: {} tamaños distintos, {errors} archivo(s) con error",
                by_size.len()
            )
        } else {
            format!(
                "Tamaños agrupados: {} tamaños distintos, 0 archivos con error",
                by_size.len()
            )
        });

        let mut candidates: Vec<(u64, Vec<FileEntry>)> = by_size
            .into_iter()
            .filter(|(_, entries)| entries.len() > 1)
            .collect();
        candidates.sort_by_key(|(size, _)| *size);

        let total_groups = candidates.len();
        let total_entries: usize = candidates.iter().map(|(_, entries)| entries.len()).sum();
        let phase_name = COMPARISON_TEXT.trim_end_matches('…');

        self.log(format!(
            "{total_groups} tamaño(s) con más de un archivo; comparando contenidos"
        ));

        let errors_comparison = AtomicUsize::new(0);
        let groups_processed = AtomicUsize::new(0);
        let entries_processed = AtomicUsize::new(0);
        let collected: Mutex<Vec<(usize, Vec<DuplicateGroup>)>> = Mutex::new(Vec::new());

        if total_groups > 0 {
            self.phase(
                format!("{phase_name} (0 de {total_entries} archivos)"),
                0.0,
                false,
            );

            let next_group = AtomicUsize::new(0);
            let worker_count = thread::available_parallelism()
                .map(|count| count.get())
                .unwrap_or(1)
                .min(total_groups);

            thread::scope(|scope| {
                for _ in 0..worker_count {
                    scope.spawn(|| {
                        loop {
                            if self.control.is_cancelled() {
                                break;
                            }

                            let index = next_group.fetch_add(1, Ordering::SeqCst);
                            if index >= candidates.len() {
                                break;
                            }

                            self.control.wait_while_paused();
                            let (size, entries) = &candidates[index];
                            self.activity(format!(
                                "{COMPARISON_TEXT} tamaño {} ({} archivos)",
                                format_size_short(*size),
                                entries.len()
                            ));

                            let counters = CompareCounters {
                                entries_processed: &entries_processed,
                                total_entries,
                                errors: &errors_comparison,
                                phase_name,
                            };
                            let mut partial = Vec::new();
                            for same_extension in group_entries_by_extension(entries) {
                                if self.control.is_cancelled() {
                                    break;
                                }

                                if same_extension.len() < 2 {
                                    for _ in 0..same_extension.len() {
                                        let processed =
                                            entries_processed.fetch_add(1, Ordering::SeqCst) + 1;
                                        self.report_entry_progress(
                                            processed,
                                            total_entries,
                                            phase_name,
                                        );
                                    }
                                    continue;
                                }

                                self.confirm_by_content(
                                    *size,
                                    &same_extension,
                                    &mut partial,
                                    &counters,
                                );

                                if self.control.is_cancelled() {
                                    break;
                                }
                            }

                            if !partial.is_empty() {
                                let mut guard = collected
                                    .lock()
                                    .unwrap_or_else(|poisoned| poisoned.into_inner());
                                guard.push((index, partial));
                            }

                            let processed = groups_processed.fetch_add(1, Ordering::SeqCst) + 1;
                            self.report_group_progress(
                                processed,
                                total_groups,
                                COMPARISON_TEXT,
                                start,
                            );
                        }
                    });
                }
            });
        }

        if self.control.is_cancelled() {
            result.cancelled = true;
            result.errors = errors + errors_comparison.load(Ordering::SeqCst);
            return result;
        }

        errors += errors_comparison.load(Ordering::SeqCst);
        let mut collected = collected
            .into_inner()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        collected.sort_by_key(|(index, _)| *index);
        let mut groups: Vec<DuplicateGroup> = collected
            .into_iter()
            .flat_map(|(_, groups)| groups)
            .collect();

        self.activity(format!(
            "Verificando identidad de {} grupo(s)",
            groups.len()
        ));
        self.phase(
            format!("Verificando identidad (0 de {} grupos)", groups.len()),
            0.0,
            false,
        );

        let total_result_groups = groups.len();
        let mut done = 0usize;
        for index in (0..groups.len()).rev() {
            if self.control.is_cancelled() {
                result.cancelled = true;
                result.errors = errors;
                return result;
            }

            self.control.wait_while_paused();
            let group = &groups[index];
            let mut resolved = Vec::with_capacity(group.files.len());
            for file in &group.files {
                if self.control.is_cancelled() {
                    result.cancelled = true;
                    result.errors = errors;
                    return result;
                }

                // La identidad ya se resolvió durante la confirmación de contenido.
                if file.identity.is_some() {
                    resolved.push(file.clone());
                    continue;
                }

                match fs_service::try_get_entry(&file.path) {
                    Some(entry) if entry.size == file.size => resolved.push(entry),
                    _ => errors += 1,
                }
            }

            if resolved.len() > 1 {
                groups[index].files = resolved;
            } else {
                groups.remove(index);
            }

            done += 1;
            self.activity(format!(
                "Verificando identidad de {done} de {total_result_groups} grupo(s)"
            ));
            let percentage = (done as u64 * 100 / total_result_groups.max(1) as u64) as f64;
            self.phase(
                format!("Verificando identidad ({done} de {total_result_groups} grupos)"),
                percentage,
                false,
            );
        }

        if self.control.is_cancelled() {
            result.cancelled = true;
            result.errors = errors;
            return result;
        }

        result.errors = errors;
        self.log(if errors > 0 {
            format!(
                "⚠ Búsqueda terminada en {}: {} grupo(s) de duplicados, {errors} error(es)",
                elapsed_text(start),
                groups.len()
            )
        } else {
            format!(
                "Búsqueda terminada en {}: {} grupo(s) de duplicados, sin errores",
                elapsed_text(start),
                groups.len()
            )
        });
        self.progress(100.0, "Finalizando…");
        result.groups = groups;
        result
    }

    fn confirm_by_content(
        &self,
        size: u64,
        entries: &[FileEntry],
        groups: &mut Vec<DuplicateGroup>,
        counters: &CompareCounters<'_>,
    ) {
        let header_size = HEADER_SIZE.min(size as usize);
        let mut entry_header = vec![0u8; header_size];
        let mut subgroups: Vec<(Vec<u8>, Vec<FileEntry>)> = Vec::new();

        for raw in entries {
            let processed = counters.entries_processed.fetch_add(1, Ordering::SeqCst) + 1;
            self.report_entry_progress(processed, counters.total_entries, counters.phase_name);

            if self.control.is_cancelled() {
                return;
            }

            self.control.wait_while_paused();

            let Some((entry, header_read)) =
                fs_service::try_get_entry_and_header(&raw.path, size, &mut entry_header)
            else {
                counters.errors.fetch_add(1, Ordering::SeqCst);
                continue;
            };

            if header_read != header_size {
                counters.errors.fetch_add(1, Ordering::SeqCst);
                continue;
            }

            let mut matched = None;
            for (index, (candidate_header, candidate_files)) in subgroups.iter().enumerate() {
                if self.control.is_cancelled() {
                    return;
                }

                // Descarte en memoria sin lecturas de disco.
                if entry_header[..header_size] != candidate_header[..] {
                    continue;
                }

                // Si el archivo mide <= 8 KB, la cabecera ya contiene todo el archivo.
                if size <= HEADER_SIZE as u64 {
                    matched = Some(index);
                    break;
                }

                match fs_service::compare_byte_by_byte(
                    &entry.path,
                    &candidate_files[0].path,
                    &|| self.control.is_cancelled(),
                    HEADER_SIZE as u64,
                ) {
                    Ok(true) => {
                        matched = Some(index);
                        break;
                    }
                    Ok(false) => {}
                    Err(_) => {
                        counters.errors.fetch_add(1, Ordering::SeqCst);
                    }
                }
            }

            if self.control.is_cancelled() {
                return;
            }

            if size > HEADER_SIZE as u64 {
                let Some(identity) = entry.identity else {
                    counters.errors.fetch_add(1, Ordering::SeqCst);
                    continue;
                };

                if !fs_service::matches_identity(&entry.path, identity, size) {
                    counters.errors.fetch_add(1, Ordering::SeqCst);
                    continue;
                }
            }

            match matched {
                Some(index) => subgroups[index].1.push(entry),
                None => subgroups.push((entry_header[..header_size].to_vec(), vec![entry])),
            }
        }

        for (_, files) in subgroups {
            if files.len() > 1 {
                let display_name = files[0]
                    .path
                    .file_name()
                    .map(|name| name.to_string_lossy().into_owned())
                    .unwrap_or_default();
                groups.push(DuplicateGroup {
                    size,
                    display_name,
                    files,
                });
            }
        }
    }

    fn report_entry_progress(&self, processed: usize, total: usize, phase_name: &str) {
        if !processed.is_multiple_of(128) && processed != total {
            return;
        }

        let percentage = (processed as u64 * 100 / total.max(1) as u64) as f64;
        self.phase(
            format!("{phase_name} ({processed} de {total} archivos)"),
            percentage,
            false,
        );
    }

    fn report_group_progress(
        &self,
        processed: usize,
        total: usize,
        comparison_text: &str,
        start: Instant,
    ) {
        if processed != 1 && !processed.is_multiple_of(5) && processed != total {
            return;
        }

        let percentage = 90.0 + (processed as u64 * 10 / total.max(1) as u64) as f64;
        self.progress(
            percentage,
            format!(
                "{comparison_text} {processed} de {total} ({})",
                elapsed_text(start)
            ),
        );
        self.log(format!(
            "{} grupo {processed} de {total} ({})",
            comparison_text.trim_end_matches('…'),
            elapsed_text(start)
        ));
    }

    fn log(&self, text: impl Into<String>) {
        (self.events)(EngineEvent::Log(text.into()));
    }

    fn progress(&self, percentage: f64, text: impl Into<String>) {
        (self.events)(EngineEvent::Progress(ScanProgress {
            percentage,
            text: text.into(),
        }));
    }

    fn activity(&self, text: impl Into<String>) {
        (self.events)(EngineEvent::Activity(text.into()));
    }

    fn phase(&self, phase: impl Into<String>, percentage: f64, indeterminate: bool) {
        (self.events)(EngineEvent::Phase(PhaseProgress {
            phase: phase.into(),
            percentage,
            indeterminate,
        }));
    }
}

/// Separa los candidatos de un mismo tamaño por extensión, sin distinguir mayúsculas.
/// El nombre no interviene: dos copias pueden llamarse de forma distinta.
fn group_entries_by_extension(entries: &[FileEntry]) -> Vec<Vec<FileEntry>> {
    let mut order: Vec<String> = Vec::new();
    let mut by_extension: HashMap<String, Vec<FileEntry>> = HashMap::new();

    for entry in entries {
        let extension = extension_key(&entry.path);
        if !by_extension.contains_key(&extension) {
            order.push(extension.clone());
        }
        by_extension
            .entry(extension)
            .or_default()
            .push(entry.clone());
    }

    order
        .into_iter()
        .filter_map(|extension| by_extension.remove(&extension))
        .collect()
}

/// Extensión como la devuelve `Path.GetExtension` de .NET, incluido el punto.
fn extension_key(path: &Path) -> String {
    let Some(name) = path.file_name() else {
        return String::new();
    };
    let name = name.to_string_lossy();
    match name.rfind('.') {
        Some(index) if index + 1 < name.len() => name[index..].to_lowercase(),
        _ => String::new(),
    }
}

fn elapsed_text(start: Instant) -> String {
    format!("{:.1}s", start.elapsed().as_secs_f64())
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::model::{Control, EngineEvent};
    use std::fs;
    use std::path::PathBuf;
    use std::sync::{Arc, Mutex};
    use std::time::{SystemTime, UNIX_EPOCH};

    fn temp_dir(name: &str) -> PathBuf {
        let stamp = SystemTime::now()
            .duration_since(UNIX_EPOCH)
            .expect("reloj del sistema")
            .as_nanos();
        let dir = std::env::temp_dir().join(format!(
            "duplicados-finder-{name}-{}-{stamp}",
            std::process::id()
        ));
        fs::create_dir_all(&dir).expect("crear temporal");
        dir
    }

    fn run_finder(dir: &Path) -> ScanResult {
        let events: EventSink = Arc::new(|_| {});
        let finder = DuplicateFinder::new(events, Control::new());
        finder.run(dir)
    }

    #[test]
    fn agrupa_solo_mismo_tamano_extensions_y_contenido() {
        let dir = temp_dir("agrupa");
        let sub = dir.join("sub");
        fs::create_dir_all(&sub).expect("subcarpeta");

        fs::write(dir.join("a.txt"), b"hola").expect("a");
        fs::write(sub.join("b.txt"), b"hola").expect("b");
        fs::write(dir.join("c.txt"), b"adios").expect("c");
        fs::write(dir.join("d.bin"), b"hola").expect("d");
        fs::write(dir.join("e.txt"), b"otro contenido").expect("e");

        let result = run_finder(&dir);
        assert!(!result.cancelled);
        assert_eq!(result.groups.len(), 1, "solo a.txt y sub/b.txt coinciden");
        assert_eq!(result.groups[0].size, 4);
        assert_eq!(result.groups[0].files.len(), 2);

        fs::remove_dir_all(&dir).expect("limpiar");
    }

    #[test]
    fn compara_el_resto_del_archivo_mas_alla_de_la_cabecera() {
        let dir = temp_dir("resto");
        let mut igual_a = vec![3u8; 40_000];
        let mut igual_b = igual_a.clone();
        let mut distinto = igual_a.clone();
        igual_a[30_000] = 8;
        igual_b[30_000] = 8;
        distinto[30_000] = 9;

        fs::write(dir.join("a.bin"), &igual_a).expect("a");
        fs::write(dir.join("b.bin"), &igual_b).expect("b");
        fs::write(dir.join("c.bin"), &distinto).expect("c");

        let result = run_finder(&dir);
        assert_eq!(result.groups.len(), 1);
        assert_eq!(result.groups[0].files.len(), 2);

        fs::remove_dir_all(&dir).expect("limpiar");
    }

    #[test]
    fn archivos_pequenos_se_comparan_por_cabecera() {
        let dir = temp_dir("pequenos");
        fs::write(dir.join("vacio1.dat"), b"").expect("vacio1");
        fs::write(dir.join("vacio2.dat"), b"").expect("vacio2");
        fs::write(dir.join("x.dat"), b"12345678").expect("x");
        fs::write(dir.join("y.dat"), b"12345678").expect("y");

        let result = run_finder(&dir);
        assert_eq!(result.groups.len(), 2);
        assert_eq!(result.errors, 0);

        fs::remove_dir_all(&dir).expect("limpiar");
    }

    #[test]
    fn cancelacion_antes_de_empezar() {
        let dir = temp_dir("cancelar");
        fs::write(dir.join("a.txt"), b"x").expect("a");
        fs::write(dir.join("b.txt"), b"x").expect("b");

        let control = Control::new();
        control.cancel();
        let finder = DuplicateFinder::new(Arc::new(|_| {}), control);
        let result = finder.run(&dir);
        assert!(result.cancelled);
        assert!(result.groups.is_empty());

        fs::remove_dir_all(&dir).expect("limpiar");
    }

    #[test]
    fn emite_progreso_y_finaliza_al_cien() {
        let dir = temp_dir("eventos");
        fs::write(dir.join("a.txt"), b"x").expect("a");
        fs::write(dir.join("b.txt"), b"x").expect("b");

        let logged: Arc<Mutex<Vec<EngineEvent>>> = Arc::new(Mutex::new(Vec::new()));
        let sink = {
            let logged = Arc::clone(&logged);
            Arc::new(move |event: EngineEvent| {
                logged
                    .lock()
                    .unwrap_or_else(|poisoned| poisoned.into_inner())
                    .push(event);
            })
        };
        let finder = DuplicateFinder::new(sink, Control::new());
        let result = finder.run(&dir);
        assert_eq!(result.groups.len(), 1);

        let events = logged
            .lock()
            .unwrap_or_else(|poisoned| poisoned.into_inner());
        let last_progress = events.iter().rev().find_map(|event| match event {
            EngineEvent::Progress(progress) => Some(progress.percentage),
            _ => None,
        });
        assert_eq!(last_progress, Some(100.0));

        fs::remove_dir_all(&dir).expect("limpiar");
    }
}
