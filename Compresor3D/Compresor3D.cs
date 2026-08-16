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
    /// Comprime los datos completos usando las dimensiones ganadoras y la mejor dirección.
    /// Determina cuál de las 3 direcciones (X, Y, Z) tiene menos runs RLE y usa solo esa.
    /// Devuelve el byte[] comprimido, el tamaño real y la dirección usada.
    /// </summary>
    public static (byte[] compressedData, long compressedSize, int direccion) Comprimir(
        byte[] datos, int ancho, int alto, int profundidad)
    {
        var cubo = new Cubo3D(ancho, alto, profundidad, datos);

        // Determinar la mejor dirección (la que tenga menos runs = mejor compresión)
        long runsX = cubo.ContarRuns(0);
        long runsY = cubo.ContarRuns(1);
        long runsZ = cubo.ContarRuns(2);

        int mejorDir = 0;
        long mejorRuns = runsX;
        if (runsY < mejorRuns) { mejorDir = 1; mejorRuns = runsY; }
        if (runsZ < mejorRuns) { mejorDir = 2; mejorRuns = runsZ; }

        string[] nombres = { "X", "Y", "Z" };
        Console.WriteLine($"  Mejor dirección: {nombres[mejorDir]} (runs: {mejorRuns:n0})");

        byte[] compressed = cubo.Comprimir(out long size, mejorDir);
        return (compressed, size, mejorDir);
    }

    // ==================== ARCHIVO .CUBO ====================

    /// <summary>
    /// Guarda los datos comprimidos en formato .cubo.
    /// Formato:
    ///   - Magic: "CUBO" (4 bytes ASCII)
    ///   - Versión: 1 (byte)
    ///   - Ancho, Alto, Profundidad: 4 bytes cada uno (little-endian)
    ///   - Tamaño original: 8 bytes (long, little-endian)
    ///   - Datos comprimidos: resto del archivo
    /// </summary>
    public static void GuardarCubo(string ruta, int ancho, int alto, int profundidad,
        long tamanoOriginal, byte[] datosComprimidos)
    {
        using var fs = new FileStream(ruta, FileMode.Create, FileAccess.Write);
        using var bw = new BinaryWriter(fs);

        // Cabecera
        bw.Write("CUBO"u8);                                  // Magic
        bw.Write((byte)1);                                   // Versión
        bw.Write(ancho);                                     // Ancho
        bw.Write(alto);                                      // Alto
        bw.Write(profundidad);                               // Profundidad
        bw.Write(tamanoOriginal);                            // Tamaño original
        bw.Write(datosComprimidos.Length);                    // Longitud comprimida
        bw.Write(datosComprimidos);                          // Datos
    }

    /// <summary>
    /// Lee y descomprime un archivo .cubo, reconstruyendo el archivo original.
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
        if (version != 1)
            throw new InvalidDataException($"Versión de formato no soportada: {version}");

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

        // Determinar ruta de salida: quitar extensión .cubo
        string dir = Path.GetDirectoryName(rutaCubo) ?? ".";
        string nombre = Path.GetFileNameWithoutExtension(rutaCubo);
        string rutaSalida = Path.Combine(dir, nombre);

        // Si el archivo ya existe sin extensión, añadir .original
        if (File.Exists(rutaSalida))
            rutaSalida = Path.Combine(dir, nombre + ".original");

        File.WriteAllBytes(rutaSalida, flat);

        Console.WriteLine($"  Archivo descomprimido: {rutaSalida}");
        Console.WriteLine($"  Tamaño original: {Utils.FormatearTamano(tamanoOriginal)}");
    }
}
