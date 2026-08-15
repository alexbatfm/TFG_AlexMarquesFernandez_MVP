using UnityEngine;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.XR.OpenXR; // OpenXRSettings, para consultar el estado de la sesion
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
    /// LA CAPA MUERE CON LA SESIÓN OPENXR, Y HAY QUE ENTERARSE (corrección del 15-08, noche).
    /// El SDK del fabricante destruye TODAS las capas de transparencia cuando la sesión OpenXR
    /// se destruye (<c>VivePassthrough.OnSessionDestroy</c> las recorre y las libera; la
    /// documentación de <c>CreatePlanarPassthrough</c> lo declara: «Passthroughs will be
    /// destroyed automatically when the current XrSession is destroyed»). La sesión se destruye
    /// cuando el sistema toma el control del visor —por ejemplo, al configurar el límite de
    /// seguridad, al pasar por el menú del sistema o al retirarse el visor el tiempo
    /// suficiente—. La primera versión de esta clase no registraba el callback de destrucción
    /// que ese mismo método ofrece como parámetro, así que tras cualquiera de esos pasos el
    /// estado interno seguía diciendo «activada» sobre una capa que ya no existía, y como
    /// <c>Aplicar(true)</c> se fiaba del estado interno, NINGÚN camino de código podía volver a
    /// encenderla: el vídeo desaparecía para siempre sin una sola línea de error. Es lo
    /// observado en la prueba del 15-08 por la tarde (modo anclado sin vídeo de principio a
    /// fin). La corrección tiene tres partes: registrar el callback (el estado refleja la
    /// muerte de la capa), reconciliar <c>Aplicar(true)</c> contra el runtime en vez de contra
    /// la creencia (ver <see cref="CapaViva"/>), y recrear la capa sola cuando la sesión vuelve.
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

        /// <summary>Cierto si, según el estado interno, la capa está creada y la cámara
        /// preparada para dejarla ver. Desde el 15-08 este estado se corrige solo cuando el
        /// runtime destruye la capa (callback de sesión), pero la señal que el modo anclado
        /// acepta como «hay vídeo» es <see cref="ConfirmadaActiva"/>, no esta.</summary>
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

        /// <summary>
        /// Cierto solo si el estado interno dice «activada» Y la capa existe ahora mismo en la
        /// lista de capas vivas del runtime. Es la única señal que el modo anclado acepta como
        /// confirmación de que hay vídeo: el estado interno puede quedarse obsoleto (la capa
        /// muere con la sesión OpenXR), y la lista del runtime no.
        /// </summary>
        public bool ConfirmadaActiva => Activado && CapaViva();

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

        /// <summary>Puesto a cierto por el callback de destrucción de sesión: la capa murió con
        /// la sesión y hay que recrearla en cuanto el runtime tenga sesión otra vez.</summary>
        private bool _recrearAlVolverLaSesion;
        private float _proximoIntentoDeRecreacion;
        private const float SegundosEntreIntentosDeRecreacion = 2f;
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

        /// <summary>
        /// Enciende o apaga la transparencia. La rama de encendido es IDEMPOTENTE DE VERDAD y se
        /// puede llamar sin condición: no se fía del estado interno, se reconcilia con el
        /// runtime. Si la capa sigue viva, solo reafirma el borrado de la cámara (barato, y de
        /// paso repara cualquier reescritura); si el estado decía «activada» pero la capa ya no
        /// existe —la sesión OpenXR se destruyó—, lo denuncia y la recrea. La guarda anterior
        /// (<c>if (activar == Activado) return;</c>) convertía exactamente ese caso en un
        /// silencio sin salida.
        /// </summary>
        public void Aplicar(bool activar)
        {
            if (!activar)
            {
                if (!Activado) return;
                Apagar();
                Activado = false;
                Debug.LogWarning("[DigitalTwin][AR] Passthrough desactivado.");
                return;
            }

            if (Activado && CapaViva())
            {
                // Ya encendida de verdad: reafirmar el borrado con alfa cero no cuesta nada y
                // deja la llamada sin condición previa. Sin traza: este camino es el habitual
                // en los reintentos y anegaría el registro.
                PrepararCamaraParaLaCapa();
                return;
            }

            if (Activado)
            {
                // El estado decía «activada» pero el runtime ya no tiene la capa. Con el
                // callback de sesión registrado este camino no debería darse; si se da, es que
                // la capa murió por una vía sin callback, y callarlo sería repetir el fallo
                // del 15-08.
                Debug.LogError("[DigitalTwin][AR] El estado decia transparencia activada pero la " +
                               "capa ya no existe en el runtime (sesion OpenXR reiniciada sin " +
                               "aviso registrado): se recrea.");
                DescartarCapaMuerta();
                Activado = false;
            }

            if (Encender())
            {
                Activado = true;
                Debug.LogWarning("[DigitalTwin][AR] Passthrough activado.");
            }
        }

        /// <summary>
        /// Cierto si la capa creada por esta clase figura ahora mismo en la lista de capas del
        /// runtime. Fuera de Android es siempre falso: no hay capa posible.
        /// </summary>
        public bool CapaViva()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!_capaCreada) return false;
            var capas = PassthroughAPI.GetCurrentPassthroughLayerIDs();
            return capas != null && capas.Contains(_capa);
