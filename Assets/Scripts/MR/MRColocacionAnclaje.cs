using System.Collections.Generic;
using DigitalTwin.Core;
using DigitalTwin.Navigation;
using IFCImporter;
using UnityEngine;
using UnityEngine.UI;

namespace DigitalTwin.MR
{
    /// <summary>
    /// Interfaz de colocación del anclaje (modo anclado): el operario REGISTRA el modelo sobre
    /// el edificio real aportando pares de puntos correspondientes, ve la calidad del ajuste, y
    /// confirma; entonces se crea y persiste el anclaje espacial. Es quien por fin llama a
    /// <see cref="MRAnchorService.ColocarEnPose"/> y a <see cref="MRAnchorService.OlvidarAnclaje"/>.
    ///
    /// EL MÉTODO Y POR QUÉ (resumen; el detalle está en <see cref="MRRegistroPorPuntos"/> y en
    /// el estado del arte de la memoria): registro por pares de puntos con giro solo horizontal,
    /// mínimo dos puntos, recomendados tres, con residuo mostrado y con la estimación del error
    /// esperado en la sala más lejana. Un punto solo determina la traslación (la guiñada queda
    /// como esté y así se declara).
    ///
    /// QUÉ PUNTOS: UMBRALES DE PUERTA, no los puntos de vista «Esfera». Un punto de vista es una
    /// posición virtual en medio de una sala que nada físico marca: el operario no puede
    /// situarse «en la esfera» con precisión. El centro del vano de una puerta, a nivel de
    /// suelo, es un rasgo físico identificable a pocos centímetros, el modelo tiene 18 con
    /// GlobalId y su punto de suelo sale de la misma regla que ya usan los nodos del grafo
    /// (<see cref="PosicionDeNodos"/>: centro del volumen en planta, base del volumen como
    /// suelo). Las estaciones se eligen solas con la regla de colocación de fiduciales de West
    /// et al. (2001): la primera junto a la entrada (la puerta más cercana al punto de vista del
    /// Recibidor), la segunda la más alejada de la primera y la tercera la que maximiza el área
    /// del triángulo —separadas y no alineadas—; las siguientes, si el operario quiere más, por
    /// distancia máxima al conjunto. Cada puerta se describe por las salas que separa (la sala
    /// más cercana a cada lado de su plano) y se señala en un plano esquemático del edificio.
    ///
    /// EL GESTO: APUNTAR AL SUELO DESDE CERCA. De las tres vías consideradas —apuntar al suelo
    /// con el mando; llevar físicamente el mando al punto; usar la posición de la cabeza— se
    /// elige apuntar con el rayo al centro del umbral A MENOS DE DOS METROS. Es el mismo gesto
    /// que todo lo demás en la aplicación (descubribilidad), el usuario VE dónde termina el
    /// rayo sobre el suelo real a través de la transparencia (confirmación visual que ninguna
    /// de las otras dos vías da), y no obliga a agacharse. Su debilidad conocida es que el
    /// error angular del pulso se multiplica por la distancia (Argelaguet y Andújar, 2013),
    /// por eso se limita la distancia y se muestra en vivo. La cabeza se descarta porque
    /// arrastra dónde tenga los pies el usuario; llevar el mando al punto obliga a agacharse y
    /// no ofrece confirmación de qué punto se ha muestreado. Dos detalles de precisión: la
    /// pose se toma de UNA MUESTRA ANTERIOR AL FLANCO DEL GATILLO (apretar el gatillo perturba
    /// la pose del mando; el desplazamiento entre ambas se mide y se registra), y el punto
    /// físico se toma SOBRE EL PLANO DEL SUELO DE SEGUIMIENTO (y = 0 del origen a nivel de
    /// suelo, comprobado en la ronda 6), no sobre la geometría del modelo, que aún no está
    /// registrada.
    ///
    /// LA TRAMPA DE LA ALTURA, resuelta por construcción y registrada: el punto del modelo es
    /// SIEMPRE un punto de suelo (base del volumen de la puerta; para el respaldo «Esfera», su
    /// altura de autor menos los 1,55 m con que se colocaron) y el punto físico es siempre el
    /// impacto del rayo con el suelo de seguimiento, así que la transformación lleva suelo a
    /// suelo. Cada punto deja en el registro las tres cifras: altura del punto del modelo,
    /// altura de la pose recibida (el mando) y corrección vertical aplicada. DESDE EL 19-08 el
    /// «suelo de seguimiento» es el plano y=0 DEL SEGUIMIENTO llevado al mundo con el transform
    /// del origen (<see cref="PlanoSueloFisico"/>), no el plano horizontal de mundo a la cota del
    /// origen: el origen de ARScene estaba guardado con 4,16° de cabeceo y los dos planos no
    /// coincidían, con el modelo inclinado y su suelo hundido o elevado 7,3 cm por metro.
    ///
    /// FLUJO: al arrancar, si el servicio restaura un anclaje guardado, no se pregunta nada
    /// (aviso breve, con la opción de recolocar); si no lo hay, el panel de registro se abre
    /// solo con la primera estación. El panel se abre y cierra con el botón primario (A/X;
    /// tecla M en el Editor) en cualquier momento; mientras está abierto captura el rayo (el
    /// controlador de interacción cede, como con el menú). Puede deshacerse el último
    /// punto, saltarse una puerta inaccesible, terminar con los puntos que haya (con la calidad
    /// que haya, dicha), y recolocar desde cero. «Anclaje creado» y «guardado» se muestran por
    /// separado, porque persistir puede fallar por su cuenta y el sistema sigue siendo usable
    /// en la sesión.
    ///
    /// SIN ANCLAJE (Editor, o extensión no disponible) el registro funciona igual: el modelo se
    /// coloca, se muestra el residuo, y solo falta la persistencia, que se declara. Por eso el
    /// flujo entero —estaciones, etiquetas, plano, gesto, ajuste, deshacer— se puede ejercitar
    /// en Play con el respaldo de ratón; lo que exige visor es el anclaje y su restauración.
    /// </summary>
    public class MRColocacionAnclaje : MonoBehaviour
    {
        // --- Parámetros del registro ------------------------------------------------------
        public const int PuntosMinimos = 2;
        public const int PuntosRecomendados = 3;
        public const int PuntosMaximos = 6;
        /// <summary>Distancia máxima del mando al punto de suelo señalado. A 2 m y con un error
        /// angular de 1° el desplazamiento sobre el suelo es de unos 3–7 cm según la
        /// inclinación del rayo; más lejos, el pulso manda.</summary>
        public const float DistanciaMaximaAlSuelo = 2.0f;
        /// <summary>Umbrales de trabajo para el semáforo del residuo RMS (no son normas: con un
        /// error de correspondencia esperado de 3–5 cm por punto, un RMS de 5 cm es coherente y
        /// uno de 15 cm delata una puerta equivocada o un punto mal tomado).</summary>
        public const float UmbralBuenoMetros = 0.05f;
        public const float UmbralAceptableMetros = 0.15f;
        /// <summary>La pose se toma de la muestra más reciente ANTERIOR a este retardo respecto
        /// al flanco del gatillo, promediando la ventana: apretar el gatillo mueve el mando.</summary>
        private const float RetardoMuestraSegundos = 0.12f;
        private const float VentanaMuestraSegundos = 0.25f;
        private const float DistanciaMinimaEntrePuertas = 1.2f;
        private const float RadioBusquedaSala = 10f;

        // --- Panel ------------------------------------------------------------------------
        private const float AnchoPx = 780f, AltoPx = 600f, AnchoMetros = 0.78f;
        private const float DistanciaPanel = 1.15f;
        private const float MargenPx = 16f;
        private const float AnchoColumnaTexto = 440f;
        private const float LadoPlanoPx = 300f;
        private const float AltoBotonPx = 46f;
        private const float SegundosAvisoBreve = 9f;

        // Seguimiento del panel (19-08). Hasta entonces el panel se quedaba donde se abrió y solo
        // saltaba delante del usuario si se alejaba más de 2,2 m o giraba más de 60° durante
        // 0,8 s: en la práctica, al moverse por la estancia se perdía de vista, y con él la
        // frase que dice QUÉ punto se está capturando. Ahora lo coloca el mismo componente que
        // la ficha de metadatos (WorldPanelPlacer: zona muerta con histéresis, giro suavizado,
        // traslación inmediata), con MÁS holgura y MÁS retardo que la ficha, porque durante el
        // registro el usuario apunta a sitios concretos del suelo y un panel que le siguiera
        // pegado a la mirada le taparía justo lo que intenta apuntar. El panel entero queda
        // bajo la línea de visión (borde superior 2,4° por debajo de los ojos; con 0,60 m de
        // alto a 1,15 m, el borde inferior queda a unos 30° bajo la horizontal): el umbral de
        // una puerta a menos de 2 m del mando está a 39° o más bajo los ojos, y el rayo que va
        // de la mano al suelo pasa por debajo del panel —el gesto válido no lo atraviesa—.
        private const float ZonaMuertaPanelGrados = 30f;
        private const float SuavizadoPanel = 2.5f;
        private const float MargenBajoVisionPanelGrados = 2.4f;

        // Maquetación por medición (19-08): alturas MÍNIMAS de los bloques de texto; la real la
        // decide el texto (ver AjustarMaquetacion), y el panel crece si hace falta.
        private const float AltoMinimoInstruccionPx = 52f;
        private const float AltoMinimoEstadoPx = 26f;
        private const float AltoMinimoCalidadPx = 0f;
        private const float SeparacionBloquesPx = 8f;
        private const float AltoPiePx = 28f;

