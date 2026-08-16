using System;
using System.Diagnostics;
using System.IO;
using Compresor3D;

// ============================================================
//  Compresor3D - Compresión de archivos mediante reordenamiento
//  en cubos 3D y escaneo de repeticiones RLE en 3 direcciones.
// ============================================================

var (archivo, descomprimir, minDim, maxDim, step) = Utils.ParsearArgumentos(args);

// Mostrar ayuda si no hay argumentos
if (archivo == null)
{
    MostrarAyuda();
    return;
}

// ---- Comando: DESCOMPRIMIR ----
if (descomprimir)
{
    EjecutarDescompresion(archivo);
    return;
}

// ---- Comando: COMPRIMIR (análisis + compresión) ----
EjecutarCompresion(archivo, minDim, maxDim, step);

// ============================================================
//  FUNCIONES LOCALES
// ============================================================

static void MostrarAyuda()
{
    Console.WriteLine("╔══════════════════════════════════════════════════╗");
    Console.WriteLine("║          Compresor3D - Compresión 3D RLE        ║");
    Console.WriteLine("╚══════════════════════════════════════════════════╝");
    Console.WriteLine();
    Console.WriteLine("Uso: Compresor3D [opciones]");
    Console.WriteLine();
    Console.WriteLine("Opciones:");
    Console.WriteLine("  --file, -f <ruta>    Archivo a comprimir (o arrastrar y soltar)");
    Console.WriteLine("  --descomprimir, -d   Descomprimir un archivo .cubo");
    Console.WriteLine("  --min-dim <n>        Dimensión mínima del cubo (default: 1)");
    Console.WriteLine("  --max-dim <n>        Dimensión máxima del cubo (default: sin límite)");
    Console.WriteLine("  --step <n>           Incremento entre dimensiones (default: 1)");
    Console.WriteLine();
    Console.WriteLine("Ejemplos:");
    Console.WriteLine("  Compresor3D --file \"C:\\archivo.bin\"");
    Console.WriteLine("  Compresor3D --file \"C:\\archivo.bin\" --min-dim 2 --max-dim 100 --step 2");
    Console.WriteLine("  Compresor3D --descomprimir \"C:\\archivo.cubo\"");
    Console.WriteLine("  Compresor3D \"C:\\archivo.bin\"              (drag & drop)");
}

static void EjecutarDescompresion(string archivo)
{
    Console.WriteLine("╔══════════════════════════════════════════════════╗");
    Console.WriteLine("║          Compresor3D - Descompresión            ║");
    Console.WriteLine("╚══════════════════════════════════════════════════╝");
    Console.WriteLine();

    if (!File.Exists(archivo))
    {
        Console.WriteLine($"  ERROR: No se encontró el archivo: {archivo}");
        return;
    }

    try
    {
        Compresor3DEngine.DescomprimirArchivo(archivo);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ERROR al descomprimir: {ex.Message}");
    }
}