#else
            return false;
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        /// <summary>
        /// Callback registrado en la creación de la capa: el SDK lo invoca cuando la sesión
        /// OpenXR va a destruirse y con ella todas las capas de transparencia. Aquí NO se
        /// recrea nada —la sesión está muriéndose—; se corrige el estado para que refleje la
        /// realidad y se deja programada la recreación para cuando la sesión vuelva.
        /// </summary>
        private void AlDestruirseLaSesionOpenXR(XrPassthroughHTC capa)
        {
            DescartarCapaMuerta();
            Activado = false;
            _recrearAlVolverLaSesion = true;
            _proximoIntentoDeRecreacion = 0f;
            Debug.LogError("[DigitalTwin][AR] La sesion OpenXR se ha destruido y el runtime ha " +
                           "retirado la capa de transparencia (ocurre al pasar por el sistema: " +
                           "limite de seguridad, menu, visor retirado). El estado pasa a " +
                           "desactivado y la capa se recreara sola cuando la sesion vuelva.");
        }

        private void Update()
        {
            if (!_recrearAlVolverLaSesion || Activado) return;
            if (Time.unscaledTime < _proximoIntentoDeRecreacion) return;
            _proximoIntentoDeRecreacion = Time.unscaledTime + SegundosEntreIntentosDeRecreacion;

            if (!SesionOpenXRCreada()) return; // aún sin sesión: se sigue esperando

            Debug.LogWarning("[DigitalTwin][AR] La sesion OpenXR ha vuelto: se recrea la capa " +
                             "de transparencia.");
            Aplicar(true);
            if (Activado) _recrearAlVolverLaSesion = false;
        }

        private static bool SesionOpenXRCreada()
        {
            var ajustes = OpenXRSettings.Instance;
            var feature = ajustes != null ? ajustes.GetFeature<VivePassthrough>() : null;
            return feature != null && feature.XrSessionCreated;
        }
#endif

        private void DescartarCapaMuerta()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // La capa ya no existe en el runtime: no hay nada que destruir, solo olvidarla.
            _capaCreada = false;
            _capa = default;
#endif
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
            // El tercer argumento es el callback de destrucción de sesión: sin él, la muerte de
            // la capa junto con la sesión OpenXR es invisible para la aplicación (ver la nota
            // de la clase; es la causa raíz del modo anclado sin vídeo del 15-08).
            XrResult res = PassthroughAPI.CreatePlanarPassthrough(out _capa, LayerType.Underlay,
                                                                  AlDestruirseLaSesionOpenXR);
            if (res != XrResult.XR_SUCCESS)
            {
                // Dos motivos conocidos, y se distinguen por el codigo: XR_ERROR_SESSION_LOST
                // significa que la sesion OpenXR no existe en este momento (el sistema tiene el
                // control; se puede reintentar); cualquier otro apunta a la caracteristica
                // 'VIVE XR Passthrough' sin marcar en la pestana de Android. El sintoma sin esta
                // traza —fondo opaco— es identico al de olvidar el borrado de la camara.
                Debug.LogError("[DigitalTwin][AR] No se pudo crear la capa de transparencia (" + res +
                               "). Si es SESSION_LOST, la sesion OpenXR no esta disponible ahora " +
                               "mismo y se puede reintentar; si no, comprueba que 'VIVE XR " +
                               "Passthrough' este marcada en Project Settings > XR Plug-in " +
                               "Management > OpenXR, pestaña Android.");
                return false;
            }
            _capaCreada = true;
            _recrearAlVolverLaSesion = false;

            // 2) El borrado de la cámara. Sin esto la capa existe y no se ve.
            PrepararCamaraParaLaCapa();
            return true;
#else
            return false;
#endif
        }

        private void Apagar()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // Apagar es una orden del usuario o del modo: se cancela cualquier recreación
            // pendiente para que la capa no reaparezca sola tras elegir navegación por nodos.
            _recrearAlVolverLaSesion = false;
            if (_capaCreada)
            {
                if (CapaViva())
                {
                    PassthroughAPI.DestroyPassthrough(_capa);
                }
                else
                {
                    Debug.LogWarning("[DigitalTwin][AR] Passthrough: la capa ya no existia en el " +
                                     "runtime (la retiro la destruccion de la sesion); no hay " +
                                     "nada que destruir.");
                }
                _capaCreada = false;
                _capa = default;
            }
#endif
            RestaurarAjustesCamara();
        }

        /// <summary>Borrado a color sólido con alfa cero, guardando antes los ajustes
        /// originales (una sola vez). Reafirmarlo es barato y repara reescrituras ajenas.</summary>
        private void PrepararCamaraParaLaCapa()
        {
            if (_camara == null) return;
            GuardarAjustesCamara();
            _camara.clearFlags = CameraClearFlags.SolidColor;
            _camara.backgroundColor = new Color(0f, 0f, 0f, 0f);
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
