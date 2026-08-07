using System;

namespace DigitalTwin.IoT
{
    /// <summary>Coincide 1:1 con el enum `sensor_type` de la tabla `sensors` en periscoopedb.</summary>
    public enum SensorKind
    {
        Temperatura,
        Humedad,
        Presion,
        Presencia
    }

    /// <summary>Fila de la tabla `sensors` (+ nombre de sala resuelto desde `sensor_rooms`).</summary>
    public class SensorInfo
    {
        public string SensorId;
        public string Name;
        public SensorKind Kind;
        public string GlobalId; // == IfcMetadata.globalId del EQE... correspondiente en Unity
        public string RoomId;
        public string RoomName;
        public bool Active;
    }

    /// <summary>Última lectura conocida de un sensor, ya con el valor tipado según su clase.</summary>
    public class SensorReading
    {
        public string SensorId;
        public double NumericValue;
        public bool BoolValue;
        public DateTime RecordedAt;

        public string Format(SensorKind kind)
        {
            switch (kind)
            {
                case SensorKind.Temperatura: return $"{NumericValue:0.0} °C";
                case SensorKind.Humedad: return $"{NumericValue:0.0} % HR";
                case SensorKind.Presion: return $"{NumericValue:0.0} hPa";
                case SensorKind.Presencia: return BoolValue ? "Presencia detectada" : "Sin presencia";
                default: return NumericValue.ToString("0.0");
            }
        }
    }
}
