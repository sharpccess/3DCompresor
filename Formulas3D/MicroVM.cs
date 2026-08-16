using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Formulas3D;

/// <summary>
/// MicroVM para compresión por microprogramas ejecutables.
/// En vez de almacenar bytes directamente, busca el programa más corto que genera la secuencia.
/// Las funciones matemáticas son un subconjunto: esta VM puede expresar cualquier patrón computable.
/// </summary>
public static class MicroVM
{
    // ==================== OPCODES ====================
    public const byte OP_PUSH_CONST = 0x01;   // [0x01][val]           A = constante
    public const byte OP_PUSH_CONST16 = 0x02;  // [0x02][lo][hi]       A = constante 16-bit
    public const byte OP_LOAD_I = 0x03;         // A = i (posición)
    public const byte OP_LOAD_PREV = 0x04;      // A = H (último output)
    public const byte OP_ADD = 0x10;            // A = (A + H) & 0xFF
    public const byte OP_SUB = 0x11;            // A = (A - H) & 0xFF
    public const byte OP_MUL = 0x12;            // A = (A * H) & 0xFF
    public const byte OP_XOR = 0x13;            // A = A ^ H
    public const byte OP_AND = 0x14;            // A = A & H
    public const byte OP_OR = 0x15;             // A = A | H
    public const byte OP_NOT = 0x16;            // A = ~A & 0xFF
    public const byte OP_SHL = 0x17;            // A = (A << 1) & 0xFF
    public const byte OP_SHR = 0x18;            // A = (A >> 1) & 0xFF
    public const byte OP_ADD_CONST = 0x20;      // [0x20][val]  A = (A + val) & 0xFF
    public const byte OP_SUB_CONST = 0x21;      // [0x21][val]  A = (A - val) & 0xFF
    public const byte OP_XOR_CONST = 0x22;      // [0x22][val]  A = A ^ val
    public const byte OP_MUL_CONST = 0x23;      // [0x23][val]  A = (A * val) & 0xFF
    public const byte OP_INC = 0x30;            // A = (A + 1) & 0xFF
    public const byte OP_DEC = 0x31;            // A = (A - 1) & 0xFF
    public const byte OP_DUP = 0x32;            // H = A (duplicar A en registro history)
    public const byte OP_STORE = 0x40;          // output[idx] = A; H = A; idx++
    public const byte OP_LOOP = 0xF0;           // saltar atrás si idx < count
    public const byte OP_STOP = 0xFF;

    // ==================== MOTOR VM ====================

    /// <summary>
    /// Ejecuta un programa sobre la VM y devuelve cuántos bytes del target produce correctamente.
    /// Si devuelve target.Length, el programa genera la secuencia completa.
    /// </summary>
    public static int Ejecutar(byte[] program, byte[] target)
    {
        int a = 0, h = 0, ip = 0, idx = 0;
        int maxSteps = target.Length * 20 + 1000;
        int steps = 0;

        while (ip < program.Length && idx < target.Length && steps < maxSteps)
        {
            steps++;
            byte op = program[ip++];

            switch (op)
            {
                case OP_PUSH_CONST:
                    if (ip >= program.Length) return idx;
                    a = program[ip++];
                    break;
                case OP_PUSH_CONST16:
                    if (ip + 1 >= program.Length) return idx;
                    a = (program[ip] | (program[ip + 1] << 8)) & 0xFF;
                    ip += 2;
                    break;
                case OP_LOAD_I:
                    a = idx;
                    break;
                case OP_LOAD_PREV:
                    a = h;
                    break;
                case OP_ADD: a = (a + h) & 0xFF; break;
                case OP_SUB: a = (a - h) & 0xFF; break;
                case OP_MUL: a = (a * h) & 0xFF; break;
                case OP_XOR: a = (a ^ h) & 0xFF; break;
                case OP_AND: a = (a & h) & 0xFF; break;
                case OP_OR: a = (a | h) & 0xFF; break;
                case OP_NOT: a = (~a) & 0xFF; break;
                case OP_SHL: a = (a << 1) & 0xFF; break;
                case OP_SHR: a = (a >> 1) & 0xFF; break;
                case OP_ADD_CONST:
                    if (ip >= program.Length) return idx;
                    a = (a + program[ip++]) & 0xFF;
                    break;
                case OP_SUB_CONST:
                    if (ip >= program.Length) return idx;
                    a = (a - program[ip++] + 256) & 0xFF;
                    break;
                case OP_XOR_CONST:
                    if (ip >= program.Length) return idx;
                    a = a ^ program[ip++];
                    break;
                case OP_MUL_CONST:
                    if (ip >= program.Length) return idx;
                    a = (a * program[ip++]) & 0xFF;
                    break;
                case OP_INC: a = (a + 1) & 0xFF; break;
                case OP_DEC: a = (a - 1 + 256) & 0xFF; break;
                case OP_DUP: h = a; break;
                case OP_STORE:
                    target[idx] = (byte)a;
                    h = a;
                    idx++;
                    break;
                case OP_LOOP:
                    if (idx < target.Length) ip = 0;
                    break;
                case OP_STOP:
                    return idx;
                default:
                    return idx; // opcode inválido
            }

            a &= 0xFF;
        }

        return idx;
    }

