using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.Rendering.RenderGraphModule;
using UnityEngine.Rendering.Universal;
using UnityEngine.XR;
#if UNITY_ANDROID && !UNITY_EDITOR
using UnityEngine.XR.OpenXR;            // OpenXRSettings
using UnityEngine.XR.OpenXR.Features;   // OpenXRFeature (modo de mezcla, xrGetInstanceProcAddr)
using VIVE.OpenXR;                      // ViveInterceptors, XrResult, XrInstance, XrSystemId, XrSession, XrEnvironmentBlendMode, XrStructureType, OpenXRHelper
using VIVE.OpenXR.Passthrough;          // VivePassthrough (instancia y sistema OpenXR por reflexión)
#endif

namespace DigitalTwin.MR
{
    /// <summary>
    /// Diagnóstico de la COMPOSICIÓN del fotograma con el vídeo de transparencia. Solo lee y
    /// escribe en el registro; no cambia ningún ajuste ni toca la capa de transparencia.
    ///
    /// POR QUÉ EXISTE (ronda 10, 17-08). Cuatro sesiones de visor sin ver el vídeo. En la
    /// cuarta, con registro, TODO lo medible dijo que la cadena funcionaba: capa creada con
    /// XR_SUCCESS y en la lista del SDK, servicio del sistema mostrando la transparencia,
    /// cámara física encendida a 60 fps — y aun así, negro. Si la capa se crea, el servicio la
    /// muestra y la cámara está encendida, entonces el vídeo se está COMPONIENDO y no llega a
    /// los ojos: algo lo tapa. La transparencia se compone como capa SUBYACENTE, y una capa
    /// subyacente solo se ve donde el fotograma de la aplicación llega al compositor con alfa
    /// menor que uno. Este componente mide, en el dispositivo y en cada arranque, cada eslabón
    /// de ese «llegar con alfa»:
    ///
    ///   1. EL MODO DE MEZCLA CON EL ENTORNO (XrEnvironmentBlendMode): los que el runtime
    ///      declara soportar (xrEnumerateEnvironmentBlendModes), el que Unity tiene
    ///      seleccionado (OpenXRFeature.GetEnvironmentBlendMode) y el que de verdad viaja en
    ///      cada xrEndFrame (leído en el interceptor del SDK). Lectura, no cambio.
    ///   2. LA PILA DE CAPAS de cada xrEndFrame tras pasar por el SDK: cuántas capas, de qué
    ///      tipo, en qué orden y con qué banderas — en particular si la capa PASSTHROUGH_HTC
    ///      va DEBAJO de la de proyección y si la de proyección lleva el bit
    ///      BLEND_TEXTURE_SOURCE_ALPHA que el SDK dice poner (VivePassthrough.
    ///      ForceProjectionLayerTransparent). Es lo que el compositor recibe, medido.
    ///   3. EL CAMINO DEL ALFA EN URP: la configuración estática (HDR, precisión del color,
    ///      MSAA, postprocesado, preservación del alfa del framebuffer, formato de intercambio
    ///      del ojo) y, medido por el propio URP en cada cámara, el formato del objetivo
    ///      intermedio, si renderiza directo al backbuffer y si URP considera habilitada la
    ///      salida de alfa (isAlphaOutputEnabled). El eslabón que las rondas anteriores nunca
    ///      comprobaron: un objetivo intermedio SIN canal alfa (B10G11R11, el formato HDR de
    ///      32 bits) entrega al blit final un alfa igual a uno en todo el fotograma, y el
    ///      compositor tapa la capa subyacente con todas las llamadas devolviendo éxito.
    ///   4. EL ALFA REAL en el objetivo del ojo, leído de la GPU (AsyncGPUReadback sobre la
    ///      textura del pase de renderizado XR) en tres puntos, cada pocos segundos: 0 es
    ///      transparente, 1 es opaco. Es la prueba directa, si la plataforma permite la lectura.
    ///   5. EL SEGUIMIENTO DE LA CABEZA (estado y presencia del usuario), porque el servicio
    ///      del visor oculta el vídeo a nivel de sistema al perder el seguimiento
    ///      (setLostTracking en el registro del sistema): sus transiciones quedan con hora.
    ///
    /// Cada traza lleva el prefijo [DigitalTwin][AR][Compos]; el mapa traza→pregunta está en
    /// docs/roadmap/PRUEBA-AR-10-2026-08-17.md. Vive en la escena (no persiste): renace con
    /// cada rearranque, como el controlador de transparencia. El interceptor de xrEndFrame se
    /// instala UNA vez por proceso.
    /// </summary>
    public class MRDiagnosticoComposicion : MonoBehaviour
    {
        private const string Pref = "[DigitalTwin][AR][Compos] ";
        private const float SegundosEntreLatidos = 2f;
        private const float SegundosEntreLecturasDeAlfa = 3f;
        private const int MaxCapasRegistradas = 8;

        public static MRDiagnosticoComposicion Instancia { get; private set; }

        public static MRDiagnosticoComposicion Crear()
        {
            Debug.LogWarning(Pref + "Crear() alcanzado.");
            if (Instancia != null) return Instancia;
            var go = new GameObject("DiagnosticoComposicionAR");
            Instancia = go.AddComponent<MRDiagnosticoComposicion>();
            return Instancia;
        }

