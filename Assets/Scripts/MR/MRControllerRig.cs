using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR;

namespace DigitalTwin.MR
{
    /// <summary>
    /// Mandos de la versión de Realidad Aumentada: crea un anclaje por mano bajo el rig de la
    /// cámara, lo mantiene sincronizado con la pose real del mando, y dibuja desde cada uno un rayo
    /// que indica hacia dónde apunta.
    ///
    /// Por qué existe. La escena solo trae el rig de cabeza: origen, desplazamiento de cámara y
    /// cámara con seguimiento de pose. No hay mandos ni interactores, de modo que el usuario ve el
    /// modelo pero no puede señalar nada. Este componente cubre ese hueco.
    ///
    /// Por qué se lee la entrada por <c>UnityEngine.XR.InputDevices</c> y no con los interactores
    /// del XR Interaction Toolkit. Los interactores del Toolkit necesitan un activo de acciones de
    /// entrada correctamente enlazado, que hay que crear y configurar en el editor; y su interfaz
    /// de programación ha cambiado de forma sustancial entre versiones mayores. Frente a eso, la
    /// consulta directa de nodos y botones es estable, no depende de ningún activo que haya que
    /// mantener sincronizado, y encaja con la decisión transversal del proyecto de construir la
    /// interacción por código en lugar de apoyarse en componentes colocados a mano (véase la
    /// discusión del enrutador de clics sin sistema de eventos). Si en el futuro se necesitara
    /// agarrar y manipular objetos, entonces sí compensaría adoptar el Toolkit completo.
    ///
    /// RESPALDO DE RATÓN EN EL EDITOR (2026-08-14). En modo Play sin visor,
    /// <c>InputDevices</c> devuelve cero dispositivos, y toda la capa de RA quedaba
    /// contemplativa: cuatro tandas se han verificado a ciegas por ese hueco. Cuando NO hay
    /// ningún mando XR válido y se ejecuta en el Editor, el rig sintetiza el rayo desde la
    /// cámara a través del cursor, con el botón izquierdo como gatillo (mismo flanco de
    /// subida), la rueda como joystick vertical y la tecla M como botón de menú. Se elige este
    /// camino y no el simulador de dispositivo del XR Interaction Toolkit por tres razones:
    /// el simulador alimenta el Input System y este rig lee el subsistema heredado (conectarlo
    /// exigiría un segundo camino de lectura completo, con su propio mapeo de poses y botones,
    /// solo para el Editor); el respaldo vive entero bajo <c>#if UNITY_EDITOR</c>, así que en
    /// una compilación NO EXISTE y el comportamiento con mandos reales queda intacto por
    /// construcción; y para lo que se quiere probar —grafo, carteles, tránsito, selección,
    /// panel, puerta transparente, menú de zonas— el ratón es más rápido de manejar que un
    /// mando simulado con teclado. La prioridad es estricta: si en algún momento aparece un
    /// mando XR válido, manda el mando; el registro anuncia cada cambio de fuente para que
    /// nunca haya duda de con qué se está probando.
    ///
    /// Por qué los anclajes cuelgan del desplazamiento de cámara y no de la raíz de la escena. Las
    /// poses que devuelve el sistema están expresadas en el espacio del origen de realidad
    /// extendida. Colgarlos de la raíz haría que los mandos se despegaran de las manos en cuanto el
    /// origen se moviera --- por ejemplo, al anclar el modelo o al teletransportarse.
    /// </summary>
    public class MRControllerRig : MonoBehaviour
    {
        /// <summary>Alcance del rayo, en metros. Suficiente para señalar el fondo de una nave.</summary>
        public float AlcanceRayo = 30f;

        private const float GrosorRayo = 0.004f;

        /// <summary>
        /// Umbral de presión del gatillo. Se usa el eje analógico y no el botón: el botón tiene su
        /// propio umbral interno, distinto entre fabricantes, y con el eje el comportamiento es el
        /// mismo en cualquier mando.
        /// </summary>
        private const float UmbralGatillo = 0.6f;

        private class Mano
        {
            public XRNode Nodo;
            public Transform Anclaje;
            public LineRenderer Linea;
            public bool Valida;
            public bool GatilloAnterior;
            public bool GatilloPulsadoEsteFrame;
            public bool BotonMenuAnterior;
            public bool BotonMenuPulsadoEsteFrame;
        }

