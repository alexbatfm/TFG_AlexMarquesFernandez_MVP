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
        [Tooltip("Anfitrión para la versión de escritorio, donde el contenedor corre en la misma " +
                 "máquina. En el visor se usa HostRemoto: ver la nota de HostEfectivo.")]
        public string Host = "127.0.0.1";

        /// <summary>
        /// Dirección del equipo que aloja el contenedor, vista desde la red local.
        ///
        /// Hace falta porque el visor es un dispositivo Android autónomo: tiene su propia pila de
        /// red y <c>127.0.0.1</c> se refiere a sí mismo, no al ordenador de desarrollo. El síntoma
        /// es inequívoco en el registro --- «Unable to connect to any of the specified MySQL
        /// hosts» contra 127.0.0.1 --- y se confirmó en la primera ejecución en el visor.
        ///
        /// <b>Aquí se cambia la dirección.</b> Este componente lo crea
        /// <see cref="SensorIntegrationBootstrap"/> en tiempo de ejecución, así que no está
        /// colocado en ninguna escena y su valor no aparece en el Inspector: el único sitio donde
        /// ajustarlo es esta línea, y hay que recompilar. Se deja así a conciencia, porque una
        /// pantalla de configuración de red dentro del visor es bastante trabajo para un dato que
        /// solo cambia al mudarse de red.
        ///
        /// Averígualo con <c>ipconfig</c> en el equipo que aloja el contenedor, y recuerda abrir el
        /// puerto 3306 para conexiones entrantes en el cortafuegos de Windows: sin esa regla la
        /// conexión se rechaza igual, con el mismo mensaje de error, y es fácil culpar a la IP.
        /// </summary>
        public string HostRemoto = "127.0.0.1";

        public int Port = 3306;
        public string Database = "periscoopedb";
        public string User = "root";
        public string Password = "root_password";

        [Header("Sondeo")]
        [Tooltip("Cada cuánto se consulta la base de datos por lecturas nuevas.")]
        public float PollIntervalSeconds = 5f;

        /// <summary>Tope del retroceso exponencial cuando la base de datos no responde. En el
        /// visor sin red local el fallo es PERMANENTE (el contenedor no es alcanzable), y
        /// reintentar cada 5 s significaba pagar cada 5 s una apertura de conexión condenada
        /// (ConnectionTimeout=5) durante toda la sesión: la prueba del 14-08 registró el fallo
        /// en bucle mientras el tiempo de fotograma derivaba. Con retroceso, los reintentos se
        /// espacian 5→10→20→40→60 s y se quedan ahí: si la base de datos vuelve (se arregla la
        /// red), la conexión se recupera en a lo sumo un minuto, que para telemetría de
        /// mantenimiento sobra.</summary>
        private const float EsperaMaximaSegundos = 60f;

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

        /// <summary>
        /// Anfitrión que se usa realmente, decidido en tiempo de ejecución.
        ///
        /// La regla es la plataforma y no una preferencia del usuario: en el visor, un bucle local
        /// nunca puede llegar al contenedor, y en el escritorio la dirección de red local funciona
        /// pero da un salto innecesario por el adaptador. Resolverlo aquí permite que la misma
        /// compilación sirva en las dos plataformas, que es lo que evita mantener dos ramas de
        /// configuración y equivocarse al cambiar de una a otra.
        /// </summary>
        public string HostEfectivo
        {
            get
            {
#if UNITY_ANDROID && !UNITY_EDITOR
                return string.IsNullOrWhiteSpace(HostRemoto) ? Host : HostRemoto;
#else
                return Host;
#endif
            }
        }

        private string ConnectionString =>
            $"Server={HostEfectivo};Port={Port};User ID={User};Password={Password};Database={Database};" +
            "SslMode=None;AllowPublicKeyRetrieval=True;ConnectionTimeout=5;DefaultCommandTimeout=10;";

        private void Start()
        {
            _running = true;
            Debug.Log($"[DigitalTwin][IoT] Iniciando sondeo contra {HostEfectivo}:{Port}/{Database} cada {PollIntervalSeconds}s...");
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
                float espera = Mathf.Max(1f, PollIntervalSeconds);
                while (_running)
                {
                    bool exito = await PollOnceAsync();

                    float esperaAnterior = espera;
                    espera = exito
                        ? Mathf.Max(1f, PollIntervalSeconds)
                        : Mathf.Min(esperaAnterior * 2f, EsperaMaximaSegundos);

                    if (!exito && espera > esperaAnterior)
                        Debug.LogWarning($"[DigitalTwin][IoT] Retroceso exponencial: proximo " +
                                         $"intento en {espera:0} s (la base de datos sigue sin " +
                                         "responder).");

                    await Task.Delay(TimeSpan.FromSeconds(espera));
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

        /// <summary>Un ciclo de sondeo. Devuelve si ha habido éxito, para que el bucle decida
        /// la espera (retroceso exponencial en fallo).</summary>
        private async Task<bool> PollOnceAsync()
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

                if (!IsConnected)
                    Debug.LogWarning($"[DigitalTwin][IoT] Conexion con MySQL establecida " +
                                     $"({HostEfectivo}:{Port})" +
                                     (LastSuccessfulPollUtc == default
                                         ? "." : "; recuperada tras fallos, se vuelve al sondeo normal."));

                IsConnected = true;
                LastError = null;
                LastSuccessfulPollUtc = DateTime.UtcNow;
                return true;
            }
            catch (Exception ex)
            {
                IsConnected = false;
                LastError = ex.Message;
                // Sin condición de «solo la primera vez»: la frecuencia la limita ya el
                // retroceso exponencial del bucle (como mucho una línea por minuto), y un
                // middleware caído que deja de avisar parecería un middleware sano.
                Debug.LogWarning($"[DigitalTwin][IoT] No se ha podido consultar MySQL en {HostEfectivo}:{Port}: {ex.Message}\n" +
                                  "¿Está arrancado el contenedor? -> docker start mysql-gemelo-digital");
                return false;
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