        // ------------------------------------------------------------------------------
        //  Medida por cámara, tomada por URP en el grafo de renderizado (hilo principal)
        // ------------------------------------------------------------------------------

        private sealed class Medida
        {
            public string Camara;
            public bool Xr;
            public bool Hdr;
            public bool Post;
            public bool AlfaHabilitado;
            public GraphicsFormat Destino;
            public int MsaaDestino;
            public GraphicsFormat Swapchain;
            public bool DirectoAlBackbuffer;
            public bool IntermedioValido;
            public GraphicsFormat Intermedio;
            public int MsaaIntermedio;
            public string Firma;
        }

        private static Medida _ultimaMedida;
        private static string _ultimaFirmaRegistrada;
        private static int _fotogramasMedidos;

        /// <summary>
        /// Pase que no dibuja nada: solo lee, ya dentro del grafo de renderizado y para la
        /// cámara en curso, lo que URP ha decidido para este fotograma. Se encola desde
        /// beginCameraRendering; en el modo de compatibilidad (grafo desactivado) no se
        /// ejecuta y el arranque lo dice.
        ///
        /// Momento: BeforeRenderingPostProcessing, es decir, con la escena ya dibujada y ANTES
        /// del blit final. En AfterRendering URP ya ha llamado a SwitchActiveTexturesToBackbuffer
        /// y el objetivo activo sería siempre el backbuffer, ocultando si hubo intermedio.
        /// requiresIntermediateTexture queda en false para que el propio pase no altere la
        /// decisión de URP que se quiere medir (RequiresIntermediateAttachments la consulta).
        /// </summary>
        private sealed class PasoLecturaObjetivos : ScriptableRenderPass
        {
            public PasoLecturaObjetivos()
            {
                renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
                requiresIntermediateTexture = false;
            }

            public override void RecordRenderGraph(RenderGraph renderGraph, ContextContainer frameData)
            {
                try
                {
                    var cam = frameData.Get<UniversalCameraData>();
                    var res = frameData.Get<UniversalResourceData>();
                    var m = new Medida
                    {
                        Camara = cam.camera != null ? cam.camera.name : "?",
                        Xr = cam.xrRendering,
                        Hdr = cam.isHdrEnabled,
                        Post = cam.postProcessEnabled,
                        AlfaHabilitado = cam.isAlphaOutputEnabled,
                        Destino = cam.cameraTargetDescriptor.graphicsFormat,
                        MsaaDestino = cam.cameraTargetDescriptor.msaaSamples,
                        Swapchain = cam.xr != null && cam.xr.enabled ? cam.xr.renderTargetDesc.graphicsFormat : GraphicsFormat.None,
                        DirectoAlBackbuffer = res.isActiveTargetBackBuffer
                    };
                    if (res.activeColorTexture.IsValid())
                    {
                        var d = renderGraph.GetTextureDesc(res.activeColorTexture);
                        m.IntermedioValido = true;
                        m.Intermedio = d.format;
                        m.MsaaIntermedio = (int)d.msaaSamples;
                    }
                    m.Firma = $"{m.Camara}|{m.Xr}|{m.Hdr}|{m.Post}|{m.AlfaHabilitado}|{m.Destino}|{m.MsaaDestino}|" +
                              $"{m.Swapchain}|{m.DirectoAlBackbuffer}|{m.IntermedioValido}|{m.Intermedio}|{m.MsaaIntermedio}";
                    _ultimaMedida = m;
                    _fotogramasMedidos++;
                }
                catch (Exception e)
                {
                    _ultimaMedida = new Medida { Camara = "error", Firma = "error:" + e.GetType().Name + ":" + e.Message };
                }
            }
        }

        // ------------------------------------------------------------------------------
        //  Instantánea del xrEndFrame (escrita en el hilo de render por el interceptor)
        // ------------------------------------------------------------------------------

#if UNITY_ANDROID && !UNITY_EDITOR
        private static volatile bool _hookInstalado;
        private static volatile bool _hookConError;
        private static volatile int _modoDeMezclaEnviado;      // XrEnvironmentBlendMode del último xrEndFrame
        private static volatile int _capasEnviadas;
        private static readonly int[] _tiposCapa = new int[MaxCapasRegistradas];
        private static readonly ulong[] _banderasCapa = new ulong[MaxCapasRegistradas];
        private static volatile int _firmaPila;                 // resumen barato para detectar cambios
        private static long _fotogramasInterceptados;
        private static string _hookMensajeError = string.Empty;
#endif

        // ------------------------------------------------------------------------------
        //  Estado del componente
        // ------------------------------------------------------------------------------

        private Camera _camara;
        private PasoLecturaObjetivos _paso;
        private float _proximoLatido;
        private float _proximaLecturaDeAlfa;
#if UNITY_ANDROID && !UNITY_EDITOR
        private int _ultimaFirmaPilaRegistrada = int.MinValue;
        private bool _pilaRegistradaAlgunaVez;
#endif

