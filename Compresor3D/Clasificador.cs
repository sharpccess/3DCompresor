using System;
using System.Linq;

namespace Compresor3D;

/// <summary>
/// Clasificador multi-nivel de bloques de datos.
/// 
/// La entropía es RELATIVA al nivel de observación:
/// - Nivel 0: bytes crudos → distribución marginal
/// - Nivel 1: deltas (diferencias consecutivas) → estructura local
/// - Nivel 2: autocorrelación → periodicidad oculta
/// - Nivel 3: sparse/runs → estructura posicional
/// 
/// Datos que parecen "alta entropía" en el nivel 0 pueden ser
/// perfectamente predecibles en los niveles 1-3.
/// </summary>
public static class Clasificador
{
    public enum Metodo
    {
        Raw,        // datos aleatorios, no comprimibles
        PackBits,   // muchos runs de bytes iguales
        Dedup,      // líneas repetidas
        LZ77,       // patrones repetidos a distancias variables
        MicroVM,    // estructura algebraica (XOR, LCG, geométrica)
        Funciones   // estructura funcional (constante, lineal, sinusoidal, cuadrática)
    }

    public struct ResultadoClasificacion
    {
        public Metodo metodo;
        public double confianza;        // 0-1, qué tan claro es el ganador
        public double[] scores;          // score por cada método (mayor = mejor)
        public Caracteristicas features; // features extraídas
    }

    public struct Caracteristicas
    {
        // Nivel 0: bytes crudos
        public double entropiaBytes;         // Shannon entropy (0-8 bits/byte)
        public double uniformidad;           // 0 = muy sesgada, 1 = perfectamente uniforme

        // Nivel 1: deltas (d[i+1] - d[i])
        public double entropiaDeltas;        // Shannon entropy de las diferencias
        public double ratioDeltasCercanas;   // % de deltas con |d| < 16 (datos "suaves")

        // Nivel 2: periodicidad
        public double mejorAutocorrelacion;  // max autocorrelación normalizada (0-1)
        public int mejorLag;                 // lag de la mejor autocorrelación

        // Nivel 3: runs y sparse
        public double ratioCeros;            // % de bytes = 0
        public int numRuns;                  // número de runs RLE
        public double ratioRuns;             // runs / longitud (menos = más compresible)
        public double ratioMaxRun;           // maxRun / longitud

        // Agregados
        public double ratioUnicasEstimado;   // estimación de líneas únicas (para dedup)
    }

