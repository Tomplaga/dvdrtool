# dvdrtool 3.0 — reconstrucción limpia

Esta versión se reconstruyó tomando como base únicamente el motor estable que ya
había grabado correctamente y las funciones que se fueron pidiendo.

## Incluido

- **Grabar Audio**: Audio CD (CD-DA).
- **Datos CD/DVD**: disco de datos ISO9660/Joliet, sin carpetas predeterminadas.
- **Crear CD Extra**: primera sesión de Audio CD + segunda sesión de datos.
- En CD Extra se conservan `AUDIO`, `CDPLUS` y `PICTURES`.
- Barra de capacidad automática, sin botón de comprobar.
- Línea **Falta para llenar / EXCESO**.
- **Quitar todas las canciones** en Audio y CD Extra.
- Soporte universal de audio mediante FFmpeg.
- Instalación automática de FFmpeg/ffprobe si hace falta.
- cdrtools portable/autoinstalable, incluyendo runtime Cygwin.
- Soporte de CD y DVD de datos.
- Icono DVD-R.
- Registro completo de comandos y errores.

## Velocidad

La velocidad predeterminada es **4x**.

Cuando seleccionas manualmente 4x, dvdrtool envía:

```text
speed=4
```

Si la grabadora anuncia la capacidad `FORCESPEED`, dvdrtool añade además:

```text
driveropts=forcespeed
```

Si BURN-Free también está disponible, ambas opciones se combinan:

```text
driveropts=burnfree,forcespeed
```

Esto es distinto de las versiones anteriores, que solo solicitaban `speed=4`.
El firmware de algunas grabadoras todavía puede rechazar una velocidad que
físicamente no soporte para el medio insertado.

## Interfaz

Se eliminó de la barra lateral el bloque visible:

```text
UNIDAD ÓPTICA
H: · Sin disco
```

La detección de la unidad sigue funcionando internamente.

## Portable

dvdrtool puede instalar automáticamente cdrtools y FFmpeg. También puede usar:

```text
tools\cdrtools\cdrecord.exe
tools\cdrtools\mkisofs.exe
tools\cygwin\cygwin1.dll
tools\ffmpeg\ffmpeg.exe
tools\ffmpeg\ffprobe.exe
```

## Compilar

Requiere .NET 8 SDK en Windows:

```bat
build-release.bat
```

Para publicación x64 autocontenida:

```bat
publish-win-x64.bat
```


## Protección de velocidad exacta — 3.0.1

La selección manual (por defecto **4x**) se envía a `cdrecord` como `speed=4`.

Antes de que `cdrecord` termine su cuenta regresiva y empiece a escribir,
dvdrtool comprueba la velocidad que el propio `cdrecord` anuncia en:

```text
Starting to write ... at speed N ...
```

Si `N` no coincide con la velocidad pedida, dvdrtool detiene el proceso
antes del comienzo de la escritura. Esto evita que una selección de 4x
termine grabándose silenciosamente a 8x.

`forcespeed` ya no se activa automáticamente.


## Ventana adaptable — 3.0.3

Al iniciar, dvdrtool calcula el área útil disponible de Windows y ajusta
automáticamente el ancho y alto de la ventana.

- En pantallas pequeñas ocupa casi toda el área disponible.
- En Full HD y resoluciones superiores aprovecha más espacio.
- Respeta la barra de tareas.
- También tiene en cuenta el escalado DPI de Windows.
- El usuario puede seguir redimensionando la ventana manualmente.


## Interfaz visual renovada — 3.0.5

La versión 3.0.5 mejora únicamente la presentación de dvdrtool:

- barra lateral azul oscuro con degradado;
- tarjetas y paneles con acabado más moderno;
- mejor espaciado y jerarquía de texto;
- botones y estados más claros;
- listas/tablas más cómodas de leer;
- mantiene el diseño adaptable de la versión anterior.

El motor de grabación, cdrtools, FFmpeg, CD Extra y la protección de velocidad no
se modificaron en esta actualización visual.


## Etiquetas CD-TEXT — 3.0.6

En **Grabar Audio** y **Crear CD Extra** aparecen dos campos opcionales:

- **Artista (CD-TEXT)**
- **Nombre del disco (CD-TEXT)**

Cuando se completa al menos uno, dvdrtool crea archivos `.inf` para las pistas y
ejecuta `cdrecord` con `-useinfo -text`. El título de cada pista se toma del nombre
del archivo original.