        // Lectura de alfa del ojo.
        private bool _lecturaDeAlfaDisponible = true;
        private int _erroresDeLectura;
        private readonly float[] _alfaLeido = new float[3] { -1f, -1f, -1f };
        private readonly bool[] _alfaValido = new bool[3];
        private int _lecturasCompletadas;
        private int _lecturasRegistradas;

        // Seguimiento de la cabeza.
        private InputTrackingState _estadoSeguimientoAnterior = (InputTrackingState)(-1);
        private bool _presenciaAnterior;
        private bool _presenciaLeida;

        private void Awake()
        {
            if (Instancia != null && Instancia != this) { Destroy(gameObject); return; }
            Instancia = this;
            _camara = Camera.main;
        }

        private void Start()
        {
            RegistrarConfiguracionEstatica();

            // Cada montaje (el objeto renace con la escena) vuelve a dejar su linea base de URP y de
            // la pila de capas aunque no hayan cambiado: asi cada anclado del registro lleva la suya.
            _ultimaFirmaRegistrada = null;

            _paso = new PasoLecturaObjetivos();
            RenderPipelineManager.beginCameraRendering += EncolarPaso;
            RenderPipelineManager.endContextRendering += LeerAlfaDelOjo;

#if UNITY_ANDROID && !UNITY_EDITOR
            InstalarInterceptorDeEndFrame();
            Debug.LogWarning(Pref + "Modo de mezcla: soportados por el runtime " + ModosSoportadosPorElRuntime() +
                             "; seleccionado por Unity=" + ModoDeMezclaSegunUnity() +
                             ". El enviado en cada xrEndFrame se registra con la pila de capas.");
#else
            Debug.LogWarning(Pref + "Modo de mezcla: fuera del visor no hay runtime OpenXR que consultar.");
#endif
        }

        private void OnDestroy()
        {
            RenderPipelineManager.beginCameraRendering -= EncolarPaso;
            RenderPipelineManager.endContextRendering -= LeerAlfaDelOjo;
            if (Instancia == this) Instancia = null;
        }

        private void EncolarPaso(ScriptableRenderContext contexto, Camera camara)
        {
            if (_paso == null || camara == null) return;
            if (_camara != null && camara != _camara) return;   // solo la cámara principal
            // Sin GetUniversalAdditionalCameraData(): esa extension AÑADE el componente si falta y
            // aqui no se cambia nada de la camara. Sin componente, la camara usa el renderer por
            // defecto del activo, que es el que se consulta.
            ScriptableRenderer renderer = null;
            if (camara.TryGetComponent<UniversalAdditionalCameraData>(out var datos))
                renderer = datos.scriptableRenderer;
            else if (UniversalRenderPipeline.asset != null)
                renderer = UniversalRenderPipeline.asset.scriptableRenderer;
            if (renderer == null) return;
            renderer.EnqueuePass(_paso);
        }

        // ------------------------------------------------------------------------------
        //  1 y 3 (estático): lo que dicen los activos y la plataforma, una vez por arranque
        // ------------------------------------------------------------------------------

