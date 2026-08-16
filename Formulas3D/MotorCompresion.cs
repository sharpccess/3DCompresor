using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using MathNet.Numerics;
using NWaves.Transforms;
using NWaves.Transforms.Wavelets;

namespace Formulas3D;

/// <summary>
/// Motor unificado de compresión: funciones matemáticas + microcódigo + DCT + wavelets.
/// 
/// Usa librerías profesionales:
/// - Math.NET Numerics (MIT): regresión lineal/polimonial, FFT
/// - NWaves (MIT): DCT tipo II (como JPEG), wavelets Daubechies (como JPEG2000)
/// 
/// Métodos de compresión disponibles:
/// 1. Matching Pursuit: funciones (cte, lineal, sin, x²) + microcódigo (xor, alt, geo, lcg)
/// 2. DCT: transformada coseno discreta con cuantización (estilo JPEG)
/// 3. Wavelets: transformada wavelet rápida con umbralización (estilo JPEG2000)
/// 
/// El motor prueba los 3 métodos y elige el que produzca el archivo más pequeño.
/// </summary>
public static class MotorCompresion
{
    // ==================== TIPOS DE GENERADOR (Matching Pursuit) ====================
    public const byte TIPO_FUNCION = 0x00;
    public const byte TIPO_MICROCODIGO = 0x01;

    public const int FUNC_CONSTANTE = 0;
    public const int FUNC_LINEAL = 1;
    public const int FUNC_SINUSOIDAL = 2;
    public const int FUNC_CUADRATICA = 3;

    public const int MIC_XOR = 0;
    public const int MIC_ALTERNANTE = 1;
    public const int MIC_GEOMETRICA = 2;
    public const int MIC_LCG = 3;

    // ==================== EVALUACIÓN ====================

    public static double EvaluarFuncion(int funcId, double[] param, int x)
    {
        switch (funcId)
        {
            case FUNC_CONSTANTE: return param[0];
            case FUNC_LINEAL: return (param[0] * x + param[1]) % 256;
            case FUNC_SINUSOIDAL:
                double v = param[0] * Math.Sin(2 * Math.PI * param[1] * x + param[2]) + param[3];
                return ((v % 256) + 256) % 256;
            case FUNC_CUADRATICA: return (param[0] * x * x + param[1] * x + param[2]) % 256;
            default: return 0;
        }
    }

    public static byte EvaluarMicro(int microId, double[] param, int x, byte prev)
    {
        return microId switch
        {
            MIC_XOR => (byte)(prev ^ (byte)param[0]),
            MIC_ALTERNANTE => (x % 2 == 0) ? (byte)param[0] : (byte)param[1],
            MIC_GEOMETRICA => (byte)((prev * (byte)param[0]) & 0xFF),
            MIC_LCG => (byte)(((byte)param[0] * prev + (byte)param[1]) & 0xFF),
            _ => 0
        };
    }

    public static int NumParametrosFuncion(int id) => id switch
    {
        FUNC_CONSTANTE => 1, FUNC_LINEAL => 2,
        FUNC_SINUSOIDAL => 4, FUNC_CUADRATICA => 3, _ => 0
    };

    public static int NumParametrosMicro(int id) => id switch
    {
        MIC_XOR => 1, MIC_ALTERNANTE => 2,
        MIC_GEOMETRICA => 1, MIC_LCG => 2, _ => 0
    };

    public static string NombreGenerador(byte tipo, int id) => (tipo, id) switch
    {
        (TIPO_FUNCION, FUNC_CONSTANTE) => "cte",
        (TIPO_FUNCION, FUNC_LINEAL) => "lineal",
        (TIPO_FUNCION, FUNC_SINUSOIDAL) => "sin",
        (TIPO_FUNCION, FUNC_CUADRATICA) => "x²",
        (TIPO_MICROCODIGO, MIC_XOR) => "xor",
        (TIPO_MICROCODIGO, MIC_ALTERNANTE) => "alt",
        (TIPO_MICROCODIGO, MIC_GEOMETRICA) => "geo",
        (TIPO_MICROCODIGO, MIC_LCG) => "lcg",
        _ => "?"
    };

