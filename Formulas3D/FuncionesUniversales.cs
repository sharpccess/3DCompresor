using System;
using System.Collections.Generic;
using System.IO;

namespace Formulas3D;

/// <summary>
/// Librería indexada de funciones universales para descomposición funcional.
/// 
/// Principio: universalidad creciente por complejidad decreciente.
/// Funciones con menos parámetros son más universales y se intentan primero.
/// Se pueden combinar varias funciones para capturar patrones complejos.
/// 
/// Flujo:
///   1. Tomar residual (datos tras compresión tradicional)
///   2. Probar cada función de simple a compleja
///   3. Si mejora: aplicar, restar contribución, re-intentar
///   4. Repetir hasta que ninguna función mejore o se alcance el límite
///   5. Lo que queda → raw bytes
/// </summary>
public static class FuncionesUniversales
{
    // ==================== FORMATO DE BLOQUE FUNCIONAL ====================
    //
    // [numFunciones: uint16]
    // { [funcId: byte][paramCount: byte][params: float32 × paramCount] }...
    // [numRawBytes: int32]
    // [rawMap: byte × numRawBytes]    → posiciones en el bloque
    // [rawValues: byte × numRawBytes] → valores en esas posiciones
    //
    // Coste por función:
    //   ID 0 (constante):  2 + 1×4 = 6 bytes
    //   ID 1 (lineal):     2 + 2×4 = 10 bytes
    //   ID 2 (sinusoidal): 2 + 4×4 = 18 bytes
    //   ID 3 (cuadrática): 2 + 3×4 = 14 bytes
    //
    // Overhead fijo por bloque: 2 (numFuncs) + 4 (numRaw) = 6 bytes
    // Raw byte: 1 (posición) + 1 (valor) = 2 bytes por byte raw

    // ==================== TABLA DE FUNCIONES ====================

    /// <summary>
    /// Evalúa la función con ID dado en la posición x con los parámetros dados.
    /// Todas las operaciones son mod 256 para trabajar con bytes.
    /// </summary>
    public static double Evaluar(int funcId, double[] parametros, int x)
    {
        switch (funcId)
        {
            case 0: // f(x) = c
                return parametros[0];

            case 1: // f(x) = a*x + b (mod 256)
                return (parametros[0] * x + parametros[1]) % 256;

            case 2: // f(x) = a*sin(2π*f*x + φ) + d (mod 256)
                double val = parametros[0] * Math.Sin(2 * Math.PI * parametros[1] * x + parametros[2]) + parametros[3];
                return ((val % 256) + 256) % 256;

            case 3: // f(x) = a*x² + b*x + c (mod 256)
                return (parametros[0] * x * x + parametros[1] * x + parametros[2]) % 256;

            default:
                throw new ArgumentException($"Función ID {funcId} no soportada.");
        }
    }

    /// <summary>Número de parámetros de cada función.</summary>
    public static int NumParametros(int funcId) => funcId switch
    {
        0 => 1, // c
        1 => 2, // a, b
        2 => 4, // a, f, φ, d
        3 => 3, // a, b, c
        _ => throw new ArgumentException($"Función ID {funcId} no soportada.")
    };

    /// <summary>Nombres descriptivos de las funciones.</summary>
    public static string Nombre(int funcId) => funcId switch
    {
        0 => "constante",
        1 => "lineal",
        2 => "sinusoidal",
        3 => "cuadrática",
        _ => $"func({funcId})"
    };

    /// <summary>IDs de todas las funciones disponibles, de simple a compleja.</summary>
    public static readonly int[] IDs = { 0, 1, 2, 3 };

    // ==================== MOTOR DE FITTING ====================

