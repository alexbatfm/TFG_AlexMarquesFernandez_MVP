using System;
using System.Collections.Generic;
using System.Text;
using Stopwatch = System.Diagnostics.Stopwatch;
using Debug = UnityEngine.Debug;

namespace DigitalTwin.Core
{
    /// <summary>
    /// Una etapa del arranque, tal y como se le cuenta al usuario y tal y como pesa en la barra.
    ///
    /// El peso NO es una medida: es la porción de la barra que se le concede a la fase. Se
    /// calibra con los tiempos medidos (ver la tabla de <see cref="ProgresoDeArranque"/>) para
    /// que la barra avance a ritmo aproximadamente constante. Un peso mal estimado deforma el
    /// ritmo pero nunca los extremos: la barra sigue empezando en cero y terminando en uno.
    /// </summary>
    public struct FaseDeArranque
    {
        public string Clave;
        public string Texto;
        public float Peso;

        public FaseDeArranque(string clave, string texto, float peso)
        {
            Clave = clave;
            Texto = texto;
            Peso = peso;
        }
    }

    /// <summary>
    /// Estado del arranque: en qué paso va, cuánto queda, cuánto ha costado cada paso y —lo que
    /// de verdad importa en un visor— con qué regularidad se han ido entregando fotogramas
    /// mientras tanto.
    ///
    /// POR QUÉ ES UN MODELO APARTE DE LA PANTALLA. Las dos plataformas necesitan lo mismo (saber
    /// en qué paso va el arranque) y lo presentan distinto: en escritorio basta un lienzo sobre
    /// la pantalla; en el visor hay restricciones de confort que no existen en un monitor. Con
    /// el estado aquí, <see cref="UI.PantallaDeCargaEscritorio"/> y
    /// <c>MR.MRPantallaDeCarga</c> son dos presentaciones de un único modelo, y el arranque de
    /// cada plataforma declara sus fases sin saber quién las dibuja.
    ///
    /// POR QUÉ MIDE, Y POR QUÉ MIDE ESTO. El objetivo del trabajo no era enseñar una barra sino
    /// dejar de bloquear el bucle principal: si la aplicación deja de entregar fotogramas, el
    /// runtime de OpenXR reproyecta el último y la imagen deja de responder al movimiento de la
    /// cabeza, que es la causa directa del malestar. Por eso se registran dos cosas distintas:
    ///
    ///   · el coste de cada fase (cronómetro por fase), que dice DÓNDE está el trabajo;
    ///   · el intervalo entre fotogramas consecutivos durante el arranque —el peor, y cuántos
    ///     superan 25, 50 y 100 ms—, que dice si ese trabajo llegó a interrumpir la entrega.
    ///
    /// La segunda serie es la que responde a la pregunta que importa. Un arranque que tarde más
    /// en total pero sin ningún intervalo por encima de dos periodos de pantalla es mejor que
    /// uno más corto con una parada de 400 ms, y sin esta medida las dos cosas son
    /// indistinguibles desde el registro.
    ///
    /// El intervalo se mide con <see cref="Stopwatch"/> y no con <c>Time.unscaledDeltaTime</c>:
    /// el valor del motor pasa por sus propios ajustes de acotación y lo que aquí se quiere es
    /// el tiempo de pared entre dos vueltas del bucle, sin intermediarios.
    /// </summary>
    public static class ProgresoDeArranque
    {
        /// <summary>Se dispara en cada cambio de fase o de fracción. Las pantallas se suscriben;
        /// nadie más debería necesitarlo.</summary>
        public static event Action AlCambiar;

        private static readonly List<FaseDeArranque> _fases = new List<FaseDeArranque>();
        private static readonly List<double> _msPorFase = new List<double>();
        private static readonly List<bool> _faseVisitada = new List<bool>();

        private static int _indice = -1;
        private static float _fraccionDeFase;
        private static float _pesoTotal;
        private static string _perfil = string.Empty;

        private static readonly Stopwatch _cronoTotal = new Stopwatch();
        private static readonly Stopwatch _cronoFase = new Stopwatch();

        // --- Regularidad de la entrega de fotogramas durante el arranque -------------------
        private static long _marcaFotogramaAnterior;
        private static int _fotogramas;
        private static double _peorIntervaloMs;
        private static int _intervalosMayores25;
        private static int _intervalosMayores50;
        private static int _intervalosMayores100;

        public static bool EnCurso { get; private set; }

        public static string Perfil => _perfil;

        /// <summary>Texto del paso actual, ya redactado para el usuario.</summary>
        public static string TextoDeFase =>
            _indice >= 0 && _indice < _fases.Count ? _fases[_indice].Texto : string.Empty;

