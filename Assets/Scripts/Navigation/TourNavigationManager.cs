using System.Collections;
using System.Collections.Generic;
using System.Linq;
using DigitalTwin.Core;
using DigitalTwin.UI;
using IFCImporter;
using UnityEngine;
using UnityEngine.UI;

namespace DigitalTwin.Navigation
{
    /// <summary>
    /// Navegación por puntos al estilo tour virtual inmersivo (referencia: vtour.cloud).
    /// El usuario se queda fijo en un punto de vista ("Esfera..."), ve hotspots hacia los
    /// puntos cercanos con línea de visión libre, y al clicar uno la cámara se desplaza con una
    /// transición suave hasta él. No hay movimiento libre tipo FPS.
    ///
    /// Decisión de transición: interpolación de posición (dolly) con giro parcial hacia la
    /// dirección de desplazamiento, en vez de un corte o un fundido a negro. Se eligió así
    /// porque, a diferencia de un tour de fotos 360, aquí hay geometría 3D real y continua:
    /// mantener la posición interpolada conserva la orientación espacial del operario dentro
    /// del edificio, que es justo lo que se quiere reforzar en un gemelo digital de
    /// mantenimiento. Ver DEV_README.md para más detalle y para los parámetros ajustables.
    /// </summary>
    public class TourNavigationManager : MonoBehaviour
    {
        private class NavPointData
        {
            public IfcMetadata Meta;
            public Transform Transform;
            public string DisplayName;
            /// <summary>Sala a la que pertenece (pset Otros/LOC_Localizacion4), para garantizar salida.</summary>
            public string Sala;

            /// <summary>
            /// Posición a la que viaja la cámara, si difiere del origen del objeto.
            ///
            /// Los puntos "Esfera..." están colocados directamente a la altura de la vista, así
            /// que para ellos basta el origen. Los nodos de puerta, en cambio, toman su posición
            /// del centro del volumen de la hoja: el origen de una puerta procedente de Revit
            /// suele caer en una esquina del marco, y usarlo dejaría la cámara incrustada en el
            /// tabique.
            /// </summary>
            public Vector3? PosicionFija;

            public Vector3 Pos => PosicionFija ?? Transform.position;
        }

        private class HotspotSlot
        {
            public RectTransform Root;
            public Image Ring;
            public Text Label;
            public NavPointData Target;
            public ClickTarget ClickHandle;
        }

        [Header("Alcance de los hotspots")]
        [Tooltip("Radio horizontal de búsqueda, en metros. Se mide ignorando la altura.")]
        public float MaxHotspotDistance = 15f;

        [Tooltip("Diferencia de altura máxima admitida, en metros. Es lo que evita que aparezcan " +
                 "puntos de otra planta: la distancia horizontal por sí sola no sirve para eso, " +
                 "porque un punto justo encima tendría distancia horizontal casi cero y saldría " +
                 "como el más cercano de todos.")]
        public float ToleranciaVertical = 2.5f;

        [Tooltip("Cuántos puntos se muestran como mucho. Debe ser al menos el grado máximo del " +
                 "grafo de navegación; con menos, algún nodo ofrecería destinos que la interfaz " +
                 "no llega a dibujar. Sobre el modelo de referencia ese grado máximo es 5.")]
        public int MaxHotspotsShown = 5;

        [Tooltip("Cuántos se muestran siempre, aunque queden fuera del radio. Evita que un punto " +
                 "aislado deje al usuario sin ninguna salida.")]
        public int MinHotspotsAlwaysShown = 3;

        [Header("Conectividad entre salas")]
        [Tooltip("Reserva uno de los huecos para el punto más cercano de OTRA sala, aunque haya " +
                 "puntos de la sala actual más próximos. Es lo que garantiza que siempre se pueda " +
                 "salir de la habitación en la que se está.")]
        public bool GarantizarSalidaDeSala = true;

        [Tooltip("Separación angular mínima entre hotspots, en grados, para que no se solapen en " +
                 "pantalla. 0 lo desactiva.")]
        public float SeparacionAngularMinima = 10f;

        [Header("Transición entre puntos")]
        public float TransitionDuration = 1.1f;
        [Range(0f, 1f)] public float TurnTowardsTravelBlend = 0.6f;

        [Header("Refresco de hotspots")]
        public float HotspotRefreshInterval = 0.15f;

        public bool IsTransitioning { get; private set; }

