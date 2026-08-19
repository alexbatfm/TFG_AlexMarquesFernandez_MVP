// ---------------------------------------------------------------------------------------------
//  ArnesRegistro.cs — Validacion numerica (Monte Carlo) del estimador de registro espacial.
//
//  QUE HACE. Ejerce el codigo real de MRRegistroPorPuntos (Resolver, AjustarGiroHorizontal,
//  EstimarErrorEnObjetivo y DispersionHorizontal) contra correspondencias sinteticas generadas
//  a partir de una transformacion VERDADERA conocida mas ruido gaussiano por eje. Al conocerse
//  la verdad, se puede comparar el error que el sistema ANUNCIA al operario con el error que
//  realmente comete en un punto objetivo que no participa en el ajuste.
//
//  POR QUE EXISTE. El criterio de aceptacion del registro ("≈4 cm con dos puntos, ≈3 cm con
//  tres") era una prediccion sin comprobacion. Ademas, el inmueble modelado esta en Madrid y no
//  habra sesion de campo en el, de modo que este arnes es la unica caracterizacion del estimador
//  disponible. No sustituye a una medida del sistema completo: aqui no hay seguimiento inercial,
//  ni deriva, ni error de senalamiento del operario. Caracteriza el ESTIMADOR, no la aplicacion.
//
//  POR QUE NO ES UN TEST NUnit. Ver PLAN-DIA-17ago.md §0.3, §0.4 y §4.0: el Test Framework no
//  descubre tests en los ensamblados predefinidos, todo el codigo de ejecucion vive en
//  Assembly-CSharp y Assets/link.xml lo preserva POR ESE NOMBRE. Assembly-CSharp-Editor si
//  referencia a Assembly-CSharp, asi que un script de editor llama al codigo real sin crear
//  ningun .asmdef y sin tocar una linea de Assets/Scripts.
//
//  El fichero no modifica nada del proyecto: solo lee, calcula y escribe un .csv.
// ---------------------------------------------------------------------------------------------
#if UNITY_EDITOR
using UnityEditor;
#endif
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using UnityEngine;
using DigitalTwin.MR;

namespace DigitalTwin.Arnes
{
    public static class ArnesRegistro
    {
        public const int SemillaPorDefecto = 20260819;
        public const int RepeticionesPorDefecto = 10000;

        // Transformacion verdadera: gira y desplaza el modelo. Valores arbitrarios pero fijos;
        // el estimador no puede saberlos y debe recuperarlos.
        const float GiroVerdadero = 23.7f;
        static readonly Vector3 TraslacionVerdadera = new Vector3(12.34f, 0.05f, -7.65f);

        // Las SEIS estaciones que la aplicacion planifica, literales del logcat del 17-08
        // (13:08:42.678), en coordenadas del modelo y llevadas a cota de suelo.
        static readonly Vector3[] Est = {
            new Vector3(41.5f, 0f, -43.6f), new Vector3(28.5f, 0f, -64.4f),
            new Vector3(51.2f, 0f, -50.0f), new Vector3(32.1f, 0f, -52.3f),
            new Vector3(43.0f, 0f, -52.6f), new Vector3(47.6f, 0f, -43.6f),
        };
        // Nodo 'Recibidor' de Assets/Resources/NavGraph.asset, a cota de suelo. Queda a 14,34 m
        // del centroide de la tripleta 1-2-3, que es el objetivo del guion de pruebas.
        static readonly Vector3 Recibidor = new Vector3(38.846085f, 0f, -38.407364f);
        static readonly Vector3 CentroTripleta123 = new Vector3(40.4f, 0f, -52.6667f);

        sealed class Escenario
        {
            public string Bloque, Nombre, NombreObjetivo;
            public Vector3[] Modelo;
            public Vector3 Objetivo;
            public float Sigma;
        }

        // Generador propio (xorshift32 + Box-Muller) en lugar de UnityEngine.Random: reproducible,
        // independiente del estado global del editor e identico fuera de Unity.
        sealed class Aleatorio
        {
            uint _s; double _guardado; bool _hay;
            public Aleatorio(int semilla) { _s = semilla == 0 ? 1u : (uint)semilla; }
            uint Sig() { _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5; return _s; }
            double U() { return (Sig() + 1.0) / 4294967297.0; }
            public float Normal()
            {
                if (_hay) { _hay = false; return (float)_guardado; }
                double u1 = U(), u2 = U(), r = Math.Sqrt(-2.0 * Math.Log(u1));
                _guardado = r * Math.Sin(2.0 * Math.PI * u2); _hay = true;
                return (float)(r * Math.Cos(2.0 * Math.PI * u2));
            }
        }