        // Este panel iba a 0,94 y el resto del visor a 0,91 o 0,92. Entra en el criterio
        // uniforme (MROpacidadInterfaz) porque se abre junto al menú del modo anclado, y ahí
        // la diferencia se ve sobre un vídeo de cámaras claro. El precio queda anotado: el
        // contraste de su texto normal baja de 5,13:1 a 4,22:1 en el peor caso, y la
        // corrección de eso es el color del texto, no la opacidad del fondo.
        private static readonly Color FondoPanel = MROpacidadInterfaz.ColorDeFondo;
        private static readonly Color FondoBoton = new Color(1f, 1f, 1f, 0.08f);
        private static readonly Color FondoBotonSenalado = new Color(1f, 0.82f, 0.2f, 0.35f);
        private static readonly Color FondoBotonInactivo = new Color(1f, 1f, 1f, 0.03f);
        private static readonly Color TextoNormal = new Color(0.90f, 0.92f, 0.96f, 1f);
        private static readonly Color TextoTenue = new Color(0.62f, 0.66f, 0.72f, 1f);
        private static readonly Color TextoInactivo = new Color(0.45f, 0.48f, 0.52f, 1f);
        private static readonly Color Ambar = new Color(1f, 0.82f, 0.2f, 1f);
        private static readonly Color Verde = new Color(0.45f, 0.90f, 0.50f, 1f);
        private static readonly Color Rojo = new Color(1f, 0.45f, 0.40f, 1f);
        private static readonly Color Azul = new Color(0.55f, 0.80f, 1f, 1f);

        private enum Fase { Inactivo, Registrando, Confirmado, Restaurado, SinSoporte }

        private class Estacion
        {
            public IfcMetadata Elemento;
            public string Etiqueta;
            public bool Tomada;
            public bool Saltada;
        }

        private class Boton
        {
            public string Id;
            public Text Texto;
            public Image Fondo;
            public BoxCollider Volumen;
            public RectTransform Rect;
            public int Fila;
            public bool Activo = true;
            public bool Senalado;
        }

        private struct Muestra
        {
            public float T;
            public Vector3 Punto;
            public float AlturaMando;
            public bool Valida;
        }

        // --- Dependencias -----------------------------------------------------------------
        private MRControllerRig _rig;
        private Camera _camara;
        private SceneModelIndex _index;
        private MRAnchorService _anclaje;
        private ModelAnchorBinder _binder;
        private Transform _origenXR;

        // --- Estado del registro ----------------------------------------------------------
        private readonly MRRegistroPorPuntos _registro = new MRRegistroPorPuntos();
        private MRRegistroPorPuntos.Resultado _ultimo;
        private readonly List<Estacion> _candidatas = new List<Estacion>();
        private readonly List<Estacion> _plan = new List<Estacion>();
        private Fase _fase = Fase.Inactivo;
        private string _mensajePersistencia = string.Empty;
        private float _ocultarAvisoEn = -1f;
        private bool _referenciaFijada;

        // --- Muestreo del gesto -----------------------------------------------------------
        private readonly List<Muestra> _muestras = new List<Muestra>(64);
        private bool _impactoSueloValido;
        private Vector3 _impactoSuelo;
        private float _distanciaImpacto;
        private float _alturaMando;

        // --- Interfaz ---------------------------------------------------------------------
        private RectTransform _raiz;
        /// <summary>Volumen del panel entero: una pulsación sobre su fondo (o sobre un botón
        /// inactivo) no debe atravesarlo y tomar un punto de suelo detrás.</summary>
        private BoxCollider _volumenPanel;
        private Text _titulo, _instruccion, _estado, _calidad;
        private readonly List<Boton> _botones = new List<Boton>();
        private RectTransform _plano;
        private readonly Dictionary<Estacion, (Image fondo, Text numero)> _marcasPlano =
            new Dictionary<Estacion, (Image, Text)>();
        private RectTransform _marcaUsuario;
        private Vector2 _planoMin, _planoMax;   // extensión del modelo en coordenadas locales de la raíz (x, z)
        private GameObject _cursorSuelo;
        private Image _cursorImagen;
        private readonly List<GameObject> _banderas = new List<GameObject>();
        /// <summary>Colocador del panel ante el usuario: el de la ficha de metadatos, en su
        /// variante sin línea ni volumen (ver las constantes de seguimiento).</summary>
        private DigitalTwin.Metadata.WorldPanelPlacer _colocador;
        private RectTransform _pie;
        private float _altoActualPx = AltoPx;
        private string _textoEstadoMaquetado;

        /// <summary>Verdadero mientras el panel está abierto: el controlador de interacción
        /// cede el rayo (mismo contrato que <see cref="MRMenuZonas.Abierto"/>).</summary>
        public bool CapturaElRayo => _raiz != null && _raiz.gameObject.activeSelf;

        /// <summary>Fotograma en que este panel consumió el botón primario (para cerrarse).
        /// El menú del modo anclado lo consulta para no abrirse con esa misma pulsación:
        /// el orden de ejecución entre ambos componentes no está garantizado.</summary>
        public int FotogramaBotonConsumido { get; private set; } = -1;

        /// <summary>Reabre el panel desde el menú del modo anclado (desde la ronda 9 el botón
        /// primario abre el menú, no este panel; el panel sigue abriéndose solo cuando el
        /// registro lo exige).</summary>
        public void AbrirPanel()
        {
            Abrir(); // Abrir ya refresca el contenido con la fase vigente
            Debug.LogWarning("[DigitalTwin][MR] Panel de anclaje reabierto desde el menu.");
        }

        /// <summary>Rehacer el anclaje desde el menú: olvida el guardado y reinicia el registro
        /// (misma acción que el botón «Recolocar desde cero» del propio panel).</summary>
        public void RehacerAnclaje() => RecolocarDesdeCero();

        /// <summary>
        /// Una línea, para el aviso de confirmación de «Volver al selector de modo» del menú
        /// del modo anclado: QUÉ trabajo de ESTA sesión se perdería al abandonar el modo ahora,
        /// calculado con el estado real del registro y de la persistencia, no una frase fija.
        /// Volver al selector conserva el anclaje persistido en el visor (decisión de la ronda
        /// 9), así que cuando el anclaje vigente está guardado se dice que no se pierde nada;
        /// solo cuenta como pérdida lo que no está en el visor: los puntos tomados de un
        /// registro en curso, un registro confirmado cuya persistencia no ha llegado a
        /// «Guardado», o el registro aplicado sin soporte de anclaje (Editor). Debe caber en
        /// una línea del pie del menú (~55 caracteres a 14 px).
        /// </summary>
        public string AvisoAlAbandonarElModo()
        {
            bool guardado = _anclaje != null
                            && _anclaje.Estado == MRAnchorService.EstadoAnclaje.Anclado
                            && _anclaje.Persistencia == MRAnchorService.EstadoPersistencia.Guardado;
            switch (_fase)
            {
                case Fase.Registrando:
                {
                    int n = _registro.Cuenta;
                    if (n == 0) return "Sin puntos tomados: no se pierde nada";
                    return n == 1 ? "Se pierde el punto tomado en esta sesión"
                                  : $"Se pierden los {n} puntos tomados en esta sesión";
                }
                case Fase.Confirmado:
                    return guardado
                        ? "El anclaje guardado se conserva y se restaura al volver"
                        : "Se pierde el registro de esta sesión: no está guardado";
                case Fase.Restaurado:
                    return "El anclaje guardado se conserva y se restaura al volver";
                case Fase.SinSoporte:
                    return "Se pierde el registro de esta sesión (sin anclaje aquí)";
                default:
                    return guardado
                        ? "El anclaje guardado se conserva y se restaura al volver"
                        : "Se pierde el registro no guardado de esta sesión";
            }
        }

        public void Initialize(MRControllerRig rig, Camera camara, SceneModelIndex index,
                               MRAnchorService anclaje, ModelAnchorBinder binder, Transform origenXR)
        {
            _rig = rig;
            _camara = camara;
            _index = index;
            _anclaje = anclaje;
            _binder = binder;
            _origenXR = origenXR;

            ConstruirCandidatas();
            PlanificarEstaciones();
            ConstruirPanel();
            ConstruirCursorDeSuelo();

            anclaje.OnEstadoCambiado += AlCambiarEstadoAnclaje;
            anclaje.OnAnclado += AlAnclar;
            anclaje.OnPersistencia += AlPersistir;

            Debug.LogWarning($"[DigitalTwin][MR] Interfaz de colocacion lista: {_candidatas.Count} puertas " +
                             $"candidatas, plan de {_plan.Count} estaciones; minimo {PuntosMinimos}, " +
                             $"recomendado {PuntosRecomendados}. Suelo fisico (mundo) en y=" +
                             $"{AlturaSueloFisico():0.000}, inclinacion del suelo de seguimiento " +
                             $"respecto al mundo {(_origenXR != null ? Vector3.Angle(_origenXR.up, Vector3.up) : 0f):0.00}° " +
                             "(debe ser 0,00: el bootstrap nivela el origen). A/X (M en el Editor) la " +
                             "oculta; se reabre desde el menu del modo anclado.");
        }

        // ==================================================================================
        //  Estaciones
        // ==================================================================================

        private float AlturaSueloFisico() => _origenXR != null ? _origenXR.position.y : 0f;

        /// <summary>
        /// EL PLANO DEL SUELO FÍSICO, en mundo: el plano y=0 del espacio de seguimiento, que pasa
        /// por la posición del origen de realidad extendida con la normal «arriba» DEL ORIGEN.
        /// Hasta el 19-08 el suelo se tomaba como el plano horizontal de mundo a la cota del
        /// origen; era lo mismo solo si el origen no estaba inclinado, y el de ARScene tenía
        /// 4,16° de cabeceo: el rayo cortaba un plano que se apartaba del suelo real 7,3 cm por
        /// metro (el registro del 19-08 guarda poses de anclaje a y=-0,04 y -0,30 m de
        /// seguimiento para puntos que debían estar en el suelo). El bootstrap nivela hoy el
        /// origen, así que ambos planos coinciden; este método es la definición correcta en
        /// cualquier caso y deja de depender de que nadie vuelva a inclinarlo.
        /// </summary>
        private Plane PlanoSueloFisico() =>
            _origenXR != null ? new Plane(_origenXR.up, _origenXR.position) : new Plane(Vector3.up, Vector3.zero);

