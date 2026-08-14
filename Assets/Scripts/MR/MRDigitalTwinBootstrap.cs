using System.Collections;
using System.Collections.Generic;
using DigitalTwin.Core;
using DigitalTwin.Metadata;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.XR;

namespace DigitalTwin.MR
{
    /// <summary>
    /// Fase 5 - Punto de entrada del modo de Realidad Aumentada, equivalente a
    /// <see cref="DigitalTwinBootstrap"/> pero para la escena del visor.
    ///
    /// Se mantiene el mismo criterio que en las fases anteriores: todo se construye por código
    /// al arrancar, sin colocar objetos a mano en el fichero de escena.
    ///
    /// EL ARRANQUE ES EN DOS ETAPAS desde el 2026-08-13. La etapa A prepara lo mínimo para poder
    /// preguntar: índice del modelo, colisionadores, transparencia y mandos. Con la transparencia
    /// ya activa se muestra el selector de modo (<see cref="MRModeSelector"/>): modo anclado
    /// —en obra, modelo superpuesto al edificio real, desplazamiento andando— o navegación por
    /// nodos —en oficina, revisión remota—. Solo entonces la etapa B monta el gemelo digital
    /// según el modo elegido. No son dos ajustes del mismo programa sino dos programas (cambian
    /// entrada, fondo, papel de la geometría y colisionadores), por eso la elección es al
    /// arrancar y no un conmutador en caliente; ver docs/roadmap/DISENO-modo-anclado.md.
    ///
    /// SE DIFIERE DENTRO DE ARScene; NO HAY ESCENA NUEVA. Una escena de menú obligaría a ampliar
    /// el filtro por nombre de escena y a mantenerla en Build Settings, y ese filtro es
    /// exactamente el mecanismo que ya costó un bloque de trabajo cuando MRScene pasó a llamarse
    /// ARScene y la constante quedó sin actualizar. El coste asumido es que el modelo se carga
    /// antes de preguntar; la elección aparece unos segundos después de ponerse el visor, una
    /// vez por sesión.
    ///
    /// Convivencia con el modo escritorio: ambos bootstraps se autoejecutan al cargar
    /// cualquier escena, así que cada uno comprueba si le toca actuar según el nombre de la
    /// escena activa (ver <see cref="NombreEscenaMR"/>). De ese modo MainScene sigue
    /// comportándose exactamente igual que antes de existir la Fase 5.
    /// </summary>
    public static class MRDigitalTwinBootstrap
    {
        /// <summary>
        /// Nombre de la escena de Realidad Aumentada.
        /// </summary>
        public const string NombreEscenaMR = "ARScene";

        /// <summary>
        /// Nombres aceptados, además del anterior.
        ///
        /// Existe esta lista por una razón concreta: la escena se llamó primero <c>MRScene</c> y
        /// pasó a <c>ARScene</c> al fijarse la terminología del trabajo. Como la comprobación era
        /// una comparación exacta contra una constante, el simple renombrado desactivó en silencio
        /// todo el arranque de Realidad Aumentada --- y, peor, dejó que corriera en su lugar el de
        /// escritorio, que montó su recorrido y su control de cámara de ratón dentro del visor.
        /// No hubo ningún error: solo una aplicación que se comportaba de forma extraña.
        ///
        /// Aceptar varios nombres evita que un cambio de nomenclatura vuelva a romper el arranque.
        /// </summary>
        private static readonly string[] NombresAceptados = { "ARScene", "MRScene" };

        private static bool _initialized;
        private static bool _gemeloMontado;

        /// <summary>Raíz del modelo apagada mientras el selector de modo está en pantalla;
        /// MontarGemelo la reactiva. Null si no se apagó (vía de emergencia o raíz no hallada).</summary>
        private static GameObject _raizModeloApagadaDuranteSelector;