        private void RegistrarConfiguracionEstatica()
        {
            var activo = UniversalRenderPipeline.asset;
            string urp = activo == null ? "SIN ACTIVO URP" :
                $"activo='{activo.name}', HDR={activo.supportsHDR}, precisionHDR={activo.hdrColorBufferPrecision}, " +
                $"MSAA={activo.msaaSampleCount}, escala={activo.renderScale:0.00}, texturaOpacos={activo.supportsCameraOpaqueTexture}, " +
                $"alfaEnPost={activo.allowPostProcessAlphaOutput}";

            bool b10 = SystemInfo.IsFormatSupported(GraphicsFormat.B10G11R11_UFloatPack32, GraphicsFormatUsage.Blend);
            bool rgba16 = SystemInfo.IsFormatSupported(GraphicsFormat.R16G16B16A16_SFloat, GraphicsFormatUsage.Blend);
            GraphicsFormat ldr = SystemInfo.GetGraphicsFormat(DefaultFormat.LDR);
            GraphicsFormat hdr = SystemInfo.GetGraphicsFormat(DefaultFormat.HDR);

            string cam = "sin camara";
            bool camaraHdr = false, post = false;
            if (_camara != null)
            {
                camaraHdr = _camara.allowHDR;
                // TryGetComponent y no GetUniversalAdditionalCameraData(): esta ultima añade el
                // componente si falta, y el diagnostico no debe cambiar la camara.
                _camara.TryGetComponent<UniversalAdditionalCameraData>(out var extra);
                post = extra != null && extra.renderPostProcessing;
                cam = $"HDR permitido={_camara.allowHDR}, MSAA permitido={_camara.allowMSAA}, borrado={_camara.clearFlags}, " +
                      $"alfa del fondo={_camara.backgroundColor.a:0.00}, postprocesado={post}" +
                      (extra != null ? $", antialiasing={extra.antialiasing}" : ", sin UniversalAdditionalCameraData (valores por defecto)");
            }

            string modoGrafo = "?";
            try
            {
                var ajustesGrafo = GraphicsSettings.GetRenderPipelineSettings<RenderGraphSettings>();
                modoGrafo = ajustesGrafo != null
                    ? (ajustesGrafo.enableRenderCompatibilityMode ? "COMPATIBILIDAD (el pase de lectura NO se ejecuta)" : "grafo de renderizado")
                    : "sin ajustes";
            }
            catch (Exception e) { modoGrafo = "error: " + e.GetType().Name; }

            var descOjo = XRSettings.eyeTextureDesc;
            string ojo = XRSettings.enabled
                ? $"{descOjo.width}x{descOjo.height} {descOjo.graphicsFormat} msaa={descOjo.msaaSamples} dim={descOjo.dimension}"
                : "XR no habilitado";

            // Predicción de lo que URP 17.3.0 elegirá como formato del objetivo de color, con su
            // propia regla (UniversalRenderPipelineCore.MakeRenderTextureGraphicsFormat): con
            // HDR y precisión de 32 bits, si no se exige alfa y B10G11R11 se soporta, ese es el
            // formato — y no tiene canal alfa. La medida real la da el pase (linea "MEDIDO").
            bool hdrEfectivo = activo != null && activo.supportsHDR && camaraHdr;
            bool exigeAlfa = Graphics.preserveFramebufferAlpha;
            GraphicsFormat previsto;
            if (hdrEfectivo)
            {
                if (!exigeAlfa && activo.hdrColorBufferPrecision != HDRColorBufferPrecision._64Bits && b10)
                    previsto = GraphicsFormat.B10G11R11_UFloatPack32;
                else if (rgba16) previsto = GraphicsFormat.R16G16B16A16_SFloat;
                else previsto = hdr;
            }
            else previsto = ldr;
            bool previstoConAlfa = GraphicsFormatUtility.HasAlphaChannel(previsto);

            Debug.LogWarning(Pref + "Configuracion de color: URP " + urp + "; camara: " + cam +
                             $"; preserveFramebufferAlpha={exigeAlfa}; API={SystemInfo.graphicsDeviceType}; " +
                             $"B10G11R11 soportado={b10}; RGBA16F soportado={rgba16}; LDR={ldr}; HDR={hdr}; " +
                             $"pipeline={modoGrafo}; textura de ojo XR={ojo}.");
            Debug.LogWarning(Pref + $"Formato del objetivo de color PREVISTO (regla de URP 17.3.0): {previsto}, " +
                             $"con canal alfa={previstoConAlfa}. " +
                             (previstoConAlfa
                                 ? "El fotograma PUEDE llegar al compositor con alfa: si aun asi no hay video, el fallo esta en la pila de capas o en el sistema (ver las trazas de xrEndFrame y de seguimiento)."
                                 : "SIN canal alfa el blit final entrega alfa=1 en todo el fotograma y el compositor TAPA la capa subyacente aunque todas las llamadas devuelvan exito. Correccion: HDR desactivado en el activo URP movil (o precision de 64 bits)."));
        }

        // ------------------------------------------------------------------------------
        //  Latido: vuelca lo medido cuando cambia (URP, xrEndFrame, alfa leido, seguimiento)
        // ------------------------------------------------------------------------------

        private void Update()
        {
            if (Time.unscaledTime < _proximoLatido) return;
            _proximoLatido = Time.unscaledTime + SegundosEntreLatidos;

            RegistrarMedidaDeUrpSiCambia();
            RegistrarPilaDeCapasSiCambia();
            RegistrarAlfaLeidoSiHayNuevo();
            RegistrarSeguimientoSiCambia();
        }

        private static void RegistrarMedidaDeUrpSiCambia()
        {
            var m = _ultimaMedida;
            if (m == null || m.Firma == _ultimaFirmaRegistrada) return;
            _ultimaFirmaRegistrada = m.Firma;

            if (m.Camara == "error")
            {
                Debug.LogError(Pref + "Pase de lectura de objetivos: " + m.Firma);
                return;
            }

            bool alfaEnDestino = GraphicsFormatUtility.HasAlphaChannel(m.Destino);
            bool alfaEnIntermedio = m.IntermedioValido && GraphicsFormatUtility.HasAlphaChannel(m.Intermedio);
            string veredicto;
            if (m.DirectoAlBackbuffer)
                veredicto = "renderiza DIRECTO al objetivo del ojo: el alfa del borrado llega tal cual al compositor.";
            else if (alfaEnIntermedio && m.AlfaHabilitado)
                veredicto = "intermedio CON alfa y salida de alfa habilitada: el blit final conserva el alfa.";
            else
                veredicto = "intermedio SIN canal alfa o salida de alfa deshabilitada: el blit final entrega alfa=1 y la capa subyacente queda TAPADA.";

            Debug.LogWarning(Pref + $"Objetivos MEDIDOS por URP (camara '{m.Camara}', XR={m.Xr}, HDR={m.Hdr}, post={m.Post}): " +
                             $"descriptor de destino={m.Destino} (alfa={alfaEnDestino}, msaa={m.MsaaDestino}); " +
                             $"swapchain XR={m.Swapchain}; directo al backbuffer={m.DirectoAlBackbuffer}; " +
                             $"objetivo de color activo={(m.IntermedioValido ? m.Intermedio.ToString() : "no valido")} " +
                             $"(alfa={alfaEnIntermedio}, msaa={m.MsaaIntermedio}); salida de alfa segun URP={m.AlfaHabilitado}; " +
                             $"fotogramas medidos={_fotogramasMedidos}. VEREDICTO: {veredicto}");
        }

