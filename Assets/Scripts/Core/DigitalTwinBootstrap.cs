using DigitalTwin.Metadata;
using DigitalTwin.Navigation;
using DigitalTwin.UI;
using UnityEngine;

namespace DigitalTwin.Core
{
    /// <summary>
    /// Punto de entrada único de todos los sistemas del gemelo digital (navegación, selección,
    /// panel de metadatos y, desde la Fase 3/4, el middleware IoT). Se ejecuta automáticamente
    /// vía [RuntimeInitializeOnLoadMethod] al cargar la escena, sin necesidad de colocar ningún
    /// GameObject a mano en MainScene.unity: así no hace falta editar el archivo de escena
    /// (evita el riesgo de corromper 48k líneas de YAML) y el sistema funciona en cualquier
    /// escena que tenga una Main Camera y el modelo IFC ya importado con sus metadatos.
    /// </summary>
    public static class DigitalTwinBootstrap
    {
        private static bool _initialized;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Bootstrap()
        {
            // Los managers se marcan DontDestroyOnLoad; esta guarda evita duplicarlos si la
            // escena se recarga durante la misma ejecución.
            if (_initialized) return;

            if (Camera.main == null)
            {
                Debug.LogWarning("[DigitalTwin] No se ha encontrado ninguna cámara con tag MainCamera en la escena; " +
                                  "el bootstrap del gemelo digital no se ejecuta.");
                return;
            }

            var index = SceneModelIndex.Build();
            ColliderBootstrapper.Setup(index);

            var canvas = RuntimeUIFactory.CreateRootCanvas("DigitalTwinCanvas");

            var camGo = Camera.main.gameObject;
            var tour = camGo.AddComponent<TourNavigationManager>();
            camGo.AddComponent<TourCameraLook>();
            tour.Initialize(index, canvas);

            var panelGo = new GameObject("~MetadataPanel");
            Object.DontDestroyOnLoad(panelGo);
            var panel = panelGo.AddComponent<MetadataPanelController>();
            panel.Initialize(canvas);

            var selectorGo = new GameObject("~ElementSelector");
            Object.DontDestroyOnLoad(selectorGo);
            var selector = selectorGo.AddComponent<ElementSelector>();
            selector.Initialize(panel, tour);

            IoT.SensorIntegrationBootstrap.TryAttach(index, panel);

            _initialized = true;
            Debug.Log("[DigitalTwin] Bootstrap completo: navegación por puntos + selección + panel de metadatos activos.");
        }
    }
}
