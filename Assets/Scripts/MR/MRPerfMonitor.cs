using UnityEngine;

namespace DigitalTwin.MR
{
    /// <summary>
    /// Medición de rendimiento de la versión de Realidad Aumentada, DESDE EL PRIMER FOTOGRAMA.
    ///
    /// POR QUÉ EXISTE. En la prueba del 2026-08-14 el tiempo de fotograma derivó de 32 a 53 ms
    /// en veinte segundos, y la única medición disponible —la del controlador de interacción—
    /// no arrancaba hasta que el gemelo estaba montado: la fase del selector de modo, donde el
    /// usuario también nota tirones, quedaba sin números. Este monitor nace en la etapa A del
    /// arranque, antes que el selector, y vuelca cada diez segundos una línea que discrimina
    /// entre las causas posibles de una deriva:
    ///
    ///   · el tiempo de fotograma sube Y la memoria gestionada sube → algo acumula (fuga);
    ///   · el fotograma sube, la memoria está plana y los GC no crecen → el coste por fotograma
    ///     crece (más objetos activos… o el dispositivo bajando frecuencias por temperatura,
    ///     que en un visor autónomo es lo esperable tras minutos de uso);
    ///   · targetsUI crece sin parar → un consumidor registra pulsables sin liberarlos.
    ///
    /// La fase acompaña a cada línea («selector», «montado (navegacion)»…): así el registro
    /// dice no solo cuánto tarda un fotograma sino EN QUÉ ESTADO estaba la aplicación, y la
    /// deriva se puede correlacionar con el montaje del gemelo en vez de suponerse.
    ///
    /// La selección no se mide aquí: la mide quien la ejecuta (<see cref="MRInteractionController"/>,
    /// con su cronómetro alrededor de su propio trazado) y la reporta con
    /// <see cref="ReportarSeleccion"/>. Este monitor solo agrega y vuelca, para que exista UNA
    /// línea [Perf] por ventana y no dos series que haya que casar a mano.
    ///
    /// Todas las líneas salen por LogWarning: regla de la casa, lo que no es Warning no llega
    /// al logcat filtrado de una build de distribución.
    /// </summary>
    public class MRPerfMonitor : MonoBehaviour
    {
        private const float SegundosEntreInformes = 10f;

        /// <summary>Media móvil exponencial: un pico aislado no debe leerse como estado.</summary>
        private const float Alfa = 0.05f;

        public static MRPerfMonitor Instancia { get; private set; }

        private string _fase = "arranque";
        private float _mediaMsFotograma;
        private float _mediaMsSeleccion;
        private float _mediaImpactos;
        private bool _haySeleccion;
        private int _targetsUI;

        private float _proximoInforme;
        private long _memoriaInformeAnterior;
        private int _gc0InformeAnterior;

        /// <summary>
        /// Crea el monitor si no existe. Se llama en la etapa A del arranque, antes de mostrar
        /// el selector de modo, precisamente para que la fase del selector quede medida.
        /// </summary>
        public static MRPerfMonitor Crear()
        {
            if (Instancia != null) return Instancia;
            var go = new GameObject("~PerfMonitorAR");
            DontDestroyOnLoad(go);
            Instancia = go.AddComponent<MRPerfMonitor>();
            Instancia._proximoInforme = Time.unscaledTime + SegundosEntreInformes;
            Instancia._memoriaInformeAnterior = System.GC.GetTotalMemory(false);
            Instancia._gc0InformeAnterior = System.GC.CollectionCount(0);
            Debug.LogWarning("[DigitalTwin][AR][Perf] Monitor de rendimiento activo desde el " +
                             "primer fotograma (informe cada " + SegundosEntreInformes + " s).");
            return Instancia;
        }

        /// <summary>Cambio de fase de la aplicación («selector», «montado (anclado)»…). Deja
        /// traza propia para poder alinear las líneas [Perf] con el estado.</summary>
        public void FijarFase(string fase)
        {
            _fase = fase;
            Debug.LogWarning($"[DigitalTwin][AR][Perf] fase → {fase} (t={Time.unscaledTime:0.0} s).");
        }

        /// <summary>Coste de la selección de este fotograma, medido por quien la ejecuta.
        /// Estático y tolerante a monitor ausente: quien mide no debe fallar por reportar.</summary>
        public static void ReportarSeleccion(float msSeleccion, int impactos)
        {
            var m = Instancia;
            if (m == null) return;
            m._mediaMsSeleccion = m._haySeleccion
                ? Mathf.Lerp(m._mediaMsSeleccion, msSeleccion, Alfa) : msSeleccion;
            m._mediaImpactos = m._haySeleccion
                ? Mathf.Lerp(m._mediaImpactos, impactos, Alfa) : impactos;
            m._haySeleccion = true;
        }

        private void Update()
        {
            float ms = Time.unscaledDeltaTime * 1000f;
            _mediaMsFotograma = _mediaMsFotograma <= 0f ? ms : Mathf.Lerp(_mediaMsFotograma, ms, Alfa);

            if (Time.unscaledTime < _proximoInforme) return;
            _proximoInforme = Time.unscaledTime + SegundosEntreInformes;

            _targetsUI = DigitalTwin.UI.ClickRouter.NumTargetsRegistrados;
            long memoria = System.GC.GetTotalMemory(false);
            int gc0 = System.GC.CollectionCount(0);
            float memoriaMB = memoria / (1024f * 1024f);
            float deltaMB = (memoria - _memoriaInformeAnterior) / (1024f * 1024f);
            int deltaGc0 = gc0 - _gc0InformeAnterior;
            _memoriaInformeAnterior = memoria;
            _gc0InformeAnterior = gc0;

            float fps = _mediaMsFotograma > 0.01f ? 1000f / _mediaMsFotograma : 0f;
            string seleccion = _haySeleccion
                ? $"{_mediaMsSeleccion:0.00} ms ({_mediaImpactos:0.0} impactos)"
                : "sin medir (controlador aun no activo)";

            Debug.LogWarning($"[DigitalTwin][AR][Perf] fase={_fase}; t={Time.unscaledTime:0.0} s; " +
                             $"fotograma medio {_mediaMsFotograma:0.0} ms ({fps:0} fps); " +
                             $"seleccion media {seleccion}; targetsUI={_targetsUI}; " +
                             $"memoria gestionada {memoriaMB:0.0} MB ({(deltaMB >= 0 ? "+" : "")}{deltaMB:0.0} MB); " +
                             $"GC0 {(deltaGc0 >= 0 ? "+" : "")}{deltaGc0}.");
        }
    }
}