        /// <summary>Avance global en [0,1], repartiendo por pesos y usando el avance interno de
        /// la fase en curso cuando quien la ejecuta sabe medirlo.</summary>
        public static float Fraccion
        {
            get
            {
                if (_pesoTotal <= 0f || _indice < 0) return 0f;
                float acumulado = 0f;
                for (int i = 0; i < _indice && i < _fases.Count; i++) acumulado += _fases[i].Peso;
                if (_indice < _fases.Count) acumulado += _fases[_indice].Peso * Clamp01(_fraccionDeFase);
                return Clamp01(acumulado / _pesoTotal);
            }
        }

        /// <summary>Arranca un perfil de fases, descartando cualquier estado anterior.</summary>
        public static void Comenzar(string perfil, params FaseDeArranque[] fases)
        {
            _perfil = perfil ?? string.Empty;
            _fases.Clear();
            _msPorFase.Clear();
            _faseVisitada.Clear();
            _pesoTotal = 0f;
            foreach (var f in fases)
            {
                _fases.Add(f);
                _msPorFase.Add(0d);
                _faseVisitada.Add(false);
                _pesoTotal += Math.Max(0.0001f, f.Peso);
            }
            _indice = -1;
            _fraccionDeFase = 0f;
            EnCurso = true;

            _cronoTotal.Reset(); _cronoTotal.Start();
            _cronoFase.Reset();

            _marcaFotogramaAnterior = 0L;
            _fotogramas = 0;
            _peorIntervaloMs = 0d;
            _intervalosMayores25 = _intervalosMayores50 = _intervalosMayores100 = 0;

            Avisar();
        }

        /// <summary>
        /// Arranca el perfil solo si no hay otro en curso.
        ///
        /// Lo usa el arranque de la escena para no pisar un perfil más largo que ya lo incluye:
        /// la vuelta al selector declara de una vez la recarga Y el arranque posterior, de modo
        /// que la barra no retrocede a cero a mitad de espera, que es de las pocas cosas que una
        /// barra de progreso puede hacer peor que no existir.
        /// </summary>
        public static void ComenzarSiProcede(string perfil, params FaseDeArranque[] fases)
        {
            if (EnCurso) return;
            Comenzar(perfil, fases);
        }

        public static void EntrarEnFase(string clave)
        {
            if (!EnCurso) return;

            CerrarFaseEnCurso();

            for (int i = 0; i < _fases.Count; i++)
            {
                if (_fases[i].Clave != clave) continue;
                _indice = i;
                _faseVisitada[i] = true;
                _fraccionDeFase = 0f;
                _cronoFase.Reset(); _cronoFase.Start();
                Avisar();
                return;
            }

            // Fase no declarada: no se inventa una entrada en la barra (movería los pesos y con
            // ellos el ritmo), pero se deja dicho, porque significa que el perfil y el código
            // que lo recorre se han desincronizado.
            Debug.LogWarning($"[DigitalTwin][Arranque] Fase '{clave}' no declarada en el perfil " +
                             $"'{_perfil}': el progreso no la refleja. Revisa el perfil.");
        }

        /// <summary>Avance interno de la fase en curso, para las que saben medirse (recuento de
        /// elementos procesados, fotogramas de espera consumidos, carga asíncrona…).</summary>
        public static void ProgresoDeFase(float fraccion01)
        {
            if (!EnCurso) return;
            float nueva = Clamp01(fraccion01);
            // Filtro de ruido: la barra solo se redibuja cuando el cambio es visible. Sin él, un
            // bucle que reporta cada elemento dispara un rebuild de la interfaz por elemento, y
            // el propio informe de progreso pasa a ser parte del problema que se está midiendo.
            if (Math.Abs(nueva - _fraccionDeFase) < 0.01f) return;
            _fraccionDeFase = nueva;
            Avisar();
        }

        public static void Terminar()
        {
            if (!EnCurso) return;
            CerrarFaseEnCurso();
            _cronoTotal.Stop();
            EnCurso = false;
            _indice = _fases.Count;   // la barra queda llena
            _fraccionDeFase = 0f;
            Avisar();

            Debug.LogWarning(InformeDeTiempos());

            // Comprobación simétrica a la de EntrarEnFase: una fase declarada por la que el
            // código nunca ha pasado. No bloquea nada —este modelo es pasivo, nadie espera a
            // que una fase llegue—, pero significa que el perfil y el código se han
            // desincronizado y que la barra ha estado mintiendo sobre lo que quedaba.
            var noVisitadas = new StringBuilder();
            for (int i = 0; i < _fases.Count; i++)
            {
                if (_faseVisitada[i]) continue;
                if (noVisitadas.Length > 0) noVisitadas.Append(", ");
                noVisitadas.Append('\'').Append(_fases[i].Clave).Append('\'');
            }
            if (noVisitadas.Length > 0)
                Debug.LogWarning($"[DigitalTwin][Arranque] Perfil '{_perfil}' terminado sin pasar " +
                                 $"por las fases declaradas {noVisitadas}: el perfil y el codigo " +
                                 "que lo recorre se han desincronizado. Revisa el perfil.");
        }

