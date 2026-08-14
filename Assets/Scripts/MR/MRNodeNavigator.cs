using System.Collections;
using System.Collections.Generic;
using System.Text;
using DigitalTwin.Core;
using DigitalTwin.Navigation;
using IFCImporter;
using UnityEngine;

namespace DigitalTwin.MR
{
    /// <summary>
    /// Política de navegación por nodos de la versión de Realidad Aumentada: mantiene el nodo
    /// actual, impone el grafo de alcanzabilidad, muestra los carteles de destino y ejecuta el
    /// desplazamiento.
    ///
    /// Es la contraparte inmersiva de <see cref="TourNavigationManager"/> (escritorio). La
    /// definición de «qué destinos son alcanzables» NO vive aquí: ambas versiones consumen
    /// <see cref="NavReachability"/>, de modo que solo existe una. Lo que esta clase añade es
    /// lo específico del visor: mover el origen de realidad extendida en lugar de la cámara
    /// (la cámara la posee el seguimiento de la cabeza y no se puede escribir) y presentar los
    /// destinos como carteles en el espacio en lugar de proyectarlos a pantalla.
    ///
    /// REVERSIÓN DEL 2026-08-14. Entre el 13 y el 14 esta clase encadenaba desplazamientos a
    /// través de los nodos puerta (tránsito automático con giro, continuación por producto
    /// escalar) y ofrecía destinos expandidos «a través de» las puertas, con carteles apilados.
    /// La segunda prueba del 14-08 lo descartó: carteles a alturas imposibles —el apilado sobre el
    /// dintel atravesaba el techo— y una navegación menos predecible. Se vuelve al contrato del
    /// escritorio, que es el que la memoria documenta: PULSAR UN DESTINO ES LLEGAR A ESE
    /// DESTINO, en un único desplazamiento. Estar en el umbral es un destino válido (las
    /// puertas son nodos del grafo a propósito), y verse la hoja delante de la cara lo
    /// resuelve <see cref="PuertaTransparente"/>, que es presentación, no navegación.
    ///
    /// SOBRE LAS POSICIONES. Los destinos se resuelven contra la escena viva (transformadas y
    /// volúmenes actuales), no contra las posiciones guardadas en el asset del grafo: el asset
    /// se generó con el modelo en su pose de autor y quedaría obsoleto si el modelo se moviera.
    ///
    /// SOBRE LAS ALTURAS (unificación del 15-08). En el visor, la altura de los OJOS la impone
    /// el usuario a través del seguimiento a nivel de suelo: no es un parámetro del programa, y
    /// por eso el VIAJE es puramente HORIZONTAL — el origen de realidad extendida se desplaza
    /// en planta hasta el nodo y la vista conserva la estatura real del usuario. El parámetro
    /// de diseño es la altura de los NODOS, unificada en 1,40 m sobre su suelo para todos
    /// (<see cref="PosicionDeNodos"/>, con la investigación antropométrica en su cabecera), de
    /// modo que el marcador de un destino queda en o bajo la línea de visión de prácticamente
    /// cualquier adulto de pie. El cartel se dibuja EN el nodo (MRIndicadoresDestino alinea su
    /// lienzo para que el anillo caiga exactamente a esa altura) y por encima de la geometría,
    /// así que no queda ninguna regla de compensación: ni carteles elevados sobre el nodo, ni
    /// alturas distintas por tipo de nodo, ni viajes que preserven o impongan alturas de
    /// vista. Las tres reglas especiales que hacían eso se eliminaron al unificar la causa. El
    /// refresco registra la altura de cada cartel, y cada llegada la altura real de la vista.
    /// </summary>
    public class MRNodeNavigator : MonoBehaviour
    {
        /// <summary>Misma constante que el desplazamiento simple original: por debajo de esta
        /// longitud la transición no aporta orientación y solo hace esperar.</summary>
        private const float DistanciaMinimaParaAnimar = 1.5f;