        private void RegistrarPilaDeCapasSiCambia()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            if (!_hookInstalado)
            {
                if (!_pilaRegistradaAlgunaVez)
                {
                    _pilaRegistradaAlgunaVez = true;
                    Debug.LogError(Pref + "Interceptor de xrEndFrame NO instalado (" + _hookMensajeError +
                                   "): la pila de capas no se puede medir en este arranque.");
                }
                return;
            }
            if (_hookConError && !_pilaRegistradaAlgunaVez)
            {
                _pilaRegistradaAlgunaVez = true;
                Debug.LogError(Pref + "El interceptor de xrEndFrame lanzo una excepcion (" + _hookMensajeError +
                               "); se ha desactivado la lectura de la pila.");
                return;
            }

            long fotogramas = System.Threading.Interlocked.Read(ref _fotogramasInterceptados);
            if (fotogramas == 0)
            {
                if (!_pilaRegistradaAlgunaVez && Time.unscaledTime > 8f)
                {
                    _pilaRegistradaAlgunaVez = true;
                    Debug.LogWarning(Pref + "Interceptor instalado pero xrEndFrame aun no ha pasado por el (0 fotogramas): " +
                                     "o la sesion no renderiza, o el SDK no encadena AfterOriginalEndFrame.");
                }
                return;
            }

            int firma = _firmaPila;
            if (firma == _ultimaFirmaPilaRegistrada && _pilaRegistradaAlgunaVez) return;
            _ultimaFirmaPilaRegistrada = firma;
            _pilaRegistradaAlgunaVez = true;

            int n = Math.Min(_capasEnviadas, MaxCapasRegistradas);
            var texto = new System.Text.StringBuilder();
            bool hayPassthrough = false, hayProyeccion = false, proyeccionConAlfa = false;
            int indicePassthrough = -1, indiceProyeccion = -1;
            for (int i = 0; i < n; i++)
            {
                var tipo = (XrStructureType)_tiposCapa[i];
                ulong banderas = _banderasCapa[i];
                string nombreTipo = tipo == XrStructureType.XR_TYPE_COMPOSITION_LAYER_PROJECTION ? "PROYECCION"
                                  : tipo == XrStructureType.XR_TYPE_COMPOSITION_LAYER_PASSTHROUGH_HTC ? "PASSTHROUGH_HTC"
                                  : tipo.ToString() + "(" + _tiposCapa[i] + ")";
                string nombreBanderas = DescribirBanderas(banderas);
                texto.Append($" [{i}] {nombreTipo} banderas=0x{banderas:X} ({nombreBanderas});");
                if (tipo == XrStructureType.XR_TYPE_COMPOSITION_LAYER_PASSTHROUGH_HTC) { hayPassthrough = true; if (indicePassthrough < 0) indicePassthrough = i; }
                if (tipo == XrStructureType.XR_TYPE_COMPOSITION_LAYER_PROJECTION)
                {
                    hayProyeccion = true; indiceProyeccion = i;
                    proyeccionConAlfa = (banderas & 0x2UL) != 0;   // XR_COMPOSITION_LAYER_BLEND_TEXTURE_SOURCE_ALPHA_BIT
                }
            }
            string modo = ((XrEnvironmentBlendMode)_modoDeMezclaEnviado).ToString();
            string lectura;
            if (!hayPassthrough) lectura = "NO hay capa de transparencia en la pila: el SDK no la esta enviando (capa destruida o lista vacia).";
            else if (!hayProyeccion) lectura = "hay transparencia pero NO hay capa de proyeccion: el motor no ha enviado fotograma.";
            else if (indicePassthrough > indiceProyeccion) lectura = "la transparencia va ENCIMA de la proyeccion (overlay): taparia al modelo, no al reves.";
            else if (!proyeccionConAlfa) lectura = "la transparencia va debajo pero la proyeccion NO lleva el bit de alfa: el compositor la trata como opaca.";
            else lectura = "transparencia DEBAJO y proyeccion CON bit de alfa: el compositor usara el alfa del fotograma; lo que decide es el alfa que llega (ver 'Objetivos MEDIDOS' y 'Alfa medido').";

            Debug.LogWarning(Pref + $"xrEndFrame (fotograma interceptado n.º {fotogramas}): modo de mezcla enviado={modo}, " +
                             $"capas={_capasEnviadas}:{texto} LECTURA: {lectura}");
#endif
        }

        private static string DescribirBanderas(ulong banderas)
        {
            var partes = new List<string>();
            if ((banderas & 0x1UL) != 0) partes.Add("CORRECT_CHROMATIC_ABERRATION");
            if ((banderas & 0x2UL) != 0) partes.Add("BLEND_TEXTURE_SOURCE_ALPHA");
            if ((banderas & 0x4UL) != 0) partes.Add("UNPREMULTIPLIED_ALPHA");
            if ((banderas & 0x8UL) != 0) partes.Add("INVERTED_ALPHA_EXT");
            return partes.Count == 0 ? "ninguna" : string.Join("|", partes);
        }

