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
            foreach (var meta in Ocultables(index))
            {
                if (meta == null) continue;
                foreach (var t in meta.GetComponentsInChildren<Transform>(true))
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
                      $"collider, {espacios} omitidos por ser geometría no representable " +
                      $"(volúmenes de espacio y definiciones de tipo).");

            OcultarEspacios(index);

            if (NavPointLayer >= 0)
            {
                foreach (var meta in index.NavPoints)
                    meta.gameObject.layer = NavPointLayer;
            }
        }

        /// <summary>
        /// Elementos cuya geometría no debe dibujarse ni participar en la física: volúmenes de
        /// espacio y definiciones de tipo del catálogo. Se recorren juntos porque el tratamiento
        /// es el mismo, aunque el motivo de cada uno sea distinto (ver SceneModelIndex).
        /// </summary>
        private static IEnumerable<IFCImporter.IfcMetadata> Ocultables(SceneModelIndex index)
        {
            foreach (var m in index.Spaces) yield return m;
            foreach (var m in index.TypeDefinitions) yield return m;
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
            var yaVistos = new HashSet<Renderer>();

            foreach (var meta in Ocultables(index))
            {
                if (meta == null) continue;
                foreach (var r in meta.GetComponentsInChildren<Renderer>(true))
                {
                    // Un IfcSpaceType está en las dos listas; sin esta guarda se contaría doble.
                    if (r == null || !yaVistos.Add(r)) continue;
                    r.enabled = false;
                    ocultados++;
                }
            }

            // Los puntos de navegación son la tercera familia de geometría no representable: son
            // esferas colocadas durante el modelado como referencia espacial, no elementos
            // construidos. Se ocultan aquí, y no en el gestor de navegación de escritorio como se
            // hacía antes, porque ese gestor no existe en la versión de Realidad Aumentada y el
            // resultado era que allí las 36 esferas aparecían a tamaño real en mitad del edificio.
            //
            // Solo se desactiva el Renderer, nunca el Collider: ambas versiones los usan como
            // destino de selección, la de escritorio por pulsación y la inmersiva por rayo del
            // mando. Desactivar el objeto entero los volvería inalcanzables.
            int marcadores = 0;
            foreach (var meta in index.NavPoints)
            {
                if (meta == null) continue;
                foreach (var r in meta.GetComponentsInChildren<Renderer>(true))
                {
                    if (r == null || !yaVistos.Add(r)) continue;
                    r.enabled = false;
                    marcadores++;
                }
            }
            ocultados += marcadores;

            if (ocultados > 0)
                Debug.Log($"[DigitalTwin] {ocultados} mallas ocultadas por no representar geometría real " +
                          $"del edificio ({index.Spaces.Count} volúmenes de espacio, " +
                          $"{index.TypeDefinitions.Count} definiciones de tipo y " +
                          $"{index.NavPoints.Count} marcadores de navegación). Sus metadatos siguen " +
                          $"disponibles: los de IfcSpace sostienen la relación sensor-sala.");
        }

        /// <summary>Máscara de física para raycasts/linecasts de oclusión: todo menos los puntos de navegación.</summary>
        public static int OcclusionMask()
        {
            if (NavPointLayer < 0) return Physics.DefaultRaycastLayers;
            return Physics.DefaultRaycastLayers & ~(1 << NavPointLayer);
        }

        /// <summary>
        /// Cierto cuando los puntos de navegación deben quedar fuera de la selección por rayo.
        /// Lo activa el modo anclado de Realidad Aumentada: allí no existe el desplazamiento por
        /// nodos, las esferas no se dibujan, y un marcador invisible que intercepta el rayo haría
        /// que apuntar al vacío seleccionara algo que el usuario no puede ver. En escritorio y en
        /// navegación por nodos nadie lo activa, así que su comportamiento no cambia.
        /// </summary>
        private static bool _excluirPuntosDeNavegacionDeSeleccion;

        /// <summary>
        /// Excluye los marcadores de navegación de la máscara de selección (modo anclado).
        /// Si la capa dedicada no existe, cae a desactivar sus colisionadores, que consigue lo
        /// mismo por otra vía; ambas decisiones quedan en el registro.
        /// </summary>
        public static void ExcluirPuntosDeNavegacionDeLaSeleccion(SceneModelIndex index)
        {
            _excluirPuntosDeNavegacionDeSeleccion = true;

            if (NavPointLayer >= 0)
            {
                Debug.LogWarning("[DigitalTwin] Mascara de seleccion: puntos de navegacion " +
                                 "excluidos (capa " + NavPointLayerName + ").");
                return;
            }

            int desactivados = 0;
            if (index != null)
            {
                foreach (var meta in index.NavPoints)
                {
                    if (meta == null) continue;
                    foreach (var col in meta.GetComponentsInChildren<Collider>(true))
                    {
                        col.enabled = false;
                        desactivados++;
                    }
                }
            }
            Debug.LogWarning($"[DigitalTwin] Mascara de seleccion: no existe la capa " +
                             $"{NavPointLayerName}, asi que se desactivan {desactivados} " +
                             "colisionadores de marcadores para excluirlos de la seleccion.");
        }

        /// <summary>
        /// Máscara de física para el raycast de selección de elementos (Fase 2): todas las capas
        /// de física salvo, en modo anclado, la de los puntos de navegación (ver arriba).
        /// </summary>
        public static int SelectionMask()
        {
            if (_excluirPuntosDeNavegacionDeSeleccion && NavPointLayer >= 0)
                return Physics.AllLayers & ~(1 << NavPointLayer);
            return Physics.AllLayers;
        }
    }
}