    /// <summary>
    /// Analiza un bloque de datos en múltiples niveles de observación
    /// y determina el método de compresión más adecuado.
    /// </summary>
    public static ResultadoClasificacion Clasificar(byte[] datos)
    {
        var features = ExtraerCaracteristicas(datos);

        // ═══ SCORING: todos los métodos compiten en "tamaño estimado en bytes" ═══
        // Menor = mejor. Raw es el baseline (siempre disponible).
        // Los métodos solo "ganan" si su estimación es MENOR que Raw.
        var scores = new double[6];
        double baseline = datos.Length; // tamaño sin comprimir

        // ─── Raw: baseline = tamaño original ───
        scores[(int)Metodo.Raw] = baseline;

        // ─── PackBits: estimado = 2 bytes por run + overhead ───
        scores[(int)Metodo.PackBits] = features.numRuns * 2.0 + baseline * 0.05;

        // ─── Dedup: estimado por ratio de líneas únicas ───
        double unicas = features.ratioUnicasEstimado;
        scores[(int)Metodo.Dedup] = unicas * baseline * 0.95 + (1 - unicas) * baseline * 0.05
                                  + baseline * 0.05; // overhead del mapa de índices

        // ─── LZ77: heurística basada en entropía y runs ───
        {
            double ratio = 1.0;
            // Entropía media-baja → hay estructura para el diccionario
            if (features.entropiaBytes < 6) ratio *= 0.5 + features.entropiaBytes / 12.0;
            else ratio *= 0.85; // entropía alta → LZ77 apenas ayuda

            // Muchos runs moderados → buenos matches
            if (features.ratioRuns < 0.1) ratio *= 0.7;
            else if (features.ratioRuns > 0.5) ratio *= 1.2;

            // Deltas cercanas → estructura local aprovechable
            if (features.ratioDeltasCercanas > 0.6) ratio *= 0.8;

            // Penalización dura: entropía muy alta = incompresible
            if (features.entropiaBytes > 7.0) ratio = Math.Max(ratio, 0.95);
            if (features.entropiaBytes > 7.5) ratio = 0.98;

            scores[(int)Metodo.LZ77] = baseline * ratio;
        }

        // ─── MicroVM: heurística basada en estructura algebraica ───
        {
            double ratio = 1.0;

            // Señal principal: deltas mucho más simples que los bytes
            double deltaRatio = features.entropiaBytes > 0
                ? features.entropiaDeltas / features.entropiaBytes : 1.0;
            if (deltaRatio < 0.6) ratio *= 0.4; // deltas muy predecibles
            else if (deltaRatio < 0.8) ratio *= 0.7;
            else ratio *= 0.95; // deltas = entropía similar a bytes → poco aprovechable

            // Autocorrelación → dependencia entre bytes
            if (features.mejorAutocorrelacion > 0.5) ratio *= 0.7;
            else if (features.mejorAutocorrelacion > 0.3) ratio *= 0.85;

            // Penalización dura: entropía alta = microcódigo no encuentra patrones
            if (features.entropiaBytes > 6.5) ratio = Math.Max(ratio, 0.85);
            if (features.entropiaBytes > 7.5) ratio = Math.Max(ratio, 0.97);

            scores[(int)Metodo.MicroVM] = baseline * ratio;
        }

        // ─── Funciones: heurística basada en suavidad y periodicidad ───
        {
            double ratio = 1.0;

            // Señal principal: deltas cercanas (datos suaves = función suave)
            if (features.ratioDeltasCercanas > 0.8) ratio *= 0.3;
            else if (features.ratioDeltasCercanas > 0.6) ratio *= 0.5;
            else if (features.ratioDeltasCercanas > 0.4) ratio *= 0.7;
            else ratio *= 0.9;

            // Entropía de deltas baja → función predecible
            if (features.entropiaDeltas < 3) ratio *= 0.5;
            else if (features.entropiaDeltas < 5) ratio *= 0.7;

            // Periodicidad fuerte → sinusoidal
            if (features.mejorAutocorrelacion > 0.6) ratio *= 0.6;
            else if (features.mejorAutocorrelacion > 0.3) ratio *= 0.8;

            // Muchos ceros → función que pasa por 0
            if (features.ratioCeros > 0.3) ratio *= 0.8;

            // PENALIZACIÓN CRÍTICA: si entropía bytes Y deltas son altas,
            // no hay función que se ajuste (los datos son aleatorios)
            if (features.entropiaBytes > 6.0) ratio = Math.Max(ratio, 0.7);
            if (features.entropiaBytes > 7.0 && features.entropiaDeltas > 6.0)
                ratio = Math.Max(ratio, 0.95);
            if (features.entropiaBytes > 7.5)
                ratio = Math.Max(ratio, 0.98);

            scores[(int)Metodo.Funciones] = baseline * ratio;
        }

        // ─── Elegir ganador ───
        int mejorIdx = 0;
        double mejorScore = double.MinValue;
        for (int i = 0; i < 6; i++)
        {
            if (scores[i] > mejorScore)
            {
                mejorScore = scores[i];
                mejorIdx = i;
            }
        }

        // Confianza: diferencia entre el mejor y el segundo, normalizada
        double segundoScore = double.MinValue;
        for (int i = 0; i < 6; i++)
        {
            if (i != mejorIdx && scores[i] > segundoScore)
                segundoScore = scores[i];
        }
        double range = Math.Max(1, Math.Abs(mejorScore) + Math.Abs(segundoScore));
        double confianza = Math.Min(1.0, Math.Abs(mejorScore - segundoScore) / range);

        return new ResultadoClasificacion
        {
            metodo = (Metodo)mejorIdx,
            confianza = confianza,
            scores = scores,
            features = features
        };
    }