        /// <summary>Punto de suelo de un elemento, en mundo: la misma regla que
        /// <see cref="PosicionDeNodos"/> pero a ras de suelo (sin sumar la altura de nodo).</summary>
        private static Vector3 PuntoDeSueloMundo(IfcMetadata meta, out string regla)
        {
            if (meta.ifcType == "IfcDoor")
            {
                var r = meta.GetComponentInChildren<Renderer>();
                if (r != null)
                {
                    regla = "base del volumen de la puerta";
                    return new Vector3(r.bounds.center.x, r.bounds.min.y, r.bounds.center.z);
                }
                regla = "origen de la puerta (sin renderer)";
                return meta.transform.position;
            }
            regla = $"altura de autor del punto de vista menos {PosicionDeNodos.AlturaAutorPuntosDeVista:0.00} m";
            Vector3 p = meta.transform.position;
            return new Vector3(p.x, p.y - PosicionDeNodos.AlturaAutorPuntosDeVista, p.z);
        }

        private void ConstruirCandidatas()
        {
            _candidatas.Clear();
            var puertas = new List<IfcMetadata>();
            foreach (var m in _index.AllElements)
            {
                if (m == null || m.ifcType != "IfcDoor" || SceneModelIndex.EsDefinicionDeTipo(m.ifcType))
                    continue;
                if (m.GetComponentInChildren<Renderer>() == null) continue;
                puertas.Add(m);
            }
            // Orden estable por GlobalId, como el generador del grafo, para que el plan sea
            // reproducible entre ejecuciones.
            puertas.Sort((a, b) => string.CompareOrdinal(a.globalId, b.globalId));

            foreach (var p in puertas)
            {
                Vector3 s = PuntoDeSueloMundo(p, out _);
                bool duplicada = false;
                foreach (var c in _candidatas)
                {
                    Vector3 o = PuntoDeSueloMundo(c.Elemento, out _);
                    if (DistanciaHorizontal(s, o) < DistanciaMinimaEntrePuertas) { duplicada = true; break; }
                }
                if (duplicada) continue;   // hojas dobles o puertas pegadas: una sola estación
                _candidatas.Add(new Estacion { Elemento = p, Etiqueta = DescribirPuerta(p) });
            }

            if (_candidatas.Count == 0)
            {
                // Modelo sin puertas: se cae al punto de vista de referencia del binder, que es
                // el respaldo de siempre (menos preciso: nada físico lo marca).
                var refMeta = _binder != null ? _binder.Referencia : null;
                if (refMeta != null)
                    _candidatas.Add(new Estacion
                    {
                        Elemento = refMeta,
                        Etiqueta = $"punto de vista '{Sala(refMeta)}' (sin puertas en el modelo)"
                    });
                Debug.LogWarning("[DigitalTwin][MR] El modelo no tiene puertas utilizables como estaciones; " +
                                 "se registra con el punto de vista de referencia (menos preciso).");
            }
        }

        private static float DistanciaHorizontal(Vector3 a, Vector3 b)
        {
            a.y = 0f; b.y = 0f;
            return Vector3.Distance(a, b);
        }

        private static string Sala(IfcMetadata m)
        {
            string s = m != null ? m.GetValue("Otros", "LOC_Localizacion4") : null;
            return string.IsNullOrEmpty(s) ? "?" : s.Trim('"');
        }

        /// <summary>
        /// Describe una puerta por las salas que separa: la sala del punto de vista más cercano
        /// a cada lado de su plano. Las puertas del modelo no declaran sala (LOC_Localizacion4
        /// vacío) y su nombre IFC es de catálogo, así que esto es lo que un operario puede
        /// reconocer. Se prueba el eje frontal y, si no separa salas distintas, el lateral (los
        /// importadores no garantizan qué eje local es la normal de la puerta); si ninguno
        /// separa, se dan las dos salas más cercanas.
        /// </summary>
        private string DescribirPuerta(IfcMetadata puerta)
        {
            Vector3 centro = PuntoDeSueloMundo(puerta, out _);
            foreach (var eje in new[] { puerta.transform.forward, puerta.transform.right })
            {
                Vector3 n = Vector3.ProjectOnPlane(eje, Vector3.up);
                if (n.sqrMagnitude < 1e-4f) continue;
                n.Normalize();
                string ladoA = SalaMasCercana(centro, n, +1f, out float dA);
                string ladoB = SalaMasCercana(centro, n, -1f, out float dB);
                if (ladoA != null && ladoB != null && ladoA != ladoB)
                    return $"puerta entre {ladoA} y {ladoB}";
            }
            // Ningún eje separa dos salas distintas: las dos más cercanas, en orden.
            var cercanas = new List<(float d, string sala)>();
            foreach (var pv in _index.NavPoints)
            {
                if (pv == null) continue;
                string s = Sala(pv);
                if (s == "?") continue;
                float d = DistanciaHorizontal(pv.transform.position, centro);
                if (d > RadioBusquedaSala) continue;
                bool ya = false;
                foreach (var c in cercanas) if (c.sala == s) { ya = true; break; }
                if (!ya) cercanas.Add((d, s));
            }
            cercanas.Sort((a, b) => a.d.CompareTo(b.d));
            if (cercanas.Count >= 2) return $"puerta junto a {cercanas[0].sala} y {cercanas[1].sala}";
            if (cercanas.Count == 1) return $"puerta de {cercanas[0].sala}";
            return "puerta (sin sala cercana conocida)";
        }

        private string SalaMasCercana(Vector3 centro, Vector3 normal, float signo, out float distancia)
        {
            string mejor = null;
            distancia = float.MaxValue;
            foreach (var pv in _index.NavPoints)
            {
                if (pv == null) continue;
                Vector3 d = pv.transform.position - centro;
                d.y = 0f;
                if (Vector3.Dot(d, normal) * signo <= 0.05f) continue;   // ese lado y no en el plano
                float dist = d.magnitude;
                if (dist > RadioBusquedaSala || dist >= distancia) continue;
                string s = Sala(pv);
                if (s == "?") continue;
                mejor = s;
                distancia = dist;
            }
            return mejor;
        }

        /// <summary>
        /// Plan de estaciones con la regla de dispersión de West et al. (2001): primera junto a
        /// la entrada (la puerta más cercana al punto de referencia del binder, el Recibidor por
        /// defecto), segunda la más lejana de la primera, tercera la de mayor área de triángulo
        /// con las dos anteriores; a partir de ahí, la de mayor distancia mínima al conjunto.
        /// Las estaciones saltadas se excluyen; el plan se puede recomputar tras un salto.
        /// </summary>
        private void PlanificarEstaciones()
        {
            _plan.Clear();
            var disponibles = new List<Estacion>();
            foreach (var c in _candidatas) if (!c.Saltada) disponibles.Add(c);
            if (disponibles.Count == 0) return;

            Vector3 entrada = _binder != null && _binder.Referencia != null
                ? _binder.Referencia.transform.position
                : PuntoDeSueloMundo(disponibles[0].Elemento, out _);

            // Las ya tomadas mantienen su sitio; el resto se planifica detrás.
            var tomadas = new List<Estacion>();
            foreach (var c in disponibles) if (c.Tomada) tomadas.Add(c);
            _plan.AddRange(tomadas);
            disponibles.RemoveAll(e => e.Tomada);

            while (_plan.Count < PuntosMaximos && disponibles.Count > 0)
            {
                Estacion elegida = null;
                float mejor = float.MinValue;
                foreach (var cand in disponibles)
                {
                    Vector3 s = PuntoDeSueloMundo(cand.Elemento, out _);
                    float puntuacion;
                    if (_plan.Count == 0)
                        puntuacion = -DistanciaHorizontal(s, entrada);           // la más cercana a la entrada
                    else if (_plan.Count == 1)
                        puntuacion = DistanciaHorizontal(s, PuntoDeSueloMundo(_plan[0].Elemento, out _));
                    else if (_plan.Count == 2)
                        puntuacion = AreaTriangulo(PuntoDeSueloMundo(_plan[0].Elemento, out _),
                                                   PuntoDeSueloMundo(_plan[1].Elemento, out _), s);
                    else
                    {
                        puntuacion = float.MaxValue;
                        foreach (var e in _plan)
                            puntuacion = Mathf.Min(puntuacion,
                                DistanciaHorizontal(s, PuntoDeSueloMundo(e.Elemento, out _)));
                    }
                    if (puntuacion > mejor) { mejor = puntuacion; elegida = cand; }
                }
                if (elegida == null) break;
                _plan.Add(elegida);
                disponibles.Remove(elegida);
            }

            var texto = new System.Text.StringBuilder();
            for (int i = 0; i < _plan.Count; i++)
            {
                Vector3 s = PuntoDeSueloMundo(_plan[i].Elemento, out _);
                texto.Append($" {i + 1}: {_plan[i].Etiqueta} ({s.x:0.0}, {s.z:0.0}){(_plan[i].Tomada ? " [tomada]" : "")};");
            }
            Debug.LogWarning($"[DigitalTwin][MR] Plan de estaciones de registro:{texto} — lo mas " +
                             "separadas que el edificio permita.");
        }

        private static float AreaTriangulo(Vector3 a, Vector3 b, Vector3 c)
        {
            float abx = b.x - a.x, abz = b.z - a.z, acx = c.x - a.x, acz = c.z - a.z;
            return 0.5f * Mathf.Abs(abx * acz - abz * acx);
        }

