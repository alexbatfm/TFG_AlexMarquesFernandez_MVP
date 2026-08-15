using System;
using UnityEngine;
using VIVE.OpenXR;
using VIVE.OpenXR.Toolkits;
using VIVE.OpenXR.Toolkits.Anchor;

namespace DigitalTwin.MR
{
    /// <summary>
    /// Fase 5 - Gestión del anclaje espacial del gemelo digital sobre el edificio real,
    /// usando las spatial anchors de VIVE OpenXR (extensiones XR_HTC_anchor y
    /// XR_HTC_anchor_persistence) en el HTC Vive Focus Vision.
    ///
    /// Por qué anchors y no un marcador impreso: el diseño inicial (ver
    /// TFG/docs/roadmap/ADR-001-integracion-ar.md) preveía anclar el modelo con una imagen
    /// de referencia al estilo ARTrackedImageManager de AR Foundation. Al revisar las
    /// extensiones OpenXR que el Focus Vision soporta realmente, no hay ninguna de
    /// seguimiento de imágenes: lo que sí ofrece son anchors espaciales persistentes. Por
    /// eso el flujo es "colocar una vez y recordar" en lugar de "reconocer un marcador cada
    /// vez", que además encaja mejor con el caso de uso real (un operario no debería tener
    /// que ir pegando marcadores por el edificio). El precio, que hay que decir: un anclaje
    /// espacial arrastra la deriva del seguimiento del propio dispositivo, y el fabricante de
    /// la plataforma de referencia recomienda no dibujar contenido a más de 3 m de su anclaje
    /// por el efecto brazo de palanca; un edificio entero colgado de un solo anclaje está
    /// fuera de esa recomendación y se documenta como limitación conocida.
    ///
    /// QUIÉN DECIDE LA POSE (desde el 15-08 por la tarde). Este servicio no sabe nada de la geometría del
    /// modelo: recibe una pose y la ancla. La pose la calcula la interfaz de colocación
    /// (<see cref="MRColocacionAnclaje"/>) a partir de un REGISTRO POR PARES DE PUNTOS
    /// (<see cref="MRRegistroPorPuntos"/>) y la aplica al modelo a través de
    /// <see cref="ModelAnchorBinder"/>; lo que aquí se ancla es la pose de un ELEMENTO DE
    /// REFERENCIA del modelo (identificado por su GlobalId IFC), de forma que en la siguiente
    /// sesión baste con volver a llevar ese elemento a la pose recuperada. Por eso el
    /// GlobalId de la referencia viaja DENTRO DEL NOMBRE del anclaje persistido
    /// (<see cref="PrefijoNombrePersistido"/> + GlobalId): anclaje y referencia no pueden
    /// desincronizarse porque son el mismo dato.
    ///
    /// Flujo de uso:
    ///   1. Primera ejecución: el operario registra el modelo (varios puntos) y confirma. Se
    ///      crea el anchor en la pose de la referencia y se persiste con su nombre.
    ///   2. Ejecuciones siguientes: se recupera ese anchor persistido y el modelo vuelve
    ///      automáticamente a la misma posición física, sin recolocarlo.
    ///
    /// Todas las llamadas al SDK están guardadas por comprobaciones de soporte: si las
    /// extensiones no están activas (por ejemplo al abrir la escena en el Editor sin visor),
    /// el servicio informa por evento y no lanza excepciones, para que la escena siga siendo
    /// abrible y depurable en escritorio. Las poses que entrega y recibe están en el ESPACIO
    /// DE SEGUIMIENTO (el del origen de realidad extendida), no en el de mundo: la
    /// conversión la hace quien conoce el origen (el binder).
    /// </summary>
    public class MRAnchorService : MonoBehaviour
    {
        /// <summary>
        /// Prefijo del nombre con el que se persiste el anchor del edificio. El nombre completo
        /// es prefijo + GlobalId IFC del elemento de referencia. Es una constante y no un campo
        /// configurable a propósito: es la clave que permite reencontrar el anclaje entre
        /// sesiones, y cambiarla equivale a perder el anclaje guardado.
        /// </summary>
        public const string PrefijoNombrePersistido = "GemeloDigital_";

        public enum EstadoAnclaje
        {
            Inicializando,
            NoSoportado,
            EsperandoColocacion,
            Anclado,
            Error
        }

        /// <summary>Persistir es una operación aparte de anclar y puede fallar por su cuenta;
        /// se expone separado para que la interfaz distinga «anclado» de «guardado».</summary>
        public enum EstadoPersistencia
        {
            SinIntentar,
            NoSoportada,
            Guardando,
            Guardado,
            Fallo
        }

        public EstadoAnclaje Estado { get; private set; } = EstadoAnclaje.Inicializando;
        public EstadoPersistencia Persistencia { get; private set; } = EstadoPersistencia.SinIntentar;
        public string UltimoError { get; private set; }

