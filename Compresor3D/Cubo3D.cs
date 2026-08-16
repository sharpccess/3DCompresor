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
    /// Comprime el cubo almacenando todas las líneas en las 3 direcciones con RLE.
    /// Orden: primero todas las líneas X, luego Y, luego Z.
    /// Formato por línea: [dirección: 1 byte] [RLE: count, value, count, value, ...]
    /// Las líneas X se leen directamente del array plano (son contiguas).
    /// </summary>
    public byte[] Comprimir(out long compressedSize)
    {
        using var ms = new MemoryStream();
        int sliceSize = Ancho * Alto;
        // Buffer reutilizable para RLE (tamaño máximo = 2 * longitud de línea)
        byte[] rleBuf = new byte[Math.Max(Math.Max(Ancho, Alto), Profundidad) * 2];

        int xLineLen = Ancho, yLineLen = Alto, zLineLen = Profundidad;
        int totalLines = Alto * Profundidad + Ancho * Profundidad + Ancho * Alto;

        // Cabecera: total de líneas
        WriteInt32(ms, totalLines);

        // --- Líneas X: Y=fijo, Z=fijo. Base = (0, y, z) = y*Ancho + z*sliceSize ---
        for (int z = 0; z < Profundidad; z++)
            for (int y = 0; y < Alto; y++)
            {
                int xStart = y * Ancho + z * sliceSize;
                int rleLen = RleCompressBlock(_flat, xStart, xLineLen, rleBuf);
                ms.WriteByte(0); // dirección X
                ms.Write(rleBuf, 0, rleLen);
            }

        // --- Líneas Y: X=fijo, Z=fijo. Base = (x, 0, z) = x + z*sliceSize ---
        for (int z = 0; z < Profundidad; z++)
            for (int x = 0; x < Ancho; x++)
            {
                int yStart = x + z * sliceSize;
                var line = ReadLine(yStart, 1, yLineLen);
                int rleLen = RleCompressBlock(line, 0, line.Length, rleBuf);
                ms.WriteByte(1); // dirección Y
                ms.Write(rleBuf, 0, rleLen);
            }

        // --- Líneas Z: X=fijo, Y=fijo. Base = (x, y, 0) = x + y*Ancho ---
        for (int y = 0; y < Alto; y++)
            for (int x = 0; x < Ancho; x++)
            {
                int zStart = x + y * Ancho;  // (x, y, 0)
                var line = ReadLine(zStart, 2, zLineLen);
                int rleLen = RleCompressBlock(line, 0, line.Length, rleBuf);
                ms.WriteByte(2); // dirección Z
                ms.Write(rleBuf, 0, rleLen);
            }

        compressedSize = ms.Length;
        return ms.ToArray();
    }

    // ==================== DESCOMPRESIÓN ====================

    /// <summary>
    /// Descomprime datos producidos por Comprimir(), reconstruyendo el array plano original.
    /// </summary>
    public static byte[] Descomprimir(byte[] compressedData, int ancho, int alto, int profundidad)
    {
        byte[] flat = new byte[ancho * alto * profundidad];
        int sliceSize = ancho * alto;

        using var ms = new MemoryStream(compressedData);
        int totalLines = ReadInt32(ms);

        for (int lineIdx = 0; lineIdx < totalLines; lineIdx++)
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

            // Descomprimir RLE de esta línea
            byte[] lineData = RleDecompressStream(ms, lineLen);

            // Determinar la posición de inicio según el tipo de línea
            int lineIndexInDirection;
            switch (dir)
            {
                case 0: // Línea X
                    lineIndexInDirection = lineIdx;
                    {
                        int y = lineIndexInDirection % alto;
                        int z = lineIndexInDirection / alto;
                        int start = y * ancho + z * sliceSize;
                        Buffer.BlockCopy(lineData, 0, flat, start, lineLen);
                    }
                    break;

                case 1: // Línea Y
                    lineIndexInDirection = lineIdx - (alto * profundidad);
                    {
                        int x = lineIndexInDirection % ancho;
                        int z = lineIndexInDirection / ancho;
                        int baseIdx = x + z * sliceSize;
                        for (int y = 0; y < lineLen; y++)
                            flat[baseIdx + y * ancho] = lineData[y];
                    }
                    break;

                case 2: // Línea Z
                    lineIndexInDirection = lineIdx - (alto * profundidad) - (ancho * profundidad);
                    {
                        int x = lineIndexInDirection % ancho;
                        int y = lineIndexInDirection / ancho;
                        int baseIdx = x + y * ancho;
                        for (int z = 0; z < lineLen; z++)
                            flat[baseIdx + z * sliceSize] = lineData[z];
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

    /// <summary>
    /// RLE: comprime un bloque de bytes en un buffer pre-allocado.
    /// Formato: [count, value, count, value, ...] donde count es un byte (1-255).
    /// Devuelve la longitud del datos RLE escritos en el buffer.
    /// </summary>
    private static int RleCompressBlock(byte[] source, int offset, int length, byte[] output)
    {
        int outPos = 0;
        int end = offset + length;
        int i = offset;

        while (i < end)
        {
            byte val = source[i];
            int runLen = 1;
            while (i + runLen < end && source[i + runLen] == val && runLen < 255)
                runLen++;

            output[outPos++] = (byte)runLen;
            output[outPos++] = val;
            i += runLen;
        }

        return outPos;
    }

    /// <summary>
    /// RLE: descomprime desde un MemoryStream hacia un array del tamaño exacto de la línea.
    /// </summary>
    private static byte[] RleDecompressStream(MemoryStream ms, int expectedLen)
    {
        byte[] result = new byte[expectedLen];
        int pos = 0;

        while (pos < expectedLen)
        {
            int countByte = ms.ReadByte();
            int valByte = ms.ReadByte();
            if (countByte < 0 || valByte < 0)
                throw new InvalidDataException("Datos RLE truncados.");

            byte count = (byte)countByte;
            byte val = (byte)valByte;

            for (int j = 0; j < count && pos < expectedLen; j++)
                result[pos++] = val;
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
