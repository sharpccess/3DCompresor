using System;
using System.Diagnostics;
using System.IO;
using Compresor3D;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

// ============================================================
//  Compresor3D - Compresión de archivos mediante reordenamiento
//  en cubos 3D y escaneo de repeticiones RLE en 3 direcciones.
// ============================================================

var (archivo, descomprimir, minDim, maxDim, step, batchFiles) = Utils.ParsearArgumentos(args);

// Mostrar ayuda si no hay argumentos
if (archivo == null && batchFiles.Length == 0)
{
    MostrarAyuda();
    return;
}

// ---- Comando: BATCH (múltiples archivos desde shell extension) ----
if (batchFiles.Length > 0)
{
    EjecutarBatch(batchFiles);
    return;
}

// ---- Comando: DESCOMPRIMIR ----
if (descomprimir)
{
    EjecutarDescompresion(archivo);
    return;
}

// ---- Comando: GENERAR TEST (archivo BMP con estructura) ----
if (archivo == "--generar-test")
{
    GenerarArchivoTest();
    return;
}

// ---- Comando: DECODE PNG (comprimir pixeles raw) ----
if (archivo != null && archivo.StartsWith("--decode-png"))
{
    string pngFile = archivo.Contains(':') ? archivo[(archivo.IndexOf(':') + 1)..] : "";
    if (string.IsNullOrEmpty(pngFile))
    {
        Console.WriteLine("Uso: Compresor3D --decode-png <archivo.png>");
        return;
    }
    DecodificarYComprimirPNG(pngFile);
    return;
}

// ---- Comando: TEST CONTAINER (round-trip multi-archivo) ----
if (archivo == "--test-container")
{
    TestContainer();
    return;
}

// ---- Comando: TEST PAQ1 (comparar con ZIP) ----
if (archivo != null && archivo.StartsWith("--test-paq"))
{
    string testFile = archivo.Contains(':') ? archivo[(archivo.IndexOf(':') + 1)..] : "";
    if (string.IsNullOrEmpty(testFile))
    {
        Console.WriteLine("Uso: Compresor3D --test-paq <archivo>");
        return;
    }
    ProbarPAQ1(testFile);
    return;
}

// ---- Comando: COMPRIMIR (análisis + compresión) ----
EjecutarCompresion(archivo, minDim, maxDim, step);

// ============================================================
//  FUNCIONES LOCALES
// ============================================================

static void MostrarAyuda()
{
    Console.WriteLine("╔══════════════════════════════════════════════════╗");
    Console.WriteLine("║          Compressor3D - Compresión 3D           ║");
    Console.WriteLine("╚══════════════════════════════════════════════════╝");
    Console.WriteLine();
    Console.WriteLine("Uso: Compresor3D [opciones]");
    Console.WriteLine();
    Console.WriteLine("Opciones:");
    Console.WriteLine("  --file, -f <ruta>    Archivo a comprimir (o arrastrar y soltar)");
    Console.WriteLine("  --descomprimir, -d   Descomprimir un archivo .cubo");
    Console.WriteLine("  --min-dim <n>        Dimensión mínima del cubo (default: 1)");
    Console.WriteLine("  --max-dim <n>        Dimensión máxima del cubo (default: sin límite)");
    Console.WriteLine("  --step <n>           Incremento entre dimensiones (default: 1)");
    Console.WriteLine();
    Console.WriteLine("Ejemplos:");
    Console.WriteLine("  Compresor3D --file \"C:\\archivo.bin\"");
    Console.WriteLine("  Compresor3D --file \"C:\\archivo.bin\" --min-dim 2 --max-dim 100 --step 2");
    Console.WriteLine("  Compresor3D --descomprimir \"C:\\archivo.cubo\"");
    Console.WriteLine("  Compresor3D \"C:\\archivo.bin\"              (drag & drop)");
}

