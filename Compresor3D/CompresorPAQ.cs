using System;
using System.IO;

namespace Compresor3D;

/// <summary>
/// Compresor Context Mixing basado en PAQ1 (Matt Mahoney, 2002).
/// 
/// PAQ1 es el compresor más simple de la familia PAQ (campeona mundial).
/// Funciona a nivel de BIT (no byte), con múltiples modelos de contexto
/// que se mezclan con pesos adaptativos.
/// 
/// Componentes:
/// - Arithmetic Coder: codifica bits según probabilidad predicha
/// - Modelos de contexto: orden 0 a N (historial de bits anteriores)
/// - Blending: mezcla ponderada de predicciones (peso = orden²)
/// - Adaptación no-estacionaria: conteos limitados a 255, olvido agresivo
/// 
/// Referencia: https://shitpoet.cc/simple-implementation-of-paq1.html
/// </summary>
public static class CompresorPAQ
{
    private const int NUM_MODELS = 6; // Orden 0 a 5
    private const int MAX_COUNT = 255;
    
    // ==================== ARITHMETIC CODER ====================
    
    /// <summary>Encoder aritmético a nivel de bit con manejo de pending bits (E3).</summary>
    private class ArithmeticEncoder
    {
        private Stream stream;
        private uint low, high;
        private int pendingCount;
        private byte outputBuffer;
        private int bitsInBuffer;
        
        private const uint FULL = 0xFFFFFFFF;
        private const uint HALF = 0x80000000;
        private const uint QUARTER = 0x40000000;
        
        public ArithmeticEncoder(Stream output)
        {
            stream = output;
            low = 0;
            high = FULL;
            pendingCount = 0;
            outputBuffer = 0;
            bitsInBuffer = 0;
        }
        
        /// <summary>Codifica un bit con probabilidad p(1) = p1 / (p0 + p1).</summary>
        public void EncodeBit(uint p0, uint p1, int bit)
        {
            uint range = high - low;
            uint split = (uint)((ulong)range * p0 / (p0 + p1));
            uint mid = low + split;
            
            if (bit == 0)
            {
                high = mid;
            }
            else
            {
                low = mid + 1;
            }
            
            // Renormalization
            while (true)
            {
                if (high < HALF)
                {
                    // Both in [0, 0.5)
                    OutputBit(0);
                    OutputPendingBits(1);
                    low <<= 1;
                    high = (high << 1) | 1;
                }
                else if (low >= HALF)
                {
                    // Both in [0.5, 1)
                    OutputBit(1);
                    OutputPendingBits(0);
                    low -= HALF;
                    high -= HALF;
                    low <<= 1;
                    high = (high << 1) | 1;
                }
                else if (low >= QUARTER && high < 3 * QUARTER)
                {
                    // E3 region: [0.25, 0.75)
                    pendingCount++;
                    low -= QUARTER;
                    high -= QUARTER;
                    low <<= 1;
                    high = (high << 1) | 1;
                }
                else
                {
                    break;
                }
            }
        }
        
        /// <summary>Flush final bits.</summary>
        public void Flush()
        {
            // Output enough bits to disambiguate
            pendingCount++;
            if (low < QUARTER)
            {
                OutputBit(0);
                OutputPendingBits(1);
            }
            else
            {
                OutputBit(1);
                OutputPendingBits(0);
            }
            
            // Flush remaining bits in buffer
            if (bitsInBuffer > 0)
            {
                outputBuffer <<= (8 - bitsInBuffer);
                stream.WriteByte(outputBuffer);
            }
        }
        
        private void OutputBit(int bit)
        {
            outputBuffer = (byte)((outputBuffer << 1) | bit);
            bitsInBuffer++;
            
            if (bitsInBuffer == 8)
            {
                stream.WriteByte(outputBuffer);
                outputBuffer = 0;
                bitsInBuffer = 0;
            }
        }
        
