using System.Collections.Generic;
using UnityEngine;

namespace DigitalTwin.MR
{
    /// <summary>
    /// Registro espacial del modelo BIM sobre el edificio real por PARES DE PUNTOS
    /// CORRESPONDIENTES: cada par es un punto del modelo (en coordenadas locales de la raíz del
    /// modelo, invariantes a dónde esté colocada la raíz) y el punto físico donde el operario
    /// ha señalado que está ese mismo punto (en coordenadas de mundo, que en modo anclado
    /// coinciden con el espacio de seguimiento del visor).
    ///
    /// EL MÉTODO. Es el problema de la orientación absoluta: hallar la transformación rígida que
    /// mejor lleva un conjunto de puntos sobre otro por mínimos cuadrados. Su solución general en
    /// forma cerrada es clásica (Horn 1987 con cuaterniones; Arun, Huang y Blostein 1987 con la
    /// descomposición en valores singulares; Umeyama 1991 con la corrección del caso
    /// degenerado). Aquí se resuelve la VARIANTE RESTRINGIDA que este sistema necesita: el
    /// edificio no puede inclinarse, así que la rotación se limita al giro alrededor de la
    /// vertical (guiñada) y las incógnitas son cuatro —traslación 3D y un ángulo— en lugar de
    /// seis. La restricción no es una simplificación cómoda sino física: los puntos se toman a
    /// nivel de suelo con ruido vertical, y una rotación libre absorbería ese ruido inclinando
    /// el modelo entero. Con guiñada + traslación el ajuste sigue teniendo forma cerrada:
    ///
    ///   · centroides p̄ (modelo) y q̄ (físico);  a_i = p_i − p̄,  b_i = q_i − q̄ (solo x, z)
    ///   · θ = atan2( Σ (a_i.z·b_i.x − a_i.x·b_i.z),  Σ (a_i.x·b_i.x + a_i.z·b_i.z) )
    ///     — maximiza Σ b_i·R(θ)a_i, es decir, minimiza Σ|R(θ)a_i − b_i|² —
    ///   · t = q̄ − R(θ)·p̄   (la componente vertical de t queda determinada por la media de las
    ///     diferencias de altura, por eso los puntos deben ser suelo con suelo)
    ///
    /// con la convención de Unity para R(θ) (giro alrededor de +Y: x' = x·cosθ + z·sinθ,
    /// z' = −x·sinθ + z·cosθ, comprobada contra Quaternion.AngleAxis).
    ///
    /// GRADOS DE LIBERTAD Y DEGRADACIÓN. Con N correspondencias hay 3N ecuaciones y 4
    /// incógnitas: con N=1 el sistema es indeterminado (la guiñada no se mide: se conserva la
    /// que tenga el modelo y así se declara), con N=2 queda determinado Y sobra información
    /// (2 grados de libertad de residuo: las distancias entre los dos puntos y su diferencia de
    /// altura tienen que coincidir en modelo y realidad), y con N≥3 hay redundancia. Por eso el
    /// sistema exige DOS puntos como mínimo para dar por registrado el modelo y recomienda TRES:
    /// el tercero no cambia el número de incógnitas, cambia la capacidad del sistema de detectar
    /// un punto mal tomado.
    ///
    /// LA MÉTRICA Y LO QUE NO MIDE. Se calcula el residuo de cada punto y su RMS (lo que la
    /// bibliografía de registro llama FRE, error de registro de los fiduciales). Fitzpatrick,
    /// West y Maurer (1998) demostraron que el FRE es un indicador POCO FIABLE del error en el
    /// punto que de verdad importa (TRE, error de registro en el objetivo): un residuo pequeño
    /// no garantiza una superposición buena lejos de los puntos medidos, y el TRE depende del
    /// número de puntos, de su dispersión y de la distancia del objetivo a su centroide. Por eso
    /// aquí se acompaña el residuo de una ESTIMACIÓN del error esperado en el punto del edificio
    /// más alejado del centroide de las correspondencias, derivada para este modelo de guiñada +
    /// traslación (ver <see cref="EstimarErrorEnObjetivo"/>), y por eso la interfaz manda tomar
    /// los puntos MUY SEPARADOS y NO ALINEADOS, que es la regla de colocación de fiduciales de
    /// West et al. (2001).
    ///
    /// Referencias completas en referencias.bib de la memoria (horn1987, arun1987, umeyama1991,
    /// fitzpatrick1998tre, west2001fiducial). Clase sin dependencias de escena para poder
    /// razonarse y verificarse aparte del visor.
    /// </summary>
    public sealed class MRRegistroPorPuntos
    {
        /// <summary>Una correspondencia: qué elemento del modelo, dónde está en el modelo (local
        /// a la raíz) y dónde ha dicho el operario que está en la realidad (mundo).</summary>
        public sealed class Correspondencia
        {
            public string Etiqueta;
            public string GlobalId;
            public Vector3 PuntoModeloLocal;
            public Vector3 PuntoFisicoMundo;
            /// <summary>Residuo del último ajuste (mundo, metros); cero hasta que se resuelve.</summary>
            public Vector3 Residuo;
            public float ErrorMetros => Residuo.magnitude;
        }