        // ------------------------------------------------------------------------------
        //  4: alfa real en el objetivo del ojo
        // ------------------------------------------------------------------------------

        private void LeerAlfaDelOjo(ScriptableRenderContext contexto, List<Camera> camaras)
        {
            if (!_lecturaDeAlfaDisponible) return;
            if (Time.unscaledTime < _proximaLecturaDeAlfa) return;
            _proximaLecturaDeAlfa = Time.unscaledTime + SegundosEntreLecturasDeAlfa;

            if (!SystemInfo.supportsAsyncGPUReadback)
            {
                _lecturaDeAlfaDisponible = false;
                Debug.LogWarning(Pref + "Alfa medido: la plataforma no soporta AsyncGPUReadback; se omite la lectura directa.");
                return;
            }

            RenderTexture objetivo = ObjetivoDelOjo(out string motivo);
            if (objetivo == null)
            {
                if (_erroresDeLectura++ == 0)
                    Debug.LogWarning(Pref + "Alfa medido: sin textura del ojo (" + motivo + "). Se reintenta cada " +
                                     SegundosEntreLecturasDeAlfa + " s; solo se avisa una vez.");
                return;
            }

            try
            {
                int w = objetivo.width, h = objetivo.height;
                int lado = 4;
                // Tres puntos: centro, cuadrante superior izquierdo, cuadrante inferior derecho.
                (int x, int y)[] puntos = { (w / 2, h / 2), (w / 4, (3 * h) / 4), ((3 * w) / 4, h / 4) };
                for (int i = 0; i < 3; i++)
                {
                    int idx = i;
                    int x = Mathf.Clamp(puntos[i].x - lado / 2, 0, w - lado);
                    int y = Mathf.Clamp(puntos[i].y - lado / 2, 0, h - lado);
                    // Firma de Unity: (src, mip, x, ANCHO, y, ALTO, z, profundidad, formato, callback).
                    // z=0, profundidad=1: primera capa del array de texturas (ojo izquierdo en un solo pase).
                    AsyncGPUReadback.Request(objetivo, 0, x, lado, y, lado, 0, 1, GraphicsFormat.R8G8B8A8_UNorm,
                        peticion => AlLlegarLectura(peticion, idx));
                }
            }
            catch (Exception e)
            {
                if (++_erroresDeLectura >= 3) _lecturaDeAlfaDisponible = false;
                Debug.LogWarning(Pref + "Alfa medido: la peticion de lectura fallo (" + e.GetType().Name + ": " + e.Message +
                                 ")" + (_lecturaDeAlfaDisponible ? "; se reintenta." : "; se desactiva la lectura directa."));
            }
        }

        private static RenderTexture ObjetivoDelOjo(out string motivo)
        {
            motivo = string.Empty;
            var pantallas = new List<XRDisplaySubsystem>();
            SubsystemManager.GetSubsystems(pantallas);
            foreach (var p in pantallas)
            {
                if (!p.running) continue;
                if (p.GetRenderPassCount() == 0) { motivo = "el subsistema de pantalla no tiene pases"; return null; }
                var rt = p.GetRenderTextureForRenderPass(0);
                if (rt == null) { motivo = "GetRenderTextureForRenderPass devolvio null"; }
                return rt;
            }
            motivo = pantallas.Count == 0 ? "sin subsistema de pantalla XR (Editor/escritorio)" : "subsistema XR no en marcha";
            return null;
        }

        private void AlLlegarLectura(AsyncGPUReadbackRequest peticion, int indice)
        {
            if (peticion.hasError)
            {
                _alfaValido[indice] = false;
                if (++_erroresDeLectura >= 6 && _lecturaDeAlfaDisponible)
                {
                    _lecturaDeAlfaDisponible = false;
                    Debug.LogWarning(Pref + "Alfa medido: la GPU rechaza la lectura de la textura del ojo (hasError) de " +
                                     "forma repetida; se desactiva la lectura directa. Quedan las medidas de URP y de xrEndFrame.");
                }
                return;
            }
            try
            {
                var datos = peticion.GetData<Color32>();
                if (datos.Length == 0) { _alfaValido[indice] = false; return; }
                float suma = 0f;
                for (int i = 0; i < datos.Length; i++) suma += datos[i].a / 255f;
                _alfaLeido[indice] = suma / datos.Length;
                _alfaValido[indice] = true;
                _lecturasCompletadas++;
            }
            catch (Exception)
            {
                _alfaValido[indice] = false;
            }
        }

