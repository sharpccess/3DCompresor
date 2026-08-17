# Compressor3D

Compresor de archivos basado en reordenamiento 3D, Context Mixing (PAQ1) y transformadas de entropía.

## ¿Cómo funciona?

Compressor3D trata los datos de un archivo como un **cubo 3D** y explota las correlaciones espaciales en las tres direcciones (X, Y, Z) para encontrar repeticiones. Combinado con técnicas avanzadas de compresión, logra ratios competitivos frente a ZIP.

### Arquitectura

```
┌─────────────────────────────────────────────────────────┐
│                    Archivo original                      │
└─────────────────────────┬───────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────────────────────┐
│              Clasificador Multi-nivel                    │
│  ┌──────────┬──────────┬──────────┬──────────────────┐  │
│  │ Entropía │ Deltas   │ Autocorr │ Runs/RLE         │  │
│  │ (bits/byte)│ (cercanos)│ (lag)   │ (ratio)          │  │
│  └──────────┴──────────┴──────────┴──────────────────┘  │
│  → Predice: Raw / Delta / BWT / MTF / PackBits          │
└─────────────────────────┬───────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────────────────────┐
│           Pipeline de Transformadas                      │
│                                                          │
│  Prueba N combinaciones y elige la que más reduce        │
│  la entropía (Shannon):                                  │
│                                                          │
│  • Delta: d[i] = data[i] - data[i-1]                    │
│  • MTF (Move-to-Front): reindexado por frecuencia       │
│  • BWT (Burrows-Wheeler): reordena por contexto         │
│  • Prisma Virtual: descomposición en bit-planes         │
│  • Predict2D: filtro predictivo Paeth (estilo PNG)      │
│  • RLE (Run-Length Encoding): comprime runs             │
└─────────────────────────┬───────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────────────────────┐
│              Multi-segmento Adaptativo                   │
│                                                          │
│  Divide el archivo en segmentos de 1024 bytes.           │
│  Cada segmento elige la mejor transformación +           │
│  dirección de escaneo 3D.                                │
└─────────────────────────┬───────────────────────────────┘
                          │
                          ▼
┌─────────────────────────────────────────────────────────┐
│           Compresión Final                               │
│                                                          │
│  ┌─────────────────┐    ┌──────────────────────────┐    │
│  │  PAQ1 (texto)   │    │  BWT + MTF + Zstd (img) │    │
│  │  Context Mixing │    │  Transformadas + Zstd    │    │
│  │  6 modelos      │    │  Nivel 22                │    │
│  │  Arithmetic Coder│   │                          │    │
│  └─────────────────┘    └──────────────────────────┘    │
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

**Resultados vs ZIP:**

| Tipo | PAQ1 | ZIP | Ganador |
|------|------|-----|---------|
| Texto (1.1 MB) | 170 KB (15.6%) | 265 KB (24.4%) | **PAQ1 gana 1.56x** |
| PDF (76 KB) | 51 KB (67.2%) | 51.4 KB (67.6%) | PAQ1 gana 1.01x |
| BMP (768 KB) | 208 KB (27.0%) | 110 KB (14.3%) | ZIP gana |

### 2. BWT + MTF + Zstd (mejor para imágenes)

Pipeline de transformadas seguido de compresión Zstandard nivel 22.

**Transformadas disponibles:**
- **BWT (Burrows-Wheeler Transform)**: Reordena los datos para agrupar contextos similares. Implementado con suffix array propio.
- **MTF (Move-to-Front)**: Reindexa los datos por frecuencia local, reduciendo el alfabeto efectivo.
- **Delta encoding**: Codifica diferencias entre bytes consecutivos.
- **Prisma Virtual**: Descompone bytes en bit-planes (bit 0, bit 1, ..., bit 7) para separar información de alta y baja frecuencia.
- **Predict2D**: Filtro predictivo 2D (Paeth) que trata los datos como imagen.

**Multi-segmento adaptativo:**
- Divide el archivo en segmentos de 1024 bytes
- Para cada segmento, prueba todas las combinaciones de transformadas
- Elige la que produce menor entropía de Shannon
- Cada segmento puede usar una combinación diferente

### 3. Auto (recomendado)

El clasificador analiza el archivo y elige automáticamente:
- **PAQ1** para texto y datos con patrones de lenguaje
- **BWT+Zstd** para imágenes y datos binarios

## Formato de archivo .cubo

```
┌──────────────────────────────────────┐
│ Magic: "CUBO" (4 bytes)             │
│ Versión: 2 (1 byte)                  │
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
- Windows (para la GUI WPF)

### CLI (consola)
```bash
cd Compresor3D
dotnet run -- --file "archivo.txt"
dotnet run -- --descomprimir "archivo.cubo"
dotnet run -- --test-paq "archivo.txt"   # Comparar PAQ1 vs ZIP
```

### GUI (interfaz gráfica)
```bash
cd Compresor3D.GUI
dotnet run
```

La GUI soporta:
- **Drag & drop** de archivos y carpetas
- **Selector de método**: Auto / PAQ1 / BWT+Zstd
- **Barra de progreso** en tiempo real
- **Descompresión** de archivos .cubo

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

- [ ] Modelo 2D para PAQ1 (capturar correlación espacial en imágenes)
- [ ] Shell extension (botón derecho en explorador de Windows)
- [ ] Soporte para archivos .tar (compresión de carpetas completas)
- [ ] Diccionario de "fragmentos espejo" (hash de bloques + transformaciones)
- [ ] Implementar bzip3-style (LZP pre-BWT + arithmetic coder)
- [ ] ANS (Asymmetric Numeral Systems) como alternativa a Huffman

## Créditos

- **PAQ1**: Matt Mahoney (2002) - Base de los campeones mundiales de compresión
- **Zstandard**: Yann Collet (Facebook) - Compresor rápido y eficiente
- **BWT**: Burrows & Wheeler (1994) - Transformada fundamental
- **SixLabors.ImageSharp**: Para decode/encode de imágenes en .NET

## Licencia

MIT

Fernando Castro