static void EjecutarDescompresion(string archivo)
{
    Console.WriteLine("╔══════════════════════════════════════════════════╗");
    Console.WriteLine("║          Compresor3D - Descompresión            ║");
    Console.WriteLine("╚══════════════════════════════════════════════════╝");
    Console.WriteLine();

    if (!File.Exists(archivo))
    {
        Console.WriteLine($"  ERROR: No se encontró el archivo: {archivo}");
        return;
    }

    try
    {
        Compresor3DEngine.DescomprimirArchivo(archivo);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  ERROR al descomprimir: {ex.Message}");
    }
}

static void EjecutarBatch(string[] archivos)
{
    Console.WriteLine("╔══════════════════════════════════════════════════╗");
    Console.WriteLine("║          Compressor3D - Compresión por lotes    ║");
    Console.WriteLine("╚══════════════════════════════════════════════════╝");
    Console.WriteLine();
    Console.WriteLine($"  Procesando {archivos.Length} archivos...");
    Console.WriteLine();

    // Recopilar archivos válidos
    var archivosDatos = new Dictionary<string, byte[]>();
    long totalOriginal = 0;

    foreach (var ruta in archivos)
    {
        string nombre = Path.GetFileName(ruta);
        if (!File.Exists(ruta))
        {
            Console.WriteLine($"  ✗ {nombre} - No encontrado");
            continue;
        }

        try
        {
            byte[] datos = File.ReadAllBytes(ruta);
            archivosDatos[nombre] = datos;
            totalOriginal += datos.Length;
            Console.WriteLine($"  + {nombre} ({Utils.FormatearTamano(datos.Length)})");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  ✗ {nombre} - Error: {ex.Message}");
        }
    }

    if (archivosDatos.Count == 0)
    {
        Console.WriteLine("\n  No hay archivos para comprimir.");
        return;
    }

    // Determinar nombre de salida
    string outputPath;
    if (archivosDatos.Count == 1)
    {
        string key = archivosDatos.Keys.First();
        outputPath = archivos[0] + ".cubo";
    }
    else
    {
        // Usar nombre de la carpeta padre o "archivo.cubo"
        string? dir = Path.GetDirectoryName(Path.GetFullPath(archivos[0]));
        string baseName = dir != null ? Path.GetFileName(dir) : "archivo";
        if (string.IsNullOrEmpty(baseName)) baseName = "archivo";
        outputPath = Path.Combine(dir ?? ".", baseName + ".cubo");
    }

    // Elegir método
    byte metodo = CuboContainer.METHOD_BWT_ZSTD; // Default
    // Si todos son texto, usar PAQ1
    bool todosTexto = archivosDatos.All(kvp =>
    {
        string ext = Path.GetExtension(kvp.Key).ToLower();
        return ext is ".txt" or ".csv" or ".log" or ".json" or ".xml" or ".md" or ".html" or ".css" or ".js" or ".cs" or ".py";
    });
    if (todosTexto && archivosDatos.Values.All(d => d.Length < 5 * 1024 * 1024))
        metodo = CuboContainer.METHOD_PAQ1;

    string metodoNombre = metodo == CuboContainer.METHOD_PAQ1 ? "PAQ1" : "BWT+Zstd";
    Console.WriteLine($"\n  Método: {metodoNombre}");
    Console.WriteLine($"  Creando: {outputPath}");
    Console.WriteLine();

    try
    {
        var progreso = new Progress<(string archivo, double prog)>(p =>
        {
            Console.WriteLine($"  [{p.prog:P0}] {p.archivo}");
        });

        CuboContainer.CrearContenedor(outputPath, archivosDatos, metodo, progreso);

        long tamComprimido = new FileInfo(outputPath).Length;
        double ratio = totalOriginal > 0 ? (double)tamComprimido / totalOriginal * 100 : 0;

        Console.WriteLine();
        Console.WriteLine($"  ✓ Archivo creado: {outputPath}");
        Console.WriteLine($"  ✓ {Utils.FormatearTamano(totalOriginal)} → {Utils.FormatearTamano(tamComprimido)} ({ratio:F1}%)");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"\n  ERROR: {ex.Message}");
    }
}