    /// <summary>
    /// Encuentra los mejores parámetros para la función funcId que ajusten los datos.
    /// Devuelve null si no se puede ajustar.
    /// </summary>
    public static double[]? Ajustar(int funcId, byte[] datos)
    {
        int n = datos.Length;
        if (n == 0) return null;

        switch (funcId)
        {
            case 0: return AjustarConstante(datos, n);
            case 1: return AjustarLineal(datos, n);
            case 2: return AjustarSinusoidal(datos, n);
            case 3: return AjustarCuadratica(datos, n);
            default: return null;
        }
    }

    /// <summary>ID 0: f(x) = c. Mejor c = media de los datos.</summary>
    static double[] AjustarConstante(byte[] datos, int n)
    {
        double suma = 0;
        for (int i = 0; i < n; i++) suma += datos[i];
        return [suma / n];
    }

    /// <summary>ID 1: f(x) = a*x + b (mod 256). Mínimos cuadrados.</summary>
    static double[] AjustarLineal(byte[] datos, int n)
    {
        if (n < 2) return AjustarConstante(datos, n);

        // Mínimos cuadrados: a = (n·Σxy - Σx·Σy) / (n·Σx² - (Σx)²)
        double sumX = 0, sumY = 0, sumXY = 0, sumX2 = 0;
        for (int i = 0; i < n; i++)
        {
            sumX += i;
            sumY += datos[i];
            sumXY += i * datos[i];
            sumX2 += (double)i * i;
        }

        double denom = n * sumX2 - sumX * sumX;
        if (Math.Abs(denom) < 1e-10) return AjustarConstante(datos, n);

        double a = (n * sumXY - sumX * sumY) / denom;
        double b = (sumY - a * sumX) / n;

        return [a, b];
    }

    /// <summary>ID 2: f(x) = a*sin(2π*f*x + φ) + d. DFT para frecuencia dominante.</summary>
    static double[]? AjustarSinusoidal(byte[] datos, int n)
    {
        if (n < 8) return null; // necesita suficientes datos para frecuencia

        // Media (componente DC)
        double media = 0;
        for (int i = 0; i < n; i++) media += datos[i];
        media /= n;

        // DFT simplificado: buscar frecuencia dominante
        double mejorPotencia = 0;
        double mejorRe = 0, mejorIm = 0;
        double mejorFreq = 0;

        // Probar frecuencias de 1/n a n/2 ciclos por muestra
        int maxFreqBin = Math.Max(1, n / 2);
        for (int k = 1; k <= maxFreqBin; k++)
        {
            double freq = (double)k / n;
            double re = 0, im = 0;
            for (int i = 0; i < n; i++)
            {
                double angle = 2 * Math.PI * freq * i;
                re += (datos[i] - media) * Math.Cos(angle);
                im += (datos[i] - media) * Math.Sin(angle);
            }
            double potencia = re * re + im * im;
            if (potencia > mejorPotencia)
            {
                mejorPotencia = potencia;
                mejorRe = re;
                mejorIm = im;
                mejorFreq = freq;
            }
        }

        if (mejorPotencia < 1) return null; // sin señal sinusoidal significativa

        // Extraer amplitud y fase
        double amplitud = 2 * Math.Sqrt(mejorPotencia) / n;
        double fase = Math.Atan2(-mejorIm, mejorRe);

        return [amplitud, mejorFreq, fase, media];
    }

    /// <summary>ID 3: f(x) = a*x² + b*x + c (mod 256). Regresión polinómica.</summary>
    static double[]? AjustarCuadratica(byte[] datos, int n)
    {
        if (n < 3) return null;

        // Ecuaciones normales para regresión cuadrática
        // Σy = a·Σx⁴ + b·Σx³ + c·Σx²  (no, esto es para x² como variable)
        // Usar: y = a·x² + b·x + c
        // Normal equations: [Σx⁴ Σx³ Σx²; Σx³ Σx² Σx; Σx² Σx n] · [a;b;c] = [Σx²y; Σxy; Σy]
        double s0 = n, s1 = 0, s2 = 0, s3 = 0, s4 = 0;
        double t0 = 0, t1 = 0, t2 = 0;
        for (int i = 0; i < n; i++)
        {
            double x = i, y = datos[i];
            double x2 = x * x, x3 = x2 * x, x4 = x3 * x;
            s1 += x; s2 += x2; s3 += x3; s4 += x4;
            t0 += y; t1 += x * y; t2 += x2 * y;
        }

        // Resolver sistema 3x3 con eliminación de Gauss
        double[,] m = {
            { s4, s3, s2, t2 },
            { s3, s2, s1, t1 },
            { s2, s1, s0, t0 }
        };

        if (!Resolver3x3(m, out double a, out double b, out double c))
            return null;

        return [a, b, c];
    }

