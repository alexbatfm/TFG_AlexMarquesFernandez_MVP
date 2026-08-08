using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.InputSystem;

namespace DigitalTwin.UI
{
    /// <summary>
    /// Router de clics minimalista, sin dependencia de EventSystem/GraphicRaycaster.
    ///
    /// Por qué existe: el proyecto tiene "Active Input Handling" = Input System Package
    /// exclusivamente (ProjectSettings > activeInputHandler: 1), lo que significa que la
    /// clase legacy `UnityEngine.Input` no funciona. El módulo de UI estándar
    /// (StandaloneInputModule) tampoco sirve por el mismo motivo, y usar
    /// InputSystemUIInputModule requiere un asset de acciones que no existe en el proyecto.
    ///
    /// Para no depender de una configuración de Editor que no se puede verificar sin abrir
    /// Unity, este router hace la comprobación de "¿el puntero está sobre este rect?" a mano
    /// con RectTransformUtility, usando la posición del puntero leída directamente de
    /// UnityEngine.InputSystem (Mouse/Touchscreen). Funciona igual en Editor, standalone y
    /// build, sin necesitar EventSystem.
    /// </summary>
    public class ClickTarget
    {
        public RectTransform Rect;
        public Action OnClick;
        public int SortOrder;
        public Func<bool> IsActive;
    }

    /// <summary>
    /// Orden de ejecución explícito: este router debe correr DESPUÉS de
    /// <see cref="PointerGesture"/> (que está en -200) y ANTES de los sistemas de mundo, que
    /// van en el orden por defecto. Ver la explicación del problema en
    /// <see cref="PulsacionIniciadaSobreUI"/>.
    /// </summary>
    [DefaultExecutionOrder(-100)]
    public class ClickRouter : MonoBehaviour
    {
        private static ClickRouter _instance;

        public static ClickRouter Instance
        {
            get
            {
                if (_instance == null)
                {
                    var go = new GameObject("~ClickRouter");
                    DontDestroyOnLoad(go);
                    _instance = go.AddComponent<ClickRouter>();
                }
                return _instance;
            }
        }

        private readonly List<ClickTarget> _targets = new List<ClickTarget>();

        public ClickTarget Register(RectTransform rect, Action onClick, int sortOrder = 0, Func<bool> isActive = null)
        {
            var t = new ClickTarget { Rect = rect, OnClick = onClick, SortOrder = sortOrder, IsActive = isActive };
            _targets.Add(t);
            return t;
        }

        public void Unregister(ClickTarget target)
        {
            if (target != null) _targets.Remove(target);
        }

        /// <summary>Posición actual del puntero (ratón o primer dedo táctil) en coordenadas de pantalla.</summary>
        public static Vector2 PointerPosition()
        {
            if (Mouse.current != null) return Mouse.current.position.ReadValue();
            if (Touchscreen.current != null) return Touchscreen.current.primaryTouch.position.ReadValue();
            return new Vector2(-1, -1);
        }

        /// <summary>True el frame en el que se ha pulsado el botón principal (ratón o toque).</summary>
        public static bool ClickedThisFrame()
        {
            bool mouse = Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame;
            bool touch = Touchscreen.current != null && Touchscreen.current.primaryTouch.press.wasPressedThisFrame;
            return mouse || touch;
        }

        /// <summary>True mientras el botón principal se mantiene pulsado (para arrastrar/mirar alrededor).</summary>
        public static bool IsPressed()
        {
            bool mouse = Mouse.current != null && Mouse.current.leftButton.isPressed;
            bool touch = Touchscreen.current != null && Touchscreen.current.primaryTouch.press.isPressed;
            return mouse || touch;
        }

        /// <summary>
        /// True si el puntero está actualmente sobre cualquier target de UI activo.
        /// Los sistemas de mundo (selección de elementos, orbit de cámara) deben consultar
        /// esto antes de procesar su propio clic/drag para no "atravesar" los paneles de UI.
        /// </summary>
        public bool IsPointerOverUI()
        {
            Vector2 pos = PointerPosition();
            for (int i = 0; i < _targets.Count; i++)
            {
                var t = _targets[i];
                if (t.Rect == null || !t.Rect.gameObject.activeInHierarchy) continue;
                if (t.IsActive != null && !t.IsActive()) continue;
                if (RectTransformUtility.RectangleContainsScreenPoint(t.Rect, pos, null))
                    return true;
            }
            return false;
        }

        private bool _pulsacionSobreUI;
        private int _framePulsacion = -1;

        /// <summary>
        /// True si la pulsación de ESTE fotograma empezó sobre un elemento de UI, evaluado
        /// ANTES de ejecutar el callback correspondiente.
        ///
        /// Existe por un problema que no es evidente: el callback de un botón puede ocultar la
        /// propia UI sobre la que se acaba de pulsar (el botón de cerrar del panel de metadatos
        /// es exactamente ese caso). A partir de ese momento <see cref="IsPointerOverUI"/>
        /// devuelve false, porque el rect ya no está activo. Si otro sistema pregunta después
        /// —por ejemplo la detección de gestos, para decidir si el clic era "de UI" o "de
        /// mundo"— recibe una respuesta engañosa: el acto de cerrar el panel ha borrado la
        /// prueba de que la pulsación fue sobre él.
        ///
        /// El síntoma era que al pulsar cerrar, el panel se cerraba y se reabría al instante:
        /// el selector de elementos daba por bueno el clic, lanzaba su raycast por donde estaba
        /// el panel, golpeaba el elemento que había detrás y lo volvía a mostrar.
        ///
        /// Guardar el valor con marca de fotograma lo hace además independiente del orden de
        /// ejecución de los scripts, que es la causa raíz del problema.
        /// </summary>
        public bool PulsacionIniciadaSobreUI => _pulsacionSobreUI && _framePulsacion == Time.frameCount;

        private void Update()
        {
            if (!ClickedThisFrame()) return;
            Vector2 pos = PointerPosition();

            // Se evalúa y se guarda ANTES de invocar nada: después puede ser ya falso.
            _pulsacionSobreUI = IsPointerOverUI();
            _framePulsacion = Time.frameCount;

            ClickTarget best = null;
            for (int i = 0; i < _targets.Count; i++)
            {
                var t = _targets[i];
                if (t.Rect == null || !t.Rect.gameObject.activeInHierarchy) continue;
                if (t.IsActive != null && !t.IsActive()) continue;
                if (!RectTransformUtility.RectangleContainsScreenPoint(t.Rect, pos, null)) continue;
                if (best == null || t.SortOrder > best.SortOrder) best = t;
            }
            best?.OnClick?.Invoke();
        }
    }
}
