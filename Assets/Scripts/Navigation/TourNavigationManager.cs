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
        public float MaxHotspotDistance = 15f;
        public int MinHotspotsAlwaysShown = 3;
        public int MaxHotspotsShown = 8;

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

        // El aviso de "todo ocluido" se emite una sola vez: RefreshHotspots se ejecuta varias
        // veces por segundo y, sin esta guarda, inundaría la consola y taparía el resto de logs.
        private bool _avisoOclusionTotalMostrado;

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
                    DisplayName = BuildDisplayName(meta)
                });
            }

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

        private void RefreshHotspots()
        {
            Vector3 eye = _current.Transform.position + Vector3.up * 0.05f;
            int mask = ColliderBootstrapper.OcclusionMask();

            var candidates = _points
                .Where(p => p != _current)
                .Select(p => new { Point = p, Dist = Vector3.Distance(eye, p.Transform.position) })
                .OrderBy(c => c.Dist)
                .ToList();

            var visible = new List<NavPointData>();
            Collider primerBloqueo = null;

            foreach (var c in candidates)
            {
                Vector3 target = c.Point.Transform.position + Vector3.up * 0.05f;
                bool occluded = Physics.Linecast(eye, target, out RaycastHit hit, mask) &&
                                (target - eye).magnitude - hit.distance > 0.15f;
                if (occluded)
                {
                    if (primerBloqueo == null) primerBloqueo = hit.collider;
                    continue;
                }

                if (c.Dist <= MaxHotspotDistance || visible.Count < MinHotspotsAlwaysShown)
                    visible.Add(c.Point);

                if (visible.Count >= MaxHotspotsShown) break;
            }

            // Red de seguridad: si la oclusión ha descartado todos los candidatos, se muestran
            // igualmente los más cercanos.
            //
            // La comprobación de MinHotspotsAlwaysShown del bucle no bastaba, porque el `continue`
            // por oclusión se ejecuta ANTES de llegar a ella: con todos los puntos tapados, el
            // tour se quedaba sin ningún hotspot y por tanto sin salida, que es exactamente el
            // caso que esa garantía pretendía evitar. Ocurría, por ejemplo, mientras los
            // volúmenes IfcSpace conservaban collider y envolvían habitaciones enteras.
            //
            // Se prefiere un hotspot geométricamente imperfecto (que atraviese un tabique) a
            // dejar al operario encerrado sin poder moverse: lo segundo parece que la aplicación
            // está rota, lo primero como mucho resulta poco elegante.
            if (visible.Count == 0 && candidates.Count > 0)
            {
                int cuantos = Mathf.Min(MinHotspotsAlwaysShown, candidates.Count);
                for (int i = 0; i < cuantos; i++) visible.Add(candidates[i].Point);

                if (!_avisoOclusionTotalMostrado)
                {
                    _avisoOclusionTotalMostrado = true;
                    string culpable = primerBloqueo != null
                        ? $"'{primerBloqueo.name}' (capa {LayerMask.LayerToName(primerBloqueo.gameObject.layer)})"
                        : "desconocido";
                    Debug.LogWarning($"[DigitalTwin] Desde '{_current.DisplayName}' la comprobación de línea de " +
                                     $"visión descarta los {candidates.Count} puntos de navegación. Se muestran los " +
                                     $"{cuantos} más cercanos de todos modos para no dejar el tour sin salida. " +
                                     $"Primer obstáculo detectado: {culpable}. Si es geometría que no debería " +
                                     "bloquear (un volumen de espacio, un techo, un falso suelo), hay que excluirla " +
                                     "de la máscara de oclusión igual que se hace con IfcSpace.");
                }
            }

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

        private static float EaseInOutCubic(float x)
        {
            return x < 0.5f ? 4f * x * x * x : 1f - Mathf.Pow(-2f * x + 2f, 3f) / 2f;
        }
    }
}