        private void OutputPendingBits(int bit)
        {
            for (int i = 0; i < pendingCount; i++)
            {
                OutputBit(bit);
            }
            pendingCount = 0;
        }
    }
    
    /// <summary>Decoder aritmético a nivel de bit con manejo de pending bits (E3).</summary>
    private class ArithmeticDecoder
    {
        private Stream stream;
        private uint low, high, code;
        private byte inputBuffer;
        private int bitsInBuffer;
        
        private const uint FULL = 0xFFFFFFFF;
        private const uint HALF = 0x80000000;
        private const uint QUARTER = 0x40000000;
        
        public ArithmeticDecoder(Stream input)
        {
            stream = input;
            low = 0;
            high = FULL;
            code = 0;
            bitsInBuffer = 0;
            
            // Read initial 32 bits
            for (int i = 0; i < 32; i++)
            {
                code = (code << 1) | (uint)ReadBit();
            }
        }
        
        /// <summary>Decodifica un bit con probabilidad p(1) = p1 / (p0 + p1).</summary>
        public int DecodeBit(uint p0, uint p1)
        {
            uint range = high - low;
            uint split = (uint)((ulong)range * p0 / (p0 + p1));
            uint mid = low + split;
            
            int bit;
            if (code <= mid)
            {
                bit = 0;
                high = mid;
            }
            else
            {
                bit = 1;
                low = mid + 1;
            }
            
            // Renormalization
            while (true)
            {
                if (high < HALF)
                {
                    low <<= 1;
                    high = (high << 1) | 1;
                    code = (code << 1) | (uint)ReadBit();
                }
                else if (low >= HALF)
                {
                    low -= HALF;
                    high -= HALF;
                    low <<= 1;
                    high = (high << 1) | 1;
                    code -= HALF;
                    code = (code << 1) | (uint)ReadBit();
                }
                else if (low >= QUARTER && high < 3 * QUARTER)
                {
                    low -= QUARTER;
                    high -= QUARTER;
                    low <<= 1;
                    high = (high << 1) | 1;
                    code -= QUARTER;
                    code = (code << 1) | (uint)ReadBit();
                }
                else
                {
                    break;
                }
            }
            
            return bit;
        }
        
        private int ReadBit()
        {
            if (bitsInBuffer == 0)
            {
                int b = stream.ReadByte();
                if (b == -1)
                    return 0;
                inputBuffer = (byte)b;
                bitsInBuffer = 8;
            }
            
            bitsInBuffer--;
            return (inputBuffer >> bitsInBuffer) & 1;
        }
    }
    
    // ==================== MODELO DE CONTEXTO ====================
    
    /// <summary>
    /// Modelo de contexto con adaptación no-estacionaria.
    /// Mantiene conteos de 0s y 1s para cada contexto.
    /// Los conteos se limitan a 255 y se "olvidan" agresivamente.
    /// </summary>
    private class ContextModel
    {
        private int contextBits;
        private int tableSize;
        private byte[,] counts; // [context, bit] -> count
        
        public ContextModel(int contextBits)
        {
            this.contextBits = contextBits;
            tableSize = 1 << contextBits;
            counts = new byte[tableSize, 2];
            
            // Inicializar con conteo de 1 (Laplace smoothing)
            for (int i = 0; i < tableSize; i++)
            {
                counts[i, 0] = 1;
                counts[i, 1] = 1;
            }
        }
        
        /// <summary>Predice probabilidad del siguiente bit dado el contexto.</summary>
        public (uint p0, uint p1) Predict(int context)
        {
            int ctx = context & (tableSize - 1);
            return ((uint)counts[ctx, 0], (uint)counts[ctx, 1]);
        }
        
