using DigitalTwin.Core;
using DigitalTwin.Navigation;
using DigitalTwin.UI;
using IFCImporter;
using UnityEngine;

namespace DigitalTwin.Metadata
{
    /// <summary>
    /// Selección de elementos del modelo por clic (Fase 2). Usa PointerGesture para distinguir
    /// un clic real de un arrastre de "mirar alrededor" (Fase 1), y hace raycast contra los
    /// MeshCollider añadidos por ColliderBootstrapper. Clicar en vacío, o en un punto de
    /// navegación (Esfera...), cierra el panel en vez de mostrar metadatos: esos marcadores no
    /// son elementos reales del edificio.
    /// </summary>
    public class ElementSelector : MonoBehaviour
    {
        public const float MaxRayDistance = 500f;

        private Camera _camera;
        private MetadataPanelController _panel;
        private TourNavigationManager _tour;

        public void Initialize(MetadataPanelController panel, TourNavigationManager tour)
        {
            _camera = Camera.main;
            _panel = panel;
            _tour = tour;
        }

        private void Update()
        {
            if (_camera == null || _panel == null) return;

            var gesture = PointerGesture.Instance;
            if (!gesture.ClickReleasedThisFrame) return;
            if (_tour != null && _tour.IsTransitioning) return;

            Ray ray = _camera.ScreenPointToRay(gesture.ClickPosition);
            if (Physics.Raycast(ray, out RaycastHit hit, MaxRayDistance, ColliderBootstrapper.SelectionMask(), QueryTriggerInteraction.Ignore))
            {
                var meta = hit.collider.GetComponentInParent<IfcMetadata>();
                if (meta != null && meta.ifcType != SceneModelIndex.NavPointIfcType)
                {
                    _panel.Show(meta);
                    return;
                }
            }

            _panel.Hide();
        }
    }
}