        public sealed class Resultado
        {
            public int NumeroDePuntos;
            /// <summary>Guiñada a aplicar al modelo (grados, alrededor de +Y) y traslación (mundo).</summary>
            public float GiroGrados;
            public Vector3 Traslacion;
            public Quaternion Giro => Quaternion.AngleAxis(GiroGrados, Vector3.up);
            /// <summary>Verdadero cuando la guiñada se ha MEDIDO (N ≥ 2); con un punto se conserva
            /// la del modelo y esta bandera lo delata.</summary>
            public bool GiroMedido;
            /// <summary>Residuo RMS (FRE) y máximo, en metros, sobre las correspondencias.</summary>
            public float ResiduoRms;
            public float ResiduoMaximo;
            public int IndicePeor = -1;
            /// <summary>Grados de libertad sobrantes: 3N − 4 (0 con un punto, que no da residuo).</summary>
            public int GradosDeLibertad;
        }

        private readonly List<Correspondencia> _puntos = new List<Correspondencia>();
        public IReadOnlyList<Correspondencia> Puntos => _puntos;
        public int Cuenta => _puntos.Count;

        public void Anadir(Correspondencia c) => _puntos.Add(c);
        public void QuitarUltimo() { if (_puntos.Count > 0) _puntos.RemoveAt(_puntos.Count - 1); }
        public void Vaciar() => _puntos.Clear();

        /// <summary>
        /// Resuelve el ajuste contra la colocación ACTUAL de la raíz: los puntos del modelo se
        /// llevan a mundo con <paramref name="raiz"/> y se obtiene el movimiento rígido
        /// incremental (giro alrededor de la vertical + traslación) que hay que aplicar a esa
        /// raíz. Rellena los residuos de cada correspondencia. Devuelve null sin puntos.
        /// </summary>
        public Resultado Resolver(Transform raiz)
        {
            int n = _puntos.Count;
            if (n == 0) return null;

            var p = new Vector3[n];
            var q = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                p[i] = raiz.TransformPoint(_puntos[i].PuntoModeloLocal);
                q[i] = _puntos[i].PuntoFisicoMundo;
            }

            var res = new Resultado { NumeroDePuntos = n, GradosDeLibertad = Mathf.Max(0, 3 * n - 4) };

            AjustarGiroHorizontal(p, q, out res.GiroGrados, out res.Traslacion, out res.GiroMedido);

            Quaternion giro = res.Giro;
            float suma = 0f;
            for (int i = 0; i < n; i++)
            {
                Vector3 r = giro * p[i] + res.Traslacion - q[i];
                _puntos[i].Residuo = r;
                float e2 = r.sqrMagnitude;
                suma += e2;
                if (Mathf.Sqrt(e2) > res.ResiduoMaximo) { res.ResiduoMaximo = Mathf.Sqrt(e2); res.IndicePeor = i; }
            }
            res.ResiduoRms = Mathf.Sqrt(suma / n);
            return res;
        }

        /// <summary>
        /// Forma cerrada del ajuste con giro solo horizontal (ver cabecera). Con un único punto
        /// el giro no se mide: se devuelve 0 (conservar el actual) y <paramref name="giroMedido"/>
        /// = falso. También se degrada así si todos los puntos del modelo son coincidentes en
        /// planta (no habría brazo de palanca que oriente).
        /// </summary>
        public static void AjustarGiroHorizontal(IList<Vector3> modelo, IList<Vector3> fisico,
                                                 out float giroGrados, out Vector3 traslacion,
                                                 out bool giroMedido)
        {
            int n = Mathf.Min(modelo.Count, fisico.Count);
            Vector3 pm = Vector3.zero, qm = Vector3.zero;
            for (int i = 0; i < n; i++) { pm += modelo[i]; qm += fisico[i]; }
            pm /= n; qm /= n;

            float sDot = 0f, sCross = 0f;
            for (int i = 0; i < n; i++)
            {
                Vector3 a = modelo[i] - pm;
                Vector3 b = fisico[i] - qm;
                sDot += a.x * b.x + a.z * b.z;
                sCross += a.z * b.x - a.x * b.z;
            }

            // Sin brazo de palanca (un punto, o puntos coincidentes en planta) el giro es
            // indeterminado: se conserva el que haya y se declara.
            giroMedido = n >= 2 && (sDot * sDot + sCross * sCross) > 1e-8f;
            giroGrados = giroMedido ? Mathf.Atan2(sCross, sDot) * Mathf.Rad2Deg : 0f;

            Quaternion giro = Quaternion.AngleAxis(giroGrados, Vector3.up);
            traslacion = qm - giro * pm;
        }

