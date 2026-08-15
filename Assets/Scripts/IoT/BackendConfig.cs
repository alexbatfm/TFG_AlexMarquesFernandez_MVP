using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace DigitalTwin.IoT
{
    /// <summary>
    /// Parámetros de conexión con el servidor de telemetría, leídos de un fichero externo a la
    /// aplicación.
    ///
    /// POR QUÉ DEJA DE ESTAR EN EL CÓDIGO. Hasta el 15-08-2026 la dirección del servidor era una
    /// constante de <see cref="MySqlSensorPollingService"/>, justificada por tratarse de «un dato
    /// que solo cambia al mudarse de red». Esa premisa dejó de valer cuando el contenedor pasó a
    /// vivir en una máquina alojada: entre la instalación de la aplicación y la defensa la
    /// dirección cambia al menos una vez (creación de la máquina, y de nuevo si se recrea), y
    /// recompilar exige un ordenador con Unity, el proyecto abierto y el visor conectado por
    /// cable. Ninguna de las tres cosas está disponible en una sala de defensa.
    ///
    /// POR QUÉ UN FICHERO Y NO UNA PANTALLA DE AJUSTES. Escribir una dirección de red y una
    /// contraseña con los mandos del visor, carácter a carácter y sin teclado físico, es más lento
    /// y más propenso a error que copiar un fichero de veinte líneas; y la pantalla habría que
    /// construirla dos veces, en escritorio y en Realidad Aumentada. El fichero se empuja con
    /// <c>adb push</c> en un segundo, se puede preparar y revisar en el ordenador, y no ocupa
    /// superficie de interfaz que después haya que documentar y probar.
    ///
    /// POR QUÉ LAS CREDENCIALES NO VIAJAN EN EL CÓDIGO. El repositorio del MVP es público para el
    /// tribunal; una contraseña compilada dentro del <c>.apk</c> es además irrevocable sin volver
    /// a compilar. Los valores compilados de <see cref="MySqlSensorPollingService"/> son los del
    /// contenedor local de desarrollo, que solo escucha en <c>127.0.0.1</c> y no expone nada;
    /// los de producción viven únicamente en este fichero, que no se versiona.
    ///
    /// SEMÁNTICA DE SUSTITUCIÓN PARCIAL. Se aplica con <see cref="JsonUtility.FromJsonOverwrite"/>
    /// sobre un objeto ya relleno con los valores compilados, así que el fichero solo necesita
    /// contener las claves que cambian. Un <c>backend.json</c> con una sola línea
    /// (<c>{"hostRemoto":"..."}</c>) es válido y deja el resto como estaba, que es lo que se
    /// quiere cuando lo único que se ha movido es el servidor.
    /// </summary>
    [Serializable]
    public class BackendConfig
    {
        /// <summary>Anfitrión para escritorio y Editor. Ver <c>HostEfectivo</c>.</summary>
        public string host;

        /// <summary>Anfitrión para el visor (Android), donde <c>127.0.0.1</c> es el propio
        /// dispositivo y nunca puede alcanzar al contenedor.</summary>
        public string hostRemoto;

        public int puerto;
        public string baseDeDatos;
        public string usuario;
        public string contrasena;

        /// <summary>Modo TLS de MySqlConnector: <c>None</c>, <c>Preferred</c>, <c>Required</c>,
        /// <c>VerifyCA</c> o <c>VerifyFull</c>. Se deja configurable porque es el único ajuste de
        /// la conexión que puede fallar solo en el visor: si la pila TLS de Android rechaza el
        /// certificado autofirmado que MySQL genera al inicializarse, bajar a <c>None</c> desde el
        /// fichero devuelve la telemetría sin recompilar. Ver la nota de seguridad de
        /// <see cref="MySqlSensorPollingService.CadenaDeConexionSegura"/>.</summary>
        public string sslMode;

        /// <summary>Segundos entre sondeos. Configurable para poder ralentizarlo si la conexión
        /// móvil de la sala resulta ser mala, sin tocar el código.</summary>
        public float segundosEntreSondeos;
    }

    /// <summary>
    /// Localiza y aplica el fichero <c>backend.json</c>. Es estático y sin estado salvo la traza
    /// del último origen, porque el middleware se crea una sola vez por sesión
    /// (<see cref="SensorIntegrationBootstrap"/>) y no hay ningún caso en el que convenga releerlo
    /// en caliente: cambiar de servidor a mitad de una sesión dejaría marcas de agua de un
    /// servidor aplicadas contra otro.
    /// </summary>
    public static class BackendConfigLoader
    {
        public const string NombreFichero = "backend.json";

        /// <summary>Descripción legible de de dónde salió la configuración vigente, para el
        /// registro y para la comprobación previa a la defensa.</summary>
        public static string UltimoOrigen { get; private set; } = "valores compilados";

        /// <summary>
        /// Rutas donde se busca el fichero, en orden de preferencia.
        ///
        /// El orden no es arbitrario: primero el sitio que el usuario controla con un explorador
        /// de ficheros (la carpeta del proyecto en el Editor, la del ejecutable en escritorio), y
        /// después el directorio persistente de la aplicación, que en Android es el único
        /// escribible sin permisos especiales
        /// (<c>/sdcard/Android/data/&lt;identificador&gt;/files</c>) y por tanto el único destino
        /// posible de un <c>adb push</c>.
        /// </summary>
        public static List<string> RutasCandidatas()
        {
            var rutas = new List<string>();

#if UNITY_EDITOR
            // Raíz del proyecto, hermana de Assets/. Fuera de Assets/ a propósito: dentro se
            // importaría como asset y acabaría dentro de la compilación.
            string raizProyecto = Directory.GetParent(Application.dataPath)?.FullName;
            if (!string.IsNullOrEmpty(raizProyecto))
                rutas.Add(Path.Combine(raizProyecto, NombreFichero));
#elif !UNITY_ANDROID
            // Compilación de escritorio: Application.dataPath es <Build>/<Producto>_Data, así que
            // su padre es la carpeta donde está el ejecutable, junto al que se espera el fichero.
            string carpetaEjecutable = Directory.GetParent(Application.dataPath)?.FullName;
            if (!string.IsNullOrEmpty(carpetaEjecutable))
                rutas.Add(Path.Combine(carpetaEjecutable, NombreFichero));
#endif

            rutas.Add(Path.Combine(Application.persistentDataPath, NombreFichero));
            return rutas;
        }

        /// <summary>
        /// Vuelca sobre <paramref name="destino"/> lo que diga el primer fichero encontrado.
        /// Devuelve si se ha aplicado alguno.
        ///
        /// Ningún camino de salida es mudo: se registra el fichero usado, o la lista de sitios
        /// donde se ha mirado sin encontrarlo. Un middleware que se conecta al sitio equivocado en
        /// silencio es indistinguible de una base de datos vacía, y esa confusión ya costó una
        /// sesión de diagnóstico el 14-08.
        /// </summary>
        public static bool Aplicar(BackendConfig destino)
        {
            var candidatas = RutasCandidatas();

            foreach (string ruta in candidatas)
            {
                if (!File.Exists(ruta)) continue;

                try
                {
                    string json = File.ReadAllText(ruta);
                    JsonUtility.FromJsonOverwrite(json, destino);
                    UltimoOrigen = ruta;
                    Debug.LogWarning($"[DigitalTwin][IoT] Configuración de backend leída de {ruta}.");
                    return true;
                }
                catch (Exception ex)
                {
                    // Un fichero presente pero ilegible es peor que ausente: el usuario cree que
                    // ha configurado el destino y la aplicación usa otro. Se avisa con el error
                    // exacto (una coma de más en el JSON es el fallo típico) y se sigue mirando.
                    Debug.LogError($"[DigitalTwin][IoT] {ruta} existe pero no se ha podido aplicar: " +
                                   $"{ex.GetType().Name}: {ex.Message}. Se ignora este fichero y se " +
                                   "continúa con el resto de rutas candidatas.");
                }
            }

            UltimoOrigen = "valores compilados";
            Debug.LogWarning("[DigitalTwin][IoT] No hay " + NombreFichero + " en ninguna de estas rutas: " +
                             string.Join(" | ", candidatas) + ". Se usan los valores compilados.");
            return false;
        }
    }
}
