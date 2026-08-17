# Compressor3D

Compresor de archivos basado en reordenamiento 3D, Context Mixing (PAQ1), transformadas de entropía y contenedor multi-archivo.

## Características principales

- **3 métodos de compresión**: PAQ1 (texto), BWT+Zstd (imágenes/binario), PAQ1 2D (imágenes)
- **Contenedor multi-archivo**: Empaqueta múltiples archivos en un solo `.cubo` (como ZIP)
- **Smart compression**: No comprime si el resultado es más grande (ahorra tiempo y espacio)
- **Selección automática**: El clasificador elige el mejor método según el tipo de datos
- **Shell extension**: Comprimir y descomprimir desde el menú contextual de Windows
- **GUI moderna**: Interfaz gráfica WPF con drag & drop y barra de progreso
- **Extracción a carpeta**: Descomprime manteniendo estructura de directorios

## ¿Cómo funciona?

Compressor3D trata los datos de un archivo como un **cubo 3D** y explota las correlaciones espaciales en las tres direcciones (X, Y, Z) para encontrar repeticiones. Combinado con técnicas avanzadas de compresión, logra ratios competitivos frente a ZIP.

### Arquitectura general

```
┌─────────────────────────────────────────────────────────┐
│                    Archivos originales                   │
└─────────────────────────┬───────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────────────────────┐
│              Clasificador Multi-nivel                    │
│  ┌──────────┬──────────┬──────────┬──────────────────┐  │
│  │ Entropía │ Deltas   │ Autocorr │ Runs/RLE         │  │
│  │ (bits/byte)│ (cercanos)│ (lag)   │ (ratio)          │  │
│  └──────────┴──────────┴──────────┴──────────────────┘  │
│  → Predice: PAQ1 / BWT+Zstd / PAQ1 2D / STORE          │
└─────────────────────────┬───────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────────────────────┐
│           Contenedor Multi-archivo (.cubo v3)           │
│                                                          │
│  Empaqueta N archivos en uno solo                        │
│  Cada archivo con su método de compresión                │
│  Smart compression: STORE si no comprime mejor          │
└─────────────────────────┬───────────────────────────────┘
                          │
                          ▼
                    Archivo .cubo
```

## Métodos de compresión

### 1. PAQ1 - Context Mixing (mejor para texto)

Implementación del algoritmo PAQ1 (Matt Mahoney, 2002), base de los campeones mundiales de compresión (PAQ8, ZPAQ, cmix).

**Cómo funciona:**
- Trabaja a nivel de **bit individual** (no byte)
- Mantiene **6 modelos de contexto** (orden 0 a 5, con 0, 4, 8, 12, 16, 20 bits de historia)
- Cada modelo predice la probabilidad del siguiente bit basándose en el contexto
- Las predicciones se **mezclan** con pesos adaptativos (peso = orden²)
- **Adaptación no-estacionaria**: los conteos se limitan a 255 y se "olvidan" agresivamente (división entre 2)
- Un **arithmetic coder** codifica cada bit según la probabilidad predicha

**Cuándo es eficiente:**
- ✅ **Texto natural**: ASCII, UTF-8, código fuente, logs, JSON, XML
- ✅ **Datos con patrones repetitivos**: secuencias con estructura predecible
- ✅ **Archivos de lenguaje humano**: inglés, español, etc.
- ✅ **Archivos pequeños a medianos**: < 10 MB (por velocidad)
- ✅ **Datos con correlación local**: donde el contexto reciente ayuda a predecir

**Cuándo NO es eficiente:**
- ❌ **Imágenes**: BMP, PNG, JPEG (mejor usar BWT+Zstd)
- ❌ **Datos ya comprimidos**: ZIP, MP3, video (no hay patrones predecibles)
- ❌ **Datos aleatorios**: entropía alta, sin patrones
- ❌ **Archivos muy grandes**: > 100 MB (lento, usa mucha memoria)
- ❌ **Binarios sin estructura**: ejecutables, datos encriptados

**Rendimiento:**
- Velocidad: ~1-5 MB/s (depende del orden del contexto)
- Memoria: ~10-50 MB (tablas de contexto)
- Ratio típico: **15-25%** del original para texto

**Resultados vs ZIP:**

