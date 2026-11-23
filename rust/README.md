# Buscador de Archivos Duplicados (Rust / egui)

Port a Rust del original en C#/WPF. Mantiene el mismo comportamiento: recorrido en
una sola pasada, agrupación por tamaño y extensión, confirmación byte a byte,
cancelación y pausa por fases, y borrado exclusivamente a la Papelera (del sistema o
propia), con revalidación previa de cada archivo.

## Requisitos

- Windows 10 u 11.
- Rust 1.100 o superior con el toolchain `stable-x86_64-pc-windows-msvc`.
- Visual Studio Build Tools con el enlazador de MSVC (lo necesita `cargo build`).

No hace falta .NET ni WebView2.

## Compilar y ejecutar

```powershell
cd rust
cargo run --release
```

Para generar el ejecutable:

```powershell
cargo build --release
# target\release\DuplicadosApp.exe
```

## Pruebas

```powershell
cd rust
cargo test
```

Las pruebas cubren el agrupado, la comparación por cabecera y byte a byte, la
identidad de archivo, la papelera propia, la revalidación antes de borrar, la
orquestación completa de una búsqueda y la lectura de metadatos `$I`.

La prueba de extremo a extremo contra la Papelera real de Windows se ejecuta a mano,
porque necesita una sesión de escritorio:

```powershell
cargo test envia_un_archivo_de_prueba_a_la_papelera -- --ignored
```

## Cómo funciona

Es el mismo algoritmo que el original:

1. **Recorrido y medición (una pasada)**: se enumeran las carpetas sin seguir puntos
   de reanudación (enlaces y uniones) y cada archivo se agrupa por tamaño al vuelo.
   Los errores de acceso se registran sin detener el escaneo.
2. **Confirmación**: los tamaños con más de un archivo se separan por extensión
   (sin distinguir mayúsculas) y solo esos candidatos se leen:
   - se compara primero la cabecera de 8 KB en memoria;
   - si coincide y el archivo es mayor, se compara el resto byte a byte;
   - la identidad (volumen + índice) y el tamaño se revalidan al confirmar.
3. **Verificación de identidad**: se descartan los grupos que se quedan con menos de
   dos archivos válidos.
4. **Borrado**: antes de mover cada archivo se comprueba que siga siendo el mismo
   archivo (identidad y tamaño) y que algún hermano del grupo conserve el mismo
   contenido. Si algo falla, el archivo no se toca.

## Seguridad del borrado

Igual que en el original:

- Se usa `IFileOperation` con `FOF_ALLOWUNDO` para enviar a la Papelera de reciclaje.
- El envío **se verifica** leyendo el metadato `$I` de la Papelera y contando los
  `$R` antes y después. Si el archivo desapareció pero no se localiza allí, se
  informa como «sin verificar», no como borrado correcto.
- Si el entorno no puede reciclar (por ejemplo, un AppContainer), se usa la papelera
  propia en `%LOCALAPPDATA%\DuplicadosApp\Borrados\<fecha-hora>\`, conservando la
  estructura de carpetas y escribiendo `manifiesto.txt` con las rutas originales.
- Si no se puede guardar en ninguno de los dos sitios, el archivo **no se borra**.
- Nunca se usa un borrado permanente sobre archivos del usuario.

La sonda de disponibilidad de la Papelera envía un archivo de 64 bytes y limpia su
par `$I`/`$R`. Si se ejecuta desde una terminal enjaulada (por ejemplo, la de un
agente automatizado), puede dar un falso negativo; pruébala desde una sesión normal.

## Estructura

| Ruta | Contenido |
|------|-----------|
| [`src/model.rs`](src/model.rs) | Tipos compartidos, control de pausa/cancelación y formato de tamaños. |
| [`src/fs_service.rs`](src/fs_service.rs) | Identidad, tamaño, cabecera de 8 KB y comparación byte a byte. |
| [`src/finder.rs`](src/finder.rs) | Lógica de búsqueda y fases de progreso. |
| [`src/recycle_bin.rs`](src/recycle_bin.rs) | Envío real a la Papelera de Windows y verificación de `$I`/`$R`. |
| [`src/trash.rs`](src/trash.rs) | Papelera propia, manifiesto y movimiento entre volúmenes. |
| [`src/deleter.rs`](src/deleter.rs) | Borrado con revalidación y verificación de hermanos. |
| [`src/shell.rs`](src/shell.rs) | Abrir archivos y carpetas con el shell de Windows. |
| [`src/app.rs`](src/app.rs) | Interfaz egui y orquestación de hilos. |
| [`src/main.rs`](src/main.rs) | Arranque de la ventana. |

## Diferencias con la versión C#/WPF

- La interfaz usa **egui** en lugar de WPF. Las etiquetas y la disposición son
  equivalentes, pero no se usan los glifos de Segoe Fluent Icons: los botones llevan
  texto.
- Cada ruta se muestra en una sola línea con elipsis y tooltip, para poder
  virtualizar la lista con altura fija. El original ajusta el texto en varias líneas.
- El panel «Actividad» está siempre visible, igual que en el código C# (la casilla
  «Modo detallado» controla la escritura de líneas, no la visibilidad del panel).
- La lista de resultados se dibuja solo para las filas visibles; los grupos y archivos
  se recorren sin crear controles por elemento.
