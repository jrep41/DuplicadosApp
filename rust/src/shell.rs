//! Abre archivos y carpetas con el shell de Windows, como un doble clic en el
//! Explorador.

use std::path::Path;

use windows::Win32::UI::Shell::ShellExecuteW;
use windows::Win32::UI::WindowsAndMessaging::SW_SHOWNORMAL;
use windows::core::{HSTRING, w};

/// Abre una ruta con la aplicación asociada a su extensión (o el Explorador para
/// carpetas). Devuelve el mensaje de error si Windows no pudo lanzarla.
pub fn open_path(path: &Path) -> Result<(), String> {
    let file = HSTRING::from(path.as_os_str());
    // SAFETY: los punteros apuntan a cadenas válidas durante la llamada.
    let result = unsafe { ShellExecuteW(None, w!("open"), &file, None, None, SW_SHOWNORMAL) };

    // ShellExecuteW devuelve un valor <= 32 cuando falla.
    if result.0 as isize > 32 {
        Ok(())
    } else {
        Err(std::io::Error::last_os_error().to_string())
    }
}