    /// <summary>Resuelve un sistema 3x3 por eliminación de Gauss con pivoteo.</summary>
    static bool Resolver3x3(double[,] m, out double x, out double y, out double z)
    {
        // Eliminación hacia adelante
        for (int col = 0; col < 3; col++)
        {
            // Pivoteo parcial
            int maxRow = col;
            for (int row = col + 1; row < 3; row++)
                if (Math.Abs(m[row, col]) > Math.Abs(m[maxRow, col])) maxRow = row;

            if (maxRow != col)
            {
                for (int j = 0; j < 4; j++)
                    (m[col, j], m[maxRow, j]) = (m[maxRow, j], m[col, j]);
            }

            if (Math.Abs(m[col, col]) < 1e-10)
            {
                x = y = z = 0;
                return false;
            }

            for (int row = col + 1; row < 3; row++)
            {
                double factor = m[row, col] / m[col, col];
                for (int j = col; j < 4; j++)
                    m[row, j] -= factor * m[col, j];
            }
        }

        // Sustitución hacia atrás
        z = m[2, 3] / m[2, 2];
        y = (m[1, 3] - m[1, 2] * z) / m[1, 1];
        x = (m[0, 3] - m[0, 2] * z - m[0, 1] * y) / m[0, 0];
        return true;
    }

    // ==================== MOTOR DE COMBINACIÓN ====================

    /// <summary>
    /// Descompone un bloque de bytes en una combinación de funciones universales + raw residual.
    /// Greedy: prueba cada función, aplica la mejor, resta, repite.
    /// </summary>
    public static (List<(int funcId, double[] param)> funciones, byte[] rawBytes, int[] rawPositions)
        Descomponer(byte[] bloque, int maxFunciones = 8)
    {
        int n = bloque.Length;
        var funciones = new List<(int, double[])>();
        byte[] residual = new byte[n];
        Buffer.BlockCopy(bloque, 0, residual, 0, n);

        for (int iter = 0; iter < maxFunciones; iter++)
        {
            int mejorId = -1;
            double[]? mejoresParams = null;
            int mejorCorrectos = 0;

            // Probar cada función, de simple a compleja
            foreach (int id in IDs)
            {
                var params_ = Ajustar(id, residual);
                if (params_ == null) continue;

                // Contar cuántos bytes reproduce correctamente
                int correctos = 0;
                for (int i = 0; i < n; i++)
                {
                    double val = Evaluar(id, params_, i);
                    byte predicted = (byte)(((int)Math.Round(val) % 256 + 256) % 256);
                    if (predicted == residual[i]) correctos++;
                }

                // Criterio: la función debe cubrir al menos el 30% del bloque
                // para que valga la pena (ahorra más de lo que cuesta)
                int costeFunc = 2 + NumParametros(id) * 4; // bytes que cuesta almacenar
                int bytesAhorrados = correctos - costeFunc / 1; // cada byte correcto ahorra ~1 byte raw
                if (correctos > mejorCorrectos && bytesAhorrados > 0)
                {
                    mejorCorrectos = correctos;
                    mejorId = id;
                    mejoresParams = params_;
                }
            }

            if (mejorId < 0 || mejoresParams == null) break;

            // Aplicar la mejor función: restar su contribución del residual
            funciones.Add((mejorId, mejoresParams));
            for (int i = 0; i < n; i++)
            {
                double val = Evaluar(mejorId, mejoresParams, i);
                byte predicted = (byte)(((int)Math.Round(val) % 256 + 256) % 256);
                residual[i] = (byte)((residual[i] - predicted + 256) % 256);
            }
        }

        // Lo que queda en residual son los bytes que ninguna función pudo capturar
        // Identificar posiciones no-cero en el residual
        var rawPositions = new List<int>();
        var rawValues = new List<byte>();
        for (int i = 0; i < n; i++)
        {
            if (residual[i] != 0)
            {
                rawPositions.Add(i);
                rawValues.Add(residual[i]);
            }
        }

        return (funciones, rawValues.ToArray(), rawPositions.ToArray());
    }

