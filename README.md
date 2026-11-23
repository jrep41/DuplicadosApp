# Buscador de Archivos Duplicados (C# / WPF)

Aplicación de escritorio para Windows que busca archivos duplicados en una carpeta
y permite eliminarlos de forma segura, optimizada para carpetas grandes con cientos
de miles de archivos.

> **Port en Rust:** hay una versión paralela con interfaz egui en [`rust/`](rust/README.md).

## Requisitos

- .NET 10 SDK (o superior) en Windows.

## Compilar, probar y ejecutar

```powershell
dotnet build DuplicadosApp.slnx
dotnet test DuplicadosApp.Tests/DuplicadosApp.Tests.csproj
dotnet run --project DuplicadosApp.csproj
```

Para generar un ejecutable optimizado:

```powershell
dotnet publish DuplicadosApp.csproj -c Release -r win-x64 --self-contained false
```

## Arquitectura y optimizaciones de rendimiento

El motor está diseñado para minimizar lecturas de disco, asignaciones de memoria y trabajo en el hilo de interfaz:

1. **Recorrido paralelo sin fugas ni descargas inesperadas** (`FileSystemService.ScanDirectoryParallel`):
   - Recorre el árbol de directorios en paralelo con una cola de trabajo (`2–4` hilos) y búferes de 64 KiB.
   - Evita bucles en enlaces simbólicos/uniones y omite por defecto archivos solo en la nube (OneDrive Files On-Demand) para no disparar descargas involuntarias.
   - Agrupación compacta por clave `(tamaño, extensión)` sin reservar listas para tamaños únicos.
2. **Motor multietapa con confirmación exacta byte a byte** (`DuplicateFinder`):
   - **Planificación LPT**: procesa primero los grupos de mayor carga estimada (`tamaño × copias`) con paralelismo acotado (`2–8` hilos) para evitar cuellos de botella de cola y saturación de disco.
   - **Huella rápida en una sola apertura**: en una única llamada obtiene `FileIdentity` (volumen + índice), fecha de modificación y calcula el SHA-256 de la cabecera (8 KiB) + cola (8 KiB) + muestra central (4 KiB si ≥ 64 KiB), o del archivo completo si mide ≤ 16 KiB.
   - **Deduplicación de enlaces duros (hard links)**: dos rutas con la misma `FileIdentity` apuntan al mismo archivo físico y se omiten para no informar espacio falso ni comprometer copias.
   - **Complejidad O(M) para grupos numerosos**: cubetas con 2 archivos comparan directamente byte a byte el rango aún no leído (`[8 KiB .. tamaño - 8 KiB)`); cubetas con 3+ archivos calculan SHA-256 en flujo una vez por archivo y confirman byte a byte cada subgrupo coincidente.
3. **Pausa y cancelación instantáneas** (`PauseGate`):
   - Basadas en `ManualResetEventSlim` + `CancellationToken` (0 % CPU en pausa, despertar inmediato incluso en mitad de comparaciones de archivos grandes).
4. **Interfaz fluida con miles de grupos**:
   - Carga masiva en un solo `Reset` (`BulkObservableCollection<T>`), lista virtualizada con reciclaje sin capa redundante de `PropertyGroupDescription`, filtrado/ordenación instantáneos y registro de actividad por lotes con recorte en O(1).

## Seguridad del borrado

El borrado **nunca destruye archivos** (`File.Delete` nunca se invoca sobre archivos del usuario):

1. **Papelera de reciclaje de Windows** (`RecycleBin`):
   - Ejecuta `IFileOperation` en un hilo **STA dedicado** por sesión y verifica la llegada de cada archivo leyendo los metadatos `$I*` en O(1) (manteniendo registro de metadatos previos).
   - Comprueba y recuerda la disponibilidad **por volumen** (no confunde discos locales con unidades USB/red).
2. **Papelera propia de la aplicación** (`TrashService`):
   - Respaldo automático en `%LOCALAPPDATA%\DuplicadosApp\Borrados\<fecha-hora>\` cuando la Papelera de Windows no está disponible.
   - Gestiona atributos `ReadOnly`, colisiones de nombre y rutas largas (`_rutas_largas`), guardando siempre la ruta original en `manifiesto.txt`.
3. **Revalidación previa** (`FileDeleter`):
   - Antes de mover cada archivo revalida su identidad, tamaño y que conserve al menos un hermano distinto e intacto con el mismo contenido byte a byte.

## Interfaz y utilidades

- **Arrastrar y soltar** carpetas directamente sobre la ventana, o pegar la ruta y pulsar `Enter`.
- **Tema claro y tema oscuro** con detección automática del tema de Windows y sincronización de la barra de título nativa.
- **Opciones configurables**: ignorar archivos vacíos (0 B), exigir misma extensión, omitir ocultos/sistema y registro detallado.
- **Filtro instantáneo (`Ctrl+F`)** y **ordenación** (mayor espacio recuperable, mayor tamaño, más copias, nombre).
- **Estrategias de selección inteligente**: conservar el más antiguo, el más reciente, la ruta más corta o el primero; más **«Conservar solo este»** desde el menú contextual.
- **Acciones por archivo**: doble clic para abrir, botón/menú para mostrar en el Explorador, copiar ruta y **Exportar informe CSV**.
- **Atajos**: `Ctrl+O` carpeta · `F5` buscar · `Ctrl+F` filtrar · `Esc` cancelar · `Supr` eliminar seleccionados.