        /// <summary>Por encima de esta distancia el desplazamiento se resuelve con un salto
        /// instantáneo, el mismo criterio (y el mismo valor) que el escritorio
        /// (TourNavigationManager.DistanciaSaltoInstantaneo): atravesar medio edificio a
        /// velocidad constante desorienta más que un corte limpio. Hasta el menú de zonas era
        /// teórico —los vecinos del grafo quedan siempre cerca—; con él es el caso normal.</summary>
        private const float DistanciaSaltoInstantaneo = 12f;

        /// <summary>Cuántos destinos garantiza la salida de emergencia. Es el mismo mínimo que
        /// el criterio de proximidad de escritorio (MinHotspotsAlwaysShown): quedarse sin
        /// salidas es el único fallo que la navegación no se puede permitir.</summary>
        private const int MinimoDestinosGarantizados = 3;

        public bool Disponible { get; private set; }
        public bool EnTransito { get; private set; }

        private NavGraphAsset _grafo;
        private Transform _origenXR;
        private Camera _camara;
        private MRIndicadoresDestino _indicadores;

        /// <summary>IfcMetadata de cada nodo del grafo, por índice de nodo. Puede contener null
        /// si el grafo referencia un GlobalId que ya no está en la escena (modelo reimportado).</summary>
        private IfcMetadata[] _metaPorNodo;
        private int _indiceNodoActual = -1;

        /// <summary>Vecinos del nodo actual, cacheados: EsVecinoActual se consulta desde el
        /// raycast de interacción a frecuencia de fotograma y no debe reconstruir listas.</summary>
        private readonly HashSet<int> _vecinosDelNodoActual = new HashSet<int>();

        /// <summary>
        /// Destinos actualmente OFRECIDOS al usuario (los que tienen cartel). Normalmente
        /// coincide con los vecinos del grafo; cuando ninguno es utilizable entra la salida de
        /// emergencia (ver RefrescarIndicadores) y este conjunto contiene los sustitutos. El
        /// viaje valida contra este conjunto: lo que se ofrece se puede pulsar, y nada más.
        /// </summary>
        private readonly HashSet<int> _destinosOfrecidos = new HashSet<int>();

        /// <summary>Etiqueta única por nodo (índice de grafo → nombre con sufijo « · n» si el
        /// nombre visible se repite). Mismo criterio que el escritorio: sin esto, dos nodos de
        /// la misma sala («Comedor» y «Comedor») producen trazas y carteles indistinguibles.</summary>
        private Dictionary<int, string> _etiquetaUnicaPorNodo;

        public string NombreNodoActual =>
            _grafo != null && _indiceNodoActual >= 0 ? Etiqueta(_indiceNodoActual) : "(ninguno)";

