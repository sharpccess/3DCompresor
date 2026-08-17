using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace Compresor3D;

/// <summary>
/// Contenedor de archivos .cubo - Soporta múltiples archivos empaquetados.
/// Formato estándar similar a ZIP/TAR.
/// </summary>
public class CuboContainer
{
    // Magic bytes para identificar el formato
    private static readonly byte[] MAGIC = { 0x43, 0x55, 0x42, 0x4F }; // "CUBO"
    private const byte VERSION = 2;
    
    // Métodos de compresión
    public const byte METHOD_NONE = 0;      // Sin compresión
    public const byte METHOD_BWT_ZSTD = 1;  // BWT + MTF + Zstd
    public const byte METHOD_PAQ1 = 2;      // PAQ1 Context Mixing
    public const byte METHOD_PAQ1_2D = 3;   // PAQ1 con modelo 2D
    
    /// <summary>Información de un archivo dentro del contenedor.</summary>
    public class FileEntry
    {
        public string Name { get; set; } = "";
        public long OriginalSize { get; set; }
        public long CompressedSize { get; set; }
        public byte Method { get; set; }
        public byte[] Data { get; set; } = Array.Empty<byte>();
        public DateTime LastWriteTime { get; set; }
    }
    
    /// <summary>
    /// Comprueba si un archivo es un contenedor .cubo válido.
    /// </summary>
    public static bool EsCubo(string ruta)
    {
        if (!File.Exists(ruta)) return false;
        try
        {
            using var fs = File.OpenRead(ruta);
            if (fs.Length < 5) return false;
            byte[] header = new byte[4];
            fs.Read(header, 0, 4);
            return header[0] == MAGIC[0] && header[1] == MAGIC[1] && 
                   header[2] == MAGIC[2] && header[3] == MAGIC[3];
        }
        catch { return false; }
    }
    
    /// <summary>
    /// Crea un contenedor .cubo con múltiples archivos.
    /// Solo comprime si el resultado es más pequeño que el original.
    /// </summary>
    public static void CrearContenedor(string outputPath, Dictionary<string, byte[]> archivos, 
        byte metodoPreferido = METHOD_BWT_ZSTD, 
        IProgress<(string archivo, double progreso)>? progreso = null)
    {
        using var fs = File.Create(outputPath);
        using var bw = new BinaryWriter(fs);
        
        // Header
        bw.Write(MAGIC);
        bw.Write(VERSION);
        
        // Número de archivos
        bw.Write(archivos.Count);
        
        int procesados = 0;
        foreach (var kvp in archivos)
        {
            string nombre = kvp.Key;
            byte[] datos = kvp.Value;
            
            // Intentar comprimir
            byte[] datosComprimidos;
            byte metodoUsado;
            
            try
            {
                datosComprimidos = ComprimirDatos(datos, metodoPreferido);
                
                // Solo usar compresión si reduce el tamaño
                if (datosComprimidos.Length >= datos.Length)
                {
                    datosComprimidos = datos;
                    metodoUsado = METHOD_NONE;
                }
                else
                {
                    metodoUsado = metodoPreferido;
                }
            }
            catch
            {
                // Si falla la compresión, guardar sin comprimir
                datosComprimidos = datos;
                metodoUsado = METHOD_NONE;
            }
            
            // Escribir entrada
            byte[] nombreBytes = System.Text.Encoding.UTF8.GetBytes(nombre);
            bw.Write(nombreBytes.Length);
            bw.Write(nombreBytes);
            bw.Write(datos.Length); // Tamaño original
            bw.Write(datosComprimidos.Length); // Tamaño comprimido
            bw.Write(metodoUsado);
            bw.Write(datosComprimidos);
            
            procesados++;
            progreso?.Report((nombre, (double)procesados / archivos.Count));
        }
    }
    
    /// <summary>
    /// Extrae todos los archivos de un contenedor .cubo.
    /// </summary>
    public static Dictionary<string, byte[]> ExtraerContenedor(string inputPath,
        IProgress<(string archivo, double progreso)>? progreso = null)
    {
        var resultado = new Dictionary<string, byte[]>();
        
        using var fs = File.OpenRead(inputPath);
        using var br = new BinaryReader(fs);
        
        // Verificar magic
        byte[] magic = br.ReadBytes(4);
        if (magic[0] != MAGIC[0] || magic[1] != MAGIC[1] || 
            magic[2] != MAGIC[2] || magic[3] != MAGIC[3])
        {
            throw new Exception("No es un archivo .cubo válido");
        }
        
        byte version = br.ReadByte();
        if (version > VERSION)
            throw new Exception($"Versión no soportada: {version}");
        
        int numArchivos = br.ReadInt32();
        
        for (int i = 0; i < numArchivos; i++)
        {
            int nombreLen = br.ReadInt32();
            string nombre = System.Text.Encoding.UTF8.GetString(br.ReadBytes(nombreLen));
            long tamOriginal = br.ReadInt64();
            long tamComprimido = br.ReadInt64();
            byte metodo = br.ReadByte();
            byte[] datosComprimidos = br.ReadBytes((int)tamComprimido);
            
            byte[] datos;
            if (metodo == METHOD_NONE)
            {
                datos = datosComprimidos;
            }
            else
            {
                datos = DescomprimirDatos(datosComprimidos, metodo, tamOriginal);
            }
            
            resultado[nombre] = datos;
            progreso?.Report((nombre, (double)(i + 1) / numArchivos));
        }
        
        return resultado;
    }
    