        /// <summary>GlobalId del elemento de referencia del anclaje vigente (o vacío).</summary>
        public string ReferenciaGlobalId { get; private set; } = string.Empty;

        /// <summary>Verdadero si el anclaje vigente procede de una sesión anterior (restaurado),
        /// falso si se ha creado en esta.</summary>
        public bool AnclajeRestaurado { get; private set; }

        /// <summary>Se dispara con la pose del anclaje EN ESPACIO DE SEGUIMIENTO y el GlobalId
        /// del elemento de referencia cada vez que el anclaje queda establecido.</summary>
        public event Action<Pose, string> OnAnclado;
        /// <summary>Se dispara cuando cambia el estado, para que la UI pueda reaccionar.</summary>
        public event Action<EstadoAnclaje> OnEstadoCambiado;
        /// <summary>Resultado de la persistencia (éxito y detalle legible).</summary>
        public event Action<bool, string> OnPersistencia;

        private AnchorManager.Anchor _anchor;
        private FutureTask<(XrResult, IntPtr)> _tareaColeccion;
        private bool _coleccionLista;

        /// <summary>
        /// Cola de trabajo pendiente de ejecutar en el hilo principal.
        ///
        /// Es necesaria porque las continuaciones de los FutureTask del SDK
        /// (<c>AutoCompleteTask.ContinueWith</c>) se ejecutan en un hilo del pool de .NET, no
        /// en el de Unity. Desde ahí no es seguro ni llamar a la API de OpenXR ni tocar
        /// objetos de la escena: hacerlo provoca fallos intermitentes y difíciles de
        /// reproducir. Por eso las continuaciones se limitan a encolar aquí lo que haya que
        /// hacer, y <see cref="Update"/> lo ejecuta ya en el hilo correcto.
        ///
        /// (Los ejemplos del SDK usan ContinueWith directamente, pero solo para levantar
        /// banderas de UI; aquí hay llamadas reales al runtime de por medio.)
        /// </summary>
        private readonly System.Collections.Generic.Queue<Action> _pendienteHiloPrincipal =
            new System.Collections.Generic.Queue<Action>();

        private void EncolarEnHiloPrincipal(Action accion)
        {
            lock (_pendienteHiloPrincipal) _pendienteHiloPrincipal.Enqueue(accion);
        }

        private void Update()
        {
            while (true)
            {
                Action accion;
                lock (_pendienteHiloPrincipal)
                {
                    if (_pendienteHiloPrincipal.Count == 0) break;
                    accion = _pendienteHiloPrincipal.Dequeue();
                }
                accion();
            }
        }

        private void Start()
        {
            if (!AnchorManager.IsSupported())
            {
                Persistencia = EstadoPersistencia.NoSoportada;
                Fallo(EstadoAnclaje.NoSoportado,
                      "La extensión de anchors espaciales no está disponible (en el Editor es lo " +
                      "esperado; en el visor, comprueba que 'VIVE XR Anchor' esté activada en " +
                      "Project Settings > XR Plug-in Management > OpenXR). El registro por puntos " +
                      "funciona igualmente, pero no se guarda entre sesiones.");
                return;
            }

            if (!AnchorManager.IsPersistedAnchorSupported())
            {
                // No es fatal: se puede anclar en cada sesión, solo se pierde la persistencia.
                Persistencia = EstadoPersistencia.NoSoportada;
                Debug.LogWarning("[DigitalTwin][MR] Los anchors persistentes no están disponibles; " +
                                 "el anclaje habrá que rehacerlo en cada ejecución.");
                CambiarEstado(EstadoAnclaje.EsperandoColocacion);
                return;
            }

            AdquirirColeccionPersistida();
        }

        private void OnDestroy()
        {
            LiberarAnchorActual();
            // Liberar la colección al salir; si no, queda retenida entre recargas de escena.
            if (_coleccionLista) AnchorManager.ReleasePersistedAnchorCollection();
            _tareaColeccion = null;
        }

        /// <summary>
        /// La colección de anchors persistidos hay que adquirirla antes de poder consultarla
        /// o escribir en ella. Es una operación asíncrona del runtime (devuelve un FutureTask).
        /// </summary>
        private void AdquirirColeccionPersistida()
        {
            if (AnchorManager.IsPersistedAnchorCollectionAcquired())
            {
                _coleccionLista = true;
                IntentarRestaurar();
                return;
            }

            if (_tareaColeccion != null) return;

            _tareaColeccion = AnchorManager.AcquirePersistedAnchorCollection();
            _tareaColeccion.AutoComplete();
            _tareaColeccion.AutoCompleteTask.ContinueWith(_ => EncolarEnHiloPrincipal(() =>
            {
                _tareaColeccion = null;
                _coleccionLista = AnchorManager.IsPersistedAnchorCollectionAcquired();
                if (_coleccionLista)
                {
                    IntentarRestaurar();
                }
                else
                {
                    Debug.LogWarning("[DigitalTwin][MR] No se ha podido adquirir la colección de anchors " +
                                     "persistidos; se pedirá colocar el modelo manualmente.");
                    CambiarEstado(EstadoAnclaje.EsperandoColocacion);
                }
            }));
        }

