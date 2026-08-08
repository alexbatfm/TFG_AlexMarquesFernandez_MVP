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

            // Objetos que forman parte de un volumen de espacio (IfcSpace). Se excluyen tanto de
            // los colliders como del render; ver OcultarEspacios más abajo para el motivo.
            var deEspacios = new HashSet<GameObject>();
            foreach (var espacio in index.Spaces)
            {
                if (espacio == null) continue;
                foreach (var t in espacio.GetComponentsInChildren<Transform>(true))
                    deEspacios.Add(t.gameObject);
            }

            int added = 0, skipped = 0, espacios = 0;
            var seen = new HashSet<GameObject>();

            foreach (var mf in Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None))
            {
                var go = mf.gameObject;
                if (!seen.Add(go)) continue;
                if (mf.sharedMesh == null) continue;
                if (deEspacios.Contains(go)) { espacios++; continue; }
                if (go.GetComponent<Collider>() != null) { skipped++; continue; }

                var col = go.AddComponent<MeshCollider>();
                col.sharedMesh = mf.sharedMesh;
                col.convex = false;
                added++;
            }

            Debug.Log($"[DigitalTwin] ColliderBootstrapper: {added} MeshCollider añadidos, {skipped} objetos ya tenían " +
                      $"collider, {espacios} omitidos por pertenecer a un volumen de espacio.");

            OcultarEspacios(index);

            if (NavPointLayer >= 0)
            {
                foreach (var meta in index.NavPoints)
                    meta.gameObject.layer = NavPointLayer;
            }
        }

        /// <summary>
        /// Apaga el render de los volúmenes de espacio (IfcSpace) conservando el GameObject y su
        /// componente IfcMetadata.
        ///
        /// En el modelo IFC estos volúmenes están marcados como no visibles, pero esa marca no
        /// sobrevive a la exportación a glTF: llegan a Unity como mallas normales y se dibujan
        /// como cajas opacas del tamaño de cada habitación, tapando el edificio entero.
        ///
        /// Por qué apagar el render en tiempo de ejecución en vez de borrarlos en Blender antes
        /// de exportar, que sería lo aparentemente más limpio: porque llevan datos que el sistema
        /// necesita. La tabla `sensor_rooms` de periscoopedb guarda en `ifc_space_global_id` el
        /// GlobalId de estos mismos elementos, y es lo que permite saber en qué sala está cada
        /// sensor. Si se eliminan del pipeline, esa relación deja de poder resolverse contra la
        /// geometría. Se aplica por tanto el mismo criterio que con los puntos de navegación
        /// "Esfera...": el objeto se queda, simplemente no se dibuja.
        ///
        /// Además de invisibles se quedan sin collider (ver arriba), lo cual corrige un problema
        /// que no era evidente: al envolver habitaciones enteras, un linecast de oclusión entre
        /// dos puntos de navegación de salas distintas atravesaba la cara del volumen de la sala
        /// destino y se contaba como obstáculo, de modo que los hotspots hacia otras salas nunca
        /// llegaban a aparecer. Lo mismo afectaba al raycast de selección al mirar una sala desde
        /// fuera: impactaba en el volumen antes que en el muro.
        /// </summary>
        private static void OcultarEspacios(SceneModelIndex index)
        {
            int ocultados = 0;
            foreach (var espacio in index.Spaces)
            {
                if (espacio == null) continue;
                foreach (var r in espacio.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null) continue;
                    r.enabled = false;
                    ocultados++;
                }
            }

            if (ocultados > 0)
                Debug.Log($"[DigitalTwin] {ocultados} mallas de volúmenes de espacio (IfcSpace) ocultadas; " +
                          $"sus metadatos siguen disponibles para la relación sensor-sala.");
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