        public static bool EsEscenaMR()
        {
            string activa = SceneManager.GetActiveScene().name;
            foreach (var nombre in NombresAceptados)
                if (activa == nombre) return true;
            return false;
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

            // --- Etapa A: lo imprescindible para poder preguntar -------------------------------

            // El monitor de rendimiento nace ANTES que todo lo demás: en la prueba del 14-08 la
            // única medición arrancaba con el gemelo montado y la fase del selector —donde
            // también se notaban tirones— quedó sin números.
            MRPerfMonitor.Crear();

            var index = SceneModelIndex.Build();
            ColliderBootstrapper.Setup(index);

            // Resumen del índice a nivel de aviso: la línea detallada de SceneModelIndex es un
            // mensaje informativo y las compilaciones que no son de desarrollo lo filtran del
            // registro del dispositivo.
            Debug.LogWarning($"[DigitalTwin][AR] Indice del modelo: {index.AllElements.Count} " +
                             $"elementos, {index.NavPoints.Count} puntos de navegacion, " +
                             $"{index.Sensors.Count} sensores.");

            // La transparencia se prepara antes que el resto. El orden importa poco para el
            // resultado, pero mucho para diagnosticar: si algo falla al crear la capa, el aviso
            // aparece antes que las trazas de montaje y no queda sepultado.
            MRPassthroughController.Crear();

            // Los anclajes de los mandos cuelgan del desplazamiento de camara, no de la raiz de la
            // escena: las poses que entrega el sistema estan en el espacio del origen de realidad
            // extendida, y colgarlas de la raiz haria que los mandos se despegaran de las manos en
            // cuanto el origen se moviera, por ejemplo al desplazarse a un punto de navegacion.
            Transform desplazamientoCamara = Camera.main.transform.parent;
            Transform origenXR = desplazamientoCamara != null ? desplazamientoCamara.parent : null;

            if (desplazamientoCamara == null || origenXR == null)
            {
                // Sin la jerarquía del rig no hay mandos, y sin mandos no se puede elegir modo.
                // Antes que dejar al usuario ante un selector inoperante, se monta directamente
                // la navegación por nodos —el modo que funciona sin anclaje— dejando constancia.
                Debug.LogError("[DigitalTwin][AR] La camara no cuelga de la jerarquia esperada " +
                               "(origen de realidad extendida > desplazamiento de camara > camara). " +
                               "Sin mandos no hay selector de modo: se monta navegacion por nodos " +
                               "directamente. Revisa el rig de la escena.");
                MontarGemelo(ModoAR.NavegacionPorNodos, index, null, null, null);
                _initialized = true;
                return;
            }

            // Seguimiento a nivel de suelo, verificado y nunca supuesto: la escena lo pide
            // (XROrigin en modo Floor con desplazamiento cero desde el 15-08), pero el modo
            // efectivo lo decide el runtime y aquí se comprueba, se registra y, si no se
            // consigue, se compensa con una degradación declarada.
            AsegurarSeguimientoANivelDeSuelo(origenXR);

            var rigGo = new GameObject("~MandosAR");
            rigGo.transform.SetParent(desplazamientoCamara, false);
            var rig = rigGo.AddComponent<MRControllerRig>();
            rig.Initialize(desplazamientoCamara);

            // Mientras el usuario elige modo, el gemelo no aporta nada y SÍ cuesta: el modelo
            // entero se estaba dibujando detrás de las tarjetas del selector (en estéreo y a
            // resolución de visor) sin que se viera más que el vídeo de la sala. Se apaga la
            // raíz completa y MontarGemelo la reactiva. La vía de emergencia de arriba (sin
            // rig) no pasa por aquí a propósito: monta directamente y no debe apagarse nada.
            var raizModelo = RaizDelModelo(index);
            if (raizModelo != null)
            {
                raizModelo.SetActive(false);
                _raizModeloApagadaDuranteSelector = raizModelo;
                Debug.LogWarning($"[DigitalTwin][AR] Raiz del modelo '{raizModelo.name}' " +
                                 "desactivada mientras el selector de modo este en pantalla.");
            }
            else
            {
                Debug.LogWarning("[DigitalTwin][AR] No se ha resuelto la raiz del modelo; se " +
                                 "deja el gemelo dibujandose durante el selector (solo cuesta " +
                                 "rendimiento, no funcionalidad).");
            }

            var arranqueGo = new GameObject("~ArranqueDiferidoAR");
            Object.DontDestroyOnLoad(arranqueGo);
            var secuenciador = arranqueGo.AddComponent<MRBootSequencer>();
            secuenciador.Iniciar(index, rig, desplazamientoCamara, origenXR);

            _initialized = true;
        }

        /// <summary>Altura de ojos con la que se degrada cuando no hay seguimiento a nivel de
        /// suelo (y con la que se prueba en el Editor): la altura de ojos de pie mediana de la
        /// población adulta, ~1,58 m (ANSUR II, tablas resumen del Ergonomics Center NCSU).</summary>
        private const float AlturaVistaSinSuelo = 1.58f;

