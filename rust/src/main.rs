#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

mod app;
mod deleter;
mod finder;
mod fs_service;
mod model;
mod recycle_bin;
mod shell;
mod trash;

fn main() -> eframe::Result {
    let options = eframe::NativeOptions {
        viewport: eframe::egui::ViewportBuilder::default()
            .with_inner_size([1000.0, 760.0])
            .with_min_inner_size([820.0, 560.0])
            .with_title("Buscador de Archivos Duplicados"),
        ..Default::default()
    };

    eframe::run_native(
        "Buscador de Archivos Duplicados",
        options,
        Box::new(|creation| Ok(Box::new(app::DuplicadosApp::new(creation)))),
    )
}
