using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using VIVE.OpenXR;              // XrResult y sus miembros (XR_SUCCESS)
using VIVE.OpenXR.Passthrough;  // PassthroughAPI y XrPassthroughHTC
// El paquete declara XrPassthroughHTC en dos espacios de nombres, Passthrough y
// CompositionLayer, con el mismo nombre y usos distintos. Importar los dos con `using` produce
// CS0104 (referencia ambigua). Se trae LayerType por alias en lugar de importar el espacio
// entero: así solo entra el tipo que hace falta y XrPassthroughHTC queda resuelto sin
// ambigüedad al de Passthrough, que es el que devuelve PassthroughAPI.
using LayerType = VIVE.OpenXR.CompositionLayer.LayerType;
#endif

namespace DigitalTwin.MR
{
    /// <summary>
    /// Vídeo de transparencia en color (<i>passthrough</i>) del visor: muestra el entorno real
    /// como fondo sobre el que se compone la geometría virtual.
    ///
    /// Por qué hace falta código y no basta con la casilla. Marcar «VIVE XR Passthrough» en la
    /// pestaña de Android habilita la extensión de OpenXR, es decir, concede permiso para
    /// usarla; no crea ninguna capa ni cambia lo que se ve. Una aplicación puede tener la
    /// extensión activa y seguir renderizando sobre fondo opaco indefinidamente, que es
    /// exactamente el estado en que estaba este proyecto. Hacen falta dos piezas más, y ninguna
    /// de las dos funciona sin la otra: crear la capa de composición, y dejar que se vea.
    ///
    /// Por qué la capa es <c>Underlay</c> y no <c>Overlay</c>. Las capas de composición se
    /// ordenan respecto al contenido que dibuja el motor. Como capa superpuesta, el vídeo de las
    /// cámaras se pinta *encima* del modelo y lo tapa por completo; el resultado es un visor que
    /// muestra la sala y nada más. Debajo, el motor compone su fotograma sobre el vídeo, que es
    /// lo que se busca.
    ///
    /// Por qué hay que tocar el borrado de la cámara. Aunque la capa esté debajo, el motor
    /// entrega un fotograma que cubre todo el campo de visión: si la cámara borra con el cielo
    /// procedural o con un color opaco, ese fondo se compone sobre el vídeo y lo oculta. El
    /// fotograma tiene que llegar con alfa cero allí donde no hay geometría, y eso se consigue
    /// borrando a color sólido con alfa cero. Es la mitad que suele olvidarse: se crea la capa
    /// correctamente, no se ve nada, y no hay error en ninguna parte porque no ha fallado nada.
    ///
    /// Estado de partida. El componente arranca desactivado y restaura con exactitud los ajustes
    /// previos de la cámara al apagarse, siguiendo el mismo criterio que
    /// <see cref="DigitalTwin.Visual.SolarLightingController"/>: una opción que altera el
    /// aspecto de la escena no debe dejar rastro cuando se desactiva.
    /// </summary>
    public class MRPassthroughController : MonoBehaviour
    {
        private const string ClavePreferencia = "dt.ar.passthrough";

        public static MRPassthroughController Instancia { get; private set; }

        /// <summary>Cierto si la capa está creada y la cámara preparada para dejarla ver.</summary>
        public bool Activado { get; private set; }

        /// <summary>
        /// Cierto si la plataforma puede ofrecer transparencia. En el editor y en escritorio es
        /// falso: no hay cámaras que atravesar. Permite que el menú muestre la fila desactivada
        /// con una explicación en lugar de ocultarla, que confunde más.
        /// </summary>
        public bool Disponible
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return true;
#else
                return false;
#endif
            }
        }

        public string Descripcion
        {
            get
            {
                if (!Disponible) return "no disponible fuera del visor";
                return Activado ? "activada" : "desactivada";
            }
        }

        private Camera _camara;

        // Ajustes originales de la cámara, para devolverlos tal cual estaban.
        private CameraClearFlags _borradoOriginal;
        private Color _fondoOriginal;
        private bool _ajustesGuardados;

#if UNITY_ANDROID && !UNITY_EDITOR
        private XrPassthroughHTC _capa;
        private bool _capaCreada;