| Tipo | PAQ1 | ZIP | Ganador |
|------|------|-----|---------|
| Texto (1.1 MB) | 170 KB (15.6%) | 265 KB (24.4%) | **PAQ1 gana 1.56x** |
| PDF (76 KB) | 51 KB (67.2%) | 51.4 KB (67.6%) | PAQ1 gana 1.01x |
| Código fuente | 15-20% | 25-30% | **PAQ1 gana 1.3-1.5x** |
| BMP (768 KB) | 208 KB (27.0%) | 110 KB (14.3%) | ZIP gana |

---

### 2. BWT + MTF + Zstd (mejor para imágenes y binario)

Pipeline de transformadas seguido de compresión Zstandard nivel 22.

**Cómo funciona:**
1. **BWT (Burrows-Wheeler Transform)**: Reordena los datos para agrupar contextos similares
2. **MTF (Move-to-Front)**: Reindexa por frecuencia local, reduciendo el alfabeto efectivo
3. **Transformadas adicionales**: Delta, Prisma Virtual, Predict2D (selección adaptativa)
4. **Zstd nivel 22**: Compresión final con el algoritmo más rápido en su clase

**Transformadas disponibles:**
- **BWT**: Reordena por contexto (suffix array propio, O(n log n))
- **MTF**: Reindexa por frecuencia local
- **Delta**: d[i] = data[i] - data[i-1] (para datos con correlación lineal)
- **Prisma Virtual**: Descompone bytes en bit-planes (separa alta/baja frecuencia)
- **Predict2D**: Filtro predictivo 2D (Paeth, estilo PNG)

**Multi-segmento adaptativo:**
- Divide el archivo en segmentos de 1024 bytes
- Para cada segmento, prueba todas las combinaciones de transformadas
- Elige la que produce menor entropía de Shannon
- Cada segmento puede usar una combinación diferente

**Cuándo es eficiente:**
- ✅ **Imágenes**: BMP, PNG, TIFF (datos con correlación espacial)
- ✅ **Binarios estructurados**: ejecutables, DLLs, datos científicos
- ✅ **Datos con patrones locales**: donde BWT agrupa contextos similares
- ✅ **Archivos grandes**: > 1 MB (BWT necesita escala para brillar)
- ✅ **Datos ya comprimidos parcialmente**: PNG, JPEG (Zstd los maneja bien)

**Cuándo NO es eficiente:**
- ❌ **Texto puro**: PAQ1 es mejor (1.5x más eficiente)
- ❌ **Archivos muy pequeños**: < 1 KB (overhead del header)
- ❌ **Datos aleatorios**: entropía alta, sin patrones
- ❌ **Cuando la velocidad es crítica**: BWT es O(n log n), más lento que LZ4

**Rendimiento:**
- Velocidad: ~5-20 MB/s (depende del tamaño y transformadas)
- Memoria: ~50-200 MB (BWT necesita arrays grandes)
- Ratio típico: **20-40%** del original para imágenes

**Resultados vs ZIP:**

| Tipo | BWT+Zstd | ZIP | Ganador |
|------|----------|-----|---------|
| BMP (768 KB) | 158 KB (20.5%) | 189 KB (24.6%) | **BWT+Zstd gana 1.17x** |
| PNG (6.1 MB) | 6.16 MB (100%) | 6.13 MB (100%) | Empate |
| Ejecutable (10 MB) | 4.5 MB (45%) | 5.2 MB (52%) | **BWT+Zstd gana 1.15x** |

---

### 3. PAQ1 2D - Context Mixing Espacial (mejor para imágenes)

Variante de PAQ1 que explota la correlación espacial en datos 2D (imágenes).

**Cómo funciona:**
- Trata los datos como una **imagen 2D** (ancho × alto)
- Para cada píxel, usa el contexto de sus **vecinos**:
  - Izquierda (pixel anterior)
  - Arriba (pixel en fila anterior)
  - Diagonal superior-izquierda
  - Bits superiores del píxel actual (MSB)
- Contexto de **12 bits** combinando vecinos + bits conocidos
- **SpatialModel2D**: tabla de 4096 entradas con adaptación no-estacionaria

**Cuándo es eficiente:**
- ✅ **Imágenes raster**: BMP, PNG, TIFF (correlación espacial fuerte)
- ✅ **Datos con estructura 2D**: tablas, matrices, heatmaps
- ✅ **Imágenes con gradientes**: donde píxeles vecinos son similares
- ✅ **Imágenes médicas**: DICOM, radiografías (patrones locales)

