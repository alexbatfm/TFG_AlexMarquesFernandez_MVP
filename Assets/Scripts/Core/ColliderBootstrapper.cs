using System.Collections.Generic;
using UnityEngine;

namespace DigitalTwin.Core
{
    /// <summary>
    /// El modelo se importa con glTFast, que no añade colliders. Sin colliders no hay forma
    /// de hacer raycast contra el modelo (ni para seleccionar elementos en la Fase 2, ni para
    /// comprobar oclusión de hotspots en la Fase 1). Esta clase añade un MeshCollider a todo
    /// objeto con malla que no tenga ya un Collider, una sola vez al arrancar.
    ///
    /// También mueve los puntos de navegación ("Esfera...") a una capa de física propia
    /// (ver capa "IFCNavPoint" en ProjectSettings/TagManager.asset) para que NO bloqueen los
    /// linecasts de oclusión entre puntos de navegación: son marcadores decorativos, no
    /// geometría real del edificio.
    /// </summary>
    public static class ColliderBootstrapper
    {
        public const string NavPointLayerName = "IFCNavPoint";

        public static int NavPointLayer { get; private set; } = -1;

        public static void Setup(SceneModelIndex index)
        {
            NavPointLayer = LayerMask.NameToLayer(NavPointLayerName);
            if (NavPointLayer < 0)
            {
                Debug.LogWarning($"[DigitalTwin] La capa de física '{NavPointLayerName}' no existe todavía en " +
                                  "Edit > Project Settings > Tags and Layers. Se ha intentado crear automáticamente " +
                                  "vía TagManager.asset; si este aviso persiste, añádela manualmente (cualquier " +
                                  "hueco libre de capa de usuario, 3 en adelante). Mientras tanto, los puntos de " +
                                  "navegación se quedan en Default: el sistema sigue funcionando, pero pueden " +
                                  "bloquearse ligeramente entre sí en la comprobación de línea de visión.");
            }

            int added = 0, skipped = 0;
            var seen = new HashSet<GameObject>();

            foreach (var mf in Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
            {
                var go = mf.gameObject;
                if (!seen.Add(go)) continue;
                if (mf.sharedMesh == null) continue;
                if (go.GetComponent<Collider>() != null) { skipped++; continue; }

                var col = go.AddComponent<MeshCollider>();
                col.sharedMesh = mf.sharedMesh;
                col.convex = false;
                added++;
            }

            Debug.Log($"[DigitalTwin] ColliderBootstrapper: {added} MeshCollider añadidos, {skipped} objetos ya tenían collider.");

            if (NavPointLayer >= 0)
            {
                foreach (var meta in index.NavPoints)
                    meta.gameObject.layer = NavPointLayer;
            }
        }

        /// <summary>Máscara de física para raycasts/linecasts de oclusión: todo menos los puntos de navegación.</summary>
        public static int OcclusionMask()
        {
            if (NavPointLayer < 0) return Physics.DefaultRaycastLayers;
            return Physics.DefaultRaycastLayers & ~(1 << NavPointLayer);
        }

        /// <summary>Máscara de física para el raycast de selección de elementos (Fase 2): todas las capas de física.</summary>
        public static int SelectionMask()
        {
            return Physics.AllLayers;
        }
    }
}