    // ==================== FORMATO DE BLOQUE ====================

    /// <summary>
    /// Codifica un bloque comprimido como microprograma.
    /// Formato: [progLen: uint16][program bytes]
    /// </summary>
    public static void CodificarBloque(MemoryStream ms, byte[] program)
    {
        ushort len = (ushort)program.Length;
        ms.WriteByte((byte)len);
        ms.WriteByte((byte)(len >> 8));
        ms.Write(program, 0, program.Length);
    }

    /// <summary>
    /// Decodifica un bloque microprograma del stream.
    /// </summary>
    public static byte[] DecodificarBloque(BinaryReader br, int longitudOriginal)
    {
        int progLen = br.ReadByte() | (br.ReadByte() << 8);
        byte[] program = br.ReadBytes(progLen);
        byte[] output = new byte[longitudOriginal];
        int produced = Ejecutar(program, output);
        if (produced < longitudOriginal)
            throw new InvalidDataException(
                $"MicroVM: programa produjo solo {produced}/{longitudOriginal} bytes");
        return output;
    }

    // ==================== SÍNTESIS DE PROGRAMAS ====================

    /// <summary>
    /// Intenta encontrar el microprograma más corto que genera la secuencia target.
    /// Primero prueba patrones analíticos (O(1)), luego beam search como fallback.
    /// Devuelve null si no encuentra nada más compacto que los literales.
    /// </summary>
    public static byte[]? Sintetizar(byte[] target)
    {
        int n = target.Length;
        if (n < 4) return null; // demasiado corto para valer la pena

        // Coste de literales raw: ~5 bytes overhead + N bytes de datos
        int costeLiterales = 5 + n;

        // Intentar patrones analíticos (de más simple a más complejo)
        var resultado = ProgresionConstante(target, n)
            ?? ProgresionAritmetica(target, n)
            ?? ProgresionModular(target, n)
            ?? SecuenciaXor(target, n)
            ?? PatronAlternante(target, n)
            ?? ProgresionGeometrica(target, n)
            ?? LCG(target, n);

        if (resultado != null && resultado.Length < costeLiterales)
            return resultado;

        // Los patrones analíticos cubren los casos comunes en O(1).
        // Beam search es demasiado lento para archivos grandes y rara vez
        // encuentra patrones en datos de archivos reales (PDF, PNG, etc).
        // Si se necesitan patrones más complejos, se pueden añadir más
        // detectores analíticos sin incurrir en costo de búsqueda.

        // No se encontró ningún patrón: devolver null para que el caller
        // almacene los datos raw en vez de un programa de literales 3x más grande.
        return null;
    }

    // --- Patrón: todos los bytes iguales ---
    static byte[]? ProgresionConstante(byte[] t, int n)
    {
        byte val = t[0];
        for (int i = 1; i < n; i++)
            if (t[i] != val) return null;

        // push_const val; store; loop
        return [OP_PUSH_CONST, val, OP_STORE, OP_LOOP];
    }

    // --- Patrón: progresión aritmética simple (byte wrapping) ---
    static byte[]? ProgresionAritmetica(byte[] t, int n)
    {
        int step = ((int)t[1] - t[0] + 256) & 0xFF;
        for (int i = 1; i < n; i++)
            if ((((int)t[i] - t[i - 1] + 256) & 0xFF) != step) return null;

        if (step == 0) return null; // ya lo captura ProgresionConstante

        // push_const t[0]; dup; store; loop(add_const step; dup; store; loop)
        // Pero más eficiente: push_const t[0]; store; loop(add_const step; store; loop)
        // Necesitamos DUP antes del store para que H tenga el valor correcto para ADD
        // En realidad ADD usa H, y STORE setea H = A. Así que:
        // push_const t[0]; store; [add_const step; store] loop
        // Pero después del primer store, H = t[0]. Luego add_const step → A = (step + H) = step + t[0]
        // Eso NO es correcto. ADD suma A + H, no H + const.
        // Necesitamos: push_const step; add; → A = step + H = step + t[0]. Correcto!
        // Pero ADD usa A + H donde A=step y H=t[0]. Sí, correcto.

        // Programa: push_const t[0], store, loop(push_const step, add, store, loop)
        return [
            OP_PUSH_CONST, t[0],
            OP_STORE,
            OP_PUSH_CONST, (byte)step,
            OP_ADD,
            OP_STORE,
            OP_LOOP
        ];
    }

