using System;
using System.Collections.Generic;
using System.Linq;

namespace Compresor3D;

/// <summary>
/// Pipeline de transformaciones para reducir entropía ANTES de comprimir.
/// 
/// La clave: los datos crudos pueden tener entropía alta (7-8 bits/byte),
/// pero después de las transformaciones correctas, la entropía baja
/// drásticamente y los métodos de compresión pueden trabajar de verdad.
/// 
/// Transformaciones disponibles:
/// - Delta: d[i] = data[i] - data[i-1] mod 256 (suaviza datos correlacionados)
/// - MTF (Move-to-Front): bytes repetidos → muchos ceros
/// - Delta² (delta de delta): suaviza datos con tendencia lineal
/// - XOR predictivo: XOR con el byte anterior (patrones XOR → ceros)
/// - Ordenación por bloques: mini-BWT que agrupa valores similares
/// - Interleaved: reordena bytes por posición par/impar
/// </summary>
public static class Transformaciones
{
    // IDs de transformaciones (se guardan en el stream para descompresión)
    public const byte T_NONE = 0x00;
    public const byte T_DELTA = 0x01;
    public const byte T_MTF = 0x02;
    public const byte T_DELTA2 = 0x03;      // delta de delta
    public const byte T_XOR_PREV = 0x04;    // XOR con byte anterior
    public const byte T_SORT_BLOCKS = 0x05; // ordenación por bloques (mini-BWT)
    public const byte T_INTERLEAVE = 0x06;  // intercalado par/impar
    public const byte T_SUB_MOD256 = 0x07;  // restar media móvil
    public const byte T_BWT = 0x08;         // Burrows-Wheeler Transform (la "normalización tipo CD")
    public const byte T_RLE = 0x09;         // Run-Length Encoding post-BWT