        public void Initialize(Transform origenXR, Camera camara, SceneModelIndex index,
                               MRIndicadoresDestino indicadores)
        {
            _origenXR = origenXR;
            _camara = camara;
            _indicadores = indicadores;

            _grafo = Resources.Load<NavGraphAsset>("NavGraph");
            if (_grafo == null || _grafo.Nodos.Count == 0)
            {
                // Sin grafo no hay definición de alcanzabilidad: se degrada al comportamiento
                // antiguo (viajar a cualquier punto señalado) y se vuelven a mostrar las esferas,
                // porque sin carteles el usuario no tendría ningún destino visible. El error deja
                // claro que este estado es una degradación, no el modo normal.
                Debug.LogError("[DigitalTwin][AR] No hay grafo de navegacion (Resources/NavGraph). " +
                               "La navegacion queda SIN restriccion de alcanzabilidad y sin " +
                               "indicadores: se vuelven a mostrar las esferas y se puede viajar a " +
                               "cualquiera. Generalo con Tools > Generar grafo de navegacion.");
                _grafo = null;
                Disponible = false;
                ReactivarEsferas(index);
                return;
            }

            var porGlobalId = new Dictionary<string, IfcMetadata>();
            foreach (var meta in index.AllElements)
                if (meta != null && !string.IsNullOrEmpty(meta.globalId))
                    porGlobalId[meta.globalId] = meta;

            _metaPorNodo = new IfcMetadata[_grafo.Nodos.Count];
            int sinDestino = 0;
            for (int i = 0; i < _grafo.Nodos.Count; i++)
            {
                porGlobalId.TryGetValue(_grafo.Nodos[i].GlobalId, out _metaPorNodo[i]);
                if (_metaPorNodo[i] == null) sinDestino++;
            }

            if (sinDestino > 0)
                Debug.LogWarning($"[DigitalTwin][AR] {sinDestino} de {_grafo.Nodos.Count} nodos del " +
                                 "grafo no corresponden a ningun elemento de la escena. Suele " +
                                 "significar que el modelo se reimporto con GlobalId distintos: " +
                                 "regenera el grafo (Tools > Generar grafo de navegacion).");

            // Etiquetas únicas: mismo criterio (y misma función) que el escritorio. Sin esto,
            // dos puntos de la misma sala se llaman igual y el registro muestra tránsitos
            // «Comedor → Comedor» imposibles de interpretar (prueba del 14-08).
            var metasDelGrafo = new List<IfcMetadata>(_metaPorNodo.Length);
            foreach (var m in _metaPorNodo) if (m != null) metasDelGrafo.Add(m);
            var porGlobalIdEtiqueta = TourNavigationManager.ConstruirEtiquetasUnicas(metasDelGrafo);
            _etiquetaUnicaPorNodo = new Dictionary<int, string>();
            for (int i = 0; i < _grafo.Nodos.Count; i++)
                if (_metaPorNodo[i] != null &&
                    porGlobalIdEtiqueta.TryGetValue(_metaPorNodo[i].globalId, out string etiqueta))
                    _etiquetaUnicaPorNodo[i] = etiqueta;

            Disponible = true;
            Debug.LogWarning($"[DigitalTwin][AR] Navegacion por nodos: grafo cargado " +
                             $"({_grafo.Nodos.Count} nodos, {_grafo.ContarAristas()} aristas, " +
                             $"generado el {_grafo.GeneradoEl}); {_grafo.Nodos.Count - sinDestino} " +
                             "nodos con destino en escena.");
        }

        private void OnDestroy()
        {
            // Por ningún camino debe quedar una hoja invisible al salir de la navegación.
            PuertaTransparente.Restituir();
        }

        /// <summary>
        /// En la degradación sin grafo, las esferas vuelven a ser el único destino visible.
        /// ColliderBootstrapper las oculta al arrancar; aquí se revierte solo esa parte.
        /// </summary>
        private static void ReactivarEsferas(SceneModelIndex index)
        {
            int reactivadas = 0;
            foreach (var meta in index.NavPoints)
            {
                if (meta == null) continue;
                foreach (var r in meta.GetComponentsInChildren<Renderer>(true))
                {
                    r.enabled = true;
                    reactivadas++;
                }
            }
            Debug.LogWarning($"[DigitalTwin][AR] {reactivadas} marcadores de navegacion vueltos a " +
                             "mostrar como degradacion visible de la falta de grafo.");
        }

        /// <summary>
        /// Coloca al usuario en el nodo del grafo más cercano a su posición actual y muestra los
        /// destinos alcanzables desde él. Es el equivalente del encuadre inicial de escritorio.
        /// </summary>
        public void ColocarEnNodoInicial()
        {
            if (!Disponible)
            {
                Debug.LogWarning("[DigitalTwin][AR] Sin grafo no hay nodo inicial: el usuario " +
                                 "queda donde arranca la escena.");
                return;
            }

            int mejor = -1;
            float mejorDist = float.MaxValue;
            Vector3 desde = _camara.transform.position;
            for (int i = 0; i < _grafo.Nodos.Count; i++)
            {
                if (_metaPorNodo[i] == null) continue;
                float d = Vector3.Distance(desde, PosicionDeNodo(i));
                if (d < mejorDist) { mejorDist = d; mejor = i; }
            }

            if (mejor < 0)
            {
                Debug.LogError("[DigitalTwin][AR] Ningun nodo del grafo tiene destino en escena; " +
                               "no se puede establecer nodo inicial. Regenera el grafo tras " +
                               "reimportar el modelo.");
                return;
            }

            // Desplazamiento HORIZONTAL: la altura de la vista es del usuario (seguimiento a
            // nivel de suelo) y ninguna colocación la escribe.
            Vector3 delta = PosicionDeNodo(mejor) - _camara.transform.position;
            delta.y = 0f;
            _origenXR.position += delta;
            _indiceNodoActual = mejor;
            Debug.LogWarning($"[DigitalTwin][AR] Nodo inicial: '{Etiqueta(mejor)}' " +
                             $"(estaba a {mejorDist:0.0} m; vista a " +
                             $"{_camara.transform.position.y:0.00} m del suelo).");
            PuertaTransparente.AlLlegarANodo(MetaDe(mejor));
            RefrescarIndicadores();
        }

