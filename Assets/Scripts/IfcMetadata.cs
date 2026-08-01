using System.Collections.Generic;
using UnityEngine;

/// <summary>
/// Contenedor de metadatos IFC (Psets/Qtos) para un GameObject concreto.
/// Se rellena mediante IfcMetadataImporter tras la importación FBX.
/// </summary>
public class IfcMetadata : MonoBehaviour
{
    public string globalId;
    public string ifcType;
    public string ifcName;
    public string tag;

    public List<PsetGroup> propertySets = new List<PsetGroup>();

    public string GetValue(string psetName, string propName)
    {
        var group = propertySets.Find(g => g.psetName == psetName);
        if (group == null) return null;
        var prop = group.properties.Find(p => p.key == propName);
        return prop?.value;
    }
}

[System.Serializable]
public class PsetGroup
{
    public string psetName;
    public List<KeyValueEntry> properties = new List<KeyValueEntry>();
}

[System.Serializable]
public class KeyValueEntry
{
    public string key;
    public string value;
}
