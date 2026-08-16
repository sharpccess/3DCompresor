using System;
using System.Collections.Generic;

namespace Compresor3D;

/// <summary>
/// Métodos auxiliares: factorización de enteros, parsing de argumentos y formateo.
/// </summary>
public static class Utils
{
    // ==================== FACTORIZACIÓN EN TRIPLETAS ====================

    /// <summary>
    /// Encuentra todas las combinaciones (x, y, z) tales que x * y * z == n,
    /// donde cada dimensión está entre minDim y maxDim (inclusive).
    /// El parámetro step define el incremento entre valores probados.
    /// Devuelve tripletas con x &lt;= y &lt;= z para evitar duplicados de orientación
    /// (el análisis prueba las tres orientaciones internamente).
    /// </summary>
    public static List<(int x, int y, int z)> EncontrarFactores(int n, int minDim, int maxDim, int step)
    {
        var resultado = new List<(int, int, int)>();
        if (n <= 0) return resultado;

        // Límite superior para x: raíz cúbica de n (para garantizar x <= y <= z)
        int maxX = (int)Math.Cbrt(n);

        for (int x = minDim; x <= Math.Min(maxX, maxDim); x += step)
        {
            if (n % x != 0) continue;
            int nxy = n / x;

            // Límite superior para y: raíz cuadrada de n/x
            int maxY = (int)Math.Sqrt(nxy);

            for (int y = Math.Max(x, minDim); y <= Math.Min(maxY, maxDim); y += step)
            {
                if (nxy % y != 0) continue;
                int z = nxy / y;

                if (z >= y && z >= minDim && z <= maxDim)
                {
                    resultado.Add((x, y, z));
                }
            }
        }

        return resultado;
    }

    // ==================== PARSING DE ARGUMENTOS ====================

    /// <summary>
    /// Parsea los argumentos de línea de comandos.
    /// Soporta: --file, --descomprimir, --min-dim, --max-dim, --step
    /// También soporta drag & drop (archivo como primer argumento sin flag).
    /// Soporta --batch para múltiples archivos (shell extension).
    /// </summary>
    public static (string? archivo, bool descomprimir, int minDim, int maxDim, int step, string[] batchFiles) ParsearArgumentos(string[] args)
    {
        string? archivo = null;
        bool descomprimir = false;
        int minDim = 1;
        int maxDim = int.MaxValue;
        int step = 1;
        var batchFiles = new List<string>();
        bool isBatch = false;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i].Trim();

            // Quitar comillas si las tiene (drag & drop)
            if (arg.StartsWith('"') && arg.EndsWith('"'))
                arg = arg[1..^1];

            switch (arg.ToLowerInvariant())
            {
                case "--file":
                case "-f":
                    if (i + 1 < args.Length)
                    {
                        archivo = args[++i].Trim('"');
                    }
                    break;

                case "--descomprimir":
                case "-d":
                    descomprimir = true;
                    break;

                case "--batch":
                case "-b":
                    isBatch = true;
                    // Recoger todos los archivos restantes
                    while (i + 1 < args.Length)
                    {
                        string nextArg = args[++i].Trim('"');
                        if (Directory.Exists(nextArg))
                        {
                            // Si es carpeta, añadir todos los archivos recursivamente
                            foreach (var f in Directory.GetFiles(nextArg, "*.*", SearchOption.AllDirectories))
                                batchFiles.Add(f);
                        }
                        else
                        {
                            batchFiles.Add(nextArg);
                        }
                    }
                    break;

                case "--generar-test":
                    archivo = "--generar-test";
                    break;

                case "--decode-png":
                    archivo = "--decode-png";
                    if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                        archivo = "--decode-png:" + args[++i].Trim('"');
                    break;

                case "--test-paq":
                    archivo = "--test-paq";
                    if (i + 1 < args.Length && !args[i + 1].StartsWith('-'))
                        archivo = "--test-paq:" + args[++i].Trim('"');
                    break;

                case "--min-dim":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int min))
                        minDim = min;
                    break;

                case "--max-dim":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int max))
                        maxDim = max;
                    break;

                case "--step":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out int s))
                        step = Math.Max(1, s);
                    break;

                default:
                    // Si no es un flag conocido, tratar como ruta de archivo (drag & drop)
                    if (!arg.StartsWith('-') && archivo == null)
                        archivo = arg;
                    break;
            }
        }

        return (archivo, descomprimir, minDim, maxDim, step, isBatch ? batchFiles.ToArray() : Array.Empty<string>());
    }

    // ==================== FORMATEO ====================

    /// <summary>
    /// Formatea un tamaño en bytes a una cadena legible (B, KB, MB, GB).
    /// </summary>
    public static string FormatearTamano(long bytes)
    {
        string[] unidades = { "B", "KB", "MB", "GB" };
        double tamaño = bytes;
        int idx = 0;
        while (tamaño >= 1024 && idx < unidades.Length - 1)
        {
            tamaño /= 1024;
            idx++;
        }
        return $"{tamaño:F2} {unidades[idx]}";
    }

    /// <summary>
    /// Formatea un TimeSpan a una cadena legible con horas, minutos, segundos y milisegundos.
    /// </summary>
    public static string FormatearTiempo(TimeSpan ts)
    {
        if (ts.TotalHours >= 1)
            return $"{ts.Hours}h {ts.Minutes}m {ts.Seconds}s";
        if (ts.TotalMinutes >= 1)
            return $"{ts.Minutes}m {ts.Seconds}s";
        if (ts.TotalSeconds >= 1)
            return $"{ts.TotalSeconds:F2}s";
        return $"{ts.TotalMilliseconds:F0}ms";
    }
}