        /// <summary>Índice de nodo del grafo para un elemento, o -1 si no es un nodo.</summary>
        public int IndiceDe(IfcMetadata meta)
        {
            if (_grafo == null || meta == null) return -1;
            return _grafo.IndiceDe(meta.globalId);
        }

        public bool EsVecinoActual(int indiceNodo)
        {
            if (!Disponible || _indiceNodoActual < 0) return false;
            return _vecinosDelNodoActual.Contains(indiceNodo);
        }

        /// <summary>
        /// ¿Está este nodo entre los destinos que el usuario tiene delante (cartel visible)?
        /// Es el conjunto que valida el viaje: vecinos del grafo o, si ninguno era utilizable,
        /// los sustitutos de la salida de emergencia.
        /// </summary>
        public bool EsDestinoOfrecido(int indiceNodo)
        {
            if (!Disponible || _indiceNodoActual < 0) return false;
            return _destinosOfrecidos.Contains(indiceNodo);
        }

        /// <summary>¿Corta el rayo el cartel de algún destino alcanzable?</summary>
        public bool TryImpactoIndicador(Ray rayo, out int indiceNodo, out float distancia)
        {
            indiceNodo = -1;
            distancia = 0f;
            if (_indicadores == null || EnTransito) return false;
            return _indicadores.TryImpacto(rayo, out indiceNodo, out distancia);
        }

        /// <summary>
        /// Desplazamiento a un nodo del grafo, con la alcanzabilidad como condición. Un único
        /// desplazamiento al nodo pulsado — el mismo contrato que el escritorio: pulsar un
        /// destino es llegar a ese destino. Devuelve false —siempre con el motivo en el
        /// registro— si el destino no procede.
        /// </summary>
        public bool SolicitarViaje(int indiceDestino)
        {
            if (EnTransito) return false;
            if (!Disponible)
            {
                Debug.LogWarning("[DigitalTwin][AR] Viaje rechazado: no hay grafo cargado.");
                return false;
            }
            if (indiceDestino == _indiceNodoActual) return false;

            if (!EsDestinoOfrecido(indiceDestino))
            {
                // La imposición del grafo, en la capa de navegación y no solo en la visual:
                // aunque el rayo alcance la esfera de un punto lejano, sin arista (ni cartel de
                // emergencia) no hay viaje.
                Debug.LogWarning($"[DigitalTwin][AR] Destino '{Etiqueta(indiceDestino)}' no alcanzable " +
                                 $"desde '{Etiqueta(_indiceNodoActual)}' segun el grafo; " +
                                 "desplazamiento rechazado.");
                return false;
            }

            StartCoroutine(Desplazamiento(PosicionDeNodo(indiceDestino), indiceDestino,
                                          Etiqueta(indiceDestino)));
            return true;
        }

        /// <summary>Sala del nodo actual (LOC_Localizacion4), o cadena vacía. La consume el
        /// menú de zonas para resaltar dónde está el usuario, igual que en escritorio.</summary>
        public string SalaActual
        {
            get
            {
                var meta = MetaDe(_indiceNodoActual);
                return meta != null ? (meta.GetValue("Otros", "LOC_Localizacion4") ?? string.Empty)
                                    : string.Empty;
            }
        }