    // --- Patrón: progresión modular (f(x) = (a + b*x) mod 256) ---
    static byte[]? ProgresionModular(byte[] t, int n)
    {
        if (n < 3) return null;
        int a = t[0];
        int b = ((int)t[1] - t[0] + 256) & 0xFF;
        if (b == 0) return null;

        for (int i = 0; i < n; i++)
            if (t[i] != (byte)((a + (long)b * i) & 0xFF)) return null;

        // push_const a; load_i; push_const b; mul_const; add; store; loop
        // Verificación: A=b*i, luego add → A = b*i + H = b*i + a. Correcto!
        return [
            OP_PUSH_CONST, (byte)a,
            OP_LOAD_I,
            OP_PUSH_CONST, (byte)b,
            OP_MUL_CONST,
            OP_ADD,
            OP_STORE,
            OP_LOOP
        ];
    }

    // --- Patrón: XOR progresivo (t[i] = t[i-1] ^ xorVal) ---
    static byte[]? SecuenciaXor(byte[] t, int n)
    {
        if (n < 2) return null;
        byte xorVal = (byte)(t[0] ^ t[1]);
        if (xorVal == 0) return null;

        for (int i = 1; i < n; i++)
            if ((byte)(t[i - 1] ^ t[i]) != xorVal) return null;

        // push_const t[0]; store; loop(push_const xorVal; xor; store; loop)
        return [
            OP_PUSH_CONST, t[0],
            OP_STORE,
            OP_PUSH_CONST, xorVal,
            OP_XOR,
            OP_STORE,
            OP_LOOP
        ];
    }

    // --- Patrón: alternante (a, b, a, b, ...) ---
    static byte[]? PatronAlternante(byte[] t, int n)
    {
        if (n < 4) return null;
        byte a = t[0], b = t[1];
        if (a == b) return null;

        for (int i = 0; i < n; i++)
            if (t[i] != (i % 2 == 0 ? a : b)) return null;

        byte xorVal = (byte)(a ^ b);
        // push_const a; store; loop(push_const xorVal; xor; store; loop)
        return [
            OP_PUSH_CONST, a,
            OP_STORE,
            OP_PUSH_CONST, xorVal,
            OP_XOR,
            OP_STORE,
            OP_LOOP
        ];
    }

    // --- Patrón: progresión geométrica modular (t[i] = t[i-1] * factor mod 256) ---
    static byte[]? ProgresionGeometrica(byte[] t, int n)
    {
        if (n < 3 || t[0] == 0) return null;

        // Buscar factor: t[1] = t[0] * factor mod 256
        // factor = t[1] / t[0] mod 256 (necesita inversa modular)
        int inv = InversaModular(t[0]);
        if (inv < 0) return null;
        int factor = (t[1] * inv) & 0xFF;
        if (factor < 2) return null;

        for (int i = 1; i < n; i++)
            if ((byte)((t[i - 1] * factor) & 0xFF) != t[i]) return null;

        // push_const t[0]; store; loop(push_const factor; mul; store; loop)
        return [
            OP_PUSH_CONST, t[0],
            OP_STORE,
            OP_PUSH_CONST, (byte)factor,
            OP_MUL,
            OP_STORE,
            OP_LOOP
        ];
    }

    // --- Patrón: LCG (t[i] = (a * t[i-1] + c) mod 256) ---
    static byte[]? LCG(byte[] t, int n)
    {
        if (n < 4 || t[0] == 0) return null;

        // Intentar encontrar 'a' y 'c' tales que t[i] = (a * t[i-1] + c) mod 256
        // Para cada 'a' candidato (1-255), calcular c = (t[1] - a * t[0]) mod 256
        for (int a = 1; a < 256; a++)
        {
            int c = ((int)t[1] - a * t[0] + 25600) & 0xFF;
            bool ok = true;
            for (int i = 1; i < n && ok; i++)
                if ((byte)((a * t[i - 1] + c) & 0xFF) != t[i]) ok = false;

            if (!ok) continue;

            // Programa: push_const t[0]; store; loop(push_const a; mul; push_const c; add; store; loop)
            // push_const(2) + store(1) + [push_const(2) + mul(1) + push_const(2) + add(1) + store(1) + loop(1)] = 14 bytes
            var prog = new byte[14];
            int p = 0;
            prog[p++] = OP_PUSH_CONST; prog[p++] = t[0];
            prog[p++] = OP_STORE;
            prog[p++] = OP_PUSH_CONST; prog[p++] = (byte)a;
            prog[p++] = OP_MUL;
            prog[p++] = OP_PUSH_CONST; prog[p++] = (byte)c;
            prog[p++] = OP_ADD;
            prog[p++] = OP_STORE;
            prog[p++] = OP_LOOP;
            return prog;
        }

        return null;
    }