CD-TEXT depende de que la grabadora y el reproductor sean compatibles. Si los
campos se dejan vacíos, la ruta de grabación permanece igual a versiones anteriores.


## Opciones y modo prueba — 3.0.7

La barra lateral incorpora **Opciones**.

### Modo prueba / simulación

Al activarlo, los botones de grabación cambian a **SIMULAR**. dvdrtool calcula
la duración estimada del audio, el tamaño de los datos y la capacidad aproximada,
y escribe en el Registro los comandos que se usarían.

En este modo **no se ejecuta `cdrecord` y no se escribe ningún disco**. Para la
simulación de Datos puede elegirse CD-R de 80 minutos o DVD de 4.7 GB. Audio CD
y CD Extra siempre se estiman como CD-R de 80 minutos.

### Preferencias

Se pueden guardar:

- velocidad predeterminada;
- pantalla inicial;
- modo Básico o Avanzado;
- mostrar u ocultar CD-TEXT;
- mostrar u ocultar Registro técnico;
- confirmación antes de grabar;
- modo simulación y medio simulado.

Las preferencias se guardan en:

`%LOCALAPPDATA%\dvdrtool\settings.ini`


## SDK de compilación — 3.0.8

dvdrtool apunta a `net8.0-windows` y ahora incluye `global.json` para seleccionar
un SDK **.NET 8.x** aunque haya SDK más nuevos instalados.

Comprueba tus SDK instalados con:

```bat
dotnet --list-sdks
```

Debe aparecer al menos una línea que empiece por `8.0.`.

Si solo aparece .NET 10, instala el **SDK .NET 8** y vuelve a ejecutar
`build-release.bat`.


## DVD doble capa — 3.0.9

En **Opciones > Medio usado para estimaciones de simulación** ahora están disponibles:

- CD-R 80 min / 700 MB
- DVD 4.7 GB
- DVD doble capa 8.5 GB (DVD±R DL)

La selección se usa en **Datos CD/DVD** para calcular si el proyecto cabe antes de
realizar una grabación. Audio CD y CD Extra siguen usando la capacidad de un CD-R
de 80 minutos.


## Películas Blu-ray — 3.1.0

La nueva pestaña **Películas Blu-ray** permite seleccionar y grabar directamente una
imagen `.ISO` de Blu-ray Video ya preparada. En una grabación real, dvdrtool exige
un medio BD-R/BD-RE y comprueba su estado antes de escribir.

El modo simulación permite comprobar el tamaño contra **25 GB** o **50 GB** sin
ejecutar `cdrecord`.

> Importante: esta versión no convierte un MKV/MP4 a Blu-ray Video. Para que un
> reproductor Blu-ray de salón lo reconozca, la ISO debe haber sido creada previamente
> con una estructura Blu-ray válida (normalmente BDMV/CERTIFICATE y UDF 2.50).


## Carpetas Blu-ray — 3.1.1

**Películas Blu-ray** permite ahora elegir una `.ISO` o una carpeta que contenga
una estructura Blu-ray ya creada.

Para carpeta, dvdrtool acepta:

- la carpeta raíz que contiene `BDMV`;
- o seleccionar directamente la carpeta `BDMV`.

Se comprueba `BDMV\index.bdmv`. La carpeta `CERTIFICATE` es opcional.

Cuando se realiza una grabación real desde carpeta, dvdrtool utiliza **IMAPI2FS
de Windows para crear una imagen UDF 2.50 temporal**, y después escribe esa
imagen en el BD-R/BD-RE mediante cdrtools. La imagen temporal se borra al finalizar.

Esto no convierte vídeos MKV/MP4 ni crea menús Blu-ray: la estructura BDMV debe
estar previamente autorizada.


## Barra lateral limpia — 3.1.2

El bloque **MOTOR / VELOCIDAD** ya no aparece en la barra lateral.

- La velocidad de grabación está en **Opciones > Valores predeterminados**.
- El estado de **cdrtools portátil** está en **Opciones > Motor y herramientas**.
- La barra lateral queda únicamente para navegación.
- Existe un solo selector de velocidad para evitar valores duplicados.


## Velocidad 6x — 3.1.3

La lista de velocidades incluye ahora **6x** como opción directa. La velocidad predeterminada sigue siendo **4x**.


## dvdrtool 3.1.4

Mejoras visuales en Blu-ray, etiqueta de volumen para carpetas BDMV, indicador de capacidad 25/50 GB y velocidades 2x/6x/10x/52x.
