using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Compresor3D;

/// <summary>
/// Resultado del análisis de una combinación de dimensiones.
/// </summary>
public class ResultadoCompresion
{
    public int Ancho { get; set; }
    public int Alto { get; set; }
    public int Profundidad { get; set; }
    /// <summary>Tamaño estimado del dato comprimido (2 * totalRuns en la mejor dirección).</summary>
    public long CompressedSize { get; set; }
    /// <summary>Total de runs RLE encontrados en las tres direcciones.</summary>
    public long TotalRuns { get; set; }
    public TimeSpan Duracion { get; set; }
    public Cubo3D? Cubo { get; set; }
}

/// <summary>
/// Lógica principal de compresión y descompresión 3D.
/// Busca las mejores dimensiones de cubo para maximizar la compresión RLE
/// y genera/consume archivos .cubo.
/// </summary>
public static class Compresor3DEngine
{
    /// <summary>Tamaño máximo del fragmento para el análisis (10 MB).</summary>
    public const int MAX_FRAGMENTO = 10 * 1024 * 1024;

    // ==================== ANÁLISIS DE DIMENSIONES ====================

    /// <summary>
    /// Analiza todas las combinaciones de dimensiones (x, y, z) tales que x*y*z == datos.Length,
    /// dentro de los límites especificados, y devuelve los resultados ordenados por compresión.
    /// Usa paralelismo para evaluar las combinaciones.
    /// </summary>
    public static (List<ResultadoCompresion> resultados, TimeSpan duracion) Analizar(
        byte[] datos, int minDim = 1, int maxDim = int.MaxValue, int step = 1)
    {
        var sw = Stopwatch.StartNew();
        var factores = Utils.EncontrarFactores(datos.Length, minDim, maxDim, step);

        if (factores.Count == 0)
            return (new List<ResultadoCompresion>(), sw.Elapsed);

        var resultados = new List<ResultadoCompresion>();
        var lockObj = new object();

        // Procesar combinaciones en paralelo para aprovechar múltiples núcleos
        Parallel.ForEach(factores, factor =>
        {
            var (x, y, z) = factor;
            var sw2 = Stopwatch.StartNew();

            var cubo = new Cubo3D(x, y, z, datos);
            long runsX = cubo.ContarRuns(0);
            long runsY = cubo.ContarRuns(1);
            long runsZ = cubo.ContarRuns(2);
            long totalRuns = runsX + runsY + runsZ;

            // La mejor dirección es la que tiene menos runs
            long mejorDirRuns = Math.Min(runsX, Math.Min(runsY, runsZ));
            // Score = runs en la mejor dirección (menos = más repetición = mejor compresión)
            long compressedSize = mejorDirRuns;

            sw2.Stop();

            var resultado = new ResultadoCompresion
            {
                Ancho = x,
                Alto = y,
                Profundidad = z,
                CompressedSize = compressedSize,
                TotalRuns = totalRuns,
                Duracion = sw2.Elapsed,
                Cubo = cubo
            };

            lock (lockObj)
            {
                resultados.Add(resultado);
            }
        });

        sw.Stop();

        // Ordenar por tamaño comprimido estimado (menor = mejor)
        resultados.Sort((a, b) => a.CompressedSize.CompareTo(b.CompressedSize));

        return (resultados, sw.Elapsed);
    }

    // ==================== COMPRESIÓN ====================