    // =====================================================================
    //  SECCIÓN 1: MATCHING PURSUIT (funciones + microcódigo)
    //  Usa Math.NET para fitting optimizado
    // =====================================================================

    static (byte tipo, int id, double[] param, int correctos)? MejorGenerador(byte[] datos, bool permitirMicro = false)
    {
        int n = datos.Length;
        if (n < 2) return null;

        double[] xdata = new double[n];
        double[] ydata = new double[n];
        for (int i = 0; i < n; i++) { xdata[i] = i; ydata[i] = datos[i]; }

        (byte, int, double[], int)? mejor = null;

        // ID 0: Constante
        {
            double media = ydata.Average();
            int c = ContarCorrectos(FUNC_CONSTANTE, [media], datos, n);
            mejor = Max(mejor, TIPO_FUNCION, FUNC_CONSTANTE, [media], c);
        }

        // ID 1: Lineal — Math.NET Fit.Line
        {
            var (intercept, slope) = Fit.Line(xdata, ydata);
            int c = ContarCorrectos(FUNC_LINEAL, [slope, intercept], datos, n);
            mejor = Max(mejor, TIPO_FUNCION, FUNC_LINEAL, [slope, intercept], c);
        }

        // ID 2: Sinusoidal — FFT de NWaves (O(n log n))
        if (n >= 8)
        {
            var (amp, freq, fase, dc) = AjustarSinFFT(datos, n);
            if (amp > 0.5)
            {
                int c = ContarCorrectos(FUNC_SINUSOIDAL, [amp, freq, fase, dc], datos, n);
                mejor = Max(mejor, TIPO_FUNCION, FUNC_SINUSOIDAL, [amp, freq, fase, dc], c);
            }
        }

        // ID 3: Cuadrática — Math.NET Fit.Polynomial
        if (n >= 3)
        {
            double[] coeffs = Fit.Polynomial(xdata, ydata, 2);
            if (coeffs.Length >= 3)
            {
                int c = ContarCorrectos(FUNC_CUADRATICA, [coeffs[2], coeffs[1], coeffs[0]], datos, n);
                mejor = Max(mejor, TIPO_FUNCION, FUNC_CUADRATICA, [coeffs[2], coeffs[1], coeffs[0]], c);
            }
        }

        // Microcódigo (solo permitir en primera iteración — son stateful y no se combinan bien)
        if (!permitirMicro) return mejor;

        // XOR
        {
            byte xv = (byte)(datos[0] ^ datos[1]);
            if (xv != 0)
            {
                int c = 1; byte prev = datos[0];
                for (int i = 1; i < n; i++) { if ((byte)(prev ^ xv) == datos[i]) c++; prev = datos[i]; }
                mejor = Max(mejor, TIPO_MICROCODIGO, MIC_XOR, [xv], c);
            }
        }

        // Alternante
        if (n >= 4)
        {
            byte a = datos[0], b = datos[1];
            if (a != b)
            {
                int c = 0;
                for (int i = 0; i < n; i++) if (datos[i] == (i % 2 == 0 ? a : b)) c++;
                mejor = Max(mejor, TIPO_MICROCODIGO, MIC_ALTERNANTE, [a, b], c);
            }
        }

        // Geométrica
        if (n >= 3 && datos[0] != 0)
        {
            int inv = InversaModular(datos[0]);
            if (inv >= 0)
            {
                int f = (datos[1] * inv) & 0xFF;
                if (f >= 2)
                {
                    int c = 1; byte prev = datos[0];
                    for (int i = 1; i < n; i++) { if ((byte)((prev * f) & 0xFF) == datos[i]) c++; prev = datos[i]; }
                    mejor = Max(mejor, TIPO_MICROCODIGO, MIC_GEOMETRICA, [f], c);
                }
            }
        }

        // LCG
        if (n >= 4 && datos[0] != 0)
        {
            for (int a = 1; a < 256; a++)
            {
                int cc = ((int)datos[1] - a * datos[0] + 25600) & 0xFF;
                bool ok = true;
                for (int i = 1; i < Math.Min(n, 32) && ok; i++)
                    if ((byte)((a * datos[i - 1] + cc) & 0xFF) != datos[i]) ok = false;
                if (!ok) continue;
                int c = 1; byte prev = datos[0];
                for (int i = 1; i < n; i++) { if ((byte)((a * prev + cc) & 0xFF) == datos[i]) c++; prev = datos[i]; }
                mejor = Max(mejor, TIPO_MICROCODIGO, MIC_LCG, [a, cc], c);
                break;
            }
        }

        return mejor;
    }

