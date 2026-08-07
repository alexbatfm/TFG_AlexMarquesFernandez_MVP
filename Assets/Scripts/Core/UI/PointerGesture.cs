using UnityEngine;

namespace DigitalTwin.UI
{
    /// <summary>
    /// Distingue "clic" (pulsar y soltar sin apenas mover el puntero) de "arrastre" (pulsar y
    /// mover, usado para orbitar la cámara). Ambos sistemas -selección de elementos (Fase 2) y
    /// mirar alrededor (Fase 1)- consultan esta clase en vez de leer el puntero cada uno por su
    /// cuenta, para que un arrastre para mirar alrededor nunca se interprete además como un
    /// clic de selección.
    /// </summary>
    public class PointerGesture : MonoBehaviour
    {
        private static PointerGesture _instance;

        public static PointerGesture Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("~PointerGesture");
                    Object.DontDestroyOnLoad(go);
                    _instance = go.AddComponent<PointerGesture>();
                }
                return _instance;
            }
        }

        public const float DragThresholdPixels = 8f;

        public bool IsPressed { get; private set; }
        public bool IsDragging { get; private set; }
        public bool PressStartedOverUI { get; private set; }
        public bool ClickReleasedThisFrame { get; private set; }
        public Vector2 ClickPosition { get; private set; }
        public Vector2 FrameDelta { get; private set; }

        private Vector2 _pressPos;
        private Vector2 _lastPos;

        private void Update()
        {
            ClickReleasedThisFrame = false;
            FrameDelta = Vector2.zero;

            Vector2 pos = ClickRouter.PointerPosition();
            bool pressed = ClickRouter.IsPressed();

            if (pressed && !IsPressed)
            {
                IsPressed = true;
                IsDragging = false;
                _pressPos = pos;
                _lastPos = pos;
                PressStartedOverUI = ClickRouter.Instance.IsPointerOverUI();
            }
            else if (pressed && IsPressed)
            {
                FrameDelta = pos - _lastPos;
                _lastPos = pos;
                if (!IsDragging && Vector2.Distance(pos, _pressPos) > DragThresholdPixels)
                    IsDragging = true;
            }
            else if (!pressed && IsPressed)
            {
                IsPressed = false;
                if (!IsDragging && !PressStartedOverUI)
                {
                    ClickReleasedThisFrame = true;
                    ClickPosition = pos;
                }
                IsDragging = false;
            }
        }
    }
}
