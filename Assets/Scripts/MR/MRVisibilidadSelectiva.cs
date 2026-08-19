using System.Collections.Generic;
using DigitalTwin.Core;
using DigitalTwin.Metadata;
using IFCImporter;
using UnityEngine;

namespace DigitalTwin.MR
{
    /// <summary>
    /// Visibilidad selectiva (T10, versión reducida, 19-08-2026): el activo SELECCIONADO se sigue
    /// viendo a través de los cerramientos que ocultan todo lo demás, con su ficha al lado.
    ///
    /// EL CASO DE USO. El operario busca un sensor precisamente cuando NO lo ve: tras un tabique,
    /// sobre un falso techo, en la sala contigua. En modo anclado la geometría del modelo no se
    /// dibuja —solo escribe profundidad, <see cref="MROcclusionService"/>— así que un sensor al
    /// otro lado de un muro desaparece, igual que desaparecería en la realidad. La excepción
    /// deliberada es el sensor que el operario acaba de consultar: se apunta donde sí se ve, se
    /// abre su ficha, y al girarse o alejarse sigue viéndose a través de los oclusores, con la
    /// línea que lo une a la ficha y la caja de aristas que lo encuadra.
    ///
    /// QUÉ SE HACE Y QUÉ NO (alcance fijado por Alex el 19-08). Se hace rayos X sobre el sensor
    /// YA seleccionado por señalamiento. NO se hace elegir el sensor de una lista por su nombre:
    /// es interfaz nueva y queda propuesto, no implementado.
    ///
    /// CÓMO. No se toca el material del sensor. Por cada malla del sensor se cuelga un hijo
    /// <c>~RayosX</c> con la MISMA malla y un material cuyo sombreador
    /// (<c>DigitalTwin/RayosX</c>, en Resources) dibuja SOLO lo que queda detrás de algo ya
    /// escrito en profundidad (ZTest Greater, sin escritura de profundidad, cola Transparent-10:
    /// después de los oclusores y del propio sensor, antes de los lienzos de interfaz). La parte
    /// visible la sigue pintando el material de siempre con la prueba de profundidad normal; la
    /// parte oculta la pinta el hijo, y la pinta DISTINTA.
    ///
    /// POR QUÉ LA PARTE OCULTA SE DISTINGUE (decisión). Si el sensor se viera igual delante que
    /// detrás de un muro, la vista mentiría sobre la profundidad: el operario no sabría si el
    /// activo está en su sala o en la contigua, y en una herramienta de mantenimiento eso no es
    /// un matiz estético, es la pregunta que se está haciendo. Lo oculto se dibuja como silueta
    /// translúcida en el color de selección con borde claro y trama de rayas fija al mundo
    /// —la convención del dibujo técnico para las aristas ocultas es la línea discontinua—, y la
    /// frontera entre el aspecto normal y el tramado dibuja además el canto del oclusor, que en
    /// modo anclado es invisible. Sobre un fondo uniforme (la captura de la memoria se toma en
    /// el simulador, sin vídeo) esa frontera es lo que hace legible "está detrás de algo" y no
    /// "flota en el vacío".
    ///
    /// LA LÍNEA Y LA CAJA reciben el mismo trato: se les AÑADE una segunda ranura de material
    /// con la pasada oculta (guiones a lo largo de la línea), de modo que el tramo visible sigue
    /// continuo y el tramo tras el muro se ve discontinuo. Sin esto la línea —el único vínculo
    /// entre la ficha y el activo cuando el panel no está junto al objeto— se cortaría en el muro
    /// y la ficha quedaría huérfana justo en el caso para el que existe la función.
    ///
    /// ELEMENTOS OCULTADOS. Los sensores nunca están entre los ocultados (la clasificación los
    /// deja visibles antes de decidir nada), pero si se seleccionara un elemento cuyo renderer
    /// está apagado NO se le dibuja fantasma: se ocultó precisamente porque el modelo no
    /// garantiza dónde está (puertas, mobiliario), y un fantasma afirmaría lo contrario. Hoy
    /// además no puede seleccionarse por rayo (su colisionador se apaga con él). La línea y la
    /// caja sí se dibujan, porque no afirman geometría: señalan un punto.
    ///
    /// EN LOS DOS MODOS. Se instala desde el montaje común, así que en navegación por nodos el
    /// sensor seleccionado también se ve a través de los muros (visibles allí), lo que hace el
    /// efecto inmediatamente legible en el simulador. Si se quisiera limitar al modo anclado,
    /// basta mover la llamada a <see cref="Instalar"/> de MontarComunIncremental a MontarAnclado.
    ///
    /// SI FALTA EL SOMBREADOR (la clase de fallo conocida del proyecto) se avisa una vez y la
    /// función queda inerte: un adorno que falla no debe tumbar la selección.
    /// </summary>
    public class MRVisibilidadSelectiva : MonoBehaviour
    {
        public const string RutaShaderRayosX = "MR/RayosX";