    /// <summary>FFT con NWaves para encontrar frecuencia dominante — O(n log n).</summary>
    static (double amp, double freq, double fase, double dc) AjustarSinFFT(byte[] datos, int n)
    {
        double media = 0;
        for (int i = 0; i < n; i++) media += datos[i];
        media /= n;

        // NWaves usa float[] y requiere potencia de 2 para FFT
        int fftSize = 1;
        while (fftSize < n) fftSize <<= 1;

        float[] real = new float[fftSize];
        float[] imag = new float[fftSize];
        for (int i = 0; i < n; i++) real[i] = (float)(datos[i] - media);

        var fft = new Fft(fftSize);
        fft.Direct(real, imag);

        // Encontrar frecuencia dominante (ignorar DC)
        double mejorPot = 0;
        int mejorK = 1;
        int halfFft = fftSize / 2;
        for (int k = 1; k < halfFft; k++)
        {
            double pot = real[k] * real[k] + imag[k] * imag[k];
            if (pot > mejorPot) { mejorPot = pot; mejorK = k; }
        }

        if (mejorPot < 1) return (0, 0, 0, media);

        double amp = 2 * Math.Sqrt(mejorPot) / n;
        double freq = (double)mejorK / n;
        double fase = Math.Atan2(-imag[mejorK], real[mejorK]);

        return (amp, freq, fase, media);
    }

    static int ContarCorrectos(int funcId, double[] param, byte[] datos, int n)
    {
        int c = 0;
        for (int i = 0; i < n; i++)
        {
            double val = EvaluarFuncion(funcId, param, i);
            byte predicted = (byte)(((int)Math.Round(val) % 256 + 256) % 256);
            if (predicted == datos[i]) c++;
        }
        return c;
    }

    static (byte, int, double[], int)? Max(
        (byte, int, double[], int)? actual, byte tipo, int id, double[] param, int correctos)
    {
        if (correctos <= (actual?.Item4 ?? 0)) return actual;
        return (tipo, id, param, correctos);
    }

    // Motor de combinación greedy
    public static (List<(byte tipo, int id, double[] param)> generadores,
        byte[] rawBytes, int[] rawPositions)
        Descomponer(byte[] bloque, int maxGeneradores = 8)
    {
        int n = bloque.Length;
        var generadores = new List<(byte, int, double[])>();
        byte[] residual = new byte[n];
        Buffer.BlockCopy(bloque, 0, residual, 0, n);

        for (int iter = 0; iter < maxGeneradores; iter++)
        {
            var mejor = MejorGenerador(residual, permitirMicro: false);
            if (mejor == null) break;
            var (tipo, id, param, correctos) = mejor.Value;
            if (correctos < n / 5) break;
            int pc = tipo == TIPO_FUNCION ? NumParametrosFuncion(id) : NumParametrosMicro(id);
            if (correctos <= 3 + pc * 4) break;

            generadores.Add((tipo, id, param));

            if (tipo == TIPO_FUNCION)
            {
                for (int i = 0; i < n; i++)
                {
                    double val = EvaluarFuncion(id, param, i);
                    byte predicted = (byte)(((int)Math.Round(val) % 256 + 256) % 256);
                    residual[i] = (byte)((residual[i] - predicted + 256) & 0xFF);
                }
            }
            else
            {
                byte prev = bloque[0];
                for (int i = 0; i < n; i++)
                {
                    byte predicted = EvaluarMicro(id, param, i, prev);
                    residual[i] = (byte)((residual[i] - predicted + 256) & 0xFF);
                    prev = bloque[i]; // usar dato original para cadena
                }
            }
        }

        var rawPos = new List<int>();
        var rawVal = new List<byte>();
        for (int i = 0; i < n; i++)
            if (residual[i] != 0) { rawPos.Add(i); rawVal.Add(residual[i]); }

        return (generadores, rawVal.ToArray(), rawPos.ToArray());
    }