        /// <summary>
        /// Viaje de ACCESO DIRECTO a una zona: al punto representativo de la sala elegida en el
        /// menú, SIN exigir arista del grafo. Es deliberado y es la paridad exacta con el
        /// escritorio, cuyo menú de zonas tampoco valida alcanzabilidad: el grafo gobierna el
        /// recorrido de proximidad (los carteles), y el menú existe precisamente para el salto
        /// largo que el grafo haría dar en diez etapas. La regla del salto instantáneo por
        /// encima de 12 m hace el resto. Devuelve false, con motivo en el registro, si el
        /// destino no procede.
        /// </summary>
        public bool IrAZona(IfcMetadata destino, string sala)
        {
            if (EnTransito || destino == null) return false;
            if (!Disponible)
            {
                Debug.LogWarning("[DigitalTwin][AR] Menu de zonas: sin grafo no hay nodo actual " +
                                 "que actualizar; se viaja directo al punto.");
                return ViajarDirectoSinGrafo(destino);
            }

            int indice = _grafo.IndiceDe(destino.globalId);
            if (indice < 0)
            {
                // El representante de una sala es siempre una esfera y las 36 son nodos del
                // grafo; si esto salta, el grafo se generó contra otro modelo.
                Debug.LogWarning($"[DigitalTwin][AR] Menu de zonas: el punto representativo de " +
                                 $"'{sala}' no es un nodo del grafo; se viaja igualmente, pero " +
                                 "el nodo actual no se actualizara. Regenera el grafo.");
                return ViajarDirectoSinGrafo(destino);
            }
            if (indice == _indiceNodoActual)
            {
                Debug.LogWarning($"[DigitalTwin][AR] Menu de zonas: ya se esta en el punto " +
                                 $"representativo de '{sala}'; no hay viaje.");
                return false;
            }

            Debug.LogWarning($"[DigitalTwin][AR] Menu de zonas: viaje de acceso directo a " +
                             $"'{sala}' ('{Etiqueta(indice)}'), fuera del grafo a proposito " +
                             "(mismo contrato que el menu de escritorio).");
            StartCoroutine(Desplazamiento(PosicionDeNodo(indice), indice, Etiqueta(indice)));
            return true;
        }

        /// <summary>
        /// Degradación sin grafo: desplazamiento directo al punto señalado, el comportamiento
        /// anterior a la imposición de alcanzabilidad. Solo se llega aquí si el grafo falta,
        /// y eso ya quedó registrado como error al arrancar.
        /// </summary>
        public bool ViajarDirectoSinGrafo(IfcMetadata destino)
        {
            if (EnTransito || destino == null) return false;

            // Solo cuenta la planta del destino: la altura de la vista es del usuario.
            StartCoroutine(Desplazamiento(destino.transform.position, nodoFinal: -1,
                                          etiquetaFinal: destino.ifcName));
            return true;
        }

