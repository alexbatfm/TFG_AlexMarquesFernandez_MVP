using System.Collections;
using System.Collections.Generic;
using DigitalTwin.Core;
using DigitalTwin.Navigation;
using IFCImporter;
using UnityEngine;

namespace DigitalTwin.MR
{
    /// <summary>
    /// Política de navegación por nodos de la versión de Realidad Aumentada: mantiene el nodo
    /// actual, impone el grafo de alcanzabilidad, muestra los indicadores de destino y ejecuta
    /// los desplazamientos —incluido el tránsito automático a través de los nodos de puerta—.
    ///
    /// Es la contraparte inmersiva de <see cref="TourNavigationManager"/> (escritorio). La
    /// definición de «qué destinos son alcanzables» NO vive aquí: ambas versiones consumen
    /// <see cref="NavReachability"/>, de modo que solo existe una. Lo que esta clase añade es
    /// lo específico del visor: mover el origen de realidad extendida en lugar de la cámara
    /// (la cámara la posee el seguimiento de la cabeza y no se puede escribir), presentar los
    /// destinos como carteles en el espacio en lugar de proyectarlos a pantalla, y girar el
    /// mundo —no la cabeza del usuario— cuando el tránsito cruza una puerta.
    ///
    /// SOBRE LAS POSICIONES. Los destinos se resuelven contra la escena viva (transformadas y
    /// volúmenes actuales), no contra las posiciones guardadas en el asset del grafo: el asset
    /// se generó con el modelo en su pose de autor y quedaría obsoleto si el modelo se moviera.
    /// Para las DIRECCIONES del producto escalar sí se usan las posiciones del asset, que en
    /// modo de navegación por nodos coinciden con las vivas (el modelo no se mueve en este
    /// modo; el modo anclado no usa esta clase).
    /// </summary>
    public class MRNodeNavigator : MonoBehaviour
    {
        /// <summary>Misma constante que usaba el desplazamiento simple: por debajo de esta
        /// longitud total la transición no aporta orientación y solo hace esperar.</summary>
        private const float DistanciaMinimaParaAnimar = 1.5f;

        /// <summary>Altura de la vista si el nodo final no tiene una posición utilizable.</summary>
        private const float AlturaVistaPorDefecto = 1.6f;

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

        /// <summary>Cuántos destinos garantiza la salida de emergencia. Es el mismo mínimo que
        /// el criterio de proximidad de escritorio (MinHotspotsAlwaysShown): quedarse sin
        /// salidas es el único fallo que la navegación no se puede permitir.</summary>
        private const int MinimoDestinosGarantizados = 3;

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

            Disponible = true;
            Debug.LogWarning($"[DigitalTwin][AR] Navegacion por nodos: grafo cargado " +
                             $"({_grafo.Nodos.Count} nodos, {_grafo.ContarAristas()} aristas, " +
                             $"generado el {_grafo.GeneradoEl}); {_grafo.Nodos.Count - sinDestino} " +
                             "nodos con destino en escena.");
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

            _origenXR.position += PosicionDeNodo(mejor) - _camara.transform.position;
            _indiceNodoActual = mejor;
            Debug.LogWarning($"[DigitalTwin][AR] Nodo inicial: '{Etiqueta(mejor)}' " +
                             $"(estaba a {mejorDist:0.0} m).");
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
        /// Desplazamiento a un nodo del grafo, con la alcanzabilidad como condición y el
        /// tránsito por puertas resuelto. Devuelve false —siempre con el motivo en el
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

            var ruta = NavReachability.ResolverDestino(_grafo, _indiceNodoActual, indiceDestino,
                                                       EsPuerta);
            if (ruta.Count == 0)
            {
                Debug.LogWarning("[DigitalTwin][AR] La resolucion del destino devolvio una ruta " +
                                 "vacia; no se viaja.");
                return false;
            }

            // Registro del tránsito por puertas: nodo a nodo, con el producto escalar que
            // justifica cada continuación elegida.
            int previo = _indiceNodoActual;
            for (int j = 0; j < ruta.Count - 1; j++)
            {
                float producto = NavReachability.ProductoEscalarDe(_grafo, previo, ruta[j], ruta[j + 1]);
                Debug.LogWarning($"[DigitalTwin][AR] Nodo puerta '{Etiqueta(ruta[j])}': el transito " +
                                 $"continua hacia '{Etiqueta(ruta[j + 1])}' (producto escalar " +
                                 $"{producto:0.00}).");
                previo = ruta[j];
            }