        /// <summary>Nombre persistido del edificio si existe alguno (el primero con el prefijo),
        /// o null.</summary>
        private static string BuscarNombrePersistido(out int cuantosConPrefijo)
        {
            cuantosConPrefijo = 0;
            XrResult res = AnchorManager.EnumeratePersistedAnchorNames(out string[] nombres);
            if (res != XrResult.XR_SUCCESS || nombres == null) return null;
            string primero = null;
            foreach (var n in nombres)
            {
                if (string.IsNullOrEmpty(n) || !n.StartsWith(PrefijoNombrePersistido, StringComparison.Ordinal))
                    continue;
                cuantosConPrefijo++;
                if (primero == null) primero = n;
            }
            return primero;
        }

        /// <summary>
        /// Busca el anchor guardado en sesiones anteriores. Si existe lo recupera y ancla el
        /// modelo sin intervención del usuario; si no, pasa a modo colocación manual.
        /// </summary>
        private void IntentarRestaurar()
        {
            string nombre = BuscarNombrePersistido(out int cuantos);
            if (nombre == null)
            {
                Debug.LogWarning("[DigitalTwin][MR] No hay anclaje guardado de una sesión anterior; " +
                                 "hay que registrar el modelo sobre el edificio.");
                CambiarEstado(EstadoAnclaje.EsperandoColocacion);
                return;
            }
            if (cuantos > 1)
                Debug.LogWarning($"[DigitalTwin][MR] Hay {cuantos} anclajes guardados con el prefijo del " +
                                 $"edificio; se usa '{nombre}'. Al recolocar se limpian todos.");

            string referencia = nombre.Substring(PrefijoNombrePersistido.Length);

            var tarea = AnchorManager.CreateSpatialAnchorFromPersistedAnchor(nombre, nombre);
            tarea.AutoComplete();
            tarea.AutoCompleteTask.ContinueWith(_ => EncolarEnHiloPrincipal(() =>
            {
                var (r, anchor) = tarea.Result;
                if (r != XrResult.XR_SUCCESS || anchor == null)
                {
                    Debug.LogWarning($"[DigitalTwin][MR] El anclaje guardado '{nombre}' no se ha podido " +
                                     $"recuperar ({r}); se pedirá registrar el modelo de nuevo.");
                    CambiarEstado(EstadoAnclaje.EsperandoColocacion);
                    return;
                }

                LiberarAnchorActual();
                _anchor = anchor;
                ReferenciaGlobalId = referencia;
                AnclajeRestaurado = true;
                Persistencia = EstadoPersistencia.Guardado;
                PublicarPose($"anclaje recuperado de una sesión anterior (referencia {referencia})");
            }));
        }

        /// <summary>
        /// Fija el anclaje en la pose indicada (EN ESPACIO DE SEGUIMIENTO; la pose del elemento
        /// de referencia tras el registro) y lo persiste para las siguientes sesiones. Es el
        /// punto de entrada que llama la interfaz de colocación. Un anclaje anterior de esta
        /// sesión se libera y cualquier anclaje persistido del edificio se borra antes de
        /// guardar el nuevo: el SDK exige nombres únicos y un nombre repetido haría fallar la
        /// persistencia en silencio.
        /// </summary>
        public void ColocarEnPose(Pose poseSeguimiento, string referenciaGlobalId)
        {
            if (Estado == EstadoAnclaje.NoSoportado)
            {
                Debug.LogWarning("[DigitalTwin][MR] ColocarEnPose ignorado: anclaje espacial no soportado " +
                                 "en este entorno (el registro por puntos ya está aplicado al modelo).");
                return;
            }
            if (string.IsNullOrEmpty(referenciaGlobalId))
            {
                Fallo(EstadoAnclaje.Error, "ColocarEnPose sin GlobalId de referencia: no se puede nombrar el anclaje.");
                return;
            }

            LiberarAnchorActual();
            if (_coleccionLista) BorrarPersistidosDelEdificio();

            string nombre = PrefijoNombrePersistido + referenciaGlobalId;
            _anchor = AnchorManager.CreateAnchor(poseSeguimiento, nombre);
            if (_anchor == null)
            {
                Fallo(EstadoAnclaje.Error, "No se ha podido crear el anchor espacial en la pose indicada.");
                return;
            }

            ReferenciaGlobalId = referenciaGlobalId;
            AnclajeRestaurado = false;
            PublicarPose($"anclaje creado por registro manual (referencia {referenciaGlobalId})");

            if (_coleccionLista && AnchorManager.IsPersistedAnchorSupported())
            {
                PersistirAnclaje(nombre);
            }
            else
            {
                Persistencia = EstadoPersistencia.NoSoportada;
                OnPersistencia?.Invoke(false, "persistencia no disponible: el anclaje vale solo para esta sesión");
            }
        }