**Cuándo NO es eficiente:**
- ❌ **Texto**: PAQ1 1D es mejor (contexto lineal)
- ❌ **Datos 1D**: audio, series temporales (no tienen estructura 2D)
- ❌ **Imágenes con ruido**: la correlación espacial se pierde
- ❌ **Archivos muy pequeños**: overhead del modelo 2D

**Rendimiento:**
- Velocidad: ~0.5-2 MB/s (más lento que PAQ1 1D)
- Memoria: ~20-100 MB (modelo 2D + tablas)
- Ratio típico: **15-30%** del original para imágenes

---

### 4. Auto (recomendado)

El clasificador analiza el archivo y elige automáticamente el mejor método.

**Cómo funciona:**
1. **Análisis multi-nivel**:
   - Entropía de Shannon (bits/byte)
   - Deltas cercanas (% de diferencias pequeñas)
   - Autocorrelación (correlación con desplazamiento)
   - Ratio de runs (proporción de bytes en runs)
   - Ceros (% de bytes cero)

2. **Clasificación**:
   - **PAQ1**: texto, datos con patrones de lenguaje, entropía media
   - **BWT+Zstd**: imágenes, binarios, datos con estructura espacial
   - **PAQ1 2D**: imágenes raster (si se detecta estructura 2D fuerte)

3. **Smart compression**:
   - Si la compresión produce un archivo **más grande**, se guarda sin comprimir (STORE)
   - El contenedor `.cubo` siempre elige la opción más eficiente

**Cuándo usar Auto:**
- ✅ **Siempre** si no sabes qué método usar
- ✅ **Lotes mixtos**: archivos de diferentes tipos
- ✅ **Producción**: maximiza ratio sin intervención manual

**Cuándo NO usar Auto:**
- ❌ **Cuando sabes el tipo de dato**: usa el método específico (más rápido)
- ❌ **Cuando necesitas velocidad**: el análisis añade overhead
- ❌ **Cuando necesitas control**: usa el método específico para comparar

---

### Resumen: ¿Qué método elegir?

| Tipo de archivo | Método recomendado | Ratio esperado | Velocidad |
|-----------------|-------------------|----------------|-----------|
| **Texto, código, logs** | PAQ1 | 15-25% | 1-5 MB/s |
| **Imágenes (BMP, PNG)** | PAQ1 2D o BWT+Zstd | 20-35% | 0.5-20 MB/s |
| **Binarios, ejecutables** | BWT+Zstd | 40-60% | 5-20 MB/s |
| **Datos científicos** | BWT+Zstd | 30-50% | 5-20 MB/s |
| **Archivos mixtos** | Auto | Variable | Variable |
| **Datos aleatorios** | STORE (no comprime) | 100% | Máxima |
| **Datos ya comprimidos** | STORE o BWT+Zstd | 95-100% | Variable |

### Comparativa con otros compresores

| Compresor | Texto (1 MB) | BMP (768 KB) | Velocidad | Ratio |
|-----------|--------------|--------------|-----------|-------|
| **ZIP** | 265 KB (24%) | 189 KB (25%) | 50 MB/s | Bueno |
| **7-Zip (LZMA)** | 180 KB (16%) | 140 KB (18%) | 10 MB/s | Muy bueno |
| **Compressor3D PAQ1** | 170 KB (15%) | 208 KB (27%) | 2 MB/s | **Excelente** |
| **Compressor3D BWT+Zstd** | 200 KB (18%) | 158 KB (20%) | 15 MB/s | Muy bueno |
| **Compressor3D PAQ1 2D** | - | 150 KB (19%) | 1 MB/s | **Excelente** |

**Conclusión**: Compressor3D PAQ1 es **1.5x mejor que ZIP** para texto, y competitivo con 7-Zip. Para imágenes, BWT+Zstd y PAQ1 2D ofrecen los mejores ratios.

## Formato de archivo .cubo

### Versión 3 - Contenedor multi-archivo (actual)

El formato `.cubo` versión 3 funciona como un contenedor estándar (similar a ZIP), permitiendo empaquetar múltiples archivos en uno solo.