            StartCoroutine(Transito(ruta));
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

            Vector3 pos = destino.transform.position;
            float altura = pos.y > 0.01f ? pos.y : AlturaVistaPorDefecto;
            var ruta = new List<Vector3> { _camara.transform.position, new Vector3(pos.x, altura, pos.z) };
            StartCoroutine(TransitoPorPolilinea(ruta, new bool[] { false, false },
                                                nodoFinal: -1, etiquetaFinal: destino.ifcName));
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
                    Posicion = PosicionDeCartel(v),
                    Etiqueta = Etiqueta(v)
                });
            }

            // SALIDA GARANTIZADA. Un nodo sin ningún destino utilizable deja al usuario
            // encerrado, que es el único fallo que la navegación no se puede permitir (ocurrió
            // en la prueba del 2026-08-13: un punto de baño cuyos únicos vecinos eran puertas
            // con el cartel hundido en la propia hoja). Si tras resolver vecinos no queda nada
            // que ofrecer, se ofrecen los nodos utilizables más cercanos, exactamente el mismo
            // seguro que el mínimo del criterio de proximidad de escritorio. Nunca en silencio.
            if (destinos.Count == 0)
            {
                var sustitutos = NodosMasCercanos(MinimoDestinosGarantizados);
                foreach (int s in sustitutos)
                {
                    destinos.Add(new MRIndicadoresDestino.Destino
                    {
                        IndiceNodo = s,
                        Posicion = PosicionDeCartel(s),
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
            Debug.LogWarning($"[DigitalTwin][AR] Indicadores: {destinos.Count} destinos ofrecidos " +
                             $"desde '{Etiqueta(_indiceNodoActual)}'" +
                             (omitidos > 0 ? $" ({omitidos} nodos del grafo sin elemento en escena)." : "."));
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

        /// <summary>
        /// Punto de anclaje del CARTEL de un nodo, que no siempre es su punto de viaje.
        ///
        /// Para las esferas coinciden. Para las puertas no: el punto de viaje es el centro del
        /// volumen de la hoja (por ahí se pasa), pero un cartel colgado ahí queda HUNDIDO EN LA
        /// PROPIA HOJA y la prueba de profundidad lo oculta — es la causa de que en la prueba
        /// del 2026-08-13 un punto de baño rodeado solo de puertas pareciera no tener salidas.
        /// El cartel de una puerta se cuelga sobre su dintel (tope del volumen), donde flota
        /// libre y se ve desde ambos lados.
        /// </summary>
        private Vector3 PosicionDeCartel(int indiceNodo)
        {
            var meta = MetaDe(indiceNodo);
            if (meta != null && meta.ifcType == "IfcDoor")
            {
                var r = meta.GetComponentInChildren<Renderer>();
                if (r != null)
                    return new Vector3(r.bounds.center.x, r.bounds.max.y + 0.05f, r.bounds.center.z);
            }
            return PosicionDeNodo(indiceNodo);
        }

        // ------------------------------------------------------------------ tránsito ---------

        private IEnumerator Transito(List<int> ruta)
        {
            // Polilínea en mundo: del usuario al primer nodo y de ahí, nodo a nodo, hasta el
            // final. SIN suavizar la trayectoria: por el vano de una puerta hay que pasar, y
            // recortar la esquina significa atravesar la jamba.
            var puntos = new List<Vector3>(ruta.Count + 1) { _camara.transform.position };
            var esPuertaPunto = new bool[ruta.Count + 1];
            for (int j = 0; j < ruta.Count; j++)
            {
                puntos.Add(PosicionDeNodo(ruta[j]));
                esPuertaPunto[j + 1] = EsPuerta(ruta[j]);
            }

            // Altura: el punto final manda (regla del desplazamiento simple); los intermedios
            // interpolan entre la altura de partida y la final para que cruzar una puerta —cuyo
            // centro de hoja queda a media altura— no hunda la vista y la devuelva a subir.
            int ultimo = puntos.Count - 1;
            float alturaFinal = puntos[ultimo].y > 0.01f ? puntos[ultimo].y : AlturaVistaPorDefecto;
            puntos[ultimo] = new Vector3(puntos[ultimo].x, alturaFinal, puntos[ultimo].z);
            float largoXZ = 0f;
            var acumuladoXZ = new float[puntos.Count];
            for (int j = 1; j < puntos.Count; j++)
            {
                Vector3 a = puntos[j - 1], b = puntos[j];
                a.y = 0; b.y = 0;
                largoXZ += Vector3.Distance(a, b);
                acumuladoXZ[j] = largoXZ;
            }
            for (int j = 1; j < ultimo; j++)
            {
                float f = largoXZ > 0.001f ? acumuladoXZ[j] / largoXZ : 0f;
                puntos[j] = new Vector3(puntos[j].x,
                                        Mathf.Lerp(puntos[0].y, alturaFinal, f),
                                        puntos[j].z);
            }

            yield return TransitoPorPolilinea(puntos, esPuertaPunto,
                                              nodoFinal: ruta[ruta.Count - 1],
                                              etiquetaFinal: Etiqueta(ruta[ruta.Count - 1]));
        }

        /// <summary>
        /// Un único desplazamiento continuo sobre la polilínea. La duración y el umbral de
        /// animación se calculan sobre la LONGITUD TOTAL del recorrido, no sobre la distancia en
        /// línea recta: un tránsito que rodea por una puerta recorre más metros que los que
        /// separan origen y destino, y debe durar en consecuencia.
        ///
        /// El giro: al aproximarse a un punto de puerta, el origen de realidad extendida gira en
        /// horizontal —alrededor de la posición del usuario— hasta que la vista queda mirando la
        /// dirección de salida, completándose ANTES de alcanzar la puerta. Se gira el mundo una
        /// cantidad fija calculada al entrar en el tramo; los giros de cabeza del usuario durante
        /// el tránsito se respetan, no se contrarrestan.
        /// </summary>
        private IEnumerator TransitoPorPolilinea(List<Vector3> puntos, bool[] esPuertaPunto,
                                                 int nodoFinal, string etiquetaFinal)
        {
            EnTransito = true;
            if (_indicadores != null) _indicadores.OcultarTodos();

            int tramos = puntos.Count - 1;
            var acumulado = new float[puntos.Count];
            float largoTotal = 0f;
            for (int j = 1; j < puntos.Count; j++)
            {
                largoTotal += Vector3.Distance(puntos[j - 1], puntos[j]);
                acumulado[j] = largoTotal;
            }

            float duracion = largoTotal < DistanciaMinimaParaAnimar
                ? 0f
                : Mathf.Clamp(largoTotal * 0.05f, 0.35f, 1.1f);

            Debug.LogWarning($"[DigitalTwin][AR] Transito hacia '{etiquetaFinal}': {tramos} tramo(s), " +
                             $"{largoTotal:0.0} m, {duracion:0.00} s.");

            if (duracion <= 0f)
            {
                _origenXR.position += puntos[puntos.Count - 1] - puntos[0];
            }
            else
            {
                float t = 0f;
                float dPrevio = 0f;
                int tramoActual = -1;
                float gradosPorMetro = 0f;

                while (t < 1f)
                {
                    t += Time.deltaTime / duracion;
                    // Suavizado solo en el TIEMPO (arranque y frenada del conjunto); la
                    // trayectoria en el espacio sigue siendo la polilínea exacta.
                    float s = Mathf.SmoothStep(0f, 1f, Mathf.Clamp01(t));
                    float d = s * largoTotal;

                    int tramo = TramoDe(acumulado, d);
                    if (tramo != tramoActual)
                    {
                        tramoActual = tramo;
                        gradosPorMetro = GradosPorMetroDeGiro(puntos, esPuertaPunto, tramo,
                                                              acumulado, d);
                    }

                    float avance = d - dPrevio;
                    if (gradosPorMetro != 0f && avance > 0f)
                        _origenXR.RotateAround(_camara.transform.position, Vector3.up,
                                               gradosPorMetro * avance);

                    _origenXR.position += PuntoEn(puntos, acumulado, d) - PuntoEn(puntos, acumulado, dPrevio);
                    dPrevio = d;
                    yield return null;
                }

                _origenXR.position += PuntoEn(puntos, acumulado, largoTotal) - PuntoEn(puntos, acumulado, dPrevio);
            }

            if (nodoFinal >= 0) _indiceNodoActual = nodoFinal;
            EnTransito = false;
            Debug.LogWarning($"[DigitalTwin][AR] Llegada a '{etiquetaFinal}'.");
            RefrescarIndicadores();
        }

        /// <summary>
        /// Si el tramo termina en una puerta con continuación, grados de giro horizontal por
        /// metro recorrido para que la vista quede mirando la dirección de salida justo al
        /// llegar a la puerta. Cero si el tramo no pide giro.
        /// </summary>
        private float GradosPorMetroDeGiro(List<Vector3> puntos, bool[] esPuertaPunto, int tramo,
                                           float[] acumulado, float dActual)
        {
            int destinoTramo = tramo + 1;
            if (destinoTramo >= puntos.Count || !esPuertaPunto[destinoTramo]) return 0f;
            if (destinoTramo + 1 >= puntos.Count) return 0f; // puerta sin continuación: no se gira

            Vector3 salida = puntos[destinoTramo + 1] - puntos[destinoTramo];
            salida.y = 0f;
            if (salida.sqrMagnitude < 0.0001f) return 0f;

            Vector3 mirada = _camara.transform.forward;
            mirada.y = 0f;
            if (mirada.sqrMagnitude < 0.0001f) return 0f;

            float grados = Vector3.SignedAngle(mirada.normalized, salida.normalized, Vector3.up);
            float metrosRestantes = acumulado[destinoTramo] - dActual;
            if (metrosRestantes < 0.05f) return 0f;

            return grados / metrosRestantes;
        }

        private static int TramoDe(float[] acumulado, float d)
        {
            for (int j = 1; j < acumulado.Length; j++)
                if (d <= acumulado[j]) return j - 1;
            return acumulado.Length - 2;
        }

        private static Vector3 PuntoEn(List<Vector3> puntos, float[] acumulado, float d)
        {
            int tramo = TramoDe(acumulado, d);
            float enTramo = d - acumulado[tramo];
            float largoTramo = acumulado[tramo + 1] - acumulado[tramo];
            float f = largoTramo > 0.0001f ? Mathf.Clamp01(enTramo / largoTramo) : 1f;
            return Vector3.Lerp(puntos[tramo], puntos[tramo + 1], f);
        }

        // ------------------------------------------------------------------ nodos ------------

        private bool EsPuerta(int indiceNodo)
        {
            var meta = MetaDe(indiceNodo);
            return meta != null && meta.ifcType == "IfcDoor";
        }

        private IfcMetadata MetaDe(int indiceNodo)
        {
            if (_metaPorNodo == null || indiceNodo < 0 || indiceNodo >= _metaPorNodo.Length) return null;
            return _metaPorNodo[indiceNodo];
        }

        /// <summary>
        /// Posición viva del nodo: el origen del objeto para los puntos "Esfera..." (ya están a
        /// la altura de la vista) y el centro del volumen para las puertas (su origen cae en una
        /// esquina del marco; usarlo dejaría la vista incrustada en el tabique). Es la misma
        /// regla que aplica el gestor de escritorio.
        /// </summary>
        private Vector3 PosicionDeNodo(int indiceNodo)
        {
            var meta = MetaDe(indiceNodo);
            if (meta == null) return _grafo.Nodos[indiceNodo].Posicion;

            if (meta.ifcType == "IfcDoor")
            {
                var r = meta.GetComponentInChildren<Renderer>();
                if (r != null) return r.bounds.center;
            }
            return meta.transform.position;
        }

        private string Etiqueta(int indiceNodo)
        {
            var meta = MetaDe(indiceNodo);
            if (meta != null) return TourNavigationManager.BuildDisplayName(meta);
            return _grafo != null && indiceNodo >= 0 && indiceNodo < _grafo.Nodos.Count
                ? _grafo.Nodos[indiceNodo].Nombre
                : $"nodo {indiceNodo}";
        }
    }
}
