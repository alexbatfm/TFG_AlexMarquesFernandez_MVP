using System.IO;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;
using IFCImporter;

/// <summary>
/// Ventana del Editor en Unity: Tools > IFC > Import Metadata
///
/// Lee el archivo metadata.json (generado por extract_ifc_metadata.py)
/// y asigna los metadatos a los GameObjects de la escena mediante el componente IfcMetadata.
///
/// INTEGRIDAD DE IDENTIFICADORES (corregido 2026-08-13)
///
/// La versión anterior asignaba GlobalIds duplicados: sobre el modelo de referencia, 71 nodos
/// llegaban a la desambiguación por ruta, TODOS empataban (la puntuación comparaba el nombre
/// crudo del GameObject con los segmentos de la ruta IFC, que nunca coinciden) y se tomaba el
/// primer candidato del JSON. Resultado medido: 14 GlobalId compartidos por 76 objetos, con un
/// caso de 37 objetos sobre un mismo identificador. Peor aún: esos candidatos eran entradas de
/// DEFINICIÓN DE TIPO (IfcMemberType, IfcColumnType, IfcDoorStyle...), de modo que 37 montantes,
/// 11 paneles y 5 de los 6 pilares reales quedaban clasificados como definiciones de catálogo
/// y por tanto ocultos y sin colisionador.
///
/// La causa raíz: Blender trunca los nombres de objeto a 63 caracteres y elimina el sufijo
/// «:tag» que distingue a las instancias, de modo que el nombre limpio del nodo coincide
/// EXACTAMENTE con el nombre de su definición de tipo y solo por prefijo con el de su instancia.
///
/// La corrección, en tres reglas (validadas en frío sobre el GLB y el JSON de referencia:
/// 0 GlobalId compartidos entre elementos, 533 objetos asignados, 113 oclusores, 32 sensores,
/// 36 esferas y 18 puertas — todas las cifras canónicas):
///
///  1. El prefijo «IfcClase/» del nombre del nodo (lo pone el pipeline de Bonsai) restringe los
///     candidatos a entradas de ESE tipo exacto: un nodo IfcMember ya no puede llevarse una
///     entrada IfcMemberType, ni al revés.
///  2. Si el filtro deja la lista vacía (nombre truncado), se buscan las entradas del tipo cuyo
///     nombre EMPIEZA por el nombre limpio del nodo: así cada montante recupera su instancia
///     «...:tag» aunque el tag se perdiera al exportar.
///  3. La asignación es inyectiva: un GlobalId asignado se consume y no vuelve a repartirse.
///     Dentro de un grupo de gemelos con el mismo nombre, los nodos (ordenados por nombre) se
///     emparejan con las entradas (ordenadas por tag numérico). El emparejamiento es
///     determinista; entre gemelos geométricamente idénticos no es verificable cuál es cuál
///     sin coordenadas en el JSON, y se asume ese margen: los psets de los gemelos solo
///     difieren en el tag. Los CONTENEDORES espaciales (IfcProject, IfcSite, IfcBuilding,
///     IfcBuildingStorey) quedan exentos: el exportador genera dos nodos por planta y ambos
///     deben referirse a la misma entrada.
///
/// Al terminar, el importador AUDITA el resultado y escribe cuántos GlobalId quedan compartidos
/// (objetivo: 0 fuera de contenedores). Esa línea es la verificación del contrato de identidad
/// sin necesidad de compilar nada.
/// </summary>
public class IfcMetadataImporterWindow : EditorWindow
{
    private string jsonPath = "";
    private JObject jsonData = null;
    private Dictionary<string, List<string>> nameIndex = new Dictionary<string, List<string>>();

    /// <summary>
    /// Con la casilla activada se ignoran los GlobalId ya presentes en los componentes y se
    /// recalcula todo. Necesario UNA VEZ tras corregir el algoritmo: la reimportación normal
    /// conserva lo ya asignado (para respetar retoques manuales), lo que perpetuaría las
    /// asignaciones duplicadas antiguas.
    /// </summary>
    private bool reasignarTodos = false;