    /// <summary>
    /// Lista los archivos contenidos en un .cubo sin extraerlos.
    /// </summary>
    public static List<FileEntry> ListarContenido(string inputPath)
    {
        var entries = new List<FileEntry>();
        
        using var fs = File.OpenRead(inputPath);
        using var br = new BinaryReader(fs);
        
        // Verificar magic
        byte[] magic = br.ReadBytes(4);
        if (magic[0] != MAGIC[0] || magic[1] != MAGIC[1] || 
            magic[2] != MAGIC[2] || magic[3] != MAGIC[3])
        {
            throw new Exception("No es un archivo .cubo válido");
        }
        
        byte version = br.ReadByte();
        int numArchivos = br.ReadInt32();
        
        for (int i = 0; i < numArchivos; i++)
        {
            int nombreLen = br.ReadInt32();
            string nombre = System.Text.Encoding.UTF8.GetString(br.ReadBytes(nombreLen));
            long tamOriginal = br.ReadInt64();
            long tamComprimido = br.ReadInt64();
            byte metodo = br.ReadByte();
            
            entries.Add(new FileEntry
            {
                Name = nombre,
                OriginalSize = tamOriginal,
                CompressedSize = tamComprimido,
                Method = metodo
            });
            
            // Saltar datos
            fs.Seek(tamComprimido, SeekOrigin.Current);
        }
        
        return entries;
    }
    
    /// <summary>Comprime datos usando el método especificado.</summary>
    private static byte[] ComprimirDatos(byte[] data, byte metodo)
    {
        switch (metodo)
        {
            case METHOD_PAQ1:
                return CompresorPAQ.Comprimir(data);
                
            case METHOD_PAQ1_2D:
                // Estimamos ancho para imágenes (asumiendo formato común)
                int width = EstimarAnchoImagen(data);
                return CompresorPAQ.ComprimirCon2D(data, width);
                
            case METHOD_BWT_ZSTD:
            default:
            {
                // Usar BWT + Zstd con formato Cubo3D
                var cubo = new Cubo3D(1, 1, data.Length, data);
                byte[] comprimido = cubo.ComprimirFunciones(out _, direccion: 1);
                
                // Crear el formato completo de Cubo3D en memoria
                using var ms = new MemoryStream();
                using var bw = new BinaryWriter(ms);
                
                bw.Write("CUBO"u8);
                bw.Write((byte)2); // Versión 2
                bw.Write(0); // Nombre vacío (longitud 0)
                bw.Write(1); // ancho
                bw.Write(1); // alto
                bw.Write(data.Length); // profundidad
                bw.Write((long)data.Length); // tamaño original
                bw.Write(comprimido.Length); // longitud comprimida
                bw.Write(comprimido); // datos
                
                return ms.ToArray();
            }
        }
    }
    
    /// <summary>Descomprime datos usando el método especificado.</summary>
    private static byte[] DescomprimirDatos(byte[] data, byte metodo, long tamOriginal)
    {
        switch (metodo)
        {
            case METHOD_PAQ1:
                return CompresorPAQ.Descomprimir(data);
                
            case METHOD_PAQ1_2D:
                return CompresorPAQ.DescomprimirCon2D(data);
                
            case METHOD_BWT_ZSTD:
            default:
            {
                // El formato BWT+Zstd usa el formato de Cubo3D
                // Necesitamos leer el header del formato Cubo3D
                using var ms = new MemoryStream(data);
                using var br = new BinaryReader(ms);
                
                // Leer header del formato Cubo3D
                // "CUBO" + version + nombreLen + nombre + ancho + alto + profundidad + tamOriginal + tamComprimido + datos
                Span<byte> magic = stackalloc byte[4];
                br.Read(magic);
                byte version = br.ReadByte();
                
                string nombreOriginal = "";
                if (version >= 2)
                {
                    int nombreLen = br.ReadInt32();
                    byte[] nombreBytes = br.ReadBytes(nombreLen);
                    nombreOriginal = System.Text.Encoding.UTF8.GetString(nombreBytes);
                }
                
                int ancho = br.ReadInt32();
                int alto = br.ReadInt32();
                int profundidad = br.ReadInt32();
                long tamOriginalCubo = br.ReadInt64();
                int tamComprimido = br.ReadInt32();
                byte[] datosComprimidos = br.ReadBytes(tamComprimido);
                
                return Cubo3D.Descomprimir(datosComprimidos, ancho, alto, profundidad);
            }
        }
    }
    
    /// <summary>Estima el ancho de una imagen a partir de los datos.</summary>
    private static int EstimarAnchoImagen(byte[] data)
    {
        // Heurística: buscar anchos comunes de imágenes
        int[] anchosComunes = { 1920, 1280, 1024, 800, 640, 3840, 2560 };
        long size = data.Length;
        
        foreach (int w in anchosComunes)
        {
            // Asumir 3 bytes por pixel (RGB) o 4 bytes (RGBA)
            if (size % (w * 3) == 0 || size % (w * 4) == 0)
                return w;
        }
        
        // Default: asumir imagen cuadrada
        return (int)Math.Sqrt(size / 3);
    }
}
