using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace DigitalTwin.EditorTools
{
    /// <summary>
    /// Fija la identidad con la que la aplicación aparece instalada en el visor: identificador de
    /// paquete, nombre visible, empresa, versión y número de compilación.
    ///
    /// Qué problema resuelve. Hasta el 15-08-2026 el proyecto conservaba el identificador de la
    /// plantilla de Unity, <c>com.UnityTechnologies.com.unity.template.urpblank</c>, y el nombre
    /// del producto era el de la carpeta del repositorio. Un identificador de plantilla tiene dos
    /// consecuencias que no son cosméticas: cualquier otro proyecto creado a partir de la misma
    /// plantilla se instala encima de este —el sistema Android identifica las aplicaciones por ese
    /// texto, no por su nombre—, y en la biblioteca del visor la entrada aparece con un nombre que
    /// no dice qué es. El identificador elegido sigue el convenio de DNS invertido con el dominio
    /// de la institución, que es lo que Android espera y lo que hace improbable la colisión.
    ///
    /// Por qué es una opción de menú y no se aplica sola, al revés que
    /// <see cref="ConfigurarSplashUnizar"/>. Aquí sí hay una decisión en cada ejecución: el número
    /// de compilación se incrementa, y hacerlo en cada recarga de dominio lo dispararía a cientos
    /// sin que ninguna de esas cifras corresponda a un paquete real. El número de compilación es
    /// además lo único que distingue dos <c>.apk</c> con la misma versión, y sirve para saber, con
    /// el visor en la mano, cuál de los dos está instalado.
    ///
    /// Lo que esta herramienta no hace, y hay que hacer a mano una sola vez: el icono de la
    /// aplicación (Player Settings, sección Icon) y la casilla de compilación de desarrollo, que
    /// en Unity 6 vive en el perfil de compilación y no en los ajustes del reproductor. Ambos
    /// pasos están en <c>TFG/docs/roadmap/DESPLIEGUE-nube-y-publicacion.md</c>. Se dejan fuera a
    /// propósito: las interfaces de programación de iconos y de perfiles han cambiado entre
    /// versiones del editor, y una herramienta que no compila bloquea todo el proyecto, no solo a
    /// sí misma.
    /// </summary>
    public static class ConfigurarPublicacion
    {
        private const string Identificador = "es.unizar.eupt.gemelodigital";
        private const string NombreVisible = "Gemelo Digital BIM";
        private const string Empresa = "Universidad de Zaragoza";

        /// <summary>Versión visible para el usuario. Se sube a mano cuando cambia el alcance de lo
        /// entregado, no en cada compilación; para eso está el número de compilación.</summary>
        private const string Version = "1.0";

        [MenuItem("Tools/TFG/Preparar identidad de publicación")]
        public static void Aplicar()
        {
            PlayerSettings.companyName = Empresa;
            PlayerSettings.productName = NombreVisible;
            PlayerSettings.bundleVersion = Version;
            PlayerSettings.SetApplicationIdentifier(NamedBuildTarget.Android, Identificador);

            // Permiso de red declarado de forma explícita. Con el ajuste automático, Unity lo
            // añade solo si detecta que el código usa sus propias clases de red; la conexión de
            // este proyecto la abre MySqlConnector desde un ensamblado externo, un caso que esa
            // detección no tiene por qué cubrir. Sin el permiso, la conexión falla en el visor con
            // un error de red corriente, indistinguible de un servidor apagado.
            PlayerSettings.Android.forceInternetPermission = true;

            int anterior = PlayerSettings.Android.bundleVersionCode;
            PlayerSettings.Android.bundleVersionCode = anterior + 1;

            AssetDatabase.SaveAssets();

            Debug.LogWarning(
                $"[Publicación] Identidad aplicada: {NombreVisible} ({Identificador}), versión {Version}, " +
                $"compilación {anterior} -> {PlayerSettings.Android.bundleVersionCode}, permiso de red forzado. " +
                "Quedan a mano el icono (Player Settings > Icon) y la casilla Development Build del perfil " +
                "de compilación.");
        }

        /// <summary>
        /// Vuelca la identidad vigente sin modificar nada. Sirve para comprobar, antes de compilar
        /// la versión que se lleva a la defensa, que se está compilando lo que se cree: los
        /// ajustes del reproductor se pierden de vista con facilidad al cambiar de plataforma
        /// activa, porque el identificador es un valor por plataforma.
        /// </summary>
        [MenuItem("Tools/TFG/Comprobar identidad de publicación")]
        public static void Comprobar()
        {
            string identificadorActual = PlayerSettings.GetApplicationIdentifier(NamedBuildTarget.Android);
            bool correcto = identificadorActual == Identificador;

            string resumen =
                $"[Publicación] Android: identificador '{identificadorActual}', producto " +
                $"'{PlayerSettings.productName}', empresa '{PlayerSettings.companyName}', versión " +
                $"{PlayerSettings.bundleVersion}, compilación {PlayerSettings.Android.bundleVersionCode}, " +
                $"permiso de red forzado: {PlayerSettings.Android.forceInternetPermission}.";

            if (correcto) Debug.LogWarning(resumen);
            else Debug.LogError(resumen + $" El identificador no es el previsto ({Identificador}): " +
                                "ejecuta Tools > TFG > Preparar identidad de publicación.");
        }
    }
}
