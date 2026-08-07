using DigitalTwin.UI;
using UnityEngine;

namespace DigitalTwin.Navigation
{
    /// <summary>
    /// Control de "mirar alrededor" desde un punto fijo, al estilo de un tour virtual
    /// (arrastrar para orbitar la vista; no hay desplazamiento libre tipo FPS). Se desactiva
    /// mientras la cámara está en transición entre puntos de navegación.
    /// </summary>
    public class TourCameraLook : MonoBehaviour
    {
        public float Sensitivity = 0.15f;
        public float MinPitch = -70f;
        public float MaxPitch = 80f;

        private float _yaw;
        private float _pitch;
        private TourNavigationManager _tour;

        private void Start()
        {
            _tour = GetComponent<TourNavigationManager>();
            SyncFromTransform();
        }

        /// <summary>Realinea el yaw/pitch internos con la rotación actual de la cámara. Se llama
        /// tras cada transición para que el arrastre continúe suavemente desde donde ha quedado
        /// mirando la cámara al llegar al nuevo punto.</summary>
        public void SyncFromTransform()
        {
            Vector3 euler = transform.eulerAngles;
            _pitch = NormalizePitch(euler.x);
            _yaw = euler.y;
        }

        private static float NormalizePitch(float x)
        {
            return x > 180f ? x - 360f : x;
        }

        private void Update()
        {
            if (_tour != null && _tour.IsTransitioning) return;

            var gesture = PointerGesture.Instance;
            if (!gesture.IsDragging || gesture.PressStartedOverUI) return;

            _yaw += gesture.FrameDelta.x * Sensitivity;
            _pitch -= gesture.FrameDelta.y * Sensitivity;
            _pitch = Mathf.Clamp(_pitch, MinPitch, MaxPitch);

            transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);
        }
    }
}