    /// <summary>Tipos que pueden compartir GlobalId entre varios nodos del GLB: son contenedores
    /// jerárquicos, no elementos, y el exportador produce más de un nodo por contenedor.</summary>
    private static readonly HashSet<string> TiposContenedor = new HashSet<string>
    {
        "IfcProject", "IfcSite", "IfcBuilding", "IfcBuildingStorey"
    };

    [MenuItem("Tools/IFC/Import Metadata")]
    public static void ShowWindow()
    {
        GetWindow<IfcMetadataImporterWindow>("Import IFC Metadata");
    }

    private void OnGUI()
    {
        EditorGUILayout.LabelField("Importador de Metadatos IFC", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        GameObject root = Selection.activeGameObject;
        EditorGUILayout.LabelField("Raíz seleccionada:", root != null ? root.name : "(selecciona un objeto en la jerarquía)");

        EditorGUILayout.Space();
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("metadata.json:", GUILayout.Width(90));
        EditorGUILayout.LabelField(string.IsNullOrEmpty(jsonPath) ? "(sin seleccionar)" : Path.GetFileName(jsonPath));
        if (GUILayout.Button("...", GUILayout.Width(30)))
        {
            string picked = EditorUtility.OpenFilePanel("Selecciona metadata.json", "", "json");
            if (!string.IsNullOrEmpty(picked))
            {
                jsonPath = picked;
                LoadJsonData();
            }
        }
        EditorGUILayout.EndHorizontal();

        if (jsonData != null)
        {
            EditorGUILayout.Space();
            EditorGUILayout.HelpBox($"JSON cargado: {jsonData.Count} elementos en el modelo", MessageType.Info);
        }

        EditorGUILayout.Space();
        reasignarTodos = EditorGUILayout.ToggleLeft(
            new GUIContent("Recalcular todos los GlobalId (ignora los ya asignados)",
                "Actívalo una vez tras la corrección del 2026-08-13 para sanear las " +
                "asignaciones duplicadas antiguas. Desactivado, los componentes que ya " +
                "tienen GlobalId se conservan tal cual."),
            reasignarTodos);

        EditorGUILayout.Space();
        GUI.enabled = root != null && jsonData != null;
        if (GUILayout.Button("Importar metadatos", GUILayout.Height(40)))
        {
            Import(root, jsonData, nameIndex, reasignarTodos);
        }
        GUI.enabled = true;
    }

    private void LoadJsonData()
    {
        try
        {
            jsonData = JObject.Parse(File.ReadAllText(jsonPath));

            // Construye el índice rápido O(1): Nombre -> Lista de GlobalIds
            nameIndex.Clear();
            foreach (var kvp in jsonData)
            {
                var entry = kvp.Value as JObject;
                if (entry == null) continue;

                string name = (string)entry["name"];
                if (string.IsNullOrEmpty(name)) continue;

                if (!nameIndex.ContainsKey(name))
                    nameIndex[name] = new List<string>();

                nameIndex[name].Add(kvp.Key);
            }
        }
        catch (System.Exception ex)
        {
            EditorUtility.DisplayDialog("Error", $"No se pudo cargar el JSON:\n{ex.Message}", "OK");
            jsonData = null;
        }
    }

    private static void Import(GameObject root, JObject json, Dictionary<string, List<string>> index,
                               bool reasignarTodos)
    {
        int totalObjects = 0;
        var unmatchedObjects = new List<string>();
        var sinCandidatoLibre = new List<string>();

        // GlobalIds ya repartidos en esta importación (o conservados de una anterior): la
        // asignación es inyectiva salvo para los contenedores.
        var consumidos = new HashSet<string>();
        var asignaciones = new Dictionary<Transform, string>();

        // Pendientes con varios candidatos: se resuelven en una segunda pasada, agrupados,
        // para que los gemelos de un mismo nombre se repartan las entradas sin pisarse.
        var ambiguos = new List<(Transform t, List<string> candidatos)>();

        var transforms = root.GetComponentsInChildren<Transform>(true);

        // --- Pasada 1: conservados, únicos y recogida de ambiguos ------------------------
        foreach (Transform t in transforms)
        {
            totalObjects++;

            if (!reasignarTodos)
            {
                var existing = t.GetComponent<IfcMetadata>();
                if (existing != null && !string.IsNullOrEmpty(existing.globalId))
                {
                    asignaciones[t] = existing.globalId;
                    if (!EsContenedor(json, existing.globalId)) consumidos.Add(existing.globalId);
                    continue;
                }
            }

            var candidatos = ResolverCandidatos(t.name, json, index);
            if (candidatos == null || candidatos.Count == 0)
            {
                unmatchedObjects.Add($"• '{t.name}' (Limpio: '{CleanName(t.name)}') -> Ruta: {BuildGameObjectPath(t)}");
                continue;
            }

            if (candidatos.Count == 1)
            {
                Asignar(t, candidatos[0], json, asignaciones, consumidos);
                continue;
            }

            ambiguos.Add((t, candidatos));
        }

        // --- Pasada 2: grupos de gemelos, reparto inyectivo y determinista ----------------
        foreach (var grupo in ambiguos.GroupBy(a => string.Join("|", a.candidatos)))
        {
            var nodos = grupo.OrderBy(a => a.t.name, System.StringComparer.Ordinal).ToList();
            var candidatosOrdenados = grupo.First().candidatos
                .OrderBy(g => ClaveDeTag(json, g).clave)
                .ThenBy(g => ClaveDeTag(json, g).gid, System.StringComparer.Ordinal)
                .ToList();

            foreach (var (t, _) in nodos)
            {
                // La puntuación por ruta se conserva como criterio previo: hoy no discrimina
                // (empata a cero en el modelo de referencia), pero si el pipeline mejorase las
                // rutas volvería a ser el criterio más informado.
                string porRuta = MejorPorRutaSiDiscrimina(candidatosOrdenados, BuildGameObjectPath(t),
                                                          json, consumidos);
                string elegido = porRuta ?? candidatosOrdenados.FirstOrDefault(g =>
                    !consumidos.Contains(g) || EsContenedor(json, g));

                if (elegido == null)
                {
                    sinCandidatoLibre.Add($"• '{t.name}': las {candidatosOrdenados.Count} entradas " +
                                          "candidatas ya están asignadas a otros objetos.");
                    continue;
                }

                Asignar(t, elegido, json, asignaciones, consumidos);
            }
        }

        // --- Volcado a componentes ---------------------------------------------------------
        int matched = 0;
        foreach (var kvp in asignaciones)
        {
            AplicarEntrada(kvp.Key, kvp.Value, json);
            matched++;
        }

        Debug.Log($"✓ Metadatos inyectados correctamente en {matched} de {totalObjects} objetos.");

        if (unmatchedObjects.Count > 0)
        {
            Debug.LogWarning($"⚠️ ELEMENTOS NO VINCULADOS ({unmatchedObjects.Count}):\n" +
                             string.Join("\n", unmatchedObjects));
        }
        if (sinCandidatoLibre.Count > 0)
        {
            Debug.LogWarning($"⚠️ MÁS OBJETOS QUE ENTRADAS ({sinCandidatoLibre.Count}):\n" +
                             string.Join("\n", sinCandidatoLibre));
        }

        AuditarIntegridad(root, json);

        EditorUtility.SetDirty(root);
    }

    /// <summary>
    /// Auditoría del contrato de identidad: ningún GlobalId debe quedar compartido entre
    /// elementos (los contenedores espaciales están exentos). Es la línea que verifica la
    /// corrección del 2026-08-13 sin salir del Editor.
    /// </summary>
    private static void AuditarIntegridad(GameObject root, JObject json)
    {
        var porGid = new Dictionary<string, List<string>>();
        foreach (var comp in root.GetComponentsInChildren<IfcMetadata>(true))
        {
            if (string.IsNullOrEmpty(comp.globalId)) continue;
            if (!porGid.TryGetValue(comp.globalId, out var lista))
                porGid[comp.globalId] = lista = new List<string>();
            lista.Add(comp.gameObject.name);
        }

        var duplicados = porGid.Where(kv => kv.Value.Count > 1 && !EsContenedor(json, kv.Key))
                               .OrderByDescending(kv => kv.Value.Count)
                               .ToList();

        if (duplicados.Count == 0)
        {
            Debug.Log("✓ Integridad de GlobalId: ningún identificador compartido entre elementos. " +
                      "(Los contenedores de proyecto/planta pueden compartir el suyo: son dos " +
                      "nodos del exportador para el mismo contenedor.)");
            return;
        }

        int objetos = duplicados.Sum(kv => kv.Value.Count);
        var detalle = string.Join("\n", duplicados.Take(10).Select(kv =>
            $"   {kv.Key} x{kv.Value.Count} ('{kv.Value[0]}'...)"));
        Debug.LogWarning($"⚠️ INTEGRIDAD DE GlobalId: {duplicados.Count} identificadores compartidos " +
                         $"por {objetos} objetos. Si acabas de reimportar con el algoritmo corregido, " +
                         "activa la casilla «Recalcular todos los GlobalId» y reimporta: las " +
                         "asignaciones antiguas se conservan por defecto.\n" + detalle);
    }

    // ------------------------------------------------------------------ resolución ----------

    /// <summary>
    /// Candidatos del JSON para un nombre de nodo del GLB, aplicando el filtro por clase IFC del
    /// prefijo y la búsqueda por prefijo de nombre para los truncados. Ver la nota de la clase.
    /// </summary>
    private static List<string> ResolverCandidatos(string rawName, JObject json,
                                                   Dictionary<string, List<string>> index)
    {
        string cleanName = CleanName(rawName);
        string clase = PrefijoIfc(rawName);

        List<string> candidatos = null;
        if (!index.TryGetValue(rawName, out candidatos))
            index.TryGetValue(cleanName, out candidatos);

        if (clase == null)
            return candidatos != null ? new List<string>(candidatos) : null;

        // Regla 1: el prefijo del nodo fija el tipo de la entrada.
        List<string> filtrados = null;
        if (candidatos != null)
            filtrados = candidatos.Where(g => TipoDe(json, g) == clase).ToList();

        if (filtrados != null && filtrados.Count > 0)
            return filtrados;

        // Regla 2: nombre truncado por Blender -> entradas del tipo cuyo nombre empieza por el
        // nombre limpio. El mínimo de 8 caracteres evita que un nombre residual arrastre medio
        // catálogo.
        if (cleanName != null && cleanName.Length >= 8)
        {
            var porPrefijo = new List<string>();
            foreach (var kvp in json)
            {
                var entry = kvp.Value as JObject;
                if (entry == null) continue;
                if ((string)entry["type"] != clase) continue;
                string nombre = (string)entry["name"];
                if (!string.IsNullOrEmpty(nombre) && nombre.StartsWith(cleanName))
                    porPrefijo.Add(kvp.Key);
            }
            if (porPrefijo.Count > 0) return porPrefijo;
        }

        return null;
    }

    private static void Asignar(Transform t, string globalId, JObject json,
                                Dictionary<Transform, string> asignaciones, HashSet<string> consumidos)
    {
        asignaciones[t] = globalId;
        if (!EsContenedor(json, globalId)) consumidos.Add(globalId);
    }

    private static void AplicarEntrada(Transform t, string globalId, JObject json)
    {
        var entry = (JObject)json[globalId];
        if (entry == null) return;

        var comp = t.gameObject.GetComponent<IfcMetadata>();
        if (comp == null)
            comp = t.gameObject.AddComponent<IfcMetadata>();

        comp.globalId = globalId;
        comp.ifcType = (string)entry["type"];
        comp.ifcName = (string)entry["name"];
        comp.ifcTag = (string)entry["tag"];
        comp.hierarchyPath = (string)entry["path"];
        comp.propertySets.Clear();

        var psets = entry["psets"] as JObject;
        if (psets != null)
        {
            foreach (var pset in psets)
            {
                var group = new PsetGroup { psetName = pset.Key };
                var props = pset.Value as JObject;
                if (props != null)
                {
                    foreach (var prop in props)
                    {
                        string val = (prop.Value == null || prop.Value.Type == JTokenType.Null)
                            ? null
                            : prop.Value.ToString();

                        group.properties.Add(new KeyValueEntry { key = prop.Key, value = val });
                    }
                }
                comp.propertySets.Add(group);
            }
        }
    }

    private static string TipoDe(JObject json, string globalId)
    {
        var entry = json[globalId] as JObject;
        return entry != null ? (string)entry["type"] : null;
    }

    private static bool EsContenedor(JObject json, string globalId)
    {
        var tipo = TipoDe(json, globalId);
        return tipo != null && TiposContenedor.Contains(tipo);
    }

    /// <summary>Prefijo «IfcClase» del nombre de nodo que genera el pipeline de Bonsai, o null.</summary>
    private static string PrefijoIfc(string rawName)
    {
        if (string.IsNullOrEmpty(rawName)) return null;
        int slash = rawName.IndexOf('/');
        if (slash > 3 && rawName.StartsWith("Ifc")) return rawName.Substring(0, slash);
        return null;
    }

    /// <summary>Orden determinista de las entradas gemelas: por tag numérico y, sin tag, por
    /// GlobalId. Es el mismo orden en cada reimportación.</summary>
    private static (long clave, string gid) ClaveDeTag(JObject json, string globalId)
    {
        var entry = json[globalId] as JObject;
        string tag = entry != null ? (string)entry["tag"] : null;
        if (!string.IsNullOrEmpty(tag) && long.TryParse(tag, out long n)) return (n, globalId);
        return (long.MaxValue, globalId);
    }

    /// <summary>
    /// La puntuación por ruta original, mantenida como criterio previo al reparto por orden:
    /// solo decide si un candidato LIBRE puntúa estrictamente mejor que todos los demás.
    /// </summary>
    private static string MejorPorRutaSiDiscrimina(List<string> candidatos, string goPath,
                                                   JObject json, HashSet<string> consumidos)
    {
        string mejor = null;
        int mejorPuntuacion = -1;
        bool empate = false;

        foreach (string gid in candidatos)
        {
            if (consumidos.Contains(gid) && !EsContenedor(json, gid)) continue;
            var entry = (JObject)json[gid];
            string ifcPath = (string)entry["path"] ?? "";
            int puntuacion = CountMatchingPathSegments(goPath, ifcPath);
            if (puntuacion > mejorPuntuacion)
            {
                mejorPuntuacion = puntuacion;
                mejor = gid;
                empate = false;
            }
            else if (puntuacion == mejorPuntuacion)
            {
                empate = true;
            }
        }

        // Solo discrimina una puntuación estrictamente mejor y distinta de cero; un empate
        // (que hoy es lo habitual) delega en el reparto determinista por tag.
        return (!empate && mejorPuntuacion > 0) ? mejor : null;
    }

    /// <summary>
    /// Limpia el nombre del GameObject en Unity:
    ///   - Elimina prefijos de clase IFC de Blender (ej. "IfcProject/0001" -> "0001")
    ///   - Elimina sufijos numéricos de duplicados (ej. "Muro.001" -> "Muro")
    /// </summary>
    private static string CleanName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        // 1. Elimina el prefijo "IfcClass/" generado por Blender/Bonsai
        int slashIdx = name.LastIndexOf('/');
        if (slashIdx >= 0 && slashIdx < name.Length - 1)
        {
            name = name.Substring(slashIdx + 1);
        }

        // 2. Elimina sufijos estilo .001, .002
        int dotIdx = name.LastIndexOf('.');
        if (dotIdx > 0 && dotIdx < name.Length - 1)
        {
            string suffix = name.Substring(dotIdx + 1);
            if (int.TryParse(suffix, out _))
            {
                name = name.Substring(0, dotIdx);
            }
        }

        return name.Trim();
    }

    private static string BuildGameObjectPath(Transform t)
    {
        var path = new List<string>();
        var current = t;
        while (current != null)
        {
            path.Insert(0, current.name);
            current = current.parent;
        }
        return string.Join("/", path);
    }

    private static int CountMatchingPathSegments(string goPath, string ifcPath)
    {
        if (string.IsNullOrEmpty(ifcPath)) return 0;

        var goSegments = goPath.Split('/');
        var ifcSegments = ifcPath.Split('/');

        int score = 0;
        int goIdx = goSegments.Length - 1;
        int ifcIdx = ifcSegments.Length - 1;

        while (goIdx >= 0 && ifcIdx >= 0)
        {
            if (goSegments[goIdx] == ifcSegments[ifcIdx])
            {
                score++;
                goIdx--;
                ifcIdx--;
            }
            else
            {
                break;
            }
        }

        return score;
    }
}
