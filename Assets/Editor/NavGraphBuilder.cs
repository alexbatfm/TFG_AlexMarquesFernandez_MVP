using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DigitalTwin.Core;
using DigitalTwin.Navigation;
using IFCImporter;
using UnityEditor;
using UnityEngine;

namespace DigitalTwin.EditorTools
{
    /// <summary>
    /// Genera el grafo de navegación entre los puntos "Esfera..." del modelo y lo guarda como
    /// asset (<see cref="NavGraphAsset"/>). Menú: Tools > IFC > Generar grafo de navegación.
    ///
    /// EL PROBLEMA QUE RESUELVE
    /// Elegir a qué puntos se puede saltar desde el punto actual admite soluciones sencillas que
    /// no funcionan bien:
    ///
    ///  - Por línea de visión: el edificio está lleno de tabiques, mamparas de vidrio y puertas
    ///    cerradas que bloquean la línea sin impedir que un operario llegue andando. Deja zonas
    ///    incomunicadas.
    ///  - Por radio: en una sala diáfana con muchos puntos alineados, los N más cercanos caen
    ///    casi en la misma dirección, se solapan en pantalla y provocan clics no deseados. Además
    ///    no garantiza que todas las zonas queden conectadas.
    ///
    /// LA SOLUCIÓN: GRAFO DE VECINDAD RELATIVA (RNG) SOBRE UN COSTE, NO SOBRE LA DISTANCIA
    /// Se conectan dos puntos A y B solo si no existe un tercer punto C al que llegar sea más
    /// barato desde ambos extremos que unirlos directamente. Formalmente, si no hay ningún C tal
    /// que max(c(A,C), c(B,C)) &lt; c(A,B).
    ///
    /// La clave está en qué se mide como coste. No es la distancia pura, sino la distancia más
    /// una penalización por lo que el trayecto atraviesa: nada, un paso practicable (puerta,
    /// hueco, escalera) o un cerramiento (muro, forjado, pilar). Así el algoritmo prefiere,
    /// por este orden, el camino despejado, el que cruza por donde cruzaría una persona, y solo
    /// como último recurso el que atraviesa un muro.
    ///
    /// Integrar la preferencia en la métrica, en lugar de añadir reglas aparte, hace que se
    /// propague por sí sola a toda la estructura: dos puntos separados por un tabique dejan de
    /// conectarse en cuanto existe un tercero que los comunica rodeando por la puerta, porque
    /// esa es justamente la condición que evalúa el algoritmo.
    ///
    /// La consecuencia práctica es justo lo que se busca: en una fila de puntos alineados, cada
    /// uno queda unido únicamente a sus vecinos inmediatos, porque para cualquier par no
    /// consecutivo existe un punto intermedio que rompe la condición. Desaparecen los hotspots
    /// superpuestos sin necesidad de filtros angulares que descartarían destinos legítimos.
    ///
    /// Además el RNG contiene siempre al árbol de recubrimiento mínimo, así que sobre un conjunto
    /// de puntos completo el grafo resultante es conexo por construcción. Como aquí se filtra
    /// por planta y opcionalmente por longitud de arista, esa garantía puede perderse, y por eso
    /// hay una pasada final que detecta componentes aisladas y las une por su par más próximo.
    ///
    /// Las distancias se miden en horizontal (ignorando la altura) y solo se consideran pares con
    /// un desnivel pequeño, para que el grafo no salte de planta.
    ///
    /// El análisis de obstáculos exige volúmenes de colisión, que en modo edición no existen
    /// porque los añade ColliderBootstrapper al arrancar. La herramienta los crea de forma
    /// temporal y los retira al terminar, dejando la escena como estaba.
    /// </summary>
    public static class NavGraphBuilder
    {
        private const string RutaCarpeta = "Assets/Resources";
        private const string RutaAsset = RutaCarpeta + "/NavGraph.asset";

        // Puntos con más desnivel que esto no se conectan entre sí: se consideran plantas
        // distintas. Coincide con el valor por defecto de TourNavigationManager.
        private const float ToleranciaVertical = 2.5f;

        // Corta aristas absurdamente largas que el RNG puede generar en zonas con pocos puntos
        // (por ejemplo, un punto suelto en un extremo del edificio). 0 = sin límite.
        private const float LongitudMaximaArista = 25f;