static void EjecutarCompresion(string archivo, int minDim, int maxDim, int step)
{
    Console.WriteLine("╔══════════════════════════════════════════════════╗");
    Console.WriteLine("║          Compresor3D - Análisis de Cubo 3D      ║");
    Console.WriteLine("╚══════════════════════════════════════════════════╝");
    Console.WriteLine();

    if (!File.Exists(archivo))
    {
        Console.WriteLine($"  ERROR: No se encontró el archivo: {archivo}");
        return;
    }

    // Leer archivo completo
    byte[] datos = File.ReadAllBytes(archivo);
    long tamanoOriginal = datos.Length;

    Console.WriteLine($"  Archivo: {Path.GetFileName(archivo)}");
    Console.WriteLine($"  Tamaño: {Utils.FormatearTamano(tamanoOriginal)}");
    Console.WriteLine();

    // Si el archivo es grande, usar solo un fragmento para el análisis
    byte[] muestra;
    if (datos.Length > Compresor3DEngine.MAX_FRAGMENTO)
    {
        muestra = datos[..Compresor3DEngine.MAX_FRAGMENTO];
        Console.WriteLine($"  NOTA: Archivo grande. Analizando primeros {Utils.FormatearTamano(Compresor3DEngine.MAX_FRAGMENTO)}...");
    }
    else
    {
        muestra = datos;
    }

    Console.WriteLine($"  Parámetros: min-dim={minDim}, max-dim={(maxDim == int.MaxValue ? "∞" : maxDim.ToString())}, step={step}");
    Console.WriteLine();

    // Ejecutar análisis
    Console.WriteLine("  Buscando mejores dimensiones...");
    var (resultados, duracionAnalisis) = Compresor3DEngine.Analizar(muestra, minDim, maxDim, step);

    // Mostrar resultados
    Console.WriteLine();
    if (resultados.Count == 0)
    {
        Console.WriteLine("  No se encontraron combinaciones de dimensiones válidas.");
        Console.WriteLine("  Intenta ajustar --min-dim, --max-dim o --step.");
        return;
    }

    int topN = Math.Min(10, resultados.Count);
    Console.WriteLine($"  Se probaron {resultados.Count} combinaciones en {Utils.FormatearTiempo(duracionAnalisis)}");
    Console.WriteLine();
    MostrarTabla(resultados, topN, tamanoOriginal);

    // Comprimir con las mejores dimensiones
    var mejor = resultados[0];
    Console.WriteLine();
    Console.WriteLine($"  Mejor combinación: {mejor.Ancho} x {mejor.Alto} x {mejor.Profundidad}");
    Console.WriteLine($"  Comprimiendo archivo completo con estas dimensiones...");

    var swComp = Stopwatch.StartNew();
    var (datosComprimidos, tamanoComprimido, direccion, totalLineas, lineasUnicas, usaDedup) = Compresor3DEngine.Comprimir(
        datos, mejor.Ancho, mejor.Alto, mejor.Profundidad);
    swComp.Stop();

    double ratio = tamanoOriginal > 0 ? (1.0 - (double)tamanoComprimido / tamanoOriginal) * 100 : 0;
    double dedupRatio = totalLineas > 0 ? (double)lineasUnicas / totalLineas * 100 : 100;
    string[] dirNombres = { "X", "Y", "Z" };
    Console.WriteLine($"  Dirección de escaneo: {dirNombres[direccion]}");
    if (usaDedup)
        Console.WriteLine($"  Deduplicación: {lineasUnicas:n0} líneas únicas de {totalLineas:n0} ({dedupRatio:F1}%)");
    else
        Console.WriteLine($"  Modo: PackBits directo (dedup no conviene para estas dimensiones)");
    Console.WriteLine($"  Tamaño comprimido: {Utils.FormatearTamano(tamanoComprimido)} (ratio: {ratio:F1}%)");
    Console.WriteLine($"  Tiempo de compresión: {Utils.FormatearTiempo(swComp.Elapsed)}");

    // Guardar archivo .cubo
    string rutaCubo = Path.ChangeExtension(archivo, ".cubo");
    Console.WriteLine();
    Console.WriteLine($"  Guardando archivo .cubo: {rutaCubo}");
    Compresor3DEngine.GuardarCubo(rutaCubo, mejor.Ancho, mejor.Alto, mejor.Profundidad,
        tamanoOriginal, datosComprimidos);
    Console.WriteLine($"  Archivo guardado correctamente ({Utils.FormatearTamano(new FileInfo(rutaCubo).Length)}).");
    Console.WriteLine();
    Console.WriteLine("  Para descomprimir:");
    Console.WriteLine($"    Compresor3D --descomprimir \"{rutaCubo}\"");
}

static void MostrarTabla(System.Collections.Generic.List<ResultadoCompresion> resultados, int topN, long tamanoOriginal)
{
    // Cabecera de la tabla
    Console.WriteLine("  ┌───────┬──────────────────┬──────────────────┬──────────┬──────────┐");
    Console.WriteLine("  │ Puesto│   Dimensiones    │  Runs (mejor dir)│  Calidad │  Tiempo  │");
    Console.WriteLine("  ├───────┼──────────────────┼──────────────────┼──────────┼──────────┤");

    for (int i = 0; i < topN; i++)
    {
        var r = resultados[i];
        string dims = $"{r.Ancho} x {r.Alto} x {r.Profundidad}";
        string runs = $"{r.CompressedSize:N0}";
        // Calidad: % de runs respecto al total de bytes (menos = mejor)
        double calidad = tamanoOriginal > 0 ? (1.0 - (double)r.CompressedSize / tamanoOriginal) * 100 : 0;
        string tiempo = Utils.FormatearTiempo(r.Duracion);

        Console.WriteLine(
            $"  │ {i + 1,5} │ {dims,-16} │ {runs,16} │ {calidad,7:F1}% │ {tiempo,8} │");
    }

    Console.WriteLine("  └───────┴──────────────────┴──────────────────┴──────────┴──────────┘");
}
