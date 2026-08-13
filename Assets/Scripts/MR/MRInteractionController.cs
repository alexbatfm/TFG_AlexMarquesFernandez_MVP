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
    /// La regla, por orden de prioridad:
    ///   1. La INTERFAZ: si el rayo corta el panel de metadatos, el gatillo se consume ahí.
    ///   2. Los INDICADORES DE DESTINO (solo navegación por nodos): un cartel señala cada vecino
    ///      alcanzable desde el nodo actual; dispararle desplaza hasta él, con el tránsito por
    ///      puertas resuelto por <see cref="MRNodeNavigator"/>.
    ///   3. El MUNDO: el primer elemento constructivo alcanzado muestra (o cierra) su ficha de
    ///      metadatos. Si en la línea de tiro hay un punto de navegación ALCANZABLE, el
    ///      desplazamiento gana sobre la consulta: consultar un muro es algo que se hace
    ///      apuntando a un muro, no a través de él.
    ///
    /// Los marcadores de navegación (las esferas) nunca cuentan como elemento consultable: no
    /// son elementos del edificio, y es la misma regla que aplica ElementSelector en escritorio.
    /// Están además ocultos en ambas versiones, así que un marcador NO alcanzable simplemente
    /// deja pasar el rayo hacia lo que haya detrás.
    ///
    /// La ALCANZABILIDAD la impone el navegador consumiendo la misma definición que el
    /// escritorio (<see cref="Navigation.NavReachability"/>): apuntar a una esfera de otra sala
    /// ya no teletransporta a través de los cerramientos, que era la divergencia que invalidaba
    /// el grafo de coste en la versión inmersiva.
    ///
    /// En modo anclado no hay navegador: el desplazamiento es andando, y el rayo solo consulta
    /// fichas de lo que sí está representado (oclusores y sensores; ver MROcclusionService).
    /// </summary>
    public class MRInteractionController : MonoBehaviour
    {
        private MRControllerRig _rig;
        private MetadataPanelController _panel;

        /// <summary>
        /// Colocador del panel en el espacio. Se necesita aquí para poder preguntarle si el rayo
        /// corta la ficha antes de resolver la selección contra el edificio.
        /// </summary>
        private WorldPanelPlacer _colocadorPanel;

        /// <summary>Navegación por nodos; null en modo anclado.</summary>
        private MRNodeNavigator _navegador;

        private Camera _camara;

        public void Initialize(MRControllerRig rig, MetadataPanelController panel,
                               WorldPanelPlacer colocadorPanel, MRNodeNavigator navegador)
        {
            _rig = rig;
            _panel = panel;
            _colocadorPanel = colocadorPanel;
            _navegador = navegador;
            _camara = Camera.main;
        }

        private void Update()
        {
            if (_rig == null || _camara == null) return;

            // Bloqueo durante el trayecto: un disparo a mitad de camino encadenaria dos
            // desplazamientos y dejaria al usuario en un sitio que no ha elegido.
            if (_navegador != null && _navegador.EnTransito) return;

            if (!_rig.TryGetRayo(out Ray rayo)) { _rig.MostrarImpacto(0f, false); return; }

            // 1) La interfaz se consulta ANTES que el mundo. El panel flota ante el usuario,
            // entre él y el edificio, así que en cuanto está abierto se interpone en casi
            // cualquier línea de tiro. Sin esta comprobación el rayo lo atraviesa —un lienzo no
            // participa en la física— y pulsar sobre la ficha seleccionaba lo que hubiera detrás.
            if (_colocadorPanel != null && _colocadorPanel.RayoImpactaPanel(rayo, out float distPanel))
            {
                _rig.MostrarImpacto(distPanel, true);
                // El gatillo se consume aquí. Todavía no hay controles dentro del panel a los que
                // dirigirlo, pero no hacer nada es el comportamiento correcto: pulsar sobre una
                // ficha nunca debe actuar sobre lo que queda detrás de ella.
                return;
            }

            // 2) Carteles de destino: la vía visible de desplazarse. Cada cartel es un vecino
            // alcanzable, así que no hace falta revalidar aquí (el navegador lo hace igualmente).
            if (_navegador != null && _navegador.TryImpactoIndicador(rayo, out int nodoCartel,
                                                                     out float distCartel))
            {
                _rig.MostrarImpacto(distCartel, true);
                if (_rig.GatilloPulsadoEsteFrame()) _navegador.SolicitarViaje(nodoCartel);
                return;
            }

            // 3) El mundo. Se recogen TODOS los impactos del rayo, no solo el primero: apuntando
            // a un destino que está detrás de una cristalera o de una barandilla, el rayo se
            // detenía en el obstáculo pese a que el destino se veía perfectamente. La
            // realimentación visual, en cambio, muestra el PRIMER impacto: el rayo debe terminar
            // donde el usuario ve que termina, o la sensación de puntería se rompe.
            var impactos = Physics.RaycastAll(rayo, ElementSelector.MaxRayDistance,
                                              ColliderBootstrapper.SelectionMask(),
                                              QueryTriggerInteraction.Ignore);

            IfcMetadata primero = null;      // primer elemento constructivo: el que se señala
            float distanciaPrimero = 0f;
            int nodoDestino = -1;            // primer punto de navegación ALCANZABLE en la línea
            IfcMetadata destinoSinGrafo = null; // degradación: sin grafo, cualquier punto sirve

            if (impactos != null && impactos.Length > 0)
            {
                System.Array.Sort(impactos, (a, b) => a.distance.CompareTo(b.distance));

                foreach (var h in impactos)
                {
                    var m = h.collider.GetComponentInParent<IfcMetadata>();
                    if (m == null) continue;

                    if (m.ifcType == SceneModelIndex.NavPointIfcType)
                    {
                        if (_navegador == null) continue; // modo anclado: las esferas no juegan

                        if (_navegador.Disponible)
                        {
                            int idx = _navegador.IndiceDe(m);
                            if (nodoDestino < 0 && idx >= 0 && _navegador.EsVecinoActual(idx))
                                nodoDestino = idx;
                        }
                        else if (destinoSinGrafo == null)
                        {
                            destinoSinGrafo = m;
                        }
                        continue; // nunca es "primero": un marcador no es un elemento del edificio
                    }

                    if (primero == null) { primero = m; distanciaPrimero = h.distance; }
                }
            }

            bool haySenal = primero != null || nodoDestino >= 0 || destinoSinGrafo != null;
            _rig.MostrarImpacto(distanciaPrimero, haySenal);

            if (!_rig.GatilloPulsadoEsteFrame()) return;

            // El desplazamiento gana sobre la consulta de metadatos, pero solo hacia destinos
            // que el grafo declare alcanzables: la esfera de una sala remota, aunque el rayo la
            // alcance a través de un tabique, ya no es un destino.
            if (nodoDestino >= 0)
            {
                _navegador.SolicitarViaje(nodoDestino);
                return;
            }
            if (destinoSinGrafo != null)
            {
                _navegador.ViajarDirectoSinGrafo(destinoSinGrafo);
                return;
            }

            if (primero != null)
            {
                // Segundo disparo sobre el elemento ya consultado: se cierra la ficha.
                //
                // Alterna en lugar de reabrir porque el panel viaja con el usuario: una ficha
                // olvidada ya no se queda atrás sola, hace falta una forma explícita de
                // retirarla, y el propio elemento es el gesto que el usuario ya conoce.
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
    }
}