    /// <summary>
    /// Pipeline completo: prueba múltiples combinaciones de transformaciones
    /// y devuelve la que produzca la menor entropía en el resultado.
    /// </summary>
    public static (byte[] transformed, byte[] transformIds, double entropyReduction)
        MejorarEntropia(byte[] datos)
    {
        if (datos.Length < 4) return (datos, new byte[] { T_NONE }, 0);

        double entropiaOriginal = CalcularEntropia(datos);

        // Probar cada transformación individual
        var candidatos = new List<(byte[] data, byte[] ids, double entropy)>();

        // 0: Sin transformación (baseline)
        candidatos.Add((datos, new byte[] { T_NONE }, entropiaOriginal));

        // 1: Delta simple
        {
            byte[] delta = AplicarDelta(datos);
            candidatos.Add((delta, new byte[] { T_DELTA }, CalcularEntropia(delta)));
        }

        // 2: Delta + MTF
        {
            byte[] delta = AplicarDelta(datos);
            byte[] mtf = AplicarMTF(delta);
            candidatos.Add((mtf, new byte[] { T_DELTA, T_MTF }, CalcularEntropia(mtf)));
        }

        // 3: Delta² (delta de delta)
        {
            byte[] d1 = AplicarDelta(datos);
            byte[] d2 = AplicarDelta(d1);
            candidatos.Add((d2, new byte[] { T_DELTA2 }, CalcularEntropia(d2)));
        }

        // 4: Delta² + MTF
        {
            byte[] d1 = AplicarDelta(datos);
            byte[] d2 = AplicarDelta(d1);
            byte[] mtf = AplicarMTF(d2);
            candidatos.Add((mtf, new byte[] { T_DELTA2, T_MTF }, CalcularEntropia(mtf)));
        }

        // 5: XOR predictivo
        {
            byte[] xor = AplicarXORPrev(datos);
            candidatos.Add((xor, new byte[] { T_XOR_PREV }, CalcularEntropia(xor)));
        }

        // 6: XOR predictivo + MTF
        {
            byte[] xor = AplicarXORPrev(datos);
            byte[] mtf = AplicarMTF(xor);
            candidatos.Add((mtf, new byte[] { T_XOR_PREV, T_MTF }, CalcularEntropia(mtf)));
        }

        // 7: Ordenación por bloques (mini-BWT)
        {
            byte[] sorted = AplicarSortBlocks(datos, 256);
            candidatos.Add((sorted, new byte[] { T_SORT_BLOCKS }, CalcularEntropia(sorted)));
        }

        // 8: Delta + Sort blocks
        {
            byte[] delta = AplicarDelta(datos);
            byte[] sorted = AplicarSortBlocks(delta, 256);
            candidatos.Add((sorted, new byte[] { T_DELTA, T_SORT_BLOCKS }, CalcularEntropia(sorted)));
        }

        // 9: Interleave + Delta
        {
            byte[] interleaved = AplicarInterleave(datos);
            byte[] delta = AplicarDelta(interleaved);
            candidatos.Add((delta, new byte[] { T_INTERLEAVE, T_DELTA }, CalcularEntropia(delta)));
        }

        // 10: Restar media móvil
        {
            byte[] sub = AplicarSubMediaMovil(datos, 16);
            candidatos.Add((sub, new byte[] { T_SUB_MOD256 }, CalcularEntropia(sub)));
        }

        // 11: XOR + Delta
        {
            byte[] xor = AplicarXORPrev(datos);
            byte[] delta = AplicarDelta(xor);
            candidatos.Add((delta, new byte[] { T_XOR_PREV, T_DELTA }, CalcularEntropia(delta)));
        }

        // ═══ BWT: la transformación "tipo CD" que normaliza entropía ═══
        // BWT es O(n log²n) — solo para bloques ≤ 4KB (costo/beneficio)
        // Probamos solo el pipeline clásico bzip2: BWT + MTF
        if (datos.Length <= 4096)
        {
            // 12: BWT + MTF (pipeline bzip2)
            {
                byte[] bwt = AplicarBWT(datos, out _);
                byte[] mtf = AplicarMTF(bwt);
                candidatos.Add((mtf, new byte[] { T_BWT, T_MTF }, CalcularEntropia(mtf)));
            }
        }

        // Elegir el que tenga menor entropía
        int mejorIdx = 0;
        double mejorEnt = entropiaOriginal;
        for (int i = 1; i < candidatos.Count; i++)
        {
            if (candidatos[i].entropy < mejorEnt)
            {
                mejorEnt = candidatos[i].entropy;
                mejorIdx = i;
            }
        }

        var mejor = candidatos[mejorIdx];
        return (mejor.data, mejor.ids, entropiaOriginal - mejor.entropy);
    }

    /// <summary>Revierte el pipeline de transformaciones.</summary>
    public static byte[] Revertir(byte[] datos, byte[] transformIds)
    {
        // Revertir en orden inverso
        byte[] current = datos;
        for (int i = transformIds.Length - 1; i >= 0; i--)
        {
            current = transformIds[i] switch
            {
                T_NONE => current,
                T_DELTA => RevertirDelta(current),
                T_MTF => RevertirMTF(current),
                T_DELTA2 => RevertirDelta(RevertirDelta(current)),
                T_XOR_PREV => RevertirXORPrev(current),
                T_SORT_BLOCKS => throw new NotSupportedException("SortBlocks no es reversible sin metadata"),
                T_INTERLEAVE => RevertirInterleave(current),
                T_SUB_MOD256 => RevertirSubMediaMovil(current, 16),
                T_BWT => RevertirBWT(current),  // lee primaryIndex de los primeros 4 bytes
                T_RLE => RevertirRLE(current),
                _ => throw new InvalidDataException($"Transformación desconocida: 0x{transformIds[i]:X2}")
            };
        }
        return current;
    }

    // ==================== TRANSFORMACIONES INDIVIDUALES ====================

    /// <summary>Delta encoding: d[i] = data[i] - data[i-1] mod 256.</summary>
    public static byte[] AplicarDelta(byte[] data)
    {
        int n = data.Length;
        byte[] result = new byte[n];
        result[0] = data[0];
        for (int i = 1; i < n; i++)
            result[i] = (byte)(data[i] - data[i - 1]);
        return result;
    }

