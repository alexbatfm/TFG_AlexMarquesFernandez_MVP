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
    /// CORRECCIÓN DE LA CORRECCIÓN (ronda 9, 17-08 — dos defectos VERIFICADOS EN EL FUENTE del
    /// SDK instalado, no hipótesis):
    ///
    /// 1. EL CALLBACK NUNCA LLEGA A REGISTRARSE. <c>PassthroughAPI.CreatePlanarPassthrough</c>
    ///    (Runtime/Toolkits/Passthrough/PassthroughAPI.cs:42) declara el parámetro
    ///    <c>onDestroyPassthroughSessionHandler</c> pero SU CUERPO NO LO USA NUNCA: la creación
    ///    baja por <c>XR_HTC_passthrough.xrCreatePassthroughHTC</c> →
    ///    <c>XR_HTC_passthrough_impls</c> → <c>feature.CreatePassthroughHTC(createInfo, out p)</c>,
    ///    la sobrecarga de DOS argumentos que registra <c>onDestroy = null</c>
    ///    (VivePassthrough.cs:817-820). El diccionario de handlers solo se llena por
    ///    <c>VivePassthrough.CreatePassthroughHTC(..., onDestroy)</c>, al que el toolkit no pasa
    ///    nada. Consecuencia: <see cref="AlDestruirseLaSesionOpenXR"/> es código muerto en esta
    ///    versión del SDK. Se sigue pasando el delegado (si HTC arregla el toolkit, empezará a
    ///    funcionar), pero la detección de muerte NO depende de él: la hace un latido de sondeo
    ///    (<see cref="Latido"/>) que compara el estado interno con la lista del SDK y con el
    ///    ciclo de vida de la sesión. La ronda 8 daba por hecho el registro y por eso su
    ///    recreación automática («cuando la sesión vuelva») no podía ejecutarse jamás: el
    ///    banderín que la armaba solo lo escribía el callback.
    ///
    /// 2. EL TOOLKIT ACUMULA CAPAS MUERTAS Y LAS SIGUE COMPONIENDO. Cuando la sesión se
    ///    destruye, <c>VivePassthrough.OnSessionDestroy</c> limpia SU lista
    ///    (<c>PassthroughList</c>) pero no el diccionario estático <c>layersDict</c> de
    ///    <c>PassthroughAPI</c>, cuyo contenido se reenvía ENTERO al runtime en cada fotograma
    ///    (<c>SubmitLayer()</c> → <c>SubmitLayers(layersDict.Values.ToList())</c> →
    ///    <c>OnBeforeEndFrame</c>). Tras recrear, el runtime recibe la capa nueva Y un
    ///    identificador destruido. <see cref="PurgarCapasMuertasDelSdk"/> limpia ese diccionario
    ///    por reflexión antes de cada recreación, con el resultado en el registro.
    ///
    /// LÍMITE DE LO QUE SE PUEDE MEDIR DESDE C#: <see cref="CapaViva"/> consulta
    /// <c>GetCurrentPassthroughLayerIDs()</c>, que devuelve la CONTABILIDAD EN C# DEL SDK
    /// (<c>VivePassthrough.PassthroughList</c>), no el estado del compositor. Detecta la muerte
    /// por destrucción de sesión (la lista se vacía), pero NO detecta una capa que el runtime
    /// haya dejado de componer sin pasar por ahí (p. ej. caída del servicio de cámaras
    /// <c>com.htc.vr.device.ser</c>, ya observada el 13-08). Por eso el latido registra además
    /// el estado de la sesión OpenXR: para poder correlacionar «el vídeo se fue a las hh:mm»
    /// con lo que el SDK sí cuenta.
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
        /// preparada para dejarla ver. Desde la ronda 9 lo reconcilia el latido de sondeo
        /// contra la lista del SDK (el callback de sesión no llega a registrarse; ver la nota
        /// de la clase), pero la señal que el modo anclado acepta como «hay vídeo» es
        /// <see cref="ConfirmadaActiva"/>, no esta.</summary>
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
        /// Cierto solo si el estado interno dice «activada» Y la capa figura ahora mismo en la
        /// lista de capas del SDK. Es la señal que el modo anclado acepta como confirmación de
        /// que hay vídeo. OJO CON LO QUE MIDE (ronda 9): la lista es la contabilidad en C# del
        /// SDK, no el compositor del visor — detecta la capa retirada por la destrucción de la
        /// sesión, pero un vídeo muerto por una vía que no pase por esa lista (caída del
        /// servicio de cámaras) seguiría contando como «confirmado». Ver la nota de la clase.
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

        /// <summary>Cierto cuando se ha detectado la muerte de la capa y hay que recrearla en
        /// cuanto el runtime tenga sesión otra vez. Desde la ronda 9 lo escribe el latido de
        /// sondeo (el callback del SDK nunca se registra; ver la nota de la clase).</summary>
        private bool _recrearAlVolverLaSesion;

        // --- Latido de diagnóstico y reconciliación (ronda 9) ------------------------------
        // Cada 2 s: compara estado interno contra la lista del SDK y contra el ciclo de vida de
        // la sesión OpenXR, denuncia cada transición, y recrea la capa cuando procede. Cada
        // 10 s deja además una línea completa de estado, para que el registro permita
        // reconstruir la secuencia entera sin conjeturas.
        private const float SegundosEntreLatidos = 2f;
        private const float SegundosEntreLineasDeEstado = 10f;
        private float _proximoLatido;
        private float _proximaLineaDeEstado;
        private VivePassthrough _feature;
        private bool _sesionCreadaAnterior;
        private XrSessionState _estadoSesionAnterior = XrSessionState.XR_SESSION_STATE_UNKNOWN;
        private bool _primeraLecturaDeSesion = true;
        private bool _avisadoSinFeature;
        private bool _extensionesRegistradas;
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
                // El estado decía «activada» pero la capa ya no está en la lista del SDK. Este
                // ES el camino normal de reconciliación tras una destrucción de sesión: el
                // callback del SDK nunca llega a registrarse (verificado en fuente, ronda 9),
                // así que la muerte de la capa solo se descubre aquí o en el latido.
                Debug.LogError("[DigitalTwin][AR] El estado decia transparencia activada pero la " +
                               "capa ya no esta en la lista del SDK (la sesion OpenXR se " +
                               "destruyo en algun momento anterior): se descarta y se recrea.");
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
        /// SDK (<c>VivePassthrough.PassthroughList</c>: contabilidad en C#, no el compositor;
        /// ver la nota de la clase). Fuera de Android es siempre falso: no hay capa posible.
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
        /// Callback que se PASA al SDK en la creación de la capa. VERIFICADO EN FUENTE (ronda
        /// 9): esta versión del toolkit lo descarta sin registrarlo, así que hoy es código
        /// muerto y la detección real la hace <see cref="Latido"/>. Se conserva porque pasarlo
        /// no cuesta nada y, si HTC corrige el toolkit, esta traza pasará a ser la más temprana.
        /// </summary>
        private void AlDestruirseLaSesionOpenXR(XrPassthroughHTC capa)
        {
            DescartarCapaMuerta();
            Activado = false;
            _recrearAlVolverLaSesion = true;
            Debug.LogError("[DigitalTwin][AR] CALLBACK del SDK: la sesion OpenXR se destruye y " +
                           "retira la capa de transparencia. (Si esta linea aparece, HTC ha " +
                           "corregido el registro del callback en el toolkit; hasta hoy no se " +
                           "invocaba nunca y la muerte la detectaba el latido de sondeo.)");
        }

        private VivePassthrough Feature()
        {
            if (_feature != null) return _feature;
            var ajustes = OpenXRSettings.Instance;
            _feature = ajustes != null ? ajustes.GetFeature<VivePassthrough>() : null;
            return _feature;
        }

        private bool SesionOpenXRCreada()
        {
            var feature = Feature();
            return feature != null && feature.XrSessionCreated;
        }

        private void Update()
        {
            if (Time.unscaledTime < _proximoLatido) return;
            _proximoLatido = Time.unscaledTime + SegundosEntreLatidos;
            Latido();
        }

        /// <summary>
        /// Un latido cada 2 s. Cuatro trabajos, todos con traza: (1) anunciar cada transición
        /// del ciclo de vida de la sesión OpenXR (creada/destruida y cambios de
        /// XrSessionState — la señal que faltaba en todas las sesiones anteriores para saber si
        /// el sistema tomó el control); (2) detectar la divergencia «estado interno activado
        /// pero la capa ya no está en la lista del SDK» sin depender del callback que el SDK no
        /// registra; (3) recrear la capa cuando hay sesión y está pendiente; (4) cada 10 s,
        /// una línea completa de estado para reconstruir la secuencia sin conjeturas.
        /// </summary>
        private void Latido()
        {
            var feature = Feature();
            if (feature == null)
            {
                if (!_avisadoSinFeature)
                {
                    _avisadoSinFeature = true;
                    Debug.LogError("[DigitalTwin][AR] Latido: OpenXRSettings no tiene la feature " +
                                   "VivePassthrough. Sin ella no puede existir ninguna capa de " +
                                   "transparencia: comprueba 'VIVE XR Passthrough' en la pestana " +
                                   "Android de OpenXR.");
                }
                return;
            }

            bool sesionCreada = feature.XrSessionCreated;
            XrSessionState estadoSesion = feature.XrSessionCurrentState;

            if (_primeraLecturaDeSesion)
            {
                _primeraLecturaDeSesion = false;
                Debug.LogWarning($"[DigitalTwin][AR] Latido inicial: sesion creada={sesionCreada}, " +
                                 $"estado de sesion={estadoSesion}, estado interno={Activado}, " +
                                 $"capa creada={_capaCreada}, capa en lista SDK={CapaViva()}.");
            }
            else
            {
                if (sesionCreada != _sesionCreadaAnterior)
                {
                    if (!sesionCreada)
                        Debug.LogError("[DigitalTwin][AR] La sesion OpenXR se ha DESTRUIDO " +
                                       "(el sistema ha tomado el control: limite de seguridad, " +
                                       "menu del sistema o visor retirado). El runtime retira " +
                                       "todas las capas de transparencia.");
                    else
                        Debug.LogWarning("[DigitalTwin][AR] La sesion OpenXR se ha CREADO de nuevo.");
                }
                if (estadoSesion != _estadoSesionAnterior)
                    Debug.LogWarning($"[DigitalTwin][AR] Estado de la sesion OpenXR: " +
                                     $"{_estadoSesionAnterior} -> {estadoSesion}.");
            }
            _sesionCreadaAnterior = sesionCreada;
            _estadoSesionAnterior = estadoSesion;

            // Divergencia estado/lista: la deteccion que la ronda 8 confiaba al callback.
            if (Activado && !CapaViva())
            {
                Debug.LogError("[DigitalTwin][AR] El estado interno decia transparencia activada " +
                               "pero la capa ya NO esta en la lista del SDK (la retiro la " +
                               "destruccion de la sesion OpenXR). Se descarta la capa muerta y " +
                               "se recreara en cuanto haya sesion.");
                DescartarCapaMuerta();
                Activado = false;
                _recrearAlVolverLaSesion = true;
            }

            if (_recrearAlVolverLaSesion && !Activado && sesionCreada)
            {
                Debug.LogWarning("[DigitalTwin][AR] Hay sesion OpenXR de nuevo: se recrea la " +
                                 "capa de transparencia.");
                Aplicar(true);
                if (Activado) _recrearAlVolverLaSesion = false;
            }

            if (Time.unscaledTime >= _proximaLineaDeEstado)
            {
                _proximaLineaDeEstado = Time.unscaledTime + SegundosEntreLineasDeEstado;
                var capas = PassthroughAPI.GetCurrentPassthroughLayerIDs();
                string alfa = _camara != null
                    ? $"{_camara.clearFlags}/alfa {_camara.backgroundColor.a:0.00}"
                    : "sin camara";
                Debug.LogWarning($"[DigitalTwin][AR] Estado transparencia: interno={Activado}, " +
                                 $"capa creada={_capaCreada}, capa en lista SDK={CapaViva()}, " +
                                 $"capas en lista={(capas != null ? capas.Count : -1)}, " +
                                 $"sesion creada={sesionCreada}, estado sesion={estadoSesion}, " +
                                 $"recreacion pendiente={_recrearAlVolverLaSesion}, " +
                                 $"camara={alfa}.");
            }
        }

        /// <summary>
        /// Limpia por reflexión el diccionario estático <c>PassthroughAPI.layersDict</c> de las
        /// entradas cuyo identificador ya no existe en la lista del SDK (defecto verificado en
        /// fuente: el SDK no lo limpia al morir la sesión y las sigue enviando al compositor en
        /// cada fotograma; ver la nota de la clase). Si la reflexión fallara (recorte de
        /// metadatos de IL2CPP), se declara y se sigue: el estado no queda peor que sin purga.
        /// </summary>
        private static void PurgarCapasMuertasDelSdk()
        {
            try
            {
                var campo = typeof(PassthroughAPI).GetField(
                    "layersDict",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
                if (campo == null)
                {
                    Debug.LogError("[DigitalTwin][AR] Purga del toolkit: no se encuentra el campo " +
                                   "layersDict de PassthroughAPI (¿version del SDK distinta?). No " +
                                   "se purga nada.");
                    return;
                }
                var diccionario = campo.GetValue(null) as System.Collections.IDictionary;
                if (diccionario == null || diccionario.Count == 0) return;

                var vivas = PassthroughAPI.GetCurrentPassthroughLayerIDs();
                var muertas = new System.Collections.Generic.List<object>();
                foreach (var clave in diccionario.Keys)
                {
                    if (!(clave is XrPassthroughHTC id)) continue;
                    if (vivas == null || !vivas.Contains(id)) muertas.Add(clave);
                }
                if (muertas.Count == 0) return;

                foreach (var clave in muertas) diccionario.Remove(clave);

                // Reenvio inmediato de la lista saneada: sin esto, la feature seguiria
                // componiendo la instantanea anterior (con las capas muertas) hasta la proxima
                // llamada interna a SubmitLayer.
                var feature = OpenXRSettings.Instance != null
                    ? OpenXRSettings.Instance.GetFeature<VivePassthrough>() : null;
                if (feature != null)
                {
                    var restantes = new System.Collections.Generic.List<PassthroughLayer>();
                    foreach (var valor in diccionario.Values)
                        if (valor is PassthroughLayer capa) restantes.Add(capa);
                    feature.SubmitLayers(restantes);
                }

                Debug.LogWarning($"[DigitalTwin][AR] Purga del toolkit: {muertas.Count} capa(s) " +
                                 "muerta(s) retirada(s) del diccionario de PassthroughAPI y " +
                                 "reenvio de la lista saneada al compositor.");
            }
            catch (System.Exception e)
            {
                Debug.LogError("[DigitalTwin][AR] Purga del toolkit fallida (" + e.GetType().Name +
                               ": " + e.Message + "). Se continua sin purgar: el riesgo es que el " +
                               "SDK siga componiendo una capa destruida junto a la nueva.");
            }
        }
#endif

        /// <summary>
        /// Diagnóstico en una línea para que otros componentes (el guardián del modo anclado)
        /// lo adjunten a sus trazas sin duplicar la lógica de consulta.
        /// </summary>
        public string DiagnosticoBreve()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            var feature = Feature();
            var capas = PassthroughAPI.GetCurrentPassthroughLayerIDs();
            return $"estado interno={Activado}, capa creada={_capaCreada}, " +
                   $"capa en lista SDK={CapaViva()}, capas en lista={(capas != null ? capas.Count : -1)}, " +
                   $"sesion creada={(feature != null ? feature.XrSessionCreated.ToString() : "sin feature")}, " +
                   $"estado sesion={(feature != null ? feature.XrSessionCurrentState.ToString() : "?")}";
#else
            return "plataforma sin transparencia (Editor/escritorio)";
#endif
        }

        private void DescartarCapaMuerta()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            // La capa ya no existe para el SDK: no hay nada que destruir, solo olvidarla — y
            // purgar el diccionario del toolkit, que a diferencia de la lista NO se limpia solo
            // y seguiria enviando el identificador muerto al compositor en cada fotograma.
            _capaCreada = false;
            _capa = default;
            PurgarCapasMuertasDelSdk();
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
            // Extensiones OpenXR de la transparencia, una sola vez: si el runtime no las
            // habilito al crear la instancia, ninguna llamada posterior puede funcionar y esta
            // es la traza que lo dice.
            if (!_extensionesRegistradas)
            {
                _extensionesRegistradas = true;
                Debug.LogWarning("[DigitalTwin][AR] Extensiones OpenXR de transparencia: " +
                                 "XR_HTC_passthrough habilitada=" +
                                 OpenXRRuntime.IsExtensionEnabled("XR_HTC_passthrough") +
                                 ", XR_HTC_passthrough_configuration habilitada=" +
                                 OpenXRRuntime.IsExtensionEnabled("XR_HTC_passthrough_configuration") + ".");
            }

            // Purga preventiva: si una sesion anterior dejo capas muertas en el diccionario del
            // toolkit, crear una nueva las pondria a componer juntas (ver la nota de la clase).
            PurgarCapasMuertasDelSdk();

            // 1) La capa. Underlay: el motor compone su fotograma sobre el vídeo, no al revés.
            // El tercer argumento es el callback de destrucción de sesión; VERIFICADO que esta
            // versión del SDK lo descarta (ver la nota de la clase), así que la detección real
            // de la muerte de la capa la hace el latido. Se pasa igualmente por si el toolkit
            // se corrige en el futuro.
            XrResult res = PassthroughAPI.CreatePlanarPassthrough(out _capa, LayerType.Underlay,
                                                                  AlDestruirseLaSesionOpenXR);
            if (res != XrResult.XR_SUCCESS)
            {
                // Dos motivos conocidos, y se distinguen por el codigo: XR_ERROR_SESSION_LOST
                // significa que la sesion OpenXR no existe en este momento (el sistema tiene el
                // control; se puede reintentar); cualquier otro apunta a la caracteristica
                // 'VIVE XR Passthrough' sin marcar en la pestana de Android. El sintoma sin esta
                // traza —fondo opaco— es identico al de olvidar el borrado de la camara.
                Debug.LogError("[DigitalTwin][AR] No se pudo crear la capa de transparencia " +
                               "(XrResult=" + res + "; " + DiagnosticoBreve() + "). Si es " +
                               "SESSION_LOST, la sesion OpenXR no esta disponible ahora mismo y " +
                               "se puede reintentar; si no, comprueba que 'VIVE XR Passthrough' " +
                               "este marcada en Project Settings > XR Plug-in Management > " +
                               "OpenXR, pestaña Android.");
                return false;
            }
            _capaCreada = true;
            _recrearAlVolverLaSesion = false;

            var capasTrasCrear = PassthroughAPI.GetCurrentPassthroughLayerIDs();
            Debug.LogWarning("[DigitalTwin][AR] Capa de transparencia creada: " +
                             "XrResult=" + res + ", id=" + _capa + ", capas en lista SDK=" +
                             (capasTrasCrear != null ? capasTrasCrear.Count : -1) + ".");

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
                    XrResult resDestruir = PassthroughAPI.DestroyPassthrough(_capa);
                    Debug.LogWarning("[DigitalTwin][AR] Capa de transparencia destruida a " +
                                     "peticion (XrResult=" + resDestruir + ").");
                }
                else
                {
                    Debug.LogWarning("[DigitalTwin][AR] Passthrough: la capa ya no existia para " +
                                     "el SDK (la retiro la destruccion de la sesion); no hay " +
                                     "nada que destruir.");
                    PurgarCapasMuertasDelSdk();
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
