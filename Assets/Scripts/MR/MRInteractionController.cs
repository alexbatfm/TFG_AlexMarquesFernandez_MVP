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
    /// lenta») este componente cronometra su propio coste por fotograma. El VOLCADO ya no es
    /// suyo: lo reporta a <see cref="MRPerfMonitor"/>, que existe desde el primer fotograma del
    /// arranque —este controlador nace con el gemelo montado, y medir solo desde ahí dejaba la
    /// fase del selector sin números (prueba del 2026-08-14)—. Las dos mejoras aplicadas de
    /// antemano no cambian el comportamiento: el trazado usa un búfer fijo (RaycastNonAlloc,
    /// cero basura por fotograma) y los metadatos se resuelven por diccionario
    /// collider→elemento construido una sola vez, en lugar de subir por la jerarquía en cada
    /// impacto de cada fotograma.
    ///
    /// ETIQUETA DE SEÑALADO, SOLO EN MODO ANCLADO (15-08, noche). En anclado la geometría es
    /// oclusor invisible: al apuntar, lo único visible era el punto de impacto del rayo, y en
    /// una sala a oscuras el usuario no podía responder «qué estoy señalando» sin disparar y
    /// leer la ficha —justo lo que no puede hacer si duda de si va a acertar—. La respuesta es
    /// una etiqueta flotante junto al punto de impacto con el nombre del elemento señalado,
    /// continua y discreta, autoiluminada (material de interfaz siempre visible, el mismo de
    /// los indicadores) para que no dependa de la iluminación de la sala ni la tape la
    /// escritura de profundidad de los oclusores. Se eligió esta vía y no reforzar el realce de
    /// contorno porque dibujar contornos en cada apuntado equivaldría a redibujar el edificio
    /// pieza a pieza, que es lo que el modo anclado existe para no hacer; el marcador de
    /// impacto ya dice si hay elemento válido (color), y la etiqueta añade el cuál. En
    /// navegación por nodos no se crea: allí el edificio entero es visible e iluminado y el
    /// elemento se identifica solo.
    /// </summary>
    public class MRInteractionController : MonoBehaviour
    {
        /// <summary>Velocidad de desplazamiento del contenido del panel con el joystick, en
        /// unidades de layout por segundo a palanca completa.</summary>
        private const float VelocidadScrollPanel = 700f;

        private MRControllerRig _rig;
        private MetadataPanelController _panel;

        /// <summary>
        /// Colocador del panel en el espacio. Se necesita aquí para poder preguntarle si el rayo
        /// corta la ficha antes de resolver la selección contra el edificio.
        /// </summary>
        private WorldPanelPlacer _colocadorPanel;

        /// <summary>Navegación por nodos; null en modo anclado.</summary>
        private MRNodeNavigator _navegador;

        /// <summary>Menú del modo de navegación (zonas, solar, volver al selector); null en
        /// modo anclado. Mientras está abierto, este controlador cede el rayo: el menú gestiona
        /// su propia interacción (patrón del selector de modo) y una pulsación sobre él no debe
        /// seleccionar lo de detrás.</summary>
        private MRMenuZonas _menuZonas;

        /// <summary>Menú del modo anclado (panel de anclaje, rehacer, volver al selector);
        /// null en navegación. Mismo contrato de cesión del rayo que el de navegación.</summary>
        private MRMenuAnclado _menuAnclado;

        /// <summary>Interfaz de colocación del anclaje; null en navegación por nodos. Mientras
        /// su panel está abierto captura el rayo (toma puntos de suelo y acciona sus botones),
        /// con el mismo contrato de cesión que el menú.</summary>
        private MRColocacionAnclaje _colocacionAnclaje;

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

        // Medición: el coste propio se cronometra aquí y se agrega/vuelca en MRPerfMonitor.
        private int _impactosDelFotograma;
        private readonly System.Diagnostics.Stopwatch _crono = new System.Diagnostics.Stopwatch();

        // --- Etiqueta de señalado (solo modo anclado; ver la nota de la clase) --------------
        /// <summary>Ancho del rótulo a un metro de distancia; la escala crece con la distancia
        /// para mantener el tamaño angular (~11°) constante.</summary>
        private const float AnchoEtiquetaMetros = 0.20f;
        private const float DistanciaMinimaEscala = 0.5f;
        private const float DistanciaMaximaEscala = 6f;
        private const int MaximoCaracteresEtiqueta = 40;
        private RectTransform _etiquetaRaiz;
        private UnityEngine.UI.Text _etiquetaTexto;
        private Vector3 _etiquetaEscalaBase;
        private IfcMetadata _metaEtiquetado;

        public void Initialize(MRControllerRig rig, MetadataPanelController panel,
                               WorldPanelPlacer colocadorPanel, MRNodeNavigator navegador,
                               SceneModelIndex index, MRMenuZonas menuZonas = null,
                               MRColocacionAnclaje colocacionAnclaje = null,
                               bool identificarSenalado = false,
                               MRMenuAnclado menuAnclado = null)
        {
            _rig = rig;
            _panel = panel;
            _colocadorPanel = colocadorPanel;
            _navegador = navegador;
            _menuZonas = menuZonas;
            _menuAnclado = menuAnclado;
            _colocacionAnclaje = colocacionAnclaje;
            _camara = Camera.main;

            if (identificarSenalado) ConstruirEtiquetaSenalado();

            ConstruirCacheDeColliders();
        }

        /// <summary>
        /// Rótulo flotante con el nombre del elemento señalado. Lienzo de mundo diminuto con el
        /// material de interfaz siempre visible: sin él, la escritura de profundidad de los
        /// oclusores invisibles lo recortaría contra el propio muro que está nombrando.
        /// </summary>
        private void ConstruirEtiquetaSenalado()
        {
            var canvas = DigitalTwin.UI.RuntimeUIFactory.CreateWorldCanvas("~EtiquetaSenaladoAR",
                anchoPx: 560f, altoPx: 76f, anchoMetros: AnchoEtiquetaMetros);
            _etiquetaRaiz = (RectTransform)canvas.transform;
            _etiquetaRaiz.SetParent(transform, true);

            var material = MRIndicadoresDestino.MaterialSiempreVisible();

            var fondo = DigitalTwin.UI.RuntimeUIFactory.CreatePanel(_etiquetaRaiz, "Fondo",
                new Color(0.05f, 0.06f, 0.08f, 0.82f));
            DigitalTwin.UI.RuntimeUIFactory.StretchToParent((RectTransform)fondo.transform);
            if (material != null) fondo.material = material;

            _etiquetaTexto = DigitalTwin.UI.RuntimeUIFactory.CreateText(_etiquetaRaiz, "Nombre",
                "", 34, TextAnchor.MiddleCenter, new Color(0.92f, 0.94f, 0.97f, 1f),
                FontStyle.Bold);
            DigitalTwin.UI.RuntimeUIFactory.StretchToParent((RectTransform)_etiquetaTexto.transform);
            if (material != null) _etiquetaTexto.material = material;

            _etiquetaEscalaBase = _etiquetaRaiz.localScale;
            _etiquetaRaiz.gameObject.SetActive(false);

            Debug.LogWarning("[DigitalTwin][AR] Etiqueta de senalado creada (solo modo anclado): " +
                             "nombre del elemento junto al impacto del rayo, sin necesidad de " +
                             "disparar.");
        }

        private void OcultarEtiquetaSenalado()
        {
            if (_etiquetaRaiz == null) return;
            if (_etiquetaRaiz.gameObject.activeSelf) _etiquetaRaiz.gameObject.SetActive(false);
            _metaEtiquetado = null;
        }

        /// <summary>
        /// Coloca el rótulo junto al punto de impacto, un paso hacia el usuario y otro hacia
        /// arriba para no clavarse en la superficie, encarado a la cámara y escalado con la
        /// distancia para conservar su tamaño angular. El texto solo se reasigna al cambiar de
        /// elemento: reescribir un Text fuerza su remaquetado.
        /// </summary>
        private void ActualizarEtiquetaSenalado(IfcMetadata meta, Ray rayo, float distancia)
        {
            if (_etiquetaRaiz == null) return;
            if (meta == null) { OcultarEtiquetaSenalado(); return; }

            if (!_etiquetaRaiz.gameObject.activeSelf) _etiquetaRaiz.gameObject.SetActive(true);
            if (meta != _metaEtiquetado)
            {
                _metaEtiquetado = meta;
                string nombre = string.IsNullOrEmpty(meta.ifcName) ? meta.ifcType : meta.ifcName;
                _etiquetaTexto.text = Recortar(nombre, MaximoCaracteresEtiqueta);
            }

            Vector3 impacto = rayo.origin + rayo.direction * distancia;
            Vector3 posicion = impacto - rayo.direction * 0.10f + Vector3.up * 0.06f;
            _etiquetaRaiz.position = posicion;

            if (_camara != null)
            {
                Vector3 mirada = posicion - _camara.transform.position;
                if (mirada.sqrMagnitude > 1e-6f)
                    _etiquetaRaiz.rotation = Quaternion.LookRotation(mirada, Vector3.up);
            }

            float factor = Mathf.Clamp(distancia, DistanciaMinimaEscala, DistanciaMaximaEscala);
            _etiquetaRaiz.localScale = _etiquetaEscalaBase * factor;
        }

        private static string Recortar(string s, int maximo)
        {
            if (string.IsNullOrEmpty(s) || s.Length <= maximo) return s;
            return s.Substring(0, maximo - 1) + "…";
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
            _impactosDelFotograma = 0;

            ProcesarRayo();

            _crono.Stop();
            MRPerfMonitor.ReportarSeleccion((float)_crono.Elapsed.TotalMilliseconds,
                                            _impactosDelFotograma);
        }

        private void ProcesarRayo()
        {
            if (_rig == null || _camara == null) return;

            // Bloqueo durante el trayecto: un disparo a mitad de camino encadenaria dos
            // desplazamientos y dejaria al usuario en un sitio que no ha elegido.
            if (_navegador != null && _navegador.EnTransito) return;

            // Con el menu abierto (el de navegacion o el de anclado), el rayo es suyo (el
            // gestiona su propia seleccion y el marcador de impacto): pulsar sobre el menu no
            // debe seleccionar lo de detras.
            if (_menuZonas != null && _menuZonas.Abierto) return;
            if (_menuAnclado != null && _menuAnclado.Abierto)
            {
                OcultarEtiquetaSenalado();
                return;
            }

            // Con el panel de colocacion del anclaje abierto (modo anclado), el rayo es suyo:
            // toma puntos de suelo y acciona sus botones; una pulsacion no debe ademas
            // seleccionar lo que haya detras. La etiqueta de senalado se retira: el panel de
            // colocacion ya lleva su propia guia (anillo de suelo y distancia).
            if (_colocacionAnclaje != null && _colocacionAnclaje.CapturaElRayo)
            {
                OcultarEtiquetaSenalado();
                return;
            }

            if (!_rig.TryGetRayo(out Ray rayo))
            {
                _rig.MostrarImpacto(0f, false);
                OcultarEtiquetaSenalado();
                return;
            }

            // 1) La interfaz se consulta ANTES que el mundo. El panel flota ante el usuario,
            // entre él y el edificio, así que en cuanto está abierto se interpone en casi
            // cualquier línea de tiro.
            if (_colocadorPanel != null && _colocadorPanel.RayoImpactaPanel(rayo, out float distPanel))
            {
                _rig.MostrarImpacto(distPanel, true);
                // Sobre la ficha no hay ambiguedad que resolver: la etiqueta se retira.
                OcultarEtiquetaSenalado();

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
            _impactosDelFotograma = numImpactos;

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

            // «Qué estoy señalando», sin pulsar nada: el nombre del primer elemento en la línea
            // de tiro, junto al impacto. Solo existe en modo anclado (null en navegación).
            ActualizarEtiquetaSenalado(primero, rayo, distanciaPrimero);

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

    }
}