        private Estacion EstacionPendiente()
        {
            for (int i = 0; i < _plan.Count; i++)
                if (!_plan[i].Tomada) return _plan[i];
            return null;
        }

        // ==================================================================================
        //  Reacciones al servicio de anclaje
        // ==================================================================================

        private void AlCambiarEstadoAnclaje(MRAnchorService.EstadoAnclaje estado)
        {
            switch (estado)
            {
                case MRAnchorService.EstadoAnclaje.EsperandoColocacion:
                    // Sin anclaje guardado, o tras olvidarlo: se abre el registro sin preguntar
                    // qué se quiere hacer, porque no hay otra cosa que hacer.
                    if (_fase != Fase.Registrando) EmpezarRegistro("sin anclaje guardado");
                    break;
                case MRAnchorService.EstadoAnclaje.NoSoportado:
                    // Editor o extensión ausente: el registro funciona, la persistencia no.
                    if (_fase != Fase.Registrando) EmpezarRegistro("anclaje espacial no soportado en este entorno");
                    break;
                case MRAnchorService.EstadoAnclaje.Error:
                    _estado.text = "Error del anclaje: " + _anclaje.UltimoError;
                    _estado.color = Rojo;
                    Abrir();
                    break;
            }
        }

        private void AlAnclar(Pose poseSeguimiento, string referencia)
        {
            if (_anclaje.AnclajeRestaurado)
            {
                _fase = Fase.Restaurado;
                _registro.Vaciar();
                _ultimo = null;
                LimpiarBanderas();
                RefrescarPanel();
                Abrir();
                _ocultarAvisoEn = Time.time + SegundosAvisoBreve;
                Debug.LogWarning("[DigitalTwin][MR] Anclaje restaurado sin preguntar; el aviso se retira solo. " +
                                 "Recolocar: menu (A/X) > 'Rehacer el anclaje'.");
            }
            else
            {
                _fase = Fase.Confirmado;
                RefrescarPanel();
                _ocultarAvisoEn = Time.time + SegundosAvisoBreve;
            }
        }

        private void AlPersistir(bool ok, string detalle)
        {
            _mensajePersistencia = detalle;
            RefrescarPanel();
            if (_fase == Fase.Confirmado) _ocultarAvisoEn = Time.time + SegundosAvisoBreve;
        }

        // ==================================================================================
        //  Flujo
        // ==================================================================================

        private void EmpezarRegistro(string motivo)
        {
            _fase = Fase.Registrando;
            _registro.Vaciar();
            _ultimo = null;
            _mensajePersistencia = string.Empty;
            _referenciaFijada = false;
            foreach (var c in _candidatas) { c.Tomada = false; c.Saltada = false; }
            PlanificarEstaciones();
            LimpiarBanderas();
            _ocultarAvisoEn = -1f;
            RefrescarPanel();
            Abrir();
            Debug.LogWarning($"[DigitalTwin][MR] Registro por puntos iniciado ({motivo}).");
        }

        private void TomarPunto(Muestra muestra, Vector3 impactoAlPulsar, float alturaMandoAlPulsar)
        {
            var estacion = EstacionPendiente();
            if (estacion == null) return;

            Transform raiz = _binder.RaizModelo;
            Vector3 modeloMundo = PuntoDeSueloMundo(estacion.Elemento, out string regla);
            Vector3 fisico = muestra.Punto;
            // El punto físico se proyecta sobre el plano del suelo de seguimiento (el promedio de
            // muestras ya está en él por construcción: la corrección mide cuánto se apartó, y se
            // registra). Desde el 19-08 el plano es el del seguimiento, no el horizontal de mundo.
            Plane sueloFisico = PlanoSueloFisico();
            float correccionVertical = -sueloFisico.GetDistanceToPoint(fisico);
            fisico = sueloFisico.ClosestPointOnPlane(fisico);

            var c = new MRRegistroPorPuntos.Correspondencia
            {
                Etiqueta = estacion.Etiqueta,
                GlobalId = estacion.Elemento.globalId,
                PuntoModeloLocal = raiz.InverseTransformPoint(modeloMundo),
                PuntoFisicoMundo = fisico
            };
            _registro.Anadir(c);
            estacion.Tomada = true;

            float desplazamientoAlPulsar = Vector3.Distance(impactoAlPulsar, muestra.Punto);
            int numero = _registro.Cuenta;
            Debug.LogWarning($"[DigitalTwin][MR] Punto {numero}: '{estacion.Etiqueta}' (GlobalId " +
                             $"{estacion.Elemento.globalId}). MODELO: suelo del elemento en y={modeloMundo.y:0.000} " +
                             $"(mundo; regla: {regla}). FISICO: mando a {muestra.AlturaMando:0.00} m de altura, " +
                             $"impacto en el suelo de seguimiento y={muestra.Punto.y:0.000}, a {_distanciaImpacto:0.00} m " +
                             $"del mando; muestra tomada {(Time.time - muestra.T) * 1000f:0} ms antes del gatillo, " +
                             $"desplazada {desplazamientoAlPulsar * 100f:0.0} cm respecto al instante de pulsar " +
                             $"(mando a {alturaMandoAlPulsar:0.00} m). CORRECCION VERTICAL aplicada al punto " +
                             $"fisico: {correccionVertical * 100f:0.0} cm (suelo con suelo).");

            if (!_referenciaFijada)
            {
                // La primera estación tomada es la referencia del anclaje: es donde el operario
                // estuvo físicamente y donde el mapa del entorno es más rico.
                _referenciaFijada = _binder.UsarReferencia(estacion.Elemento.globalId);
                if (!_referenciaFijada)
                    Debug.LogWarning("[DigitalTwin][MR] No se ha podido fijar la primera estacion como " +
                                     "referencia del anclaje; se usara la de respaldo del binder.");
            }

            ResolverYAplicar();
            ColocarBandera(fisico, numero);
            RefrescarPanel();
        }

        private void ResolverYAplicar()
        {
            _ultimo = _registro.Resolver(_binder.RaizModelo);
            if (_ultimo == null) return;

            _binder.AplicarMovimientoRigido(_ultimo.Giro, _ultimo.Traslacion);
            // Los residuos se calcularon antes de mover; tras aplicar, el ajuste es el mismo.

            string peor = _ultimo.IndicePeor >= 0 ? _registro.Puntos[_ultimo.IndicePeor].Etiqueta : "-";
            string salaLejana = SalaMasLejana(out Vector3 objetivo);
            float tre = _registro.EstimarErrorEnObjetivo(_binder.RaizModelo, objetivo);
            Debug.LogWarning($"[DigitalTwin][MR] Ajuste con {_ultimo.NumeroDePuntos} punto(s): giro " +
                             $"{_ultimo.GiroGrados:0.00}° ({(_ultimo.GiroMedido ? "MEDIDO" : "NO medido: se conserva el del modelo")}), " +
                             $"traslacion {_ultimo.Traslacion}; residuo RMS {_ultimo.ResiduoRms * 100f:0.0} cm, " +
                             $"maximo {_ultimo.ResiduoMaximo * 100f:0.0} cm en '{peor}'; grados de libertad " +
                             $"{_ultimo.GradosDeLibertad}; dispersion de los puntos f={_registro.DispersionHorizontal():0.0} m; " +
                             $"error esperado en la sala mas lejana ({salaLejana}) ≈ " +
                             $"{(tre >= 0f ? (tre * 100f).ToString("0") + " cm" : "no estimable con un punto")}. " +
                             $"Guiñada del modelo ahora {_binder.GuinadaActualDelModelo():0.0}°.");
        }

        private string SalaMasLejana(out Vector3 objetivo)
        {
            objetivo = Vector3.zero;
            string sala = "?";
            Vector3 c = Vector3.zero;
            int n = 0;
            foreach (var p in _registro.Puntos) { c += p.PuntoFisicoMundo; n++; }
            if (n == 0) return sala;
            c /= n;
            float mejor = -1f;
            foreach (var pv in _index.NavPoints)
            {
                if (pv == null) continue;
                float d = DistanciaHorizontal(pv.transform.position, c);
                if (d > mejor) { mejor = d; objetivo = pv.transform.position; sala = Sala(pv); }
            }
            return sala;
        }

        private void DeshacerUltimo()
        {
            if (_registro.Cuenta == 0) return;
            var ultimo = _registro.Puntos[_registro.Cuenta - 1];
            foreach (var e in _plan)
                if (e.Elemento.globalId == ultimo.GlobalId) e.Tomada = false;
            _registro.QuitarUltimo();
            if (_banderas.Count > 0)
            {
                Destroy(_banderas[_banderas.Count - 1]);
                _banderas.RemoveAt(_banderas.Count - 1);
            }
            Debug.LogWarning($"[DigitalTwin][MR] Punto deshecho: '{ultimo.Etiqueta}'. Quedan {_registro.Cuenta}.");
            if (_registro.Cuenta > 0) ResolverYAplicar(); else _ultimo = null;
            RefrescarPanel();
        }

        private void SaltarEstacion()
        {
            var e = EstacionPendiente();
            if (e == null) return;
            e.Saltada = true;
            Debug.LogWarning($"[DigitalTwin][MR] Estacion saltada: '{e.Etiqueta}'. Se replanifica.");
            PlanificarEstaciones();
            RefrescarPanel();
        }