        private void PersistirAnclaje(string nombre)
        {
            Persistencia = EstadoPersistencia.Guardando;
            var tarea = AnchorManager.PersistAnchor(_anchor, nombre);
            tarea.AutoComplete();
            tarea.AutoCompleteTask.ContinueWith(_ => EncolarEnHiloPrincipal(() =>
            {
                if (tarea.Result == XrResult.XR_SUCCESS)
                {
                    Persistencia = EstadoPersistencia.Guardado;
                    Debug.LogWarning($"[DigitalTwin][MR] Anclaje guardado como '{nombre}'; en la próxima " +
                                     "ejecución se restaurará solo.");
                    OnPersistencia?.Invoke(true, "guardado para las próximas sesiones");
                }
                else
                {
                    Persistencia = EstadoPersistencia.Fallo;
                    Debug.LogWarning($"[DigitalTwin][MR] El anclaje no se ha podido guardar ({tarea.Result}); " +
                                     "funcionará en esta sesión pero habrá que recolocarlo en la siguiente.");
                    OnPersistencia?.Invoke(false, $"no guardado ({tarea.Result}): vale solo para esta sesión");
                }
            }));
        }

        /// <summary>
        /// Pose actual del anclaje en el espacio de seguimiento. Se consulta cada vez en lugar
        /// de cachearla porque el runtime puede reajustarla al refinar su mapa del entorno.
        /// </summary>
        public bool TryGetPose(out Pose pose)
        {
            pose = default;
            return _anchor != null && AnchorManager.GetTrackingSpacePose(_anchor, out pose);
        }

        /// <summary>Borra el anclaje guardado y libera el vigente, para poder registrar el
        /// modelo desde cero.</summary>
        public void OlvidarAnclaje()
        {
            if (_coleccionLista) BorrarPersistidosDelEdificio();
            LiberarAnchorActual();
            ReferenciaGlobalId = string.Empty;
            AnclajeRestaurado = false;
            if (Persistencia != EstadoPersistencia.NoSoportada) Persistencia = EstadoPersistencia.SinIntentar;
            Debug.LogWarning("[DigitalTwin][MR] Anclaje olvidado: sin anclaje vigente ni guardado.");
            CambiarEstado(Estado == EstadoAnclaje.NoSoportado ? EstadoAnclaje.NoSoportado
                                                              : EstadoAnclaje.EsperandoColocacion);
        }

        private void BorrarPersistidosDelEdificio()
        {
            XrResult res = AnchorManager.EnumeratePersistedAnchorNames(out string[] nombres);
            if (res != XrResult.XR_SUCCESS || nombres == null) return;
            foreach (var n in nombres)
            {
                if (string.IsNullOrEmpty(n) || !n.StartsWith(PrefijoNombrePersistido, StringComparison.Ordinal))
                    continue;
                XrResult r = AnchorManager.UnpersistAnchor(n);
                Debug.LogWarning($"[DigitalTwin][MR] Anclaje persistido '{n}' borrado ({r}).");
            }
        }

        private void LiberarAnchorActual()
        {
            if (_anchor == null) return;
            try { _anchor.Dispose(); }
            catch (Exception e) { Debug.LogWarning($"[DigitalTwin][MR] Al liberar el anchor anterior: {e.Message}"); }
            _anchor = null;
        }

        private void PublicarPose(string motivo)
        {
            if (!TryGetPose(out Pose pose))
            {
                Fallo(EstadoAnclaje.Error, "El anchor existe pero su pose todavía no es consultable.");
                return;
            }

            Debug.LogWarning($"[DigitalTwin][MR] {motivo}: posición (seguimiento) {pose.position}, " +
                             $"guiñada {pose.rotation.eulerAngles.y:0.0}°.");
            CambiarEstado(EstadoAnclaje.Anclado);
            OnAnclado?.Invoke(pose, ReferenciaGlobalId);
        }

        private void Fallo(EstadoAnclaje estado, string mensaje)
        {
            UltimoError = mensaje;
            Debug.LogWarning("[DigitalTwin][MR] " + mensaje);
            CambiarEstado(estado);
        }

        private void CambiarEstado(EstadoAnclaje nuevo)
        {
            if (Estado == nuevo) return;
            Estado = nuevo;
            OnEstadoCambiado?.Invoke(nuevo);
        }
    }
}
