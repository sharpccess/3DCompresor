using System;

namespace Compresor3D;

/// <summary>
/// Representa un cubo 3D de bytes usando un array plano para máximo rendimiento.
/// El mapeo de coordenadas (x, y, z) al índice plano es: x + y * Ancho + z * Ancho * Alto.
/// Soporta escaneo RLE en las tres direcciones (X, Y, Z) y compresión/descompresión adaptativa.
/// </summary>
public sealed class Cubo3D
{
    public int Ancho { get; }
    public int Alto { get; }
    public int Profundidad { get; }
    public int TotalBytes => Ancho * Alto * Profundidad;

    // Array plano: índice = x + y * Ancho + z * Ancho * Alto
    private readonly byte[] _flat;

    public Cubo3D(int ancho, int alto, int profundidad, byte[] datos)
    {
        Ancho = ancho;
        Alto = alto;
        Profundidad = profundidad;
        _flat = datos;
    }

    // ==================== ESCANEO RLE (para puntuación) ====================

    /// <summary>
    /// Cuenta el total de runs RLE en las tres direcciones.
    /// Un "run" es una secuencia máxima de bytes idénticos consecutivos en una línea.
    /// Menos runs = mejor compresión.
    /// </summary>
    public long ContarRunsEn3D()
    {
        long totalRuns = 0;

        // --- Dirección X: líneas a lo largo del eje X (Y y Z fijos) ---
        for (int z = 0; z < Profundidad; z++)
            for (int y = 0; y < Alto; y++)
            {
                int baseIdx = y * Ancho + z * Ancho * Alto;
                int end = baseIdx + Ancho;
                byte prev = _flat[baseIdx];
                int runs = 1;
                for (int i = baseIdx + 1; i < end; i++)
                {
                    if (_flat[i] != prev) { runs++; prev = _flat[i]; }
                }
                totalRuns += runs;
            }

        // --- Dirección Y: líneas a lo largo del eje Y (X y Z fijos) ---
        for (int z = 0; z < Profundidad; z++)
            for (int x = 0; x < Ancho; x++)
            {
                int idx = x + z * Ancho * Alto;
                byte prev = _flat[idx];
                int runs = 1;
                for (int y = 1; y < Alto; y++)
                {
                    idx += Ancho;
                    if (_flat[idx] != prev) { runs++; prev = _flat[idx]; }
                }
                totalRuns += runs;
            }

        // --- Dirección Z: líneas a lo largo del eje Z (X e Y fijos) ---
        int sliceSize = Ancho * Alto;
        for (int y = 0; y < Alto; y++)
            for (int x = 0; x < Ancho; x++)
            {
                int idx = x + y * Ancho;
                byte prev = _flat[idx];
                int runs = 1;
                for (int z = 1; z < Profundidad; z++)
                {
                    idx += sliceSize;
                    if (_flat[idx] != prev) { runs++; prev = _flat[idx]; }
                }
                totalRuns += runs;
            }

        return totalRuns;
    }

    /// <summary>
    /// Cuenta runs RLE en una sola dirección (más rápido que ContarRunsEn3D).
    /// dir: 0 = X, 1 = Y, 2 = Z
    /// </summary>
    public long ContarRuns(int dir)
    {
        long totalRuns = 0;

        switch (dir)
        {
            case 0: // Dirección X
                for (int z = 0; z < Profundidad; z++)
                    for (int y = 0; y < Alto; y++)
                    {
                        int baseIdx = y * Ancho + z * Ancho * Alto;
                        int end = baseIdx + Ancho;
                        byte prev = _flat[baseIdx];
                        int runs = 1;
                        for (int i = baseIdx + 1; i < end; i++)
                        {
                            if (_flat[i] != prev) { runs++; prev = _flat[i]; }
                        }
                        totalRuns += runs;
                    }
                break;

            case 1: // Dirección Y
                for (int z = 0; z < Profundidad; z++)
                    for (int x = 0; x < Ancho; x++)
                    {
                        int idx = x + z * Ancho * Alto;
                        byte prev = _flat[idx];
                        int runs = 1;
                        for (int y = 1; y < Alto; y++)
                        {
                            idx += Ancho;
                            if (_flat[idx] != prev) { runs++; prev = _flat[idx]; }
                        }
                        totalRuns += runs;
                    }
                break;

            case 2: // Dirección Z
                int sliceSize = Ancho * Alto;
                for (int y = 0; y < Alto; y++)
                    for (int x = 0; x < Ancho; x++)
                    {
                        int idx = x + y * Ancho;
                        byte prev = _flat[idx];
                        int runs = 1;
                        for (int z = 1; z < Profundidad; z++)
                        {
                            idx += sliceSize;
                            if (_flat[idx] != prev) { runs++; prev = _flat[idx]; }
                        }
                        totalRuns += runs;
                    }
                break;
        }

        return totalRuns;
    }

