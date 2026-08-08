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
    /// LA SOLUCIÓN: GRAFO DE VECINDAD RELATIVA (RNG)
    /// Se conectan dos puntos A y B solo si no existe un tercer punto C que esté más cerca de
    /// ambos que la distancia que separa A de B. Formalmente, si no hay ningún C tal que
    /// max(d(A,C), d(B,C)) &lt; d(A,B).
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

        [MenuItem("Tools/IFC/Generar grafo de navegación")]
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

            var aristas = ConstruirRNG(puntos);
            int aristasRNG = aristas.Count;
            int añadidasPorConectividad = GarantizarConectividad(puntos, aristas);

            var asset = CrearAsset(puntos, aristas);

            Debug.Log($"[NavGraph] Grafo generado: {puntos.Count} nodos, {asset.ContarAristas()} aristas " +
                      $"({aristasRNG} del RNG + {añadidasPorConectividad} añadidas para unir componentes aisladas). " +
                      $"Guardado en {RutaAsset}.");

            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);
        }

        private sealed class Punto
        {
            public IfcMetadata Meta;
            public Vector3 Pos;
            public string Sala;
        }

        private static List<Punto> RecogerPuntos()
        {
            var lista = new List<Punto>();
            foreach (var meta in UnityEngine.Object.FindObjectsByType<IfcMetadata>(FindObjectsSortMode.None))
            {
                if (meta == null || meta.ifcType != SceneModelIndex.NavPointIfcType) continue;
                if (string.IsNullOrEmpty(meta.ifcName) || !meta.ifcName.StartsWith(SceneModelIndex.NavPointPrefix)) continue;

                lista.Add(new Punto
                {
                    Meta = meta,
                    Pos = meta.transform.position,
                    Sala = meta.GetValue("Otros", "LOC_Localizacion4")
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

        private static bool MismaPlanta(Punto a, Punto b)
        {
            return Mathf.Abs(a.Pos.y - b.Pos.y) <= ToleranciaVertical;
        }

        /// <summary>
        /// Grafo de vecindad relativa. Coste O(n^3), que con las decenas de puntos de un edificio
        /// como este es instantáneo; no merece la pena complicarlo con estructuras espaciales.
        /// </summary>
        private static HashSet<(int, int)> ConstruirRNG(List<Punto> p)
        {
            var aristas = new HashSet<(int, int)>();
            int n = p.Count;

            for (int i = 0; i < n; i++)
            {
                for (int j = i + 1; j < n; j++)
                {
                    if (!MismaPlanta(p[i], p[j])) continue;

                    float dij = DistHorizontal(p[i].Pos, p[j].Pos);
                    if (LongitudMaximaArista > 0f && dij > LongitudMaximaArista) continue;

                    bool bloqueada = false;
                    for (int k = 0; k < n && !bloqueada; k++)
                    {
                        if (k == i || k == j) continue;
                        if (!MismaPlanta(p[i], p[k]) || !MismaPlanta(p[j], p[k])) continue;

                        // ¿Hay un punto intermedio más cercano a ambos extremos que ellos entre sí?
                        float dik = DistHorizontal(p[i].Pos, p[k].Pos);
                        float djk = DistHorizontal(p[j].Pos, p[k].Pos);
                        if (Mathf.Max(dik, djk) < dij) bloqueada = true;
                    }

                    if (!bloqueada) aristas.Add((i, j));
                }
            }

            return aristas;
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
        private static int GarantizarConectividad(List<Punto> p, HashSet<(int, int)> aristas)
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
                        float d = DistHorizontal(p[i].Pos, p[j].Pos);
                        if (d < mejor) { mejor = d; mi = i; mj = j; }
                    }
                }

                if (mi < 0) break; // no debería ocurrir, pero evita un bucle infinito
                aristas.Add((mi, mj));
                Unir(mi, mj);
                añadidas++;
                Debug.LogWarning($"[NavGraph] Componente aislada detectada: se une '{p[mi].Meta.ifcName}' con " +
                                 $"'{p[mj].Meta.ifcName}' ({mejor:F1} m) para que no queden zonas inalcanzables. " +
                                 "Conviene revisar esa arista en el asset por si conviene moverla a otro par.");
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
            asset.Parametros = $"RNG, distancia horizontal, tolerancia vertical {ToleranciaVertical} m, " +
                               $"longitud máxima de arista {LongitudMaximaArista} m";

            if (esNuevo) AssetDatabase.CreateAsset(asset, RutaAsset);
            else EditorUtility.SetDirty(asset);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return asset;
        }
    }
}