        /// <summary>
        /// Fija y VERIFICA el modo de origen de seguimiento a nivel de suelo, con el resultado
        /// en el registro. Con origen de suelo, la altura de la cámara es la estatura real del
        /// usuario sobre el suelo de juego y el programa no la toca nunca (los viajes son
        /// horizontales). Dos degradaciones declaradas: en el Editor sin subsistema XR, el
        /// origen se eleva a una altura de ojos mediana para que el respaldo de ratón vea como
        /// una persona de pie; y si el dispositivo no admitiera el modo de suelo, se aplica la
        /// misma elevación —la estatura real no es conocible en ese modo— dejándolo dicho.
        /// </summary>
        private static void AsegurarSeguimientoANivelDeSuelo(Transform origenXR)
        {
            var subsistemas = new List<XRInputSubsystem>();
            SubsystemManager.GetSubsystems(subsistemas);

            if (subsistemas.Count == 0)
            {
#if UNITY_EDITOR
                origenXR.position += Vector3.up * AlturaVistaSinSuelo;
                Debug.LogWarning("[DigitalTwin][AR] Sin subsistema XR (modo Play del Editor): " +
                                 $"origen elevado {AlturaVistaSinSuelo:0.00} m para que el " +
                                 "respaldo de raton vea a altura de ojos de una persona de pie.");
#else
                Debug.LogWarning("[DigitalTwin][AR] Sin subsistema XR de entrada: no se puede " +
                                 "fijar el origen de seguimiento. La altura de la vista queda " +
                                 "en manos de la escena.");
#endif
                return;
            }

            foreach (var subsistema in subsistemas)
            {
                var soportados = subsistema.GetSupportedTrackingOriginModes();
                bool admiteSuelo = (soportados & TrackingOriginModeFlags.Floor) != 0;
                bool aplicado = admiteSuelo &&
                                subsistema.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
                var resuelto = subsistema.GetTrackingOriginMode();

                Debug.LogWarning($"[DigitalTwin][AR] Origen de seguimiento: soportados " +
                                 $"[{soportados}], solicitado Floor, aplicado {aplicado}, " +
                                 $"RESUELTO [{resuelto}]. Con Floor, la altura de la vista es " +
                                 "la estatura real del usuario y el programa no la escribe.");

                if (resuelto != TrackingOriginModeFlags.Floor)
                {
                    origenXR.position += Vector3.up * AlturaVistaSinSuelo;
                    Debug.LogWarning("[DigitalTwin][AR] El dispositivo NO ha quedado en origen " +
                                     $"de suelo: se eleva el origen {AlturaVistaSinSuelo:0.00} m " +
                                     "como altura de ojos mediana (degradacion declarada; la " +
                                     "estatura real no es conocible en este modo).");
                }
            }
        }

        /// <summary>
        /// Raíz del modelo importado, con la misma resolución que usa
        /// <see cref="ModelAnchorBinder"/>: subir por la jerarquía desde cualquier elemento con
        /// metadatos hasta el objeto más alto. No se codifica el nombre del objeto del .glb,
        /// que puede cambiar al reimportar.
        /// </summary>
        private static GameObject RaizDelModelo(SceneModelIndex index)
        {
            if (index == null || index.AllElements.Count == 0 || index.AllElements[0] == null)
                return null;
            Transform t = index.AllElements[0].transform;
            while (t.parent != null) t = t.parent;
            return t.gameObject;
        }