        [Header("Salto directo entre zonas")]
        [Tooltip("A partir de esta distancia en metros, el salto desde el menú de zonas se " +
                 "resuelve de forma instantánea en vez de con desplazamiento continuo.")]
        public float DistanciaSaltoInstantaneo = 12f;

        /// <summary>Punto representativo de cada sala, en el orden en que se ofrece al usuario.</summary>
        private readonly List<(string Sala, NavPointData Punto)> _orbesPrincipales =
            new List<(string, NavPointData)>();

        /// <summary>Salas disponibles para el menú de acceso directo.</summary>
        public IReadOnlyList<string> Salas
        {
            get
            {
                var nombres = new List<string>(_orbesPrincipales.Count);
                foreach (var o in _orbesPrincipales) nombres.Add(o.Sala);
                return nombres;
            }
        }

        /// <summary>Sala en la que se encuentra el punto actual, o cadena vacía si no consta.</summary>
        public string SalaActual => _current != null ? (_current.Sala ?? string.Empty) : string.Empty;

        private readonly List<NavPointData> _points = new List<NavPointData>();
        private NavPointData _current;
        private Camera _camera;
        private Canvas _canvas;
        private RectTransform _hotspotLayer;
        private Text _currentPointLabel;
        private readonly List<HotspotSlot> _pool = new List<HotspotSlot>();
        private float _refreshTimer;

        /// <summary>
        /// Grafo precalculado en el Editor (Tools > Generar grafo de navegacion). Si no
        /// existe, el tour sigue funcionando con el criterio de proximidad: el grafo mejora la
        /// navegacion pero no es un requisito para arrancar.
        /// </summary>
        private NavGraphAsset _grafo;
        private readonly Dictionary<string, NavPointData> _puntosPorGlobalId =
            new Dictionary<string, NavPointData>();


        public void Initialize(SceneModelIndex index, Canvas canvas)
        {
            _canvas = canvas;
            _camera = Camera.main;

            if (_camera == null)
            {
                Debug.LogError("[DigitalTwin] TourNavigationManager: no se encuentra ninguna cámara con tag MainCamera.");
                enabled = false;
                return;
            }

            foreach (var meta in index.NavPoints)
            {
                if (meta == null) continue;
                var renderer = meta.GetComponent<Renderer>();
                if (renderer != null) renderer.enabled = false; // son marcadores, no geometría visible

                _points.Add(new NavPointData
                {
                    Meta = meta,
                    Transform = meta.transform,
                    DisplayName = BuildDisplayName(meta),
                    Sala = meta.GetValue("Otros", "LOC_Localizacion4")
                });
            }

            foreach (var p in _points)
                if (p.Meta != null && !string.IsNullOrEmpty(p.Meta.globalId))
                    _puntosPorGlobalId[p.Meta.globalId] = p;

            CargarGrafo();
            IncorporarNodosDelGrafoQueNoSonEsferas(index);
            VerificarCoberturaDelGrafo();
            AplicarEtiquetasUnicas();
            CalcularOrbesPrincipales();
            BuildUI();

            if (_points.Count == 0)
            {
                Debug.LogWarning("[DigitalTwin] Sin puntos 'Esfera...' no hay tour de navegación posible.");
                return;
            }

            _current = FindNearest(_camera.transform.position, null);
            _camera.transform.position = _current.Pos;
            GetComponent<TourCameraLook>()?.SyncFromTransform();

            // Regla de la puerta transparente también en el encuadre inicial: si el punto más
            // cercano resultara ser un umbral, la hoja no debe taparlo desde el primer fotograma.
            PuertaTransparente.AlLlegarANodo(_current.Meta);
            RefreshHotspots();
        }

        private void OnDestroy()
        {
            // Por ningún camino debe quedar una hoja invisible al salir del recorrido.
            PuertaTransparente.Restituir();
        }