    // ==================== COMPRESIÓN ====================

    /// <summary>
    /// Comprime el cubo usando PackBits híbrido en una sola dirección (0=X, 1=Y, 2=Z).
    /// PackBits escribe bytes literales sin expansión y solo comprime repeticiones (runs ≥ 3).
    /// Formato del stream: [totalLíneas: int32] [dir: 1 byte] [PackBits] [dir] [PackBits] ...
    /// </summary>
    public byte[] Comprimir(out long compressedSize, int direccion = 0)
    {
        using var ms = new MemoryStream();
        int sliceSize = Ancho * Alto;

        switch (direccion)
        {
            case 0: // Dirección X: Y*Z líneas, cada una de longitud Ancho
                WriteInt32(ms, Alto * Profundidad);
                for (int z = 0; z < Profundidad; z++)
                    for (int y = 0; y < Alto; y++)
                    {
                        int start = y * Ancho + z * sliceSize;
                        ms.WriteByte(0); // dirección X
                        PackBitsCompressToStream(ms, _flat, start, 1, Ancho);
                    }
                break;

            case 1: // Dirección Y: X*Z líneas, cada una de longitud Alto
                WriteInt32(ms, Ancho * Profundidad);
                for (int z = 0; z < Profundidad; z++)
                    for (int x = 0; x < Ancho; x++)
                    {
                        int start = x + z * sliceSize;
                        ms.WriteByte(1); // dirección Y
                        PackBitsCompressToStream(ms, _flat, start, Ancho, Alto);
                    }
                break;

            case 2: // Dirección Z: X*Y líneas, cada una de longitud Profundidad
                WriteInt32(ms, Ancho * Alto);
                for (int y = 0; y < Alto; y++)
                    for (int x = 0; x < Ancho; x++)
                    {
                        int start = x + y * Ancho;
                        ms.WriteByte(2); // dirección Z
                        PackBitsCompressToStream(ms, _flat, start, sliceSize, Profundidad);
                    }
                break;

            default:
                throw new ArgumentException($"Dirección inválida: {direccion}");
        }

        compressedSize = ms.Length;
        return ms.ToArray();
    }

    // ==================== DESCOMPRESIÓN ====================

