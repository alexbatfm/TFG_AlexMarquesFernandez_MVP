using DigitalTwin.Core;
using DigitalTwin.Metadata;
using IFCImporter;
using UnityEngine;

namespace DigitalTwin.MR
{
    /// <summary>
    /// Interacción de la versión de Realidad Aumentada: decide qué significa apretar el gatillo en
    /// función de lo que el rayo del mando esté señalando.
    ///
    /// Por qué un solo componente y no uno por acción. Selección y desplazamiento comparten el
    /// mismo gesto --- apuntar y disparar --- y el mismo trazado de rayo. Repartirlos entre dos
    /// componentes obligaría a que ambos consultaran el gatillo en el mismo fotograma y a
    /// coordinarse para no actuar los dos a la vez. Concentrar aquí la decisión hace que la regla
    /// sea explícita y quede en un único sitio.
    ///
    /// La regla es la siguiente. Si el rayo alcanza un punto de navegación --- las esferas del
    /// modelo ---, el gatillo desplaza al usuario hasta él. Si alcanza cualquier otro elemento
    /// constructivo, muestra sus metadatos. Si no alcanza nada, cierra el panel.
    ///
    /// Nótese una diferencia deliberada con la versión de escritorio: allí las esferas se ocultan y
    /// se sustituyen por indicadores dibujados sobre la pantalla, porque señalar con el ratón un
    /// objeto pequeño a distancia es incómodo. Aquí se dejan visibles y son ellas mismas el
    /// elemento con el que se interactúa. En un entorno inmersivo, un objeto que ocupa un lugar en
    /// el espacio es más fácil de señalar con la mano que un icono superpuesto, y además no tapa la
    /// vista del edificio.
    /// </summary>
    public class MRInteractionController : MonoBehaviour
    {
        /// <summary>
        /// Altura a la que se sitúa la vista al llegar a un punto de navegación. Los puntos del
        /// modelo están colocados a la altura de los ojos, así que se toma su propia altura; esta
        /// constante solo interviene si el punto careciera de una posición utilizable.
        /// </summary>
        private const float AlturaVistaPorDefecto = 1.6f;

        private MRControllerRig _rig;
        private MetadataPanelController _panel;
        private Transform _origenXR;
        private Camera _camara;

        /// <summary>
        /// Esfera en la que se encuentra el usuario, oculta mientras esté sobre ella. Estando
        /// dentro de un punto de navegación, su propia esfera queda a la altura de los ojos y tapa
        /// la vista sin aportar nada: no es un destino al que se pueda ir, porque ya se está allí.
        /// Se guarda para poder devolverla a la vista al marcharse.
        /// </summary>
        private Renderer _esferaActualOculta;

        public void Initialize(MRControllerRig rig, MetadataPanelController panel, Transform origenXR)
        {
            _rig = rig;
            _panel = panel;
            _origenXR = origenXR;
            _camara = Camera.main;
        }

        private void Update()
        {
            if (_rig == null || _camara == null) return;
            if (!_rig.TryGetRayo(out Ray rayo)) { _rig.MostrarImpacto(0f, false); return; }

            bool hayImpacto = Physics.Raycast(rayo, out RaycastHit hit,
                                              ElementSelector.MaxRayDistance,
                                              ColliderBootstrapper.SelectionMask(),
                                              QueryTriggerInteraction.Ignore);

            IfcMetadata meta = hayImpacto ? hit.collider.GetComponentInParent<IfcMetadata>() : null;
            bool esPuntoNavegacion = meta != null && meta.ifcType == SceneModelIndex.NavPointIfcType;

            // Realimentación continua: el rayo se acorta hasta el impacto y cambia de color cuando
            // hay algo con lo que se puede interactuar. Sin esto no se sabe qué se está apuntando
            // hasta después de disparar.
            _rig.MostrarImpacto(hayImpacto ? hit.distance : 0f, meta != null);

            if (!_rig.GatilloPulsadoEsteFrame()) return;

            if (esPuntoNavegacion)
            {
                Desplazar(meta.transform.position);
                OcultarEsferaDeDestino(meta);
                return;
            }

            if (meta != null)
            {
                _panel.Show(meta);
                return;
            }

            _panel.Hide();
        }

        /// <summary>
        /// Lleva la vista hasta el destino moviendo el origen de realidad extendida, no la cámara.
        ///
        /// La cámara está gobernada por el seguimiento de pose: cualquier valor que se le escriba lo
        /// sobrescribe el sistema en el fotograma siguiente. El desplazamiento tiene que aplicarse
        /// al origen del que cuelga, y por la diferencia entre donde está la cámara y donde se la
        /// quiere llevar; no basta con colocar el origen en el destino, porque el usuario puede
        /// haberse desplazado caminando respecto a él.
        /// </summary>
        private void Desplazar(Vector3 destino)
        {
            if (_origenXR == null) return;

            float alturaDestino = destino.y > 0.01f ? destino.y : AlturaVistaPorDefecto;
            var objetivo = new Vector3(destino.x, alturaDestino, destino.z);

            _origenXR.position += objetivo - _camara.transform.position;

            Debug.Log($"[DigitalTwin][AR] Desplazamiento a punto de navegacion en {objetivo}.");
        }

        /// <summary>
        /// Oculta la esfera del punto al que se acaba de llegar y devuelve a la vista la anterior.
        /// Se oculta solo el <c>Renderer</c>, no el objeto: su collider debe seguir existiendo para
        /// que el rayo pueda volver a alcanzarla desde otro punto, y sus metadatos sostienen la
        /// relación con la base de datos.
        /// </summary>
        private void OcultarEsferaDeDestino(IfcMetadata destino)
        {
            if (_esferaActualOculta != null) _esferaActualOculta.enabled = true;

            _esferaActualOculta = destino.GetComponentInChildren<Renderer>();
            if (_esferaActualOculta != null) _esferaActualOculta.enabled = false;
        }
    }
}