        private void Terminar()
        {
            if (_registro.Cuenta == 0) return;

            if (!_binder.TryPoseDeReferenciaEnSeguimiento(out Pose poseSeguimiento))
            {
                _estado.text = "No se ha podido calcular la pose de la referencia.";
                _estado.color = Rojo;
                return;
            }
            string referencia = _binder.Referencia != null ? _binder.Referencia.globalId : string.Empty;
            string calidad = _ultimo != null && _ultimo.GiroMedido
                ? $"residuo RMS {_ultimo.ResiduoRms * 100f:0.0} cm con {_ultimo.NumeroDePuntos} puntos"
                : "UN solo punto: orientacion NO medida";
            Debug.LogWarning($"[DigitalTwin][MR] Registro confirmado ({calidad}); referencia " +
                             $"'{(_binder.Referencia != null ? _binder.Referencia.ifcName : "?")}' " +
                             $"(GlobalId {referencia}); pose de anclaje (seguimiento) posicion " +
                             $"{poseSeguimiento.position}, guiñada {poseSeguimiento.rotation.eulerAngles.y:0.0}°. " +
                             "Se solicita el anclaje.");

            if (_anclaje.Estado == MRAnchorService.EstadoAnclaje.NoSoportado)
            {
                // Sin anclaje: el registro queda aplicado y se dice; no hay nada que persistir.
                _fase = Fase.SinSoporte;
                _mensajePersistencia = "sin anclaje espacial en este entorno: el registro vale solo para esta sesion";
                RefrescarPanel();
                _ocultarAvisoEn = Time.time + SegundosAvisoBreve;
                return;
            }

            _mensajePersistencia = "guardando...";
            _anclaje.ColocarEnPose(poseSeguimiento, referencia);
            // La confirmación llega por OnAnclado / OnPersistencia.
        }

        private void RecolocarDesdeCero()
        {
            Debug.LogWarning("[DigitalTwin][MR] Recolocar desde cero: se olvida el anclaje y se reinicia el registro.");
            // Se sale de la fase actual ANTES de olvidar: si el servicio cambia de estado, su
            // evento arranca el registro; si no cambia (ya estaba esperando, o no hay soporte),
            // se arranca aquí. En ningún caso se arranca dos veces.
            _fase = Fase.Inactivo;
            _anclaje.OlvidarAnclaje();
            if (_fase != Fase.Registrando) EmpezarRegistro("recolocacion pedida por el usuario");
        }

        // ==================================================================================
        //  Update: botón de menú, gesto, panel
        // ==================================================================================

        private void Update()
        {
            if (_rig == null || _raiz == null) return;

            // Desde la ronda 9 el botón primario pertenece al menú del modo anclado (mismo
            // gesto que en navegación). Este panel solo lo consume para OCULTARSE cuando está
            // abierto; reabrirlo es una fila de ese menú. Se anota el fotograma para que el
            // menú no interprete la misma pulsación como orden de abrirse.
            if (_rig.BotonMenuPulsadoEsteFrame() && CapturaElRayo)
            {
                FotogramaBotonConsumido = Time.frameCount;
                Cerrar();
            }

            if (!CapturaElRayo)
            {
                if (_cursorSuelo != null && _cursorSuelo.activeSelf) _cursorSuelo.SetActive(false);
                return;
            }

            if (_ocultarAvisoEn > 0f && Time.time > _ocultarAvisoEn)
            {
                _ocultarAvisoEn = -1f;
                Cerrar();
                return;
            }

            ActualizarMarcaUsuario();

            bool esperandoPunto = _fase == Fase.Registrando && EstacionPendiente() != null
                                  && _registro.Cuenta < PuntosMaximos;

            // Rayo: primero los botones del panel; si no, el suelo.
            _impactoSueloValido = false;
            Boton senalado = null;
            float distBoton = float.MaxValue;
            bool sobrePanel = false;
            float distPanel = 0f;
            bool hayRayo = _rig.TryGetRayo(out Ray rayo);
            if (hayRayo)
            {
                foreach (var b in _botones)
                {
                    b.Senalado = false;
                    if (!b.Activo || b.Volumen == null || !b.Volumen.gameObject.activeInHierarchy) continue;
                    if (b.Volumen.Raycast(rayo, out RaycastHit hit, 10f) && hit.distance < distBoton)
                    {
                        distBoton = hit.distance;
                        senalado = b;
                    }
                }
                if (senalado != null) senalado.Senalado = true;

                // El fondo del panel (o un botón inactivo) absorbe el rayo: no se toma un punto
                // de suelo a través de la interfaz.
                if (senalado == null && _volumenPanel != null &&
                    _volumenPanel.Raycast(rayo, out RaycastHit hitPanel, 10f))
                {
                    sobrePanel = true;
                    distPanel = hitPanel.distance;
                }

                if (senalado == null && !sobrePanel && esperandoPunto)
                {
                    // Corte del rayo con el SUELO DE SEGUIMIENTO real (ver PlanoSueloFisico),
                    // no con un plano horizontal de mundo: con el origen nivelado son el mismo
                    // plano; si no lo estuviera, este es el suelo que pisa el operario.
                    Plane suelo = PlanoSueloFisico();
                    bool apuntaHaciaAbajo = Vector3.Dot(rayo.direction, -suelo.normal) > 0.05f;
                    if (apuntaHaciaAbajo && suelo.Raycast(rayo, out float t) && t > 0f)
                    {
                        _impactoSuelo = rayo.GetPoint(t);
                        _distanciaImpacto = t;
                        _alturaMando = suelo.GetDistanceToPoint(rayo.origin);
                        _impactoSueloValido = true;
                    }
                }
            }
            RegistrarMuestra();

            foreach (var b in _botones) PintarBoton(b);

            bool enRango = _impactoSueloValido && _distanciaImpacto <= DistanciaMaximaAlSuelo;
            if (_cursorSuelo != null)
            {
                _cursorSuelo.SetActive(_impactoSueloValido);
                if (_impactoSueloValido)
                {
                    _cursorSuelo.transform.position = _impactoSuelo + Vector3.up * 0.005f;
                    _cursorImagen.color = enRango ? Verde : Rojo;
                }
            }
            if (esperandoPunto)
            {
                _estado.text = _impactoSueloValido
                    ? (enRango ? $"Rayo sobre el suelo a {_distanciaImpacto:0.0} m: aprieta el gatillo."
                               : $"Rayo sobre el suelo a {_distanciaImpacto:0.0} m: acercate a menos de {DistanciaMaximaAlSuelo:0.0} m.")
                    : "Apunta con el mando al centro del umbral, en el suelo.";
                _estado.color = _impactoSueloValido ? (enRango ? Verde : Ambar) : TextoNormal;
            }
            // El estado cambia cada fotograma mientras se apunta (la distancia): se remaqueta
            // solo si el texto ha cambiado, y el coste es medir tres textos.
            if (_estado.text != _textoEstadoMaquetado) AjustarMaquetacion();

            if (senalado != null) _rig.MostrarImpacto(distBoton, true);
            else if (sobrePanel) _rig.MostrarImpacto(distPanel, false);
            else if (_impactoSueloValido) _rig.MostrarImpacto(_distanciaImpacto, enRango);
            else _rig.MostrarImpacto(0f, false);

            if (!_rig.GatilloPulsadoEsteFrame()) return;

            if (senalado != null) { Accionar(senalado.Id); return; }
            if (sobrePanel) return;   // fondo del panel o botón inactivo: la pulsación se absorbe

            if (esperandoPunto && _impactoSueloValido)
            {
                if (!enRango)
                {
                    Debug.LogWarning($"[DigitalTwin][MR] Punto rechazado: el impacto esta a {_distanciaImpacto:0.00} m " +
                                     $"del mando (maximo {DistanciaMaximaAlSuelo:0.0}). Acercate.");
                    return;
                }
                var muestra = MuestraAnteriorAlGatillo();
                TomarPunto(muestra, _impactoSuelo, _alturaMando);
            }
        }

        private void RegistrarMuestra()
        {
            _muestras.Add(new Muestra
            {
                T = Time.time, Punto = _impactoSuelo, AlturaMando = _alturaMando, Valida = _impactoSueloValido
            });
            // Se conserva medio segundo de historia.
            while (_muestras.Count > 0 && Time.time - _muestras[0].T > 0.5f) _muestras.RemoveAt(0);
        }

        /// <summary>Promedio de las muestras válidas de la ventana anterior al flanco (ver
        /// cabecera); si no hay ninguna, la actual.</summary>
        private Muestra MuestraAnteriorAlGatillo()
        {
            float ahora = Time.time;
            Vector3 suma = Vector3.zero;
            float sumaAltura = 0f, tMasReciente = -1f;
            int n = 0;
            foreach (var m in _muestras)
            {
                float edad = ahora - m.T;
                if (!m.Valida || edad < RetardoMuestraSegundos || edad > VentanaMuestraSegundos) continue;
                suma += m.Punto; sumaAltura += m.AlturaMando; n++;
                if (m.T > tMasReciente) tMasReciente = m.T;
            }
            if (n == 0)
                return new Muestra { T = ahora, Punto = _impactoSuelo, AlturaMando = _alturaMando, Valida = true };
            return new Muestra { T = tMasReciente, Punto = suma / n, AlturaMando = sumaAltura / n, Valida = true };
        }

        private void Accionar(string id)
        {
            switch (id)
            {
                case "deshacer": DeshacerUltimo(); break;
                case "saltar": SaltarEstacion(); break;
                case "terminar": Terminar(); break;
                case "recolocar": RecolocarDesdeCero(); break;
                case "cerrar": Cerrar(); break;
                case "reintentar":
                    _mensajePersistencia = string.Empty;
                    Terminar();
                    break;
            }
        }

        // ==================================================================================
        //  Panel
        // ==================================================================================