static void EjecutarCompresion(string archivo, int minDim, int maxDim, int step)
{
    Console.WriteLine("╔══════════════════════════════════════════════════╗");
    Console.WriteLine("║          Compresor3D - Análisis de Cubo 3D      ║");
    Console.WriteLine("╚══════════════════════════════════════════════════╝");
    Console.WriteLine();

    if (!File.Exists(archivo))
    {
        Console.WriteLine($"  ERROR: No se encontró el archivo: {archivo}");
        return;
    }

    // Leer archivo completo
    byte[] datos = File.ReadAllBytes(archivo);
    long tamanoOriginal = datos.Length;

    Console.WriteLine($"  Archivo: {Path.GetFileName(archivo)}");
    Console.WriteLine($"  Tamaño: {Utils.FormatearTamano(tamanoOriginal)}");
    Console.WriteLine();

    // Si el archivo es grande, usar solo un fragmento para el análisis
    byte[] muestra;
    if (datos.Length > Compresor3DEngine.MAX_FRAGMENTO)
    {
        muestra = datos[..Compresor3DEngine.MAX_FRAGMENTO];
        Console.WriteLine($"  NOTA: Archivo grande. Analizando primeros {Utils.FormatearTamano(Compresor3DEngine.MAX_FRAGMENTO)}...");
    }
    else
    {
        muestra = datos;
    }

    Console.WriteLine($"  Parámetros: min-dim={minDim}, max-dim={(maxDim == int.MaxValue ? "∞" : maxDim.ToString())}, step={step}");
    Console.WriteLine();

    // Ejecutar análisis
    Console.WriteLine("  Buscando mejores dimensiones...");
    var (resultados, duracionAnalisis) = Compresor3DEngine.Analizar(muestra, minDim, maxDim, step);

    // Mostrar resultados
    Console.WriteLine();
    if (resultados.Count == 0)
    {
        Console.WriteLine("  No se encontraron combinaciones de dimensiones válidas.");
        Console.WriteLine("  Intenta ajustar --min-dim, --max-dim o --step.");
        return;
    }

    int topN = Math.Min(10, resultados.Count);
    Console.WriteLine($"  Se probaron {resultados.Count} combinaciones en {Utils.FormatearTiempo(duracionAnalisis)}");
    Console.WriteLine();
    MostrarTabla(resultados, topN, tamanoOriginal);

    // Comprimir con las mejores dimensiones
    var mejor = resultados[0];
    Console.WriteLine();
    Console.WriteLine($"  Mejor combinación: {mejor.Ancho} x {mejor.Alto} x {mejor.Profundidad}");
    Console.WriteLine($"  Comprimiendo archivo completo con estas dimensiones...");

    var swComp = Stopwatch.StartNew();
    var (datosComprimidos, tamanoComprimido, direccion, totalLineas, lineasUnicas, metodo) = Compresor3DEngine.Comprimir(
        datos, mejor.Ancho, mejor.Alto, mejor.Profundidad);
    swComp.Stop();

    double ratio = tamanoOriginal > 0 ? (1.0 - (double)tamanoComprimido / tamanoOriginal) * 100 : 0;
    string[] dirNombres = { "X", "Y", "Z" };
    Console.WriteLine($"  Dirección de escaneo: {dirNombres[direccion]}");
    Console.WriteLine($"  Método de compresión: {metodo}");
    Console.WriteLine($"  Tamaño comprimido: {Utils.FormatearTamano(tamanoComprimido)} (ratio: {ratio:F1}%)");
    Console.WriteLine($"  Tiempo de compresión: {Utils.FormatearTiempo(swComp.Elapsed)}");

    // Guardar archivo .cubo
    string rutaCubo = Path.ChangeExtension(archivo, ".cubo");
    string nombreOriginal = Path.GetFileName(archivo);
    Console.WriteLine();
    Console.WriteLine($"  Guardando archivo .cubo: {rutaCubo}");
    Compresor3DEngine.GuardarCubo(rutaCubo, nombreOriginal, mejor.Ancho, mejor.Alto, mejor.Profundidad,
        tamanoOriginal, datosComprimidos);
    Console.WriteLine($"  Archivo guardado correctamente ({Utils.FormatearTamano(new FileInfo(rutaCubo).Length)}).");
    Console.WriteLine();
    Console.WriteLine("  Para descomprimir:");
    Console.WriteLine($"    Compresor3D --descomprimir \"{rutaCubo}\"");
}

