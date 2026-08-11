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
            // Traza incondicional, antes de cualquier salida temprana. Su ausencia en el registro
            // significa que el metodo no se ha ejecutado en absoluto --- lo que apunta al recorte
            // del enlazador, no a la logica --- y su presencia indica qué escena esta activa. Sin
            // ella, "no se inicializa" y "se inicializa y decide no actuar" son indistinguibles.
            Debug.LogWarning("[DigitalTwin] Punto de entrada de escritorio alcanzado. Escena activa: " +
                      $"'{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}'.");

            // Los managers se marcan DontDestroyOnLoad; esta guarda evita duplicarlos si la
            // escena se recarga durante la misma ejecución.
            if (_initialized) return;

            // Fase 5: en la escena de Realidad Mixta manda MRDigitalTwinBootstrap. Ambos se
            // autoejecutan al cargar cualquier escena, así que sin esta comprobación el modo
            // escritorio montaría aquí su tour y su cámara, pisando al del visor.
            if (DigitalTwin.MR.MRDigitalTwinBootstrap.EsEscenaMR()) return;

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

            // Menú de acceso directo a zonas. Se engancha al gestor del tour, que es quien
            // conoce las salas del modelo y sabe viajar a ellas.
            var menuGo = new GameObject("~MenuZonas");
            Object.DontDestroyOnLoad(menuGo);
            menuGo.AddComponent<Navigation.RoomMenuController>().Initialize(tour, canvas);

            var panelGo = new GameObject("~MetadataPanel");
            Object.DontDestroyOnLoad(panelGo);
            var panel = panelGo.AddComponent<MetadataPanelController>();
            panel.Initialize(canvas);

            var selectorGo = new GameObject("~ElementSelector");
            Object.DontDestroyOnLoad(selectorGo);
            var selector = selectorGo.AddComponent<ElementSelector>();
            selector.Initialize(panel, tour);

            // Resaltado del elemento seleccionado. Se engancha a los eventos que el panel ya
            // publicaba, así que ni ElementSelector ni el propio panel necesitan enterarse.
            var resaltadoGo = new GameObject("~SelectionHighlighter");
            Object.DontDestroyOnLoad(resaltadoGo);
            var resaltador = resaltadoGo.AddComponent<Metadata.SelectionHighlighter>();
            panel.OnElementShown += meta => resaltador.Resaltar(meta != null ? meta.transform : null);
            panel.OnPanelHidden += resaltador.Limpiar;

            IoT.SensorIntegrationBootstrap.TryAttach(index, panel);

            // Menú de pausa y configuración (Escape). Se construye el último a propósito: necesita
            // localizar TourCameraLook para congelar el giro de cámara mientras está abierto, y se
            // registra en ClickRouter con una prioridad por encima del resto de la interfaz.
            // En un visor no se construye: ver la nota de la propia clase.
            var ajustesGo = new GameObject("~MenuConfiguracion");
            Object.DontDestroyOnLoad(ajustesGo);
            ajustesGo.AddComponent<UI.SettingsMenuController>().Initialize(canvas);

            _initialized = true;
            Debug.Log("[DigitalTwin] Bootstrap completo: navegación por puntos + selección + panel de metadatos activos.");
        }
    }
}
