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

        [Tooltip("Cuántos puntos se muestran como mucho.")]
        public int MaxHotspotsShown = 4;

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

        private readonly List<NavPointData> _points = new List<NavPointData>();
        private NavPointData _current;
        private Camera _camera;
        private Canvas _canvas;
        private RectTransform _hotspotLayer;
        private Text _currentPointLabel;
        private readonly List<HotspotSlot> _pool = new List<HotspotSlot>();
        private float _refreshTimer;

        /// <summary>
        /// Grafo precalculado en el Editor (Tools > IFC > Generar grafo de navegacion). Si no
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
            BuildUI();

            if (_points.Count == 0)
            {
                Debug.LogWarning("[DigitalTwin] Sin puntos 'Esfera...' no hay tour de navegación posible.");
                return;
            }

            _current = FindNearest(_camera.transform.position, null);
            _camera.transform.position = _current.Transform.position;
            GetComponent<TourCameraLook>()?.SyncFromTransform();

            RefreshHotspots();
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
                                 "en salas diafanas. Generalo con Tools > IFC > Generar grafo de navegacion.");
                return;
            }

            int reconocidos = 0;
            foreach (var nodo in _grafo.Nodos)
                if (_puntosPorGlobalId.ContainsKey(nodo.GlobalId)) reconocidos++;

            Debug.Log($"[DigitalTwin] Grafo de navegacion cargado: {_grafo.Nodos.Count} nodos, " +
                      $"{_grafo.ContarAristas()} aristas, generado el {_grafo.GeneradoEl}. " +
                      $"{reconocidos} de {_grafo.Nodos.Count} nodos localizados en la escena.");

            if (reconocidos < _grafo.Nodos.Count)
                Debug.LogWarning("[DigitalTwin] Hay nodos del grafo que no corresponden a ningun punto de la " +
                                 "escena. Suele significar que el modelo se ha reimportado con GlobalId distintos: " +
                                 "vuelve a generar el grafo.");
        }

        private static string BuildDisplayName(IfcMetadata meta)
        {
            string room = meta.GetValue("Otros", "LOC_Localizacion4");
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
                float d = Vector3.Distance(fromPosition, p.Transform.position);
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
        /// </summary>
        private List<NavPointData> SeleccionarPorGrafo()
        {
            int idx = _grafo.IndiceDe(_current.Meta != null ? _current.Meta.globalId : null);
            if (idx < 0) return null;

            Vector3 origen = _current.Transform.position;
            var vecinos = new List<NavPointData>();

            foreach (int v in _grafo.Nodos[idx].Vecinos)
            {
                if (v < 0 || v >= _grafo.Nodos.Count) continue;
                if (_puntosPorGlobalId.TryGetValue(_grafo.Nodos[v].GlobalId, out var punto) && punto != _current)
                    vecinos.Add(punto);
            }

            if (vecinos.Count == 0) return null;

            // Se ordenan por cercania para que, si hay mas vecinos que huecos, se ofrezcan los
            // saltos cortos antes que los largos.
            vecinos.Sort((a, b) => DistanciaHorizontal(origen, a.Transform.position)
                                  .CompareTo(DistanciaHorizontal(origen, b.Transform.position)));

            if (vecinos.Count > _pool.Count) vecinos.RemoveRange(_pool.Count, vecinos.Count - _pool.Count);
            return vecinos;
        }

        private List<NavPointData> SeleccionarPorProximidad()
        {
            Vector3 origen = _current.Transform.position;

            var candidatos = _points
                .Where(p => p != _current)
                .Select(p => new
                {
                    Punto = p,
                    Dist = DistanciaHorizontal(origen, p.Transform.position),
                    Desnivel = Mathf.Abs(p.Transform.position.y - origen.y)
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
                        Dist = DistanciaHorizontal(origen, p.Transform.position),
                        Desnivel = Mathf.Abs(p.Transform.position.y - origen.y)
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

                Vector3 world = slot.Target.Transform.position;
                Vector3 screen = _camera.WorldToScreenPoint(world);

                if (screen.z <= 0f)
                {
                    slot.Root.gameObject.SetActive(false);
                    continue;
                }

                slot.Root.position = screen;

                float dist = Vector3.Distance(_current.Transform.position, world);
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

            Vector3 startPos = _camera.transform.position;
            Quaternion startRot = _camera.transform.rotation;
            Vector3 endPos = target.Transform.position;

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
            Vector3 dirCand = candidato.Transform.position - origen;
            dirCand.y = 0f;
            if (dirCand.sqrMagnitude < 0.0001f) return false;
            dirCand.Normalize();

            foreach (var otro in yaElegidos)
            {
                Vector3 dirOtro = otro.Transform.position - origen;
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
