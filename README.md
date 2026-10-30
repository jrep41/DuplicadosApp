# Buscador de Archivos Duplicados (C# / WPF)

Aplicación de escritorio para Windows que busca archivos duplicados en una carpeta
y permite eliminarlos de forma segura. Es un port a C# con interfaz WPF de la lógica
de [`duplica2.py`](../duplica2.py).

## Requisitos

- .NET 10 SDK (o superior) en Windows.

## Compilar y ejecutar

```powershell
cd DuplicadosApp
dotnet build
dotnet run
```

Para generar un ejecutable autónomo:

```powershell
dotnet publish -c Release -r win-x64 --self-contained false
```

## Cómo funciona

El proceso recorre la carpeta **una sola vez**: cada archivo se mide en cuanto el
recorrido lo entrega y se agrupa por tamaño al vuelo.

1. **Recorrido y medición (una pasada)**: se enumeran las carpetas y, por cada
   archivo, se obtiene su tamaño y su identidad (volumen + índice, equivalente a
   `st_dev`/`st_ino` en Python). Los errores de acceso se registran y no detienen
   el escaneo.
2. **Confirmación**: se agrupan los archivos por tamaño y luego:
   - **Modo nombre y tamaño** (por defecto): se agrupan por nombre de archivo
     sin distinguir mayúsculas.
   - **Modo byte a byte**: se compara el contenido real de cada archivo contra el
     primer representante de cada subgrupo, sin calcular hashes.

Los archivos que no comparten tamaño con ningún otro se descartan sin leerlos.

## Rendimiento

Puntos que importan con carpetas grandes, y cómo están resueltos:

| Situación | Cómo se maneja |
|-----------|----------------|
| Miles de líneas en el panel Actividad | La vista **añade** cada línea al final; el registro no cuesta más cuanto más largo es. Se recortan las líneas más antiguas al pasar de 512 KB. |
| Cientos de grupos de resultados | La lista está **virtualizada** (`VirtualizationMode="Recycling"`): solo se crean los elementos visibles. |
| Clic en una casilla con miles de resultados | Cada casilla informa su propio delta; no se recorre la lista entera. |
| Muchos avisos de progreso por segundo | Solo se publica el último estado pendiente, en lugar de uno por aviso. |
| Carpetas con decenas de miles de entradas | El listado se enumera de forma perezosa. |
| Cancelar a mitad de una fase | El token se comprueba en el recorrido, al agrupar por nombre, al comparar contenidos y grupo a grupo al verificar la identidad. |

## Cancelación

«Cancelar» se atiende en todas las fases, no solo en el recorrido de carpetas. La
comprobación del token se hace:

- en cada archivo del recorrido y en cada carpeta leída;
- dentro del agrupado por nombre (un mismo tamaño puede tener cientos de miles de
  archivos);
- dentro de la comparación de contenido, en cada bloque leído, no solo al terminar de
  comparar un par;
- **grupo a grupo y archivo a archivo en la verificación de identidad**, que es la fase
  más larga cuando hay muchos duplicados.

Al cancelar, la búsqueda se detiene y la ventana vuelve al estado de arranque (sin
carpeta, sin resultados, sin progreso y con el panel de actividad vacío), igual que
hace el script original al interrumpirlo.

## Seguridad del borrado

El borrado **nunca destruye archivos**. Se resuelve en dos niveles:

1. **Papelera de reciclaje de Windows** (`IFileOperation`), la misma operación que usa
   el Explorador. El envío **se verifica** leyendo los metadatos de la Papelera: solo se
   da por bueno el archivo que aparece allí. Si el archivo desapareció de su ruta pero
   no se localiza en la Papelera, no se afirma que se pueda restaurar: se informa aparte
   como «sin verificar», sin contarlo como error de borrado.
2. **Papelera propia de la aplicación**, para cuando la del sistema no está
   disponible. Ocurre, por ejemplo, dentro de un AppContainer: el shell no puede
   escribir en la Papelera y borraría el archivo para siempre. En ese caso los archivos
   se mueven a:

   ```
   %LOCALAPPDATA%\DuplicadosApp\Borrados\<fecha-hora>\
   ```

   conservando su estructura de carpetas, y se escribe un `manifiesto.txt` con la ruta
   original de cada uno. Cada tanda usa una carpeta propia: si ya existe una con la
   misma marca de tiempo se añade un sufijo (`-2`, `-3`…) y el manifiesto se amplía en
   lugar de sobrescribirse. Desde la ventana, el botón **Abrir papelera de la
   aplicación** lleva a esa carpeta.

Si el archivo no se puede guardar en ninguno de los dos sitios, **no se borra** y se
cuenta como error. Nunca se usa `File.Delete` sobre los archivos del usuario.