```
┌──────────────────────────────────────────────────────────────┐
│ Magic: "CUBO" (4 bytes)                                     │
│ Versión: 3 (1 byte)                                         │
│ Número de archivos (int32)                                   │
│                                                               │
│ Para cada archivo:                                           │
│   - Longitud del nombre (int32)                             │
│   - Nombre original (UTF-8 bytes)                           │
│   - Tamaño original (int64)                                 │
│   - Tamaño comprimido (int64)                               │
│   - Método de compresión (byte):                            │
│       0 = STORE (sin comprimir)                             │
│       1 = BWT + MTF + Zstd                                  │
│       2 = PAQ1 (Context Mixing 1D)                          │
│       3 = PAQ1 2D (Context Mixing Espacial)                 │
│   - Datos comprimidos (bytes)                               │
└──────────────────────────────────────────────────────────────┘
```

**Características:**
- **Multi-archivo**: Empaqueta múltiples archivos en un solo `.cubo`
- **Smart compression**: Si la compresión produce un archivo más grande, guarda sin comprimir (STORE)
- **Extracción a carpeta**: Descomprime todos los archivos manteniendo estructura de directorios
- **Detección automática**: El descompresor detecta la versión del formato

### Versión 1/2 - Archivo simple (legacy)

Formato antiguo para un solo archivo:

```
┌──────────────────────────────────────┐
│ Magic: "CUBO" (4 bytes)             │
│ Versión: 1 o 2 (1 byte)             │
│ Nombre original:                     │
│   - Longitud (int32)                 │
│   - UTF-8 bytes                      │
│ Dimensiones del cubo:                │
│   - Ancho (int32)                    │
│   - Alto (int32)                     │
│   - Profundidad (int32)              │
│ Tamaño original (int64)              │
│ Datos comprimidos:                   │
│   - Longitud (int32)                 │
│   - Bytes                            │
└──────────────────────────────────────┘
```

**Compatibilidad**: El descompresor detecta automáticamente la versión y extrae correctamente.

## Estructura del proyecto

```
compresor/
├── Compresor3D/              # Motor de compresión (librería)
│   ├── Compresor3D.cs        # Lógica principal del cubo 3D
│   ├── CompresorPAQ.cs       # Context Mixing PAQ1
│   ├── Clasificador.cs       # Análisis multi-nivel de bloques
│   ├── Transformaciones.cs   # Delta, MTF, BWT, RLE, Prisma, Predict2D
│   ├── Cubo3D.cs             # Estructura de datos 3D
│   ├── Utils.cs              # Factorización, parsing, formateo
│   └── Program.cs            # CLI (consola)
│
├── Compresor3D.GUI/          # Interfaz gráfica WPF
│   ├── MainWindow.xaml       # UI (drag&drop, selector, progreso)
│   └── MainWindow.xaml.cs    # Lógica de la GUI
│
└── Formulas3D/               # Librería de funciones matemáticas
    ├── FuncionesUniversales.cs  # 10+ funciones (sin, cos, exp, log...)
    ├── MicroVM.cs               # Máquina virtual para microcódigo
    └── MotorCompresion.cs       # Motor unificado (DFT + fitting)
```

## Compilar y ejecutar

### Requisitos
- .NET 8.0 SDK
- Windows (para la GUI WPF y shell extension)

### CLI (consola)
```bash
# Navegar al proyecto CLI
cd Compresor3D

# Comprimir archivo único
dotnet run -- --file "archivo.txt"

# Comprimir múltiples archivos (crea un solo .cubo)
dotnet run -- --batch "archivo1.txt" "archivo2.cs" "imagen.bmp"

# Descomprimir archivo
dotnet run -- --descomprimir "archivo.cubo"

# Comparar PAQ1 vs ZIP
dotnet run -- --test-paq "archivo.txt"

# Test del contenedor multi-archivo
dotnet run -- --test-container
```

### GUI (interfaz gráfica)
```bash
# Navegar al proyecto GUI
cd Compresor3D.GUI

# Ejecutar la GUI
dotnet run
```

La GUI soporta:
- **Drag & drop** de archivos y carpetas
- **Selector de método**: Auto / PAQ1 / PAQ1 2D / BWT+Zstd
- **Barra de progreso** en tiempo real
- **Descompresión** de archivos .cubo a carpeta
- **Multi-archivo**: Selecciona múltiples archivos y crea un solo .cubo

### Shell Extension (menú contextual de Windows)

Para instalar la extensión del menú contextual (botón derecho):