        /// <summary>
        /// Etapa B: montaje del gemelo digital según el modo elegido. Todo lo que en la versión
        /// de escritorio construye su bootstrap se construye aquí, más las piezas propias del
        /// visor. Se ejecuta una sola vez.
        /// </summary>
        internal static void MontarGemelo(ModoAR modo, SceneModelIndex index, MRControllerRig rig,
                                          Transform desplazamientoCamara, Transform origenXR)
        {
            if (_gemeloMontado)
            {
                Debug.LogWarning("[DigitalTwin][AR] MontarGemelo llamado dos veces; se ignora.");
                return;
            }
            _gemeloMontado = true;

            // Lo primero es devolver el modelo apagado durante el selector: todo lo que sigue
            // (oclusores, colliders de seleccion, sensores) da por hecho que la escena esta viva.
            if (_raizModeloApagadaDuranteSelector != null)
            {
                _raizModeloApagadaDuranteSelector.SetActive(true);
                Debug.LogWarning($"[DigitalTwin][AR] Raiz del modelo " +
                                 $"'{_raizModeloApagadaDuranteSelector.name}' reactivada para el montaje.");
                _raizModeloApagadaDuranteSelector = null;
            }

            if (MRPerfMonitor.Instancia != null)
                MRPerfMonitor.Instancia.FijarFase($"montado ({modo})");

            Debug.LogWarning($"[DigitalTwin][AR] Montaje del gemelo digital iniciado (modo {modo}).");

            // --- Anclaje espacial: SOLO en modo anclado ---------------------------------------
            // En navegación por nodos un anclaje persistido de una sesión anterior movería el
            // edificio entero bajo los pies del usuario a mitad de recorrido, que es exactamente
            // lo contrario de lo que ese modo promete (el modelo quieto y el usuario saltando
            // entre nodos).
            if (modo == ModoAR.Anclado)
            {
                var anclajeGo = new GameObject("~MRAnchorService");
                Object.DontDestroyOnLoad(anclajeGo);
                var anclaje = anclajeGo.AddComponent<MRAnchorService>();

                var binder = anclajeGo.AddComponent<ModelAnchorBinder>();
                binder.Initialize(index, anclaje);

                anclaje.OnEstadoCambiado += estado =>
                    Debug.LogWarning($"[DigitalTwin][MR] Estado del anclaje: {estado}.");
            }
            else
            {
                Debug.LogWarning("[DigitalTwin][AR] Anclaje espacial no aplicable en navegacion " +
                                 "por nodos: el modelo permanece en su pose de autor.");
            }

            // El panel de metadatos y el middleware IoT reutilizan la misma implementación que en
            // escritorio; lo único que cambia es dónde vive el panel. Aquí el canvas es de tipo
            // world-space: en un visor, una interfaz pegada a la cara resulta incómoda y rompe la
            // sensación de estar dentro del edificio.
            //
            // ANCHO DEFINITIVO: 1 m (decisión del 14-08, tras probar 0,58 y 0,70). Cada unidad
            // de maquetación pasa a valer 1,11 mm de mundo (900 px de lienzo), así que los
            // cuerpos actuales crecen un 43 % respecto al 0,70 probado y un 72 % respecto al
            // 0,58 original SIN tocar ningún tamaño de fuente; antes de subir más cuerpos hay
            // que comprobar en el visor si con esto basta (la sospecha es que el problema era
            // la nitidez del rasterizado, no el tamaño). La altura del panel ya NO está
            // acoplada a mano con este valor: WorldPanelPlacer la deriva del tamaño real del
            // lienzo (ver AlturaRelativaCalculada), de modo que este número se puede cambiar
            // sin revisar nada más — el 0,70 del 14-08 con la constante antigua dejó el borde
            // superior 2,8 cm por encima de los ojos, y esa clase de error ya no puede ocurrir.
            var canvas = DigitalTwin.UI.RuntimeUIFactory.CreateWorldCanvas("DigitalTwinCanvasMR",
                                                                           anchoMetros: 1f);

            var panelGo = new GameObject("~MetadataPanelMR");
            Object.DontDestroyOnLoad(panelGo);
            var panel = panelGo.AddComponent<MetadataPanelController>();
            panel.Initialize(canvas);
            // En el visor la ficha ocupa el lienzo entero: dejarla en la columna de 440 px del
            // escritorio la reducía a la mitad del ancho decidido (0,28 m de los 0,58) y el
            // texto quedaba por debajo del píxel físico — la borrosidad de la prueba del 13-08.
            panel.UsarAnchoCompleto();
            // Cuerpos de letra del visor: con la escala de render en la estándar (1.0), los
            // píxeles de legibilidad se ganan en el contenido (prueba del 14-08: el cuerpo
            // pequeño era ilegible a escala 1.0 y solo aceptable encareciendo toda la escena
            // a 1.4). Los cuerpos NO se suben más en esta tanda: el paso a 1 m de ancho ya los
            // agranda un 43 % angular respecto al 0,70 probado, y primero hay que ver si basta.
            panel.UsarTipografiaDeVisor();
            // Los gestos del panel, dichos en el propio panel (segundo disparo cierra,
            // conjuntos desplegables, joystick desplaza): eran funciones implementadas y mudas.
            panel.UsarAyudaDeVisor();
            // Fondo translúcido: da sensación de espacio sin restar legibilidad al texto, que
            // sigue a opacidad completa (ver SetOpacidadFondo). Ante el usuario y a 1,1 m ocupa
            // bastante campo de visión, y en modo anclado lo que hay detrás es el edificio real.
            panel.SetOpacidadFondo(0.55f);

            // Identificación del elemento seleccionado, por triple vía: caja de aristas y tinte
            // sobre el objeto, panel colocado ante el usuario, y línea que une panel y objeto.
            // Cada mecanismo cubre un caso en el que los otros fallan.
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

            // Mismo criterio que en escritorio: disponible pero apagada por defecto.
            Visual.SolarLightingController.Crear();

            IoT.SensorIntegrationBootstrap.TryAttach(index, panel, tipografiaDeVisor: true);

            // --- Lo específico de cada modo ---------------------------------------------------

            MRNodeNavigator navegador = null;
            MRMenuZonas menuZonas = null;

            if (modo == ModoAR.NavegacionPorNodos)
            {
                // La transparencia se apaga al entrar: la revisión remota se hace desde la
                // oficina, y el vídeo de la sala real detrás del modelo solo confunde. No se
                // persiste la preferencia: al próximo arranque el selector vuelve a mostrarse
                // sobre transparencia.
                if (MRPassthroughController.Instancia != null)
                    MRPassthroughController.Instancia.Aplicar(false);

                if (origenXR == null)
                {
                    // Vía de emergencia sin jerarquía de rig: sin origen de realidad extendida
                    // no hay a qué aplicar los desplazamientos, así que la navegación queda
                    // contemplativa. Ya quedó registrado el error de jerarquía más arriba.
                    Debug.LogError("[DigitalTwin][AR] Sin origen de realidad extendida no se " +
                                   "monta la navegacion por nodos: no habria a que aplicar los " +
                                   "desplazamientos.");
                }
                else
                {
                    var indicadoresGo = new GameObject("~IndicadoresDestinoAR");
                    Object.DontDestroyOnLoad(indicadoresGo);
                    var indicadores = indicadoresGo.AddComponent<MRIndicadoresDestino>();
                    indicadores.Initialize(Camera.main);

                    var navegadorGo = new GameObject("~NavegacionPorNodosAR");
                    Object.DontDestroyOnLoad(navegadorGo);
                    navegador = navegadorGo.AddComponent<MRNodeNavigator>();
                    navegador.Initialize(origenXR, Camera.main, index, indicadores);
                    navegador.ColocarEnNodoInicial();

                    // Menú de zonas: la última pieza de paridad con escritorio. Solo en este
                    // modo y solo con mandos (sin rig no habría forma de abrirlo ni de elegir).
                    if (rig != null)
                    {
                        var menuGo = new GameObject("~MenuZonasARRaiz");
                        Object.DontDestroyOnLoad(menuGo);
                        menuZonas = menuGo.AddComponent<MRMenuZonas>();
                        menuZonas.Initialize(rig, Camera.main, navegador, index);
                    }
                    else
                    {
                        Debug.LogWarning("[DigitalTwin][AR] Sin rig de mandos no se crea el " +
                                         "menu de zonas: no habria boton con que abrirlo.");
                    }
                }
            }
            else
            {
                // Modo anclado: la geometría pasa a oclusor invisible o desaparece, los
                // marcadores quedan fuera de la selección, y la transparencia debe estar
                // encendida (es el fondo sobre el que se compone la telemetría).
                MROcclusionService.Aplicar(index);
                ColliderBootstrapper.ExcluirPuntosDeNavegacionDeLaSeleccion(index);

                // El menú de zonas NO se ofrece en anclado, a conciencia: aquí el
                // desplazamiento es físico (el usuario anda por la obra) y un teletransporte
                // desincronizaría la vista de su cuerpo — la misma razón por la que este modo
                // tampoco ofrece puntos de navegación.
                Debug.LogWarning("[DigitalTwin][AR] Menu de zonas no aplicable en modo anclado: " +
                                 "el desplazamiento es fisico y un salto desincronizaria al " +
                                 "usuario de su cuerpo.");

                if (MRPassthroughController.Instancia != null &&
                    !MRPassthroughController.Instancia.Activado)
                    MRPassthroughController.Instancia.Aplicar(true);
            }

            // --- Mandos e interaccion ---------------------------------------------------------
            // Sin esto la escena era solo contemplativa: el modelo se veia pero no habia forma de
            // senalar nada. El rig ya existe desde la etapa A (hizo falta para el selector); aqui
            // se le suma el interprete del gatillo.
            if (rig != null && desplazamientoCamara != null)
            {
                var interaccionGo = new GameObject("~InteraccionAR");
                interaccionGo.transform.SetParent(desplazamientoCamara, false);
                var interaccion = interaccionGo.AddComponent<MRInteractionController>();
                interaccion.Initialize(rig, panel, colocador, navegador, index, menuZonas);
            }
            else
            {
                Debug.LogError("[DigitalTwin][AR] Sin rig de mandos no se crea la interaccion: " +
                               "la escena queda contemplativa (sin seleccion ni desplazamiento).");
            }

            Debug.LogWarning($"[DigitalTwin][AR] Bootstrap de Realidad Aumentada completo " +
                             $"(modo {modo}).");
        }

    }

