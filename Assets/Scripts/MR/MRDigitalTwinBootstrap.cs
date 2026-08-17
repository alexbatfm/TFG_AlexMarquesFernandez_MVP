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
    /// Desde la ronda 9 se puede VOLVER al selector sin reiniciar la aplicación, pero no
    /// conmutando en caliente sino terminando el «programa» en curso y arrancando el otro por
    /// el mismo camino: ver <see cref="VolverAlSelector"/>.
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

            // Diagnóstico de la composición (ronda 10, 17-08): SOLO LEE. Mide el modo de mezcla,
            // la pila de capas de cada xrEndFrame, el formato del objetivo de color que URP elige
            // (con o sin canal alfa) y el alfa real del objetivo del ojo. No cambia ningún ajuste
            // ni toca la capa de transparencia; sus trazas llevan [DigitalTwin][AR][Compos].
            MRDiagnosticoComposicion.Crear();

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
        ///
        /// DESDE EL 15-08 (noche) EL MODO ANCLADO NO SE MONTA AQUÍ: se delega en
        /// <see cref="MRArranqueAnclado"/>, que espera a que el vídeo de transparencia esté
        /// CONFIRMADO activo —estado interno Y capa en la lista del SDK— y solo entonces llama a
        /// <see cref="MontarAncladoTrasConfirmarTransparencia"/>. La regla es dura: en modo
        /// anclado la aplicación no pide nada al usuario hasta que el vídeo esté confirmado,
        /// porque la premisa del modo es superponer el modelo al edificio real y sin cámara no
        /// hay edificio real. En la prueba del 15-08 por la tarde el orden antiguo llegó a pedir
        /// puntos de registro sobre fondo negro: la interfaz de colocación se creaba unas 150
        /// líneas de arranque antes de que nadie se ocupara del vídeo, y la activación final
        /// estaba tras una guarda <c>!Activado</c> que convertía un estado interno obsoleto en
        /// silencio (la causa raíz de ese estado, la capa destruida junto con la sesión OpenXR
        /// sin callback registrado, está en la nota de <see cref="MRPassthroughController"/>).
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

            Debug.LogWarning($"[DigitalTwin][AR] Montaje del gemelo digital iniciado (modo {modo}).");

            if (modo == ModoAR.Anclado)
            {
                // El modelo sigue apagado mientras se confirma la transparencia: sin vídeo, un
                // edificio opaco alrededor del usuario solo confundiría, y ninguna pieza del
                // modo anclado debe pedirle nada todavía. El guardián recibe la raíz apagada y
                // la entrega al montaje real cuando el vídeo se confirma.
                if (MRPerfMonitor.Instancia != null)
                    MRPerfMonitor.Instancia.FijarFase("esperando transparencia (Anclado)");

                var guardianGo = new GameObject("~ArranqueAnclado");
                Object.DontDestroyOnLoad(guardianGo);
                var guardian = guardianGo.AddComponent<MRArranqueAnclado>();
                guardian.Iniciar(index, rig, desplazamientoCamara, origenXR,
                                 _raizModeloApagadaDuranteSelector);
                _raizModeloApagadaDuranteSelector = null;
                return;
            }

            // --- Navegación por nodos: el flujo de siempre, sin cambios de comportamiento -----

            if (MRPerfMonitor.Instancia != null)
                MRPerfMonitor.Instancia.FijarFase($"montado ({modo})");

            ReactivarRaiz(_raizModeloApagadaDuranteSelector);
            _raizModeloApagadaDuranteSelector = null;

            // En navegación por nodos un anclaje persistido de una sesión anterior movería el
            // edificio entero bajo los pies del usuario a mitad de recorrido, que es exactamente
            // lo contrario de lo que ese modo promete (el modelo quieto y el usuario saltando
            // entre nodos): el servicio de anclaje no se crea.
            Debug.LogWarning("[DigitalTwin][AR] Anclaje espacial no aplicable en navegacion " +
                             "por nodos: el modelo permanece en su pose de autor.");

            var panel = MontarComun(index, out var colocador);

            // La transparencia se apaga al entrar: la revisión remota se hace desde la
            // oficina, y el vídeo de la sala real detrás del modelo solo confunde. No se
            // persiste la preferencia: al próximo arranque el selector vuelve a mostrarse
            // sobre transparencia.
            if (MRPassthroughController.Instancia != null)
                MRPassthroughController.Instancia.Aplicar(false);

            MRNodeNavigator navegador = null;
            MRMenuZonas menuZonas = null;

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

                // El menú del modo de navegación (zonas, iluminación solar, volver al
                // selector). Solo con mandos: sin rig no habría forma de abrirlo ni de elegir.
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
                                     "menu: no habria boton con que abrirlo.");
                }
            }

            CrearInteraccion(rig, desplazamientoCamara, panel, colocador, navegador, index,
                             menuZonas, colocacionAnclaje: null, identificarSenalado: false);

            Debug.LogWarning($"[DigitalTwin][AR] Bootstrap de Realidad Aumentada completo " +
                             $"(modo {modo}).");
        }

        /// <summary>
        /// Montaje del modo anclado, invocado por <see cref="MRArranqueAnclado"/> ÚNICAMENTE con
        /// el vídeo de transparencia confirmado (o con su indisponibilidad declarada, en el
        /// Editor). Orden: raíz del modelo, servicio de anclaje y binder (el binder se suscribe
        /// ANTES de que el servicio arranque, así la restauración de un anclaje guardado no
        /// encuentra a nadie escuchando), interfaz de colocación —el registro por pares de
        /// puntos que llama a ColocarEnPose / OlvidarAnclaje—, piezas comunes, oclusión e
        /// interacción.
        /// </summary>
        internal static void MontarAncladoTrasConfirmarTransparencia(SceneModelIndex index,
            MRControllerRig rig, Transform desplazamientoCamara, Transform origenXR,
            GameObject raizApagada)
        {
            // Traza de autorizacion con el estado medido EN ESTE INSTANTE: si alguna vez se ve
            // este montaje con la capa fuera de la lista del SDK (en el visor), el guardian ha
            // dejado pasar algo que no debia, y esta linea es la que lo demuestra sin conjeturas.
            var transparenciaAlMontar = MRPassthroughController.Instancia;
            Debug.LogWarning("[DigitalTwin][AR] Montaje anclado AUTORIZADO por el guardian. " +
                             "Transparencia: " + (transparenciaAlMontar != null
                                 ? transparenciaAlMontar.DiagnosticoBreve()
                                 : "SIN CONTROLADOR (no deberia ocurrir)") + ".");

            if (MRPerfMonitor.Instancia != null)
                MRPerfMonitor.Instancia.FijarFase("montado (Anclado)");

            ReactivarRaiz(raizApagada);

            MRColocacionAnclaje colocacion = null;
            var anclajeGo = new GameObject("~MRAnchorService");
            Object.DontDestroyOnLoad(anclajeGo);
            var anclaje = anclajeGo.AddComponent<MRAnchorService>();

            var binder = anclajeGo.AddComponent<ModelAnchorBinder>();
            binder.Initialize(index, anclaje, origenXR);

            anclaje.OnEstadoCambiado += estado =>
                Debug.LogWarning($"[DigitalTwin][MR] Estado del anclaje: {estado}.");

            if (rig != null && binder.RaizModelo != null)
            {
                var colocacionGo = new GameObject("~ColocacionAnclajeAR");
                Object.DontDestroyOnLoad(colocacionGo);
                colocacion = colocacionGo.AddComponent<MRColocacionAnclaje>();
                colocacion.Initialize(rig, Camera.main, index, anclaje, binder, origenXR);

                // La leyenda del mando nace en la etapa A con los controles de navegación; en
                // anclado el gatillo también toma puntos y A/X abre el menú (desde la ronda 9
                // el mismo gesto que en navegación; el panel de anclaje se reabre desde él).
                rig.FijarLeyenda("Gatillo · seleccionar / tomar punto\n" +
                                 "A o X · menu\n" +
                                 "Joystick · desplazar la ficha");
            }
            else
            {
                Debug.LogError("[DigitalTwin][AR] Sin rig de mandos o sin raiz de modelo no se crea la " +
                               "interfaz de colocacion: el anclaje solo podra restaurarse, nunca crearse.");
            }

            var panel = MontarComun(index, out var colocador);

            // La geometría pasa a oclusor invisible o desaparece (solo-profundidad desde el
            // primer fotograma; el canario de revelado verde se retiró el 15-08 tras cumplir su
            // función diagnóstica) y los marcadores quedan fuera de la selección.
            MROcclusionService.Aplicar(index);
            ColliderBootstrapper.ExcluirPuntosDeNavegacionDeLaSeleccion(index);

            // Menú del modo anclado (ronda 9): mismo gesto y misma forma que el menú de
            // navegación, para que se aprenda una sola vez. NO contiene zonas —aquí el
            // desplazamiento es físico y un teletransporte desincronizaría la vista del
            // cuerpo, la misma razón por la que este modo no ofrece puntos de navegación—;
            // aloja el panel de anclaje, rehacer el anclaje y la vuelta al selector de modo.
            MRMenuAnclado menuAnclado = null;
            if (rig != null)
            {
                var menuAncladoGo = new GameObject("~MenuAncladoARRaiz");
                Object.DontDestroyOnLoad(menuAncladoGo);
                menuAnclado = menuAncladoGo.AddComponent<MRMenuAnclado>();
                menuAnclado.Initialize(rig, Camera.main, colocacion);
            }
            else
            {
                Debug.LogWarning("[DigitalTwin][AR] Sin rig de mandos no se crea el menu del " +
                                 "modo anclado: no habria boton con que abrirlo.");
            }

            // La etiqueta de señalado solo existe en anclado: con los oclusores invisibles y en
            // un entorno oscuro es la única respuesta continua a «qué estoy señalando» que no
            // exige disparar ni depende de la iluminación de la sala.
            CrearInteraccion(rig, desplazamientoCamara, panel, colocador, navegador: null,
                             index: index, menuZonas: null, colocacionAnclaje: colocacion,
                             identificarSenalado: true, menuAnclado: menuAnclado);

            Debug.LogWarning("[DigitalTwin][AR] Bootstrap de Realidad Aumentada completo " +
                             "(modo Anclado).");
        }

        /// <summary>
        /// Piezas comunes a los dos modos: panel de metadatos en lienzo de mundo, resaltado y
        /// colocación del panel, iluminación solar (apagada por defecto) y middleware IoT.
        /// Extraído de MontarGemelo el 15-08 al dividirse el montaje anclado; contenido y orden
        /// son los que tenía dentro del método.
        /// </summary>
        private static MetadataPanelController MontarComun(SceneModelIndex index,
                                                           out WorldPanelPlacer colocador)
        {
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
            colocador = colocadorGo.AddComponent<WorldPanelPlacer>();
            colocador.Initialize(canvas);

            var colocadorLocal = colocador;
            panel.OnElementShown += meta =>
            {
                Transform t = meta != null ? meta.transform : null;
                resaltador.Resaltar(t);
                colocadorLocal.Seguir(t);
            };
            panel.OnPanelHidden += () =>
            {
                resaltador.Limpiar();
                colocadorLocal.Seguir(null);
            };

            // Mismo criterio que en escritorio: disponible pero apagada por defecto.
            Visual.SolarLightingController.Crear();

            IoT.SensorIntegrationBootstrap.TryAttach(index, panel, tipografiaDeVisor: true);

            return panel;
        }

        /// <summary>Devuelve a la escena la raíz del modelo apagada durante el selector (o la
        /// espera de transparencia). Todo lo que se monta después (oclusores, colliders de
        /// selección, sensores) da por hecho que la escena está viva.</summary>
        private static void ReactivarRaiz(GameObject raiz)
        {
            if (raiz == null) return;
            raiz.SetActive(true);
            Debug.LogWarning($"[DigitalTwin][AR] Raiz del modelo '{raiz.name}' reactivada " +
                             "para el montaje.");
        }

        /// <summary>
        /// Intérprete del gatillo. Sin esto la escena era solo contemplativa: el modelo se veía
        /// pero no había forma de señalar nada. El rig ya existe desde la etapa A (hizo falta
        /// para el selector).
        /// </summary>
        private static void CrearInteraccion(MRControllerRig rig, Transform desplazamientoCamara,
                                             MetadataPanelController panel, WorldPanelPlacer colocador,
                                             MRNodeNavigator navegador, SceneModelIndex index,
                                             MRMenuZonas menuZonas, MRColocacionAnclaje colocacionAnclaje,
                                             bool identificarSenalado, MRMenuAnclado menuAnclado = null)
        {
            if (rig != null && desplazamientoCamara != null)
            {
                var interaccionGo = new GameObject("~InteraccionAR");
                interaccionGo.transform.SetParent(desplazamientoCamara, false);
                var interaccion = interaccionGo.AddComponent<MRInteractionController>();
                interaccion.Initialize(rig, panel, colocador, navegador, index, menuZonas,
                                       colocacionAnclaje, identificarSenalado, menuAnclado);
            }
            else
            {
                Debug.LogError("[DigitalTwin][AR] Sin rig de mandos no se crea la interaccion: " +
                               "la escena queda contemplativa (sin seleccion ni desplazamiento).");
            }
        }

        // ==================================================================================
        //  Volver al selector de modo sin reiniciar la aplicación (ronda 9)
        // ==================================================================================

        /// <summary>
        /// Desmonta la sesión entera y vuelve al selector de modo, sin reiniciar la aplicación.
        ///
        /// LA VÍA ES RECARGAR LA ESCENA, no desmontar pieza a pieza, y es una decisión de
        /// diseño: los dos modos siguen siendo «dos programas, no dos ajustes»
        /// (DISENO-modo-anclado.md). Cada modo altera la escena de forma destructiva —el
        /// anclado sustituye materiales por solo-profundidad, apaga renderers y colliders y
        /// mueve la raíz del modelo con el registro; la navegación mueve el origen XR y oculta
        /// hojas de puerta— y deshacerlo a mano exigiría un método de desmontaje en cada pieza,
        /// con un residuo garantizado en cuanto una se olvidara. Recargar la escena devuelve
        /// TODO el estado de escena al del fichero (materiales, transformadas, renderers,
        /// colliders) por construcción; lo único que hay que hacer a mano es (1) destruir los
        /// objetos persistentes de la sesión (DontDestroyOnLoad sobrevive a la recarga),
        /// (2) reponer los estáticos de proceso, y (3) relanzar el arranque, porque
        /// [RuntimeInitializeOnLoadMethod] corre una sola vez por proceso, no por escena.
        ///
        /// Lo que se conserva a propósito: el anclaje espacial persistido en el visor (volver
        /// al selector no es olvidar el edificio; al reentrar en anclado se restaura solo) y
        /// las preferencias de PlayerPrefs. El coste es recargar el modelo (segundos, los
        /// mismos del arranque de la escena), que es exactamente el precio que ya se pagaba
        /// con el reinicio completo, menos el APK y el motor.
        /// </summary>
        internal static void VolverAlSelector(string motivo)
        {
            // Guarda previa: si la escena no puede recargarse (no esta en Build Settings, caso
            // posible solo en el Editor), abortarlo ANTES de desmontar nada deja la sesion
            // usable; descubrirlo despues la dejaria destruida y sin recarga.
            string escenaActiva = SceneManager.GetActiveScene().name;
            if (!Application.CanStreamedLevelBeLoaded(escenaActiva))
            {
                Debug.LogError($"[DigitalTwin][AR] No se puede volver al selector: la escena " +
                               $"'{escenaActiva}' no es recargable (¿falta en Build Settings?). " +
                               "No se desmonta nada.");
                return;
            }

            Debug.LogWarning($"[DigitalTwin][AR] VOLVER AL SELECTOR DE MODO ({motivo}): se " +
                             "desmonta la sesion, se recarga la escena y se rearranca el " +
                             "bootstrap. El anclaje persistido del visor NO se toca.");

            // 1) Estado estático compartido que sobrevive a la recarga de escena.
            Navigation.PuertaTransparente.Restituir();          // tolera renderers ya destruidos
            ColliderBootstrapper.ReiniciarSeleccionDeSesion();  // deshace la exclusion del anclado
            _initialized = false;
            _gemeloMontado = false;
            _raizModeloApagadaDuranteSelector = null;

            // 2) Objetos persistentes de la sesión. Se registran los NOMBRES, no solo la cuenta:
            // en el registro del 17-08 el barrido dio 11, 11 y 10 objetos en las tres vueltas y
            // no había forma de saber qué objeto faltaba en la tercera ni de auditar que no
            // cayera ninguno del motor.
            int destruidos = DestruirObjetosPersistentesDeSesion(out string nombres);
            Debug.LogWarning($"[DigitalTwin][AR] {destruidos} objeto(s) persistente(s) de la " +
                             $"sesion destruidos antes de recargar: {nombres}.");

            // 3) Recarga y rearranque. El manejador se registra ANTES de pedir la carga.
            SceneManager.sceneLoaded += RearrancarTrasRecarga;
            SceneManager.LoadScene(escenaActiva);
        }

        private static void RearrancarTrasRecarga(Scene escena, LoadSceneMode modo)
        {
            SceneManager.sceneLoaded -= RearrancarTrasRecarga;
            Debug.LogWarning($"[DigitalTwin][AR] Escena '{escena.name}' recargada: se rearranca " +
                             "el punto de entrada de Realidad Aumentada (RuntimeInitialize solo " +
                             "corre una vez por proceso).");
            Bootstrap();
        }

        /// <summary>
        /// Destruye los objetos de la escena DontDestroyOnLoad creados por esta sesión. La
        /// escena de persistentes no es enumerable directamente; el truco de la sonda —crear un
        /// objeto, marcarlo persistente y preguntarle por su escena— sí da acceso a sus raíces.
        /// El criterio es conservador: solo caen los nombres del propio proyecto (prefijo «~» y
        /// el lienzo del panel), nunca los objetos de gestión de XR del motor, que también
        /// viven ahí y sin los cuales no habría visor.
        /// </summary>
        private static int DestruirObjetosPersistentesDeSesion(out string nombres)
        {
            var sonda = new GameObject("~SondaEscenaPersistente");
            Object.DontDestroyOnLoad(sonda);
            int destruidos = 0;
            var lista = new System.Text.StringBuilder();
            foreach (var raiz in sonda.scene.GetRootGameObjects())
            {
                if (raiz == sonda) continue;
                bool esDeLaSesion = raiz.name.StartsWith("~") || raiz.name == "DigitalTwinCanvasMR";
                if (!esDeLaSesion) continue;
                if (lista.Length > 0) lista.Append(", ");
                lista.Append(raiz.name);
                Object.Destroy(raiz);
                destruidos++;
            }
            Object.Destroy(sonda);
            nombres = destruidos > 0 ? lista.ToString() : "(ninguno)";
            return destruidos;
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
                // Confirmada de verdad (estado interno Y capa en la lista del SDK), no la
                // creencia interna: la prueba del 15-08 demostró que pueden divergir. En el
                // Editor ambas señales son falsas y el selector aparece tras la espera máxima,
                // exactamente como antes de este cambio.
                return MRPassthroughController.Instancia != null &&
                       MRPassthroughController.Instancia.ConfirmadaActiva;
            }
    }

    /// <summary>
    /// Guardián del arranque del modo anclado (15-08, noche): el gemelo anclado NO se monta
    /// hasta que el vídeo de transparencia esté CONFIRMADO activo, con confirmación real
    /// (<see cref="MRPassthroughController.ConfirmadaActiva"/>: estado interno Y capa viva en
    /// el runtime), no supuesta. No es una preferencia de presentación: el modo anclado
    /// superpone el modelo al edificio real, y sin cámara no hay edificio real; pedir puntos de
    /// registro a ciegas —lo ocurrido en la prueba del 15-08 por la tarde— produce puntos que
    /// luego hay que deshacer.
    ///
    /// Comportamiento: pide la activación sin condición (Aplicar(true) es idempotente y
    /// reconcilia contra el runtime, recreando la capa si murió con la sesión), espera la
    /// confirmación con reintentos y, si no llega, lo declara con LogError y con un aviso en el
    /// visor — y SIGUE reintentando de fondo, porque la causa más probable (sesión OpenXR
    /// destruida por el paso por el sistema) se resuelve sola al volver la sesión y entonces el
    /// modo anclado se monta sin que el usuario tenga que reiniciar. En plataformas sin
    /// transparencia (Editor, escritorio) el montaje procede como degradación declarada: es lo
    /// que permite ejercitar el registro en Play con el respaldo de ratón.
    /// </summary>
    internal class MRArranqueAnclado : MonoBehaviour
    {
        /// <summary>Reintentos silenciosos (solo registro) antes de declarar el fallo al
        /// usuario. Con la reconciliación del controlador, la confirmación es inmediata en el
        /// caso sano: llegar aquí ya es señal de problema.</summary>
        private const int IntentosAntesDeDeclararFallo = 5;
        private const float SegundosEntreReintentos = 2f;
        /// <summary>Cadencia de reintento una vez mostrado el aviso: más lenta, porque a partir
        /// de ahí se está esperando a que el sistema devuelva la sesión.</summary>
        private const float SegundosEntreReintentosConAviso = 5f;
        /// <summary>Fotogramas concedidos tras cada petición para que el runtime componga.</summary>
        private const int FotogramasDeGraciaPorIntento = 10;

        private SceneModelIndex _index;
        private MRControllerRig _rig;
        private Transform _desplazamientoCamara;
        private Transform _origenXR;
        private GameObject _raizApagada;
        private GameObject _aviso;

        public void Iniciar(SceneModelIndex index, MRControllerRig rig,
                            Transform desplazamientoCamara, Transform origenXR,
                            GameObject raizApagada)
        {
            _index = index;
            _rig = rig;
            _desplazamientoCamara = desplazamientoCamara;
            _origenXR = origenXR;
            _raizApagada = raizApagada;
            StartCoroutine(Secuencia());
        }

        private IEnumerator Secuencia()
        {
            var transparencia = MRPassthroughController.Instancia;

            if (transparencia == null || !transparencia.Disponible)
            {
                // Editor o escritorio: no hay cámaras que atravesar. Degradación declarada
                // para poder desarrollar y probar el registro en Play; en el visor este camino
                // no se da (Disponible es cierto en toda compilación de Android).
                Debug.LogWarning("[DigitalTwin][AR] Modo anclado sin transparencia disponible " +
                                 "en esta plataforma (Editor/escritorio): se monta en modo de " +
                                 "desarrollo, sin video. En el visor esto seria un fallo.");
                Continuar();
                yield break;
            }

            Debug.LogWarning("[DigitalTwin][AR] Modo anclado: esperando la confirmacion del " +
                             "video de transparencia (capa en la lista del SDK) antes de montar " +
                             "nada ni pedir nada al usuario.");

            int intentos = 0;
            while (true)
            {
                transparencia.Aplicar(true);
                for (int i = 0; i < FotogramasDeGraciaPorIntento; i++)
                {
                    if (transparencia.ConfirmadaActiva) break;
                    yield return null;
                }
                if (transparencia.ConfirmadaActiva) break;

                intentos++;
                if (intentos < IntentosAntesDeDeclararFallo)
                {
                    Debug.LogWarning($"[DigitalTwin][AR] Transparencia sin confirmar (intento " +
                                     $"{intentos} de {IntentosAntesDeDeclararFallo}; " +
                                     $"{transparencia.DiagnosticoBreve()}); nuevo " +
                                     $"intento en {SegundosEntreReintentos:0} s.");
                    yield return new WaitForSeconds(SegundosEntreReintentos);
                    continue;
                }
                if (intentos == IntentosAntesDeDeclararFallo)
                {
                    Debug.LogError("[DigitalTwin][AR] El video de transparencia NO se ha podido " +
                                   $"confirmar tras {intentos} intentos " +
                                   $"({transparencia.DiagnosticoBreve()}): el modo anclado no se " +
                                   "monta, porque registrar puntos sin camara es peor que no " +
                                   "registrar. Causas conocidas: sesion OpenXR destruida por el " +
                                   "sistema y aun sin recrear (se resuelve sola al volver), o " +
                                   "caracteristica 'VIVE XR Passthrough' sin marcar en la " +
                                   "pestana de Android (no se resuelve sin recompilar). El " +
                                   "detalle esta en las trazas anteriores del passthrough; se " +
                                   $"sigue reintentando cada {SegundosEntreReintentosConAviso:0} s.");
                    MostrarAviso();
                }
                yield return new WaitForSeconds(SegundosEntreReintentosConAviso);
            }

            if (_aviso != null)
            {
                Destroy(_aviso);
                _aviso = null;
                Debug.LogWarning("[DigitalTwin][AR] La transparencia ha vuelto: se retira el " +
                                 "aviso y se monta el modo anclado.");
            }

            Debug.LogWarning("[DigitalTwin][AR] Transparencia CONFIRMADA activa: se monta el " +
                             "modo anclado.");
            Continuar();
        }

        private void Continuar()
        {
            MRDigitalTwinBootstrap.MontarAncladoTrasConfirmarTransparencia(
                _index, _rig, _desplazamientoCamara, _origenXR, _raizApagada);
            Destroy(gameObject);
        }

        /// <summary>
        /// Aviso en el visor: sobre fondo negro, el registro de logcat no le dice nada a quien
        /// lleva el casco puesto. Mismo patrón de presentación que las tarjetas del selector
        /// (colocado una vez delante del usuario, no persiguiéndolo).
        /// </summary>
        private void MostrarAviso()
        {
            if (_aviso != null) return;

            var camara = Camera.main;
            Vector3 adelante = camara != null ? camara.transform.forward : Vector3.forward;
            adelante.y = 0f;
            if (adelante.sqrMagnitude < 0.0001f) adelante = Vector3.forward;
            adelante.Normalize();
            Vector3 posicion = (camara != null ? camara.transform.position : Vector3.zero)
                               + adelante * 1.3f;

            var canvas = DigitalTwin.UI.RuntimeUIFactory.CreateWorldCanvas("~AvisoSinCamaraAR",
                anchoPx: 660f, altoPx: 400f, anchoMetros: 0.74f);
            _aviso = canvas.gameObject;
            _aviso.transform.SetParent(transform, true);
            _aviso.transform.position = posicion;
            _aviso.transform.rotation = Quaternion.LookRotation(adelante, Vector3.up);

            var rt = (RectTransform)canvas.transform;
            var fondo = DigitalTwin.UI.RuntimeUIFactory.CreatePanel(rt, "Fondo",
                new Color(0.12f, 0.05f, 0.06f, 0.93f));
            DigitalTwin.UI.RuntimeUIFactory.StretchToParent((RectTransform)fondo.transform);

            var tituloRect = DigitalTwin.UI.RuntimeUIFactory.CreateRect(rt, "Titulo");
            tituloRect.anchorMin = new Vector2(0f, 1f);
            tituloRect.anchorMax = new Vector2(1f, 1f);
            tituloRect.pivot = new Vector2(0.5f, 1f);
            tituloRect.anchoredPosition = new Vector2(0f, -24f);
            tituloRect.sizeDelta = new Vector2(-48f, 64f);
            DigitalTwin.UI.RuntimeUIFactory.CreateText(tituloRect, "Texto",
                "La cámara del visor no responde", 34, TextAnchor.MiddleCenter,
                new Color(1f, 0.62f, 0.55f, 1f), FontStyle.Bold);
            DigitalTwin.UI.RuntimeUIFactory.StretchToParent(
                (RectTransform)tituloRect.GetChild(0).transform);

            var cuerpoRect = DigitalTwin.UI.RuntimeUIFactory.CreateRect(rt, "Cuerpo");
            cuerpoRect.anchorMin = new Vector2(0f, 0f);
            cuerpoRect.anchorMax = new Vector2(1f, 1f);
            cuerpoRect.offsetMin = new Vector2(30f, 26f);
            cuerpoRect.offsetMax = new Vector2(-30f, -100f);
            DigitalTwin.UI.RuntimeUIFactory.CreateText(cuerpoRect, "Texto",
                "El modo anclado superpone el modelo al vídeo de las cámaras, y ese vídeo no se " +
                "ha podido activar. No se pedirá ningún punto de registro a ciegas.\n\n" +
                "La aplicación lo reintenta sola cada pocos segundos; si el sistema acaba de " +
                "mostrar el límite de seguridad o un menú, el vídeo volverá al recuperarse la " +
                "sesión. Si este aviso no desaparece, reinicia la aplicación; si aun así " +
                "persiste, la compilación no tiene activada la característica VIVE XR " +
                "Passthrough.", 24, TextAnchor.UpperLeft,
                new Color(1f, 1f, 1f, 0.85f), FontStyle.Normal);
            DigitalTwin.UI.RuntimeUIFactory.StretchToParent(
                (RectTransform)cuerpoRect.GetChild(0).transform);
        }
    }
}