        /// <summary>
        /// Carga el grafo desde Resources. Se busca por nombre y no por referencia asignada en el
        /// Inspector porque todo el sistema se construye por codigo, sin tocar la escena.
        /// </summary>
        private void CargarGrafo()
        {
            _grafo = Resources.Load<NavGraphAsset>("NavGraph");

            if (_grafo == null || _grafo.Nodos.Count == 0)
            {
                _grafo = null;
                Debug.LogWarning("[DigitalTwin] No hay grafo de navegacion (Assets/Resources/NavGraph.asset). " +
                                 "Se usara el criterio de proximidad, que funciona pero puede solapar hotspots " +
                                 "en salas diafanas. Generalo con Tools > Generar grafo de navegacion.");
                return;
            }

            int reconocidos = 0;
            foreach (var nodo in _grafo.Nodos)
                if (_puntosPorGlobalId.ContainsKey(nodo.GlobalId)) reconocidos++;

            Debug.Log($"[DigitalTwin] Grafo de navegacion cargado: {_grafo.Nodos.Count} nodos, " +
                      $"{_grafo.ContarAristas()} aristas, generado el {_grafo.GeneradoEl}. " +
                      $"{reconocidos} de {_grafo.Nodos.Count} nodos corresponden a puntos 'Esfera...'.");

            // El aviso de nodos huerfanos NO puede emitirse aqui. En este punto solo se han
            // emparejado los puntos "Esfera...", y el grafo contiene ademas puertas y pasos del
            // modelo, que se dan de alta justo despues en IncorporarNodosDelGrafoQueNoSonEsferas.
            // Comprobarlo ahora da siempre un falso positivo que invita a regenerar un grafo
            // que esta bien: con 36 esferas y 18 puertas sobre 54 nodos, saltaba en cada arranque.
            // La comprobacion real se hace en VerificarCoberturaDelGrafo, ya con todo incorporado.
        }

        /// <summary>
        /// Comprueba que todos los nodos del grafo tengan un destino en la escena, una vez
        /// incorporadas tanto las esferas como las puertas y pasos del modelo.
        ///
        /// Solo aqui tiene sentido la comprobacion: un nodo sin destino significa de verdad que
        /// el grafo se genero contra otra version del modelo, con GlobalId distintos, y entonces
        /// hay que regenerarlo. Hecha antes de incorporar las puertas, la comprobacion avisaba
        /// siempre aunque el grafo estuviese perfectamente.
        /// </summary>
        private void VerificarCoberturaDelGrafo()
        {
            if (_grafo == null) return;

            int sinDestino = 0;
            foreach (var nodo in _grafo.Nodos)
                if (!_puntosPorGlobalId.ContainsKey(nodo.GlobalId)) sinDestino++;

            if (sinDestino == 0) return;

            Debug.LogWarning($"[DigitalTwin] {sinDestino} de {_grafo.Nodos.Count} nodos del grafo no " +
                             "corresponden a ningun elemento de la escena. Suele significar que el modelo " +
                             "se ha reimportado con GlobalId distintos: regenera el grafo con " +
                             "Tools > Generar grafo de navegacion.");
        }

        /// <summary>
        /// Da de alta como destinos los nodos del grafo que no son puntos "Esfera...".
        ///
        /// El generador del grafo incorpora las puertas del modelo como nodos intermedios, porque
        /// sin ellas no hay por dónde rodear y los enlaces acaban atravesando tabiques. Esos nodos
        /// existen en la escena -son elementos IFC con sus metadatos- pero no estaban en la lista
        /// de puntos de navegación, así que sin este paso el recorrido los descartaría y se
        /// perdería justo la conectividad que se pretendía ganar.
        ///
        /// La posición se toma del centro del volumen y no del origen del objeto: el origen de
        /// una puerta procedente de Revit suele caer en una esquina del marco, y usarlo dejaría la
        /// cámara incrustada en el tabique.
        /// </summary>
        private void IncorporarNodosDelGrafoQueNoSonEsferas(SceneModelIndex index)
        {
            if (_grafo == null) return;

            var porGlobalId = new Dictionary<string, IfcMetadata>();
            foreach (var meta in index.AllElements)
                if (meta != null && !string.IsNullOrEmpty(meta.globalId))
                    porGlobalId[meta.globalId] = meta;

            int añadidos = 0;
            foreach (var nodo in _grafo.Nodos)
            {
                if (string.IsNullOrEmpty(nodo.GlobalId)) continue;
                if (_puntosPorGlobalId.ContainsKey(nodo.GlobalId)) continue;
                if (!porGlobalId.TryGetValue(nodo.GlobalId, out var meta)) continue;

                var renderer = meta.GetComponentInChildren<Renderer>();
                var punto = new NavPointData
                {
                    Meta = meta,
                    Transform = meta.transform,
                    DisplayName = BuildDisplayName(meta),
                    Sala = meta.GetValue("Otros", "LOC_Localizacion4"),
                    PosicionFija = renderer != null ? renderer.bounds.center : (Vector3?)null
                };

                _points.Add(punto);
                _puntosPorGlobalId[nodo.GlobalId] = punto;
                añadidos++;
            }

            if (añadidos > 0)
                Debug.Log($"[DigitalTwin] {añadidos} nodos del grafo incorporados al recorrido " +
                          "además de los puntos 'Esfera...' (puertas y pasos del modelo).");
        }