        private void RegistrarAlfaLeidoSiHayNuevo()
        {
            if (_lecturasCompletadas == 0 || _lecturasCompletadas == _lecturasRegistradas) return;
            // Se registra la primera lectura y despues una de cada diez (cada ~30 s), o cuando
            // el valor cambia de banda (transparente <0,5 / opaco >=0,5).
            bool primera = _lecturasRegistradas == 0;
            bool cambioDeBanda = false;
            if (!primera)
            {
                foreach (var v in _alfaLeido) if (v >= 0f && ((v >= 0.5f) != (_ultimaBandaOpaca))) cambioDeBanda = true;
            }
            _lecturasRegistradas = _lecturasCompletadas;
            _lecturasDesdeElUltimoRegistro++;
            if (!primera && !cambioDeBanda && _lecturasDesdeElUltimoRegistro < 10) return;
            _lecturasDesdeElUltimoRegistro = 0;

            string f(int i) => _alfaValido[i] ? _alfaLeido[i].ToString("0.00") : "?";
            bool algunoValido = _alfaValido[0] || _alfaValido[1] || _alfaValido[2];
            float peor = -1f;
            for (int i = 0; i < 3; i++) if (_alfaValido[i]) peor = Mathf.Max(peor, _alfaLeido[i]);
            _ultimaBandaOpaca = peor >= 0.5f;
            string lectura = !algunoValido ? "sin lecturas validas."
                : peor >= 0.98f ? "OPACO en los tres puntos: el fotograma llega al compositor sin transparencia (el fondo de alfa 0 no sobrevive al camino de URP)."
                : peor < 0.5f ? "TRANSPARENTE: el fotograma llega con alfa bajo; si aun asi no hay video, el fallo esta en la composicion o en el sistema."
                : "MIXTO: hay zonas opacas (interfaz o geometria) y zonas transparentes; el fondo si conserva el alfa.";
            Debug.LogWarning(Pref + $"Alfa medido en el objetivo del ojo izquierdo (0=transparente, 1=opaco): centro={f(0)}, " +
                             $"cuadrante sup. izq.={f(1)}, cuadrante inf. dcho.={f(2)} (lectura n.º {_lecturasCompletadas}). {lectura}");
        }
        private bool _ultimaBandaOpaca;
        private int _lecturasDesdeElUltimoRegistro;

        // ------------------------------------------------------------------------------
        //  5: seguimiento de la cabeza y presencia
        // ------------------------------------------------------------------------------

        private void RegistrarSeguimientoSiCambia()
        {
            var cabeza = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            if (!cabeza.isValid) return;
            if (cabeza.TryGetFeatureValue(CommonUsages.trackingState, out InputTrackingState estado) &&
                estado != _estadoSeguimientoAnterior)
            {
                bool primera = _estadoSeguimientoAnterior == (InputTrackingState)(-1);
                _estadoSeguimientoAnterior = estado;
                bool posicion = (estado & InputTrackingState.Position) != 0;
                bool rotacion = (estado & InputTrackingState.Rotation) != 0;
                Debug.LogWarning(Pref + $"Seguimiento de la cabeza {(primera ? "(inicial)" : "CAMBIA")}: {estado} " +
                                 $"(posicion={posicion}, rotacion={rotacion}). " +
                                 (posicion ? "" : "SIN POSICION: el servicio del visor puede ocultar el video a nivel de sistema (setLostTracking)."));
            }
            if (cabeza.TryGetFeatureValue(CommonUsages.userPresence, out bool presencia) &&
                (!_presenciaLeida || presencia != _presenciaAnterior))
            {
                _presenciaLeida = true;
                _presenciaAnterior = presencia;
                Debug.LogWarning(Pref + $"Presencia del usuario (sensor de proximidad): {presencia}.");
            }
        }

        // ------------------------------------------------------------------------------
        //  Android: interceptor de xrEndFrame y consultas OpenXR
        // ------------------------------------------------------------------------------
#if UNITY_ANDROID && !UNITY_EDITOR
        private static void InstalarInterceptorDeEndFrame()
        {
            if (_hookInstalado) return;
            try
            {
                var interceptores = ViveInterceptors.Instance;
                if (interceptores == null) { _hookMensajeError = "ViveInterceptors.Instance es null"; return; }
                interceptores.AfterOriginalEndFrame += TrasEndFrame;
                _hookInstalado = true;
                Debug.LogWarning(Pref + "Interceptor de xrEndFrame instalado (AfterOriginalEndFrame del SDK): la pila de " +
                                 "capas y el modo de mezcla enviados se leen en cada fotograma.");
            }
            catch (Exception e)
            {
                _hookMensajeError = e.GetType().Name + ": " + e.Message;
            }
        }

        /// <summary>Se ejecuta en el hilo que llama a xrEndFrame (el de render): solo lecturas
        /// de memoria y escrituras en campos estaticos, ninguna llamada al motor.</summary>
        private static bool TrasEndFrame(XrSession sesion, ref ViveInterceptors.XrFrameEndInfo info, ref XrResult resultado)
        {
            if (_hookConError) return true;
            try
            {
                _modoDeMezclaEnviado = (int)info.environmentBlendMode;
                int n = (int)info.layerCount;
                _capasEnviadas = n;
                int firma = n * 7919 + (int)info.environmentBlendMode * 31;
                int leer = Math.Min(n, MaxCapasRegistradas);
                for (int i = 0; i < leer; i++)
                {
                    IntPtr p = info.layers != IntPtr.Zero ? Marshal.ReadIntPtr(info.layers, i * IntPtr.Size) : IntPtr.Zero;
                    if (p == IntPtr.Zero) { _tiposCapa[i] = 0; _banderasCapa[i] = 0; continue; }
                    var cabecera = Marshal.PtrToStructure<ViveInterceptors.XrCompositionLayerBaseHeader>(p);
                    _tiposCapa[i] = (int)cabecera.type;
                    _banderasCapa[i] = (ulong)cabecera.layerFlags;
                    firma = unchecked(firma * 31 + _tiposCapa[i] * 17 + (int)(_banderasCapa[i] & 0xFF));
                }
                _firmaPila = firma;
                System.Threading.Interlocked.Increment(ref _fotogramasInterceptados);
            }
            catch (Exception e)
            {
                _hookConError = true;
                _hookMensajeError = e.GetType().Name + ": " + e.Message;
            }
            return true;
        }

