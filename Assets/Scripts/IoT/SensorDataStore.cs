using System;
using System.Collections.Generic;

namespace DigitalTwin.IoT
{
    /// <summary>
    /// Caché en memoria de la última lectura conocida de cada sensor, indexada por su GlobalId
    /// de IFC (la clave que llevan los GameObjects "EQE..." en Unity vía IfcMetadata.globalId),
    /// no por su sensor_id de periscoopedb. La traducción sensor_id -> GlobalId la resuelve
    /// SensorCatalog al cargar el catálogo de sensores desde la tabla `sensors`
    /// (columna ifc_sensor_global_id).
    /// </summary>
    public class SensorDataStore
    {
        private readonly Dictionary<string, SensorReading> _latestByGlobalId = new Dictionary<string, SensorReading>();

        /// <summary>Se dispara con el GlobalId cuando llega una lectura más reciente que la que había.</summary>
        public event Action<string> OnSensorUpdated;

        public void Update(string globalId, SensorReading reading)
        {
            if (string.IsNullOrEmpty(globalId) || reading == null) return;

            if (_latestByGlobalId.TryGetValue(globalId, out var existing) && existing.RecordedAt >= reading.RecordedAt)
                return;

            _latestByGlobalId[globalId] = reading;
            OnSensorUpdated?.Invoke(globalId);
        }

        public bool TryGet(string globalId, out SensorReading reading)
        {
            return _latestByGlobalId.TryGetValue(globalId, out reading);
        }
    }
}
