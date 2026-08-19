using System.Collections;
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

        /// <summary>
        /// Versión síncrona, conservada para quien no necesite repartir el trabajo (el Editor,
        /// una herramienta, una prueba). Consume la versión incremental de una sentada.
        /// </summary>
        public static void Setup(SceneModelIndex index)
        {
            var pasos = SetupIncremental(index, null);
            while (pasos.MoveNext()) { }
        }

        /// <summary>
        /// Igual que <see cref="Setup"/>, pero repartido entre fotogramas.
        ///
        /// POR QUÉ ESTE MÉTODO ES INCREMENTAL Y NO OTROS. En la sesión del 2026-08-18 (01:15) el
        /// registro del visor mide 71 ms entre la traza del índice del modelo y la de este
        /// método: los 351 <c>MeshCollider</c> son, con diferencia, la pieza más cara del
        /// arranque, y hasta ahora se creaban todos dentro del mismo fotograma. La razón del
        /// coste no es el <c>AddComponent</c> sino la asignación de <c>sharedMesh</c> con
        /// <c>convex = false</c>: PhysX construye entonces la estructura de aceleración de la
        /// malla de triángulos, y ese cocinado es trabajo de CPU en el hilo principal. El mismo
        /// registro lo confirma por otra vía: en la segunda ejecución de la misma sesión, tras
        /// recargar la escena, el método baja a 13 ms porque las mallas ya están cocinadas y en
        /// caché. Es decir, los 71 ms son coste de primera vez, exactamente el instante en que el
        /// usuario acaba de ponerse el visor.
        ///
        /// Setenta y un milisegundos son seis fotogramas perdidos a los 90 Hz que el visor declara en el
        /// propio registro (<c>RefreshRate change: 90.0</c>). Repartidos en
        /// tramos de <see cref="PresupuestoDeFotograma"/> el trabajo total no baja —sube un poco,
        /// por el coste de reanudar— pero deja de haber ningún fotograma que se lo lleve entero,
        /// que es lo que decide si la imagen sigue respondiendo a la cabeza.
        /// </summary>
        /// <param name="progreso">Avance en [0,1] para la pantalla de carga. Admite null.</param>
        public static IEnumerator SetupIncremental(SceneModelIndex index,
                                                   System.Action<float> progreso)
        {
            var presupuesto = new PresupuestoDeFotograma();

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

                if (presupuesto.Agotado)
                {
                    yield return null;
                    presupuesto.Reiniciar();
                }
            }

            int added = 0, skipped = 0, espacios = 0;
            var seen = new HashSet<GameObject>();

            var filtros = Object.FindObjectsByType<MeshFilter>(FindObjectsSortMode.None);
            for (int i = 0; i < filtros.Length; i++)
            {
                var mf = filtros[i];
                var go = mf.gameObject;
                if (!seen.Add(go)) continue;
                if (mf.sharedMesh == null) continue;
                if (deEspacios.Contains(go)) { espacios++; continue; }
                if (go.GetComponent<Collider>() != null) { skipped++; continue; }

                var col = go.AddComponent<MeshCollider>();
                col.sharedMesh = mf.sharedMesh;
                col.convex = false;
                added++;

                // La comprobación va DESPUÉS de crear el collider y no antes: el cocinado de una
                // malla grande puede agotar el presupuesto por sí solo, y en ese caso lo correcto
                // es ceder el fotograma inmediatamente, no encadenar otro cocinado.
                if (presupuesto.Agotado)
                {
                    progreso?.Invoke((i + 1) / (float)filtros.Length);
                    yield return null;
                    presupuesto.Reiniciar();
                }
            }
            progreso?.Invoke(1f);

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
        /// Repone el estado de selección de proceso al de un arranque limpio. Lo llama la
        /// vuelta al selector de modo (ronda 9): la exclusión de los puntos de navegación es
        /// una decisión DEL MODO anclado, y como vive en un estático sobrevive a la recarga de
        /// escena — sin esta reposición, un anclado→selector→navegación heredaría la máscara
        /// del modo anterior. Los colisionadores desactivados por la vía de respaldo no se
        /// reactivan aquí: pertenecen a la escena vieja y la recarga los reinstancia intactos.
        /// </summary>
        public static void ReiniciarSeleccionDeSesion()
        {
            if (!_excluirPuntosDeNavegacionDeSeleccion) return;
            _excluirPuntosDeNavegacionDeSeleccion = false;
            Debug.LogWarning("[DigitalTwin] Mascara de seleccion repuesta al estado de arranque " +
                             "(los puntos de navegacion vuelven a ser seleccionables donde el " +
                             "modo lo permita).");
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