        /// <summary>
        /// Estimación del error esperado en un OBJETIVO situado a distancia horizontal
        /// <paramref name="distanciaAlCentroide"/> del centroide de las correspondencias, a partir
        /// del residuo del ajuste. Sigue el razonamiento de Fitzpatrick, West y Maurer (1998),
        /// rehecho para el modelo de guiñada + traslación en planta:
        ///
        ///   · con N puntos y error de localización isótropo σ por eje, el ajuste en planta tiene
        ///     2N ecuaciones y 3 incógnitas, así que E[Σ|r_i|²] ≈ (2N − 3)·σ² → σ² ≈ Σ|r_i|²/(2N−3);
        ///   · la varianza del giro estimado es σ²/(N·f²) con f² = distancia cuadrática media de
        ///     los puntos a su centroide (a más dispersión, mejor giro), y la de la traslación
        ///     σ²/N por eje;
        ///   · el error en un objetivo a distancia d del centroide combina ambas:
        ///     E[TRE²] ≈ (2σ²/N)·(1 + d²/(2f²)).
        ///
        /// Es una estimación estadística, no una medida: sirve para que el operario vea que un
        /// residuo pequeño con los puntos muy juntos NO promete precisión en la sala de enfrente,
        /// y para dejar en el registro un orden de magnitud comparable entre sesiones. Devuelve
        /// −1 si no hay grados de libertad para estimar σ (N < 2).
        /// </summary>
        public float EstimarErrorEnObjetivo(Transform raiz, Vector3 objetivoMundo)
        {
            int n = _puntos.Count;
            if (n < 2) return -1f;

            float sumaR2 = 0f;
            Vector3 centro = Vector3.zero;
            var pw = new Vector3[n];
            for (int i = 0; i < n; i++)
            {
                sumaR2 += _puntos[i].Residuo.x * _puntos[i].Residuo.x
                        + _puntos[i].Residuo.z * _puntos[i].Residuo.z;
                pw[i] = _puntos[i].PuntoFisicoMundo;
                centro += pw[i];
            }
            centro /= n;

            float f2 = 0f;
            for (int i = 0; i < n; i++)
            {
                float dx = pw[i].x - centro.x, dz = pw[i].z - centro.z;
                f2 += dx * dx + dz * dz;
            }
            f2 /= n;
            if (f2 < 1e-6f) return -1f;

            int dof = 2 * n - 3;
            float sigma2 = sumaR2 / dof;
            // Con dos puntos hay un solo grado de libertad y la estimación es tosca; se aplica
            // un suelo de 2 cm por eje para no anunciar un error irrealmente bajo cuando el
            // residuo, por azar, salga casi nulo.
            const float SigmaMinima = 0.02f;
            sigma2 = Mathf.Max(sigma2, SigmaMinima * SigmaMinima);

            float dx2 = objetivoMundo.x - centro.x, dz2 = objetivoMundo.z - centro.z;
            float d2 = dx2 * dx2 + dz2 * dz2;
            return Mathf.Sqrt(2f * sigma2 / n * (1f + d2 / (2f * f2)));
        }

        /// <summary>Distancia horizontal cuadrática media de los puntos físicos a su centroide,
        /// solo para el registro (dispersión de la configuración).</summary>
        public float DispersionHorizontal()
        {
            int n = _puntos.Count;
            if (n == 0) return 0f;
            Vector3 c = Vector3.zero;
            foreach (var p in _puntos) c += p.PuntoFisicoMundo;
            c /= n;
            float f2 = 0f;
            foreach (var p in _puntos)
            {
                float dx = p.PuntoFisicoMundo.x - c.x, dz = p.PuntoFisicoMundo.z - c.z;
                f2 += dx * dx + dz * dz;
            }
            return Mathf.Sqrt(f2 / n);
        }
    }
}
