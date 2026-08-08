using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MySqlConnector;
using UnityEngine;

namespace DigitalTwin.IoT
{
    /// <summary>
    /// Middleware de conexión en tiempo real Unity ↔ MySQL (Fase 3).
    ///
    /// Decisión de arquitectura (polling directo por MySqlConnector, no WebSocket/MQTT):
    /// el contenedor Docker `mysql-gemelo-digital` solo expone el puerto 3306 (MySQL puro);
    /// no hay ningún broker de mensajería ni API intermedia desplegada, y montar una no estaba
    /// pedido ni forma parte de la infraestructura ya decidida para el proyecto. De hecho, el
    /// propio proyecto ya traía descargado el paquete NuGet `MySqlConnector` en
    /// `TFG/utility/mysqlconnector.2.6.1/` sin llegar a integrarlo en Unity: es la señal más
    /// clara de que la conexión directa a MySQL desde Unity era la solución ya prevista, sin
    /// piezas de infraestructura adicionales que mantener. Con sondeo periódico (por defecto
    /// cada 5s, configurable) es más que suficiente para un panel de mantenimiento de un
    /// gemelo digital -no hace falta latencia de milisegundos-, y es sensiblemente más simple
    /// de desplegar, depurar y mantener en el contexto de un TFG que levantar un servidor de
    /// WebSockets o un broker MQTT solo para republicar lo que ya está en la base de datos.
    ///
    /// Estrategia de sondeo: en el primer ciclo trae el último valor de cada sensor (para no
    /// arrancar el panel "en blanco"); a partir de ahí solo pide filas con
    /// `recorded_at > última_marca_de_agua_conocida` por tabla, así el coste de cada sondeo no
    /// crece con el histórico acumulado en la base de datos.
    ///
    /// El bucle usa async/await de C# de principio a fin sin `ConfigureAwait(false)`: como se
    /// arranca desde el hilo principal de Unity, todas las continuaciones (incluida la
    /// actualización de <see cref="SensorDataStore"/>) vuelven también al hilo principal, así
    /// que es seguro tocar estado de Unity/eventos desde aquí sin locks ni Dispatcher manual.
    /// </summary>
    public class MySqlSensorPollingService : MonoBehaviour
    {
        [Header("Conexión (ver TFG/utility/informacion_mysql-gemelo-digital.txt)")]
        public string Host = "127.0.0.1";
        public int Port = 3306;
        public string Database = "periscoopedb";
        public string User = "root";
        public string Password = "root_password";

        [Header("Sondeo")]
        [Tooltip("Cada cuánto se consulta la base de datos por lecturas nuevas.")]
        public float PollIntervalSeconds = 5f;

        public SensorDataStore Store { get; } = new SensorDataStore();
        public SensorCatalog Catalog { get; } = new SensorCatalog();

        public bool IsConnected { get; private set; }
        public DateTime LastSuccessfulPollUtc { get; private set; }
        public string LastError { get; private set; }

        private static readonly (string Table, string ValueColumn, SensorKind Kind)[] ReadingTables =
        {
            ("temperature_sensor_readings", "temperature_c", SensorKind.Temperatura),
            ("humidity_sensor_readings", "relative_humidity", SensorKind.Humedad),
            ("pressure_sensor_readings", "pressure_hpa", SensorKind.Presion),
            ("presence_sensor_readings", "presence", SensorKind.Presencia),
        };

        private readonly Dictionary<string, DateTime> _watermarks = new Dictionary<string, DateTime>();
        private bool _catalogLoaded;
        private bool _running;

        private string ConnectionString =>
            $"Server={Host};Port={Port};User ID={User};Password={Password};Database={Database};" +
            "SslMode=None;AllowPublicKeyRetrieval=True;ConnectionTimeout=5;DefaultCommandTimeout=10;";

        private void Start()
        {
            _running = true;
            Debug.Log($"[DigitalTwin][IoT] Iniciando sondeo contra {Host}:{Port}/{Database} cada {PollIntervalSeconds}s...");
            _ = PollLoopAsync();
        }

        private void OnDestroy()
        {
            _running = false;
        }