        private void ConstruirPanel()
        {
            var canvas = DigitalTwin.UI.RuntimeUIFactory.CreateWorldCanvas(
                "~ColocacionAnclajeAR", anchoPx: AnchoPx, altoPx: AltoPx, anchoMetros: AnchoMetros);
            _raiz = (RectTransform)canvas.transform;
            _raiz.SetParent(transform, true);
            var material = MRIndicadoresDestino.MaterialSiempreVisible();

            // Seguimiento perezoso: el mismo componente que coloca la ficha de metadatos, en su
            // variante sin línea ni volumen de bloqueo (este panel trae el suyo y resuelve su
            // rayo aquí). Holgura y retardo mayores que los de la ficha; razón y cifras en las
            // constantes de seguimiento de la cabecera.
            _colocador = gameObject.AddComponent<DigitalTwin.Metadata.WorldPanelPlacer>();
            _colocador.DistanciaAlUsuario = DistanciaPanel;
            _colocador.ZonaMuertaGrados = ZonaMuertaPanelGrados;
            _colocador.Suavizado = SuavizadoPanel;
            _colocador.MargenBajoVisionGrados = MargenBajoVisionPanelGrados;
            _colocador.Initialize(canvas, conLineaYVolumen: false);

            var fondo = DigitalTwin.UI.RuntimeUIFactory.CreatePanel(_raiz, "Fondo", FondoPanel);
            DigitalTwin.UI.RuntimeUIFactory.StretchToParent((RectTransform)fondo.transform);
            if (material != null) fondo.material = material;

            _volumenPanel = _raiz.gameObject.AddComponent<BoxCollider>();
            _volumenPanel.isTrigger = true;
            _volumenPanel.size = new Vector3(AnchoPx, AltoPx, 1f);
            _volumenPanel.center = new Vector3((0.5f - _raiz.pivot.x) * AnchoPx,
                                               (0.5f - _raiz.pivot.y) * AltoPx, 0f);

            _titulo = TextoEn(_raiz, "Titulo", "Colocar el modelo sobre el edificio", 26, TextAnchor.MiddleLeft,
                              Color.white, FontStyle.Bold, MargenPx, MargenPx, AnchoPx - 2 * MargenPx, 40f, material);

            float xTexto = MargenPx, y = MargenPx + 48f;
            _instruccion = TextoEn(_raiz, "Instruccion", "", 22, TextAnchor.UpperLeft, TextoNormal, FontStyle.Normal,
                                   xTexto, y, AnchoColumnaTexto, 190f, material);
            y += 196f;
            _estado = TextoEn(_raiz, "Estado", "", 20, TextAnchor.UpperLeft, TextoNormal, FontStyle.Normal,
                              xTexto, y, AnchoColumnaTexto, 56f, material);
            y += 60f;
            _calidad = TextoEn(_raiz, "Calidad", "", 19, TextAnchor.UpperLeft, TextoTenue, FontStyle.Normal,
                               xTexto, y, AnchoColumnaTexto, 100f, material);
            y += 106f;

            // Botones en dos filas. Las alturas de arriba son las iniciales: AjustarMaquetacion
            // recoloca todo midiendo los textos en cuanto hay contenido.
            float anchoBoton = (AnchoColumnaTexto - 2 * 8f) / 3f;
            CrearBoton("deshacer", "Deshacer ultimo", xTexto, y, anchoBoton, material, fila: 0);
            CrearBoton("saltar", "Saltar esta puerta", xTexto + anchoBoton + 8f, y, anchoBoton, material, fila: 0);
            CrearBoton("terminar", "Terminar y anclar", xTexto + 2 * (anchoBoton + 8f), y, anchoBoton, material, fila: 0);
            y += AltoBotonPx + 8f;
            CrearBoton("recolocar", "Recolocar desde cero", xTexto, y, anchoBoton, material, fila: 1);
            CrearBoton("reintentar", "Reintentar guardado", xTexto + anchoBoton + 8f, y, anchoBoton, material, fila: 1);
            CrearBoton("cerrar", "Cerrar", xTexto + 2 * (anchoBoton + 8f), y, anchoBoton, material, fila: 1);
            y += AltoBotonPx + 10f;

            _pie = (RectTransform)TextoEn(_raiz, "Pie",
                "Gatillo: tomar el punto o pulsar un boton  ·  A o X: ocultar (se reabre desde el menu)",
                16, TextAnchor.MiddleLeft, TextoTenue, FontStyle.Normal,
                xTexto, AltoPx - MargenPx - AltoPiePx, AnchoPx - 2 * MargenPx, AltoPiePx, material).transform;

            ConstruirPlano(material);

            _raiz.gameObject.SetActive(false);
        }

        /// <summary>
        /// Maqueta la columna de texto MIDIENDO, y hace crecer el panel si hace falta.
        ///
        /// Los textos se crean con desbordamiento vertical (no se recortan: en un panel de
        /// registro es peor perder el final de un aviso que gastar píxeles), pero hasta el 19-08
        /// cada bloque tenía una altura fija pensada para el mensaje corto: instrucción 190 px,
        /// estado 56 px, calidad 100 px. Recorridos TODOS los mensajes que el código puede
        /// emitir, varios no caben: la instrucción del primer punto con una etiqueta de puerta
        /// larga (unos 300 caracteres a 22 px en 440 px de ancho = 8 líneas, 190 px son 7); el
        /// estado «Error del anclaje: …» (hasta 92 caracteres a 20 px = 3 líneas, 56 px son 2);
        /// y la calidad con tres puntos y sala lejana (unos 300 caracteres a 19 px = 7 líneas,
        /// 100 px son 4). El texto sobrante se pintaba encima del bloque siguiente y de los
        /// botones, que es lo que se vio el 19-08. Aquí cada bloque recibe la altura que su
        /// texto necesita (<c>preferredHeight</c>, que ya tiene en cuenta el ajuste de línea al
        /// ancho de la columna), los siguientes se recolocan debajo, los botones y el pie bajan
        /// con ellos, y si la suma supera la altura base el panel entero crece —lienzo, volumen
        /// del rayo y altura derivada del colocador—. Mismo principio que RecolocarCabecera en
        /// la ficha de metadatos.
        /// </summary>
        private void AjustarMaquetacion()
        {
            if (_raiz == null || _instruccion == null || _estado == null || _calidad == null) return;

            float y = MargenPx + 48f;
            y += ColocarBloque(_instruccion, y, AltoMinimoInstruccionPx) + SeparacionBloquesPx;
            y += ColocarBloque(_estado, y, AltoMinimoEstadoPx) + SeparacionBloquesPx;
            y += ColocarBloque(_calidad, y, AltoMinimoCalidadPx) + SeparacionBloquesPx;
            _textoEstadoMaquetado = _estado.text;

            foreach (var b in _botones)
            {
                if (b.Rect == null) continue;
                b.Rect.anchoredPosition = new Vector2(b.Rect.anchoredPosition.x,
                                                      -(y + b.Fila * (AltoBotonPx + 8f)));
            }
            y += 2f * AltoBotonPx + 8f + 10f;

            // La columna derecha (plano y su leyenda) es fija: 48 + 300 + 6 + 60 bajo el margen.
            float altoColumnaDerecha = MargenPx + 48f + LadoPlanoPx + 6f + 60f + MargenPx;
            float alto = Mathf.Max(AltoPx, y + AltoPiePx + MargenPx, altoColumnaDerecha);
            if (_pie != null) _pie.anchoredPosition = new Vector2(MargenPx, -(alto - MargenPx - AltoPiePx));

            if (!Mathf.Approximately(alto, _altoActualPx))
            {
                _altoActualPx = alto;
                _raiz.sizeDelta = new Vector2(AnchoPx, alto);
                if (_volumenPanel != null)
                {
                    _volumenPanel.size = new Vector3(AnchoPx, alto, 1f);
                    _volumenPanel.center = new Vector3((0.5f - _raiz.pivot.x) * AnchoPx,
                                                       (0.5f - _raiz.pivot.y) * alto, 0f);
                }
                if (_colocador != null) _colocador.RecalcularAlturaRelativa();
                Debug.LogWarning($"[DigitalTwin][MR] Panel de anclaje: altura ajustada a {alto:0} px " +
                                 $"({alto * AnchoMetros / AnchoPx:0.00} m) para que quepa el texto.");
            }
        }

        /// <summary>Coloca un bloque de texto a la altura indicada con la altura que necesita
        /// (al menos la mínima) y la devuelve.</summary>
        private static float ColocarBloque(Text texto, float y, float minimo)
        {
            var rt = (RectTransform)texto.transform;
            rt.anchoredPosition = new Vector2(MargenPx, -y);
            float alto = Mathf.Max(minimo, texto.preferredHeight);
            rt.sizeDelta = new Vector2(AnchoColumnaTexto, alto);
            return alto;
        }

        private static Text TextoEn(RectTransform padre, string nombre, string contenido, int cuerpo,
                                    TextAnchor ancla, Color color, FontStyle estilo,
                                    float x, float y, float ancho, float alto, Material material)
        {
            var t = DigitalTwin.UI.RuntimeUIFactory.CreateText(padre, nombre, contenido, cuerpo, ancla, color, estilo);
            var rt = (RectTransform)t.transform;
            rt.anchorMin = rt.anchorMax = new Vector2(0f, 1f);
            rt.pivot = new Vector2(0f, 1f);
            rt.anchoredPosition = new Vector2(x, -y);
            rt.sizeDelta = new Vector2(ancho, alto);
            if (material != null) t.material = material;
            return t;
        }