    // ==================== CODIFICACIÓN / DECODIFICACIÓN ====================

    /// <summary>
    /// Codifica un bloque descompuesto en bytes.
    /// Formato: [numFuncs: uint16]{funcId, paramCount, params...}[numRaw: int32][positions][values]
    /// </summary>
    public static void Codificar(MemoryStream ms,
        List<(int funcId, double[] param)> funciones, byte[] rawBytes, int[] rawPositions)
    {
        // Funciones
        ms.WriteByte((byte)funciones.Count);
        ms.WriteByte((byte)(funciones.Count >> 8));

        foreach (var (funcId, param) in funciones)
        {
            ms.WriteByte((byte)funcId);
            ms.WriteByte((byte)param.Length);
            foreach (double p in param)
            {
                float f = (float)p;
                byte[] bytes = BitConverter.GetBytes(f);
                ms.Write(bytes, 0, 4);
            }
        }

        // Raw residual
        int numRaw = rawBytes.Length;
        ms.WriteByte((byte)numRaw);
        ms.WriteByte((byte)(numRaw >> 8));
        ms.WriteByte((byte)(numRaw >> 16));
        ms.WriteByte((byte)(numRaw >> 24));

        if (numRaw > 0)
        {
            // Posiciones como uint16 (soporta bloques hasta 65535)
            foreach (int pos in rawPositions)
            {
                ms.WriteByte((byte)pos);
                ms.WriteByte((byte)(pos >> 8));
            }
            ms.Write(rawBytes, 0, rawBytes.Length);
        }
    }

    /// <summary>
    /// Decodifica un bloque funcional y reconstruye los bytes originales.
    /// </summary>
    public static byte[] Decodificar(BinaryReader br, int tamanoBloque)
    {
        byte[] output = new byte[tamanoBloque];

        // Leer funciones
        int numFuncs = br.ReadByte() | (br.ReadByte() << 8);
        for (int f = 0; f < numFuncs; f++)
        {
            int funcId = br.ReadByte();
            int paramCount = br.ReadByte();
            double[] param = new double[paramCount];
            for (int p = 0; p < paramCount; p++)
                param[p] = br.ReadSingle(); // float32

            // Sumar contribución de esta función
            for (int i = 0; i < tamanoBloque; i++)
            {
                double val = Evaluar(funcId, param, i);
                byte predicted = (byte)(((int)Math.Round(val) % 256 + 256) % 256);
                output[i] = (byte)((output[i] + predicted) & 0xFF);
            }
        }

        // Leer raw residual
        int numRaw = br.ReadByte() | (br.ReadByte() << 8) | (br.ReadByte() << 16) | (br.ReadByte() << 24);
        if (numRaw > 0)
        {
            // Leer posiciones
            int[] positions = new int[numRaw];
            for (int i = 0; i < numRaw; i++)
                positions[i] = br.ReadByte() | (br.ReadByte() << 8);

            // Leer valores
            byte[] values = br.ReadBytes(numRaw);
            if (values.Length < numRaw)
                throw new InvalidDataException("Datos raw truncados.");

            // Sumar residual
            for (int i = 0; i < numRaw; i++)
                output[positions[i]] = (byte)((output[positions[i]] + values[i]) & 0xFF);
        }

        return output;
    }