    /// <summary>
    /// Extrae características multi-nivel de un stream de bytes.
    /// </summary>
    static Caracteristicas ExtraerCaracteristicas(byte[] datos)
    {
        int n = datos.Length;
        if (n == 0) return default;

        var f = new Caracteristicas();

        // ═══ NIVEL 0: Distribución de bytes crudos ═══
        int[] hist = new int[256];
        for (int i = 0; i < n; i++) hist[datos[i]]++;

        double entropia = 0;
        int ceros = hist[0];
        int valoresNoCero = 0;
        for (int i = 0; i < 256; i++)
        {
            if (hist[i] > 0)
            {
                valoresNoCero++;
                double p = (double)hist[i] / n;
                entropia -= p * Math.Log2(p);
            }
        }
        f.entropiaBytes = entropia;
        f.ratioCeros = (double)ceros / n;
        // Uniformidad: 1 = perfectamente uniforme, 0 = todo en un valor
        f.uniformidad = valoresNoCero / 256.0;

        // ═══ NIVEL 1: Análisis de deltas (diferencias consecutivas) ═══
        // Si los datos son f(x) = cte → deltas = 0
        // Si son f(x) = ax+b → deltas = a (constante)
        // Si son f(x) = sin(x) → deltas = cos-like (suaves)
        // Si son random → deltas = random (alta entropía)
        if (n >= 2)
        {
            int[] deltaHist = new int[512]; // deltas en [-255, 255] → index + 255
            int deltasCercanas = 0;
            for (int i = 1; i < n; i++)
            {
                int d = datos[i] - datos[i - 1];
                deltaHist[d + 255]++;
                if (Math.Abs(d) < 16) deltasCercanas++;
            }
            double entDelta = 0;
            int dn = n - 1;
            for (int i = 0; i < 512; i++)
            {
                if (deltaHist[i] > 0)
                {
                    double p = (double)deltaHist[i] / dn;
                    entDelta -= p * Math.Log2(p);
                }
            }
            f.entropiaDeltas = entDelta;
            f.ratioDeltasCercanas = (double)deltasCercanas / dn;
        }

        // ═══ NIVEL 2: Periodicidad (autocorrelación) ═══
        // Busca el lag k donde la serie se parece más a sí misma desplazada k posiciones.
        // sinusoidal → autocorrelación alta en el periodo
        // random → autocorrelación ~0 para todo lag
        // LCG/XOR → puede tener autocorrelación en ciertos lags
        f.mejorAutocorrelacion = 0;
        f.mejorLag = 0;
        if (n >= 64)
        {
            double media = 0;
            for (int i = 0; i < n; i++) media += datos[i];
            media /= n;

            double varianza = 0;
            for (int i = 0; i < n; i++) varianza += (datos[i] - media) * (datos[i] - media);
            varianza /= n;

            if (varianza > 0.1)
            {
                int maxLag = Math.Min(n / 2, 256);
                // Muestrear cada 4 bytes para velocidad
                int step = Math.Max(1, n / 512);
                double mejorCorr = 0;
                int mejorK = 0;

                for (int k = 1; k < maxLag; k++)
                {
                    double corr = 0;
                    int count = 0;
                    for (int i = 0; i + k < n; i += step)
                    {
                        corr += (datos[i] - media) * (datos[i + k] - media);
                        count++;
                    }
                    corr = Math.Abs(corr / (count * varianza));
                    if (corr > mejorCorr)
                    {
                        mejorCorr = corr;
                        mejorK = k;
                    }
                }
                f.mejorAutocorrelacion = mejorCorr;
                f.mejorLag = mejorK;
            }
        }

        // ═══ NIVEL 3: Runs RLE y sparse ═══
        int runs = 1;
        int maxRun = 1;
        int curRun = 1;
        for (int i = 1; i < n; i++)
        {
            if (datos[i] == datos[i - 1])
            {
                curRun++;
                if (curRun > maxRun) maxRun = curRun;
            }
            else
            {
                runs++;
                curRun = 1;
            }
        }
        f.numRuns = runs;
        f.ratioRuns = (double)runs / n;
        f.ratioMaxRun = (double)maxRun / n;

        // ═══ Estimación de líneas únicas para dedup ═══
        // Si hay muchos runs largos, probablemente hay líneas repetidas
        // Si la entropía es baja, las líneas tienden a ser similares
        // Heurística: si uniformidad es baja y runs son pocos, hay líneas repetidas
        if (n >= 64)
        {
            // Muestrear líneas de longitud sqrt(n) y ver cuántas son únicas
            int lineLen = Math.Max(8, (int)Math.Sqrt(n));
            int totalLines = n / lineLen;
            if (totalLines >= 2)
            {
                var uniques = new System.Collections.Generic.HashSet<string>();
                for (int li = 0; li < totalLines; li++)
                {
                    string key = System.Text.Encoding.Latin1.GetString(datos, li * lineLen, lineLen);
                    uniques.Add(key);
                }
                f.ratioUnicasEstimado = (double)uniques.Count / totalLines;
            }
            else
            {
                f.ratioUnicasEstimado = 1.0; // no se puede estimar
            }
        }
        else
        {
            f.ratioUnicasEstimado = 1.0;
        }

        return f;
    }

    /// <summary>Devuelve el nombre legible del método.</summary>
    public static string NombreMetodo(Metodo m) => m switch
    {
        Metodo.Raw => "Raw (sin comprimir)",
        Metodo.PackBits => "PackBits (RLE)",
        Metodo.Dedup => "Dedup (deduplicación de líneas)",
        Metodo.LZ77 => "LZ77 (diccionario deslizante)",
        Metodo.MicroVM => "MicroVM (microcódigo ejecutable)",
        Metodo.Funciones => "Funciones (descomposición funcional)",
        _ => "?"
    };

    /// <summary>
    /// Devuelve los métodos ordenados por score (el mejor primero).
    /// Útil para tener fallbacks si el primero no comprime bien.
    /// </summary>
    public static Metodo[] Ranking(ResultadoClasificacion r)
    {
        var ranking = new Metodo[6];
        bool[] usado = new bool[6];
        for (int i = 0; i < 6; i++)
        {
            int mejorIdx = -1;
            double mejorScore = double.MinValue;
            for (int j = 0; j < 6; j++)
            {
                if (!usado[j] && r.scores[j] > mejorScore)
                {
                    mejorScore = r.scores[j];
                    mejorIdx = j;
                }
            }
            ranking[i] = (Metodo)mejorIdx;
            usado[mejorIdx] = true;
        }
        return ranking;
    }
}