        /// <summary>
        /// Sustituye los nombres repetidos por sus variantes con ordinal («Comedor · 1/2»),
        /// las mismas que usa la versión inmersiva. Solo cambia el TEXTO de los indicadores en
        /// las salas con varios puntos; la selección de destinos no interviene aquí.
        /// </summary>
        private void AplicarEtiquetasUnicas()
        {
            var metas = new List<IfcMetadata>(_points.Count);
            foreach (var p in _points) if (p.Meta != null) metas.Add(p.Meta);

            var etiquetas = ConstruirEtiquetasUnicas(metas);
            foreach (var p in _points)
                if (p.Meta != null && etiquetas.TryGetValue(p.Meta.globalId, out string etiqueta))
                    p.DisplayName = etiqueta;
        }

        /// <summary>
        /// Elige un punto representativo por sala: el más próximo al centro geométrico de los
        /// puntos de esa sala.
        ///
        /// Se prefiere el más céntrico al primero de la lista porque es el que ofrece una vista
        /// más representativa del espacio al llegar. En una sala alargada, aterrizar en un
        /// extremo obliga al usuario a girarse para entender dónde está.
        ///
        /// Solo se consideran los puntos "Esfera..." del modelo. Los nodos de puerta, aunque
        /// tengan sala asignada, están en el umbral y no representan la estancia; además
        /// duplicarían entradas del menú.
        /// </summary>
        private void CalcularOrbesPrincipales()
        {
            var porSala = new Dictionary<string, List<NavPointData>>();

            foreach (var p in _points)
            {
                if (p.Meta == null || p.Meta.ifcType != SceneModelIndex.NavPointIfcType) continue;
                if (string.IsNullOrEmpty(p.Sala)) continue;

                if (!porSala.TryGetValue(p.Sala, out var lista))
                    porSala[p.Sala] = lista = new List<NavPointData>();
                lista.Add(p);
            }

            _orbesPrincipales.Clear();
            foreach (var par in porSala)
            {
                Vector3 centro = Vector3.zero;
                foreach (var p in par.Value) centro += p.Pos;
                centro /= par.Value.Count;

                NavPointData mejor = null;
                float mejorDist = float.MaxValue;
                foreach (var p in par.Value)
                {
                    float d = Vector3.Distance(p.Pos, centro);
                    if (d < mejorDist) { mejorDist = d; mejor = p; }
                }

                if (mejor != null) _orbesPrincipales.Add((par.Key, mejor));
            }

            // Orden alfabético estable: el usuario busca una sala por su nombre, no por su
            // posición en el edificio.
            _orbesPrincipales.Sort((a, b) => string.Compare(a.Sala, b.Sala, System.StringComparison.CurrentCulture));

            Debug.Log($"[DigitalTwin] Menú de zonas: {_orbesPrincipales.Count} salas con punto representativo.");
        }

        /// <summary>
        /// Lleva la cámara al punto representativo de la sala indicada. Devuelve false si la sala
        /// no existe o si ya se está en ella.
        /// </summary>
        public bool ViajarASala(string sala)
        {
            if (IsTransitioning || string.IsNullOrEmpty(sala)) return false;

            foreach (var (nombre, punto) in _orbesPrincipales)
            {
                if (nombre != sala) continue;
                if (punto == _current) return false;

                TravelTo(punto);
                return true;
            }
            return false;
        }