    // ==================== COMPRESIÓN DE STREAM ====================

    /// <summary>
    /// Comprime un stream de bytes usando descomposición funcional por bloques.
    /// Formato: [marker: 0x55][tamanoBloque: uint16][numBlocks: int32]
    ///   por bloque: [type: byte]
    ///     type 0x00 = raw: [bytes]
    ///     type 0x01 = funcional: [encoded block]
    /// </summary>
    public static byte[] ComprimirStream(byte[] data, int tamanoBloque = 1024)
    {
        using var ms = new MemoryStream();

        // Marker y configuración
        ms.WriteByte(0x55); // marker de funciones universales
        ms.WriteByte((byte)tamanoBloque);
        ms.WriteByte((byte)(tamanoBloque >> 8));

        int numBlocks = (data.Length + tamanoBloque - 1) / tamanoBloque;
        WriteInt32(ms, numBlocks);

        int bloquesFuncionales = 0;
        int bytesRawTotal = 0;

        for (int b = 0; b < numBlocks; b++)
        {
            int start = b * tamanoBloque;
            int len = Math.Min(tamanoBloque, data.Length - start);
            byte[] bloque = new byte[len];
            Buffer.BlockCopy(data, start, bloque, 0, len);

            // Descomponer en funciones
            var (funciones, rawBytes, rawPositions) = Descomponer(bloque);

            // Calcular coste de la descomposición funcional
            int costeFunc = 0;
            foreach (var (funcId, param) in funciones)
                costeFunc += 2 + param.Length * 4;
            costeFunc += 4 + rawBytes.Length * 3; // numRaw + positions + values

            // Calcular coste de raw directo
            int costeRaw = len;

            if (funciones.Count > 0 && costeFunc < costeRaw)
            {
                // Usar descomposición funcional
                ms.WriteByte(0x01);
                Codificar(ms, funciones, rawBytes, rawPositions);
                bloquesFuncionales++;
            }
            else
            {
                // Raw directo (más eficiente)
                ms.WriteByte(0x00);
                ms.Write(bloque, 0, bloque.Length);
                bytesRawTotal += len;
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Descomprime un stream de bloques funcionales.
    /// </summary>
    public static byte[] DescomprimirStream(byte[] compressedData)
    {
        using var ms = new MemoryStream(compressedData);

        // Leer marker y configuración
        int marker = ms.ReadByte();
        if (marker != 0x55)
            throw new InvalidDataException($"Marker FuncionesUniversales incorrecto: 0x{marker:X2}");

        int tamanoBloque = ms.ReadByte() | (ms.ReadByte() << 8);
        int numBlocks = ReadInt32(ms);

        using var br = new BinaryReader(ms);
        var output = new List<byte>();

        for (int b = 0; b < numBlocks; b++)
        {
            int type = ms.ReadByte();
            if (type < 0) throw new InvalidDataException("Stream truncado.");

            if (type == 0x01)
            {
                // Bloque funcional
                byte[] bloque = Decodificar(br, tamanoBloque);
                output.AddRange(bloque);
            }
            else
            {
                // Raw
                byte[] bloque = br.ReadBytes(tamanoBloque);
                output.AddRange(bloque);
            }
        }

        return output.ToArray();
    }

    // ==================== HELPERS ====================

    static void WriteInt32(MemoryStream ms, int value)
    {
        ms.WriteByte((byte)value);
        ms.WriteByte((byte)(value >> 8));
        ms.WriteByte((byte)(value >> 16));
        ms.WriteByte((byte)(value >> 24));
    }

    static int ReadInt32(Stream ms)
    {
        int b0 = ms.ReadByte();
        int b1 = ms.ReadByte();
        int b2 = ms.ReadByte();
        int b3 = ms.ReadByte();
        if (b0 < 0) throw new InvalidDataException("Stream truncado.");
        return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
    }
}
