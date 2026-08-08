using System.Collections.Generic;
using IFCImporter;
using UnityEngine;

namespace DigitalTwin.Core
{
    /// <summary>
    /// Escanea la escena una vez y clasifica todos los GameObjects que llevan el componente
    /// IfcMetadata (inyectado por el importador Tools/IFC/Import Metadata) en tres grupos,
    /// según la convención de nombres del proyecto:
    ///   - Puntos de navegación: IfcVirtualElement con ifcName que empieza por "Esfera".
    ///   - Sensores IoT:         IfcBuildingElementProxy con ifcName que empieza por "EQE".
    ///     (Se excluye deliberadamente "IfcBuildingElementProxyType": son las definiciones de
    ///     tipo del catálogo IFC, no instancias colocadas en el modelo, y no tienen una
    ///     posición 3D útil para un hotspot ni un sensor real en periscoopedb).
    ///   - Resto de elementos: cualquier otro objeto con metadatos (muros, puertas, etc.),
    ///     usados por el panel de metadatos genérico (Fase 2).
    /// </summary>
    public class SceneModelIndex
    {
        public const string NavPointIfcType = "IfcVirtualElement";
        public const string NavPointPrefix = "Esfera";
        public const string SensorIfcType = "IfcBuildingElementProxy";
        public const string SensorPrefix = "EQE";
        public const string SpaceIfcType = "IfcSpace";

        public readonly List<IfcMetadata> NavPoints = new List<IfcMetadata>();
        public readonly List<IfcMetadata> Sensors = new List<IfcMetadata>();

        /// <summary>
        /// Volúmenes de espacio (IfcSpace): las salas del edificio representadas como cuerpos
        /// que ocupan toda la habitación. En un visor BIM están ocultos por defecto porque no
        /// son elementos construidos, sino zonas conceptuales; al exportar a glTF se convierten
        /// en mallas normales y aparecen como cajas opacas que tapan el edificio.
        ///
        /// No se descartan del modelo aunque no se dibujen, porque llevan información que el
        /// sistema usa: la tabla `sensor_rooms` de periscoopedb referencia estos mismos GlobalId
        /// en su columna `ifc_space_global_id`, y son el vínculo entre un sensor y la sala en la
        /// que está. Borrarlos en el pipeline de Blender rompería esa relación.
        /// </summary>
        public readonly List<IfcMetadata> Spaces = new List<IfcMetadata>();

        /// <summary>
        /// Definiciones de tipo del catálogo IFC (IfcWallType, IfcFurnitureType, IfcMemberType,
        /// IfcTypeProduct...). Describen "un tipo de silla" o "un tipo de muro", no una silla ni
        /// un muro concretos colocados en el edificio.
        ///
        /// No deberían dibujarse, pero al exportar a glTF se materializan como mallas reales en
        /// posiciones arbitrarias —normalmente el origen o la posición de la primera instancia—,
        /// lo que produce el efecto de mobiliario y tabiques flotando y atravesando paredes.
        /// Es un artefacto de la exportación, no un problema del modelo.
        ///
        /// Se detectan por convención de nomenclatura del propio estándar: en IFC toda entidad de
        /// definición de tipo termina en "Type" (más <c>IfcTypeProduct</c>, que es la raíz
        /// abstracta de todas ellas). Es un criterio del estándar, no una heurística sobre los
        /// nombres que haya puesto quien modeló el edificio.
        /// </summary>
        public readonly List<IfcMetadata> TypeDefinitions = new List<IfcMetadata>();

        public readonly List<IfcMetadata> AllElements = new List<IfcMetadata>();

        /// <summary>¿Es una definición de tipo del catálogo IFC y no un elemento colocado?</summary>
        public static bool EsDefinicionDeTipo(string ifcType)
        {
            if (string.IsNullOrEmpty(ifcType)) return false;
            return ifcType.EndsWith("Type") || ifcType == "IfcTypeProduct";
        }

        public static SceneModelIndex Build()
        {
            var index = new SceneModelIndex();
            var all = Object.FindObjectsByType<IfcMetadata>(FindObjectsSortMode.None);

            foreach (var meta in all)
            {
                index.AllElements.Add(meta);

                if (meta.ifcType == NavPointIfcType && !string.IsNullOrEmpty(meta.ifcName) && meta.ifcName.StartsWith(NavPointPrefix))
                {
                    index.NavPoints.Add(meta);
                }
                else if (meta.ifcType == SensorIfcType && !string.IsNullOrEmpty(meta.ifcName) && meta.ifcName.StartsWith(SensorPrefix))
                {
                    index.Sensors.Add(meta);
                }
                else if (meta.ifcType == SpaceIfcType)
                {
                    index.Spaces.Add(meta);
                }

                // Fuera de la cadena if/else anterior: una definición de tipo puede ser además
                // de espacio (IfcSpaceType), y en ese caso debe entrar en las dos listas.
                if (EsDefinicionDeTipo(meta.ifcType))
                {
                    index.TypeDefinitions.Add(meta);
                }
            }

            Debug.Log($"[DigitalTwin] SceneModelIndex: {index.AllElements.Count} elementos con metadatos, " +
                      $"{index.NavPoints.Count} puntos de navegación (Esfera...), {index.Sensors.Count} sensores IoT (EQE...), " +
                      $"{index.Spaces.Count} volúmenes de espacio (IfcSpace), " +
                      $"{index.TypeDefinitions.Count} definiciones de tipo del catálogo IFC.");

            if (index.NavPoints.Count == 0)
                Debug.LogWarning("[DigitalTwin] No se ha encontrado ningún punto de navegación 'Esfera...'. " +
                                  "La navegación por tour no tendrá puntos de partida.");

            return index;
        }
    }
}