static void MostrarTabla(System.Collections.Generic.List<ResultadoCompresion> resultados, int topN, long tamanoOriginal)
{
    // Cabecera de la tabla
    Console.WriteLine("  ┌───────┬──────────────────┬──────────────────┬──────────┬──────────┐");
    Console.WriteLine("  │ Puesto│   Dimensiones    │  Runs (mejor dir)│  Calidad │  Tiempo  │");
    Console.WriteLine("  ├───────┼──────────────────┼──────────────────┼──────────┼──────────┤");

    for (int i = 0; i < topN; i++)
    {
        var r = resultados[i];
        string dims = $"{r.Ancho} x {r.Alto} x {r.Profundidad}";
        string runs = $"{r.CompressedSize:N0}";
        // Calidad: % de runs respecto al total de bytes (menos = mejor)
        double calidad = tamanoOriginal > 0 ? (1.0 - (double)r.CompressedSize / tamanoOriginal) * 100 : 0;
        string tiempo = Utils.FormatearTiempo(r.Duracion);

        Console.WriteLine(
            $"  │ {i + 1,5} │ {dims,-16} │ {runs,16} │ {calidad,7:F1}% │ {tiempo,8} │");
    }

    Console.WriteLine("  └───────┴──────────────────┴──────────────────┴──────────┴──────────┘");
}

// ============================================================
//  GENERAR ARCHIVO DE TEST (BMP con estructura)
// ============================================================
static void GenerarArchivoTest()
{
    Console.WriteLine("Generando archivo de test (BMP 512x512 con estructura)...");

    int width = 512, height = 512;
    int rowBytes = width * 3;
    int padding = (4 - rowBytes % 4) % 4;
    int pixelDataSize = (rowBytes + padding) * height;

    using var ms = new MemoryStream();
    using var bw = new BinaryWriter(ms);

    // File header (14 bytes)
    int fileSize = 14 + 40 + pixelDataSize;
    bw.Write((byte)'B'); bw.Write((byte)'M');
    bw.Write(fileSize);
    bw.Write(0); // reserved
    bw.Write(14 + 40); // offset to pixel data

    // Info header (40 bytes)
    bw.Write(40); // header size
    bw.Write(width);
    bw.Write(height);
    bw.Write((short)1); // planes
    bw.Write((short)24); // bits per pixel
    bw.Write(0); // compression (BI_RGB)
    bw.Write(pixelDataSize);
    bw.Write(2835); // X ppm
    bw.Write(2835); // Y ppm
    bw.Write(0); // colors used
    bw.Write(0); // important colors

    // Pixel data (BGR, bottom-up)
    var rng = new Random(42);
    for (int y = 0; y < height; y++)
    {
        int actualY = height - 1 - y; // BMP is bottom-up
        for (int x = 0; x < width; x++)
        {
            byte r, g, b;

            if (actualY < 128 && x < 256)
            {
                // Cuadrante 1: Gradiente suave (ideal para delta/BWT)
                b = (byte)(x * 255 / 255);
                g = (byte)(actualY * 255 / 127);
                r = (byte)((x + actualY) / 2);
            }
            else if (actualY < 128)
            {
                // Cuadrante 2: Patrón repetido (ideal para BWT)
                int pattern = (x + actualY * 3) % 16;
                b = g = r = (byte)(pattern * 16);
            }
            else if (x < 256)
            {
                // Cuadrante 3: Regiones uniformes con bordes (ideal para RLE)
                int region = (actualY / 32 + x / 32) % 4;
                (b, g, r) = region switch
                {
                    0 => ((byte)255, (byte)0, (byte)0),
                    1 => ((byte)0, (byte)255, (byte)0),
                    2 => ((byte)0, (byte)0, (byte)255),
                    _ => ((byte)255, (byte)255, (byte)255)
                };
            }
            else
            {
                // Cuadrante 4: Onda sinusoidal (estructura predecible)
                double wave = Math.Sin(x * 0.1) * Math.Cos(actualY * 0.08);
                byte val = (byte)(128 + wave * 127);
                b = g = r = val;
            }

            bw.Write(b); bw.Write(g); bw.Write(r);
        }
        // Padding to 4-byte boundary
        for (int p = 0; p < padding; p++) bw.Write((byte)0);
    }

    string outputPath = "test_image.bmp";
    File.WriteAllBytes(outputPath, ms.ToArray());
    Console.WriteLine($"  Creado: {outputPath} ({Utils.FormatearTamano(ms.Length)})");
    Console.WriteLine($"  Estructura: 4 cuadrantes (gradiente, patrón, regiones, onda)");
}