        /// <summary>Opacidad de la silueta oculta. 0,55 deja ver la forma y la trama sin que el
        /// sensor parezca estar delante del muro.</summary>
        private const float AlfaOculto = 0.55f;
        /// <summary>Periodo de las rayas de la malla, en metros de altura (un sensor de 10 cm
        /// muestra dos o tres rayas).</summary>
        private const float PeriodoRayasMetros = 0.04f;
        /// <summary>Número de guiones a lo largo de la línea de unión y de la caja.</summary>
        private const float GuionesPorLinea = 28f;

        private static readonly int IdColorOculto = Shader.PropertyToID("_ColorOculto");
        private static readonly int IdColorBorde = Shader.PropertyToID("_ColorBorde");
        private static readonly int IdTrama = Shader.PropertyToID("_Trama");
        private static readonly int IdPeriodo = Shader.PropertyToID("_Periodo");
        private static readonly int IdCull = Shader.PropertyToID("_Cull");

        private MetadataPanelController _panel;
        private SelectionHighlighter _resaltador;
        private WorldPanelPlacer _colocador;
        private HashSet<IfcMetadata> _sensores;

        private Material _materialMalla;
        private Material _materialLinea;
        private bool _operativo;

        private readonly List<GameObject> _fantasmas = new List<GameObject>();
        private LineRenderer _linea, _caja;
        private Material[] _materialesLineaOriginales, _materialesCajaOriginales;
        private IfcMetadata _actual;
        private bool _avisadoOcultado;

        /// <summary>Elemento con rayos X ahora mismo, o null.</summary>
        public IfcMetadata Actual => _actual;

        /// <summary>
        /// Crea el componente y lo engancha a la ficha: cada elemento mostrado recibe el trato, y
        /// cerrar la ficha lo retira. Llamado desde el montaje común del visor, después de crear
        /// el resaltador y el colocador.
        /// </summary>
        public static MRVisibilidadSelectiva Instalar(MetadataPanelController panel,
                                                      SelectionHighlighter resaltador,
                                                      WorldPanelPlacer colocador,
                                                      SceneModelIndex index)
        {
            var go = new GameObject("~VisibilidadSelectivaMR");
            Object.DontDestroyOnLoad(go);
            var vs = go.AddComponent<MRVisibilidadSelectiva>();
            vs.Initialize(panel, resaltador, colocador, index);
            return vs;
        }

        public void Initialize(MetadataPanelController panel, SelectionHighlighter resaltador,
                               WorldPanelPlacer colocador, SceneModelIndex index)
        {
            _panel = panel;
            _resaltador = resaltador;
            _colocador = colocador;
            _sensores = index != null ? new HashSet<IfcMetadata>(index.Sensors) : new HashSet<IfcMetadata>();

            var shader = Resources.Load<Shader>(RutaShaderRayosX);
            if (shader == null || !shader.isSupported)
            {
                Debug.LogError($"[DigitalTwin][AR] Rayos X: sombreador Resources/{RutaShaderRayosX} " +
                               (shader == null ? "no encontrado" : "NO soportado en este dispositivo") +
                               ". La visibilidad selectiva queda desactivada; la seleccion sigue " +
                               "funcionando (resaltado, ficha y linea, ocultables por los muros).");
                _operativo = false;
                return;
            }

            Color seleccion = resaltador != null ? resaltador.ColorResaltado : new Color(1f, 0.78f, 0.15f, 1f);
            Color linea = colocador != null ? colocador.ColorLinea : seleccion;

            _materialMalla = new Material(shader) { name = "~RayosXMalla" };
            _materialMalla.SetColor(IdColorOculto, new Color(seleccion.r, seleccion.g, seleccion.b, AlfaOculto));
            _materialMalla.SetColor(IdColorBorde, new Color(1f, 0.92f, 0.55f, 0.95f));
            _materialMalla.SetFloat(IdTrama, 0f);
            _materialMalla.SetFloat(IdPeriodo, PeriodoRayasMetros);
            _materialMalla.SetFloat(IdCull, (float)UnityEngine.Rendering.CullMode.Back);

            _materialLinea = new Material(shader) { name = "~RayosXLinea" };
            _materialLinea.SetColor(IdColorOculto, new Color(linea.r, linea.g, linea.b, 0.9f));
            _materialLinea.SetColor(IdColorBorde, new Color(linea.r, linea.g, linea.b, 0.9f));
            _materialLinea.SetFloat(IdTrama, 1f);
            _materialLinea.SetFloat(IdPeriodo, GuionesPorLinea);
            _materialLinea.SetFloat(IdCull, (float)UnityEngine.Rendering.CullMode.Off);

            _linea = colocador != null ? colocador.LineaDeUnion : null;
            _caja = resaltador != null ? resaltador.CajaDeAristas : null;

            if (_panel != null)
            {
                _panel.OnElementShown += Mostrar;
                _panel.OnPanelHidden += Quitar;
            }
            _operativo = true;

            Debug.LogWarning($"[DigitalTwin][AR] Rayos X listos: sombreador '{shader.name}' " +
                             $"(soportado={shader.isSupported}, pases={shader.passCount}, cola " +
                             $"{_materialMalla.renderQueue}); {_sensores.Count} sensores candidatos; " +
                             $"linea {( _linea != null ? "con" : "SIN")} pasada oculta, caja " +
                             $"{(_caja != null ? "con" : "SIN")} pasada oculta. Se activan al " +
                             "mostrar la ficha de un sensor y se retiran al cerrarla.");
        }