        /// <summary>
        /// Etiquetas ÚNICAS para una colección de nodos: donde varios comparten el nombre base
        /// (dos puntos de la misma sala se llaman ambos «Comedor»; hay salas con hasta trece),
        /// se les añade un ordinal estable «Comedor · 1», «Comedor · 2» por orden de etiqueta
        /// IFC. Sin esto, el registro de la prueba del 14-08 mostraba «Transito hacia 'Comedor'»
        /// estando en «Comedor», y el usuario no podía saber a cuál de los dos iba.
        ///
        /// Es compartida por las dos versiones (los indicadores de escritorio y los carteles del
        /// visor deben decir lo mismo), y devuelve un mapa GlobalId → etiqueta para que cada
        /// consumidor lo aplique a su presentación.
        /// </summary>
        public static Dictionary<string, string> ConstruirEtiquetasUnicas(IEnumerable<IfcMetadata> metas)
        {
            var porNombre = new Dictionary<string, List<IfcMetadata>>();
            foreach (var meta in metas)
            {
                if (meta == null || string.IsNullOrEmpty(meta.globalId)) continue;
                string nombre = BuildDisplayName(meta);
                if (!porNombre.TryGetValue(nombre, out var lista))
                    porNombre[nombre] = lista = new List<IfcMetadata>();
                lista.Add(meta);
            }

            var etiquetas = new Dictionary<string, string>();
            foreach (var par in porNombre)
            {
                if (par.Value.Count == 1)
                {
                    etiquetas[par.Value[0].globalId] = par.Key;
                    continue;
                }

                // Orden estable por etiqueta IFC numérica (la de Revit), no por orden de
                // aparición: así el «· 2» es el mismo punto en todas las ejecuciones.
                par.Value.Sort((a, b) =>
                {
                    bool na = long.TryParse(a.ifcTag, out long ta);
                    bool nb = long.TryParse(b.ifcTag, out long tb);
                    if (na && nb) return ta.CompareTo(tb);
                    if (na != nb) return na ? -1 : 1;
                    return string.CompareOrdinal(a.globalId, b.globalId);
                });
                for (int i = 0; i < par.Value.Count; i++)
                    etiquetas[par.Value[i].globalId] = $"{par.Key} · {i + 1}";
            }
            return etiquetas;
        }

        /// <summary>
        /// Nombre con el que un nodo se presenta al usuario. Público porque la versión de
        /// Realidad Aumentada etiqueta sus indicadores de destino con exactamente el mismo
        /// texto: dos rótulos distintos para el mismo punto romperían el reconocimiento entre
        /// versiones.
        /// </summary>
        public static string BuildDisplayName(IfcMetadata meta)
        {
            string room = meta.GetValue("Otros", "LOC_Localizacion4");

            // Las puertas se etiquetan como tales: en un umbral, "Puerta · Comedor" orienta mucho
            // mejor que el nombre de la sala a secas, que haría pensar que ya se está dentro.
            if (meta.ifcType == "IfcDoor")
                return string.IsNullOrEmpty(room) ? "Puerta" : $"Puerta · {room}";

            if (!string.IsNullOrEmpty(room)) return room;
            return string.IsNullOrEmpty(meta.ifcTag) ? meta.ifcName : $"Punto {meta.ifcTag}";
        }

        private NavPointData FindNearest(Vector3 fromPosition, NavPointData exclude)
        {
            NavPointData best = null;
            float bestDist = float.MaxValue;
            foreach (var p in _points)
            {
                if (p == exclude) continue;
                float d = Vector3.Distance(fromPosition, p.Pos);
                if (d < bestDist) { bestDist = d; best = p; }
            }
            return best;
        }

        private void BuildUI()
        {
            var root = RuntimeUIFactory.CreateRect(_canvas.transform, "TourHotspots");
            RuntimeUIFactory.StretchToParent(root);
            _hotspotLayer = root;

            for (int i = 0; i < MaxHotspotsShown; i++)
                _pool.Add(CreateHotspotSlot(i));

            // Etiqueta discreta arriba a la izquierda con el punto actual (ayuda de orientación).
            var labelRect = RuntimeUIFactory.CreateRect(_canvas.transform, "CurrentPointLabel");
            labelRect.anchorMin = new Vector2(0, 1);
            labelRect.anchorMax = new Vector2(0, 1);
            labelRect.pivot = new Vector2(0, 1);
            labelRect.anchoredPosition = new Vector2(16, -16);
            labelRect.sizeDelta = new Vector2(360, 30);
            _currentPointLabel = RuntimeUIFactory.CreateText(labelRect, "Label", "", 20, TextAnchor.MiddleLeft, new Color(1, 1, 1, 0.85f), FontStyle.Bold);
            RuntimeUIFactory.StretchToParent((RectTransform)_currentPointLabel.transform);
        }