        private readonly List<Mano> _manos = new List<Mano>();
        private Material _materialRayo;

        /// <summary>Fuente de entrada activa, solo para el registro: se anuncia cada cambio.</summary>
        private enum FuenteEntrada { Ninguna, MandosXR, RatonEditor }
        private FuenteEntrada _fuenteAnunciada = FuenteEntrada.Ninguna;

#if UNITY_EDITOR
        // --- Respaldo de ratón del Editor (ver cabecera de la clase) -----------------------
        private bool _respaldoActivo;
        private bool _clicAnterior;
        private bool _clicEsteFrame;
        private bool _teclaMenuEsteFrame;
        private Camera _camaraRespaldo;
#endif

        private static readonly Color ColorNeutro = new Color(0.55f, 0.80f, 1f, 0.55f);
        private static readonly Color ColorSobreObjeto = new Color(1f, 0.85f, 0.30f, 0.95f);

        /// <summary>Mano que se considera activa: la última cuyo gatillo se ha pulsado.</summary>
        private Mano _manoActiva;

        public void Initialize(Transform padre)
        {
            _materialRayo = CrearMaterialDeRayo();

            _manos.Add(CrearMano(XRNode.RightHand, padre, "MandoDerecho"));
            _manos.Add(CrearMano(XRNode.LeftHand, padre, "MandoIzquierdo"));
            _manoActiva = _manos[0];

            Debug.LogWarning("[DigitalTwin][AR] Rig de mandos creado. Se leerá la pose de ambas manos y se " +
                      "usará como activa la última que dispare el gatillo.");
        }

        private Mano CrearMano(XRNode nodo, Transform padre, string nombre)
        {
            var go = new GameObject(nombre);
            go.transform.SetParent(padre, false);

            var linea = go.AddComponent<LineRenderer>();
            linea.material = _materialRayo;
            linea.useWorldSpace = false;      // en local, así el rayo acompaña al anclaje sin recalcular
            linea.positionCount = 2;
            linea.startWidth = GrosorRayo;
            linea.endWidth = GrosorRayo * 0.5f;   // ligeramente cónico: ayuda a percibir la profundidad
            linea.numCapVertices = 2;
            linea.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            linea.receiveShadows = false;
            linea.SetPosition(0, Vector3.zero);
            linea.SetPosition(1, Vector3.forward * AlcanceRayo);
            linea.startColor = linea.endColor = ColorNeutro;

            return new Mano { Nodo = nodo, Anclaje = go.transform, Linea = linea };
        }

        /// <summary>
        /// El proyecto usa el pipeline universal, cuyo sombreador sin iluminación es el adecuado
        /// para un rayo: no debe recibir luces ni sombras. Se contemplan alternativas porque el
        /// nombre del sombreador depende del pipeline configurado, y un rayo invisible por no
        /// encontrar el material sería un fallo desconcertante.
        /// </summary>
        private static Material CrearMaterialDeRayo()
        {
            // Delegado en el ayudante comun: la busqueda de sombreador por nombre falla en
            // compilacion si no estan incluidos, y aqui devolvia un material de reemplazo creado a
            // su vez con un Shader.Find que tambien podia ser null. Ahora, si no hay sombreador,
            // se devuelve null y el rayo no se dibuja, pero los mandos siguen funcionando.
            return DigitalTwin.Core.RuntimeMaterials.CrearSinIluminacion(ColorNeutro);
        }