    public static byte[] RevertirDelta(byte[] data)
    {
        int n = data.Length;
        byte[] result = new byte[n];
        result[0] = data[0];
        for (int i = 1; i < n; i++)
            result[i] = (byte)(result[i - 1] + data[i]);
        return result;
    }

    /// <summary>Move-to-Front: cada byte se busca en una lista, se emite su índice, y se mueve al frente.</summary>
    public static byte[] AplicarMTF(byte[] data)
    {
        int n = data.Length;
        byte[] result = new byte[n];
        // Lista inicial: 0, 1, 2, ..., 255
        byte[] list = new byte[256];
        for (int i = 0; i < 256; i++) list[i] = (byte)i;

        for (int i = 0; i < n; i++)
        {
            byte val = data[i];
            // Buscar posición
            int pos = 0;
            while (pos < 256 && list[pos] != val) pos++;
            result[i] = (byte)pos;
            // Mover al frente
            if (pos > 0)
            {
                for (int j = pos; j > 0; j--)
                    list[j] = list[j - 1];
                list[0] = val;
            }
        }
        return result;
    }

    public static byte[] RevertirMTF(byte[] data)
    {
        int n = data.Length;
        byte[] result = new byte[n];
        byte[] list = new byte[256];
        for (int i = 0; i < 256; i++) list[i] = (byte)i;

        for (int i = 0; i < n; i++)
        {
            int pos = data[i];
            byte val = list[pos];
            result[i] = val;
            // Mover al frente
            if (pos > 0)
            {
                for (int j = pos; j > 0; j--)
                    list[j] = list[j - 1];
                list[0] = val;
            }
        }
        return result;
    }

    /// <summary>XOR con byte anterior: patrones XOR se convierten en ceros.</summary>
    public static byte[] AplicarXORPrev(byte[] data)
    {
        int n = data.Length;
        byte[] result = new byte[n];
        result[0] = data[0];
        for (int i = 1; i < n; i++)
            result[i] = (byte)(data[i] ^ data[i - 1]);
        return result;
    }

    public static byte[] RevertirXORPrev(byte[] data)
    {
        int n = data.Length;
        byte[] result = new byte[n];
        result[0] = data[0];
        for (int i = 1; i < n; i++)
            result[i] = (byte)(data[i] ^ result[i - 1]);
        return result;
    }

    /// <summary>Ordenación por bloques: divide en bloques y ordena cada uno. Crea runs de valores similares.</summary>
    public static byte[] AplicarSortBlocks(byte[] data, int blockSize)
    {
        int n = data.Length;
        byte[] result = new byte[n];
        for (int start = 0; start < n; start += blockSize)
        {
            int len = Math.Min(blockSize, n - start);
            Array.Copy(data, start, result, start, len);
            Array.Sort(result, start, len);
        }
        return result;
    }

    /// <summary>Intercalado par/impar: [0,2,4,...,1,3,5,...]. Exposa patrones de periodo 2.</summary>
    public static byte[] AplicarInterleave(byte[] data)
    {
        int n = data.Length;
        byte[] result = new byte[n];
        int half = (n + 1) / 2;
        for (int i = 0; i < n; i++)
        {
            if (i % 2 == 0)
                result[i / 2] = data[i];
            else
                result[half + i / 2] = data[i];
        }
        return result;
    }

    public static byte[] RevertirInterleave(byte[] data)
    {
        int n = data.Length;
        byte[] result = new byte[n];
        int half = (n + 1) / 2;
        for (int i = 0; i < n; i++)
        {
            if (i % 2 == 0)
                result[i] = data[i / 2];
            else
                result[i] = data[half + i / 2];
        }
        return result;
    }