        private static string ModoDeMezclaSegunUnity()
        {
            try
            {
                var metodo = typeof(OpenXRFeature).GetMethod("GetEnvironmentBlendMode",
                    BindingFlags.NonPublic | BindingFlags.Static);
                if (metodo == null) return "metodo no encontrado en el plugin";
                object valor = metodo.Invoke(null, null);
                return valor != null ? valor.ToString() : "null";
            }
            catch (Exception e) { return "error (" + e.GetType().Name + ")"; }
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        private delegate int DelegadoEnumerarModosDeMezcla(ulong instancia, ulong sistema, uint tipoDeVista,
                                                             uint capacidad, ref uint cuenta, IntPtr modos);

        /// <summary>xrEnumerateEnvironmentBlendModes contra el runtime, con la instancia y el
        /// sistema que ya tiene la feature de transparencia (por reflexion) y el
        /// xrGetInstanceProcAddr del plugin de Unity. Todo guardado: si algo falla, lo dice.</summary>
        private static string ModosSoportadosPorElRuntime()
        {
            try
            {
                var ajustes = OpenXRSettings.Instance;
                var feature = ajustes != null ? ajustes.GetFeature<VivePassthrough>() : null;
                if (feature == null) return "[sin feature VivePassthrough]";

                var campoInstancia = typeof(VivePassthrough).GetField("m_XrInstance", BindingFlags.NonPublic | BindingFlags.Instance);
                var campoSistema = typeof(VivePassthrough).GetField("m_XrSystemId", BindingFlags.NonPublic | BindingFlags.Instance);
                if (campoInstancia == null || campoSistema == null) return "[campos de instancia/sistema no encontrados en el SDK]";
                ulong instancia = campoInstancia.GetValue(feature) is XrInstance xi ? (ulong)xi : 0UL;
                ulong sistema = campoSistema.GetValue(feature) is XrSystemId xs ? (ulong)xs : 0UL;
                if (instancia == 0UL || sistema == 0UL) return $"[instancia={instancia}, sistema={sistema}: todavia no creados]";

                var propiedad = typeof(OpenXRFeature).GetProperty("xrGetInstanceProcAddr", BindingFlags.NonPublic | BindingFlags.Static);
                if (propiedad == null) return "[xrGetInstanceProcAddr no expuesto por el plugin]";
                IntPtr punteroGipa = (IntPtr)propiedad.GetValue(null);
                if (punteroGipa == IntPtr.Zero) return "[xrGetInstanceProcAddr nulo]";
                var gipa = Marshal.GetDelegateForFunctionPointer<OpenXRHelper.xrGetInstanceProcAddrDelegate>(punteroGipa);
                XrResult r = gipa(new XrInstance(instancia), "xrEnumerateEnvironmentBlendModes", out IntPtr funcion);
                if (r != XrResult.XR_SUCCESS || funcion == IntPtr.Zero) return "[xrEnumerateEnvironmentBlendModes no resuelto: " + r + "]";
                var enumerar = Marshal.GetDelegateForFunctionPointer<DelegadoEnumerarModosDeMezcla>(funcion);

                const uint PrimaryStereo = 2; // XR_VIEW_CONFIGURATION_TYPE_PRIMARY_STEREO
                uint cuenta = 0;
                int r1 = enumerar(instancia, sistema, PrimaryStereo, 0, ref cuenta, IntPtr.Zero);
                if (r1 != 0 || cuenta == 0) return "[cuenta: resultado=" + r1 + ", n=" + cuenta + "]";
                IntPtr buffer = Marshal.AllocHGlobal((int)cuenta * 4);
                try
                {
                    uint escritos = 0;
                    int r2 = enumerar(instancia, sistema, PrimaryStereo, cuenta, ref escritos, buffer);
                    if (r2 != 0) return "[lista: resultado=" + r2 + "]";
                    var nombres = new List<string>();
                    for (int i = 0; i < escritos; i++)
                        nombres.Add(((XrEnvironmentBlendMode)Marshal.ReadInt32(buffer, i * 4)).ToString());
                    return "[" + string.Join(", ", nombres) + "] (" + escritos + ")";
                }
                finally { Marshal.FreeHGlobal(buffer); }
            }
            catch (Exception e)
            {
                return "[error " + e.GetType().Name + ": " + e.Message + "]";
            }
        }
#endif
    }
}