        private void Update()
        {
            foreach (var mano in _manos)
            {
                var dispositivo = InputDevices.GetDeviceAtXRNode(mano.Nodo);
                mano.Valida = dispositivo.isValid;
                mano.GatilloPulsadoEsteFrame = false;
                mano.BotonMenuPulsadoEsteFrame = false;

                // Con el mando apagado o fuera de alcance se oculta su rayo en lugar de dejarlo
                // congelado en la última pose conocida, que resulta confuso.
                mano.Linea.enabled = mano.Valida;
                if (!mano.Valida) { mano.GatilloAnterior = false; mano.BotonMenuAnterior = false; continue; }

                if (dispositivo.TryGetFeatureValue(CommonUsages.devicePosition, out Vector3 pos))
                    mano.Anclaje.localPosition = pos;
                if (dispositivo.TryGetFeatureValue(CommonUsages.deviceRotation, out Quaternion rot))
                    mano.Anclaje.localRotation = rot;

                float gatillo = 0f;
                dispositivo.TryGetFeatureValue(CommonUsages.trigger, out gatillo);
                bool pulsado = gatillo >= UmbralGatillo;

                // Solo el flanco de subida cuenta como pulsación, o mantener el gatillo generaría
                // una selección por fotograma.
                mano.GatilloPulsadoEsteFrame = pulsado && !mano.GatilloAnterior;
                mano.GatilloAnterior = pulsado;

                // Botón primario (A/X) como botón de menú, con el mismo flanco de subida. Se usa
                // el primario y no CommonUsages.menuButton, que en varios sistemas se lo reserva
                // el propio sistema operativo del visor y no llega a la aplicación.
                bool boton = false;
                dispositivo.TryGetFeatureValue(CommonUsages.primaryButton, out boton);
                mano.BotonMenuPulsadoEsteFrame = boton && !mano.BotonMenuAnterior;
                mano.BotonMenuAnterior = boton;

                if (mano.GatilloPulsadoEsteFrame) _manoActiva = mano;
            }

#if UNITY_EDITOR
            ActualizarRespaldoEditor();
#endif
            AnunciarFuenteSiCambia();
        }

        /// <summary>
        /// Anuncia por el registro con qué fuente está funcionando el rig, solo cuando cambia:
        /// mandos XR o respaldo de ratón del Editor. Sin esta traza, un resultado de prueba no
        /// dice con qué entrada se obtuvo.
        /// </summary>
        private void AnunciarFuenteSiCambia()
        {
            FuenteEntrada actual = FuenteEntrada.Ninguna;
            int validas = 0;
            foreach (var mano in _manos) if (mano.Valida) validas++;
            if (validas > 0) actual = FuenteEntrada.MandosXR;
#if UNITY_EDITOR
            else if (_respaldoActivo) actual = FuenteEntrada.RatonEditor;
#endif
            if (actual == _fuenteAnunciada) return;
            _fuenteAnunciada = actual;

            switch (actual)
            {
                case FuenteEntrada.MandosXR:
                    Debug.LogWarning($"[DigitalTwin][AR] Entrada del rig: MANDOS XR ({validas} " +
                                     "valido/s). El respaldo de raton, si existia, queda desactivado.");
                    break;
                case FuenteEntrada.RatonEditor:
                    Debug.LogWarning("[DigitalTwin][AR] Entrada del rig: RESPALDO DE RATON del " +
                                     "Editor (ningun dispositivo XR valido). Cursor = rayo, " +
                                     "boton izquierdo = gatillo, rueda = joystick, tecla M = menu. " +
                                     "Este camino no existe en la compilacion.");
                    break;
                default:
                    Debug.LogWarning("[DigitalTwin][AR] Entrada del rig: NINGUNA (sin mandos XR " +
                                     "validos y sin respaldo). No se puede senalar nada.");
                    break;
            }
        }

#if UNITY_EDITOR
        /// <summary>
        /// Estado del respaldo de ratón. Solo se activa sin ningún mando XR válido, y la
        /// detección de flanco es la misma que la del gatillo real: mantener el botón no
        /// repite pulsaciones.
        /// </summary>
        private void ActualizarRespaldoEditor()
        {
            bool hayManoValida = false;
            foreach (var mano in _manos) if (mano.Valida) { hayManoValida = true; break; }

            _respaldoActivo = !hayManoValida;
            if (!_respaldoActivo)
            {
                _clicAnterior = false;
                _clicEsteFrame = false;
                _teclaMenuEsteFrame = false;
                return;
            }

            var raton = UnityEngine.InputSystem.Mouse.current;
            bool clic = raton != null && raton.leftButton.isPressed;
            _clicEsteFrame = clic && !_clicAnterior;
            _clicAnterior = clic;

            var teclado = UnityEngine.InputSystem.Keyboard.current;
            _teclaMenuEsteFrame = teclado != null && teclado.mKey.wasPressedThisFrame;
        }