    /// <summary>Restar media móvil: cada byte menos el promedio de los W anteriores.</summary>
    public static byte[] AplicarSubMediaMovil(byte[] data, int window)
    {
        int n = data.Length;
        byte[] result = new byte[n];
        int sum = 0;
        for (int i = 0; i < n; i++)
        {
            int media = i > 0 ? sum / Math.Min(i, window) : data[0];
            result[i] = (byte)(data[i] - media);
            sum += data[i];
            if (i >= window) sum -= data[i - window];
        }
        return result;
    }

    public static byte[] RevertirSubMediaMovil(byte[] data, int window)
    {
        int n = data.Length;
        byte[] result = new byte[n];
        int sum = 0;
        for (int i = 0; i < n; i++)
        {
            int media = i > 0 ? sum / Math.Min(i, window) : 0;
            result[i] = (byte)(data[i] + media);
            sum += result[i];
            if (i >= window) sum -= result[i - window];
        }
        return result;
    }

    // ==================== BWT (Burrows-Wheeler Transform) ====================

    /// <summary>
    /// BWT: reordena los bytes para agrupar valores similares.
    /// La transformación "tipo CD-ROM": normaliza cualquier dato a una representación
    /// con runs de valores similares, reduciendo drásticamente la entropía local.
    /// 
    /// Formato de salida: [primaryIndex: 4 bytes LE][permuted data]
    /// </summary>
    public static byte[] AplicarBWT(byte[] data, out int primaryIndex)
    {
        int n = data.Length;
        if (n == 0) { primaryIndex = 0; return Array.Empty<byte>(); }
        if (n == 1) { primaryIndex = 0; return new byte[] { 0, 0, 0, 0, data[0] }; }

        // Construir array de índices ordenados por ROTACIÓN (no sufijos)
        int[] sa = ConstruirSuffixArrayRotaciones(data);

        // El primary index es la posición en el array ordenado donde empieza la rotación 0
        primaryIndex = -1;
        for (int i = 0; i < n; i++)
        {
            if (sa[i] == 0) { primaryIndex = i; break; }
        }

        // Construir salida: última columna de la matriz de rotaciones ordenadas
        // L[i] = data[(sa[i] - 1 + n) % n]
        byte[] output = new byte[4 + n];
        output[0] = (byte)(primaryIndex & 0xFF);
        output[1] = (byte)((primaryIndex >> 8) & 0xFF);
        output[2] = (byte)((primaryIndex >> 16) & 0xFF);
        output[3] = (byte)((primaryIndex >> 24) & 0xFF);
        for (int i = 0; i < n; i++)
            output[4 + i] = data[(sa[i] - 1 + n) % n];

        return output;
    }

    /// <summary>Invierte el BWT. Lee primaryIndex de los primeros 4 bytes.</summary>
    public static byte[] RevertirBWT(byte[] data)
    {
        if (data.Length < 5) return data; // formato inválido, devolver tal cual

        // Leer primary index
        int primaryIndex = data[0] | (data[1] << 8) | (data[2] << 16) | (data[3] << 24);
        int n = data.Length - 4;
        byte[] L = new byte[n]; // última columna
        Array.Copy(data, 4, L, 0, n);

        // Construir primera columna (F) ordenando L
        // Usar counting sort para eficiencia
        int[] count = new int[256];
        for (int i = 0; i < n; i++) count[L[i]]++;

        // Calcular posiciones de inicio en F
        int[] start = new int[256];
        start[0] = 0;
        for (int i = 1; i < 256; i++) start[i] = start[i - 1] + count[i - 1];

        // Construir mapping T: T[i] = posición en F del mismo byte que L[i]
        // LF-mapping: para cada posición i en L, T[i] es la posición correspondiente en F
        int[] T = new int[n];
        int[] pos = (int[])start.Clone();
        for (int i = 0; i < n; i++)
        {
            T[i] = pos[L[i]]++;
        }

        // Reconstruir datos originales siguiendo el LF-mapping desde primaryIndex
        byte[] result = new byte[n];
        int idx = primaryIndex;
        for (int i = n - 1; i >= 0; i--)
        {
            result[i] = L[idx];
            idx = T[idx];
        }

        return result;
    }