#endif

        public static MRPassthroughController Crear()
        {
            // Traza incondicional. Sin ella, un fallo en cualquier punto posterior es
            // indistinguible de que esta llamada no se haya ejecutado nunca, y son dos
            // diagnósticos completamente distintos.
            Debug.LogWarning("[DigitalTwin][AR] Passthrough: Crear() alcanzado.");

            if (Instancia != null) return Instancia;

            var go = new GameObject("PassthroughController");
            Instancia = go.AddComponent<MRPassthroughController>();
            return Instancia;
        }

        private void Awake()
        {
            if (Instancia != null && Instancia != this) { Destroy(gameObject); return; }
            Instancia = this;

            _camara = Camera.main;
            if (_camara == null)
            {
                Debug.LogWarning("[DigitalTwin][AR] Passthrough: no hay cámara con la etiqueta " +
                                 "MainCamera. La transparencia no podrá activarse.");
            }
        }

        /// <summary>
        /// Fotogramas que se dejan pasar antes de crear la capa de transparencia.
        ///
        /// La primera versión la creaba durante el arranque, en el mismo fotograma en que se
        /// indexa el modelo, se generan los volúmenes de colisión y se carga el grafo. El
        /// resultado observado el 2026-08-13 fue que el servicio del visor
        /// (<c>com.htc.vr.device.ser</c>) terminaba en violación de segmento a los pocos segundos,
        /// en su hilo de corrección de distorsión de las cámaras, al construir la imagen EGL de
        /// copia cero de la textura de cámara. La traza apunta al controlador gráfico, no a la
        /// aplicación, pero quien elige el instante de la petición es esta.
        ///
        /// Solicitar esa creación mientras el motor satura la GPU con la carga de escena es una
        /// concurrencia evitable sin coste: el retardo es imperceptible para el usuario, que en
        /// ese momento aún está viendo aparecer el edificio.
        /// </summary>
        private const int FotogramasDeEspera = 90;

        private void Start()
        {
            StartCoroutine(EncenderCuandoLaEscenaSeAsiente());
        }

        private System.Collections.IEnumerator EncenderCuandoLaEscenaSeAsiente()
        {
            for (int i = 0; i < FotogramasDeEspera; i++) yield return null;
            AplicarPreferenciaInicial();
        }

        private void AplicarPreferenciaInicial()
        {
            // Se respeta la preferencia guardada, pero solo donde la plataforma puede cumplirla.
            //
            // El valor de fábrica es ACTIVADO, al contrario que en la iluminación solar. La razón
            // es que aquí el estado por defecto define de qué aplicación se trata: arrancar sobre
            // fondo opaco convierte una interfaz de Realidad Aumentada en una de Realidad Virtual
            // hasta que alguien toque un ajuste. Lo excepcional es querer aislarse del entorno,
            // no lo contrario, así que es eso lo que debe pedirse expresamente.
            //
            // El riesgo de encenderlo de fábrica está acotado: si la capa no puede crearse,
            // Encender() devuelve falso, el estado se queda en desactivado y la aplicación sigue
            // siendo usable sobre fondo opaco, con el motivo escrito en el registro.
            bool deseado = Disponible && PlayerPrefs.GetInt(ClavePreferencia, 1) == 1;
            Aplicar(deseado);
        }

        /// <summary>Invierte el estado y lo recuerda entre sesiones.</summary>
        public void Alternar()
        {
            if (!Disponible) return;
            Aplicar(!Activado);
            PlayerPrefs.SetInt(ClavePreferencia, Activado ? 1 : 0);
            PlayerPrefs.Save();
        }

        public void Aplicar(bool activar)
        {
            if (activar == Activado) return;

            if (activar) { if (!Encender()) return; }
            else Apagar();

            Activado = activar;
            Debug.LogWarning("[DigitalTwin][AR] Passthrough " + (Activado ? "activado" : "desactivado") + ".");
        }

        private bool Encender()
        {
            // Ningún camino de salida es mudo. Un fallo silencioso aquí obliga a distinguir a
            // ciegas entre «no se llamó», «la plataforma no lo permite», «no hay cámara» y «la
            // capa no se creó», que son cuatro diagnósticos distintos.
            if (!Disponible)
            {
                Debug.LogWarning("[DigitalTwin][AR] Passthrough no disponible en esta plataforma. " +
                                 "Solo hay transparencia en compilación de Android sobre el visor.");
                return false;
            }
            if (_camara == null)
            {
                Debug.LogError("[DigitalTwin][AR] Passthrough: no hay cámara con la etiqueta " +
                               "MainCamera, así que no se puede preparar el borrado con alfa cero.");
                return false;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            // 1) La capa. Underlay: el motor compone su fotograma sobre el vídeo, no al revés.
            XrResult res = PassthroughAPI.CreatePlanarPassthrough(out _capa, LayerType.Underlay);
            if (res != XrResult.XR_SUCCESS)
            {
                // El motivo más probable es que la característica no esté marcada en la pestaña
                // de Android. Se dice explícitamente porque el síntoma —fondo opaco— es idéntico
                // al de olvidar el borrado de la cámara, y sin este aviso no se distinguen.
                Debug.LogError("[DigitalTwin][AR] No se pudo crear la capa de transparencia (" + res +
                               "). Comprueba que 'VIVE XR Passthrough' esté marcada en " +
                               "Project Settings > XR Plug-in Management > OpenXR, pestaña Android.");
                return false;
            }
            _capaCreada = true;

            // 2) El borrado de la cámara. Sin esto la capa existe y no se ve.
            GuardarAjustesCamara();
            _camara.clearFlags = CameraClearFlags.SolidColor;
            _camara.backgroundColor = new Color(0f, 0f, 0f, 0f);
            return true;
#else
            return false;
#endif
        }

        private void Apagar()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (_capaCreada)
            {
                PassthroughAPI.DestroyPassthrough(_capa);
                _capaCreada = false;
                _capa = default;
            }
#endif
            RestaurarAjustesCamara();
        }

        private void GuardarAjustesCamara()
        {
            if (_ajustesGuardados || _camara == null) return;
            _borradoOriginal = _camara.clearFlags;
            _fondoOriginal = _camara.backgroundColor;
            _ajustesGuardados = true;
        }

        private void RestaurarAjustesCamara()
        {
            if (!_ajustesGuardados || _camara == null) return;
            _camara.clearFlags = _borradoOriginal;
            _camara.backgroundColor = _fondoOriginal;
            _ajustesGuardados = false;
        }

        private void OnDestroy()
        {
            if (Instancia == this)
            {
                Apagar();
                Instancia = null;
            }
        }
    }
}
