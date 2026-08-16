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
    /// Evalúa las 3 direcciones y los 3 métodos (dedup, PackBits, LZ77)
    /// y elige la combinación que produzca el .cubo más pequeño.
    /// </summary>
    public static (byte[] compressedData, long compressedSize, int direccion,
        int totalLineas, int lineasUnicas, string metodo) Comprimir(
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

        int datosLen = datos.Length;
        double packRatio = 0.95;

        // === Evaluar dedup ===
        double costoDedupX = unicasX * lenX * packRatio + totalX * 4.0;
        double costoDedupY = unicasY * lenY * packRatio + totalY * 4.0;
        double costoDedupZ = unicasZ * lenZ * packRatio + totalZ * 4.0;

        int mejorDirDedup = 0;
        double mejorCostoDedup = costoDedupX;
        int mejorTotalDedup = totalX, mejorUnicasDedup = (int)unicasX;

        if (costoDedupY < mejorCostoDedup) { mejorDirDedup = 1; mejorCostoDedup = costoDedupY; mejorTotalDedup = totalY; mejorUnicasDedup = (int)unicasY; }
        if (costoDedupZ < mejorCostoDedup) { mejorDirDedup = 2; mejorCostoDedup = costoDedupZ; mejorTotalDedup = totalZ; mejorUnicasDedup = (int)unicasZ; }

        // === Evaluar PackBits directo ===
        double costoDirecto = datosLen * packRatio;

        // === Mejor entre dedup y PackBits directo ===
        byte[] mejorComprimido;
        long mejorSize;
        int mejorDir;
        int mejorTotal, mejorUnicas;
        string mejorMetodo;

        if (mejorCostoDedup <= costoDirecto)
        {
            mejorDir = mejorDirDedup;
            mejorTotal = mejorTotalDedup;
            mejorUnicas = mejorUnicasDedup;
            mejorComprimido = cubo.Comprimir(out mejorSize, mejorDir);
            mejorMetodo = $"Dedup {nombres[mejorDir]}";
            Console.WriteLine($"  Mejor dedup: {nombres[mejorDir]} ({mejorUnicas:n0}/{mejorTotal:n0} únicas, costo est. {mejorCostoDedup:n0} bytes)");
        }
        else
        {
            long runsX = cubo.ContarRuns(0);
            long runsY = cubo.ContarRuns(1);
            long runsZ = cubo.ContarRuns(2);
            mejorDir = 0;
            long mejorRuns = runsX;
            if (runsY < mejorRuns) { mejorDir = 1; mejorRuns = runsY; }
            if (runsZ < mejorRuns) { mejorDir = 2; mejorRuns = runsZ; }
            mejorTotal = mejorDir switch { 0 => totalX, 1 => totalY, 2 => totalZ };
            mejorUnicas = mejorTotal;
            mejorComprimido = cubo.ComprimirPackBitsDirecto(out mejorSize, mejorDir);
            mejorMetodo = $"PackBits {nombres[mejorDir]}";
            Console.WriteLine($"  Mejor PackBits: {nombres[mejorDir]} (runs: {mejorRuns:n0}, costo est. {costoDirecto:n0} bytes)");
        }

        // === Evaluar LZ77 en las 3 direcciones ===
        if (datosLen >= 512) // solo vale la pena en datos no triviales
        {
            Console.WriteLine($"  Probando LZ77 en 3 direcciones...");
            for (int dir = 0; dir < 3; dir++)
            {
                byte[] lz77Data = cubo.ComprimirLZ77(out long lz77Size, dir);
                Console.WriteLine($"    {nombres[dir]}: {Utils.FormatearTamano(lz77Size)}");
                if (lz77Size < mejorSize)
                {
                    mejorComprimido = lz77Data;
                    mejorSize = lz77Size;
                    mejorDir = dir;
                    mejorTotal = dir switch { 0 => totalX, 1 => totalY, 2 => totalZ };
                    mejorUnicas = dir switch { 0 => (int)unicasX, 1 => (int)unicasY, 2 => (int)unicasZ };
                    mejorMetodo = $"LZ77 {nombres[dir]}";
                }
            }
        }

        // === Evaluar MicroVM en las 3 direcciones ===
        if (datosLen >= 64) // MicroVM vale la pena en datos con cierto tamaño
        {
            Console.WriteLine($"  Probando MicroVM en 3 direcciones...");
            for (int dir = 0; dir < 3; dir++)
            {
                byte[] microData = cubo.ComprimirMicroVM(out long microSize, dir);
                Console.WriteLine($"    {nombres[dir]}: {Utils.FormatearTamano(microSize)}");
                if (microSize < mejorSize)
                {
                    mejorComprimido = microData;
                    mejorSize = microSize;
                    mejorDir = dir;
                    mejorTotal = dir switch { 0 => totalX, 1 => totalY, 2 => totalZ };
                    mejorUnicas = dir switch { 0 => (int)unicasX, 1 => (int)unicasY, 2 => (int)unicasZ };
                    mejorMetodo = $"MicroVM {nombres[dir]}";
                }
            }
        }

        // === Evaluar Funciones Universales en las 3 direcciones ===
        if (datosLen >= 256) // necesita bloques de al menos 1024 bytes
        {
            Console.WriteLine($"  Probando Funciones Universales en 3 direcciones...");
            for (int dir = 0; dir < 3; dir++)
            {
                byte[] funcData = cubo.ComprimirFunciones(out long funcSize, dir);
                Console.WriteLine($"    {nombres[dir]}: {Utils.FormatearTamano(funcSize)}");
                if (funcSize < mejorSize)
                {
                    mejorComprimido = funcData;
                    mejorSize = funcSize;
                    mejorDir = dir;
                    mejorTotal = dir switch { 0 => totalX, 1 => totalY, 2 => totalZ };
                    mejorUnicas = dir switch { 0 => (int)unicasX, 1 => (int)unicasY, 2 => (int)unicasZ };
                    mejorMetodo = $"Funciones {nombres[dir]}";
                }
            }
        }

        Console.WriteLine($"\n  >>> Ganador: {mejorMetodo} ({Utils.FormatearTamano(mejorSize)})");

        return (mejorComprimido, mejorSize, mejorDir, mejorTotal, mejorUnicas, mejorMetodo);
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