        public void RefrescarIndicadores()
        {
            if (!Disponible || _indiceNodoActual < 0) return;

            // La única fuente de alcanzabilidad: el mismo ayudante que consume el escritorio.
            var vecinos = NavReachability.VecinosAlcanzables(_grafo, _indiceNodoActual);
            _vecinosDelNodoActual.Clear();
            foreach (int v in vecinos) _vecinosDelNodoActual.Add(v);

            var destinos = new List<MRIndicadoresDestino.Destino>(vecinos.Count);
            int omitidos = 0;
            foreach (int v in vecinos)
            {
                if (_metaPorNodo[v] == null) { omitidos++; continue; }
                destinos.Add(new MRIndicadoresDestino.Destino
                {
                    IndiceNodo = v,
                    Posicion = PosicionDeNodo(v),
                    Etiqueta = Etiqueta(v)
                });
            }

            // SALIDA GARANTIZADA. Un nodo sin ningún destino utilizable deja al usuario
            // encerrado, que es el único fallo que la navegación no se puede permitir (ocurrió
            // en la prueba del 2026-08-13). Si tras resolver vecinos no queda nada que ofrecer,
            // se ofrecen los nodos utilizables más cercanos, exactamente el mismo seguro que el
            // mínimo del criterio de proximidad de escritorio. Nunca en silencio.
            if (destinos.Count == 0)
            {
                var sustitutos = NodosMasCercanos(MinimoDestinosGarantizados);
                foreach (int s in sustitutos)
                {
                    destinos.Add(new MRIndicadoresDestino.Destino
                    {
                        IndiceNodo = s,
                        Posicion = PosicionDeNodo(s),
                        Etiqueta = Etiqueta(s)
                    });
                }
                Debug.LogWarning($"[DigitalTwin][AR] SALIDA DE EMERGENCIA en '{Etiqueta(_indiceNodoActual)}': " +
                                 $"grado {vecinos.Count} en el grafo, {omitidos} sin elemento en " +
                                 $"escena, 0 destinos utilizables. Se ofrecen los {destinos.Count} " +
                                 "nodos mas cercanos para que el usuario nunca quede encerrado.");
            }

            _destinosOfrecidos.Clear();
            foreach (var d in destinos) _destinosOfrecidos.Add(d.IndiceNodo);

            if (_indicadores != null) _indicadores.Mostrar(destinos);

            // La altura de cada cartel va al registro: en la prueba del 14-08 hubo carteles a
            // alturas que no correspondian (hasta atravesar el techo) y no habia forma de saber
            // desde el registro CUALES ni CUANTO. Con esta linea, la proxima discrepancia entre
            // lo que se ve y lo que deberia verse se diagnostica sin visor.
            var alturas = new StringBuilder();
            for (int i = 0; i < destinos.Count; i++)
            {
                if (i > 0) alturas.Append("; ");
                alturas.Append('\'').Append(destinos[i].Etiqueta).Append("' y=")
                       .Append(destinos[i].Posicion.y.ToString("0.00"));
            }
            Debug.LogWarning($"[DigitalTwin][AR] Indicadores: {destinos.Count} destinos ofrecidos " +
                             $"desde '{Etiqueta(_indiceNodoActual)}' (grado {vecinos.Count} en el grafo" +
                             (omitidos > 0 ? $", {omitidos} sin elemento en escena" : "") +
                             $"). Carteles: {alturas}.");
        }

        /// <summary>
        /// Los K nodos utilizables más cercanos al actual (por distancia viva), excluyéndolo.
        /// Solo se usa como salida de emergencia; la navegación normal es el grafo.
        /// </summary>
        private List<int> NodosMasCercanos(int k)
        {
            var candidatos = new List<(float dist, int indice)>();
            Vector3 desde = PosicionDeNodo(_indiceNodoActual);
            for (int i = 0; i < _grafo.Nodos.Count; i++)
            {
                if (i == _indiceNodoActual || _metaPorNodo[i] == null) continue;
                candidatos.Add((Vector3.Distance(desde, PosicionDeNodo(i)), i));
            }
            candidatos.Sort((a, b) => a.dist.CompareTo(b.dist));

            var resultado = new List<int>(k);
            for (int i = 0; i < candidatos.Count && resultado.Count < k; i++)
                resultado.Add(candidatos[i].indice);
            return resultado;
        }

        // ------------------------------------------------------------------ desplazamiento ----