        /// <summary>Actualiza el modelo con el bit observado (adaptación no-estacionaria).</summary>
        public void Update(int context, int bit)
        {
            int ctx = context & (tableSize - 1);
            
            // Incrementar conteo del bit observado
            if (counts[ctx, bit] < MAX_COUNT)
            {
                counts[ctx, bit]++;
            }
            
            // Olvido agresivo: dividir conteo del bit opuesto
            int otherBit = 1 - bit;
            if (counts[ctx, otherBit] > 0)
            {
                counts[ctx, otherBit] = (byte)(counts[ctx, otherBit] / 2 + 1);
            }
        }
    }
    
    /// <summary>
    /// Modelo de contexto 2D espacial para imágenes.
    /// Usa los píxeles vecinos (izquierda, arriba, diagonal) como contexto.
    /// </summary>
    private class SpatialModel2D
    {
        private int width;
        private int contextBits;
        private int tableSize;
        private byte[,] counts;
        
        public SpatialModel2D(int width, int contextBits = 12)
        {
            this.width = width;
            this.contextBits = contextBits;
            tableSize = 1 << contextBits;
            counts = new byte[tableSize, 2];
            
            for (int i = 0; i < tableSize; i++)
            {
                counts[i, 0] = 1;
                counts[i, 1] = 1;
            }
        }
        
        /// <summary>Calcula el contexto 2D basado en vecinos (izquierda, arriba, diagonal).</summary>
        public int GetContext(byte[] data, int index, int bitPosition)
        {
            int x = index % width;
            int y = index / width;
            
            int context = 0;
            
            // Vecino izquierdo (mismo bit)
            if (x > 0)
            {
                int leftByte = data[index - 1];
                int leftBit = (leftByte >> bitPosition) & 1;
                context = (context << 1) | leftBit;
            }
            else
            {
                context <<= 1;
            }
            
            // Vecino arriba (mismo bit)
            if (y > 0)
            {
                int upByte = data[index - width];
                int upBit = (upByte >> bitPosition) & 1;
                context = (context << 1) | upBit;
            }
            else
            {
                context <<= 1;
            }
            
            // Vecino diagonal (arriba-izquierda)
            if (x > 0 && y > 0)
            {
                int diagByte = data[index - width - 1];
                int diagBit = (diagByte >> bitPosition) & 1;
                context = (context << 1) | diagBit;
            }
            else
            {
                context <<= 1;
            }
            
            // Bits superiores del pixel actual (contexto local)
            int currentByte = data[index];
            for (int b = 7; b > bitPosition; b--)
            {
                context = (context << 1) | ((currentByte >> b) & 1);
            }
            
            return context & (tableSize - 1);
        }
        
        public (uint p0, uint p1) Predict(int context)
        {
            int ctx = context & (tableSize - 1);
            return ((uint)counts[ctx, 0], (uint)counts[ctx, 1]);
        }
        
        public void Update(int context, int bit)
        {
            int ctx = context & (tableSize - 1);
            
            if (counts[ctx, bit] < MAX_COUNT)
            {
                counts[ctx, bit]++;
            }
            
            int otherBit = 1 - bit;
            if (counts[ctx, otherBit] > 0)
            {
                counts[ctx, otherBit] = (byte)(counts[ctx, otherBit] / 2 + 1);
            }
        }
    }
    
    // ==================== COMPRESIÓN ====================
    
    /// <summary>Comprime datos usando Context Mixing (PAQ1-style).</summary>
    public static byte[] Comprimir(byte[] data)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        
        // Header: marker + tamaño original
        bw.Write((byte)0x70); // Marker PAQ
        bw.Write(data.Length);
        
        // Inicializar modelos (orden 0 a 5)
        var models = new ContextModel[NUM_MODELS];
        for (int i = 0; i < NUM_MODELS; i++)
        {
            models[i] = new ContextModel(i * 4); // 0, 4, 8, 12, 16, 20 bits de contexto
        }
        
        var encoder = new ArithmeticEncoder(ms);
        
        int context = 0; // Historial de bits
        
        // Comprimir cada byte, bit por bit (MSB primero)
        for (int i = 0; i < data.Length; i++)
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                int b = (data[i] >> bit) & 1;
                
