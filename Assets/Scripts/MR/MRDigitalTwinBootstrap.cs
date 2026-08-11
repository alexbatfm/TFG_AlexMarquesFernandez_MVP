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
            // Traza incondicional, antes de cualquier salida temprana. Sin ella, un arranque que no
            // se produce es indistinguible de uno que se produce y decide no hacer nada: en la
            // primera build para el visor no aparecio ningun mensaje del proyecto en el registro, y
            // no habia forma de saber si el metodo no se habia ejecutado, si la escena no era la
            // esperada, o si fallaba mas adelante. Este mensaje responde a las tres preguntas.
            Debug.LogWarning($"[DigitalTwin][AR] Punto de entrada alcanzado. Escena activa: " +
                      $"'{SceneManager.GetActiveScene().name}'. Se esperaba '{NombreEscenaMR}' " +
                      $"para inicializar el modo de Realidad Aumentada.");

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

            // El panel de metadatos y el middleware IoT reutilizan la misma implementación que en
            // escritorio; lo único que cambia es dónde vive el panel. Aquí el canvas es de tipo
            // world-space: en un visor, una interfaz pegada a la cara resulta incómoda y rompe la
            // sensación de estar dentro del edificio.
            var canvas = DigitalTwin.UI.RuntimeUIFactory.CreateWorldCanvas("DigitalTwinCanvasMR");

            var panelGo = new GameObject("~MetadataPanelMR");
            Object.DontDestroyOnLoad(panelGo);
            var panel = panelGo.AddComponent<MetadataPanelController>();
            panel.Initialize(canvas);
            // Fondo translúcido: da sensación de espacio sin restar legibilidad al texto, que
            // sigue a opacidad completa (ver SetOpacidadFondo).
            panel.SetOpacidadFondo(0.7f);

            // Identificación del elemento seleccionado, por triple vía: caja de aristas y tinte
            // sobre el objeto, panel colocado a su lado, y línea que une panel y objeto. Cada
            // mecanismo cubre un caso en el que los otros fallan (objeto tapado, elementos
            // repetidos cerca, objeto lejano).
            var resaltadoGo = new GameObject("~SelectionHighlighterMR");
            Object.DontDestroyOnLoad(resaltadoGo);
            var resaltador = resaltadoGo.AddComponent<SelectionHighlighter>();

            var colocadorGo = new GameObject("~WorldPanelPlacer");
            Object.DontDestroyOnLoad(colocadorGo);
            var colocador = colocadorGo.AddComponent<WorldPanelPlacer>();
            colocador.Initialize(canvas);

            panel.OnElementShown += meta =>
            {
                Transform t = meta != null ? meta.transform : null;
                resaltador.Resaltar(t);
                colocador.Seguir(t);
            };
            panel.OnPanelHidden += () =>
            {
                resaltador.Limpiar();
                colocador.Seguir(null);
            };

            IoT.SensorIntegrationBootstrap.TryAttach(index, panel);

            // --- Mandos e interaccion ---------------------------------------------------------
            // Sin esto la escena era solo contemplativa: el modelo se veia y se podia caminar
            // alrededor, pero no habia forma de senalar nada, de modo que el panel de metadatos
            // existia sin que nada pudiera pedirle que mostrase un elemento.
            //
            // Los anclajes de los mandos cuelgan del desplazamiento de camara, no de la raiz de la
            // escena: las poses que entrega el sistema estan en el espacio del origen de realidad
            // extendida, y colgarlas de la raiz haria que los mandos se despegaran de las manos en
            // cuanto el origen se moviera, por ejemplo al desplazarse a un punto de navegacion.
            Transform desplazamientoCamara = Camera.main.transform.parent;
            Transform origenXR = desplazamientoCamara != null ? desplazamientoCamara.parent : null;

            if (desplazamientoCamara == null || origenXR == null)
            {
                Debug.LogWarning("[DigitalTwin][AR] La camara no cuelga de la jerarquia esperada " +
                                 "(origen de realidad extendida > desplazamiento de camara > camara). " +
                                 "No se crean los mandos: revisa el rig de la escena.");
            }
            else
            {
                var rigGo = new GameObject("~MandosAR");
                rigGo.transform.SetParent(desplazamientoCamara, false);
                var rig = rigGo.AddComponent<MRControllerRig>();
                rig.Initialize(desplazamientoCamara);

                var interaccionGo = new GameObject("~InteraccionAR");
                interaccionGo.transform.SetParent(desplazamientoCamara, false);
                var interaccion = interaccionGo.AddComponent<MRInteractionController>();
                interaccion.Initialize(rig, panel, origenXR);
            }

            anclaje.OnEstadoCambiado += estado =>
                Debug.Log($"[DigitalTwin][MR] Estado del anclaje: {estado}.");

            _initialized = true;
            Debug.LogWarning("[DigitalTwin][AR] Bootstrap de Realidad Aumentada completo. " +
                      "A la espera del anclaje espacial para colocar el modelo.");
        }
    }
}
