using System.IO;
using UnityEditor;
using UnityEngine;
using Newtonsoft.Json.Linq;

/// <summary>
/// Herramienta de editor: Tools > IFC > Import Metadata JSON
/// Selecciona primero el GameObject raíz del modelo importado (FBX) en la jerarquía,
/// luego ejecuta el menú y elige el metadata.json generado por extract_ifc_metadata.py.
///
/// Requiere el paquete "Newtonsoft Json" (com.unity.nuget.newtonsoft-json)
/// instalable desde Window > Package Manager > Add package by name.
///
/// Requisito de nombrado: los objetos deben llevar el GlobalId como sufijo,
/// separado por "__", p.ej. "Wall-Basic__2O2Fr$t4X7Zf8NOew3FLOH"
/// (ver script de renombrado sugerido en la respuesta de chat).
/// </summary>
public static class IfcMetadataImporter
{
    [MenuItem("Tools/IFC/Import Metadata JSON")]
    public static void ImportMetadata()
    {
        string jsonPath = EditorUtility.OpenFilePanel("Selecciona metadata.json", "", "json");
        if (string.IsNullOrEmpty(jsonPath)) return;

        GameObject root = Selection.activeGameObject;
        if (root == null)
        {
            Debug.LogError("Selecciona el GameObject raíz del modelo importado antes de ejecutar.");
            return;
        }

        var json = JObject.Parse(File.ReadAllText(jsonPath));
        int matched = 0;
        int totalObjects = 0;

        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
        {
            totalObjects++;
            string guid = ExtractGuid(t.name);
            if (guid == null || json[guid] == null) continue;

            var entry = (JObject)json[guid];
            var comp = t.gameObject.GetComponent<IfcMetadata>();
            if (comp == null) comp = t.gameObject.AddComponent<IfcMetadata>();

            comp.globalId = guid;
            comp.ifcType = (string)entry["type"];
            comp.ifcName = (string)entry["name"];
            comp.tag = (string)entry["tag"];
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
                            group.properties.Add(new KeyValueEntry
                            {
                                key = prop.Key,
                                value = prop.Value != null ? prop.Value.ToString() : null
                            });
                        }
                    }
                    comp.propertySets.Add(group);
                }
            }

            matched++;
        }

        Debug.Log($"Metadatos aplicados a {matched} objetos de {totalObjects} revisados en la jerarquía.");
        EditorUtility.SetDirty(root);
    }

    // Extrae el GlobalId (22 caracteres) del nombre "NombreOriginal__GUID"
    private static string ExtractGuid(string objectName)
    {
        int idx = objectName.LastIndexOf("__");
        if (idx < 0) return null;
        string candidate = objectName.Substring(idx + 2);

        // FBX puede añadir sufijos tipo " (1)" en duplicados; los recortamos
        int spaceIdx = candidate.IndexOf(' ');
        if (spaceIdx > 0) candidate = candidate.Substring(0, spaceIdx);

        return candidate.Length == 22 ? candidate : null;
    }
}