                // Mezclar predicciones de todos los modelos
                ulong totalP0 = 0, totalP1 = 0;
                for (int m = 0; m < NUM_MODELS; m++)
                {
                    ulong weight = (ulong)(m + 1) * (ulong)(m + 1); // Peso = orden²
                    var (p0, p1) = models[m].Predict(context);
                    totalP0 += weight * (ulong)p0;
                    totalP1 += weight * (ulong)p1;
                }
                
                // Evitar overflow
                if (totalP0 > uint.MaxValue) totalP0 = uint.MaxValue;
                if (totalP1 > uint.MaxValue) totalP1 = uint.MaxValue;
                
                // Codificar bit
                encoder.EncodeBit((uint)totalP0, (uint)totalP1, b);
                
                // Actualizar modelos
                for (int m = 0; m < NUM_MODELS; m++)
                {
                    models[m].Update(context, b);
                }
                
                // Actualizar contexto (shift left, agregar bit)
                context = ((context << 1) | b) & 0xFFFFF; // Máximo 20 bits
            }
        }
        
        encoder.Flush();
        return ms.ToArray();
    }
    
    /// <summary>Descomprime datos PAQ1.</summary>
    public static byte[] Descomprimir(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);
        
        byte marker = br.ReadByte();
        if (marker != 0x70)
            throw new InvalidDataException("No es formato PAQ");
        
        int originalSize = br.ReadInt32();
        byte[] result = new byte[originalSize];
        
        // Inicializar modelos
        var models = new ContextModel[NUM_MODELS];
        for (int i = 0; i < NUM_MODELS; i++)
        {
            models[i] = new ContextModel(i * 4);
        }
        
        var decoder = new ArithmeticDecoder(ms);
        
        int context = 0;
        
        // Descomprimir byte por byte
        for (int i = 0; i < originalSize; i++)
        {
            int byteVal = 0;
            
            for (int bit = 7; bit >= 0; bit--)
            {
                // Mezclar predicciones
                ulong totalP0 = 0, totalP1 = 0;
                for (int m = 0; m < NUM_MODELS; m++)
                {
                    ulong weight = (ulong)(m + 1) * (ulong)(m + 1);
                    var (p0, p1) = models[m].Predict(context);
                    totalP0 += weight * (ulong)p0;
                    totalP1 += weight * (ulong)p1;
                }
                
                if (totalP0 > uint.MaxValue) totalP0 = uint.MaxValue;
                if (totalP1 > uint.MaxValue) totalP1 = uint.MaxValue;
                
                // Decodificar bit
                int b = decoder.DecodeBit((uint)totalP0, (uint)totalP1);
                byteVal = (byteVal << 1) | b;
                
                // Actualizar modelos
                for (int m = 0; m < NUM_MODELS; m++)
                {
                    models[m].Update(context, b);
                }
                
                // Actualizar contexto
                context = ((context << 1) | b) & 0xFFFFF;
            }
            
            result[i] = (byte)byteVal;
        }
        
        return result;
    }
    
    // ==================== COMPRESIÓN CON MODELO 2D ====================
    
    /// <summary>Comprime datos usando Context Mixing + Modelo 2D espacial (para imágenes).</summary>
    public static byte[] ComprimirCon2D(byte[] data, int width)
    {
        using var ms = new MemoryStream();
        using var bw = new BinaryWriter(ms);
        
        // Header: marker + tamaño original + ancho
        bw.Write((byte)0x71); // Marker PAQ2D
        bw.Write(data.Length);
        bw.Write(width);
        
        // Inicializar modelos 1D (orden 0 a 3)
        var models = new ContextModel[4];
        for (int i = 0; i < 4; i++)
        {
            models[i] = new ContextModel(i * 4);
        }
        
        // Inicializar modelo 2D
        var model2D = new SpatialModel2D(width, 12);
        
        var encoder = new ArithmeticEncoder(ms);
        
        int context = 0;
        
        for (int i = 0; i < data.Length; i++)
        {
            for (int bit = 7; bit >= 0; bit--)
            {
                int b = (data[i] >> bit) & 1;
                
                // Mezclar predicciones de modelos 1D
                ulong totalP0 = 0, totalP1 = 0;
                for (int m = 0; m < 4; m++)
                {
                    ulong weight = (ulong)(m + 1) * (ulong)(m + 1);
                    var (p0, p1) = models[m].Predict(context);
                    totalP0 += weight * (ulong)p0;
                    totalP1 += weight * (ulong)p1;
                }
                
                // Añadir predicción del modelo 2D (peso alto)
                int ctx2D = model2D.GetContext(data, i, bit);
                var (p02d, p12d) = model2D.Predict(ctx2D);
                totalP0 += 16UL * (ulong)p02d; // Peso alto para 2D
                totalP1 += 16UL * (ulong)p12d;
                
                if (totalP0 > uint.MaxValue) totalP0 = uint.MaxValue;
                if (totalP1 > uint.MaxValue) totalP1 = uint.MaxValue;
                
                encoder.EncodeBit((uint)totalP0, (uint)totalP1, b);
                
                // Actualizar modelos
                for (int m = 0; m < 4; m++)
                {
                    models[m].Update(context, b);
                }
                model2D.Update(ctx2D, b);
                
                context = ((context << 1) | b) & 0xFFFFF;
            }
        }
        
        encoder.Flush();
        return ms.ToArray();
    }
    
    /// <summary>Descomprime datos comprimidos con ComprimirCon2D.</summary>
    public static byte[] DescomprimirCon2D(byte[] data)
    {
        using var ms = new MemoryStream(data);
        using var br = new BinaryReader(ms);
        
        byte marker = br.ReadByte();
        if (marker != 0x71)
            throw new Exception("Marker inválido para PAQ2D");
        
        int originalSize = br.ReadInt32();
        int width = br.ReadInt32();
        
        byte[] compressed = br.ReadBytes(data.Length - 9);
        
        using var compressedStream = new MemoryStream(compressed);
        var decoder = new ArithmeticDecoder(compressedStream);
        
        var models = new ContextModel[4];
        for (int i = 0; i < 4; i++)
        {
            models[i] = new ContextModel(i * 4);
        }
        
        var model2D = new SpatialModel2D(width, 12);
        
        byte[] result = new byte[originalSize];
        int context = 0;
        
        for (int i = 0; i < originalSize; i++)
        {
            int byteVal = 0;
            
            for (int bit = 7; bit >= 0; bit--)
            {
                ulong totalP0 = 0, totalP1 = 0;
                for (int m = 0; m < 4; m++)
                {
                    ulong weight = (ulong)(m + 1) * (ulong)(m + 1);
                    var (p0, p1) = models[m].Predict(context);
                    totalP0 += weight * (ulong)p0;
                    totalP1 += weight * (ulong)p1;
                }
                
                int ctx2D = model2D.GetContext(result, i, bit);
                var (p02d, p12d) = model2D.Predict(ctx2D);
                totalP0 += 16UL * (ulong)p02d;
                totalP1 += 16UL * (ulong)p12d;
                
                if (totalP0 > uint.MaxValue) totalP0 = uint.MaxValue;
                if (totalP1 > uint.MaxValue) totalP1 = uint.MaxValue;
                
                int b = decoder.DecodeBit((uint)totalP0, (uint)totalP1);
                byteVal = (byteVal << 1) | b;
                
                for (int m = 0; m < 4; m++)
                {
                    models[m].Update(context, b);
                }
                model2D.Update(ctx2D, b);
                
                context = ((context << 1) | b) & 0xFFFFF;
            }
            
            result[i] = (byte)byteVal;
        }
        
        return result;
    }
}