    // =====================================================================
    //  SECCIÓN 2: COMPRESIÓN DCT (estilo JPEG)
    //  Usa NWaves FastDct2 — DCT tipo II rápida
    // =====================================================================

    /// <summary>
    /// Comprime un bloque aplicando DCT-II, cuantizando y almacenando solo coeficientes no-cero.
    /// La DCT concentra la energía en pocos coeficientes → alta dispersión → compresión.
    /// </summary>
    static byte[] ComprimirBloqueDCT(byte[] bloque, int quantStep = 4)
    {
        int n = bloque.Length;
        // DCT requiere potencia de 2
        int dctSize = 1;
        while (dctSize < n) dctSize <<= 1;

        float[] input = new float[dctSize];
        for (int i = 0; i < n; i++) input[i] = bloque[i];

        // Aplicar DCT-II
        var dct = new FastDct2(dctSize);
        float[] coefficients = new float[dctSize];
        dct.Direct(input, coefficients);

        // Cuantizar: dividir por quantStep y redondear a int
        int[] quantized = new int[dctSize];
        int nonZero = 0;
        for (int i = 0; i < dctSize; i++)
        {
            int q = (int)Math.Round(coefficients[i] / quantStep);
            quantized[i] = q;
            if (q != 0) nonZero++;
        }

        // Codificar: [dctSize: uint16][quantStep: uint16][nonZero: uint16]
        //   { [pos: uint16][value: int16] } × nonZero
        using var ms = new MemoryStream();
        WriteU16(ms, (ushort)dctSize);
        WriteU16(ms, (ushort)quantStep);
        WriteU16(ms, (ushort)nonZero);
        for (int i = 0; i < dctSize; i++)
        {
            if (quantized[i] != 0)
            {
                WriteU16(ms, (ushort)i);
                WriteI16(ms, (short)Math.Clamp(quantized[i], short.MinValue, short.MaxValue));
            }
        }
        return ms.ToArray();
    }

    static byte[] DescomprimirBloqueDCT(byte[] data, int originalLen)
    {
        using var ms = new MemoryStream(data);
        int dctSize = ReadU16(ms);
        int quantStep = ReadU16(ms);
        int nonZero = ReadU16(ms);

        float[] coefficients = new float[dctSize];
        for (int i = 0; i < nonZero; i++)
        {
            int pos = ReadU16(ms);
            short val = ReadI16(ms);
            coefficients[pos] = val * quantStep;
        }

        // Invertir DCT
        var dct = new FastDct2(dctSize);
        float[] reconstructed = new float[dctSize];
        dct.Inverse(coefficients, reconstructed);

        // Convertir a bytes (solo los originales)
        byte[] output = new byte[originalLen];
        for (int i = 0; i < originalLen; i++)
            output[i] = (byte)Math.Clamp((int)Math.Round(reconstructed[i]), 0, 255);

        return output;
    }

    // =====================================================================
    //  SECCIÓN 3: COMPRESIÓN WAVELET (estilo JPEG2000)
    //  Usa NWaves Fwt con Daubechies db4
    // =====================================================================