        // --- Penalizaciones por atravesar geometría -----------------------------------------
        //
        // El grafo no se limita a evitar los obstáculos: los convierte en coste. En lugar de
        // descartar toda arista que cruce algo -que fue el primer criterio del proyecto y dejaba
        // zonas incomunicadas- se mide qué atraviesa cada trayecto y se le suma una penalización
        // expresada en metros equivalentes.
        //
        // Así, entre dos formas de llegar al mismo sitio el algoritmo prefiere la que no cruza
        // nada; si no hay, la que cruza una puerta o un hueco de paso; y solo si no queda
        // alternativa, la que atraviesa un muro. Al integrarse en la misma métrica que usa el
        // grafo de vecindad relativa, esa preferencia se propaga a toda la estructura sin
        // necesidad de reglas especiales.
        //
        // Las cifras están en metros y son deliberadamente asimétricas: cruzar una puerta
        // equivale a rodear dos metros -es lo que haría una persona-, mientras que atravesar un
        // muro equivale a treinta, de modo que solo se elige cuando la alternativa es no tener
        // conexión.
        private const float PenalizacionPaso = 2f;     // puertas, huecos, escaleras
        private const float PenalizacionVidrio = 8f;   // ventanas, mamparas, muro cortina
        private const float PenalizacionMuro = 30f;    // muros, forjados, pilares
        private const float PenalizacionOtro = 10f;    // mobiliario y demás

        /// <summary>
        /// Incorporar las puertas del modelo como nodos intermedios del grafo. Ver RecogerPuntos
        /// para el motivo: sin ellas el algoritmo no tiene por dónde rodear y se ve obligado a
        /// atravesar tabiques.
        /// </summary>
        private const bool UsarPuertasComoNodos = true;

        /// <summary>
        /// Pasos practicables: atravesarlos es lo que hace una persona al ir de una estancia a
        /// otra, no un atajo imposible.
        /// </summary>
        private static readonly string[] TiposDePaso =
        {
            "IfcDoor", "IfcOpeningElement", "IfcStair", "IfcStairFlight", "IfcRamp"
        };

        /// <summary>
        /// Cerramientos transparentes: ventanas, mamparas y muro cortina con sus montantes y
        /// paneles.
        ///
        /// Ocupan un escalón intermedio propio, y no el de los muros, por una razón que tiene que
        /// ver con la orientación del usuario y no con la geometría: a través del vidrio se ve el
        /// destino. Un salto que cruza una mampara resulta comprensible -se sabe adónde se va-
        /// mientras que uno que atraviesa un tabique opaco parece que el sistema falla. Cruzarlos
        /// no es lo ideal, porque físicamente no se puede, pero es preferible a atravesar fábrica.
        ///
        /// Se incluyen IfcMember e IfcPlate porque en este modelo son los montantes y los paneles
        /// de la fachada acristalada: un rayo que impacta en ellos está, en la práctica, cruzando
        /// el muro cortina.
        /// </summary>
        private static readonly string[] TiposTransparentes =
        {
            "IfcWindow", "IfcCurtainWall", "IfcPlate", "IfcMember"
        };

        /// <summary>Cerramientos opacos: delimitan el espacio y no dejan ver al otro lado.</summary>
        private static readonly string[] TiposDeCerramiento =
        {
            "IfcWall", "IfcWallStandardCase", "IfcSlab", "IfcRoof", "IfcColumn", "IfcBeam"
        };