        private HotspotSlot CreateHotspotSlot(int index)
        {
            var root = RuntimeUIFactory.CreateRect(_hotspotLayer, $"Hotspot_{index}");
            root.sizeDelta = new Vector2(56, 56);
            root.gameObject.SetActive(false);

            var ring = RuntimeUIFactory.CreateIcon(root, "Ring", RuntimeUIFactory.RingSprite(), new Color(1f, 0.82f, 0.2f, 0.95f));
            RuntimeUIFactory.StretchToParent((RectTransform)ring.transform);

            var dot = RuntimeUIFactory.CreateIcon(root, "Dot", RuntimeUIFactory.CircleSprite(), new Color(1f, 0.82f, 0.2f, 0.9f));
            var dotRect = (RectTransform)dot.transform;
            dotRect.anchorMin = dotRect.anchorMax = new Vector2(0.5f, 0.5f);
            dotRect.sizeDelta = new Vector2(14, 14);

            var labelRect = RuntimeUIFactory.CreateRect(root, "Label");
            labelRect.anchorMin = new Vector2(0.5f, 0f);
            labelRect.anchorMax = new Vector2(0.5f, 0f);
            labelRect.pivot = new Vector2(0.5f, 1f);
            labelRect.anchoredPosition = new Vector2(0, -6);
            labelRect.sizeDelta = new Vector2(160, 34);
            var label = RuntimeUIFactory.CreateText(labelRect, "Text", "", 16, TextAnchor.UpperCenter, Color.white, FontStyle.Bold);
            RuntimeUIFactory.StretchToParent((RectTransform)label.transform);

            var slot = new HotspotSlot { Root = root, Ring = ring, Label = label };
            slot.ClickHandle = ClickRouter.Instance.Register(
                root,
                () => { if (slot.Target != null && !IsTransitioning) TravelTo(slot.Target); },
                sortOrder: 10,
                isActive: () => root.gameObject.activeSelf);

            return slot;
        }

        private void Update()
        {
            if (_current == null) return;

            _currentPointLabel.text = $"Punto actual: {_current.DisplayName}";

            if (!IsTransitioning)
            {
                _refreshTimer -= Time.deltaTime;
                if (_refreshTimer <= 0f)
                {
                    _refreshTimer = HotspotRefreshInterval;
                    RefreshHotspots();
                }
            }

            PositionActiveHotspots();
        }

        /// <summary>
        /// Elige qué puntos de navegación se ofrecen desde el punto actual.
        ///
        /// Criterio: proximidad, no línea de visión. La versión anterior descartaba cualquier
        /// punto cuya línea recta estuviera interrumpida por geometría, lo que sobre el papel es
        /// más "realista" pero en este modelo resultaba inservible: el edificio está lleno de
        /// tabiques, mamparas de vidrio y puertas cerradas que bloquean la línea sin impedir en
        /// absoluto que un operario llegue andando. El resultado era quedarse sin salidas.
        ///
        /// Un tour por puntos no es una simulación física: el usuario se teletransporta, no
        /// camina. Que un salto atraviese un tabique es aceptable; quedarse encerrado, no.
        ///
        /// Sobre la altura: el radio se mide en horizontal, ignorando la componente vertical,
        /// pero eso por sí solo NO evita saltar de planta, sino todo lo contrario (un punto justo
        /// encima tendría distancia horizontal casi nula y saldría el primero). Por eso hay
        /// además un filtro explícito de diferencia de altura.
        /// </summary>
        private void RefreshHotspots()
        {
            var visible = _grafo != null ? SeleccionarPorGrafo() : null;
            if (visible == null) visible = SeleccionarPorProximidad();

            for (int i = 0; i < _pool.Count; i++)
            {
                var slot = _pool[i];
                if (i < visible.Count)
                {
                    slot.Target = visible[i];
                    slot.Label.text = visible[i].DisplayName;
                    slot.Root.gameObject.SetActive(true);
                }
                else
                {
                    slot.Target = null;
                    slot.Root.gameObject.SetActive(false);
                }
            }
        }