        static Vector3[] Sub(params int[] ids)
        { var v = new Vector3[ids.Length]; for (int i = 0; i < ids.Length; i++) v[i] = Est[ids[i] - 1]; return v; }

        /// <summary>Triangulo isosceles de base sobre +X y apice sobre +Z, con razon apice/semibase
        /// (0 = tres puntos alineados, 1,732 = equilatero), escalado para que su dispersion sea f.</summary>
        static Vector3[] Triangulo(float razon, float f)
        {
            float u = 1f, v = razon;
            float k = f / Mathf.Sqrt((2f * u * u + 2f * v * v / 3f) / 3f);
            u *= k; v *= k;
            return new[] { CentroTripleta123 + new Vector3(-u, 0f, -v / 3f),
                           CentroTripleta123 + new Vector3( u, 0f, -v / 3f),
                           CentroTripleta123 + new Vector3( 0f, 0f, 2f * v / 3f) };
        }

        static List<Escenario> Plan()
        {
            var L = new List<Escenario>();
            Action<string, string, Vector3[], Vector3, string, float> add =
                (b, n, m, o, no, s) => L.Add(new Escenario { Bloque = b, Nombre = n, Modelo = m, Objetivo = o, NombreObjetivo = no, Sigma = s });

            // A · geometria de referencia del guion, sigma = 3 cm.
            add("A", "1-2-3", Sub(1, 2, 3), Recibidor, "Recibidor", 0.03f);
            add("A", "2-3", Sub(2, 3), Recibidor, "Recibidor", 0.03f);
            add("A", "1-2", Sub(1, 2), Recibidor, "Recibidor", 0.03f);
            add("A", "1-3", Sub(1, 3), Recibidor, "Recibidor", 0.03f);
            // B · barrido de sigma.
            foreach (float s in new[] { 0.01f, 0.03f, 0.05f, 0.10f })
            {
                add("B", "1-2-3", Sub(1, 2, 3), Recibidor, "Recibidor", s);
                add("B", "2-3", Sub(2, 3), Recibidor, "Recibidor", s);
            }
            // C · tripletas replicables con cinta metrica en casa (PLAN-18-19ago §5.1).
            add("C", "1-5-6", Sub(1, 5, 6), Recibidor, "Recibidor", 0.03f);
            add("C", "1-3-6", Sub(1, 3, 6), Recibidor, "Recibidor", 0.03f);
            add("C", "1-4-5", Sub(1, 4, 5), Recibidor, "Recibidor", 0.03f);
            // C · el mismo ajuste evaluado en una estacion NO usada = el punto de comprobacion
            // que la replica metrologica va a medir con cinta.
            add("C", "1-5-6", Sub(1, 5, 6), Est[3], "Estacion 4", 0.03f);
            add("C", "1-3-6", Sub(1, 3, 6), Est[3], "Estacion 4", 0.03f);
            // D · forma del triangulo a dispersion CONSTANTE (f = 12,7 m), dos direcciones de
            // objetivo: paralela a la base y perpendicular a ella.
            foreach (float r in new[] { 0f, 0.1f, 0.25f, 0.5f, 1f, 1.732f })
            {
                var t = Triangulo(r, 12.7338f);
                add("D", "forma r=" + r.ToString("0.###", CultureInfo.InvariantCulture), t,
                    CentroTripleta123 + new Vector3(14.3437f, 0f, 0f), "d paralelo", 0.03f);
                add("D", "forma r=" + r.ToString("0.###", CultureInfo.InvariantCulture), t,
                    CentroTripleta123 + new Vector3(0f, 0f, 14.3437f), "d perpendicular", 0.03f);
            }
            // E · dispersion variable a forma CONSTANTE (equilatero).
            foreach (float f in new[] { 2.5f, 5f, 7.5f, 12.7338f })
                add("E", "equilatero f=" + f.ToString("0.0", CultureInfo.InvariantCulture), Triangulo(1.732f, f),
                    CentroTripleta123 + new Vector3(14.3437f, 0f, 0f), "d paralelo", 0.03f);
            return L;
        }