    /// <summary>
    /// Coordina el arranque diferido: espera a la transparencia, muestra el selector y lanza
    /// la etapa B con el modo elegido. Es un MonoBehaviour porque necesita corrutinas; vive
    /// en su propio objeto para que un fallo suyo no arrastre al diagnóstico de entrada.
    /// Clase de primer nivel a propósito: los MonoBehaviour anidados funcionan con
    /// AddComponent, pero salirse del patrón del resto del proyecto no compra nada aquí.
    /// </summary>
    internal class MRBootSequencer : MonoBehaviour
    {
            /// <summary>La capa de transparencia se crea 90 fotogramas despues del arranque (ver
            /// MRPassthroughController.FotogramasDeEspera y la violacion de segmento que motivo
            /// ese retardo). Se espera ese plazo mas un margen antes de dar la transparencia por
            /// no disponible.</summary>
            private const int FotogramasDeEsperaMaxima = 210;
            private const int FotogramasDeMargenTrasReintento = 30;

            private SceneModelIndex _index;
            private MRControllerRig _rig;
            private Transform _desplazamientoCamara;
            private Transform _origenXR;

            public void Iniciar(SceneModelIndex index, MRControllerRig rig,
                                Transform desplazamientoCamara, Transform origenXR)
            {
                _index = index;
                _rig = rig;
                _desplazamientoCamara = desplazamientoCamara;
                _origenXR = origenXR;
                StartCoroutine(Secuencia());
            }

