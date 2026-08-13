using System.Collections.Generic;
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
    ///   1. La INTERFAZ: si el rayo corta el panel de metadatos, el gatillo se dirige a los
    ///      controles del panel (cerrar, desplegar un Pset) a través de
    ///      <see cref="DigitalTwin.UI.ClickRouter.InvocarPulsacionEnPuntoMundo"/>: los mismos
    ///      registros y callbacks que usa el escritorio, con el rayo como puntero. El joystick
    ///      de la mano activa desplaza la lista mientras el rayo señala la ficha.
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
    ///
    /// RENDIMIENTO (medido, no supuesto). Tras la prueba del 2026-08-13 («la selección va
    /// lenta») este componente se instrumenta a sí mismo: cronometra su propio coste por
    /// fotograma y lo vuelca al registro cada diez segundos junto con el tiempo de fotograma
    /// total, para que la próxima sesión de visor entregue números en vez de sensaciones. Las
    /// dos mejoras aplicadas de antemano no cambian el comportamiento: el trazado usa un búfer
    /// fijo (RaycastNonAlloc, cero basura por fotograma) y los metadatos se resuelven por
    /// diccionario collider→elemento construido una sola vez, en lugar de subir por la
    /// jerarquía en cada impacto de cada fotograma.
    /// </summary>
    public class MRInteractionController : MonoBehaviour
    {
        /// <summary>Velocidad de desplazamiento del contenido del panel con el joystick, en
        /// unidades de layout por segundo a palanca completa.</summary>
        private const float VelocidadScrollPanel = 700f;

        /// <summary>Cada cuántos segundos se vuelca la medición de rendimiento.</summary>
        private const float SegundosEntreInformesPerf = 10f;

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

        /// <summary>Resolución collider→metadatos calculada una vez. Los colliders creados
        /// después (carteles, panel) no están y no deben estarlo: se consultan antes que el
        /// mundo por sus propias vías.</summary>
        private readonly Dictionary<Collider, IfcMetadata> _metaPorCollider =
            new Dictionary<Collider, IfcMetadata>();

        // Búfer fijo del trazado: cero asignaciones por fotograma. 64 impactos dan de sobra
        // para un rayo interior (el peor caso razonable son unas decenas de planos); si alguna
        // vez se llena, se avisa una única vez y se sigue con los 64 más cercanos no ordenados.
        private readonly RaycastHit[] _impactos = new RaycastHit[64];
        private bool _avisadoBufferLleno;

        // Medición: media móvil exponencial del coste propio y del fotograma completo.
        private float _mediaMsSeleccion;
        private float _mediaMsFotograma;
        private float _mediaImpactos;
        private float _proximoInformePerf;
        private readonly System.Diagnostics.Stopwatch _crono = new System.Diagnostics.Stopwatch();

        public void Initialize(MRControllerRig rig, MetadataPanelController panel,
                               WorldPanelPlacer colocadorPanel, MRNodeNavigator navegador,
                               SceneModelIndex index)
        {
            _rig = rig;
            _panel = panel;
            _colocadorPanel = colocadorPanel;
            _navegador = navegador;
            _camara = Camera.main;

            ConstruirCacheDeColliders();
            _proximoInformePerf = Time.unscaledTime + SegundosEntreInformesPerf;
        }

        /// <summary>
        /// Una pasada por todos los colliders de la escena resolviendo su elemento propietario
        /// (el IfcMetadata más cercano en la ascendencia), exactamente la misma semántica que
        /// tenía GetComponentInParent por impacto — pero pagada una vez, no 90 veces por segundo.
        /// Se ejecuta después de ColliderBootstrapper, así que los MeshCollider ya existen.
        /// </summary>
        private void ConstruirCacheDeColliders()
        {
            int conElemento = 0;
            foreach (var col in Object.FindObjectsByType<Collider>(FindObjectsSortMode.None))
            {
                var meta = col.GetComponentInParent<IfcMetadata>();
                if (meta == null) continue;
                _metaPorCollider[col] = meta;
                conElemento++;
            }
            Debug.LogWarning($"[DigitalTwin][AR] Cache de seleccion: {conElemento} colliders " +
                             "resueltos a su elemento una sola vez.");
        }

        private void Update()
        {
            _crono.Restart();

            ProcesarRayo();

            _crono.Stop();
            AcumularYVolcarPerf((float)_crono.Elapsed.TotalMilliseconds);
        }

        private void ProcesarRayo()
        {
            if (_rig == null || _camara == null) return;

            // Bloqueo durante el trayecto: un disparo a mitad de camino encadenaria dos
            // desplazamientos y dejaria al usuario en un sitio que no ha elegido.
            if (_navegador != null && _navegador.EnTransito) return;

            if (!_rig.TryGetRayo(out Ray rayo)) { _rig.MostrarImpacto(0f, false); return; }

            // 1) La interfaz se consulta ANTES que el mundo. El panel flota ante el usuario,
            // entre él y el edificio, así que en cuanto está abierto se interpone en casi
            // cualquier línea de tiro.
            if (_colocadorPanel != null && _colocadorPanel.RayoImpactaPanel(rayo, out float distPanel))
            {
                _rig.MostrarImpacto(distPanel, true);

                // Desplazamiento de la lista con el joystick mientras se señala la ficha.
                float palanca = _rig.JoystickVertical();
                if (palanca != 0f)
                    _panel.DesplazarContenido(-palanca * VelocidadScrollPanel * Time.deltaTime);

                if (_rig.GatilloPulsadoEsteFrame())
                {
                    // El punto de impacto, llevado al router de pulsaciones: los controles del
                    // panel (cerrar, desplegables) ya estaban registrados alli; el visor los
                    // acciona con los MISMOS callbacks que el raton en escritorio.
                    Vector3 punto = rayo.origin + rayo.direction * distPanel;
                    bool accionado = DigitalTwin.UI.ClickRouter.Instance.InvocarPulsacionEnPuntoMundo(punto);
                    Debug.LogWarning(accionado
                        ? "[DigitalTwin][AR] Pulsacion en el panel: control accionado."
                        : "[DigitalTwin][AR] Pulsacion en el panel: sin control en ese punto " +
                          "(fondo de la ficha); no se actua sobre lo que queda detras.");
                }
                return;
            }

            // 2) Carteles de destino: la vía visible de desplazarse.
            if (_navegador != null && _navegador.TryImpactoIndicador(rayo, out int nodoCartel,
                                                                     out float distCartel))
            {
                _rig.MostrarImpacto(distCartel, true);
                if (_rig.GatilloPulsadoEsteFrame()) _navegador.SolicitarViaje(nodoCartel);
                return;
            }

            // 3) El mundo. TODOS los impactos, no solo el primero: apuntando a un destino detrás
            // de una cristalera, el rayo se detenía en el obstáculo pese a que el destino se veía
            // perfectamente. La realimentación visual muestra el PRIMER impacto: el rayo debe
            // terminar donde el usuario ve que termina.
            int numImpactos = Physics.RaycastNonAlloc(rayo, _impactos, ElementSelector.MaxRayDistance,
                                                      ColliderBootstrapper.SelectionMask(),
                                                      QueryTriggerInteraction.Ignore);
            if (numImpactos >= _impactos.Length && !_avisadoBufferLleno)
            {
                _avisadoBufferLleno = true;
                Debug.LogWarning($"[DigitalTwin][AR] El rayo ha llenado el bufer de {_impactos.Length} " +
                                 "impactos; se procesan solo esos. Si esto aparece a menudo, subir " +
                                 "el tamano del bufer.");
            }
            _mediaImpactos = Mathf.Lerp(_mediaImpactos <= 0f ? numImpactos : _mediaImpactos,
                                        numImpactos, 0.05f);

            System.Array.Sort(_impactos, 0, numImpactos, ComparadorDistancia.Instancia);

            IfcMetadata primero = null;      // primer elemento constructivo: el que se señala
            float distanciaPrimero = 0f;
            int nodoDestino = -1;            // primer punto de navegación ALCANZABLE en la línea
            IfcMetadata destinoSinGrafo = null; // degradación: sin grafo, cualquier punto sirve

            for (int i = 0; i < numImpactos; i++)
            {
                if (!_metaPorCollider.TryGetValue(_impactos[i].collider, out var m) || m == null)
                    continue;

                if (m.ifcType == SceneModelIndex.NavPointIfcType)
                {
                    if (_navegador == null) continue; // modo anclado: las esferas no juegan

                    if (_navegador.Disponible)
                    {
                        int idx = _navegador.IndiceDe(m);
                        if (nodoDestino < 0 && idx >= 0 && _navegador.EsDestinoOfrecido(idx))
                            nodoDestino = idx;
                    }
                    else if (destinoSinGrafo == null)
                    {
                        destinoSinGrafo = m;
                    }
                    continue; // nunca es "primero": un marcador no es un elemento del edificio
                }

                if (primero == null) { primero = m; distanciaPrimero = _impactos[i].distance; }
            }

            bool haySenal = primero != null || nodoDestino >= 0 || destinoSinGrafo != null;
            _rig.MostrarImpacto(distanciaPrimero, haySenal);

            if (!_rig.GatilloPulsadoEsteFrame()) return;

            // El desplazamiento gana sobre la consulta de metadatos, pero solo hacia destinos
            // ofrecidos: la esfera de una sala remota, aunque el rayo la alcance a través de un
            // tabique, no es un destino.
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
                // Segundo disparo sobre el elemento ya consultado: se cierra la ficha. Alterna
                // en lugar de reabrir porque el panel viaja con el usuario y hace falta una
                // forma explícita de retirarlo; el propio elemento es el gesto que el usuario ya
                // conoce. La comparación es por referencia a propósito: dos instancias del mismo
                // tipo son elementos distintos, y consultar una tras otra cambia la ficha.
                if (_panel.Current == primero) _panel.Hide();
                else _panel.Show(primero);
                return;
            }

            _panel.Hide();
        }

        /// <summary>Comparador sin capturas para ordenar los impactos por distancia sin generar
        /// basura por fotograma.</summary>
        private sealed class ComparadorDistancia : IComparer<RaycastHit>
        {
            public static readonly ComparadorDistancia Instancia = new ComparadorDistancia();
            public int Compare(RaycastHit a, RaycastHit b) => a.distance.CompareTo(b.distance);
        }

        /// <summary>
        /// Acumula medias móviles y las vuelca cada diez segundos. Media exponencial y no
        /// instantánea: un pico aislado no debe leerse como estado.
        /// </summary>
        private void AcumularYVolcarPerf(float msSeleccion)
        {
            const float Alfa = 0.05f;
            float msFotograma = Time.unscaledDeltaTime * 1000f;

            _mediaMsSeleccion = _mediaMsSeleccion <= 0f
                ? msSeleccion : Mathf.Lerp(_mediaMsSeleccion, msSeleccion, Alfa);
            _mediaMsFotograma = _mediaMsFotograma <= 0f
                ? msFotograma : Mathf.Lerp(_mediaMsFotograma, msFotograma, Alfa);

            if (Time.unscaledTime < _proximoInformePerf) return;
            _proximoInformePerf = Time.unscaledTime + SegundosEntreInformesPerf;

            float fps = _mediaMsFotograma > 0.01f ? 1000f / _mediaMsFotograma : 0f;
            Debug.LogWarning($"[DigitalTwin][AR][Perf] fotograma medio {_mediaMsFotograma:0.0} ms " +
                             $"({fps:0} fps); seleccion media {_mediaMsSeleccion:0.00} ms; " +
                             $"impactos medios del rayo {_mediaImpactos:0.0}.");
        }
    }
}