        /// <summary>
        /// Bucle de sondeo.
        ///
        /// El try/catch que envuelve todo el bucle no es defensivo por costumbre, corrige un fallo
        /// real: como el bucle se lanza en modo "dispara y olvida" (<c>_ = PollLoopAsync()</c>),
        /// nadie observa la tarea resultante. Cualquier excepción que escapara de aquí se perdía
        /// sin dejar ni una línea en consola, y el middleware quedaba muerto en silencio, con el
        /// panel mostrando "esperando primera lectura" para siempre.
        ///
        /// El caso concreto que se escapaba: <see cref="PollOnceAsync"/> sí tiene su propio
        /// try/catch, pero solo cubre lo que ocurre DENTRO del método. Si el ensamblado de
        /// MySqlConnector no se carga (DLL ausente, arquitectura incompatible, o no marcado para
        /// la plataforma activa), el fallo se produce al resolver los tipos en el momento de
        /// invocar el método, es decir antes de entrar en su try, y por tanto lo atravesaba.
        /// </summary>
        private async Task PollLoopAsync()
        {
            try
            {
                while (_running)
                {
                    await PollOnceAsync();
                    await Task.Delay(TimeSpan.FromSeconds(Mathf.Max(1f, PollIntervalSeconds)));
                }
            }
            catch (Exception ex)
            {
                IsConnected = false;
                LastError = ex.Message;
                Debug.LogError($"[DigitalTwin][IoT] El bucle de sondeo se ha detenido por un error no " +
                               $"recuperable: {ex.GetType().Name}: {ex.Message}\n" +
                               "Si menciona MySqlConnector, revisa que Assets/Plugins/MySqlConnector/ contenga " +
                               "el DLL y que en su importador esté marcada la plataforma activa.\n" + ex.StackTrace);
            }
        }

        private async Task PollOnceAsync()
        {
            try
            {
                using var connection = new MySqlConnection(ConnectionString);
                await connection.OpenAsync();

                if (!_catalogLoaded)
                {
                    await Catalog.LoadAsync(connection);
                    _catalogLoaded = true;
                    Debug.Log($"[DigitalTwin][IoT] Catálogo de sensores cargado: {Catalog.BySensorId.Count} sensores en periscoopedb.");
                }

                foreach (var table in ReadingTables)
                    await PollTableAsync(connection, table.Table, table.ValueColumn, table.Kind);

                IsConnected = true;
                LastError = null;
                LastSuccessfulPollUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                bool wasConnected = IsConnected;
                IsConnected = false;
                LastError = ex.Message;
                if (wasConnected || LastSuccessfulPollUtc == default)
                {
                    Debug.LogWarning($"[DigitalTwin][IoT] No se ha podido consultar MySQL en {Host}:{Port}: {ex.Message}\n" +
                                      "¿Está arrancado el contenedor? -> docker start mysql-gemelo-digital");
                }
            }
        }

        private async Task PollTableAsync(MySqlConnection connection, string table, string valueColumn, SensorKind kind)
        {
            bool firstPoll = !_watermarks.ContainsKey(table);

            string sql = firstPoll
                ? $"SELECT t.sensor_id, t.{valueColumn}, t.recorded_at FROM {table} t " +
                  $"INNER JOIN (SELECT sensor_id, MAX(recorded_at) AS max_rec FROM {table} GROUP BY sensor_id) latest " +
                  "ON t.sensor_id = latest.sensor_id AND t.recorded_at = latest.max_rec;"
                : $"SELECT sensor_id, {valueColumn}, recorded_at FROM {table} WHERE recorded_at > @since ORDER BY recorded_at ASC;";

            using var cmd = new MySqlCommand(sql, connection);
            if (!firstPoll) cmd.Parameters.AddWithValue("@since", _watermarks[table]);

            DateTime maxSeen = firstPoll ? DateTime.MinValue : _watermarks[table];
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    DateTime recordedAt = reader.GetDateTime(2);
                    ApplyRow(reader, kind, recordedAt);
                    if (recordedAt > maxSeen) maxSeen = recordedAt;
                }
            }

            // Si la tabla estaba vacía no hay marca de agua que heredar y hay que inventarla.
            // Se usa DateTime.Now (hora local) y NO DateTime.UtcNow: la columna `recorded_at`
            // es un DATETIME de MySQL, sin zona horaria, y todo el histórico está escrito en
            // hora local. Mezclar ambas escalas desplazaría la marca de agua tantas horas como
            // diste el equipo de UTC (en España, 1 o 2), con lo que las primeras lecturas
            // nuevas podrían quedar por debajo del corte y no llegar nunca al panel.
            _watermarks[table] = maxSeen == DateTime.MinValue ? DateTime.Now : maxSeen;
        }

        private void ApplyRow(MySqlDataReader reader, SensorKind kind, DateTime recordedAt)
        {
            string sensorId = reader.GetString(0);

            var reading = new SensorReading { SensorId = sensorId, RecordedAt = recordedAt };
            if (kind == SensorKind.Presencia)
                reading.BoolValue = reader.GetBoolean(1);
            else
                reading.NumericValue = (double)reader.GetDecimal(1);

            if (Catalog.BySensorId.TryGetValue(sensorId, out var info) && !string.IsNullOrEmpty(info.GlobalId))
                Store.Update(info.GlobalId, reading);
        }
    }
}