        public static string Ejecutar(int semilla, int repeticiones, string carpetaSalida)
        {
            var planes = Plan();
            var giroV = Quaternion.AngleAxis(GiroVerdadero, Vector3.up);
            var raiz = new GameObject("ArnesRegistro_Raiz").transform;   // identidad: modelo == mundo
            var sb = new StringBuilder();
            sb.Append("# ArnesRegistro — separador ';', decimales con '.', longitudes en cm salvo indicacion\n");
            sb.Append("# semilla=").Append(semilla).Append(" repeticiones=").Append(repeticiones)
              .Append(" giro_verdadero_grados=").Append(GiroVerdadero.ToString(CultureInfo.InvariantCulture)).Append('\n');
            sb.Append("bloque;escenario;objetivo;n;sigma_cm;f_medida_m;d_m;residuo_rms_media;residuo_rms_p95;")
              .Append("verdadero_media;verdadero_mediana;verdadero_p95;verdadero_rms;")
              .Append("predicho_media;predicho_mediana;predicho_p95;")
              .Append("giro_abs_media_grados;giro_abs_p95_grados;teorico_rms;")
              .Append("razon_predicho_entre_verdadero_rms;cobertura_pct;correlacion;suelo_sigma_pct\n");
            var informe = new StringBuilder();

            for (int e = 0; e < planes.Count; e++)
            {
                var esc = planes[e];
                int n = esc.Modelo.Length;
                var rnd = new Aleatorio(semilla + 7919 * e);
                float[] vRes = new float[repeticiones], vVer = new float[repeticiones],
                        vPre = new float[repeticiones], vGir = new float[repeticiones];
                double sF = 0, sSuelo = 0, sPV = 0, sP = 0, sV = 0, sPP = 0, sVV = 0, sCob = 0;
                Vector3 objVerdadero = giroV * esc.Objetivo + TraslacionVerdadera;

                for (int k = 0; k < repeticiones; k++)
                {
                    var reg = new MRRegistroPorPuntos();
                    for (int i = 0; i < n; i++)
                    {
                        Vector3 ruido = new Vector3(rnd.Normal(), rnd.Normal(), rnd.Normal()) * esc.Sigma;
                        reg.Anadir(new MRRegistroPorPuntos.Correspondencia {
                            Etiqueta = "E" + (i + 1),
                            PuntoModeloLocal = esc.Modelo[i],
                            PuntoFisicoMundo = giroV * esc.Modelo[i] + TraslacionVerdadera + ruido });
                    }
                    var res = reg.Resolver(raiz);
                    float predicho = reg.EstimarErrorEnObjetivo(raiz, objVerdadero);
                    sF += reg.DispersionHorizontal();

                    Vector3 estObj = res.Giro * esc.Objetivo + res.Traslacion;
                    float ex = estObj.x - objVerdadero.x, ez = estObj.z - objVerdadero.z;
                    float verdadero = Mathf.Sqrt(ex * ex + ez * ez);
                    float dg = res.GiroGrados - GiroVerdadero;
                    while (dg > 180f) dg -= 360f; while (dg < -180f) dg += 360f;

                    // Observado DESDE FUERA (Residuo es publico): con que frecuencia actua el
                    // suelo de 2 cm que EstimarErrorEnObjetivo impone a sigma.
                    float sr2 = 0f; foreach (var c in reg.Puntos) sr2 += c.Residuo.x * c.Residuo.x + c.Residuo.z * c.Residuo.z;
                    if (sr2 / (2 * n - 3) < 0.0004f) sSuelo += 1.0;

                    vRes[k] = res.ResiduoRms * 100f; vVer[k] = verdadero * 100f;
                    vPre[k] = predicho * 100f; vGir[k] = Mathf.Abs(dg);
                    sP += vPre[k]; sV += vVer[k]; sPV += (double)vPre[k] * vVer[k];
                    sPP += (double)vPre[k] * vPre[k]; sVV += (double)vVer[k] * vVer[k];
                    if (verdadero <= predicho) sCob += 1.0;
                }
                Array.Sort(vRes); Array.Sort(vVer); Array.Sort(vPre); Array.Sort(vGir);
                float fMed = (float)(sF / repeticiones);
                float d = Mathf.Sqrt((esc.Objetivo.x - Cx(esc.Modelo)) * (esc.Objetivo.x - Cx(esc.Modelo))
                                   + (esc.Objetivo.z - Cz(esc.Modelo)) * (esc.Objetivo.z - Cz(esc.Modelo)));
                float f2 = F2(esc.Modelo);
                float teorico = Mathf.Sqrt(2f * esc.Sigma * esc.Sigma / n * (1f + d * d / (2f * f2))) * 100f;
                double vRms = Math.Sqrt(sVV / repeticiones);
                double cov = (sPV / repeticiones - (sP / repeticiones) * (sV / repeticiones)) /
                             (Math.Sqrt(Math.Max(1e-12, sPP / repeticiones - Math.Pow(sP / repeticiones, 2))) *
                              Math.Sqrt(Math.Max(1e-12, sVV / repeticiones - Math.Pow(sV / repeticiones, 2))));

                string fila = string.Join(";", new[] {
                    esc.Bloque, esc.Nombre, esc.NombreObjetivo, n.ToString(), N(esc.Sigma * 100f), N(fMed), N(d),
                    N(Media(vRes)), N(P(vRes, 0.95)),
                    N(Media(vVer)), N(P(vVer, 0.50)), N(P(vVer, 0.95)), N((float)vRms),
                    N(Media(vPre)), N(P(vPre, 0.50)), N(P(vPre, 0.95)),
                    N(Media(vGir)), N(P(vGir, 0.95)), N(teorico),
                    N((float)(Media(vPre) / vRms)), N((float)(100.0 * sCob / repeticiones)),
                    N((float)cov), N((float)(100.0 * sSuelo / repeticiones)) });
                sb.Append(fila).Append('\n');
                informe.Append(string.Format(CultureInfo.InvariantCulture,
                    "{0} {1,-16} {2,-16} n={3} s={4,5:0.0}cm f={5,5:0.0}m d={6,5:0.0}m | RMSres {7,5:0.0} | verdadero med {8,5:0.0} rms {9,5:0.0} p95 {10,5:0.0} | predicho med {11,5:0.0} p95 {12,5:0.0} | teor {13,5:0.0} | pred/verdRMS {14,5:0.00} | cobertura {15,4:0}% | suelo {16,4:0}%\n",
                    esc.Bloque, esc.Nombre, esc.NombreObjetivo, n, esc.Sigma * 100f, fMed, d, Media(vRes),
                    Media(vVer), vRms, P(vVer, 0.95), Media(vPre), P(vPre, 0.95), teorico,
                    Media(vPre) / vRms, 100.0 * sCob / repeticiones, 100.0 * sSuelo / repeticiones));
            }
            UnityEngine.Object.DestroyImmediate(raiz.gameObject);

            Directory.CreateDirectory(carpetaSalida);
            string ruta = Path.Combine(carpetaSalida, "arnes-registro-montecarlo.csv");
            File.WriteAllText(ruta, sb.ToString(), new UTF8Encoding(false));
            Debug.Log("[ArnesRegistro] semilla " + semilla + ", " + repeticiones + " repeticiones, " +
                      planes.Count + " escenarios. Longitudes en cm.\n" + informe + "\nCSV: " + ruta);
            return ruta;
        }

