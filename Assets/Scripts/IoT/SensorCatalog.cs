using System.Collections.Generic;
using System.Threading.Tasks;
using MySqlConnector;

namespace DigitalTwin.IoT
{
    /// <summary>
    /// Catálogo de sensores (tabla `sensors`, con el nombre de sala resuelto desde
    /// `sensor_rooms`). Cambia poco en comparación con las lecturas, así que se carga una sola
    /// vez al arrancar el middleware, no en cada sondeo.
    /// </summary>
    public class SensorCatalog
    {
        public readonly Dictionary<string, SensorInfo> BySensorId = new Dictionary<string, SensorInfo>();
        public readonly Dictionary<string, SensorInfo> ByGlobalId = new Dictionary<string, SensorInfo>();

        public async Task LoadAsync(MySqlConnection connection)
        {
            var rooms = new Dictionary<string, string>();
            using (var cmd = new MySqlCommand("SELECT room_id, name FROM sensor_rooms;", connection))
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                    rooms[reader.GetString(0)] = reader.GetString(1);
            }

            BySensorId.Clear();
            ByGlobalId.Clear();

            const string sql = "SELECT sensor_id, name, sensor_type, room_id, ifc_sensor_global_id, active FROM sensors;";
            using (var cmd = new MySqlCommand(sql, connection))
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    string sensorId = reader.GetString(0);
                    string name = reader.GetString(1);
                    string typeStr = reader.GetString(2);
                    string roomId = reader.GetString(3);
                    string globalId = reader.IsDBNull(4) ? null : reader.GetString(4);
                    bool active = reader.GetBoolean(5);

                    var info = new SensorInfo
                    {
                        SensorId = sensorId,
                        Name = name,
                        Kind = ParseKind(typeStr),
                        RoomId = roomId,
                        RoomName = rooms.TryGetValue(roomId, out var roomName) ? roomName : roomId,
                        GlobalId = globalId,
                        Active = active
                    };

                    BySensorId[sensorId] = info;
                    if (!string.IsNullOrEmpty(globalId)) ByGlobalId[globalId] = info;
                }
            }
        }

        private static SensorKind ParseKind(string sensorType)
        {
            switch (sensorType)
            {
                case "temperatura": return SensorKind.Temperatura;
                case "humedad": return SensorKind.Humedad;
                case "presion": return SensorKind.Presion;
                case "presencia": return SensorKind.Presencia;
                default:
                    // La columna `sensor_type` es un ENUM en el esquema, así que llegar aquí
                    // significa que alguien lo ha ampliado sin actualizar este código. Antes
                    // se devolvía Temperatura en silencio: el panel habría mostrado el valor
                    // con las unidades equivocadas (p. ej. una presión en grados centígrados)
                    // sin ninguna señal de que algo iba mal. Es preferible avisar.
                    Debug.LogWarning($"[DigitalTwin][IoT] Tipo de sensor no reconocido en la base de datos: " +
                                     $"'{sensorType}'. Se interpretará como temperatura; añádelo a " +
                                     $"SensorKind y a ParseKind si es un tipo nuevo.");
                    return SensorKind.Temperatura;
            }
        }
    }
}