    static byte[] ComprimirBloqueWavelet(byte[] bloque, int umbral = 8)
    {
        int n = bloque.Length;
        // FWT requiere tamaño par (preferiblemente potencia de 2)
        int fwtSize = 1;
        while (fwtSize < n) fwtSize <<= 1;

        float[] input = new float[fwtSize];
        for (int i = 0; i < n; i++) input[i] = bloque[i];

        // Aplicar FWT con Daubechies db4
        var fwt = new Fwt(fwtSize, new Wavelet("db4"));
        float[] output = new float[fwtSize];
        fwt.Direct(input, output);

        // Umbralizar: poner a cero coeficientes pequeños
        int[] quantized = new int[fwtSize];
        int nonZero = 0;
        for (int i = 0; i < fwtSize; i++)
        {
            int q = (int)Math.Round(output[i]);
            if (Math.Abs(q) < umbral) q = 0;
            quantized[i] = q;
            if (q != 0) nonZero++;
        }

        // Codificar
        using var ms = new MemoryStream();
        WriteU16(ms, (ushort)fwtSize);
        WriteU16(ms, (ushort)umbral);
        WriteU16(ms, (ushort)nonZero);
        for (int i = 0; i < fwtSize; i++)
        {
            if (quantized[i] != 0)
            {
                WriteU16(ms, (ushort)i);
                WriteI16(ms, (short)Math.Clamp(quantized[i], short.MinValue, short.MaxValue));
            }
        }
        return ms.ToArray();
    }

    static byte[] DescomprimirBloqueWavelet(byte[] data, int originalLen)
    {
        using var ms = new MemoryStream(data);
        int fwtSize = ReadU16(ms);
        int umbral = ReadU16(ms);
        int nonZero = ReadU16(ms);

        float[] coefficients = new float[fwtSize];
        for (int i = 0; i < nonZero; i++)
        {
            int pos = ReadU16(ms);
            short val = ReadI16(ms);
            coefficients[pos] = val;
        }

        // Invertir FWT
        var fwt = new Fwt(fwtSize, new Wavelet("db4"));
        float[] reconstructed = new float[fwtSize];
        fwt.Inverse(coefficients, reconstructed);

        byte[] output = new byte[originalLen];
        for (int i = 0; i < originalLen; i++)
            output[i] = (byte)Math.Clamp((int)Math.Round(reconstructed[i]), 0, 255);

        return output;
    }

    // =====================================================================
    //  SECCIÓN 4: STREAM COMPLETO — Los 3 métodos con selección automática
    // =====================================================================

    // Format markers
    public const byte MARKER_MATCHING_PURSUIT = 0x55;
    public const byte MARKER_DCT = 0x56;
    public const byte MARKER_WAVELET = 0x57;

    /// <summary>
    /// Comprime los datos. Modo lossless: solo Matching Pursuit.
    /// DCT y Wavelets están disponibles como métodos lossy (no se usan automáticamente).
    /// </summary>
    public static byte[] ComprimirStream(byte[] data, int tamanoBloque = 1024)
    {
        // Matching Pursuit es el único método lossless
        // DCT y Wavelets son lossy (cuantizan coeficientes) — se guardan para modo lossy
        return ComprimirMatchingPursuit(data, tamanoBloque);
    }

    /// <summary>
    /// Detecta el formato por el marker y descomprime.
    /// </summary>
    public static byte[] DescomprimirStream(byte[] compressedData)
    {
        if (compressedData.Length == 0) throw new InvalidDataException("Stream vacío.");
        byte marker = compressedData[0];

        return marker switch
        {
            MARKER_MATCHING_PURSUIT => DescomprimirMatchingPursuit(compressedData),
            MARKER_DCT => DescomprimirDCT(compressedData),
            MARKER_WAVELET => DescomprimirWavelet(compressedData),
            _ => throw new InvalidDataException($"Marker desconocido: 0x{marker:X2}")
        };
    }

    /// <summary>Devuelve el nombre del método usado en el stream comprimido.</summary>
    public static string MetodoUsado(byte[] compressedData)
    {
        if (compressedData.Length == 0) return "vacío";
        return compressedData[0] switch
        {
            MARKER_MATCHING_PURSUIT => "Matching Pursuit (funciones+microcódigo)",
            MARKER_DCT => "DCT (transformada coseno, estilo JPEG)",
            MARKER_WAVELET => "Wavelets (Daubechies db4, estilo JPEG2000)",
            _ => $"Desconocido (0x{compressedData[0]:X2})"
        };
    }

    // --- Matching Pursuit ---

    static byte[] ComprimirMatchingPursuit(byte[] data, int tamanoBloque)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(MARKER_MATCHING_PURSUIT);
        WriteU16(ms, (ushort)tamanoBloque);
        WriteI32(ms, data.Length);