// ============================================================
//  DECODE PNG → PIXELES RAW → COMPRESIÓN
// ============================================================
static void DecodificarYComprimirPNG(string pngFile)
{
    if (!File.Exists(pngFile))
    {
        Console.WriteLine($"  ERROR: No se encontró: {pngFile}");
        return;
    }

    Console.WriteLine("╔══════════════════════════════════════════════════╗");
    Console.WriteLine("║   Compresor3D - Modo Decode PNG → Pixeles Raw   ║");
    Console.WriteLine("╚══════════════════════════════════════════════════╝");
    Console.WriteLine();

    // Cargar PNG y extraer pixeles raw
    using var image = SixLabors.ImageSharp.Image.Load<SixLabors.ImageSharp.PixelFormats.Rgba32>(pngFile);
    int w = image.Width, h = image.Height;
    byte[] pixeles = new byte[w * h * 4]; // RGBA

    image.CopyPixelDataTo(pixeles);

    Console.WriteLine($"  PNG: {w}x{h} = {Utils.FormatearTamano(pixeles.Length)} pixeles raw (RGBA)");
    Console.WriteLine($"  Archivo original: {Utils.FormatearTamano(new FileInfo(pngFile).Length)}");
    Console.WriteLine();

    // Crear header con metadatos (formato: width:4, height:4, pixelformat:4, data)
    byte[] pixelesConHeader = new byte[12 + pixeles.Length];
    BitConverter.GetBytes(w).CopyTo(pixelesConHeader, 0);
    BitConverter.GetBytes(h).CopyTo(pixelesConHeader, 4);
    BitConverter.GetBytes(0).CopyTo(pixelesConHeader, 8); // 0 = RGBA
    pixeles.CopyTo(pixelesConHeader, 12);

    // Comprimir los pixeles raw con el pipeline normal
    Console.WriteLine("Comprimiendo pixeles raw...");
    var sw = Stopwatch.StartNew();
    var cubo = new Cubo3D(1, 1, pixelesConHeader.Length, pixelesConHeader);
    var resultado = cubo.ComprimirFunciones(out long compressedSize, direccion: 1);
    sw.Stop();

    string cuboFile = Path.ChangeExtension(pngFile, ".cubo");
    File.WriteAllBytes(cuboFile, resultado);

    Console.WriteLine();
    Console.WriteLine($"  Pixeles raw: {Utils.FormatearTamano(pixelesConHeader.Length)}");
    Console.WriteLine($"  Comprimido:  {Utils.FormatearTamano(compressedSize)}");
    double ratio = (double)compressedSize / pixelesConHeader.Length * 100;
    Console.WriteLine($"  Ratio:       {ratio:F1}%");
    Console.WriteLine($"  Tiempo:      {Utils.FormatearTiempo(sw.Elapsed)}");

    // Verificar round-trip
    Console.WriteLine();
    Console.WriteLine("Verificando round-trip...");
    byte[] descomprimido = Cubo3D.Descomprimir(resultado, 1, 1, pixelesConHeader.Length);
    
    // Verificar header
    int w2 = BitConverter.ToInt32(descomprimido, 0);
    int h2 = BitConverter.ToInt32(descomprimido, 4);
    int pf = BitConverter.ToInt32(descomprimido, 8);
    
    if (w2 == w && h2 == h && pf == 0)
    {
        Console.WriteLine($"  ✓ Header correcto: {w2}x{h2}, formato {pf}");
        
        // Extraer pixeles
        byte[] pixelesRecuperados = new byte[descomprimido.Length - 12];
        Array.Copy(descomprimido, 12, pixelesRecuperados, 0, pixelesRecuperados.Length);
        
        // Verificar SHA-256
        using var sha = System.Security.Cryptography.SHA256.Create();
        string hashOriginal = Convert.ToHexString(sha.ComputeHash(pixeles));
        string hashRecuperado = Convert.ToHexString(sha.ComputeHash(pixelesRecuperados));
        
        if (hashOriginal == hashRecuperado)
        {
            Console.WriteLine($"  ✓ SHA-256 match: round-trip perfecto!");
            
            // Re-encodificar como PNG
            Console.WriteLine();
            Console.WriteLine("Re-encodificando como PNG...");
            using var image2 = SixLabors.ImageSharp.Image.LoadPixelData<SixLabors.ImageSharp.PixelFormats.Rgba32>(
                pixelesRecuperados, w, h);
            
            string outputPng = Path.ChangeExtension(pngFile, ".reconstruido.png");
            image2.SaveAsPng(outputPng);
            Console.WriteLine($"  ✓ PNG reconstruido: {outputPng} ({Utils.FormatearTamano(new FileInfo(outputPng).Length)})");
        }
        else
        {
            Console.WriteLine($"  ✗ SHA-256 MISMATCH!");
            Console.WriteLine($"    Original:    {hashOriginal[..16]}...");
            Console.WriteLine($"    Recuperado:  {hashRecuperado[..16]}...");
        }
    }
    else
    {
        Console.WriteLine($"  ✗ Header incorrecto: esperado {w}x{h}/0, obtenido {w2}x{h2}/{pf}");
    }

    // Comparar con comprimir el PNG directamente
    Console.WriteLine();
    Console.WriteLine("Comparación con PNG directo:");
    sw.Restart();
    byte[] pngData = File.ReadAllBytes(pngFile);
    var cubo3 = new Cubo3D(1, 1, pngData.Length, pngData);
    var resultado3 = cubo3.ComprimirFunciones(out long compressedSize3, direccion: 1);
    sw.Stop();

    Console.WriteLine($"  PNG directo:     {Utils.FormatearTamano(new FileInfo(pngFile).Length)} → {Utils.FormatearTamano(compressedSize3)} ({(double)compressedSize3 / new FileInfo(pngFile).Length * 100:F1}%)");
    Console.WriteLine($"  PNG→Raw→Compr:   {Utils.FormatearTamano(pixelesConHeader.Length)} → {Utils.FormatearTamano(compressedSize)} ({ratio:F1}%)");
    Console.WriteLine($"  Mejora:          {((double)compressedSize3 / compressedSize):F1}x mejor con decode");
}