    /// <summary>
    /// Descomprime datos producidos por Comprimir(), reconstruyendo el array plano original.
    /// Usa contadores independientes por dirección para soportar cualquier mezcla de líneas.
    /// </summary>
    public static byte[] Descomprimir(byte[] compressedData, int ancho, int alto, int profundidad)
    {
        byte[] flat = new byte[ancho * alto * profundidad];
        int sliceSize = ancho * alto;

        // Contadores independientes por dirección (soporta formatos mixtos)
        int xLineIdx = 0, yLineIdx = 0, zLineIdx = 0;

        using var ms = new MemoryStream(compressedData);
        int totalLines = ReadInt32(ms);

        for (int i = 0; i < totalLines; i++)
        {
            int dirByte = ms.ReadByte();
            if (dirByte < 0) throw new InvalidDataException("Datos comprimidos truncados.");
            byte dir = (byte)dirByte;

            int lineLen = dir switch
            {
                0 => ancho,
                1 => alto,
                2 => profundidad,
                _ => throw new InvalidDataException($"Dirección inválida: {dir}")
            };

            byte[] lineData = PackBitsDecompressFromStream(ms, lineLen);

            switch (dir)
            {
                case 0: // Línea X: (0, y, z) → stride 1
                    {
                        int y = xLineIdx % alto;
                        int z = xLineIdx / alto;
                        int start = y * ancho + z * sliceSize;
                        Buffer.BlockCopy(lineData, 0, flat, start, lineLen);
                        xLineIdx++;
                    }
                    break;

                case 1: // Línea Y: (x, 0, z) → stride ancho
                    {
                        int x = yLineIdx % ancho;
                        int z = yLineIdx / ancho;
                        int baseIdx = x + z * sliceSize;
                        for (int y = 0; y < lineLen; y++)
                            flat[baseIdx + y * ancho] = lineData[y];
                        yLineIdx++;
                    }
                    break;

                case 2: // Línea Z: (x, y, 0) → stride sliceSize
                    {
                        int x = zLineIdx % ancho;
                        int y = zLineIdx / ancho;
                        int baseIdx = x + y * ancho;
                        for (int z = 0; z < lineLen; z++)
                            flat[baseIdx + z * sliceSize] = lineData[z];
                        zLineIdx++;
                    }
                    break;
            }
        }

        return flat;
    }

    // ==================== MÉTODOS AUXILIRES INTERNOS ====================

    /// <summary>
    /// Cuenta runs RLE en una línea que comienza en startIdx, en la dirección dada.
    /// No asigna memoria (solo recorre el array plano).
    /// </summary>
    private int CountRunsInLine(int startIdx, int dir, int lineLen)
    {
        byte prev = _flat[startIdx];
        int runs = 1;

        switch (dir)
        {
            case 0: // X: stride = 1
                for (int i = 1; i < lineLen; i++)
                {
                    byte cur = _flat[startIdx + i];
                    if (cur != prev) { runs++; prev = cur; }
                }
                break;

            case 1: // Y: stride = Ancho
                int idxY = startIdx;
                for (int i = 1; i < lineLen; i++)
                {
                    idxY += Ancho;
                    if (_flat[idxY] != prev) { runs++; prev = _flat[idxY]; }
                }
                break;

            case 2: // Z: stride = Ancho * Alto
                int idxZ = startIdx;
                int stride = Ancho * Alto;
                for (int i = 1; i < lineLen; i++)
                {
                    idxZ += stride;
                    if (_flat[idxZ] != prev) { runs++; prev = _flat[idxZ]; }
                }
                break;
        }

        return runs;
    }

    /// <summary>
    /// Lee una línea del cubo en la dirección especificada, devolviendo un array nuevo.
    /// </summary>
    private byte[] ReadLine(int startIdx, int dir, int lineLen)
    {
        byte[] line = new byte[lineLen];

        switch (dir)
        {
            case 0: // X: contiguo
                Buffer.BlockCopy(_flat, startIdx, line, 0, lineLen);
                break;

            case 1: // Y: stride = Ancho
                int idxY = startIdx;
                for (int i = 0; i < lineLen; i++)
                {
                    line[i] = _flat[idxY];
                    idxY += Ancho;
                }
                break;

            case 2: // Z: stride = Ancho * Alto
                int idxZ = startIdx;
                int stride = Ancho * Alto;
                for (int i = 0; i < lineLen; i++)
                {
                    line[i] = _flat[idxZ];
                    idxZ += stride;
                }
                break;
        }

        return line;
    }

    // ==================== PACKBITS HÍBRIDO ====================