        /// <summary>
        /// Un latido del bucle principal. Lo llama la pantalla de carga en su <c>Update</c>,
        /// que es el único componente garantizado vivo durante todo el arranque.
        /// </summary>
        public static void RegistrarIntervaloDeFotograma()
        {
            if (!EnCurso) return;

            long ahora = Stopwatch.GetTimestamp();
            if (_marcaFotogramaAnterior != 0L)
            {
                double ms = (ahora - _marcaFotogramaAnterior) * 1000d / Stopwatch.Frequency;
                _fotogramas++;
                if (ms > _peorIntervaloMs) _peorIntervaloMs = ms;
                if (ms > 25d) _intervalosMayores25++;
                if (ms > 50d) _intervalosMayores50++;
                if (ms > 100d) _intervalosMayores100++;
            }
            _marcaFotogramaAnterior = ahora;
        }

        /// <summary>
        /// Línea única con todo lo medido. Sale por <c>LogWarning</c> y no por <c>Log</c> porque
        /// las compilaciones que no son de desarrollo filtran los informativos del registro del
        /// dispositivo, y esta es precisamente la línea que hay que poder leer en el visor.
        /// </summary>
        public static string InformeDeTiempos()
        {
            var sb = new StringBuilder();
            sb.Append("[DigitalTwin][Arranque] perfil '").Append(_perfil).Append("': total ")
              .Append(_cronoTotal.Elapsed.TotalMilliseconds.ToString("0")).Append(" ms |");

            for (int i = 0; i < _fases.Count; i++)
            {
                sb.Append(' ').Append(_fases[i].Clave).Append('=')
                  .Append(_msPorFase[i].ToString("0.0")).Append(" ms");
                if (i < _fases.Count - 1) sb.Append(';');
            }

            sb.Append(" | entrega: ").Append(_fotogramas).Append(" fotogramas, peor intervalo ")
              .Append(_peorIntervaloMs.ToString("0.0")).Append(" ms, >25 ms: ")
              .Append(_intervalosMayores25).Append(", >50 ms: ").Append(_intervalosMayores50)
              .Append(", >100 ms: ").Append(_intervalosMayores100)
              .Append(". El peor intervalo es la magnitud que decide el confort: mide cuánto " +
                      "tiempo estuvo el compositor reproyectando el mismo fotograma.");
            return sb.ToString();
        }

        private static void CerrarFaseEnCurso()
        {
            if (_indice < 0 || _indice >= _fases.Count) return;
            _cronoFase.Stop();
            _msPorFase[_indice] += _cronoFase.Elapsed.TotalMilliseconds;
        }

        private static void Avisar()
        {
            var manejador = AlCambiar;
            if (manejador == null) return;
            try { manejador(); }
            catch (Exception e)
            {
                // Una pantalla rota no puede tumbar el arranque: el arranque es lo que hay que
                // terminar, la pantalla solo lo cuenta.
                Debug.LogError("[DigitalTwin][Arranque] La pantalla de carga ha fallado al " +
                               "refrescarse; el arranque continua. " + e.Message);
            }
        }

        private static float Clamp01(float v) => v < 0f ? 0f : (v > 1f ? 1f : v);
    }

    /// <summary>
    /// Presupuesto de tiempo por fotograma para los bucles que se reparten entre fotogramas.
    ///
    /// El patrón es siempre el mismo: se recorre una colección larga y, cada cierto número de
    /// elementos, se pregunta si el presupuesto está agotado; si lo está, se cede el fotograma y
    /// se reanuda en el siguiente. Lo que se busca NO es que el trabajo termine antes —termina
    /// más tarde— sino que ningún fotograma se lleve el trabajo entero.
    ///
    /// El valor por defecto (4 ms) sale del periodo de pantalla del visor: el registro del
    /// dispositivo declara 90 Hz (<c>RefreshRate change: 90.0</c>), es decir, 11,1 ms por
    /// fotograma, de los que el propio dibujado ya consume una parte. Gastar 4 ms deja margen
    /// para que el fotograma se entregue a tiempo aunque el reparto no sea exacto, porque el
    /// presupuesto se comprueba ENTRE elementos y el último siempre se pasa un poco. Es una
    /// elección conservadora a falta de medida: la instrumentación de arranque dirá si sobra
    /// margen —ningún intervalo cerca de dos periodos— o si hay que bajarlo.
    /// </summary>
    public sealed class PresupuestoDeFotograma
    {
        public const float MilisegundosPorDefecto = 4f;

        private readonly double _presupuestoMs;
        private long _marca;

        public PresupuestoDeFotograma(float milisegundos = MilisegundosPorDefecto)
        {
            _presupuestoMs = Math.Max(1f, milisegundos);
            Reiniciar();
        }

        public void Reiniciar() => _marca = Stopwatch.GetTimestamp();

        public bool Agotado =>
            (Stopwatch.GetTimestamp() - _marca) * 1000d / Stopwatch.Frequency >= _presupuestoMs;
    }
}
