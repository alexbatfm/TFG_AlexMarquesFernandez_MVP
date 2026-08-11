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
        }

        private readonly List<Mano> _manos = new List<Mano>();
        private Material _materialRayo;

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
            string[] candidatos = { "Universal Render Pipeline/Unlit", "Unlit/Color", "Sprites/Default" };
            foreach (var nombre in candidatos)
            {
                var shader = Shader.Find(nombre);
                if (shader != null) return new Material(shader);
            }
            Debug.LogWarning("[DigitalTwin][AR] No se ha encontrado ningún sombreador para el rayo " +
                             "de los mandos; se verá con el material de reemplazo.");
            return new Material(Shader.Find("Hidden/InternalErrorShader"));
        }

        private void Update()
        {
            foreach (var mano in _manos)
            {
                var dispositivo = InputDevices.GetDeviceAtXRNode(mano.Nodo);
                mano.Valida = dispositivo.isValid;
                mano.GatilloPulsadoEsteFrame = false;

                // Con el mando apagado o fuera de alcance se oculta su rayo en lugar de dejarlo
                // congelado en la última pose conocida, que resulta confuso.
                mano.Linea.enabled = mano.Valida;
                if (!mano.Valida) { mano.GatilloAnterior = false; continue; }

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

                if (mano.GatilloPulsadoEsteFrame) _manoActiva = mano;
            }
        }

        /// <summary>Rayo de la mano activa, en coordenadas de mundo. Falso si no hay mando válido.</summary>
        public bool TryGetRayo(out Ray rayo)
        {
            rayo = default;
            var mano = _manoActiva != null && _manoActiva.Valida ? _manoActiva : PrimeraManoValida();
            if (mano == null) return false;

            rayo = new Ray(mano.Anclaje.position, mano.Anclaje.forward);
            return true;
        }

        /// <summary>Cierto en el fotograma en que se aprieta el gatillo de cualquiera de las manos.</summary>
        public bool GatilloPulsadoEsteFrame()
        {
            foreach (var mano in _manos)
                if (mano.GatilloPulsadoEsteFrame) return true;
            return false;
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