    /// <summary>
    /// PackBits híbrido: escribe literales sin expansión y comprime solo runs ≥ 3.
    /// Formato:
    ///   header 0..127  → (header+1) bytes literales siguientes
    ///   header -1..-127 → repetir el siguiente byte (1-header) veces = runs de 2..128
    /// Datos aleatorios: ~0.8% de overhead (no 100% del RLE puro).
    /// Datos repetitivos: comprime hasta 128:1.
    /// Escribe directamente al stream de salida.
    /// </summary>
    private static void PackBitsCompressToStream(MemoryStream output, byte[] source, int start, int stride, int length)
    {
        if (length == 0) return;

        // Leer la línea completa (necesario para strides no contiguos)
        byte[] line = new byte[length];
        if (stride == 1)
        {
            Buffer.BlockCopy(source, start, line, 0, length);
        }
        else
        {
            int idx = start;
            for (int i = 0; i < length; i++)
            {
                line[i] = source[idx];
                idx += stride;
            }
        }

        // Codificar con PackBits
        int pos = 0;
        while (pos < length)
        {
            // Buscar run de bytes iguales
            int runLen = 1;
            while (pos + runLen < length && line[pos + runLen] == line[pos] && runLen < 128)
                runLen++;

            if (runLen >= 3)
            {
                // Run: header = -(runLen - 1), seguido del valor
                output.WriteByte((byte)(-(runLen - 1) & 0xFF));
                output.WriteByte(line[pos]);
                pos += runLen;
            }
            else
            {
                // Literal: acumular bytes no repetitivos
                int litStart = pos;
                pos++; // avanzar más allá del primer byte (que no tiene run)
                while (pos < length)
                {
                    // Comprobar si empieza un run desde aquí
                    int ahead = 1;
                    while (pos + ahead < length && line[pos + ahead] == line[pos] && ahead < 128)
                        ahead++;
                    if (ahead >= 3) break; // empezar un run en la siguiente iteración

                    pos++;
                    if (pos - litStart >= 128) break; // máximo 128 literales por chunk
                }

                int litCount = pos - litStart;
                output.WriteByte((byte)(litCount - 1)); // header 0..127
                output.Write(line, litStart, litCount);
            }
        }
    }

    /// <summary>
    /// Descomprime PackBits desde un MemoryStream, devolviendo exactamente expectedLen bytes.
    /// </summary>
    private static byte[] PackBitsDecompressFromStream(MemoryStream ms, int expectedLen)
    {
        byte[] result = new byte[expectedLen];
        int pos = 0;

        while (pos < expectedLen)
        {
            int headerByte = ms.ReadByte();
            if (headerByte < 0)
                throw new InvalidDataException("Datos PackBits truncados.");

            sbyte header = (sbyte)headerByte;

            if (header >= 0)
            {
                // Literal: leer (header + 1) bytes
                int count = header + 1;
                int remaining = expectedLen - pos;
                int toRead = Math.Min(count, remaining);
                int read = ms.Read(result, pos, toRead);
                if (read < toRead)
                    throw new InvalidDataException("Datos PackBits truncados en literal.");
                pos += toRead;
            }
            else if (header > -128)
            {
                // Run: repetir el siguiente byte (1 - header) veces
                int valByte = ms.ReadByte();
                if (valByte < 0)
                    throw new InvalidDataException("Datos PackBits truncados en run.");
                int count = 1 - header; // 2..128
                byte val = (byte)valByte;
                for (int j = 0; j < count && pos < expectedLen; j++)
                    result[pos++] = val;
            }
            // header == -128: no-op (compatibilidad con PackBits estándar)
        }

        return result;
    }

    private static void WriteInt32(MemoryStream ms, int value)
    {
        ms.WriteByte((byte)(value & 0xFF));
        ms.WriteByte((byte)((value >> 8) & 0xFF));
        ms.WriteByte((byte)((value >> 16) & 0xFF));
        ms.WriteByte((byte)((value >> 24) & 0xFF));
    }

    private static int ReadInt32(MemoryStream ms)
    {
        int b0 = ms.ReadByte();
        int b1 = ms.ReadByte();
        int b2 = ms.ReadByte();
        int b3 = ms.ReadByte();
        if (b0 < 0 || b1 < 0 || b2 < 0 || b3 < 0)
            throw new InvalidDataException("Datos truncados al leer entero.");
        return b0 | (b1 << 8) | (b2 << 16) | (b3 << 24);
    }
}
