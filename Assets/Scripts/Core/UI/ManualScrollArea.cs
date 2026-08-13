using UnityEngine;
using UnityEngine.InputSystem;

namespace DigitalTwin.UI
{
    /// <summary>
    /// Scroll vertical simple con la rueda del ratón, sin ScrollRect/EventSystem (ver
    /// ClickRouter para el motivo). Se coloca en el "viewport"; mueve un "content" hijo
    /// dentro de los límites de su propio alto.
    /// </summary>
    public class ManualScrollArea : MonoBehaviour
    {
        public RectTransform Viewport;
        public RectTransform Content;
        public float ScrollSpeed = 40f;

        public void ResetScroll()
        {
            if (Content == null) return;
            var pos = Content.anchoredPosition;
            pos.y = 0;
            Content.anchoredPosition = pos;
        }

        /// <summary>
        /// Desplaza el contenido una cantidad en unidades de layout, acotada a los límites.
        /// Es la vía de scroll de la versión inmersiva: allí no hay rueda de ratón, así que el
        /// controlador de interacción empuja este valor desde el joystick del mando mientras el
        /// rayo señala el panel. Positivo baja por el contenido.
        /// </summary>
        public void Desplazar(float delta)
        {
            if (Viewport == null || Content == null) return;
            float maxScroll = Mathf.Max(0f, Content.rect.height - Viewport.rect.height);
            var pos = Content.anchoredPosition;
            pos.y = Mathf.Clamp(pos.y + delta, 0f, maxScroll);
            Content.anchoredPosition = pos;
        }

        /// <summary>Reajusta la posición de scroll actual a los límites válidos. Útil justo
        /// después de reconstruir el contenido (p.ej. al expandir/colapsar un Pset), cuando la
        /// altura del contenido cambia y el scroll anterior podría quedar fuera de rango.</summary>
        public void ClampNow()
        {
            if (Viewport == null || Content == null) return;
            float maxScroll = Mathf.Max(0f, Content.rect.height - Viewport.rect.height);
            var pos = Content.anchoredPosition;
            pos.y = Mathf.Clamp(pos.y, 0f, maxScroll);
            Content.anchoredPosition = pos;
        }

        private void Update()
        {
            if (Viewport == null || Content == null || !Viewport.gameObject.activeInHierarchy) return;
            if (Mouse.current == null) return;

            Vector2 pointer = Mouse.current.position.ReadValue();
            if (!RectTransformUtility.RectangleContainsScreenPoint(Viewport, pointer, null)) return;

            float scroll = Mouse.current.scroll.ReadValue().y;
            if (Mathf.Approximately(scroll, 0f)) return;

            float viewportHeight = Viewport.rect.height;
            float contentHeight = Content.rect.height;
            float maxScroll = Mathf.Max(0f, contentHeight - viewportHeight);

            var pos = Content.anchoredPosition;
            pos.y = Mathf.Clamp(pos.y - scroll * ScrollSpeed * 0.02f, 0f, maxScroll);
            Content.anchoredPosition = pos;
        }
    }
}