    /// <summary>
    /// Comprime los datos completos usando las dimensiones ganadoras.
    /// Evalúa las 3 direcciones y elige la que produzca el .cubo más pequeño,
    /// considerando tanto deduplicación de líneas como PackBits directo.
    /// </summary>
    public static (byte[] compressedData, long compressedSize, int direccion,
        int totalLineas, int lineasUnicas, bool usaDedup) Comprimir(
        byte[] datos, int ancho, int alto, int profundidad)
    {
        var cubo = new Cubo3D(ancho, alto, profundidad, datos);

        var (totalX, unicasX, lenX) = cubo.InfoLineas(0);
        var (totalY, unicasY, lenY) = cubo.InfoLineas(1);
        var (totalZ, unicasZ, lenZ) = cubo.InfoLineas(2);

        string[] nombres = { "X", "Y", "Z" };
        Console.WriteLine($"  Líneas únicas por dirección:");
        Console.WriteLine($"    X: {unicasX:n0} / {totalX:n0} (long={lenX}, {(100.0 * unicasX / totalX):F1}% únicas)");
        Console.WriteLine($"    Y: {unicasY:n0} / {totalY:n0} (long={lenY}, {(100.0 * unicasY / totalY):F1}% únicas)");
        Console.WriteLine($"    Z: {unicasZ:n0} / {totalZ:n0} (long={lenZ}, {(100.0 * unicasZ / totalZ):F1}% únicas)");

        // Estimar costo de cada opción:
        // Dedup: uniqueLines * lineLen * ~0.9 (PackBits en únicas) + totalLines * 4 (índices)
        // PackBits directo: totalLines * lineLen * ~0.9 (sin overhead de índices)
        double packRatio = 0.95; // estimación de ratio PackBits para datos poco repetitivos
        int datosLen = datos.Length;

        double costoDedupX = unicasX * lenX * packRatio + totalX * 4.0;
        double costoDedupY = unicasY * lenY * packRatio + totalY * 4.0;
        double costoDedupZ = unicasZ * lenZ * packRatio + totalZ * 4.0;
        double costoDirecto = datosLen * packRatio;

        // Encontrar la mejor opción
        int mejorDir = 0;
        double mejorCosto = costoDedupX;
        bool usaDedup = true;
        int mejorTotal = totalX, mejorUnicas = (int)unicasX;

        if (costoDedupY < mejorCosto) { mejorDir = 1; mejorCosto = costoDedupY; mejorTotal = totalY; mejorUnicas = (int)unicasY; }
        if (costoDedupZ < mejorCosto) { mejorDir = 2; mejorCosto = costoDedupZ; mejorTotal = totalZ; mejorUnicas = (int)unicasZ; }
        if (costoDirecto < mejorCosto) { usaDedup = false; mejorCosto = costoDirecto; }

        if (usaDedup)
            Console.WriteLine($"  Mejor: {nombres[mejorDir]} con deduplicación ({mejorUnicas:n0}/{mejorTotal:n0} únicas, costo est. {mejorCosto:n0} bytes)");
        else
            Console.WriteLine($"  Mejor: PackBits directo sin dedup (costo est. {mejorCosto:n0} bytes, dedup no conviene)");

        byte[] compressed;
        long size;
        if (usaDedup)
        {
            compressed = cubo.Comprimir(out size, mejorDir);
        }
        else
        {
            // Fallback: PackBits directo sin dedup, usar dirección con menos runs
            long runsX = cubo.ContarRuns(0);
            long runsY = cubo.ContarRuns(1);
            long runsZ = cubo.ContarRuns(2);
            mejorDir = 0;
            long mejorRuns = runsX;
            if (runsY < mejorRuns) { mejorDir = 1; mejorRuns = runsY; }
            if (runsZ < mejorRuns) { mejorDir = 2; mejorRuns = runsZ; }
            mejorTotal = mejorDir switch { 0 => totalX, 1 => totalY, 2 => totalZ };
            mejorUnicas = mejorTotal; // sin dedup, todas las líneas son "únicas"
            Console.WriteLine($"  Dirección PackBits: {nombres[mejorDir]} (runs: {mejorRuns:n0})");
            compressed = cubo.ComprimirPackBitsDirecto(out size, mejorDir);
        }

        return (compressed, size, mejorDir, mejorTotal, mejorUnicas, usaDedup);
    }

    // ==================== ARCHIVO .CUBO ====================