        private void CrearBoton(string id, string texto, float x, float y, float ancho, Material material, int fila)
        {
            var rect = DigitalTwin.UI.RuntimeUIFactory.CreateRect(_raiz, "Boton_" + id);
            rect.anchorMin = rect.anchorMax = new Vector2(0f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.anchoredPosition = new Vector2(x, -y);
            rect.sizeDelta = new Vector2(ancho, AltoBotonPx);

            var fondo = DigitalTwin.UI.RuntimeUIFactory.CreatePanel(rect, "Fondo", FondoBoton);
            DigitalTwin.UI.RuntimeUIFactory.StretchToParent((RectTransform)fondo.transform);
            if (material != null) fondo.material = material;

            var t = DigitalTwin.UI.RuntimeUIFactory.CreateText(rect, "Texto", texto, 18, TextAnchor.MiddleCenter, TextoNormal);
            DigitalTwin.UI.RuntimeUIFactory.StretchToParent((RectTransform)t.transform);
            if (material != null) t.material = material;

            var volumen = rect.gameObject.AddComponent<BoxCollider>();
            volumen.isTrigger = true;
            volumen.size = new Vector3(ancho, AltoBotonPx, 1f);
            // El pivote está en la esquina superior izquierda: el volumen se centra en el rect.
            volumen.center = new Vector3(ancho * 0.5f, -AltoBotonPx * 0.5f, 0f);

            _botones.Add(new Boton { Id = id, Texto = t, Fondo = fondo, Volumen = volumen, Rect = rect, Fila = fila });
        }

        private void PintarBoton(Boton b)
        {
            b.Fondo.color = !b.Activo ? FondoBotonInactivo : (b.Senalado ? FondoBotonSenalado : FondoBoton);
            b.Texto.color = b.Activo ? TextoNormal : TextoInactivo;
        }

        private Boton BotonPorId(string id)
        {
            foreach (var b in _botones) if (b.Id == id) return b;
            return null;
        }

        private void RefrescarPanel()
        {
            if (_raiz == null) return;

            bool sinSoporte = _anclaje.Estado == MRAnchorService.EstadoAnclaje.NoSoportado;
            var pendiente = EstacionPendiente();
            int n = _registro.Cuenta;

            switch (_fase)
            {
                case Fase.Registrando:
                {
                    string cabecera = sinSoporte
                        ? "Registrar el modelo (sin anclaje persistente en este entorno)"
                        : "Colocar el modelo sobre el edificio";
                    _titulo.text = cabecera;
                    if (pendiente != null && n < PuntosMaximos)
                    {
                        int numero = n + 1;
                        string cuantos = numero <= PuntosRecomendados
                            ? $"Punto {numero} de {PuntosRecomendados}"
                            : $"Punto {numero} (opcional, hasta {PuntosMaximos})";
                        _instruccion.text =
                            $"{cuantos} — {Capitalizar(pendiente.Etiqueta)}.\n" +
                            "Colocate en el umbral, apunta con el mando al CENTRO del hueco de la puerta, " +
                            $"en el suelo, a menos de {DistanciaMaximaAlSuelo:0} m, y aprieta el gatillo. " +
                            "La puerta pedida esta marcada en el plano.";
                        if (numero == 1)
                            _instruccion.text += " Con un solo punto el modelo solo se traslada; el giro se mide con el segundo.";
                    }
                    else
                    {
                        _instruccion.text = n == 0
                            ? "No quedan puertas disponibles para registrar."
                            : "Se han tomado todos los puntos previstos. Pulsa 'Terminar y anclar'.";
                    }
                    if (!_impactoSueloValido) { _estado.text = ""; }
                    _calidad.text = TextoCalidad();
                    _calidad.color = ColorCalidad();
                    break;
                }
                case Fase.Confirmado:
                    _titulo.text = "Anclaje creado";
                    _instruccion.text = $"Modelo anclado con {n} punto(s). {TextoCalidad()}";
                    _estado.text = string.IsNullOrEmpty(_mensajePersistencia)
                        ? "Guardado: pendiente..."
                        : "Guardado: " + _mensajePersistencia;
                    _estado.color = _anclaje.Persistencia == MRAnchorService.EstadoPersistencia.Guardado ? Verde
                                  : _anclaje.Persistencia == MRAnchorService.EstadoPersistencia.Fallo ? Rojo : Ambar;
                    _calidad.text = "Al arrancar la proxima vez, si el guardado fue bien, el modelo volvera solo a su sitio. " +
                                    "Este aviso se cierra solo; A/X lo vuelve a abrir.";
                    _calidad.color = TextoTenue;
                    break;
                case Fase.Restaurado:
                    _titulo.text = "Anclaje restaurado de una sesion anterior";
                    _instruccion.text = "El modelo se ha colocado solo con el anclaje guardado. Si lo ves desplazado, " +
                                        "pulsa 'Recolocar desde cero' y repite el registro.";
                    _estado.text = "";
                    _calidad.text = "Este aviso se cierra solo; A/X lo vuelve a abrir.";
                    _calidad.color = TextoTenue;
                    break;
                case Fase.SinSoporte:
                    _titulo.text = "Registro aplicado (sin anclaje)";
                    _instruccion.text = $"Modelo registrado con {n} punto(s). {TextoCalidad()}";
                    _estado.text = _mensajePersistencia;
                    _estado.color = Ambar;
                    _calidad.text = "En el visor este paso crearia y guardaria el anclaje espacial.";
                    _calidad.color = TextoTenue;
                    break;
                default:
                    _titulo.text = "Anclaje del modelo";
                    _instruccion.text = "";
                    _estado.text = "";
                    _calidad.text = "";
                    break;
            }

            // Disponibilidad de botones según la fase.
            bool registrando = _fase == Fase.Registrando;
            BotonPorId("deshacer").Activo = registrando && n > 0;
            BotonPorId("saltar").Activo = registrando && pendiente != null;
            BotonPorId("terminar").Activo = registrando && n > 0;
            BotonPorId("recolocar").Activo = !registrando || n > 0;
            BotonPorId("reintentar").Activo = _fase == Fase.Confirmado &&
                                              _anclaje.Persistencia == MRAnchorService.EstadoPersistencia.Fallo;
            BotonPorId("cerrar").Activo = true;
            foreach (var b in _botones) PintarBoton(b);

            AjustarMaquetacion();
            RefrescarPlano();
        }

        private string TextoCalidad()
        {
            if (_ultimo == null) return "Sin puntos todavia.";
            if (!_ultimo.GiroMedido)
                return "Con un punto: traslacion aplicada, ORIENTACION NO MEDIDA (se conserva la del modelo). " +
                       "El segundo punto la determina.";
            string peor = _ultimo.IndicePeor >= 0 ? _registro.Puntos[_ultimo.IndicePeor].Etiqueta : "-";
            string veredicto = _ultimo.ResiduoRms <= UmbralBuenoMetros ? "bueno"
                             : _ultimo.ResiduoRms <= UmbralAceptableMetros ? "aceptable" : "malo: repite el peor punto";
            string sala = SalaMasLejana(out Vector3 objetivo);
            float tre = _registro.EstimarErrorEnObjetivo(_binder.RaizModelo, objetivo);
            string lejana = tre >= 0f ? $"Error esperado en la sala mas lejana ({sala}): ~{tre * 100f:0} cm." : "";
            string redundancia = _ultimo.NumeroDePuntos < PuntosRecomendados
                ? " Con dos puntos el ajuste esta determinado pero apenas se comprueba: un tercero lo verifica."
                : "";
            return $"Residuo RMS {_ultimo.ResiduoRms * 100f:0.0} cm ({veredicto}); peor punto " +
                   $"{_ultimo.ResiduoMaximo * 100f:0.0} cm en {peor}. {lejana}{redundancia} " +
                   "El residuo mide la coherencia de los puntos entre si, no la deriva del seguimiento.";
        }

        private Color ColorCalidad()
        {
            if (_ultimo == null || !_ultimo.GiroMedido) return TextoTenue;
            return _ultimo.ResiduoRms <= UmbralBuenoMetros ? Verde
                 : _ultimo.ResiduoRms <= UmbralAceptableMetros ? Ambar : Rojo;
        }

        private static string Capitalizar(string s) =>
            string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s.Substring(1);

        private void Abrir()
        {
            if (_raiz == null) return;
            // El colocador reencuadra el panel ante el usuario (rumbo reiniciado) y lo activa; a
            // partir de ahí lo sigue con holgura. La colocación directa queda como respaldo.
            if (_colocador != null) _colocador.SeguirAlUsuario(true);
            else ColocarPanelAnteElUsuario();
            _raiz.gameObject.SetActive(true);
            RefrescarPanel();
        }

        private void Cerrar()
        {
            if (_raiz == null) return;
            if (_colocador != null) _colocador.SeguirAlUsuario(false);
            _raiz.gameObject.SetActive(false);
            _ocultarAvisoEn = -1f;
            if (_cursorSuelo != null) _cursorSuelo.SetActive(false);
            _rig.MostrarImpacto(0f, false);
        }

        private void ColocarPanelAnteElUsuario()
        {
            Vector3 mirada = _camara.transform.forward;
            mirada.y = 0f;
            if (mirada.sqrMagnitude < 0.0001f) mirada = Vector3.forward;
            mirada.Normalize();
            float altoMetros = _raiz.rect.height * _raiz.localScale.y;
            _raiz.position = _camara.transform.position + mirada * DistanciaPanel
                           + Vector3.up * (-altoMetros * 0.5f + 0.05f);
            _raiz.rotation = Quaternion.LookRotation(mirada, Vector3.up);
        }

        // ReubicarPanelSiHaceFalta (salto del panel al alejarse 2,2 m o girar 60° durante 0,8 s)
        // se retiró el 19-08: el seguimiento lo hace ahora WorldPanelPlacer (ver Abrir y las
        // constantes de seguimiento de la cabecera).

        // ==================================================================================
        //  Plano esquemático
        // ==================================================================================

        private void ConstruirPlano(Material material)
        {
            var area = DigitalTwin.UI.RuntimeUIFactory.CreateRect(_raiz, "Plano");
            area.anchorMin = area.anchorMax = new Vector2(1f, 1f);
            area.pivot = new Vector2(1f, 1f);
            area.anchoredPosition = new Vector2(-MargenPx, -(MargenPx + 48f));
            area.sizeDelta = new Vector2(LadoPlanoPx, LadoPlanoPx);
            var fondo = DigitalTwin.UI.RuntimeUIFactory.CreatePanel(area, "Fondo", new Color(1f, 1f, 1f, 0.06f));
            DigitalTwin.UI.RuntimeUIFactory.StretchToParent((RectTransform)fondo.transform);
            if (material != null) fondo.material = material;
            _plano = area;

            var leyenda = TextoEn(_raiz, "LeyendaPlano",
                "Plano del modelo: salas por nombre, puertas en gris, punto pedido en ambar, tomados en verde, tu posicion en azul.",
                14, TextAnchor.UpperLeft, TextoTenue, FontStyle.Normal,
                AnchoPx - MargenPx - LadoPlanoPx, MargenPx + 48f + LadoPlanoPx + 6f, LadoPlanoPx, 60f, material);

            // Extensión del modelo en coordenadas locales de la raíz (invariantes al registro).
            Transform raiz = _binder.RaizModelo;
            _planoMin = new Vector2(float.MaxValue, float.MaxValue);
            _planoMax = new Vector2(float.MinValue, float.MinValue);
            var salas = new Dictionary<string, (Vector2 suma, int n)>();
            foreach (var pv in _index.NavPoints)
            {
                if (pv == null) continue;
                Vector2 l = Local2D(raiz, pv.transform.position);
                Acumular(l);
                string s = Sala(pv);
                if (s == "?") continue;
                salas.TryGetValue(s, out var acc);
                salas[s] = (acc.suma + l, acc.n + 1);
            }
            foreach (var c in _candidatas) Acumular(Local2D(raiz, PuntoDeSueloMundo(c.Elemento, out _)));
            if (_planoMin.x > _planoMax.x) { _planoMin = Vector2.zero; _planoMax = Vector2.one; }
            Vector2 margen = (_planoMax - _planoMin) * 0.08f + Vector2.one * 0.5f;
            _planoMin -= margen; _planoMax += margen;

            // Salas: etiqueta en el centroide de sus puntos de vista.
            foreach (var kv in salas)
            {
                Vector2 c = kv.Value.suma / kv.Value.n;
                var t = DigitalTwin.UI.RuntimeUIFactory.CreateText(area, "Sala_" + kv.Key, kv.Key, 11,
                    TextAnchor.MiddleCenter, new Color(0.85f, 0.88f, 0.93f, 0.85f));
                var rt = (RectTransform)t.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = APlano(c);
                rt.sizeDelta = new Vector2(96f, 16f);
                if (material != null) t.material = material;
            }

            // Puertas: cuadrado gris; las del plan llevan además su número (se pinta al refrescar).
            foreach (var c in _candidatas)
            {
                Vector2 l = Local2D(raiz, PuntoDeSueloMundo(c.Elemento, out _));
                var img = DigitalTwin.UI.RuntimeUIFactory.CreatePanel(area, "Puerta", new Color(0.7f, 0.72f, 0.76f, 0.9f));
                var rt = (RectTransform)img.transform;
                rt.anchorMin = rt.anchorMax = new Vector2(0f, 0f);
                rt.pivot = new Vector2(0.5f, 0.5f);
                rt.anchoredPosition = APlano(l);
                rt.sizeDelta = new Vector2(8f, 8f);
                if (material != null) img.material = material;

                var num = DigitalTwin.UI.RuntimeUIFactory.CreateText(area, "Num", "", 13, TextAnchor.MiddleCenter,
                                                                    Color.black, FontStyle.Bold);
                var rtn = (RectTransform)num.transform;
                rtn.anchorMin = rtn.anchorMax = new Vector2(0f, 0f);
                rtn.pivot = new Vector2(0.5f, 0.5f);
                rtn.anchoredPosition = APlano(l);
                rtn.sizeDelta = new Vector2(24f, 24f);
                if (material != null) num.material = material;
                _marcasPlano[c] = (img, num);
            }

            // Usuario.
            var yo = DigitalTwin.UI.RuntimeUIFactory.CreateIcon(area, "Usuario",
                DigitalTwin.UI.RuntimeUIFactory.CircleSprite(), Azul);
            _marcaUsuario = (RectTransform)yo.transform;
            _marcaUsuario.anchorMin = _marcaUsuario.anchorMax = new Vector2(0f, 0f);
            _marcaUsuario.pivot = new Vector2(0.5f, 0.5f);
            _marcaUsuario.sizeDelta = new Vector2(12f, 12f);
            if (material != null) yo.material = material;
            _marcaUsuario.gameObject.SetActive(false);
        }

        private void Acumular(Vector2 l)
        {
            _planoMin = Vector2.Min(_planoMin, l);
            _planoMax = Vector2.Max(_planoMax, l);
        }

        private static Vector2 Local2D(Transform raiz, Vector3 mundo)
        {
            Vector3 l = raiz.InverseTransformPoint(mundo);
            return new Vector2(l.x, l.z);
        }

        private Vector2 APlano(Vector2 local)
        {
            float u = Mathf.InverseLerp(_planoMin.x, _planoMax.x, local.x);
            float v = Mathf.InverseLerp(_planoMin.y, _planoMax.y, local.y);
            return new Vector2(u * LadoPlanoPx, v * LadoPlanoPx);
        }

        private void RefrescarPlano()
        {
            if (_plano == null) return;
            var pendiente = _fase == Fase.Registrando ? EstacionPendiente() : null;
            foreach (var kv in _marcasPlano)
            {
                var est = kv.Key;
                int idx = _plan.IndexOf(est);
                bool enPlan = idx >= 0 && _fase == Fase.Registrando;
                bool esPedida = est == pendiente;
                var (img, num) = kv.Value;
                if (est.Tomada)
                {
                    img.color = Verde; img.rectTransform.sizeDelta = new Vector2(20f, 20f);
                    num.text = (idx + 1).ToString();
                }
                else if (esPedida)
                {
                    img.color = Ambar; img.rectTransform.sizeDelta = new Vector2(24f, 24f);
                    num.text = (idx + 1).ToString();
                }
                else if (enPlan)
                {
                    img.color = new Color(1f, 0.82f, 0.2f, 0.45f); img.rectTransform.sizeDelta = new Vector2(16f, 16f);
                    num.text = (idx + 1).ToString();
                }
                else
                {
                    img.color = new Color(0.7f, 0.72f, 0.76f, 0.9f); img.rectTransform.sizeDelta = new Vector2(8f, 8f);
                    num.text = "";
                }
            }
        }

        private void ActualizarMarcaUsuario()
        {
            if (_marcaUsuario == null || _binder.RaizModelo == null) return;
            // Solo tiene sentido con el modelo al menos trasladado (un punto): antes, la posición
            // del usuario en coordenadas del modelo es arbitraria.
            bool util = _registro.Cuenta > 0 || _fase == Fase.Restaurado || _fase == Fase.Confirmado;
            _marcaUsuario.gameObject.SetActive(util);
            if (!util) return;
            _marcaUsuario.anchoredPosition = APlano(Local2D(_binder.RaizModelo, _camara.transform.position));
        }

        // ==================================================================================
        //  Marcas en el mundo: cursor de suelo y banderas de los puntos tomados
        // ==================================================================================

        private void ConstruirCursorDeSuelo()
        {
            var canvas = DigitalTwin.UI.RuntimeUIFactory.CreateWorldCanvas("~CursorSueloAR",
                anchoPx: 200f, altoPx: 200f, anchoMetros: 0.30f);
            var rt = (RectTransform)canvas.transform;
            rt.SetParent(transform, true);
            rt.rotation = Quaternion.Euler(90f, 0f, 0f);   // tumbado sobre el suelo
            var anillo = DigitalTwin.UI.RuntimeUIFactory.CreateIcon(rt, "Anillo",
                DigitalTwin.UI.RuntimeUIFactory.RingSprite(), Verde);
            DigitalTwin.UI.RuntimeUIFactory.StretchToParent((RectTransform)anillo.transform);
            var punto = DigitalTwin.UI.RuntimeUIFactory.CreateIcon(rt, "Centro",
                DigitalTwin.UI.RuntimeUIFactory.CircleSprite(), Verde);
            var rtp = (RectTransform)punto.transform;
            rtp.anchorMin = rtp.anchorMax = new Vector2(0.5f, 0.5f);
            rtp.sizeDelta = new Vector2(16f, 16f);
            var material = MRIndicadoresDestino.MaterialSiempreVisible();
            if (material != null) { anillo.material = material; punto.material = material; }
            _cursorImagen = anillo;
            _cursorSuelo = rt.gameObject;
            _cursorSuelo.SetActive(false);
        }

        private void ColocarBandera(Vector3 posicion, int numero)
        {
            var canvas = DigitalTwin.UI.RuntimeUIFactory.CreateWorldCanvas("~PuntoRegistro" + numero,
                anchoPx: 200f, altoPx: 200f, anchoMetros: 0.24f);
            var rt = (RectTransform)canvas.transform;
            rt.SetParent(transform, true);
            rt.position = posicion + Vector3.up * 0.006f;
            rt.rotation = Quaternion.Euler(90f, 0f, 0f);
            var material = MRIndicadoresDestino.MaterialSiempreVisible();
            var anillo = DigitalTwin.UI.RuntimeUIFactory.CreateIcon(rt, "Anillo",
                DigitalTwin.UI.RuntimeUIFactory.RingSprite(), Verde);
            DigitalTwin.UI.RuntimeUIFactory.StretchToParent((RectTransform)anillo.transform);
            var texto = DigitalTwin.UI.RuntimeUIFactory.CreateText(rt, "Numero", numero.ToString(), 90,
                TextAnchor.MiddleCenter, Verde, FontStyle.Bold);
            DigitalTwin.UI.RuntimeUIFactory.StretchToParent((RectTransform)texto.transform);
            if (material != null) { anillo.material = material; texto.material = material; }
            _banderas.Add(rt.gameObject);
        }

        private void LimpiarBanderas()
        {
            foreach (var b in _banderas) if (b != null) Destroy(b);
            _banderas.Clear();
        }
    }
}