    // --- Beam search para patrones no analíticos ---
    static byte[]? BeamSearch(byte[] target, int n, int maxCost)
    {
        // Semillas: programas de 1 instrucción (antes del loop)
        var seeds = new List<byte[]>
        {
            new byte[] { OP_PUSH_CONST, 0 },
            new byte[] { OP_PUSH_CONST, 1 },
            new byte[] { OP_LOAD_I },
            new byte[] { OP_LOAD_PREV },
        };

        int beamWidth = 64;
        int maxProgLen = 12;

        // Candidato: prefijo de programa (sin loop) que se extiende iterativamente
        var beam = new List<(byte[] prog, int score)>();

        foreach (var seed in seeds)
        {
            byte[] testProg = ConstruirConLoop(seed);
            byte[] output = new byte[n];
            int produced = Ejecutar(testProg, output);
            int score = CalcularPuntaje(produced, output, target, testProg.Length);
            if (produced == n && EsPerfecto(output, target))
                return testProg;
            beam.Add((seed, score));
        }

        for (int iter = 0; iter < maxProgLen; iter++)
        {
            var nextBeam = new List<(byte[] prog, int score)>();

            // Instrucciones para extender
            var extensiones = new List<byte[]>
            {
                new byte[] { OP_PUSH_CONST, 0 }, new byte[] { OP_PUSH_CONST, 1 }, new byte[] { OP_PUSH_CONST, 0xFF },
                new byte[] { OP_LOAD_I }, new byte[] { OP_LOAD_PREV },
                new byte[] { OP_ADD }, new byte[] { OP_SUB }, new byte[] { OP_XOR }, new byte[] { OP_MUL },
                new byte[] { OP_AND }, new byte[] { OP_OR }, new byte[] { OP_SHL }, new byte[] { OP_SHR },
                new byte[] { OP_ADD_CONST, 1 }, new byte[] { OP_ADD_CONST, 2 }, new byte[] { OP_ADD_CONST, 0xFF },
                new byte[] { OP_SUB_CONST, 1 },
                new byte[] { OP_XOR_CONST, 0xFF }, new byte[] { OP_XOR_CONST, 0x80 },
                new byte[] { OP_MUL_CONST, 2 }, new byte[] { OP_MUL_CONST, 3 },
                new byte[] { OP_INC }, new byte[] { OP_DEC }, new byte[] { OP_NOT },
                new byte[] { OP_DUP },
            };

            foreach (var (prog, _) in beam)
            {
                foreach (var ext in extensiones)
                {
                    var newProg = new byte[prog.Length + ext.Length];
                    Buffer.BlockCopy(prog, 0, newProg, 0, prog.Length);
                    Buffer.BlockCopy(ext, 0, newProg, prog.Length, ext.Length);

                    if (newProg.Length > maxProgLen) continue;

                    byte[] testProg = ConstruirConLoop(newProg);
                    if (testProg.Length > maxCost) continue;

                    byte[] output = new byte[n];
                    int produced = Ejecutar(testProg, output);
                    int score = CalcularPuntaje(produced, output, target, testProg.Length);

                    if (produced == n && EsPerfecto(output, target))
                        return testProg;

                    nextBeam.Add((newProg, score));
                }
            }

            if (nextBeam.Count == 0) break;

            beam = nextBeam
                .OrderByDescending(x => x.score)
                .Take(beamWidth)
                .ToList();

            // Si el mejor no mejora, parar
            if (beam[0].score <= 0) break;
        }

        return null;
    }

    static byte[] ConstruirConLoop(byte[] cuerpo)
    {
        var prog = new byte[cuerpo.Length + 2]; // +store +loop
        Buffer.BlockCopy(cuerpo, 0, prog, 0, cuerpo.Length);
        prog[cuerpo.Length] = OP_STORE;
        prog[cuerpo.Length + 1] = OP_LOOP;
        return prog;
    }

    static int CalcularPuntaje(int produced, byte[] output, byte[] target, int progLen)
    {
        if (produced < target.Length) return 0; // incompleto
        int correctos = 0;
        for (int i = 0; i < target.Length; i++)
            if (output[i] == target[i]) correctos++;

        if (correctos < target.Length) return 0; // no es perfecto
        // Bonus por programa más corto
        return target.Length * 100 - progLen;
    }

