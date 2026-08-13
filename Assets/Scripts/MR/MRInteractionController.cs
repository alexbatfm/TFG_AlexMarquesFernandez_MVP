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

        /// <summary>
        /// Colocador del panel en el espacio. Se necesita aquí para poder preguntarle si el rayo
        /// corta la ficha antes de resolver la selección contra el edificio.
        /// </summary>
        private DigitalTwin.Metadata.WorldPanelPlacer _colocadorPanel;
        private Transform _origenXR;
        private Camera _camara;

        /// <summary>
        /// Esfera en la que se encuentra el usuario, oculta mientras esté sobre ella. Estando
        /// dentro de un punto de navegación, su propia esfera queda a la altura de los ojos y tapa
        /// la vista sin aportar nada: no es un destino al que se pueda ir, porque ya se está allí.
        /// Se guarda para poder devolverla a la vista al marcharse.
        /// </summary>
        private Renderer _esferaActualOculta;

        /// <summary>
        /// Cierto mientras dura una transicion. Bloquea la interaccion durante el trayecto: un
        /// disparo a mitad de camino encadenaria dos desplazamientos y dejaria al usuario en un
        /// sitio que no ha elegido.
        /// </summary>
        private bool _enTransito;

        public void Initialize(MRControllerRig rig, MetadataPanelController panel, Transform origenXR,
                               DigitalTwin.Metadata.WorldPanelPlacer colocadorPanel = null)
        {
            _rig = rig;
            _panel = panel;
            _origenXR = origenXR;
            _colocadorPanel = colocadorPanel;
            _camara = Camera.main;
        }

        private void Update()
        {
            if (_rig == null || _camara == null || _enTransito) return;
            if (!_rig.TryGetRayo(out Ray rayo)) { _rig.MostrarImpacto(0f, false); return; }

            // Se recogen TODOS los impactos del rayo, no solo el primero.
            //
            // El motivo es de uso, y salió al probarlo en el visor: apuntando a un punto de
            // navegación que está detrás de una cristalera o de una barandilla, el rayo se detenía
            // en el obstáculo y no había forma de desplazarse allí, pese a que el destino se veía
            // perfectamente. Recorrer todos los impactos permite dar prioridad al destino aunque
            // haya algo delante.
            //
            // La realimentación visual, en cambio, sigue mostrando el PRIMER impacto: el rayo debe
            // terminar donde el usuario ve que termina, o la sensación de puntería se rompe.
            // La interfaz se consulta ANTES que el mundo. El panel de metadatos flota ante el
            // usuario, entre él y el edificio, así que en cuanto está abierto se interpone en
            // casi cualquier línea de tiro. Sin esta comprobación el rayo lo atraviesa —un lienzo
            // no participa en la física— y pulsar sobre la ficha seleccionaba el objeto que
            // hubiera detrás: la interfaz parecía muerta y además cambiaba la selección sola.
            if (_colocadorPanel != null && _colocadorPanel.RayoImpactaPanel(rayo, out float distPanel))
            {
                _rig.MostrarImpacto(distPanel, true);
                // El gatillo se consume aquí. Todavía no hay controles dentro del panel a los que
                // dirigirlo, pero no hacer nada es el comportamiento correcto: pulsar sobre una
                // ficha nunca debe actuar sobre lo que queda detrás de ella.
                return;
            }

            var impactos = Physics.RaycastAll(rayo, ElementSelector.MaxRayDistance,
                                              ColliderBootstrapper.SelectionMask(),
                                              QueryTriggerInteraction.Ignore);

            IfcMetadata primero = null;      // lo que se está señalando visualmente
            IfcMetadata puntoNavegacion = null;   // el destino más cercano, aunque esté detrás
            float distanciaPrimero = 0f;

            if (impactos != null && impactos.Length > 0)
            {
                System.Array.Sort(impactos, (a, b) => a.distance.CompareTo(b.distance));

                foreach (var h in impactos)
                {
                    var m = h.collider.GetComponentInParent<IfcMetadata>();
                    if (m == null) continue;

                    if (primero == null) { primero = m; distanciaPrimero = h.distance; }

                    if (puntoNavegacion == null && m.ifcType == SceneModelIndex.NavPointIfcType)
                        puntoNavegacion = m;
                }
            }

            _rig.MostrarImpacto(distanciaPrimero, primero != null || puntoNavegacion != null);

            if (!_rig.GatilloPulsadoEsteFrame()) return;

            // El desplazamiento gana sobre la consulta de metadatos: si en la línea de tiro hay un
            // punto de navegación, la intención casi siempre es ir allí. Consultar la ficha de un
            // muro es algo que se hace apuntando a un muro, no a través de él.
            if (puntoNavegacion != null)
            {
                IniciarDesplazamiento(puntoNavegacion);
                return;
            }

            if (primero != null)
            {
                // Segundo disparo sobre el elemento ya consultado: se cierra la ficha.
                //
                // Alterna en lugar de reabrir porque el panel viaja con el usuario. Mientras
                // estaba anclado junto al objeto, una ficha olvidada se quedaba atrás y dejaba de
                // molestar sola; ahora acompaña al operario por todo el edificio, así que hace
                // falta una forma explícita de retirarla. Se elige el propio elemento como
                // interruptor, y no un botón aparte, porque es el gesto que el usuario ya conoce
                // —acaba de hacerlo para abrirla— y no exige descubrir nada nuevo.
                //
                // La comparación es por referencia y no por GlobalId a propósito: dos instancias
                // distintas del mismo tipo son elementos distintos del edificio, y consultar una
                // después de otra debe cambiar la ficha, no cerrarla.
                if (_panel.Current == primero) _panel.Hide();
                else _panel.Show(primero);
                return;
            }

            _panel.Hide();
        }

        /// <summary>
        /// Inicia el desplazamiento hacia un punto de navegación, con transición continua.
        ///
        /// Por qué continua y no instantánea. En la versión de escritorio el desplazamiento
        /// interpola la posición precisamente para conservar la orientación espacial del operario
        /// dentro del edificio: ver moverse el entorno indica hacia dónde se ha ido uno. En un
        /// visor ese argumento pesa aún más, porque el usuario no tiene ninguna otra referencia
        /// para reconstruir su posición. Un salto instantáneo obliga a reorientarse desde cero en
        /// cada movimiento.
        ///
        /// Se acota la duración para trayectos cortos: recorrer dos metros con la misma duración
        /// que treinta resulta pesado, y a distancias muy cortas un salto instantáneo no
        /// desorienta.
        /// </summary>
        private void IniciarDesplazamiento(IfcMetadata destino)
        {
            if (_origenXR == null) return;

            Vector3 pos = destino.transform.position;
            float alturaDestino = pos.y > 0.01f ? pos.y : AlturaVistaPorDefecto;
            var objetivo = new Vector3(pos.x, alturaDestino, pos.z);

            OcultarEsferaDeDestino(destino);
            StartCoroutine(DesplazarSuave(objetivo));
        }

        private System.Collections.IEnumerator DesplazarSuave(Vector3 objetivo)
        {
            _enTransito = true;

            Vector3 desplazamientoTotal = objetivo - _camara.transform.position;
            float distancia = desplazamientoTotal.magnitude;

            // Por debajo de este umbral la transición no aporta orientación y solo hace esperar.
            const float DistanciaMinimaParaAnimar = 1.5f;
            float duracion = distancia < DistanciaMinimaParaAnimar
                ? 0f
                : Mathf.Clamp(distancia * 0.05f, 0.35f, 1.1f);

            Debug.Log($"[DigitalTwin][AR] Desplazamiento a punto de navegacion en {objetivo} " +
                      $"({distancia:0.0} m, {duracion:0.00} s).");

            if (duracion <= 0f)
            {
                _origenXR.position += desplazamientoTotal;
                _enTransito = false;
                yield break;
            }

            Vector3 origenInicial = _origenXR.position;
            // Se recalcula el desplazamiento contra la posicion inicial del origen, no contra la
            // camara fotograma a fotograma: si el usuario mueve la cabeza durante la transicion, no
            // debe alterar el destino.
            Vector3 origenFinal = origenInicial + desplazamientoTotal;

            float t = 0f;
            while (t < 1f)
            {
                t += Time.deltaTime / duracion;
                // Suavizado en la entrada y la salida. Un movimiento a velocidad constante que
                // arranca y se detiene de golpe resulta brusco en un visor, y es una de las causas
                // conocidas de malestar en desplazamientos inmersivos.
                float s = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                _origenXR.position = Vector3.Lerp(origenInicial, origenFinal, s);
                yield return null;
            }

            _origenXR.position = origenFinal;
            _enTransito = false;
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