        /// <summary>Aplica el trato al elemento mostrado: fantasma de malla si es sensor (y sus
        /// mallas están encendidas), pasada oculta en la línea y en la caja en todo caso.</summary>
        public void Mostrar(IfcMetadata meta)
        {
            Quitar();
            if (!_operativo || meta == null) return;
            _actual = meta;

            int mallas = 0, apagadas = 0;
            bool esSensor = _sensores.Contains(meta);
            if (esSensor)
            {
                foreach (var renderer in meta.GetComponentsInChildren<MeshRenderer>())
                {
                    if (renderer == null || renderer.name == "~RayosX") continue;
                    if (!renderer.enabled) { apagadas++; continue; }
                    var filtro = renderer.GetComponent<MeshFilter>();
                    if (filtro == null || filtro.sharedMesh == null) continue;

                    var go = new GameObject("~RayosX");
                    go.layer = renderer.gameObject.layer;
                    go.transform.SetParent(renderer.transform, false);
                    var mf = go.AddComponent<MeshFilter>();
                    mf.sharedMesh = filtro.sharedMesh;
                    var mr = go.AddComponent<MeshRenderer>();
                    int ranuras = Mathf.Max(1, filtro.sharedMesh.subMeshCount);
                    var materiales = new Material[ranuras];
                    for (int k = 0; k < ranuras; k++) materiales[k] = _materialMalla;
                    mr.sharedMaterials = materiales;
                    mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                    mr.receiveShadows = false;
                    mr.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                    mr.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
                    mr.motionVectorGenerationMode = MotionVectorGenerationMode.ForceNoMotion;
                    _fantasmas.Add(go);
                    mallas++;
                }

                if (mallas == 0 && apagadas > 0 && !_avisadoOcultado)
                {
                    _avisadoOcultado = true;
                    Debug.LogWarning("[DigitalTwin][AR] Rayos X: el elemento seleccionado tiene sus " +
                                     "mallas apagadas (clase ocultada): no se dibuja fantasma, porque el " +
                                     "modelo no garantiza su posicion. Linea y caja si se ven a traves.");
                }
            }

            AnadirPasadaOculta(_linea, ref _materialesLineaOriginales);
            AnadirPasadaOculta(_caja, ref _materialesCajaOriginales);

            Debug.LogWarning($"[DigitalTwin][AR] Rayos X activos sobre '{meta.ifcName}' (GlobalId " +
                             $"{meta.globalId}; {(esSensor ? "sensor" : "no sensor: solo linea y caja")}): " +
                             $"{mallas} malla(s) fantasma, {apagadas} apagada(s).");
        }

        /// <summary>Retira el trato: destruye los fantasmas y devuelve a la línea y a la caja sus
        /// materiales de antes.</summary>
        public void Quitar()
        {
            foreach (var go in _fantasmas) if (go != null) Destroy(go);
            _fantasmas.Clear();

            QuitarPasadaOculta(_linea, ref _materialesLineaOriginales);
            QuitarPasadaOculta(_caja, ref _materialesCajaOriginales);

            if (_actual != null)
                Debug.LogWarning($"[DigitalTwin][AR] Rayos X retirados de '{_actual.ifcName}'.");
            _actual = null;
        }

        /// <summary>
        /// Añade la pasada oculta como segunda ranura de material del LineRenderer: un renderer de
        /// línea dibuja la línea entera una vez por material, así que la ranura original sigue
        /// pintando el tramo visible (prueba de profundidad normal) y la nueva pinta solo el tramo
        /// que queda detrás de algo, a guiones. Se usan materiales compartidos para no instanciar.
        /// </summary>
        private void AnadirPasadaOculta(LineRenderer linea, ref Material[] originales)
        {
            if (linea == null || _materialLinea == null || originales != null) return;
            var actuales = linea.sharedMaterials;
            originales = actuales;
            var nuevos = new Material[actuales.Length + 1];
            for (int i = 0; i < actuales.Length; i++) nuevos[i] = actuales[i];
            nuevos[actuales.Length] = _materialLinea;
            linea.sharedMaterials = nuevos;
        }

        private static void QuitarPasadaOculta(LineRenderer linea, ref Material[] originales)
        {
            if (originales == null) return;
            if (linea != null) linea.sharedMaterials = originales;
            originales = null;
        }

        private void OnDestroy()
        {
            if (_panel != null)
            {
                _panel.OnElementShown -= Mostrar;
                _panel.OnPanelHidden -= Quitar;
            }
            Quitar();
        }
    }
}