        int numBlocks = (data.Length + tamanoBloque - 1) / tamanoBloque;
        WriteI32(ms, numBlocks);

        for (int b = 0; b < numBlocks; b++)
        {
            int start = b * tamanoBloque;
            int len = Math.Min(tamanoBloque, data.Length - start);
            byte[] bloque = new byte[len];
            Buffer.BlockCopy(data, start, bloque, 0, len);

            var (generadores, rawBytes, rawPositions) = Descomponer(bloque);

            int coste = 0;
            foreach (var (tipo, id, param) in generadores)
            {
                int pc = tipo == TIPO_FUNCION ? NumParametrosFuncion(id) : NumParametrosMicro(id);
                coste += 3 + pc * 4;
            }
            coste += 4 + rawBytes.Length * 3;

            if (generadores.Count > 0 && coste < len)
            {
                ms.WriteByte(0x01); // funcional
                CodificarMP(ms, generadores, rawBytes, rawPositions);
            }
            else
            {
                ms.WriteByte(0x00); // raw
                ms.Write(bloque, 0, bloque.Length);
            }
        }
        return ms.ToArray();
    }

    static byte[] DescomprimirMatchingPursuit(byte[] data)
    {
        using var ms = new MemoryStream(data);
        ms.ReadByte(); // marker
        int tamanoBloque = ReadU16(ms);
        int totalLen = ReadI32(ms);
        int numBlocks = ReadI32(ms);

        using var br = new BinaryReader(ms);
        var output = new List<byte>();

        for (int b = 0; b < numBlocks; b++)
        {
            int type = ms.ReadByte();
            int actualLen = Math.Min(tamanoBloque, totalLen - output.Count);
            if (type == 0x01)
                output.AddRange(DecodificarMP(br, actualLen));
            else
            {
                output.AddRange(br.ReadBytes(actualLen));
            }
        }
        return output.ToArray();
    }

    // --- DCT stream ---

    static byte[] ComprimirDCT(byte[] data, int tamanoBloque)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(MARKER_DCT);
        WriteU16(ms, (ushort)tamanoBloque);
        WriteI32(ms, data.Length);

        int numBlocks = (data.Length + tamanoBloque - 1) / tamanoBloque;
        WriteI32(ms, numBlocks);

        // Probar diferentes quantization steps y elegir el mejor
        int mejorQ = 4;
        int mejorSize = int.MaxValue;

        // Muestrear el primer bloque para calibrar
        int sampleLen = Math.Min(tamanoBloque, data.Length);
        byte[] sample = new byte[sampleLen];
        Buffer.BlockCopy(data, 0, sample, 0, sampleLen);

        foreach (int q in new[] { 2, 4, 8, 16 })
        {
            byte[] compressed = ComprimirBloqueDCT(sample, q);
            if (compressed.Length < mejorSize) { mejorSize = compressed.Length; mejorQ = q; }
        }

        ms.WriteByte((byte)mejorQ);

        for (int b = 0; b < numBlocks; b++)
        {
            int start = b * tamanoBloque;
            int len = Math.Min(tamanoBloque, data.Length - start);
            byte[] bloque = new byte[len];
            Buffer.BlockCopy(data, start, bloque, 0, len);

            byte[] dctBlock = ComprimirBloqueDCT(bloque, mejorQ);

            // Si DCT no comprime, guardar raw
            if (dctBlock.Length >= len)
            {
                ms.WriteByte(0x00); // raw
                ms.Write(bloque, 0, bloque.Length);
            }
            else
            {
                ms.WriteByte(0x01); // DCT
                WriteU16(ms, (ushort)dctBlock.Length);
                ms.Write(dctBlock, 0, dctBlock.Length);
            }
        }
        return ms.ToArray();
    }

    static byte[] DescomprimirDCT(byte[] data)
    {
        using var ms = new MemoryStream(data);
        ms.ReadByte(); // marker
        int tamanoBloque = ReadU16(ms);
        int totalLen = ReadI32(ms);
        int numBlocks = ReadI32(ms);
        int quantStep = ms.ReadByte();

        var output = new List<byte>();

        for (int b = 0; b < numBlocks; b++)
        {
            int type = ms.ReadByte();
            int originalLen = Math.Min(tamanoBloque, totalLen - output.Count);

            if (type == 0x01)
            {
                int blockLen = ReadU16(ms);
                byte[] blockData = new byte[blockLen];
                ms.Read(blockData, 0, blockLen);
                output.AddRange(DescomprimirBloqueDCT(blockData, originalLen));
            }
            else
            {
                byte[] bloque = new byte[originalLen];
                ms.Read(bloque, 0, originalLen);
                output.AddRange(bloque);
            }
        }
        return output.ToArray();
    }

    // --- Wavelet stream ---

    static byte[] ComprimirWavelet(byte[] data, int tamanoBloque)
    {
        using var ms = new MemoryStream();
        ms.WriteByte(MARKER_WAVELET);
        WriteU16(ms, (ushort)tamanoBloque);
        WriteI32(ms, data.Length);

        int numBlocks = (data.Length + tamanoBloque - 1) / tamanoBloque;
        WriteI32(ms, numBlocks);

        // Calibrar umbral con el primer bloque
        int mejorU = 8;
        int mejorSize = int.MaxValue;
        int sampleLen = Math.Min(tamanoBloque, data.Length);
        byte[] sample = new byte[sampleLen];
        Buffer.BlockCopy(data, 0, sample, 0, sampleLen);

        foreach (int u in new[] { 2, 4, 8, 16, 32 })
        {
            byte[] compressed = ComprimirBloqueWavelet(sample, u);
            if (compressed.Length < mejorSize) { mejorSize = compressed.Length; mejorU = u; }
        }

        ms.WriteByte((byte)mejorU);

        for (int b = 0; b < numBlocks; b++)
        {
            int start = b * tamanoBloque;
            int len = Math.Min(tamanoBloque, data.Length - start);
            byte[] bloque = new byte[len];
            Buffer.BlockCopy(data, start, bloque, 0, len);

            byte[] wavBlock = ComprimirBloqueWavelet(bloque, mejorU);

            if (wavBlock.Length >= len)
            {
                ms.WriteByte(0x00); // raw
                ms.Write(bloque, 0, bloque.Length);
            }
            else
            {
                ms.WriteByte(0x01); // wavelet
                WriteU16(ms, (ushort)wavBlock.Length);
                ms.Write(wavBlock, 0, wavBlock.Length);
            }
        }
        return ms.ToArray();
    }

    static byte[] DescomprimirWavelet(byte[] data)
    {
        using var ms = new MemoryStream(data);
        ms.ReadByte(); // marker
        int tamanoBloque = ReadU16(ms);
        int totalLen = ReadI32(ms);
        int numBlocks = ReadI32(ms);
        int umbral = ms.ReadByte();

        var output = new List<byte>();

        for (int b = 0; b < numBlocks; b++)
        {
            int type = ms.ReadByte();
            int originalLen = Math.Min(tamanoBloque, totalLen - output.Count);

            if (type == 0x01)
            {
                int blockLen = ReadU16(ms);
                byte[] blockData = new byte[blockLen];
                ms.Read(blockData, 0, blockLen);
                output.AddRange(DescomprimirBloqueWavelet(blockData, originalLen));
            }
            else
            {
                byte[] bloque = new byte[originalLen];
                ms.Read(bloque, 0, originalLen);
                output.AddRange(bloque);
            }
        }
        return output.ToArray();
    }

    // =====================================================================
    //  SECCIÓN 5: CODIFICACIÓN MATCHING PURSUIT
    // =====================================================================

    static void CodificarMP(MemoryStream ms,
        List<(byte tipo, int id, double[] param)> generadores,
        byte[] rawBytes, int[] rawPositions)
    {
        WriteU16(ms, (ushort)generadores.Count);
        foreach (var (tipo, id, param) in generadores)
        {
            ms.WriteByte(tipo);
            ms.WriteByte((byte)id);
            ms.WriteByte((byte)param.Length);
            foreach (double p in param)
            {
                byte[] bytes = BitConverter.GetBytes(p);
                ms.Write(bytes, 0, 8);
            }
        }
        int numRaw = rawBytes.Length;
        WriteI32(ms, numRaw);
        if (numRaw > 0)
        {
            foreach (int pos in rawPositions) WriteU16(ms, (ushort)pos);
            ms.Write(rawBytes, 0, rawBytes.Length);
        }
    }

    static byte[] DecodificarMP(BinaryReader br, int tamanoBloque)
    {
        byte[] output = new byte[tamanoBloque];
        int numGen = br.ReadByte() | (br.ReadByte() << 8);
        for (int g = 0; g < numGen; g++)
        {
            byte tipo = br.ReadByte();
            int id = br.ReadByte();
            int paramCount = br.ReadByte();
            double[] param = new double[paramCount];
            for (int p = 0; p < paramCount; p++) param[p] = br.ReadDouble();

            if (tipo == TIPO_FUNCION)
            {
                for (int i = 0; i < tamanoBloque; i++)
                {
                    double val = EvaluarFuncion(id, param, i);
                    byte predicted = (byte)(((int)Math.Round(val) % 256 + 256) % 256);
                    output[i] = (byte)((output[i] + predicted) & 0xFF);
                }
            }
            else
            {
                byte prev = 0;
                for (int i = 0; i < tamanoBloque; i++)
                {
                    byte predicted = EvaluarMicro(id, param, i, prev);
                    output[i] = (byte)((output[i] + predicted) & 0xFF);
                    prev = output[i];
                }
            }
        }
        int numRaw = br.ReadByte() | (br.ReadByte() << 8) | (br.ReadByte() << 16) | (br.ReadByte() << 24);
        if (numRaw > 0)
        {
            int[] positions = new int[numRaw];
            for (int i = 0; i < numRaw; i++) positions[i] = br.ReadByte() | (br.ReadByte() << 8);
            byte[] values = br.ReadBytes(numRaw);
            if (values.Length < numRaw) throw new InvalidDataException("Raw truncado.");
            for (int i = 0; i < numRaw; i++)
                output[positions[i]] = (byte)((output[positions[i]] + values[i]) & 0xFF);
        }
        return output;
    }

    // =====================================================================
    //  UTILIDADES
    // =====================================================================

    static int InversaModular(int a)
    {
        a = ((a % 256) + 256) % 256;
        if (a % 2 == 0) return -1;
        int t = 0, newT = 1, r = 256, newR = a;
        while (newR != 0)
        {
            int q = r / newR;
            (t, newT) = (newT, t - q * newT);
            (r, newR) = (newR, r - q * newR);
        }
        return r > 1 ? -1 : ((t % 256) + 256) % 256;
    }

    static void WriteU16(MemoryStream ms, ushort v)
    {
        ms.WriteByte((byte)v);
        ms.WriteByte((byte)(v >> 8));
    }

    static void WriteI16(MemoryStream ms, short v)
    {
        ms.WriteByte((byte)v);
        ms.WriteByte((byte)(v >> 8));
    }

    static void WriteI32(MemoryStream ms, int v)
    {
        ms.WriteByte((byte)v);
        ms.WriteByte((byte)(v >> 8));
        ms.WriteByte((byte)(v >> 16));
        ms.WriteByte((byte)(v >> 24));
    }

    static int ReadU16(Stream ms)
    {
        int b0 = ms.ReadByte(), b1 = ms.ReadByte();
        if (b0 < 0) throw new InvalidDataException("Stream truncado.");
        return b0 | (b1 << 8);
    }

    static short ReadI16(Stream ms)
    {
        int b0 = ms.ReadByte(), b1 = ms.ReadByte();
        if (b0 < 0) throw new InvalidDataException("Stream truncado.");
        return (short)(b0 | (b1 << 8));
    }

    static int ReadI32(Stream ms)
    {
        int b0 = ms.ReadByte(), b1 = ms.ReadByte(), b2 = ms.ReadByte(), b3 = ms.ReadByte();
        if (b0 < 0) throw new InvalidDataException("Stream truncado.");
        return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
    }
}