Antes de eliminar cada archivo se revalida que:

- siga siendo un archivo regular,
- su identidad (volumen + índice) y su tamaño no hayan cambiado,
- su contenido siga coincidiendo con el resto del grupo (en modo byte a byte) o
  su nombre siga siendo el mismo (en modo nombre y tamaño).

Si alguna comprobación falla, el archivo **no** se toca y se cuenta como error. El
resultado de un borrado distingue tres desenlaces por archivo: movido y verificado en
la papelera, movido sin poder verificar el destino, y no movido. Tras un borrado
correcto se relanza la búsqueda automáticamente para refrescar los resultados, y los
archivos que ya no existen se retiran de la lista aunque el borrado se cancele a medias.

## Interfaz

- **Seleccionar carpeta**: elige la ruta donde buscar. Al elegir una carpeta nueva,
  vacía el panel de actividad y devuelve las dos barras de progreso al reposo (0 %),
  para no mezclar el resultado anterior con la búsqueda que se va a lanzar.
- **Comparar byte a byte**: activa la comparación por contenido.
- **Modo detallado**: muestra u oculta el panel de actividad.
- **Buscar duplicados**: inicia el escaneo en segundo plano.
- **Seleccionar copias (automático)**: marca todas las copias dejando el primer
  archivo de cada grupo sin marcar.
- **Quitar selección**: desmarca todo.
- **Pausar / Reanudar**: detiene temporalmente el trabajo en curso.
- **Cancelar**: aborta el escaneo o el borrado en curso. Se atiende en cualquier fase
  (recorrido, agrupado por nombre, comparación de contenido y verificación de
  identidad), así que no hay que esperar a que termine la fase en curso.
- **Eliminar archivos seleccionados**: envía los archivos marcados a la papelera tras
  confirmar.
- **Abrir papelera de la aplicación**: abre la carpeta donde se guardan los archivos
  cuando la Papelera de Windows no está disponible.

## Comprobar el envío a la Papelera

La Papelera no es accesible desde todos los entornos: dentro de un *AppContainer*
(procesos enjaulados) Windows no deja escribir en ella y el borrado acaba siendo
permanente y silencioso. Por eso el envío se verifica y la aplicación **se niega a
borrar** cuando la Papelera no está disponible.

Aviso importante: si esta comprobación se ejecuta desde una terminal que a su vez está
enjaulada (por ejemplo, la de un agente automatizado), da un **falso negativo**. Hay
que hacerlo desde una ventana de PowerShell normal del usuario.

```powershell
# 1. Cuenta lo que hay ahora en la Papelera
$p = "G:\`$Recycle.Bin\$(([System.Security.Principal.WindowsIdentity]::GetCurrent()).User.Value)"
"antes: " + (Get-ChildItem -LiteralPath $p -Force -File | Where-Object Name -like '$R*').Count

# 2. Envía un archivo de prueba con la misma operación que usa la aplicación
$f = "$env:TEMP\prueba-papelera.bin"
[IO.File]::WriteAllBytes($f, [byte[]]::new(1024))
Add-Type -AssemblyName Microsoft.VisualBasic
[Microsoft.VisualBasic.FileIO.FileSystem]::DeleteFile($f,'OnlyErrorDialogs','SendToRecycleBin')

# 3. Vuelve a contar: debe haber uno más
"despues: " + (Get-ChildItem -LiteralPath $p -Force -File | Where-Object Name -like '$R*').Count
```

Si el segundo número es mayor, tu entorno recicla y la aplicación también lo hará. En
cambio, si el número no cambia, esa terminal no puede reciclar y el resultado no dice
nada sobre la aplicación: pruébala directamente.

La comprobación más directa es usar la aplicación: si al borrar avisa de que la
Papelera no está disponible, es que ese entorno no puede reciclar (y no se habrá
borrado nada).

## Estructura del proyecto

| Ruta | Contenido |
|------|-----------|
| [`Models/`](Models) | Tipos de datos: identidad de archivo, grupos, resultados. |
| [`Services/FileSystemService.cs`](Services/FileSystemService.cs) | Identidad, tamaño, cabecera de 8 KB y comparación byte a byte. |
| [`Services/DuplicateFinder.cs`](Services/DuplicateFinder.cs) | Lógica de búsqueda de duplicados. |
| [`Services/FileDeleter.cs`](Services/FileDeleter.cs) | Borrado con revalidación previa. |
| [`ViewModels/`](ViewModels) | Estado de la interfaz y orquestación de hilos. |
| [`Themes/Palette.xaml`](Themes/Palette.xaml) | Paleta de colores, tipografía, iconos y plantillas de los controles. |
| [`MainWindow.xaml`](MainWindow.xaml) | Interfaz gráfica. |