    /// <summary>
    /// Guarda los datos comprimidos en formato .cubo.
    /// Formato:
    ///   - Magic: "CUBO" (4 bytes ASCII)
    ///   - Versión: 2 (byte)
    ///   - Nombre original: [longitud: int32] [UTF-8 bytes]
    ///   - Ancho, Alto, Profundidad: 4 bytes cada uno (little-endian)
    ///   - Tamaño original: 8 bytes (long, little-endian)
    ///   - Datos comprimidos: [longitud: int32] [bytes]
    /// </summary>
    public static void GuardarCubo(string ruta, string nombreOriginal,
        int ancho, int alto, int profundidad,
        long tamanoOriginal, byte[] datosComprimidos)
    {
        using var fs = new FileStream(ruta, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        byte[] nombreBytes = System.Text.Encoding.UTF8.GetBytes(nombreOriginal);

        // Cabecera
        bw.Write("CUBO"u8);                                  // Magic
        bw.Write((byte)2);                                   // Versión 2
        bw.Write(nombreBytes.Length);                         // Longitud del nombre
        bw.Write(nombreBytes);                                // Nombre original (UTF-8)
        bw.Write(ancho);                                     // Ancho
        bw.Write(alto);                                      // Alto
        bw.Write(profundidad);                               // Profundidad
        bw.Write(tamanoOriginal);                            // Tamaño original
        bw.Write(datosComprimidos.Length);                    // Longitud comprimida
        bw.Write(datosComprimidos);                          // Datos
    }

    /// <summary>
    /// Lee y descomprime un archivo .cubo, reconstruyendo el archivo original
    /// con su nombre y extensión correctos.
    /// </summary>
    public static void DescomprimirArchivo(string rutaCubo)
    {
        using var fs = new FileStream(rutaCubo, FileMode.Open, FileAccess.Read);
        using var br = new BinaryReader(fs);

        // Leer y validar cabecera
        Span<byte> magic = stackalloc byte[4];
        br.Read(magic);
        if (!magic.SequenceEqual("CUBO"u8))
            throw new InvalidDataException("El archivo no tiene el formato .cubo (magic incorrecto).");

        byte version = br.ReadByte();
        if (version > 2)
            throw new InvalidDataException($"Versión de formato no soportada: {version}");

        // Versión 2: leer nombre original
        string? nombreOriginal = null;
        if (version >= 2)
        {
            int nombreLen = br.ReadInt32();
            byte[] nombreBytes = br.ReadBytes(nombreLen);
            nombreOriginal = System.Text.Encoding.UTF8.GetString(nombreBytes);
        }

        int ancho = br.ReadInt32();
        int alto = br.ReadInt32();
        int profundidad = br.ReadInt32();
        long tamanoOriginal = br.ReadInt64();
        int tamanoComprimido = br.ReadInt32();
        byte[] datosComprimidos = br.ReadBytes(tamanoComprimido);

        if (datosComprimidos.Length != tamanoComprimido)
            throw new InvalidDataException("Datos comprimidos truncados en el archivo.");

        // Descomprimir
        byte[] flat = Cubo3D.Descomprimir(datosComprimidos, ancho, alto, profundidad);

        // Verificar tamaño
        if (flat.Length != tamanoOriginal)
            throw new InvalidDataException(
                $"Tamaño descomprimido ({flat.Length}) no coincide con el original ({tamanoOriginal}).");

        // Determinar ruta de salida
        string dir = Path.GetDirectoryName(rutaCubo) ?? ".";
        string rutaSalida;

        if (nombreOriginal != null && nombreOriginal.Length > 0)
        {
            // Usar el nombre original guardado en la cabecera
            rutaSalida = Path.Combine(dir, nombreOriginal);
        }
        else
        {
            // Fallback para versión 1: quitar extensión .cubo
            string nombre = Path.GetFileNameWithoutExtension(rutaCubo);
            rutaSalida = Path.Combine(dir, nombre);
        }

        // Si el archivo ya existe, añadir .restaurado
        if (File.Exists(rutaSalida))
        {
            string sinExt = Path.Combine(dir,
                Path.GetFileNameWithoutExtension(rutaSalida) + ".restaurado" +
                Path.GetExtension(rutaSalida));
            rutaSalida = sinExt;
        }

        File.WriteAllBytes(rutaSalida, flat);

        Console.WriteLine($"  Archivo descomprimido: {rutaSalida}");
        Console.WriteLine($"  Tamaño original: {Utils.FormatearTamano(tamanoOriginal)}");
    }
}