        // Cuelga de Tools y no de Tools/IFC: el submenu IFC agrupa lo que opera sobre el modelo
        // IFC y sus metadatos, y el grafo de navegacion no lo hace. Se calcula sobre la geometria
        // ya importada, y seguiria teniendo sentido con un modelo de otra procedencia.
        [MenuItem("Tools/Generar grafo de navegación")]
        public static void Generar()
        {
            var puntos = RecogerPuntos();
            if (puntos.Count < 2)
            {
                EditorUtility.DisplayDialog("Grafo de navegación",
                    $"Se han encontrado {puntos.Count} puntos de navegación en la escena abierta.\n\n" +
                    "Comprueba que la escena con el modelo esté abierta y que los metadatos IFC " +
                    "se hayan importado (Tools > IFC > Import Metadata).", "Vale");
                return;
            }

            // Los colliders no existen en modo edicion: los crea ColliderBootstrapper al
            // arrancar. Se anaden temporalmente para poder analizar que atraviesa cada trayecto.
            var collidersTemporales = AsegurarColliders();
            var aristas = new HashSet<(int, int)>();
            int aristasRNG = 0, añadidasPorConectividad = 0;
            int conMuro = 0, conVidrio = 0, conPaso = 0, limpias = 0;
            try
            {
                var coste = CalcularCostes(puntos, out int[,] muros, out int[,] pasos,
                                           out int[,] vidrios);

                aristas = ConstruirRNG(puntos, coste);
                aristasRNG = aristas.Count;
                añadidasPorConectividad = GarantizarConectividad(puntos, aristas, coste);

                // Se clasifica por el obstaculo mas restrictivo que atraviesa cada arista.
                foreach (var (a, b) in aristas)
                {
                    if (muros[a, b] > 0) conMuro++;
                    else if (vidrios[a, b] > 0) conVidrio++;
                    else if (pasos[a, b] > 0) conPaso++;
                    else limpias++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                RetirarColliders(collidersTemporales);
            }

            var asset = CrearAsset(puntos, aristas);

            Debug.Log($"[NavGraph] Grafo generado: {puntos.Count} nodos, {asset.ContarAristas()} aristas " +
                      $"({aristasRNG} del RNG + {añadidasPorConectividad} para unir componentes aisladas). " +
                      $"Trayectos sin obstaculos: {limpias}; por puertas o huecos: {conPaso}; " +
                      $"a traves de vidrio: {conVidrio}; atravesando fabrica: {conMuro}. " +
                      $"Guardado en {RutaAsset}.");

            if (conMuro > 0)
                Debug.LogWarning($"[NavGraph] {conMuro} aristas atraviesan fabrica opaca. Es el ultimo " +
                                 "recurso del algoritmo: ocurre cuando no hay ninguna alternativa por " +
                                 "puerta. Conviene revisarlas en el asset y comprobar si falta algun punto " +
                                 "de navegacion intermedio que permitiria rodear.");

            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        private sealed class Punto
        {
            public IfcMetadata Meta;
            public Vector3 Pos;
            public string Sala;
            public bool EsPuerta;
        }

        /// <summary>
        /// Posición del nodo. Para las puertas se usa el centro de su volumen y no el origen del
        /// objeto, que en la geometría procedente de Revit suele quedar en una esquina del marco
        /// y dejaría el nodo incrustado en el tabique.
        /// </summary>
        private static Vector3 PosicionDe(IfcMetadata meta)
        {
            var r = meta.GetComponentInChildren<Renderer>();
            return r != null ? r.bounds.center : meta.transform.position;
        }

        /// <summary>
        /// Recoge los nodos del grafo: los puntos "Esfera..." del IFC y, además, las puertas.
        ///
        /// Por qué las puertas son nodos. Los puntos de navegación del modelo están situados en
        /// el centro de las estancias, no en los umbrales. Sin un nodo intermedio en la puerta, el
        /// algoritmo no tiene por dónde rodear: para ir de una sala a la contigua no existe
        /// ninguna alternativa mejor que la línea recta, que atraviesa el tabique. La penalización
        /// no sirve de nada, porque se aplica por igual a todos los caminos posibles.
        ///
        /// Añadir las puertas como nodos resuelve el problema en su origen y sale prácticamente
        /// gratis, porque el modelo IFC ya sabe dónde están: son entidades <c>IfcDoor</c> con su
        /// posición y su identificador global. Es además el punto por el que un operario pasaría
        /// realmente, de modo que el recorrido resultante se parece más a caminar por el edificio
        /// que a atravesarlo.
        ///
        /// Un umbral es un sitio perfectamente razonable desde el que mirar en un recorrido
        /// virtual: se ve la sala que se deja y la que se entra.
        /// </summary>
        private static List<Punto> RecogerPuntos()
        {
            var lista = new List<Punto>();
            foreach (var meta in UnityEngine.Object.FindObjectsByType<IfcMetadata>(FindObjectsSortMode.None))
            {
                if (meta == null) continue;

                bool esEsfera = meta.ifcType == SceneModelIndex.NavPointIfcType &&
                                !string.IsNullOrEmpty(meta.ifcName) &&
                                meta.ifcName.StartsWith(SceneModelIndex.NavPointPrefix);

                // Solo IfcDoor, y ademas se descarta explicitamente cualquier definicion de
                // catalogo. La comprobacion de tipo exacto ya excluye IfcDoorStyle e IfcDoorType,
                // pero se deja la guarda para que el criterio quede en un unico sitio y no se
                // desincronice si en el futuro se admiten mas tipos como nodo.
                bool esPuerta = UsarPuertasComoNodos && meta.ifcType == "IfcDoor" &&
                                !SceneModelIndex.EsDefinicionDeTipo(meta.ifcType);

                if (!esEsfera && !esPuerta) continue;

                lista.Add(new Punto
                {
                    Meta = meta,
                    Pos = PosicionDe(meta),
                    Sala = meta.GetValue("Otros", "LOC_Localizacion4"),
                    EsPuerta = esPuerta
                });
            }

            // Orden estable por GlobalId: así dos generaciones sobre el mismo modelo producen
            // exactamente el mismo asset y el diff en git es vacío en vez de una reordenación.
            return lista.OrderBy(p => p.Meta.globalId, StringComparer.Ordinal).ToList();
        }

        private static float DistHorizontal(Vector3 a, Vector3 b)
        {
            a.y = 0f; b.y = 0f;
            return Vector3.Distance(a, b);
        }

        /// <summary>
        /// Coste del trayecto entre dos puntos: distancia horizontal más las penalizaciones de
        /// lo que atraviesa. Es la métrica que alimenta todo el algoritmo, en lugar de la
        /// distancia pura.
        ///
        /// El trazado se hace a la altura de los ojos y no a la del suelo, para que el rayo
        /// atraviese la hoja de las puertas y los muros a media altura en lugar de colarse por
        /// debajo del marco o por encima de un zócalo.
        /// </summary>
        private static float Coste(Punto a, Punto b, out int pasos, out int vidrios,
                                   out int muros, out int otros)
        {
            pasos = 0; vidrios = 0; muros = 0; otros = 0;

            Vector3 origen = a.Pos;
            Vector3 destino = b.Pos;
            Vector3 dir = destino - origen;
            float longitud = dir.magnitude;

            float penalizacion = 0f;
            if (longitud > 0.001f)
            {
                var impactos = Physics.RaycastAll(origen, dir.normalized, longitud);
                var yaContados = new HashSet<GameObject>();

                foreach (var h in impactos)
                {
                    // Un mismo elemento puede aparecer varias veces (entrada y salida de la
                    // malla, o troceado en varias submallas por material). Se cuenta una vez.
                    var meta = h.collider.GetComponentInParent<IfcMetadata>();
                    var clave = meta != null ? meta.gameObject : h.collider.gameObject;
                    if (!yaContados.Add(clave)) continue;

                    // Los propios puntos de navegación no son obstáculos.
                    if (meta != null && meta.ifcType == SceneModelIndex.NavPointIfcType) continue;

                    string tipo = meta != null ? meta.ifcType : null;
                    if (EsDeTipo(tipo, TiposDePaso))             { pasos++;   penalizacion += PenalizacionPaso; }
                    else if (EsDeTipo(tipo, TiposTransparentes)) { vidrios++; penalizacion += PenalizacionVidrio; }
                    else if (EsDeTipo(tipo, TiposDeCerramiento)) { muros++;   penalizacion += PenalizacionMuro; }
                    else                                          { otros++;   penalizacion += PenalizacionOtro; }
                }
            }

            return DistHorizontal(a.Pos, b.Pos) + penalizacion;
        }

        private static bool EsDeTipo(string ifcType, string[] familia)
        {
            if (string.IsNullOrEmpty(ifcType)) return false;
            foreach (var t in familia)
                if (ifcType == t || ifcType.StartsWith(t)) return true;
            return false;
        }

        // Nota sobre la altura del trazado, que costo un diagnostico erroneo:
        //
        // El rayo se traza directamente entre las posiciones de los nodos, sin sumarles ninguna
        // altura. Una version anterior anadia 1,2 m "para que el rayo atravesara la hoja de las
        // puertas en lugar de colarse bajo el marco", razonamiento que era correcto en abstracto
        // pero partia de una premisa falsa: que los nodos estuvieran a ras de suelo.
        //
        // No lo estan. Los puntos "Esfera..." del modelo ya vienen a 1,55 m, la altura de la
        // vista, y los nodos de puerta se situan en el centro del volumen de la hoja, en torno a
        // 1,05 m. Sumarles 1,2 m elevaba el trazado a 2,75 m y 2,29 m respectivamente; como una
        // puerta mide unos 2,1 m, el rayo pasaba POR ENCIMA DEL DINTEL y atravesaba el macizo del
        // muro. El resultado era que ninguna puerta llegaba a contabilizarse como paso y todas
        // las aristas entre estancias contiguas aparecian como atravesando fabrica.
        //
        // Entre 1,05 y 1,55 m el rayo discurre por el centro del hueco de paso, que es justo
        // donde debe ir.

        /// <summary>
        /// Los volúmenes de colisión los añade <c>ColliderBootstrapper</c> al arrancar el juego,
        /// así que en modo edición el modelo no los tiene y las comprobaciones no detectarían
        /// nada. Esta función los crea temporalmente y devuelve los que ha añadido para poder
        /// retirarlos después y dejar la escena como estaba.
        ///
        /// Se omiten los mismos elementos que en ejecución: volúmenes de espacio, definiciones de
        /// tipo del catálogo y puntos de navegación. Incluirlos falsearía el resultado, ya que
        /// los prismas de estancia envuelven habitaciones enteras y toda arista los atravesaría.
        /// </summary>
        private static List<Collider> AsegurarColliders()
        {
            var index = SceneModelIndex.Build();
            var excluidos = new HashSet<GameObject>();

            foreach (var lista in new[] { index.Spaces, index.TypeDefinitions, index.NavPoints })
                foreach (var meta in lista)
                {
                    if (meta == null) continue;
                    foreach (var t in meta.GetComponentsInChildren<Transform>(true))
                        excluidos.Add(t.gameObject);
                }

            var creados = new List<Collider>();
            foreach (var mf in UnityEngine.Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
            {
                var go = mf.gameObject;
                if (mf.sharedMesh == null) continue;
                if (excluidos.Contains(go)) continue;
                if (go.GetComponent<Collider>() != null) continue;

                var col = go.AddComponent<MeshCollider>();
                col.sharedMesh = mf.sharedMesh;
                col.convex = false;
                creados.Add(col);
            }

            // Las consultas de física usan las posiciones sincronizadas, no las del Transform.
            Physics.SyncTransforms();
            return creados;
        }

        private static void RetirarColliders(List<Collider> creados)
        {
            foreach (var c in creados)
                if (c != null) UnityEngine.Object.DestroyImmediate(c);
        }

        private static bool MismaPlanta(Punto a, Punto b)
        {
            return Mathf.Abs(a.Pos.y - b.Pos.y) <= ToleranciaVertical;
        }

        /// <summary>
        /// Grafo de vecindad relativa. Coste O(n^3), que con las decenas de puntos de un edificio
        /// como este es instantáneo; no merece la pena complicarlo con estructuras espaciales.
        /// </summary>
        private static HashSet<(int, int)> ConstruirRNG(List<Punto> p, float[,] coste)
        {
            var aristas = new HashSet<(int, int)>();
            int n = p.Count;

            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if (!MismaPlanta(p[i], p[j])) continue;
                    if (LongitudMaximaArista > 0f &&
                        DistHorizontal(p[i].Pos, p[j].Pos) > LongitudMaximaArista) continue;

                    float cij = coste[i, j];

                    bool bloqueada = false;
                    for (int k = 0; k < n && !bloqueada; k++)
                    {
                        if (k == i || k == j) continue;
                        if (!MismaPlanta(p[i], p[k]) || !MismaPlanta(p[j], p[k])) continue;

                        // Existe un punto intermedio al que llegar es mas barato desde ambos
                        // extremos que unirlos directamente. Al ser el coste el que incorpora las
                        // penalizaciones, esto descarta a la vez los enlaces redundantes por
                        // geometria y los que atraviesan un muro pudiendo rodearlo por la puerta.
                        if (Mathf.Max(coste[i, k], coste[j, k]) < cij) bloqueada = true;
                    }

                    if (!bloqueada) aristas.Add((i, j));
                }
            }

            return aristas;
        }

        /// <summary>
        /// Matriz de costes entre todos los pares, con el recuento de lo que atraviesa cada
        /// trayecto. Se calcula una sola vez porque el RNG consulta cada par muchas veces y las
        /// consultas de fisica son con diferencia lo mas caro del proceso.
        /// </summary>
        private static float[,] CalcularCostes(List<Punto> p, out int[,] murosCruzados,
                                               out int[,] pasosCruzados, out int[,] vidriosCruzados)
        {
            int n = p.Count;
            var coste = new float[n, n];
            murosCruzados = new int[n, n];
            pasosCruzados = new int[n, n];
            vidriosCruzados = new int[n, n];

            for (int i = 0; i < n; i++)
            {
                EditorUtility.DisplayProgressBar("Grafo de navegacion",
                    $"Analizando obstaculos ({i + 1}/{n})...", (i + 1) / (float)n);

                for (int j = i + 1; j < n; j++)
                {
                    float c = Coste(p[i], p[j], out int pasos, out int vidrios, out int muros, out int _);
                    coste[i, j] = coste[j, i] = c;
                    murosCruzados[i, j] = murosCruzados[j, i] = muros;
                    pasosCruzados[i, j] = pasosCruzados[j, i] = pasos;
                    vidriosCruzados[i, j] = vidriosCruzados[j, i] = vidrios;
                }
            }

            EditorUtility.ClearProgressBar();
            return coste;
        }

        /// <summary>
        /// Une las componentes que hayan quedado aisladas conectando, en cada paso, el par de
        /// puntos más próximo entre dos componentes distintas. Devuelve cuántas aristas se han
        /// añadido.
        ///
        /// Estas uniones ignoran el límite de longitud de arista y el filtro de planta: es
        /// preferible una arista larga, o incluso una que cambie de nivel, a dejar una zona del
        /// edificio a la que no se pueda llegar de ninguna manera.
        /// </summary>
        private static int GarantizarConectividad(List<Punto> p, HashSet<(int, int)> aristas,
                                                  float[,] coste)
        {
            int n = p.Count;
            var padre = new int[n];
            for (int i = 0; i < n; i++) padre[i] = i;

            int Raiz(int x) { while (padre[x] != x) { padre[x] = padre[padre[x]]; x = padre[x]; } return x; }
            void Unir(int a, int b) { int ra = Raiz(a), rb = Raiz(b); if (ra != rb) padre[rb] = ra; }

            foreach (var (a, b) in aristas) Unir(a, b);

            int añadidas = 0;
            while (true)
            {
                var raices = new HashSet<int>();
                for (int i = 0; i < n; i++) raices.Add(Raiz(i));
                if (raices.Count <= 1) break;

                float mejor = float.MaxValue;
                int mi = -1, mj = -1;
                for (int i = 0; i < n; i++)
                {
                    for (int j = i + 1; j < n; j++)
                    {
                        if (Raiz(i) == Raiz(j)) continue;
                        // Tambien aqui se usa el coste y no la distancia: si hay que unir dos
                        // zonas a la fuerza, que sea por donde menos estorbo haya.
                        float d = coste[i, j];
                        if (d < mejor) { mejor = d; mi = i; mj = j; }
                    }
                }

                if (mi < 0) break; // no debería ocurrir, pero evita un bucle infinito
                aristas.Add((mi, mj));
                Unir(mi, mj);
                añadidas++;
                Debug.LogWarning($"[NavGraph] Componente aislada detectada: se une '{p[mi].Meta.ifcName}' con " +
                                 $"'{p[mj].Meta.ifcName}' (coste {mejor:F1}) para que no queden zonas " +
                                 "inalcanzables. Conviene revisar esa arista en el asset.");
            }

            return añadidas;
        }

        private static NavGraphAsset CrearAsset(List<Punto> p, HashSet<(int, int)> aristas)
        {
            if (!Directory.Exists(RutaCarpeta)) Directory.CreateDirectory(RutaCarpeta);

            // Se reutiliza el asset existente si lo hay, para no perder su GUID: si se borrara y
            // recreara, cualquier referencia guardada a él quedaría rota.
            var asset = AssetDatabase.LoadAssetAtPath<NavGraphAsset>(RutaAsset);
            bool esNuevo = asset == null;
            if (esNuevo) asset = ScriptableObject.CreateInstance<NavGraphAsset>();

            asset.Nodos = p.Select(x => new NavGraphAsset.Nodo
            {
                GlobalId = x.Meta.globalId,
                Nombre = x.Meta.ifcName,
                Sala = x.Sala,
                Posicion = x.Pos,
                Vecinos = new List<int>()
            }).ToList();

            foreach (var (a, b) in aristas)
            {
                asset.Nodos[a].Vecinos.Add(b);
                asset.Nodos[b].Vecinos.Add(a);
            }
            foreach (var nodo in asset.Nodos) nodo.Vecinos.Sort();

            asset.GeneradoEl = DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            asset.EscenaOrigen = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            asset.Parametros = $"RNG sobre coste; tolerancia vertical {ToleranciaVertical} m; " +
                               $"longitud máxima {LongitudMaximaArista} m; penalizaciones: " +
                               $"paso {PenalizacionPaso} m, vidrio {PenalizacionVidrio} m, " +
                               $"fabrica {PenalizacionMuro} m, otros {PenalizacionOtro} m; " +
                               $"puertas como nodos: {UsarPuertasComoNodos}";

            if (esNuevo) AssetDatabase.CreateAsset(asset, RutaAsset);
            else EditorUtility.SetDirty(asset);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return asset;
        }
    }
}