    /// <summary>
    /// Ordena las ROTACIONES cíclicas de data (no sufijos).
    /// Usa prefix doubling sobre rotaciones O(n log²n).
    /// </summary>
    static int[] ConstruirSuffixArrayRotaciones(byte[] data)
    {
        int n = data.Length;
        int[] sa = new int[n];
        int[] rank = new int[n];
        int[] tmp = new int[n];

        // Inicializar: rank[i] = data[i] (primer byte de la rotación i)
        for (int i = 0; i < n; i++)
        {
            sa[i] = i;
            rank[i] = data[i];
        }

        // Prefix doubling sobre ROTACIONES (wrapping con módulo)
        for (int k = 1; k < n; k *= 2)
        {
            Array.Sort(sa, (a, b) =>
            {
                if (rank[a] != rank[b]) return rank[a].CompareTo(rank[b]);
                // Para rotaciones: (a+k) % n en vez de a+k
                int ra = rank[(a + k) % n];
                int rb = rank[(b + k) % n];
                return ra.CompareTo(rb);
            });

            tmp[sa[0]] = 0;
            for (int i = 1; i < n; i++)
            {
                int prev = sa[i - 1], curr = sa[i];
                bool same = rank[prev] == rank[curr] &&
                    rank[(prev + k) % n] == rank[(curr + k) % n];
                tmp[curr] = tmp[prev] + (same ? 0 : 1);
            }
            Array.Copy(tmp, rank, n);

            if (rank[sa[n - 1]] == n - 1) break;
        }

        return sa;
    }

    // ==================== RLE (post-BWT+MTF) ====================

    /// <summary>
    /// RLE optimizado para datos post-MTF (mayormente ceros).
    /// Formato: byte > 0 → run de ceros; byte == 0 → escape, siguiente byte es literal.
    /// </summary>
    public static byte[] AplicarRLE(byte[] data)
    {
        using var ms = new MemoryStream();
        int n = data.Length;
        int i = 0;
        while (i < n)
        {
            if (data[i] == 0)
            {
                // Contar ceros consecutivos
                int runLen = 0;
                while (i < n && data[i] == 0 && runLen < 255) { runLen++; i++; }
                ms.WriteByte((byte)runLen); // 1-255 = run de ceros
            }
            else
            {
                ms.WriteByte(0); // escape: literal sigue
                ms.WriteByte(data[i]);
                i++;
            }
        }
        return ms.ToArray();
    }

    /// <summary>Invierte el RLE.</summary>
    public static byte[] RevertirRLE(byte[] data)
    {
        using var ms = new MemoryStream();
        int i = 0;
        while (i < data.Length)
        {
            if (data[i] > 0)
            {
                // Run de ceros
                int count = data[i];
                for (int j = 0; j < count; j++) ms.WriteByte(0);
                i++;
            }
            else
            {
                // Literal
                i++; // skip escape byte (0)
                if (i < data.Length) ms.WriteByte(data[i++]);
            }
        }
        return ms.ToArray();
    }

    // ==================== UTILIDADES ====================

    /// <summary>Calcula entropía Shannon de un array de bytes.</summary>
    public static double CalcularEntropia(byte[] data)
    {
        if (data.Length == 0) return 0;
        int[] hist = new int[256];
        for (int i = 0; i < data.Length; i++) hist[data[i]]++;
        double ent = 0;
        for (int i = 0; i < 256; i++)
        {
            if (hist[i] > 0)
            {
                double p = (double)hist[i] / data.Length;
                ent -= p * Math.Log2(p);
            }
        }
        return ent;
    }

    /// <summary>Cuenta ceros en un array (indicador de que MTF funcionó).</summary>
    public static double RatioCeros(byte[] data)
    {
        if (data.Length == 0) return 0;
        int ceros = 0;
        for (int i = 0; i < data.Length; i++) if (data[i] == 0) ceros++;
        return (double)ceros / data.Length;
    }
}
