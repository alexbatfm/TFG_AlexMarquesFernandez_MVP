using DigitalTwin.Core;
using DigitalTwin.Metadata;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace DigitalTwin.MR
{
    /// <summary>
    /// Fase 5 - Punto de entrada del modo de Realidad Mixta, equivalente a
    /// <see cref="DigitalTwinBootstrap"/> pero para la escena del visor.
    ///
    /// Se mantiene el mismo criterio que en las fases anteriores: todo se construye por código
    /// al arrancar, sin colocar objetos a mano en el fichero de escena.
    ///
    /// Diferencia clave con el modo escritorio: allí la cámara puede indexar y recorrer el
    /// modelo nada más cargar la escena, porque el modelo ya está donde tiene que estar. Aquí
    /// el modelo no tiene una posición válida hasta que el anclaje espacial queda establecido,
    /// así que el orden es: indexar -> esperar anclaje -> colocar modelo -> activar interacción.
    ///
    /// Convivencia con el modo escritorio: ambos bootstraps se autoejecutan al cargar
    /// cualquier escena, así que cada uno comprueba si le toca actuar según el nombre de la
    /// escena activa (ver <see cref="NombreEscenaMR"/>). De ese modo MainScene sigue
    /// comportándose exactamente igual que antes de existir la Fase 5.
    /// </summary>
    public static class MRDigitalTwinBootstrap
    {
        /// <summary>
        /// Nombre de la escena de Realidad Mixta. Debe coincidir con el fichero
        /// Assets/Scenes/MRScene.unity que se creará en el Editor.
        /// </summary>
        public const string NombreEscenaMR = "MRScene";

        private static bool _initialized;

        public static bool EsEscenaMR()
        {
            return SceneManager.GetActiveScene().name == NombreEscenaMR;
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            if (_initialized || !EsEscenaMR()) return;

            if (Camera.main == null)
            {
                Debug.LogWarning("[DigitalTwin][MR] No hay cámara con tag MainCamera en la escena; " +
                                 "el XR Origin debe tener una. No se inicializa el modo MR.");
                return;
            }

            var index = SceneModelIndex.Build();
            ColliderBootstrapper.Setup(index);

            var anclajeGo = new GameObject("~MRAnchorService");
            Object.DontDestroyOnLoad(anclajeGo);
            var anclaje = anclajeGo.AddComponent<MRAnchorService>();

            var binder = anclajeGo.AddComponent<ModelAnchorBinder>();
            binder.Initialize(index, anclaje);

            // El panel de metadatos y el middleware IoT se reutilizan tal cual: ninguno de los
            // dos depende de cómo se renderice la escena. El panel se construye en el mismo
            // canvas de siempre; su adaptación a world-space (más natural en un visor) queda
            // pendiente de poder probarla con el dispositivo puesto.
            var canvas = DigitalTwin.UI.RuntimeUIFactory.CreateRootCanvas("DigitalTwinCanvasMR");

            var panelGo = new GameObject("~MetadataPanelMR");
            Object.DontDestroyOnLoad(panelGo);
            var panel = panelGo.AddComponent<MetadataPanelController>();
            panel.Initialize(canvas);

            IoT.SensorIntegrationBootstrap.TryAttach(index, panel);

            anclaje.OnEstadoCambiado += estado =>
                Debug.Log($"[DigitalTwin][MR] Estado del anclaje: {estado}.");

            _initialized = true;
            Debug.Log("[DigitalTwin][MR] Bootstrap de Realidad Mixta completo. " +
                      "A la espera del anclaje espacial para colocar el modelo.");
        }
    }
}