        /// <summary>Rayo desde la cámara a través del cursor. Falso sin cámara o sin ratón.</summary>
        private bool TryGetRayoRespaldo(out Ray rayo)
        {
            rayo = default;
            if (!_respaldoActivo) return false;

            if (_camaraRespaldo == null) _camaraRespaldo = Camera.main;
            var raton = UnityEngine.InputSystem.Mouse.current;
            if (_camaraRespaldo == null || raton == null) return false;

            rayo = _camaraRespaldo.ScreenPointToRay(raton.position.ReadValue());
            return true;
        }
#endif

        /// <summary>Rayo de la mano activa, en coordenadas de mundo. Con el respaldo del Editor
        /// activo, el rayo sale de la cámara a través del cursor. Falso si no hay fuente.</summary>
        public bool TryGetRayo(out Ray rayo)
        {
            rayo = default;
            var mano = _manoActiva != null && _manoActiva.Valida ? _manoActiva : PrimeraManoValida();
            if (mano != null)
            {
                rayo = new Ray(mano.Anclaje.position, mano.Anclaje.forward);
                return true;
            }
#if UNITY_EDITOR
            return TryGetRayoRespaldo(out rayo);
#else
            return false;
#endif
        }

        /// <summary>Cierto en el fotograma en que se aprieta el gatillo de cualquiera de las
        /// manos (o, en el respaldo del Editor, el botón izquierdo del ratón).</summary>
        public bool GatilloPulsadoEsteFrame()
        {
            foreach (var mano in _manos)
                if (mano.GatilloPulsadoEsteFrame) return true;
#if UNITY_EDITOR
            if (_respaldoActivo) return _clicEsteFrame;
#endif
            return false;
        }

        /// <summary>Cierto en el fotograma en que se pulsa el botón primario (A/X) de cualquiera
        /// de las manos (o, en el respaldo del Editor, la tecla M). Lo consume el menú de zonas.</summary>
        public bool BotonMenuPulsadoEsteFrame()
        {
            foreach (var mano in _manos)
                if (mano.BotonMenuPulsadoEsteFrame) return true;
#if UNITY_EDITOR
            if (_respaldoActivo) return _teclaMenuEsteFrame;
#endif
            return false;
        }

        /// <summary>
        /// Componente vertical del joystick de la mano activa, en [-1, 1] con una zona muerta
        /// pequeña. Se usa para desplazar el contenido del panel de metadatos mientras el rayo
        /// lo señala; fuera de esa situación nadie lo consulta, así que no interfiere con nada.
        /// En el respaldo del Editor, la rueda del ratón hace este papel.
        /// </summary>
        public float JoystickVertical()
        {
            var mano = _manoActiva != null && _manoActiva.Valida ? _manoActiva : PrimeraManoValida();
            if (mano == null)
            {
#if UNITY_EDITOR
                if (_respaldoActivo)
                {
                    var raton = UnityEngine.InputSystem.Mouse.current;
                    if (raton == null) return 0f;
                    // La rueda entrega impulsos discretos (±1 por muesca en la mayoría de
                    // plataformas); se satura a [-1, 1] para igualarla al eje del joystick.
                    return Mathf.Clamp(raton.scroll.ReadValue().y, -1f, 1f);
                }
#endif
                return 0f;
            }

            var dispositivo = InputDevices.GetDeviceAtXRNode(mano.Nodo);
            if (!dispositivo.TryGetFeatureValue(CommonUsages.primary2DAxis, out Vector2 eje))
                return 0f;

            const float ZonaMuerta = 0.25f;
            if (Mathf.Abs(eje.y) < ZonaMuerta) return 0f;
            return eje.y;
        }

        /// <summary>
        /// Acorta el rayo hasta el punto alcanzado y lo tiñe, para que se vea dónde termina. Sin
        /// esto, el rayo atraviesa la geometría y no hay forma de saber qué se está señalando.
        /// </summary>
        public void MostrarImpacto(float distancia, bool sobreObjetoSeleccionable)
        {
            foreach (var mano in _manos)
            {
                if (!mano.Valida) continue;
                float largo = distancia > 0f ? Mathf.Min(distancia, AlcanceRayo) : AlcanceRayo;
                mano.Linea.SetPosition(1, Vector3.forward * largo);
                mano.Linea.startColor = mano.Linea.endColor =
                    sobreObjetoSeleccionable ? ColorSobreObjeto : ColorNeutro;
            }
        }

        private Mano PrimeraManoValida()
        {
            foreach (var mano in _manos)
                if (mano.Valida) return mano;
            return null;
        }
    }
}