        /// <summary>
        /// Un único desplazamiento continuo en línea recta, el «desplazamiento simple» que
        /// documenta la memoria: duración proporcional a la distancia, acotada, y suavizado
        /// solo en el tiempo. Mueve el origen de realidad extendida por la diferencia de
        /// posiciones de cámara, de modo que la cabeza del usuario termina exactamente en el
        /// destino aunque se mueva durante la transición.
        ///
        /// EL DESPLAZAMIENTO ES HORIZONTAL: del destino solo cuenta su planta (x, z) y la
        /// vista conserva la estatura real del usuario, que con el seguimiento a nivel de
        /// suelo es quien la impone. Ninguna llegada escribe la altura de la vista.
        /// </summary>
        private IEnumerator Desplazamiento(Vector3 destino, int nodoFinal, string etiquetaFinal)
        {
            EnTransito = true;
            if (_indicadores != null) _indicadores.OcultarTodos();

            Vector3 desde = _camara.transform.position;
            Vector3 hasta = new Vector3(destino.x, desde.y, destino.z);
            float distancia = Vector3.Distance(desde, hasta);

            // Corto: instantáneo porque animar no aporta. Largo: instantáneo porque animar
            // desorienta (regla de escritorio, mismo umbral). Solo el tramo intermedio se anima.
            bool saltoPorLargo = distancia > DistanciaSaltoInstantaneo;
            float duracion = distancia < DistanciaMinimaParaAnimar || saltoPorLargo
                ? 0f
                : Mathf.Clamp(distancia * 0.05f, 0.35f, 1.1f);

            Debug.LogWarning($"[DigitalTwin][AR] Desplazamiento hacia '{etiquetaFinal}': " +
                             $"{distancia:0.0} m, " +
                             (saltoPorLargo
                                 ? $"salto instantaneo (supera los {DistanciaSaltoInstantaneo:0} m)."
                                 : $"{duracion:0.00} s."));

            if (duracion <= 0f)
            {
                _origenXR.position += hasta - desde;
            }
            else
            {
                float t = 0f;
                Vector3 previo = desde;
                while (t < 1f)
                {
                    t += Time.deltaTime / duracion;
                    // Suavizado solo en el TIEMPO (arranque y frenada); la trayectoria en el
                    // espacio es el segmento exacto.
                    float s = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                    Vector3 punto = Vector3.Lerp(desde, hasta, s);
                    _origenXR.position += punto - previo;
                    previo = punto;
                    yield return null;
                }
                _origenXR.position += hasta - previo;
            }

            if (nodoFinal >= 0) _indiceNodoActual = nodoFinal;
            EnTransito = false;
            // La altura de la vista va en cada llegada: es el dato que permite comprobar en una
            // sola prueba que el seguimiento a nivel de suelo funciona (debe ser la estatura
            // real del usuario, estable entre llegadas, no la altura de ningún nodo).
            Debug.LogWarning($"[DigitalTwin][AR] Llegada a '{etiquetaFinal}' (vista a " +
                             $"{_camara.transform.position.y:0.00} m del suelo).");

            // La regla de la puerta: si el nodo alcanzado es una puerta, su hoja deja de
            // dibujarse mientras se ocupe; si no lo es, cualquier hoja oculta se restituye.
            PuertaTransparente.AlLlegarANodo(nodoFinal >= 0 ? MetaDe(nodoFinal) : null);
            RefrescarIndicadores();
        }

        // ------------------------------------------------------------------ nodos ------------

        private IfcMetadata MetaDe(int indiceNodo)
        {
            if (_metaPorNodo == null || indiceNodo < 0 || indiceNodo >= _metaPorNodo.Length) return null;
            return _metaPorNodo[indiceNodo];
        }

        /// <summary>
        /// Posición viva del nodo: la regla ÚNICA de <see cref="PosicionDeNodos"/> (planta del
        /// elemento + 1,40 m sobre su suelo), la misma que usan el generador del grafo y el
        /// gestor de escritorio. Sirve a la vez de destino de viaje (solo cuenta su planta),
        /// de anclaje del cartel (el anillo cae exactamente a esta altura) y de referencia de
        /// distancias. Un nodo sin elemento en escena cae a la posición del asset, que tras
        /// regenerar el grafo lleva la misma altura.
        /// </summary>
        private Vector3 PosicionDeNodo(int indiceNodo)
        {
            var meta = MetaDe(indiceNodo);
            if (meta == null) return _grafo.Nodos[indiceNodo].Posicion;
            return PosicionDeNodos.De(meta);
        }

        private string Etiqueta(int indiceNodo)
        {
            // Primero la etiqueta única («Comedor · 2»): sin el ordinal, dos nodos de la misma
            // sala son indistinguibles en carteles y trazas («Transito hacia 'Comedor'» estando
            // en Comedor, prueba del 14-08).
            if (_etiquetaUnicaPorNodo != null &&
                _etiquetaUnicaPorNodo.TryGetValue(indiceNodo, out string unica))
                return unica;

            var meta = MetaDe(indiceNodo);
            if (meta != null) return TourNavigationManager.BuildDisplayName(meta);
            return _grafo != null && indiceNodo >= 0 && indiceNodo < _grafo.Nodos.Count
                ? _grafo.Nodos[indiceNodo].Nombre
                : $"nodo {indiceNodo}";
        }
    }
}