        /// <summary>
        /// Vecinos del punto actual segun el grafo precalculado. Devuelve null si el grafo no
        /// cubre este punto, para que quien llama recurra al criterio de proximidad.
        ///
        /// La definición de qué es alcanzable NO vive aquí sino en <see cref="NavReachability"/>,
        /// compartida con la versión de Realidad Aumentada: una sola implementación para los dos
        /// arranques. Este método conserva solo lo presentacional: traducir índices del grafo a
        /// puntos de la escena, ordenarlos por cercanía y acotarlos al pool de indicadores. El
        /// resultado es el mismo que producía el bucle anterior (misma lista, mismo orden).
        /// </summary>
        private List<NavPointData> SeleccionarPorGrafo()
        {
            int idx = _grafo.IndiceDe(_current.Meta != null ? _current.Meta.globalId : null);
            if (idx < 0) return null;

            Vector3 origen = _current.Pos;
            var vecinos = new List<NavPointData>();

            foreach (int v in NavReachability.VecinosAlcanzables(_grafo, idx))
            {
                if (_puntosPorGlobalId.TryGetValue(_grafo.Nodos[v].GlobalId, out var punto) && punto != _current)
                    vecinos.Add(punto);
            }

            if (vecinos.Count == 0) return null;

            // Se ordenan por cercania para que, si hay mas vecinos que huecos, se ofrezcan los
            // saltos cortos antes que los largos.
            vecinos.Sort((a, b) => DistanciaHorizontal(origen, a.Pos)
                                  .CompareTo(DistanciaHorizontal(origen, b.Pos)));

            if (vecinos.Count > _pool.Count) vecinos.RemoveRange(_pool.Count, vecinos.Count - _pool.Count);
            return vecinos;
        }

        private List<NavPointData> SeleccionarPorProximidad()
        {
            Vector3 origen = _current.Pos;

            var candidatos = _points
                .Where(p => p != _current)
                .Select(p => new
                {
                    Punto = p,
                    Dist = DistanciaHorizontal(origen, p.Pos),
                    Desnivel = Mathf.Abs(p.Pos.y - origen.y)
                })
                .Where(c => c.Desnivel <= ToleranciaVertical)
                .OrderBy(c => c.Dist)
                .ToList();

            // Si el filtro de altura deja fuera absolutamente todo (punto suelto en un altillo,
            // tolerancia mal ajustada), se ignora antes que dejar al usuario sin salidas.
            if (candidatos.Count == 0)
            {
                candidatos = _points
                    .Where(p => p != _current)
                    .Select(p => new
                    {
                        Punto = p,
                        Dist = DistanciaHorizontal(origen, p.Pos),
                        Desnivel = Mathf.Abs(p.Pos.y - origen.y)
                    })
                    .OrderBy(c => c.Dist)
                    .ToList();
            }

            var visible = new List<NavPointData>();

            // Reserva de salida: se coge primero el punto más cercano de otra sala. Sin esto, en
            // una habitación con varios puntos propios los huecos se llenarían todos con ellos y
            // no habría forma de salir sin ir dando saltos hasta el borde. Es la única propiedad
            // del grafo de navegación que de verdad hace falta aquí, y sale gratis usando el
            // pset de localización que los puntos ya traen del IFC.
            if (GarantizarSalidaDeSala && !string.IsNullOrEmpty(_current.Sala))
            {
                var salida = candidatos.FirstOrDefault(c => !string.IsNullOrEmpty(c.Punto.Sala) &&
                                                            c.Punto.Sala != _current.Sala);
                if (salida != null) visible.Add(salida.Punto);
            }

            foreach (var c in candidatos)
            {
                if (visible.Count >= MaxHotspotsShown) break;
                if (visible.Contains(c.Punto)) continue;

                bool dentroDelRadio = c.Dist <= MaxHotspotDistance;
                bool haceFaltaParaElMinimo = visible.Count < MinHotspotsAlwaysShown;
                if (!dentroDelRadio && !haceFaltaParaElMinimo) continue;

                // Evita apilar dos hotspots casi en la misma dirección: se solaparían en pantalla
                // y desperdiciarían un hueco sin ofrecer un destino distinguible.
                if (SeparacionAngularMinima > 0f && !haceFaltaParaElMinimo &&
                    DemasiadoAlineadoConAlguno(origen, c.Punto, visible)) continue;

                visible.Add(c.Punto);
            }

            return visible;
        }