        static float Cx(Vector3[] p) { float s = 0f; foreach (var v in p) s += v.x; return s / p.Length; }
        static float Cz(Vector3[] p) { float s = 0f; foreach (var v in p) s += v.z; return s / p.Length; }
        static float F2(Vector3[] p)
        { float cx = Cx(p), cz = Cz(p), s = 0f; foreach (var v in p) s += (v.x - cx) * (v.x - cx) + (v.z - cz) * (v.z - cz); return s / p.Length; }
        static float Media(float[] v) { double s = 0; foreach (var x in v) s += x; return (float)(s / v.Length); }
        static float P(float[] ord, double p) { return ord[Mathf.Clamp((int)Math.Round(p * (ord.Length - 1)), 0, ord.Length - 1)]; }
        static string N(float x) { return x.ToString("0.####", CultureInfo.InvariantCulture); }

#if UNITY_EDITOR
        [MenuItem("Tools/TFG/Comprobar/Estimador de registro (Monte Carlo)")]
        static void Menu()
        {
            // TFG/utility/ esta dos niveles por encima de la carpeta del proyecto Unity.
            string utility = Path.GetFullPath(Path.Combine(Application.dataPath, "../../../TFG/utility"));
            if (!Directory.Exists(utility))
            {
                utility = Path.GetFullPath(Path.Combine(Application.dataPath, "../ArnesSalida"));
                Debug.LogWarning("[ArnesRegistro] No encuentro TFG/utility; escribo en " + utility);
            }
            Ejecutar(SemillaPorDefecto, RepeticionesPorDefecto, utility);
        }
#endif
    }
}
