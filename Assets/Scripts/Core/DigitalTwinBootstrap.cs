using System.Collections;
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
            if (_initialized)
            {
                Debug.LogWarning("[DigitalTwin] El gemelo de escritorio ya estaba inicializado en " +
                                 "este proceso; no se monta de nuevo.");
                return;
            }

            // Fase 5: en la escena de Realidad Mixta manda MRDigitalTwinBootstrap. Ambos se
            // autoejecutan al cargar cualquier escena, así que sin esta comprobación el modo
            // escritorio montaría aquí su tour y su cámara, pisando al del visor.
            //
            // La retirada se anuncia a propósito: en el registro del visor del 17-08 la traza de
            // entrada aparecía y después nada más de este bootstrap, y desde el registro no se
            // podía distinguir «se retiró porque la escena es de RA» de «murió sin decir por qué».
            if (DigitalTwin.MR.MRDigitalTwinBootstrap.EsEscenaMR())
            {
                Debug.LogWarning("[DigitalTwin] Escena de Realidad Aumentada: el punto de entrada " +
                                 "de escritorio se retira y manda MRDigitalTwinBootstrap.");
                return;
            }

            if (Camera.main == null)
            {
                Debug.LogWarning("[DigitalTwin] No se ha encontrado ninguna cámara con tag MainCamera en la escena; " +
                                  "el bootstrap del gemelo digital no se ejecuta.");
                return;
            }

            // EL MONTAJE ESTÁ REPARTIDO ENTRE FOTOGRAMAS, IGUAL QUE EN EL VISOR, Y POR LA
            // MISMA RAZÓN DE FONDO AUNQUE SIN LA CONSECUENCIA FÍSICA. En escritorio no hay
            // problema vestibular: el monitor no se mueve con la cabeza, así que una parada del
            // bucle no produce malestar, solo una aplicación congelada. Lo que sí se conserva es
            // el valor de que el usuario sepa qué está ocurriendo, y el hecho de que el trabajo
            // es exactamente el mismo código: índice del modelo, 351 MeshCollider, ficha de
            // activos y middleware de sensores. Compartir el reparto y la pantalla de carga con
            // el visor evita mantener dos arranques que envejecerían por separado.
            //
            // La pantalla de escritorio es un lienzo sobre la pantalla, no un panel flotante:
            // las restricciones de confort del visor (nada fijado a la cabeza, holgura en el
            // seguimiento) no tienen sentido aquí y aplicarlas sería peor interfaz.
            PantallaDeCargaEscritorio.Abrir();
            ProgresoDeArranque.Comenzar("arranque escritorio", FasesDeEscritorio());

            var arranqueGo = new GameObject("~ArranqueEscritorio");
            Object.DontDestroyOnLoad(arranqueGo);
            arranqueGo.AddComponent<ArranqueEscritorio>().Iniciar();

            _initialized = true;
        }

        /// <summary>
        /// Fases del arranque de escritorio.
        ///
        /// LOS PESOS SON PRESTADOS Y ESO HAY QUE DECIRLO. Están tomados de los tiempos medidos
        /// en el visor el 2026-08-18, porque el código que se ejecuta es el mismo salvo la capa
        /// de Realidad Aumentada; de la versión de escritorio no hay todavía ninguna medición
        /// equivalente, porque no se vuelca un registro de la build de Windows. La instrumentación
        /// de <see cref="ProgresoDeArranque"/> la produce en la primera ejecución, y entonces
        /// estos pesos se recalibran. Mientras tanto, un peso prestado deforma el ritmo de la
        /// barra pero no su significado.
        /// </summary>
        private static FaseDeArranque[] FasesDeEscritorio()
        {
            return new[]
            {
                new FaseDeArranque("indice", "Leyendo el modelo del edificio", 5f),
                new FaseDeArranque("colisionadores", "Preparando la geometría", 75f),
                new FaseDeArranque("navegacion", "Montando el recorrido", 60f),
                new FaseDeArranque("panel", "Preparando la ficha de activos", 130f),
                new FaseDeArranque("sensores", "Conectando con los sensores", 70f),
                new FaseDeArranque("ajustes", "Preparando el menú de ajustes", 20f),
            };
        }

        /// <summary>
        /// Montaje de la versión de escritorio, repartido entre fotogramas. El contenido y el
        /// ORDEN son los que tenía el arranque síncrono; lo único añadido son las cesiones de
        /// fotograma y la declaración de fase. En particular, el menú de configuración se sigue
        /// construyendo el último, porque necesita localizar TourCameraLook ya creado y se
        /// registra en ClickRouter por encima del resto de la interfaz.
        /// </summary>
        internal class ArranqueEscritorio : MonoBehaviour
        {
            public void Iniciar()
            {
                StartCoroutine(Secuencia());
            }

            private IEnumerator Secuencia()
            {
                ProgresoDeArranque.EntrarEnFase("indice");
                var index = SceneModelIndex.Build();
                yield return null;

                ProgresoDeArranque.EntrarEnFase("colisionadores");
                yield return StartCoroutine(ColliderBootstrapper.SetupIncremental(
                    index, ProgresoDeArranque.ProgresoDeFase));

                ProgresoDeArranque.EntrarEnFase("navegacion");
                var canvas = RuntimeUIFactory.CreateRootCanvas("DigitalTwinCanvas");

                var camGo = Camera.main.gameObject;
                var tour = camGo.AddComponent<TourNavigationManager>();
                camGo.AddComponent<TourCameraLook>();
                tour.Initialize(index, canvas);
                yield return null;

                // Menú de acceso directo a zonas. Se engancha al gestor del tour, que es quien
                // conoce las salas del modelo y sabe viajar a ellas.
                var menuGo = new GameObject("~MenuZonas");
                Object.DontDestroyOnLoad(menuGo);
                menuGo.AddComponent<Navigation.RoomMenuController>().Initialize(tour, canvas);
                yield return null;

                ProgresoDeArranque.EntrarEnFase("panel");
                var panelGo = new GameObject("~MetadataPanel");
                Object.DontDestroyOnLoad(panelGo);
                var panel = panelGo.AddComponent<MetadataPanelController>();
                panel.Initialize(canvas);
                yield return null;

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
                yield return null;

                // Iluminacion solar por georreferenciacion del IFC. Se monta desactivada: el
                // modelo no tiene luz artificial, asi que con la hora real de noche el interior
                // queda a oscuras. Se activa desde el menu de ajustes.
                Visual.SolarLightingController.Crear();
                yield return null;

                ProgresoDeArranque.EntrarEnFase("sensores");
                IoT.SensorIntegrationBootstrap.TryAttach(index, panel);
                yield return null;

                // Menú de pausa y configuración (Escape). Se construye el último a propósito:
                // necesita localizar TourCameraLook para congelar el giro de cámara mientras está
                // abierto, y se registra en ClickRouter con una prioridad por encima del resto de
                // la interfaz. En un visor no se construye: ver la nota de la propia clase.
                ProgresoDeArranque.EntrarEnFase("ajustes");
                var ajustesGo = new GameObject("~MenuConfiguracion");
                Object.DontDestroyOnLoad(ajustesGo);
                ajustesGo.AddComponent<UI.SettingsMenuController>().Initialize(canvas);

                ProgresoDeArranque.Terminar();
                if (PantallaDeCargaEscritorio.Instancia != null)
                    PantallaDeCargaEscritorio.Instancia.Cerrar();

                Debug.Log("[DigitalTwin] Bootstrap completo: navegación por puntos + selección + panel de metadatos activos.");
                Destroy(gameObject);
            }
        }
    }
}
