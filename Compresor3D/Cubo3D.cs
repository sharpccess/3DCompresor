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

    // ==================== DEDUPLICACIÓN DE LÍNEAS ====================

    /// <summary>
    /// Cuenta cuántas líneas ÚNICAS hay en la dirección dada.
    /// Menos líneas únicas = más redundancia estructural = mejor compresión por deduplicación.
    /// dir: 0 = X, 1 = Y, 2 = Z
    /// </summary>
    public int ContarLineasUnicas(int dir)
    {
        var uniques = new HashSet<byte[]>(ByteArrayComparer.Instance);
        int sliceSize = Ancho * Alto;

        switch (dir)
        {
            case 0: // X: Y*Z líneas de longitud Ancho
                for (int z = 0; z < Profundidad; z++)
                    for (int y = 0; y < Alto; y++)
                    {
                        byte[] line = new byte[Ancho];
                        Buffer.BlockCopy(_flat, y * Ancho + z * sliceSize, line, 0, Ancho);
                        uniques.Add(line);
                    }
                break;

            case 1: // Y: X*Z líneas de longitud Alto
                for (int z = 0; z < Profundidad; z++)
                    for (int x = 0; x < Ancho; x++)
                        uniques.Add(ReadLine(x + z * sliceSize, 1, Alto));
                break;

            case 2: // Z: X*Y líneas de longitud Profundidad
                for (int y = 0; y < Alto; y++)
                    for (int x = 0; x < Ancho; x++)
                        uniques.Add(ReadLine(x + y * Ancho, 2, Profundidad));
                break;
        }

        return uniques.Count;
    }

    /// <summary>
    /// Devuelve (totalLineas, lineasUnicas, longitudLinea) para la dirección dada.
    /// </summary>
    public (int total, int unicas, int longitud) InfoLineas(int dir)
    {
        int total = dir switch { 0 => Alto * Profundidad, 1 => Ancho * Profundidad, 2 => Ancho * Alto };
        int unicas = ContarLineasUnicas(dir);
        int longitud = dir switch { 0 => Ancho, 1 => Alto, 2 => Profundidad };
        return (total, unicas, longitud);
    }

    // ==================== COMPRESIÓN CON DEDUPLICACIÓN ====================

    /// <summary>
    /// Comprime el cubo usando deduplicación de líneas + PackBits.
    /// 
    /// Estrategia: en la dirección elegida, muchas líneas pueden ser idénticas.
    /// En vez de almacenar cada línea, guardamos solo las ÚNICAS (comprimidas con PackBits)
    /// y un mapa de índices que referencia cada línea original a su versión única.
    /// 
    /// Formato:
    ///   [direccion: 1 byte]
    ///   [totalLineas: int32]
    ///   [lineasUnicas: int32]
    ///   [longitudLinea: int32]
    ///   [PackBits de cada línea única, concatenados]
    ///   [Índices: totalLineas × int32]
    /// </summary>
    public byte[] Comprimir(out long compressedSize, int direccion = 0)
    {
        using var ms = new MemoryStream();
        int sliceSize = Ancho * Alto;

        int lineLen = direccion switch { 0 => Ancho, 1 => Alto, 2 => Profundidad };
        int totalLines = direccion switch { 0 => Alto * Profundidad, 1 => Ancho * Profundidad, 2 => Ancho * Alto };

        // Paso 1: Recorrer todas las líneas, deduplicar y construir mapa de índices
        var uniqueLines = new List<byte[]>();
        var lineToIndex = new Dictionary<byte[], int>(ByteArrayComparer.Instance);
        int[] indices = new int[totalLines];

        for (int i = 0; i < totalLines; i++)
        {
            byte[] line = direccion switch
            {
                0 => ReadLineContiguo((i % Alto) * Ancho + (i / Alto) * sliceSize, lineLen),
                1 => ReadLine((i % Ancho) + (i / Ancho) * sliceSize, 1, lineLen),
                2 => ReadLine((i % Ancho) + (i / Ancho) * Ancho, 2, lineLen),
                _ => throw new ArgumentException()
            };

            if (!lineToIndex.TryGetValue(line, out int idx))
            {
                idx = uniqueLines.Count;
                lineToIndex[line] = idx;
                uniqueLines.Add(line);
            }
            indices[i] = idx;
        }

        // Paso 2: Escribir cabecera
        ms.WriteByte((byte)direccion);
        WriteInt32(ms, totalLines);
        WriteInt32(ms, uniqueLines.Count);
        WriteInt32(ms, lineLen);

        // Paso 3: Escribir líneas únicas comprimidas con PackBits
        foreach (var ul in uniqueLines)
        {
            // Para líneas únicas, escribir directamente (son byte[] contiguos)
            // Usamos PackBits para comprimir runs dentro de cada línea
            PackBitsCompressBufferToStream(ms, ul);
        }

        // Paso 4: Escribir mapa de índices
        foreach (int idx in indices)
            WriteInt32(ms, idx);

        compressedSize = ms.Length;
        return ms.ToArray();
    }

    /// <summary>
    /// Comprime con PackBits directo (sin deduplicación).
    /// Formato: [0x80|dir: 1 byte] [totalLineas: int32] [lineLen: int32] [PackBits de cada línea]
    /// Más eficiente que dedup cuando las líneas son cortas y hay pocos duplicados.
    /// </summary>
    public byte[] ComprimirPackBitsDirecto(out long compressedSize, int direccion)
    {
        using var ms = new MemoryStream();
        int sliceSize = Ancho * Alto;

        int lineLen = direccion switch { 0 => Ancho, 1 => Alto, 2 => Profundidad, _ => throw new ArgumentException() };
        int totalLines = direccion switch { 0 => Alto * Profundidad, 1 => Ancho * Profundidad, 2 => Ancho * Alto, _ => throw new ArgumentException() };

        // Marker de formato directo (0x80 | dir)
        ms.WriteByte((byte)(0x80 | direccion));
        WriteInt32(ms, totalLines);
        WriteInt32(ms, lineLen);

        for (int i = 0; i < totalLines; i++)
        {
            int start = direccion switch
            {
                0 => (i % Alto) * Ancho + (i / Alto) * sliceSize,
                1 => (i % Ancho) + (i / Ancho) * sliceSize,
                2 => (i % Ancho) + (i / Ancho) * Ancho,
                _ => 0
            };
            int stride = direccion switch { 0 => 1, 1 => Ancho, 2 => sliceSize, _ => 1 };
            PackBitsCompressToStream(ms, _flat, start, stride, lineLen);
        }

        compressedSize = ms.Length;
        return ms.ToArray();
    }

    // ==================== DESCOMPRESIÓN ====================

    /// <summary>
    /// Descomprime datos producidos por Comprimir() o ComprimirPackBitsDirecto().
    /// Detecta automáticamente el formato por el primer byte:
    ///   - 0x00-0x02: formato deduplicación
    ///   - 0x80-0x82: formato PackBits directo
    /// </summary>
    public static byte[] Descomprimir(byte[] compressedData, int ancho, int alto, int profundidad)
    {
        using var ms = new MemoryStream(compressedData);
        int firstByte = ms.ReadByte();
        if (firstByte < 0) throw new InvalidDataException("Datos truncados.");

        if (firstByte >= 0x80)
            return DescomprimirPackBitsDirecto(ms, (byte)(firstByte & 0x7F), ancho, alto, profundidad);
        else
            return DescomprimirDedup(ms, (byte)firstByte, ancho, alto, profundidad);
    }

    /// <summary>Descomprime formato deduplicación.</summary>
    private static byte[] DescomprimirDedup(MemoryStream ms, byte dir, int ancho, int alto, int profundidad)
    {
        byte[] flat = new byte[ancho * alto * profundidad];
        int sliceSize = ancho * alto;

        int totalLines = ReadInt32(ms);
        int uniqueCount = ReadInt32(ms);
        int lineLen = ReadInt32(ms);

        // Leer y descomprimir líneas únicas
        byte[][] uniqueLines = new byte[uniqueCount][];
        for (int i = 0; i < uniqueCount; i++)
            uniqueLines[i] = PackBitsDecompressFromStream(ms, lineLen);

        // Leer mapa de índices
        int[] indices = new int[totalLines];
        for (int i = 0; i < totalLines; i++)
            indices[i] = ReadInt32(ms);

        // Reconstruir
        for (int i = 0; i < totalLines; i++)
        {
            byte[] lineData = uniqueLines[indices[i]];
            switch (dir)
            {
                case 0:
                    {
                        int y = i % alto, z = i / alto;
                        Buffer.BlockCopy(lineData, 0, flat, y * ancho + z * sliceSize, lineLen);
                    }
                    break;
                case 1:
                    {
                        int x = i % ancho, z = i / ancho;
                        int baseIdx = x + z * sliceSize;
                        for (int y = 0; y < lineLen; y++) flat[baseIdx + y * ancho] = lineData[y];
                    }
                    break;
                case 2:
                    {
                        int x = i % ancho, y = i / ancho;
                        int baseIdx = x + y * ancho;
                        for (int z = 0; z < lineLen; z++) flat[baseIdx + z * sliceSize] = lineData[z];
                    }
                    break;
            }
        }
        return flat;
    }

    /// <summary>Descomprime formato PackBits directo.</summary>
    private static byte[] DescomprimirPackBitsDirecto(MemoryStream ms, byte dir, int ancho, int alto, int profundidad)
    {
        byte[] flat = new byte[ancho * alto * profundidad];
        int sliceSize = ancho * alto;

        int totalLines = ReadInt32(ms);
        int lineLen = ReadInt32(ms);

        for (int i = 0; i < totalLines; i++)
        {
            byte[] lineData = PackBitsDecompressFromStream(ms, lineLen);
            switch (dir)
            {
                case 0:
                    {
                        int y = i % alto, z = i / alto;
                        Buffer.BlockCopy(lineData, 0, flat, y * ancho + z * sliceSize, lineLen);
                    }
                    break;
                case 1:
                    {
                        int x = i % ancho, z = i / ancho;
                        int baseIdx = x + z * sliceSize;
                        for (int y = 0; y < lineLen; y++) flat[baseIdx + y * ancho] = lineData[y];
                    }
                    break;
                case 2:
                    {
                        int x = i % ancho, y = i / ancho;
                        int baseIdx = x + y * ancho;
                        for (int z = 0; z < lineLen; z++) flat[baseIdx + z * sliceSize] = lineData[z];
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
    /// Lee una línea contigua del array plano (dirección X, stride=1).
    /// </summary>
    private byte[] ReadLineContiguo(int start, int length)
    {
        byte[] line = new byte[length];
        Buffer.BlockCopy(_flat, start, line, 0, length);
        return line;
    }

    // ==================== PACKBITS HÍBRIDO ====================

    /// <summary>
    /// Comprime un buffer byte[] completo con PackBits y escribe al stream.
    /// Versión simplificada para líneas ya extraídas (no necesita stride).
    /// </summary>
    private static void PackBitsCompressBufferToStream(MemoryStream output, byte[] data)
    {
        int length = data.Length;
        if (length == 0) return;

        int pos = 0;
        while (pos < length)
        {
            int runLen = 1;
            while (pos + runLen < length && data[pos + runLen] == data[pos] && runLen < 128)
                runLen++;

            if (runLen >= 3)
            {
                output.WriteByte((byte)(-(runLen - 1) & 0xFF));
                output.WriteByte(data[pos]);
                pos += runLen;
            }
            else
            {
                int litStart = pos;
                pos++;
                while (pos < length)
                {
                    int ahead = 1;
                    while (pos + ahead < length && data[pos + ahead] == data[pos] && ahead < 128)
                        ahead++;
                    if (ahead >= 3) break;
                    pos++;
                    if (pos - litStart >= 128) break;
                }
                int litCount = pos - litStart;
                output.WriteByte((byte)(litCount - 1));
                output.Write(data, litStart, litCount);
            }
        }
    }

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

/// <summary>
/// Comparador de byte[] por contenido (para usar en HashSet/Dictionary).
/// Usa StructuralComparisons para comparación y hash eficientes.
/// </summary>
internal sealed class ByteArrayComparer : IEqualityComparer<byte[]>
{
    public static readonly ByteArrayComparer Instance = new();

    public bool Equals(byte[]? x, byte[]? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x == null || y == null) return false;
        if (x.Length != y.Length) return false;
        for (int i = 0; i < x.Length; i++)
            if (x[i] != y[i]) return false;
        return true;
    }

    public int GetHashCode(byte[] obj)
    {
        // FNV-1a: rápido y buena distribución
        int hash = unchecked((int)2166136261);
        for (int i = 0; i < obj.Length; i++)
            hash = unchecked((hash ^ obj[i]) * 16777619);
        return hash;
    }
}