        private void PositionActiveHotspots()
        {
            foreach (var slot in _pool)
            {
                if (!slot.Root.gameObject.activeSelf || slot.Target == null) continue;

                Vector3 world = slot.Target.Pos;
                Vector3 screen = _camera.WorldToScreenPoint(world);

                if (screen.z <= 0f)
                {
                    slot.Root.gameObject.SetActive(false);
                    continue;
                }

                slot.Root.position = screen;

                float dist = Vector3.Distance(_current.Pos, world);
                float scale = Mathf.Clamp(1.3f - dist / (MaxHotspotDistance * 1.5f), 0.55f, 1.15f);
                slot.Root.localScale = Vector3.one * scale;
            }
        }

        private void TravelTo(NavPointData target)
        {
            if (target == null || target == _current || IsTransitioning) return;
            StartCoroutine(TransitionRoutine(target));
        }

        private IEnumerator TransitionRoutine(NavPointData target)
        {
            IsTransitioning = true;

            // Salto largo: se resuelve de forma instantánea en vez de con desplazamiento continuo.
            //
            // El desplazamiento interpolado tiene sentido entre puntos cercanos, porque conserva
            // la orientación espacial del operario dentro del edificio. En un salto de decenas de
            // metros deja de tenerlo: la cámara atraviesa varias estancias y tabiques a velocidad
            // constante, lo que desorienta más que un corte y además obliga a esperar. Es la misma
            // razón por la que los recorridos fotográficos cortan entre panorámicas lejanas.
            if (Vector3.Distance(_camera.transform.position, target.Pos) > DistanciaSaltoInstantaneo)
            {
                _camera.transform.position = target.Pos;
                _current = target;
                IsTransitioning = false;
                GetComponent<TourCameraLook>()?.SyncFromTransform();
                PuertaTransparente.AlLlegarANodo(_current.Meta);
                RefreshHotspots();
                yield break;
            }

            Vector3 startPos = _camera.transform.position;
            Quaternion startRot = _camera.transform.rotation;
            Vector3 endPos = target.Pos;

            Vector3 flatDir = Vector3.ProjectOnPlane(endPos - startPos, Vector3.up);
            Quaternion travelLookRot = flatDir.sqrMagnitude > 0.0001f
                ? Quaternion.LookRotation(flatDir.normalized, Vector3.up)
                : startRot;
            Quaternion endRotBlend = Quaternion.Slerp(startRot, travelLookRot, TurnTowardsTravelBlend);

            float t = 0f;
            while (t < TransitionDuration)
            {
                t += Time.deltaTime;
                float e = EaseInOutCubic(Mathf.Clamp01(t / TransitionDuration));
                _camera.transform.position = Vector3.Lerp(startPos, endPos, e);
                _camera.transform.rotation = Quaternion.Slerp(startRot, endRotBlend, e);
                yield return null;
            }

            _camera.transform.position = endPos;
            _current = target;
            IsTransitioning = false;

            GetComponent<TourCameraLook>()?.SyncFromTransform();
            // Al ocupar un nodo puerta, su hoja deja de dibujarse; al ocupar cualquier otro,
            // la hoja oculta (si la hay) se restituye. Ver PuertaTransparente.
            PuertaTransparente.AlLlegarANodo(_current.Meta);
            RefreshHotspots();
        }

        /// <summary>Distancia en planta, ignorando la altura.</summary>
        private static float DistanciaHorizontal(Vector3 a, Vector3 b)
        {
            a.y = 0f; b.y = 0f;
            return Vector3.Distance(a, b);
        }

        /// <summary>
        /// ¿El candidato queda casi en la misma dirección que algún hotspot ya elegido? Se compara
        /// en planta, que es como se perciben en pantalla.
        /// </summary>
        private bool DemasiadoAlineadoConAlguno(Vector3 origen, NavPointData candidato,
                                                List<NavPointData> yaElegidos)
        {
            Vector3 dirCand = candidato.Pos - origen;
            dirCand.y = 0f;
            if (dirCand.sqrMagnitude < 0.0001f) return false;
            dirCand.Normalize();

            foreach (var otro in yaElegidos)
            {
                Vector3 dirOtro = otro.Pos - origen;
                dirOtro.y = 0f;
                if (dirOtro.sqrMagnitude < 0.0001f) continue;
                dirOtro.Normalize();

                if (Vector3.Angle(dirCand, dirOtro) < SeparacionAngularMinima) return true;
            }
            return false;
        }

        private static float EaseInOutCubic(float x)
        {
            return x < 0.5f ? 4f * x * x * x : 1f - Mathf.Pow(-2f * x + 2f, 3f) / 2f;
        }
    }
}
