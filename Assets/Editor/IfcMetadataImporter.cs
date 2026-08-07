using System.IO;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;
using IFCImporter;

/// <summary>
/// Ventana del Editor en Unity: Tools > IFC > Import Metadata
/// 
/// Lee el archivo metadata.json (generado por extract_ifc_metadata.py)
/// y asigna los metadatos a los GameObjects de la escena mediante el componente IfcMetadata.
/// </summary>
public class IfcMetadataImporterWindow : EditorWindow
{
    private string jsonPath = "";
    private JObject jsonData = null;
    private Dictionary<string, List<string>> nameIndex = new Dictionary<string, List<string>>();

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
        GUI.enabled = root != null && jsonData != null;
        if (GUILayout.Button("Importar metadatos", GUILayout.Height(40)))
        {
            Import(root, jsonData, nameIndex);
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
    private static void Import(GameObject root, JObject json, Dictionary<string, List<string>> index)
    {
        int matched = 0;
        int totalObjects = 0;
        List<string> unmatchedObjects = new List<string>();

        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
        {
            totalObjects++;

            string globalId = FindGlobalIdFor(t, json, index);
            if (globalId == null)
            {
                // Guarda los objetos que no han encontrado coincidencia
                unmatchedObjects.Add($"• '{t.name}' (Limpio: '{CleanName(t.name)}') -> Ruta: {BuildGameObjectPath(t)}");
                continue;
            }

            var entry = (JObject)json[globalId];
            var comp = t.gameObject.GetComponent<IfcMetadata>();
            if (comp == null)
                comp = t.gameObject.AddComponent<IfcMetadata>();

            // Asignación de datos
            comp.globalId = globalId;
            comp.ifcType = (string)entry["type"];
            comp.ifcName = (string)entry["name"];
            comp.ifcTag = (string)entry["tag"]; 
            comp.hierarchyPath = (string)entry["path"];
            comp.propertySets.Clear();

            // Asignación de Psets
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

            matched++;
        }

        Debug.Log($"✓ Metadatos inyectados correctamente en {matched} de {totalObjects} objetos.");

        // MUESTRA EN CONSOLA LOS OBJETOS EXACTOS QUE NO COINCIDIERON
        if (unmatchedObjects.Count > 0)
        {
            Debug.LogWarning($"⚠️ ELEMENTOS NO VINCULADOS ({unmatchedObjects.Count}):\n" +
                             string.Join("\n", unmatchedObjects));
        }

        EditorUtility.SetDirty(root);
    }
    private static string FindGlobalIdFor(Transform t, JObject json, Dictionary<string, List<string>> index)
    {
        // 1. Reimportación: si ya tiene el componente con GlobalId, reutilizarlo directamente
        var existing = t.GetComponent<IfcMetadata>();
        if (existing != null && !string.IsNullOrEmpty(existing.globalId))
            return existing.globalId;

        string rawName = t.name;
        string cleanName = CleanName(rawName);

        // 2. Búsqueda por nombre original o por nombre limpiado
        List<string> candidates = null;
        if (!index.TryGetValue(rawName, out candidates))
        {
            index.TryGetValue(cleanName, out candidates);
        }

        if (candidates == null || candidates.Count == 0)
            return null;

        // 3. Coincidencia única
        if (candidates.Count == 1)
            return candidates[0];

        // 4. Múltiples objetos con el mismo nombre: desambiguar por ruta jerárquica
        string hierarchyPath = BuildGameObjectPath(t);
        return DisambiguateByPath(candidates, hierarchyPath, json);
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

    private static string DisambiguateByPath(List<string> candidates, string goPath, JObject json)
    {
        string best = candidates[0];
        int bestScore = -1;

        foreach (string globalId in candidates)
        {
            var entry = (JObject)json[globalId];
            string ifcPath = (string)entry["path"] ?? "";

            int score = CountMatchingPathSegments(goPath, ifcPath);
            if (score > bestScore)
            {
                bestScore = score;
                best = globalId;
            }
        }

        return best;
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