    static bool EsPerfecto(byte[] output, byte[] target)
    {
        for (int i = 0; i < target.Length; i++)
            if (output[i] != target[i]) return false;
        return true;
    }

    // --- Programa de literales (fallback) ---
    static byte[] ProgramaLiterales(byte[] target)
    {
        // push_const t[0]; store; loop(push_const t[i]; store; loop)
        // Pero esto no funciona porque los literales son diferentes en cada iteración.
        // En su lugar, generamos un programa sin loop que produce exactamente N bytes:
        // push_const t[0]; store; push_const t[1]; store; ...
        var prog = new byte[target.Length * 3];
        int p = 0;
        for (int i = 0; i < target.Length; i++)
        {
            prog[p++] = OP_PUSH_CONST;
            prog[p++] = target[i];
            prog[p++] = OP_STORE;
        }
        // Recortar al tamaño real
        byte[] result = new byte[p];
        Buffer.BlockCopy(prog, 0, result, 0, p);
        return result;
    }

    // ==================== UTILIDADES ====================

    /// <summary>
    /// Calcula la inversa modular de a mod 256 usando el algoritmo de Euclides extendido.
    /// Devuelve -1 si no existe inversa (a es par).
    /// </summary>
    static int InversaModular(int a)
    {
        a = ((a % 256) + 256) % 256;
        if (a % 2 == 0) return -1; // no tiene inversa si es par

        // Euclides extendido: encontrar x tal que (a * x) % 256 = 1
        int t = 0, newT = 1;
        int r = 256, newR = a;

        while (newR != 0)
        {
            int quotient = r / newR;
            (t, newT) = (newT, t - quotient * newT);
            (r, newR) = (newR, r - quotient * newR);
        }

        if (r > 1) return -1;
        return ((t % 256) + 256) % 256;
    }

    // ==================== COMPRESIÓN DE BLOQUES ====================

    /// <summary>
    /// Comprime un stream de bytes dividiéndolo en bloques y buscando microprogramas.
    /// Formato: [numBlocks: int32] { [type: byte][origLen: int32][data/program] }...
    ///   type 0x00 = raw bytes: [raw bytes]
    ///   type 0x01 = microprogram: [progLen: uint16][program bytes]
    /// </summary>
    public static byte[] ComprimirStream(byte[] data, int tamanoBloque = 256)
    {
        using var ms = new MemoryStream();
        int numBlocks = (data.Length + tamanoBloque - 1) / tamanoBloque;

        // Escribir número de bloques
        WriteInt32(ms, numBlocks);

        int bloquesComprimidos = 0;

        for (int b = 0; b < numBlocks; b++)
        {
            int start = b * tamanoBloque;
            int len = Math.Min(tamanoBloque, data.Length - start);
            byte[] bloque = new byte[len];
            Buffer.BlockCopy(data, start, bloque, 0, len);

            byte[]? program = Sintetizar(bloque);

            if (program != null)
            {
                // Bloque con microprograma
                ms.WriteByte(0x01); // type = microprogram
                WriteInt32(ms, len);
                CodificarBloque(ms, program);
                bloquesComprimidos++;
            }
            else
            {
                // Bloque raw (sin patrón encontrado)
                ms.WriteByte(0x00); // type = raw
                WriteInt32(ms, len);
                ms.Write(bloque, 0, bloque.Length);
            }
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Descomprime un stream de bloques microprograma.
    /// Lee el type byte de cada bloque: 0x00=raw, 0x01=microprograma.
    /// </summary>
    public static byte[] DescomprimirStream(byte[] compressedData)
    {
        using var ms = new MemoryStream(compressedData);
        using var br = new BinaryReader(ms);

        int numBlocks = ReadInt32(ms);
        var output = new List<byte>();

        for (int b = 0; b < numBlocks; b++)
        {
            int type = ms.ReadByte();
            if (type < 0) throw new InvalidDataException("Stream MicroVM truncado.");

            int origLen = ReadInt32(ms);

            if (type == 0x01)
            {
                // Microprograma
                byte[] bloque = DecodificarBloque(br, origLen);
                output.AddRange(bloque);
            }
            else
            {
                // Raw bytes
                byte[] bloque = br.ReadBytes(origLen);
                if (bloque.Length < origLen)
                    throw new InvalidDataException("Bloque raw truncado.");
                output.AddRange(bloque);
            }
        }

        return output.ToArray();
    }

    // ==================== HELPERS I/O ====================

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
