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
        internal static GameObject _raizModeloApagadaDuranteSelector;

        /// <summary>
        /// Fases del arranque de la escena, con el texto que ve el usuario y el peso que ocupan
        /// en la barra.
        ///
        /// LOS PESOS SALEN DE UNA MEDIDA, NO DE UNA IMPRESIÓN. En el registro del visor de la
        /// sesión del 2026-08-18 (01:15:04,515 → 01:15:05,669) el arranque se reparte así:
        /// índice del modelo 3 ms, colisionadores 71 ms, modelos de los mandos 29 ms, y una
        /// espera de 1,03 s hasta que la capa de transparencia queda creada. La espera es, con
        /// diferencia, la parte mayor de lo que el usuario aguarda, así que es la parte mayor de
        /// la barra: una barra ponderada por trabajo de CPU llegaría al 100 % en la primera
        /// décima y se quedaría ahí un segundo, que es la forma más eficaz de que una barra
        /// mienta. Y esa fase, además, es la única cuyo avance se conoce con exactitud, porque
        /// es un contador de fotogramas (<c>MRPassthroughController.FraccionDeEsperaInicial</c>).
        /// </summary>
        internal static FaseDeArranque[] FasesDeEscena()
        {
            return new[]
            {
                new FaseDeArranque("indice", "Leyendo el modelo del edificio", 5f),
                new FaseDeArranque("colisionadores", "Preparando la geometría", 75f),
                new FaseDeArranque("mandos", "Buscando los mandos", 30f),
                new FaseDeArranque("camara", "Encendiendo la cámara del visor", 900f),
            };
        }

        /// <summary>
        /// Perfil de la vuelta al selector: desmontaje y recarga de escena MÁS las fases del
        /// arranque que viene después. Se declaran juntas a propósito, para que la barra no
        /// vuelva a cero a mitad de espera cuando el arranque tome el relevo.
        ///
        /// Los pesos de la segunda mitad son menores que en un arranque en frío porque la
        /// recarga no paga el cocinado de las mallas: el mismo registro del 18-08 mide 71 ms de
        /// colisionadores en el primer arranque y 13 ms en el segundo, con las mallas ya en
        /// caché de PhysX.
        /// </summary>
        internal static FaseDeArranque[] FasesDeRecarga()
        {
            return new[]
            {
                new FaseDeArranque("desmontaje", "Cerrando la sesión", 10f),
                new FaseDeArranque("escena", "Recargando la escena", 80f),
                new FaseDeArranque("indice", "Leyendo el modelo del edificio", 5f),
                new FaseDeArranque("colisionadores", "Preparando la geometría", 20f),
                new FaseDeArranque("mandos", "Buscando los mandos", 5f),
                new FaseDeArranque("camara", "Encendiendo la cámara del visor", 1000f),
            };
        }

        /// <summary>
        /// Fases del montaje del gemelo, una vez elegido modo. Pesos calibrados con el mismo
        /// registro: entre la traza de «Montaje del gemelo digital iniciado» (01:15:08,027) y la
        /// de «Bootstrap de Realidad Aumentada completo» (01:15:08,410) pasan 383 ms, de los que
        /// 311 corresponden a las piezas comunes —ficha de activos y middleware de sensores— y
        /// el resto a la navegación y los menús. El modo anclado añade dos fases propias.
        /// </summary>
        internal static FaseDeArranque[] FasesDeMontaje(ModoAR modo)
        {
            if (modo == ModoAR.Anclado)
            {
                return new[]
                {
                    new FaseDeArranque("camaraConfirmada", "Esperando la cámara del visor", 60f),
                    new FaseDeArranque("geometria", "Colocando el modelo", 15f),
                    new FaseDeArranque("anclaje", "Preparando el anclaje al edificio", 40f),
                    new FaseDeArranque("panel", "Preparando la ficha de activos", 130f),
                    new FaseDeArranque("sensores", "Conectando con los sensores", 70f),
                    new FaseDeArranque("oclusion", "Ajustando el modelo al edificio real", 80f),
                    new FaseDeArranque("menus", "Preparando los menús", 40f),
                };
            }

            return new[]
            {
                new FaseDeArranque("geometria", "Colocando el modelo", 15f),
                new FaseDeArranque("panel", "Preparando la ficha de activos", 130f),
                new FaseDeArranque("sensores", "Conectando con los sensores", 70f),
                new FaseDeArranque("navegacion", "Cargando el grafo de navegación", 110f),
                new FaseDeArranque("menus", "Preparando los menús", 55f),
            };
        }

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
            //
            // SOLO SE CREA AQUÍ LO QUE NO PUEDE ESPERAR AL SIGUIENTE FOTOGRAMA: el monitor de
            // rendimiento, la pantalla de carga (que tiene que estar en el primer fotograma que
            // se dibuje, o vuelve a existir el intervalo indefinido que viene a cubrir), el
            // controlador de transparencia y el diagnóstico de composición. Todo lo demás —el
            // índice, los colisionadores, los mandos— pasa a la corrutina del secuenciador y se
            // reparte entre fotogramas.
            //
            // POR QUÉ SE REPARTE. En la sesión del 18-08 esta etapa ocupaba 112 ms dentro de un
            // único fotograma (71 de ellos en los 351 MeshCollider). A los 90 Hz que el dispositivo declara en el propio registro
            // (<c>RefreshRate change: 90.0</c>) eso son diez fotogramas sin entregar: el compositor de OpenXR reproyecta el último disponible,
            // la imagen deja de seguir a la cabeza y aparece el desacople visual-vestibular que
            // produce el malestar. Enseñar un panel encima no lo arregla; repartir el trabajo,
            // sí.
            //
            // EL ORDEN DE LA TRANSPARENCIA NO CAMBIA. La capa se sigue creando 90 fotogramas
            // después de que arranque su Start (ver MRPassthroughController y la violación de
            // segmento del 13-08), y su Start sigue corriendo al final de ESTE fotograma. Lo
            // único que cambia es que su Crear() ahora se ejecuta antes que los colisionadores
            // en lugar de después, dentro del mismo fotograma: el contador de 90 no se entera.
            // El reparto de la etapa A debe terminar holgadamente dentro de esos 90 fotogramas,
            // porque la premisa de la espera es que la capa se pida con el motor ya tranquilo;
            // el secuenciador lo comprueba y lo denuncia si deja de cumplirse.

            MRPerfMonitor.Crear();

            // Perfil de fases. «SiProcede» y no «Comenzar» porque la vuelta al selector declara
            // un perfil más largo que ya incluye estas fases (ver FasesDeRecarga).
            ProgresoDeArranque.ComenzarSiProcede("arranque", FasesDeEscena());

            var pantalla = MRPantallaDeCarga.Abrir();
            // Tras una recarga de escena la cámara es otra: la pantalla vuelve a tomarla y a
            // colocarse delante del usuario. Si acaba de crearse, no hace nada.
            pantalla.ReanclarTrasRecarga();

            // La transparencia se prepara antes que el resto. El orden importa poco para el
            // resultado, pero mucho para diagnosticar: si algo falla al crear la capa, el aviso
            // aparece antes que las trazas de montaje y no queda sepultado.
            MRPassthroughController.Crear();

            // La pantalla de carga ya ha cambiado el borrado de la cámara para enseñar un fondo
            // neutro mientras no hay vídeo. Se le entregan al controlador los valores de ANTES
            // de ese cambio, que son los que debe reponer si algún día apaga la transparencia
            // —lo que hace el modo de navegación por nodos al entrar—. Sin esto, ese modo se
            // quedaría con el fondo de la pantalla de carga en lugar del cielo de la escena.
            if (MRPassthroughController.Instancia != null)
                MRPassthroughController.Instancia.AdoptarAjustesDeCamaraPrevios(
                    pantalla.BorradoOriginal, pantalla.FondoOriginal);

            // Diagnóstico de la composición (ronda 10, 17-08): SOLO LEE. Mide el modo de mezcla,
            // la pila de capas de cada xrEndFrame, el formato del objetivo de color que URP elige
            // (con o sin canal alfa) y el alfa real del objetivo del ojo. No cambia ningún ajuste
            // ni toca la capa de transparencia; sus trazas llevan [DigitalTwin][AR][Compos].
            MRDiagnosticoComposicion.Crear();

            var arranqueGo = new GameObject("~ArranqueDiferidoAR");
            Object.DontDestroyOnLoad(arranqueGo);
            arranqueGo.AddComponent<MRBootSequencer>().Iniciar();

            _initialized = true;
        }

        /// <summary>Altura de ojos con la que se degrada cuando no hay seguimiento a nivel de
        /// suelo (y con la que se prueba en el Editor): la altura de ojos de pie mediana de la
        /// población adulta, ~1,58 m (ANSUR II, tablas resumen del Ergonomics Center NCSU).</summary>
        private const float AlturaVistaSinSuelo = 1.58f;

        /// <summary>
        /// Cuánto está elevado el origen XR por encima del suelo que representa. Es 0 con
        /// seguimiento a nivel de suelo (el suelo físico ES el plano y=0 del seguimiento) y
        /// <see cref="AlturaVistaSinSuelo"/> en las dos degradaciones declaradas (Editor sin
        /// XR, dispositivo sin modo de suelo), donde el origen se eleva para que la vista quede
        /// a altura de ojos. Lo consume <see cref="MRNodeNavigator"/> para alinear el suelo de
        /// seguimiento con el suelo del nodo SIN deshacer esa elevación. Se recalcula en cada
        /// llamada a <see cref="AsegurarSeguimientoANivelDeSuelo"/> (también tras recargar).
        /// </summary>
        internal static float ElevacionOrigenSobreSuelo { get; private set; }

        /// <summary>Inclinación del origen por debajo de la cual no se toca: es el ruido de un
        /// cuaternión normalizado, no una inclinación real.</summary>
        private const float InclinacionOrigenDespreciableGrados = 0.01f;

        /// <summary>
        /// NIVELA el origen de realidad extendida: le quita cabeceo y alabeo y conserva solo la
        /// guiñada. HALLAZGO DEL 19-08 (agente A): el objeto «XR Origin (VR)» de ARScene.unity
        /// está guardado con rotación (-0,0010, 0,9989, -0,0363, -0,0281), es decir, guiñada
        /// -176,8° Y UN CABECEO DE 4,16°, y a y=0,441 m. Con seguimiento a nivel de suelo el
        /// suelo físico es el plano y=0 del ESPACIO DE SEGUIMIENTO, y ese plano llega al mundo a
        /// través de este transform: con cabeceo, el suelo real quedaba en el mundo como un
        /// plano inclinado 4,16° (7,3 cm por metro) que pasa por el origen. Consecuencias, todas
        /// medibles en el logcat del 19-08: (1) la altura de la vista en navegación por nodos
        /// —que solo mueve el origen en planta— dependía de la POSICIÓN FÍSICA del usuario en su
        /// sala (1,48–2,42 m para la misma persona de pie: 0,441 + estatura − 0,073·z_seg); (2)
        /// en modo anclado el modelo, horizontal en el mundo, quedaba inclinado 4,16° respecto al
        /// edificio real y su suelo se hundía o ascendía 7,3 cm por metro de distancia al eje
        /// del área de juego (las poses de anclaje del log, y=-0,04 e y=-0,30 m en seguimiento
        /// para puntos del SUELO del modelo, coinciden con esa inclinación al centímetro); (3)
        /// el rayo del registro cortaba un plano que no era el suelo real, con decenas de cm de
        /// error horizontal por punto. Nivelar aquí, al resolver el origen, cubre los dos modos y
        /// cada recarga de la escena; la escena puede corregirse además a mano, pero este código
        /// no depende de ello. La guiñada se conserva porque es arbitraria (el registro la mide;
        /// la navegación la ignora). La altura del origen NO se toca aquí: en navegación la fija
        /// el navegador al suelo de cada nodo; en modo anclado es la cota del suelo físico a la
        /// que el registro lleva el modelo.
        /// </summary>
        internal static void NivelarOrigenXR(Transform origenXR)
        {
            if (origenXR == null) return;
            float inclinacion = Vector3.Angle(origenXR.up, Vector3.up);
            Vector3 adelante = Vector3.ProjectOnPlane(origenXR.forward, Vector3.up);
            if (adelante.sqrMagnitude < 1e-6f) adelante = Vector3.ProjectOnPlane(origenXR.right, Vector3.up);
            Quaternion soloGuinada = adelante.sqrMagnitude < 1e-6f
                ? Quaternion.identity
                : Quaternion.LookRotation(adelante.normalized, Vector3.up);

            if (inclinacion < InclinacionOrigenDespreciableGrados)
            {
                Debug.LogWarning($"[DigitalTwin][AR] Origen XR ya nivelado: inclinacion {inclinacion:0.000}°, " +
                                 $"guiñada {soloGuinada.eulerAngles.y:0.0}°, posicion {origenXR.position}.");
                return;
            }

            Vector3 eulerAntes = origenXR.rotation.eulerAngles;
            origenXR.rotation = soloGuinada;
            Debug.LogWarning($"[DigitalTwin][AR] Origen XR NIVELADO: tenia {inclinacion:0.00}° de inclinacion " +
                             $"(Euler antes x={eulerAntes.x:0.00}° y={eulerAntes.y:0.00}° z={eulerAntes.z:0.00}°); " +
                             $"ahora solo guiñada {origenXR.rotation.eulerAngles.y:0.0}°, posicion {origenXR.position}. " +
                             "Sin nivelar, el suelo de seguimiento llegaba al mundo inclinado: " +
                             $"{Mathf.Tan(inclinacion * Mathf.Deg2Rad) * 100f:0.0} cm de error vertical por metro " +
                             "(la y del origen la fija la navegacion al suelo de cada nodo; en modo anclado es la " +
                             "cota del suelo fisico).");
        }

        /// <summary>
        /// Fija y VERIFICA el modo de origen de seguimiento a nivel de suelo, con el resultado
        /// en el registro, y NIVELA el origen (ver <see cref="NivelarOrigenXR"/>). Con origen de
        /// suelo, la altura de la cámara sobre el suelo es la estatura real del usuario y el
        /// programa no la escribe nunca: los viajes son horizontales y lo único que la navegación
        /// fija es la cota del origen, para que el suelo físico (y=0 del seguimiento) coincida
        /// con el suelo del nodo. Dos degradaciones declaradas: en el Editor sin subsistema XR, el
        /// origen se eleva a una altura de ojos mediana para que el respaldo de ratón vea como
        /// una persona de pie; y si el dispositivo no admitiera el modo de suelo, se aplica la
        /// misma elevación —la estatura real no es conocible en ese modo— dejándolo dicho. En
        /// ambos casos <see cref="ElevacionOrigenSobreSuelo"/> queda en esa elevación para que
        /// el navegador la respete al alinear el suelo.
        /// </summary>
        internal static void AsegurarSeguimientoANivelDeSuelo(Transform origenXR)
        {
            ElevacionOrigenSobreSuelo = 0f;
            NivelarOrigenXR(origenXR);

            var subsistemas = new List<XRInputSubsystem>();
            SubsystemManager.GetSubsystems(subsistemas);

            if (subsistemas.Count == 0)
            {
#if UNITY_EDITOR
                origenXR.position += Vector3.up * AlturaVistaSinSuelo;
                ElevacionOrigenSobreSuelo = AlturaVistaSinSuelo;
                Debug.LogWarning("[DigitalTwin][AR] Sin subsistema XR (modo Play del Editor): " +
                                 $"origen elevado {AlturaVistaSinSuelo:0.00} m para que el " +
                                 "respaldo de raton vea a altura de ojos de una persona de pie " +
                                 "(ALTURA APLICADA: constante AlturaVistaSinSuelo; el navegador " +
                                 "la conserva al alinear el suelo de cada nodo).");
#else
                Debug.LogWarning("[DigitalTwin][AR] Sin subsistema XR de entrada: no se puede " +
                                 "fijar el origen de seguimiento. La altura de la vista queda " +
                                 "en manos de la escena (ALTURA APLICADA: ninguna).");
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
                    ElevacionOrigenSobreSuelo = AlturaVistaSinSuelo;
                    Debug.LogWarning("[DigitalTwin][AR] El dispositivo NO ha quedado en origen " +
                                     $"de suelo: se eleva el origen {AlturaVistaSinSuelo:0.00} m " +
                                     "como altura de ojos mediana (degradacion declarada; la " +
                                     "estatura real no es conocible en este modo). ALTURA " +
                                     "APLICADA: constante AlturaVistaSinSuelo.");
                }
                else
                {
                    Debug.LogWarning("[DigitalTwin][AR] ALTURA APLICADA: ninguna. Con origen Floor el " +
                                     "programa no suma nada a la vista; la altura de la camara sobre " +
                                     "el suelo de seguimiento es la del visor, y la cota del origen " +
                                     $"(ahora y={origenXR.position.y:0.000}) la fija el navegador al " +
                                     "suelo del nodo o, en modo anclado, es el suelo fisico al que " +
                                     "el registro lleva el modelo.");
                }
            }
        }

        /// <summary>
        /// Raíz del modelo importado, con la misma resolución que usa
        /// <see cref="ModelAnchorBinder"/>: subir por la jerarquía desde cualquier elemento con
        /// metadatos hasta el objeto más alto. No se codifica el nombre del objeto del .glb,
        /// que puede cambiar al reimportar.
        /// </summary>
        internal static GameObject RaizDelModelo(SceneModelIndex index)
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

            // La pantalla de carga vuelve a ponerse: entre elegir modo y tener el gemelo montado
            // hay trabajo real —383 ms medidos el 18-08— y hasta ahora transcurría dentro de un
            // único fotograma, justo en el instante en que el usuario acaba de disparar y espera
            // respuesta. Además de cubrir la espera, la pantalla restringe la máscara de cultivo
            // de la cámara, y eso resuelve un problema que el reparto habría creado: en modo
            // anclado la geometría se reactiva antes de vestirse de oclusor, y sin la máscara el
            // usuario vería el edificio virtual opaco encima de su sala real mientras se cambian
            // los materiales.
            MRPantallaDeCarga.Abrir();
            ProgresoDeArranque.Comenzar("montaje " + modo, FasesDeMontaje(modo));

            if (modo == ModoAR.Anclado)
            {
                // El modelo sigue apagado mientras se confirma la transparencia: sin vídeo, un
                // edificio opaco alrededor del usuario solo confundiría, y ninguna pieza del
                // modo anclado debe pedirle nada todavía. El guardián recibe la raíz apagada y
                // la entrega al montaje real cuando el vídeo se confirma.
                if (MRPerfMonitor.Instancia != null)
                    MRPerfMonitor.Instancia.FijarFase("esperando transparencia (Anclado)");

                ProgresoDeArranque.EntrarEnFase("camaraConfirmada");

                var guardianGo = new GameObject("~ArranqueAnclado");
                Object.DontDestroyOnLoad(guardianGo);
                var guardian = guardianGo.AddComponent<MRArranqueAnclado>();
                guardian.Iniciar(index, rig, desplazamientoCamara, origenXR,
                                 _raizModeloApagadaDuranteSelector);
                _raizModeloApagadaDuranteSelector = null;
                return;
            }

            if (MRPerfMonitor.Instancia != null)
                MRPerfMonitor.Instancia.FijarFase($"montado ({modo})");

            LanzarMontaje(ModoAR.NavegacionPorNodos, index, rig, desplazamientoCamara, origenXR,
                          _raizModeloApagadaDuranteSelector);
            _raizModeloApagadaDuranteSelector = null;
        }

        /// <summary>
        /// Crea el objeto que ejecuta el montaje como corrutina. El montaje dejó de ser una
        /// llamada síncrona cuando se repartió entre fotogramas; esto es lo único que queda de
        /// aquella llamada.
        /// </summary>
        private static void LanzarMontaje(ModoAR modo, SceneModelIndex index, MRControllerRig rig,
                                          Transform desplazamientoCamara, Transform origenXR,
                                          GameObject raizApagada)
        {
            var go = new GameObject("~MontajeGemeloAR");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<MRMontajeGemelo>()
              .Iniciar(modo, index, rig, desplazamientoCamara, origenXR, raizApagada);
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

            LanzarMontaje(ModoAR.Anclado, index, rig, desplazamientoCamara, origenXR, raizApagada);
        }

        /// <summary>
        /// Piezas comunes a los dos modos: panel de metadatos en lienzo de mundo, resaltado y
        /// colocación del panel, iluminación solar (apagada por defecto) y middleware IoT.
        /// Extraído de MontarGemelo el 15-08 al dividirse el montaje anclado; contenido y orden
        /// son los que tenía dentro del método.
        /// </summary>
        internal static IEnumerator MontarComunIncremental(SceneModelIndex index,
            System.Action<MetadataPanelController, WorldPanelPlacer> alTerminar)
        {
            WorldPanelPlacer colocador;
            ProgresoDeArranque.EntrarEnFase("panel");

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
            // Opacidad del fondo de la ficha. La cifra, y el razonamiento de composición que
            // la sostiene, viven en MROpacidadInterfaz: el fondo del panel es la única
            // superficie que escribe alfa bajo el texto, y ese alfa es el que el compositor
            // usa para mezclar con el vídeo de las cámaras. Aquí solo se aplica, porque el
            // mismo valor lo comparten el menú de zonas, el menú del modo anclado y el panel
            // de colocación del anclaje, y estaba escrito cuatro veces.
            //
            // El escritorio no pasa por aquí: allí el fondo del panel lo pone la propia
            // escena y el problema no existe.
            panel.SetOpacidadFondo(MROpacidadInterfaz.FondoDePanel);

            // Cesión de fotograma. La construcción de la ficha de activos es la pieza más cara
            // del montaje y NO se puede subdividir sin reescribir MetadataPanelController, que
            // construye su interfaz entera en Initialize. Queda medida en el informe de
            // arranque: si domina el resto, es la siguiente pieza a atacar.
            yield return null;

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

            // Visibilidad selectiva (T10, versión reducida, 19-08): el sensor cuya ficha está
            // abierta se sigue viendo a través de los cerramientos —con la parte oculta
            // dibujada DISTINTA, y la línea y la caja a guiones tras el muro—. Se engancha a
            // los mismos eventos que el resaltado y el colocador, después de ellos. En los dos
            // modos; si se quisiera solo en anclado, esta línea se mueve a MontarAnclado.
            MRVisibilidadSelectiva.Instalar(panel, resaltador, colocador, index);

            yield return null;

            // Mismo criterio que en escritorio: disponible pero apagada por defecto.
            Visual.SolarLightingController.Crear();
            yield return null;

            ProgresoDeArranque.EntrarEnFase("sensores");

            IoT.SensorIntegrationBootstrap.TryAttach(index, panel, tipografiaDeVisor: true);

            alTerminar?.Invoke(panel, colocador);
        }

        /// <summary>Devuelve a la escena la raíz del modelo apagada durante el selector (o la
        /// espera de transparencia). Todo lo que se monta después (oclusores, colliders de
        /// selección, sensores) da por hecho que la escena está viva.</summary>
        internal static void ReactivarRaiz(GameObject raiz)
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
        internal static void CrearInteraccion(MRControllerRig rig, Transform desplazamientoCamara,
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

            // La pantalla de carga se pone ANTES de desmontar nada: el desmontaje y la recarga
            // son exactamente el rato en que la escena está a medias, y con el visor puesto ese
            // rato no puede quedar a la vista. Sobrevive al barrido (está exceptuada por nombre)
            // y a la propia recarga, porque es persistente.
            MRPantallaDeCarga.Abrir();
            ProgresoDeArranque.Comenzar("recarga", FasesDeRecarga());
            ProgresoDeArranque.EntrarEnFase("desmontaje");

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

            // 3) Recarga y rearranque. La recarga es ASÍNCRONA desde este cambio: la síncrona
            // medía 58 ms de parada en el registro del 18-08 (01:15:26,683 → 01:15:26,741), es
            // decir, cinco fotogramas a 90 Hz en los que la imagen deja de responder a la
            // cabeza. Como el desmontaje ya ha destruido la sesión, esos fotogramas ni siquiera
            // mostraban nada útil. Con la carga asíncrona el motor la reparte y la pantalla de
            // carga sigue dibujándose y siguiendo al usuario mientras tanto.
            var recargaGo = new GameObject("~RecargaEscenaAR");
            Object.DontDestroyOnLoad(recargaGo);
            recargaGo.AddComponent<MRRecargaDeEscena>().Iniciar(escenaActiva);
        }

        /// <summary>
        /// Rearranca el punto de entrada tras una recarga de escena. Lo llama
        /// <see cref="MRRecargaDeEscena"/> cuando la carga asíncrona ha terminado; antes lo hacía
        /// el evento <c>sceneLoaded</c>, que con carga asíncrona llegaría igual pero dejaría el
        /// orden en manos del motor en vez de en las de la corrutina que conduce la recarga.
        /// </summary>
        internal static void RearrancarTrasRecarga(string nombreEscena)
        {
            Debug.LogWarning($"[DigitalTwin][AR] Escena '{nombreEscena}' recargada: se rearranca " +
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
                // La pantalla de carga lleva el mismo prefijo que el resto, pero es
                // justamente la que tiene que sobrevivir: su trabajo es cubrir esta recarga.
                if (raiz.name == MRPantallaDeCarga.NombreObjeto) continue;
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
    /// <summary>
    /// Coordina la etapa A del arranque y el paso al selector de modo, repartiendo el trabajo
    /// entre fotogramas para que el bucle principal no se detenga.
    ///
    /// Antes de este cambio la etapa A entera se ejecutaba dentro del método de entrada, es
    /// decir, en un solo fotograma: 112 ms medidos en el visor el 18-08. Aquí cada pieza tiene
    /// su fase, cede el fotograma al terminar, y las que son bucles largos (los colisionadores)
    /// ceden también por dentro con un presupuesto de tiempo.
    ///
    /// Es un MonoBehaviour porque necesita corrutinas; vive en su propio objeto para que un
    /// fallo suyo no arrastre al diagnóstico de entrada. Clase de primer nivel a propósito: los
    /// MonoBehaviour anidados funcionan con AddComponent, pero salirse del patrón del resto del
    /// proyecto no compra nada aquí.
    /// </summary>
    internal class MRBootSequencer : MonoBehaviour
    {
            /// <summary>La capa de transparencia se crea 90 fotogramas despues del arranque (ver
            /// MRPassthroughController.FotogramasDeEspera y la violacion de segmento que motivo
            /// ese retardo). Se espera ese plazo mas un margen antes de dar la transparencia por
            /// no disponible.</summary>
            private const int FotogramasDeEsperaMaxima = 210;
            private const int FotogramasDeMargenTrasReintento = 30;

            /// <summary>
            /// Fotogramas que la etapa A puede consumir sin comprometer la premisa de la espera
            /// de la transparencia.
            ///
            /// Esa espera de 90 fotogramas existe para que la capa se pida con el motor ya
            /// tranquilo: pedirla mientras se satura la GPU con la carga de escena terminó en
            /// violación de segmento del servicio del visor el 13-08. Repartir la etapa A la
            /// alarga en fotogramas, y si llegara a solaparse con el fotograma 90 estaríamos
            /// reintroduciendo por la puerta de atrás la concurrencia que aquel retardo evita.
            /// Con el presupuesto por fotograma y los 112 ms medidos, la etapa A debería caber
            /// en menos de 20 fotogramas; 60 es un margen amplio que solo se supera si algo ha
            /// cambiado de verdad. No se corrige sola: se denuncia, porque la corrección
            /// correcta depende de qué haya crecido.
            /// </summary>
            private const int FotogramasSanosDeEtapaA = 60;

            /// <summary>
            /// GUARDA DEL ARRANQUE. EL SELECTOR DE MODO ES LA ÚNICA ENTRADA A LA APLICACIÓN, y
            /// por tanto no puede depender de que la etapa A termine bien: cualquier excepción
            /// dentro de la corrutina (el motor la registra y la corrutina muere sin más), una
            /// espera que nunca se satisface o una pantalla de carga que no se retira dejarían al
            /// usuario con el visor puesto y sin nada que hacer, que es exactamente lo que pasó
            /// el 19-08. Pasado este plazo, si el selector no se ha mostrado, se fuerza con lo
            /// que haya (el rig puede faltar; el selector lo tolera y avisa), se retira la
            /// pantalla de carga y se deja en el registro en qué fase se quedó el arranque.
            ///
            /// Por qué 15 s: el camino legítimo más largo es la etapa A (1,2 s medidos el 19-08)
            /// más la espera máxima de la transparencia (210 + 30 fotogramas, unos 2,7 s a
            /// 90 Hz), es decir, unos 4 s; el selector se ha mostrado a t=5,8 s desde el inicio
            /// del proceso en las seis sesiones registradas. 15 s es más del triple de ese peor
            /// caso y queda por debajo de los 20 s a los que el propio selector ya avisa de que
            /// no hay mando: antes de esa marca el usuario tiene que tener algo delante. La
            /// pantalla de carga conserva su propia red a 30 s como último recurso.
            /// </summary>
            private const float SegundosDeGuarda = 15f;

            private SceneModelIndex _index;
            private MRControllerRig _rig;
            private Transform _desplazamientoCamara;
            private Transform _origenXR;
            private Coroutine _secuencia;
            private bool _selectorMostrado;
            private bool _terminadoSinSelector;

            public void Iniciar()
            {
                _secuencia = StartCoroutine(Secuencia());
                // La guarda corre en una corrutina APARTE: si la secuencia muere por una
                // excepción, la guarda sigue viva. Esa independencia es todo el sentido.
                StartCoroutine(Guarda());
            }

            private IEnumerator Guarda()
            {
                float inicio = Time.unscaledTime;
                while (Time.unscaledTime - inicio < SegundosDeGuarda)
                {
                    if (_selectorMostrado || _terminadoSinSelector) yield break;
                    yield return null;
                }
                if (_selectorMostrado || _terminadoSinSelector) yield break;

                Debug.LogError($"[DigitalTwin][AR] GUARDA DEL ARRANQUE: han pasado {SegundosDeGuarda} s " +
                               "y el selector de modo no se ha mostrado. Perfil '" +
                               ProgresoDeArranque.Perfil + "', ultima fase: '" +
                               ProgresoDeArranque.TextoDeFase + "' (" +
                               ProgresoDeArranque.Fraccion.ToString("0.00") + "); indice=" +
                               (_index != null ? "si" : "NO") + ", rig=" +
                               (_rig != null ? "si" : "NO") + ". Se fuerza el selector y se " +
                               "retira la pantalla de carga: el arranque ha quedado a medias y " +
                               "hay que buscar en el registro que fase no termino.");

                if (_secuencia != null) StopCoroutine(_secuencia);

                // Lo que falte se intenta completar con lo mínimo, cada pieza por separado y sin
                // que el fallo de una impida las demás.
                if (_index == null)
                {
                    try { _index = SceneModelIndex.Build(); }
                    catch (System.Exception e)
                    {
                        Debug.LogError("[DigitalTwin][AR] GUARDA: no se pudo construir el indice " +
                                       "del modelo: " + e.Message);
                    }
                }
                if (_rig == null)
                {
                    try { ResolverJerarquiaYCrearRig(); }
                    catch (System.Exception e)
                    {
                        Debug.LogError("[DigitalTwin][AR] GUARDA: no se pudo crear el rig de " +
                                       "mandos: " + e.Message + ". El selector se mostrara igual " +
                                       "y esperara a un mando.");
                    }
                }

                MostrarSelectorYRetirarCarga("guarda de " + SegundosDeGuarda + " s");
            }

            /// <summary>
            /// Resuelve la jerarquía del rig (origen de realidad extendida > desplazamiento de
            /// cámara > cámara) y crea los mandos. Devuelve false si la jerarquía no es la
            /// esperada; en ese caso no crea nada.
            /// </summary>
            private bool ResolverJerarquiaYCrearRig()
            {
                // Los anclajes de los mandos cuelgan del desplazamiento de camara, no de la raiz
                // de la escena: las poses que entrega el sistema estan en el espacio del origen
                // de realidad extendida, y colgarlas de la raiz haria que los mandos se
                // despegaran de las manos en cuanto el origen se moviera, por ejemplo al
                // desplazarse a un punto de navegacion.
                _desplazamientoCamara = Camera.main != null ? Camera.main.transform.parent : null;
                _origenXR = _desplazamientoCamara != null ? _desplazamientoCamara.parent : null;
                if (_desplazamientoCamara == null || _origenXR == null) return false;

                var rigGo = new GameObject("~MandosAR");
                rigGo.transform.SetParent(_desplazamientoCamara, false);
                _rig = rigGo.AddComponent<MRControllerRig>();
                _rig.Initialize(_desplazamientoCamara);
                return true;
            }

            /// <summary>
            /// El traspaso: selector primero, pantalla de carga después. Idempotente y SIN
            /// condiciones: lo llaman el final de la secuencia y la guarda, y gane quien gane el
            /// resultado es el mismo. Si el selector no puede construirse, se monta la navegación
            /// por nodos directamente antes que dejar al usuario sin nada.
            /// </summary>
            private void MostrarSelectorYRetirarCarga(string motivo)
            {
                if (_selectorMostrado) return;
                _selectorMostrado = true;

                Debug.LogWarning($"[DigitalTwin][AR] Selector de modo visible (transparencia " +
                                 $"activa: {TransparenciaActiva()}; via: {motivo}).");

                if (MRPerfMonitor.Instancia != null)
                    MRPerfMonitor.Instancia.FijarFase("selector");

                // El selector ES la pantalla estable a partir de aquí: se muestra ANTES de
                // retirar la de carga, para que aparezca por detrás del panel que se desvanece.
                // Un corte seco entre las dos se percibe como un parpadeo.
                bool selectorCreado = false;
                try
                {
                    var camara = Camera.main != null ? Camera.main : Object.FindFirstObjectByType<Camera>();
                    if (camara == null) throw new System.InvalidOperationException("no hay ninguna camara");
                    var index = _index; var rig = _rig;
                    var desplazamiento = _desplazamientoCamara; var origen = _origenXR;
                    MRModeSelector.Mostrar(rig, camara, modo =>
                    {
                        MRDigitalTwinBootstrap.MontarGemelo(modo, index, rig, desplazamiento, origen);
                        Destroy(gameObject);
                    });
                    selectorCreado = true;
                }
                catch (System.Exception e)
                {
                    Debug.LogError("[DigitalTwin][AR] No se ha podido construir el selector de " +
                                   "modo: " + e.Message + (_index != null
                                       ? ". Se monta navegacion por nodos directamente."
                                       : ". Sin indice del modelo no se puede montar nada."));
                }

                // Pase lo que pase, la pantalla de carga se retira y la cámara recupera su máscara.
                ProgresoDeArranque.Terminar();
                if (MRPantallaDeCarga.Instancia != null) MRPantallaDeCarga.Instancia.Cerrar();

                if (!selectorCreado && _index != null)
                {
                    MRDigitalTwinBootstrap.MontarGemelo(ModoAR.NavegacionPorNodos, _index, _rig,
                                                        _desplazamientoCamara, _origenXR);
                    Destroy(gameObject);
                }
            }

            private IEnumerator Secuencia()
            {
                int fotogramaInicial = Time.frameCount;

                // --- Índice del modelo -----------------------------------------------------
                ProgresoDeArranque.EntrarEnFase("indice");
                _index = SceneModelIndex.Build();
                var index = _index;

                // Resumen del índice a nivel de aviso: la línea detallada de SceneModelIndex es
                // un mensaje informativo y las compilaciones que no son de desarrollo lo filtran
                // del registro del dispositivo.
                Debug.LogWarning($"[DigitalTwin][AR] Indice del modelo: {index.AllElements.Count} " +
                                 $"elementos, {index.NavPoints.Count} puntos de navegacion, " +
                                 $"{index.Sensors.Count} sensores.");
                yield return null;

                // --- Colisionadores --------------------------------------------------------
                // La pieza más cara del arranque, y la única que se subdivide por dentro.
                ProgresoDeArranque.EntrarEnFase("colisionadores");
                yield return StartCoroutine(ColliderBootstrapper.SetupIncremental(
                    index, ProgresoDeArranque.ProgresoDeFase));

                // --- Jerarquía del rig y mandos --------------------------------------------
                ProgresoDeArranque.EntrarEnFase("mandos");

                _desplazamientoCamara = Camera.main != null ? Camera.main.transform.parent : null;
                _origenXR = _desplazamientoCamara != null ? _desplazamientoCamara.parent : null;

                if (_desplazamientoCamara == null || _origenXR == null)
                {
                    // Sin la jerarquía del rig no hay mandos, y sin mandos no se puede elegir
                    // modo. Antes que dejar al usuario ante un selector inoperante, se monta
                    // directamente la navegación por nodos —el modo que funciona sin anclaje—
                    // dejando constancia.
                    Debug.LogError("[DigitalTwin][AR] La camara no cuelga de la jerarquia esperada " +
                                   "(origen de realidad extendida > desplazamiento de camara > camara). " +
                                   "Sin mandos no hay selector de modo: se monta navegacion por nodos " +
                                   "directamente. Revisa el rig de la escena.");
                    _terminadoSinSelector = true;
                    ProgresoDeArranque.Terminar();
                    MRDigitalTwinBootstrap.MontarGemelo(ModoAR.NavegacionPorNodos, index, null,
                                                        null, null);
                    Destroy(gameObject);
                    yield break;
                }

                // Seguimiento a nivel de suelo, verificado y nunca supuesto: la escena lo pide
                // (XROrigin en modo Floor con desplazamiento cero desde el 15-08), pero el modo
                // efectivo lo decide el runtime y aquí se comprueba, se registra y, si no se
                // consigue, se compensa con una degradación declarada.
                MRDigitalTwinBootstrap.AsegurarSeguimientoANivelDeSuelo(_origenXR);
                yield return null;

                ResolverJerarquiaYCrearRig();
                yield return null;

                // Mientras el usuario elige modo, el gemelo no aporta nada y SÍ cuesta: el modelo
                // entero se estaba dibujando detrás de las tarjetas del selector (en estéreo y a
                // resolución de visor) sin que se viera más que el vídeo de la sala. Se apaga la
                // raíz completa y MontarGemelo la reactiva. La vía de emergencia de arriba (sin
                // rig) no pasa por aquí a propósito: monta directamente y no debe apagarse nada.
                var raizModelo = MRDigitalTwinBootstrap.RaizDelModelo(index);
                if (raizModelo != null)
                {
                    raizModelo.SetActive(false);
                    MRDigitalTwinBootstrap._raizModeloApagadaDuranteSelector = raizModelo;
                    Debug.LogWarning($"[DigitalTwin][AR] Raiz del modelo '{raizModelo.name}' " +
                                     "desactivada mientras el selector de modo este en pantalla.");
                }
                else
                {
                    Debug.LogWarning("[DigitalTwin][AR] No se ha resuelto la raiz del modelo; se " +
                                     "deja el gemelo dibujandose durante el selector (solo cuesta " +
                                     "rendimiento, no funcionalidad).");
                }

                int fotogramasEtapaA = Time.frameCount - fotogramaInicial;
                if (fotogramasEtapaA > FotogramasSanosDeEtapaA)
                {
                    Debug.LogError($"[DigitalTwin][AR] La etapa A ha consumido {fotogramasEtapaA} " +
                                   $"fotogramas (limite sano {FotogramasSanosDeEtapaA}): se acerca " +
                                   "a los 90 que la capa de transparencia espera para pedirse con " +
                                   "el motor tranquilo. Revisa que no haya crecido el trabajo de " +
                                   "arranque antes de dar por buena la siguiente sesion.");
                }
                else
                {
                    Debug.LogWarning($"[DigitalTwin][AR] Etapa A repartida en {fotogramasEtapaA} " +
                                     "fotogramas sin parada perceptible del bucle principal.");
                }

                // --- Espera de la transparencia --------------------------------------------
                ProgresoDeArranque.EntrarEnFase("camara");
                Debug.LogWarning("[DigitalTwin][AR] Arranque diferido: esperando a la transparencia " +
                                 "para mostrar el selector de modo.");

                int fotogramas = 0;
                while (fotogramas < FotogramasDeEsperaMaxima && !TransparenciaActiva())
                {
                    var transparencia = MRPassthroughController.Instancia;
                    // El avance real de la espera, que es un contador de fotogramas del propio
                    // controlador. Si no hubiera controlador se cae al reparto sobre la espera
                    // máxima, que es una estimación pero nunca retrocede.
                    ProgresoDeArranque.ProgresoDeFase(transparencia != null
                        ? transparencia.FraccionDeEsperaInicial
                        : fotogramas / (float)FotogramasDeEsperaMaxima);
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

                MostrarSelectorYRetirarCarga("secuencia normal");
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
                // Avance de esta fase: no hay forma honesta de saber cuánto falta —se espera a
                // que el sistema devuelva la sesión— así que la barra avanza hacia el punto en
                // que se declara el fallo y allí se queda. Una barra que se detiene dice la
                // verdad; una que sigue subiendo hasta el 100 % sin llegar a nada, no.
                ProgresoDeArranque.ProgresoDeFase(
                    Mathf.Min(1f, intentos / (float)IntentosAntesDeDeclararFallo));
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
                    // La pantalla de carga se retira ANTES del aviso: mientras está puesta,
                    // la cámara solo dibuja su capa y el aviso no se vería. Y a partir de aquí
                    // ya no hay nada que cargar, sino algo que contar.
                    if (MRPantallaDeCarga.Instancia != null) MRPantallaDeCarga.Instancia.Cerrar();
                    ProgresoDeArranque.Terminar();
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
                // Se recupera la pantalla de carga para cubrir el montaje, que sigue costando
                // lo mismo que si se hubiera montado a la primera.
                MRPantallaDeCarga.Abrir();
                ProgresoDeArranque.Comenzar("montaje Anclado",
                    MRDigitalTwinBootstrap.FasesDeMontaje(ModoAR.Anclado));
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
            // Rojo apagado en vez del gris azulado de los demás paneles: este aviso sale
            // cuando la cámara del visor no responde y conviene que no se confunda con una
            // ficha normal. La OPACIDAD sí es la común, para que ningún fondo del visor quede
            // fuera del criterio de MROpacidadInterfaz.
            var fondo = DigitalTwin.UI.RuntimeUIFactory.CreatePanel(rt, "Fondo",
                MROpacidadInterfaz.ConOpacidadDeFondo(new Color(0.12f, 0.05f, 0.06f)));
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

    /// <summary>
    /// Ejecuta el montaje del gemelo digital repartido entre fotogramas, en cualquiera de los
    /// dos modos.
    ///
    /// POR QUÉ EXISTE. Hasta este cambio el montaje era una llamada síncrona: entre la traza
    /// «Montaje del gemelo digital iniciado» y «Bootstrap de Realidad Aumentada completo» el
    /// registro del visor del 18-08 mide 383 ms dentro de un único fotograma. Son 34 fotogramas
    /// a 90 Hz durante los cuales el compositor de OpenXR reproyecta el último fotograma
    /// entregado: la imagen se queda pegada a la cabeza justo después de que el usuario haya
    /// disparado el gatillo, que es el peor momento posible para que deje de responder.
    ///
    /// El contenido y el ORDEN son los que tenía el montaje síncrono. Lo único que se ha
    /// añadido son cesiones de fotograma entre piezas y la declaración de fase para la pantalla
    /// de carga. En particular: en modo anclado el servicio de anclaje se crea antes que el
    /// binder para que el binder esté suscrito cuando se restaure un anclaje guardado, y la
    /// oclusión se aplica después de las piezas comunes. Nada de eso se ha movido.
    /// </summary>
    internal class MRMontajeGemelo : MonoBehaviour
    {
        private ModoAR _modo;
        private SceneModelIndex _index;
        private MRControllerRig _rig;
        private Transform _desplazamientoCamara;
        private Transform _origenXR;
        private GameObject _raizApagada;

        private MetadataPanelController _panel;
        private WorldPanelPlacer _colocador;

        public void Iniciar(ModoAR modo, SceneModelIndex index, MRControllerRig rig,
                            Transform desplazamientoCamara, Transform origenXR,
                            GameObject raizApagada)
        {
            _modo = modo;
            _index = index;
            _rig = rig;
            _desplazamientoCamara = desplazamientoCamara;
            _origenXR = origenXR;
            _raizApagada = raizApagada;
            StartCoroutine(Secuencia());
        }

        private IEnumerator Secuencia()
        {
            if (_modo == ModoAR.Anclado) yield return StartCoroutine(MontarAnclado());
            else yield return StartCoroutine(MontarNavegacionPorNodos());

            ProgresoDeArranque.Terminar();
            if (MRPantallaDeCarga.Instancia != null) MRPantallaDeCarga.Instancia.Cerrar();

            Debug.LogWarning($"[DigitalTwin][AR] Bootstrap de Realidad Aumentada completo " +
                             $"(modo {_modo}).");
            Destroy(gameObject);
        }

        private IEnumerator MontarNavegacionPorNodos()
        {
            ProgresoDeArranque.EntrarEnFase("geometria");
            MRDigitalTwinBootstrap.ReactivarRaiz(_raizApagada);
            _raizApagada = null;

            // En navegación por nodos un anclaje persistido de una sesión anterior movería el
            // edificio entero bajo los pies del usuario a mitad de recorrido, que es exactamente
            // lo contrario de lo que ese modo promete (el modelo quieto y el usuario saltando
            // entre nodos): el servicio de anclaje no se crea.
            Debug.LogWarning("[DigitalTwin][AR] Anclaje espacial no aplicable en navegacion " +
                             "por nodos: el modelo permanece en su pose de autor.");
            yield return null;

            yield return StartCoroutine(MRDigitalTwinBootstrap.MontarComunIncremental(
                _index, (panel, colocador) => { _panel = panel; _colocador = colocador; }));

            // La transparencia se apaga al entrar: la revisión remota se hace desde la
            // oficina, y el vídeo de la sala real detrás del modelo solo confunde. No se
            // persiste la preferencia: al próximo arranque el selector vuelve a mostrarse
            // sobre transparencia.
            if (MRPassthroughController.Instancia != null)
                MRPassthroughController.Instancia.Aplicar(false);

            MRNodeNavigator navegador = null;
            MRMenuZonas menuZonas = null;

            ProgresoDeArranque.EntrarEnFase("navegacion");

            if (_origenXR == null)
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
                yield return null;

                var navegadorGo = new GameObject("~NavegacionPorNodosAR");
                Object.DontDestroyOnLoad(navegadorGo);
                navegador = navegadorGo.AddComponent<MRNodeNavigator>();
                navegador.Initialize(_origenXR, Camera.main, _index, indicadores);
                navegador.ColocarEnNodoInicial();
                yield return null;

                // El menú del modo de navegación (zonas, iluminación solar, volver al
                // selector). Solo con mandos: sin rig no habría forma de abrirlo ni de elegir.
                ProgresoDeArranque.EntrarEnFase("menus");
                if (_rig != null)
                {
                    var menuGo = new GameObject("~MenuZonasARRaiz");
                    Object.DontDestroyOnLoad(menuGo);
                    menuZonas = menuGo.AddComponent<MRMenuZonas>();
                    menuZonas.Initialize(_rig, Camera.main, navegador, _index);
                    yield return null;
                }
                else
                {
                    Debug.LogWarning("[DigitalTwin][AR] Sin rig de mandos no se crea el " +
                                     "menu: no habria boton con que abrirlo.");
                }
            }

            MRDigitalTwinBootstrap.CrearInteraccion(_rig, _desplazamientoCamara, _panel,
                _colocador, navegador, _index, menuZonas, colocacionAnclaje: null,
                identificarSenalado: false);
        }

        private IEnumerator MontarAnclado()
        {
            ProgresoDeArranque.EntrarEnFase("geometria");
            MRDigitalTwinBootstrap.ReactivarRaiz(_raizApagada);
            _raizApagada = null;
            yield return null;

            ProgresoDeArranque.EntrarEnFase("anclaje");

            MRColocacionAnclaje colocacion = null;
            var anclajeGo = new GameObject("~MRAnchorService");
            Object.DontDestroyOnLoad(anclajeGo);
            var anclaje = anclajeGo.AddComponent<MRAnchorService>();

            var binder = anclajeGo.AddComponent<ModelAnchorBinder>();
            binder.Initialize(_index, anclaje, _origenXR);

            anclaje.OnEstadoCambiado += estado =>
                Debug.LogWarning($"[DigitalTwin][MR] Estado del anclaje: {estado}.");
            yield return null;

            if (_rig != null && binder.RaizModelo != null)
            {
                var colocacionGo = new GameObject("~ColocacionAnclajeAR");
                Object.DontDestroyOnLoad(colocacionGo);
                colocacion = colocacionGo.AddComponent<MRColocacionAnclaje>();
                colocacion.Initialize(_rig, Camera.main, _index, anclaje, binder, _origenXR);

                // La leyenda del mando nace en la etapa A con los controles de navegación; en
                // anclado el gatillo también toma puntos y A/X abre el menú (desde la ronda 9
                // el mismo gesto que en navegación; el panel de anclaje se reabre desde él).
                _rig.FijarLeyenda("Gatillo · seleccionar / tomar punto\n" +
                                  "A o X · menu\n" +
                                  "Joystick · desplazar la ficha");
                yield return null;
            }
            else
            {
                Debug.LogError("[DigitalTwin][AR] Sin rig de mandos o sin raiz de modelo no se crea la " +
                               "interfaz de colocacion: el anclaje solo podra restaurarse, nunca crearse.");
            }

            yield return StartCoroutine(MRDigitalTwinBootstrap.MontarComunIncremental(
                _index, (panel, colocador) => { _panel = panel; _colocador = colocador; }));

            // La geometría pasa a oclusor invisible o desaparece (solo-profundidad desde el
            // primer fotograma; el canario de revelado verde se retiró el 15-08 tras cumplir su
            // función diagnóstica) y los marcadores quedan fuera de la selección.
            ProgresoDeArranque.EntrarEnFase("oclusion");
            yield return StartCoroutine(MROcclusionService.AplicarIncremental(
                _index, ProgresoDeArranque.ProgresoDeFase, null));
            ColliderBootstrapper.ExcluirPuntosDeNavegacionDeLaSeleccion(_index);

            // Menú del modo anclado (ronda 9): mismo gesto y misma forma que el menú de
            // navegación, para que se aprenda una sola vez. NO contiene zonas —aquí el
            // desplazamiento es físico y un teletransporte desincronizaría la vista del
            // cuerpo, la misma razón por la que este modo no ofrece puntos de navegación—;
            // aloja el panel de anclaje, rehacer el anclaje y la vuelta al selector de modo.
            ProgresoDeArranque.EntrarEnFase("menus");
            MRMenuAnclado menuAnclado = null;
            if (_rig != null)
            {
                var menuAncladoGo = new GameObject("~MenuAncladoARRaiz");
                Object.DontDestroyOnLoad(menuAncladoGo);
                menuAnclado = menuAncladoGo.AddComponent<MRMenuAnclado>();
                menuAnclado.Initialize(_rig, Camera.main, colocacion);
                yield return null;
            }
            else
            {
                Debug.LogWarning("[DigitalTwin][AR] Sin rig de mandos no se crea el menu del " +
                                 "modo anclado: no habria boton con que abrirlo.");
            }

            // La etiqueta de señalado solo existe en anclado: con los oclusores invisibles y en
            // un entorno oscuro es la única respuesta continua a «qué estoy señalando» que no
            // exige disparar ni depende de la iluminación de la sala.
            MRDigitalTwinBootstrap.CrearInteraccion(_rig, _desplazamientoCamara, _panel,
                _colocador, navegador: null, index: _index, menuZonas: null,
                colocacionAnclaje: colocacion, identificarSenalado: true,
                menuAnclado: menuAnclado);
        }
    }

    /// <summary>
    /// Conduce la recarga de escena de la vuelta al selector, de forma asíncrona y con la
    /// pantalla de carga puesta.
    ///
    /// La versión anterior llamaba a <c>SceneManager.LoadScene</c>, que descarga y carga dentro
    /// del mismo fotograma: 58 ms medidos en el visor el 18-08. La carga asíncrona deja que el
    /// motor la reparta y devuelve el control cada fotograma, con lo que la aplicación sigue
    /// entregando imágenes al compositor durante toda la operación. El fotograma de activación
    /// —cuando el motor sustituye una escena por otra— sigue siendo caro y no hay forma de
    /// evitarlo desde la aplicación; lo que se elimina es todo lo demás.
    /// </summary>
    internal class MRRecargaDeEscena : MonoBehaviour
    {
        private string _escena;

        public void Iniciar(string escena)
        {
            _escena = escena;
            StartCoroutine(Secuencia());
        }

        private IEnumerator Secuencia()
        {
            ProgresoDeArranque.EntrarEnFase("escena");

            var operacion = SceneManager.LoadSceneAsync(_escena);
            if (operacion == null)
            {
                Debug.LogError($"[DigitalTwin][AR] La carga asincrona de '{_escena}' no ha " +
                               "devuelto operacion. Se recarga de forma sincrona como respaldo: " +
                               "habra una parada, pero es preferible a quedarse sin escena.");
                SceneManager.LoadScene(_escena);
                MRDigitalTwinBootstrap.RearrancarTrasRecarga(_escena);
                Destroy(gameObject);
                yield break;
            }

            while (!operacion.isDone)
            {
                ProgresoDeArranque.ProgresoDeFase(operacion.progress);
                yield return null;
            }
            ProgresoDeArranque.ProgresoDeFase(1f);

            MRDigitalTwinBootstrap.RearrancarTrasRecarga(_escena);
            Destroy(gameObject);
        }
    }
}