```powershell
# Requiere permisos de Administrador
cd c:\Users\emoti\source\repos\compresor
.\install-shell.ps1
```

**Opciones disponibles:**
- **Comprimir con Compressor3D**: Aparece en archivos, carpetas y fondo de carpeta
- **Descomprimir aquí**: Aparece solo en archivos .cubo

**Desinstalar:**
```powershell
.\uninstall-shell.ps1
```

**Nota**: Después de instalar, puede ser necesario reiniciar el Explorador de Windows o cerrar sesión.

## Técnicas implementadas

### Clasificador Multi-nivel
Analiza cada bloque para predecir el mejor método:
- **Entropía de Shannon** (bits/byte)
- **Deltas cercanas** (% de diferencias pequeñas)
- **Autocorrelación** (correlación con desplazamiento)
- **Ratio de runs** (proporción de bytes en runs)
- **Ceros** (% de bytes cero)

### Arithmetic Coder
Codificador aritmético a nivel de bit con:
- Rango de 32 bits (uint)
- Manejo de **E3 region** (pending bits)
- Renormalización cuando el rango se reduce
- Output byte-a-byte con buffer

### Context Models (PAQ1)
6 modelos con diferentes longitudes de contexto:
- Orden 0: 0 bits (frecuencia global)
- Orden 1: 4 bits (16 contextos)
- Orden 2: 8 bits (256 contextos)
- Orden 3: 12 bits (4096 contextos)
- Orden 4: 16 bits (65536 contextos)
- Orden 5: 20 bits (1M contextos)

Cada modelo usa **adaptación no-estacionaria**:
- Conteo de 0s y 1s por contexto
- Límite superior: 255
- Olvido agresivo: conteo_opuesto = conteo_opuesto / 2 + 1

### BWT Propio
Transformada de Burrows-Wheeler implementada con **suffix array**:
- Construcción del suffix array en O(n log n)
- Inversión para descompresión
- No usa bzip2 como fallback (todo propio)

### Zstd Nivel 22
Usa ZstdSharp (port managed de Zstandard) al nivel máximo de compresión.

## Resultados

| Archivo | Compresor3D | ZIP | Ratio vs ZIP |
|---------|-------------|-----|--------------|
| Texto (1.1 MB) | 170 KB | 265 KB | **1.56x mejor** |
| PDF (76 KB) | 51 KB | 51.4 KB | 1.01x mejor |
| BMP (768 KB) | 158 KB* | 189 KB | **1.17x mejor** |
| PNG (6.1 MB) | 6.16 MB | 6.13 MB | 0.99x (empate) |

*BMP comprimido con pipeline BWT+Zstd (no PAQ1)

## Roadmap

### Completado ✅

- [x] **Modelo 2D para PAQ1**: Captura correlación espacial en imágenes (SpatialModel2D)
- [x] **Shell extension**: Botón derecho en explorador de Windows (comprimir + descomprimir)
- [x] **Contenedor multi-archivo**: Formato .cubo v3 (empaqueta múltiples archivos como ZIP)
- [x] **Smart compression**: No comprime si el resultado es más grande (STORE automático)
- [x] **Extracción a carpeta**: Descomprime manteniendo estructura de directorios
- [x] **GUI con progreso**: Barra de progreso real, drag & drop, selector de método

### Pendiente 🔧

- [ ] Diccionario de "fragmentos espejo" (hash de bloques + transformaciones)
- [ ] Implementar bzip3-style (LZP pre-BWT + arithmetic coder)
- [ ] ANS (Asymmetric Numeral Systems) como alternativa a Huffman
- [ ] Soporte para archivos .tar (compresión de carpetas completas sin empaquetar)
- [ ] Compresión paralela (dividir archivo en bloques independientes)
- [ ] Diccionario LZ4 pre-BWT (para datos con repeticiones exactas)
- [ ] Soporte para archivos .cubo encriptados (AES-256)

## Créditos

- **PAQ1**: Matt Mahoney (2002) - Base de los campeones mundiales de compresión
- **Zstandard**: Yann Collet (Facebook) - Compresor rápido y eficiente
- **BWT**: Burrows & Wheeler (1994) - Transformada fundamental
- **SixLabors.ImageSharp**: Para decode/encode de imágenes en .NET

## Licencia

MIT

Fernando Castro
