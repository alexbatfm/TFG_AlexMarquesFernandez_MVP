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
        [Header("Conexión (valores de desarrollo; producción en backend.json)")]
        [Tooltip("Anfitrión para la versión de escritorio, donde el contenedor corre en la misma " +
                 "máquina. En el visor se usa HostRemoto: ver la nota de HostEfectivo.")]
        public string Host = "127.0.0.1";

        /// <summary>
        /// Dirección del equipo que aloja el contenedor, vista desde el visor.
        ///
        /// Hace falta porque el visor es un dispositivo Android autónomo: tiene su propia pila de
        /// red y <c>127.0.0.1</c> se refiere a sí mismo, no al ordenador de desarrollo. El síntoma
        /// es inequívoco en el registro --- «Unable to connect to any of the specified MySQL
        /// hosts» contra 127.0.0.1 --- y se confirmó en la primera ejecución en el visor.
        ///
        /// <b>El valor de producción no está aquí.</b> Todos los campos de esta cabecera son los
        /// del contenedor local de desarrollo y se sustituyen en arranque por los de
        /// <c>backend.json</c> (<see cref="BackendConfig"/>), que es lo que permite cambiar de
        /// servidor sin recompilar y lo que mantiene la contraseña de producción fuera del
        /// repositorio. Los valores compilados siguen siendo los locales a propósito: trabajar en
        /// casa contra el contenedor de Docker no debe exigir conexión a Internet ni fichero
        /// alguno.
        /// </summary>
        public string HostRemoto = "127.0.0.1";

        public int Port = 3306;
        public string Database = "periscoopedb";
        public string User = "root";
        public string Password = "root_password";

        /// <summary>
        /// Modo TLS de la conexión, configurable desde <c>backend.json</c>.
        ///
        /// Era <c>None</c> fijo mientras la base de datos vivía en <c>127.0.0.1</c>, donde el
        /// tráfico no sale de la máquina. Con el contenedor alojado en Internet, las credenciales
        /// y las lecturas atraviesan una red pública, así que el valor por defecto pasa a
        /// <c>Preferred</c>: cifra siempre que el servidor ofrezca TLS, y MySQL 8.4 lo ofrece de
        /// serie porque genera su propio certificado al inicializar el directorio de datos.
        ///
        /// No se sube a <c>Required</c> por defecto porque esa opción convierte cualquier fallo de
        /// la pila TLS del visor en ausencia total de telemetría, y eso no se ha podido probar
        /// todavía en el dispositivo. La comprobación está en la lista previa a la defensa; si
        /// resultara fallar, se baja a <c>None</c> desde el fichero, sin recompilar.
        /// </summary>
        public string SslMode = "Preferred";

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

        /// <summary>
        /// TOPE DE FILAS POR TABLA Y CICLO del sondeo incremental (15-08). El incremental pedía
        /// `recorded_at > @since` SIN LIMIT: con la marca de agua atrasada —fallos de conexión,
        /// retroceso de hasta 60 s, o un arranque tardío frente a un simulador que lleva horas
        /// escribiendo— un único ciclo bueno se traía TODO el atraso de las cuatro tablas de
        /// golpe y lo procesaba fila a fila. Es un mecanismo real para el «se puso raro» del
        /// 14-08 (y encaja con que reiniciar la base de datos lo arreglara: histórico
        /// acumulado), aunque sin registro de aquella sesión la causa no se puede afirmar.
        ///
        /// LA POLÍTICA CON EL ATRASO, escrita: si una tabla devuelve el tope completo, hay más
        /// atraso esperando, y para telemetría de mantenimiento el histórico intermedio no
        /// vale nada — lo que importa es el estado ACTUAL de cada sensor. Se descarta el
        /// atraso saltando al valor más reciente: la marca de agua de esa tabla se borra y el
        /// ciclo siguiente vuelve al sondeo inicial (último valor por sensor, acotado por
        /// construcción). Nunca en silencio: el salto queda registrado con la tabla y la marca.
        ///
        /// El valor: 32 sensores y sondeo cada 5 s producen como mucho unas decenas de filas
        /// por tabla y ciclo en funcionamiento normal; 200 deja un orden de magnitud de
        /// holgura antes de declarar atraso.
        /// </summary>
        private const int LimiteFilasPorCiclo = 200;

        /// <summary>Cada cuántos ciclos se vuelca el resumen del sondeo (con 5 s por ciclo,
        /// una línea por minuto): filas por tabla, duración media y marcas de agua. Es la
        /// instrumentación que faltó el 14-08 para que la anomalía fuera legible.</summary>
        private const int CiclosEntreResumenes = 12;

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

        // Instrumentación del sondeo: acumuladores del resumen periódico y estado del último
        // ciclo. Todo se vuelca por LogWarning; ver LimiteFilasPorCiclo y CiclosEntreResumenes.
        private readonly Dictionary<string, int> _filasDelCiclo = new Dictionary<string, int>();
        private readonly Dictionary<string, long> _filasAcumuladas = new Dictionary<string, long>();
        private int _ciclosDesdeResumen;
        private double _msAcumulados;
        private readonly System.Diagnostics.Stopwatch _cronoCiclo = new System.Diagnostics.Stopwatch();

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
            $"SslMode={SslMode};AllowPublicKeyRetrieval=True;ConnectionTimeout=5;DefaultCommandTimeout=10;";

        /// <summary>
        /// La misma cadena que <c>ConnectionString</c> pero sin la contraseña, para poder
        /// registrarla. El registro del visor se vuelca con <c>adb logcat</c> a un fichero que
        /// después se adjunta como evidencia en los anexos: una contraseña ahí dentro sería una
        /// contraseña publicada.
        /// </summary>
        public string CadenaDeConexionSegura =>
            $"{User}@{HostEfectivo}:{Port}/{Database} (SslMode={SslMode}, " +
            $"contraseña de {Password?.Length ?? 0} caracteres)";

        /// <summary>
        /// Sustituye los valores compilados por los de <c>backend.json</c> si existe.
        ///
        /// En <c>Awake</c> y no en <c>Start</c> porque el bucle de sondeo arranca en <c>Start</c>:
        /// leer la configuración después de haber abierto la primera conexión dejaría el primer
        /// ciclo apuntando al servidor equivocado, y con él la primera marca de agua.
        /// </summary>
        private void Awake()
        {
            var config = new BackendConfig
            {
                host = Host,
                hostRemoto = HostRemoto,
                puerto = Port,
                baseDeDatos = Database,
                usuario = User,
                contrasena = Password,
                sslMode = SslMode,
                segundosEntreSondeos = PollIntervalSeconds,
            };

            if (!BackendConfigLoader.Aplicar(config)) return;

            Host = config.host;
            HostRemoto = config.hostRemoto;
            Port = config.puerto;
            Database = config.baseDeDatos;
            User = config.usuario;
            Password = config.contrasena;
            SslMode = config.sslMode;
            PollIntervalSeconds = config.segundosEntreSondeos;
        }

        private void Start()
        {
            _running = true;

            // Un bucle local en el visor no puede alcanzar a ninguna base de datos que no sea el
            // propio visor, así que este caso solo se da cuando falta backend.json o cuando lleva
            // el anfitrión sin rellenar. Se distingue del fallo de red porque aquí se sabe la
            // causa exacta, y con ella la solución: sin este aviso, el síntoma es idéntico al de
            // un servidor caído y se pierde media hora buscando en el sitio equivocado.
            if (Application.platform == RuntimePlatform.Android && EsBuclelocal(HostEfectivo))
                Debug.LogError($"[DigitalTwin][IoT] El anfitrión configurado es {HostEfectivo}, que en " +
                               "el visor se refiere al propio visor: NO habrá telemetría. Falta " +
                               $"{BackendConfigLoader.NombreFichero} en {Application.persistentDataPath} " +
                               "o no trae 'hostRemoto'. Origen de la configuración en uso: " +
                               $"{BackendConfigLoader.UltimoOrigen}.");

            // Warning y no Log: cuando el sondeo del 14-08 «se puso raro», esta linea y la del
            // catalogo eran las que habrian dicho que estaba haciendo, y una compilacion que no
            // es de desarrollo las filtraba del logcat.
            Debug.LogWarning($"[DigitalTwin][IoT] Iniciando sondeo contra {CadenaDeConexionSegura} cada " +
                             $"{PollIntervalSeconds}s (tope {LimiteFilasPorCiclo} filas/tabla/ciclo). " +
                             $"Configuración tomada de: {BackendConfigLoader.UltimoOrigen}.");
            _ = PollLoopAsync();
        }

        /// <summary>Direcciones que se refieren a la propia máquina. Se comprueba el nombre además
        /// de las dos formas numéricas porque <c>localhost</c> escrito en el fichero produce el
        /// mismo fallo y no lo detectaría una comparación con <c>127.0.0.1</c>.</summary>
        private static bool EsBuclelocal(string anfitrion)
        {
            if (string.IsNullOrWhiteSpace(anfitrion)) return true;
            string h = anfitrion.Trim().ToLowerInvariant();
            return h == "localhost" || h == "::1" || h.StartsWith("127.");
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
                    Debug.LogWarning($"[DigitalTwin][IoT] Catálogo de sensores cargado: {Catalog.BySensorId.Count} sensores en periscoopedb.");
                }

                _filasDelCiclo.Clear();
                _cronoCiclo.Restart();
                foreach (var table in ReadingTables)
                    await PollTableAsync(connection, table.Table, table.ValueColumn, table.Kind);
                _cronoCiclo.Stop();

                RegistrarEstadisticasDelCiclo();

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

            // El incremental lleva TOPE (ver LimiteFilasPorCiclo): se pide una fila más que el
            // tope solo para poder distinguir «justo el tope» de «hay atraso pendiente».
            string sql = firstPoll
                ? $"SELECT t.sensor_id, t.{valueColumn}, t.recorded_at FROM {table} t " +
                  $"INNER JOIN (SELECT sensor_id, MAX(recorded_at) AS max_rec FROM {table} GROUP BY sensor_id) latest " +
                  "ON t.sensor_id = latest.sensor_id AND t.recorded_at = latest.max_rec;"
                : $"SELECT sensor_id, {valueColumn}, recorded_at FROM {table} WHERE recorded_at > @since " +
                  $"ORDER BY recorded_at ASC LIMIT {LimiteFilasPorCiclo + 1};";

            using var cmd = new MySqlCommand(sql, connection);
            if (!firstPoll) cmd.Parameters.AddWithValue("@since", _watermarks[table]);

            DateTime maxSeen = firstPoll ? DateTime.MinValue : _watermarks[table];
            int filas = 0;
            using (var reader = await cmd.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    filas++;
                    if (!firstPoll && filas > LimiteFilasPorCiclo) break; // hay atraso: se corta
                    DateTime recordedAt = reader.GetDateTime(2);
                    ApplyRow(reader, kind, recordedAt);
                    if (recordedAt > maxSeen) maxSeen = recordedAt;
                }
            }
            _filasDelCiclo[table] = filas;
            _filasAcumuladas[table] = (_filasAcumuladas.TryGetValue(table, out long acc) ? acc : 0) +
                                      Mathf.Min(filas, LimiteFilasPorCiclo);

            if (!firstPoll && filas > LimiteFilasPorCiclo)
            {
                // ATRASO DECLARADO: la política escrita en LimiteFilasPorCiclo — el histórico
                // intermedio se descarta y se salta al valor más reciente por sensor. Borrar la
                // marca hace que el próximo ciclo use el sondeo inicial, acotado.
                Debug.LogWarning($"[DigitalTwin][IoT] Atraso en '{table}': mas de " +
                                 $"{LimiteFilasPorCiclo} filas pendientes desde " +
                                 $"{_watermarks[table]:yyyy-MM-dd HH:mm:ss}. Se DESCARTA el " +
                                 "atraso y el proximo ciclo salta al ultimo valor por sensor " +
                                 "(para telemetria de mantenimiento importa el estado actual, " +
                                 "no el historico intermedio).");
                _watermarks.Remove(table);
                return;
            }

            // Si la tabla estaba vacía no hay marca de agua que heredar y hay que inventarla.
            // Se usa DateTime.Now (hora local) y NO DateTime.UtcNow: la columna `recorded_at`
            // es un DATETIME de MySQL, sin zona horaria, y todo el histórico está escrito en
            // hora local. Mezclar ambas escalas desplazaría la marca de agua tantas horas como
            // diste el equipo de UTC (en España, 1 o 2), con lo que las primeras lecturas
            // nuevas podrían quedar por debajo del corte y no llegar nunca al panel.
            //
            // ESE RAZONAMIENTO DA POR HECHO QUE LOS DOS RELOJES COINCIDEN, y visor y equipo
            // son máquinas distintas: si el visor va adelantado, la marca queda en el futuro y
            // no llega nada nunca; si va atrasado, llega una avalancha. Por eso el sellado
            // deja registro con la hora local del dispositivo: el desfase se lee comparándola
            // con el recorded_at que escriba el simulador.
            if (maxSeen == DateTime.MinValue)
            {
                DateTime sello = DateTime.Now;
                _watermarks[table] = sello;
                Debug.LogWarning($"[DigitalTwin][IoT] Tabla '{table}' vacia: marca de agua " +
                                 $"sellada con la hora local del dispositivo " +
                                 $"{sello:yyyy-MM-dd HH:mm:ss}. Si el reloj del equipo de la " +
                                 "base de datos difiere, este sello queda en su futuro (no " +
                                 "llegaria nada) o en su pasado (llegaria una avalancha, que " +
                                 "el tope por ciclo acota).");
            }
            else
            {
                _watermarks[table] = maxSeen;
            }
        }

        /// <summary>
        /// La instrumentación que faltó el 14-08: un resumen por minuto con filas por tabla,
        /// duración media y marcas de agua, y una línea inmediata si un ciclo se sale de lo
        /// normal (muchas filas o demasiado tiempo). Con esto, la próxima anomalía del sondeo
        /// se diagnostica desde el registro en lugar de quedarse en «se puso raro».
        /// </summary>
        private void RegistrarEstadisticasDelCiclo()
        {
            double ms = _cronoCiclo.Elapsed.TotalMilliseconds;
            _msAcumulados += ms;
            _ciclosDesdeResumen++;

            int filasCiclo = 0;
            foreach (var par in _filasDelCiclo) filasCiclo += Mathf.Min(par.Value, LimiteFilasPorCiclo);

            // Ciclo anómalo: se cuenta al momento, no se espera al resumen.
            if (ms > 1000 || filasCiclo > LimiteFilasPorCiclo)
                Debug.LogWarning($"[DigitalTwin][IoT] Ciclo fuera de lo normal: {filasCiclo} " +
                                 $"filas en {ms:0} ms.");

            if (_ciclosDesdeResumen < CiclosEntreResumenes) return;

            var detalle = new System.Text.StringBuilder();
            foreach (var table in ReadingTables)
            {
                long total = _filasAcumuladas.TryGetValue(table.Table, out long acc) ? acc : 0;
                string marca = _watermarks.TryGetValue(table.Table, out DateTime w)
                    ? w.ToString("HH:mm:ss") : "(sin marca)";
                if (detalle.Length > 0) detalle.Append("; ");
                detalle.Append(table.Table.Replace("_sensor_readings", ""))
                       .Append('=').Append(total).Append(" filas, marca ").Append(marca);
            }

            Debug.LogWarning($"[DigitalTwin][IoT] Resumen de sondeo: {_ciclosDesdeResumen} " +
                             $"ciclos, {_msAcumulados / _ciclosDesdeResumen:0} ms de media, " +
                             $"hora local del dispositivo {DateTime.Now:HH:mm:ss}. {detalle}.");

            _ciclosDesdeResumen = 0;
            _msAcumulados = 0;
            _filasAcumuladas.Clear();
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
