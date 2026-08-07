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

        public readonly List<IfcMetadata> NavPoints = new List<IfcMetadata>();
        public readonly List<IfcMetadata> Sensors = new List<IfcMetadata>();
        public readonly List<IfcMetadata> AllElements = new List<IfcMetadata>();

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
            }

            Debug.Log($"[DigitalTwin] SceneModelIndex: {index.AllElements.Count} elementos con metadatos, " +
                      $"{index.NavPoints.Count} puntos de navegación (Esfera...), {index.Sensors.Count} sensores IoT (EQE...).");

            if (index.NavPoints.Count == 0)
                Debug.LogWarning("[DigitalTwin] No se ha encontrado ningún punto de navegación 'Esfera...'. " +
                                  "La navegación por tour no tendrá puntos de partida.");

            return index;
        }
    }
}