static void ProbarPAQ1(string testFile)
{
    Console.WriteLine("╔══════════════════════════════════════════════════╗");
    Console.WriteLine("║          Test PAQ1 vs ZIP                       ║");
    Console.WriteLine("╚══════════════════════════════════════════════════╝");
    Console.WriteLine();

    if (!File.Exists(testFile))
    {
        Console.WriteLine($"  ERROR: No se encontró el archivo: {testFile}");
        return;
    }

    byte[] data = File.ReadAllBytes(testFile);
    long originalSize = data.Length;
    Console.WriteLine($"  Archivo: {Path.GetFileName(testFile)}");
    Console.WriteLine($"  Tamaño original: {Utils.FormatearTamano(originalSize)}");
    Console.WriteLine();

    // PAQ1
    Console.WriteLine("  Comprimiendo con PAQ1...");
    var sw = Stopwatch.StartNew();
    byte[] compressedPAQ = CompresorPAQ.Comprimir(data);
    sw.Stop();
    long paqSize = compressedPAQ.Length;
    Console.WriteLine($"  PAQ1: {Utils.FormatearTamano(originalSize)} → {Utils.FormatearTamano(paqSize)} ({(double)paqSize / originalSize * 100:F1}%) en {sw.ElapsedMilliseconds}ms");

    // Verificar round-trip
    Console.WriteLine("  Verificando round-trip...");
    byte[] decompressed = CompresorPAQ.Descomprimir(compressedPAQ);
    if (decompressed.Length == data.Length && decompressed.SequenceEqual(data))
    {
        Console.WriteLine("  ✓ Round-trip OK");
    }
    else
    {
        Console.WriteLine($"  ✗ Round-trip FAILED: esperado {data.Length}, obtenido {decompressed.Length}");
    }

    // Comparar con ZIP
    Console.WriteLine();
    Console.WriteLine("  Comprimiendo con ZIP para comparar...");
    sw.Restart();
    var cubo = new Cubo3D(1, 1, data.Length, data);
    var resultado = cubo.ComprimirFunciones(out long zipSize, direccion: 1);
    sw.Stop();
    Console.WriteLine($"  ZIP:  {Utils.FormatearTamano(originalSize)} → {Utils.FormatearTamano(zipSize)} ({(double)zipSize / originalSize * 100:F1}%) en {sw.ElapsedMilliseconds}ms");

    // Comparación
    Console.WriteLine();
    double ratioPAQ = (double)paqSize / originalSize;
    double ratioZIP = (double)zipSize / originalSize;
    double mejora = ratioZIP / ratioPAQ;
    
    if (paqSize < zipSize)
    {
        Console.WriteLine($"  ✓ PAQ1 GANA: {mejora:F2}x mejor que ZIP");
    }
    else
    {
        Console.WriteLine($"  ✗ PAQ1 PIERDE: {1/mejora:F2}x peor que ZIP");
    }
}