            private IEnumerator Secuencia()
            {
                Debug.LogWarning("[DigitalTwin][AR] Arranque diferido: esperando a la transparencia " +
                                 "para mostrar el selector de modo.");

                int fotogramas = 0;
                while (fotogramas < FotogramasDeEsperaMaxima && !TransparenciaActiva())
                {
                    fotogramas++;
                    yield return null;
                }

                if (!TransparenciaActiva())
                {
                    // La transparencia pudo quedar apagada por una preferencia guardada o por un
                    // fallo ya registrado por el propio controlador. Se intenta una vez más y el
                    // selector se muestra igualmente: elegir modo sobre fondo opaco es peor que
                    // sobre el vídeo de la sala, pero infinitamente mejor que no poder elegir.
                    Debug.LogWarning($"[DigitalTwin][AR] La transparencia no esta activa tras " +
                                     $"{fotogramas} fotogramas; se solicita de nuevo y el selector " +
                                     "se mostrara de todos modos.");
                    if (MRPassthroughController.Instancia != null)
                        MRPassthroughController.Instancia.Aplicar(true);
                    for (int i = 0; i < FotogramasDeMargenTrasReintento; i++) yield return null;
                }

                Debug.LogWarning($"[DigitalTwin][AR] Selector de modo visible (transparencia " +
                                 $"activa: {TransparenciaActiva()}).");

                if (MRPerfMonitor.Instancia != null)
                    MRPerfMonitor.Instancia.FijarFase("selector");

                MRModeSelector.Mostrar(_rig, Camera.main, modo =>
                {
                    MRDigitalTwinBootstrap.MontarGemelo(modo, _index, _rig,
                                                        _desplazamientoCamara, _origenXR);
                    Destroy(gameObject);
                });
            }

            private static bool TransparenciaActiva()
            {
                return MRPassthroughController.Instancia != null &&
                       MRPassthroughController.Instancia.Activado;
            }
    }
}