static void TestContainer()
{
    Console.WriteLine("╔══════════════════════════════════════════════════╗");
    Console.WriteLine("║     Test Round-Trip CuboContainer               ║");
    Console.WriteLine("╚══════════════════════════════════════════════════╝");
    Console.WriteLine();
    
    string testDir = Path.Combine(Path.GetTempPath(), "cubo_test_" + Guid.NewGuid().ToString("N")[..8]);
    Directory.CreateDirectory(testDir);
    
    try
    {
        // 1. Crear archivos de prueba
        Console.WriteLine("1. Creando archivos de prueba...");
        var archivos = new Dictionary<string, byte[]>();
        
        // Texto repetitivo
        string texto = string.Concat(Enumerable.Repeat("Hola mundo, este es un archivo de texto de prueba para Compressor3D.\n", 100));
        archivos["documento.txt"] = System.Text.Encoding.UTF8.GetBytes(texto);
        Console.WriteLine($"   documento.txt: {archivos["documento.txt"].Length} bytes");
        
        // JSON
        string json = "{\"nombre\":\"test\",\"valor\":42,\"items\":[1,2,3]}";
        archivos["datos.json"] = System.Text.Encoding.UTF8.GetBytes(json);
        Console.WriteLine($"   datos.json: {archivos["datos.json"].Length} bytes");
        
        // Binario con subcarpeta
        byte[] binario = new byte[1024];
        new Random(42).NextBytes(binario);
        archivos["subcarpeta/binario.bin"] = binario;
        Console.WriteLine($"   subcarpeta/binario.bin: {archivos["subcarpeta/binario.bin"].Length} bytes");
        
        // Archivo grande con patrón repetitivo
        byte[] patron = new byte[4096];
        for (int i = 0; i < patron.Length; i++) patron[i] = (byte)(i % 256);
        byte[] grande = new byte[50000];
        for (int i = 0; i < grande.Length; i += patron.Length)
            Array.Copy(patron, 0, grande, i, Math.Min(patron.Length, grande.Length - i));
        archivos["grande.dat"] = grande;
        Console.WriteLine($"   grande.dat: {archivos["grande.dat"].Length} bytes");
        
        Console.WriteLine();
        
        // 2. Comprimir con cada método
        byte[] methods = { CuboContainer.METHOD_BWT_ZSTD, CuboContainer.METHOD_PAQ1 };
        string[] methodNames = { "BWT+Zstd", "PAQ1" };
        
        for (int m = 0; m < methods.Length; m++)
        {
            Console.WriteLine($"--- Método: {methodNames[m]} ---");
            string cuboPath = Path.Combine(testDir, $"test_{m}.cubo");
            
            try
            {
                CuboContainer.CrearContenedor(cuboPath, archivos, methods[m]);
                
                FileInfo info = new FileInfo(cuboPath);
                long totalOriginal = 0;
                foreach (var a in archivos) totalOriginal += a.Value.Length;
                
                double ratio = (double)info.Length / totalOriginal * 100;
                Console.WriteLine($"   Contenedor: {info.Length} bytes (original: {totalOriginal}, ratio: {ratio:F1}%)");
                
                // Listar contenido
                var entries = CuboContainer.ListarContenido(cuboPath);
                foreach (var e in entries)
                {
                    string met = e.Method switch { 0 => "STORE", 1 => "BWT+Zstd", 2 => "PAQ1", 3 => "PAQ1_2D", _ => "?" };
                    double r = e.OriginalSize > 0 ? (double)e.CompressedSize / e.OriginalSize * 100 : 0;
                    Console.WriteLine($"     {e.Name}: {e.OriginalSize} -> {e.CompressedSize} ({r:F1}%) [{met}]");
                }
                
                // Descomprimir
                var extraidos = CuboContainer.ExtraerContenedor(cuboPath);
                
                // Verificar
                bool todoOk = true;
                foreach (var kvp in archivos)
                {
                    if (!extraidos.ContainsKey(kvp.Key))
                    {
                        Console.WriteLine($"   ✗ FALTA: {kvp.Key}");
                        todoOk = false;
                        continue;
                    }
                    
                    byte[] original = kvp.Value;
                    byte[] extraido = extraidos[kvp.Key];
                    
                    if (original.Length != extraido.Length)
                    {
                        Console.WriteLine($"   ✗ {kvp.Key}: tamaño diferente ({original.Length} vs {extraido.Length})");
                        todoOk = false;
                        continue;
                    }
                    
                    string hashOrig = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(original))[..16];
                    string hashExtra = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(extraido))[..16];
                    
                    if (hashOrig != hashExtra)
                    {
                        Console.WriteLine($"   ✗ {kvp.Key}: contenido diferente");
                        todoOk = false;
                    }
                    else
                    {
                        Console.WriteLine($"   ✓ {kvp.Key}: OK");
                    }
                }
                
                Console.WriteLine(todoOk ? $"   >>> {methodNames[m]}: TODOS OK <<<" : $"   >>> {methodNames[m]}: ERRORES <<<");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"   ERROR: {ex.Message}");
            }
            Console.WriteLine();
        }
        
        // 3. Test smart compression
        Console.WriteLine("--- Smart Compression ---");
        byte[] randomData = new byte[100];
        new Random().NextBytes(randomData);
        var unSoloArchivo = new Dictionary<string, byte[]> { { "random.dat", randomData } };
        string smartPath = Path.Combine(testDir, "smart.cubo");
        CuboContainer.CrearContenedor(smartPath, unSoloArchivo, CuboContainer.METHOD_PAQ1);
        var smartEntries = CuboContainer.ListarContenido(smartPath);
        Console.WriteLine($"   Datos aleatorios 100b con PAQ1: method={smartEntries[0].Method} (0=STORE)");
        Console.WriteLine(smartEntries[0].Method == 0 ? "   ✓ No comprimió datos aleatorios" : "   ✗ Debería guardar sin comprimir");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR GENERAL: {ex.Message}");
        Console.WriteLine(ex.StackTrace);
    }
    finally
    {
        if (Directory.Exists(testDir))
            try { Directory.Delete(testDir, true); } catch { }
    }
    
    Console.WriteLine();
    Console.WriteLine("=== Test completado ===");
}
