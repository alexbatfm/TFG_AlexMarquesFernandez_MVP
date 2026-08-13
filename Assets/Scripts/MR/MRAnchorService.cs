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
    /// XR_HTC_anchor_persistant) en el HTC Vive Focus Vision.
    ///
    /// Por qué anchors y no un marcador impreso: el diseño inicial (ver
    /// TFG/docs/roadmap/ADR-001-integracion-ar.md) preveía anclar el modelo con una imagen
    /// de referencia al estilo ARTrackedImageManager de AR Foundation. Al revisar las
    /// extensiones OpenXR que el Focus Vision soporta realmente, no hay ninguna de
    /// seguimiento de imágenes: lo que sí ofrece son anchors espaciales persistentes. Por
    /// eso el flujo es "colocar una vez y recordar" en lugar de "reconocer un marcador cada
    /// vez", que además encaja mejor con el caso de uso real (un operario no debería tener
    /// que ir pegando marcadores por el edificio).
    ///
    /// Flujo de uso:
    ///   1. Primera ejecución: el operario sitúa el modelo sobre el edificio real y confirma.
    ///      Se crea el anchor y se persiste con un nombre conocido.
    ///   2. Ejecuciones siguientes: se recupera ese anchor persistido y el modelo vuelve
    ///      automáticamente a la misma posición física, sin recolocarlo.
    ///
    /// Todas las llamadas al SDK están guardadas por comprobaciones de soporte: si las
    /// extensiones no están activas (por ejemplo al abrir la escena en el Editor sin visor),
    /// el servicio informa por evento y no lanza excepciones, para que la escena siga siendo
    /// abrible y depurable en escritorio.
    /// </summary>
    public class MRAnchorService : MonoBehaviour
    {
        /// <summary>
        /// Nombre con el que se persiste el anchor del edificio. Es una constante y no un
        /// campo configurable a propósito: es la clave que permite reencontrar el anclaje
        /// entre sesiones, y cambiarla equivale a perder el anclaje guardado.
        /// </summary>
        public const string PersistedAnchorName = "GemeloDigital_OrigenEdificio";

        public enum EstadoAnclaje
        {
            Inicializando,
            NoSoportado,
            EsperandoColocacion,
            Anclado,
            Error
        }

        public EstadoAnclaje Estado { get; private set; } = EstadoAnclaje.Inicializando;
        public string UltimoError { get; private set; }

        /// <summary>Se dispara con la pose del anclaje cada vez que este queda establecido.</summary>
        public event Action<Pose> OnAnclado;
        /// <summary>Se dispara cuando cambia el estado, para que la UI pueda reaccionar.</summary>
        public event Action<EstadoAnclaje> OnEstadoCambiado;

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
                Fallo(EstadoAnclaje.NoSoportado,
                      "La extensión de anchors espaciales no está disponible. Comprueba que " +
                      "'VIVE XR Anchor' esté activada en Project Settings > XR Plug-in Management > OpenXR.");
                return;
            }

            if (!AnchorManager.IsPersistedAnchorSupported())
            {
                // No es fatal: se puede anclar en cada sesión, solo se pierde la persistencia.
                Debug.LogWarning("[DigitalTwin][MR] Los anchors persistentes no están disponibles; " +
                                 "el anclaje habrá que rehacerlo en cada ejecución.");
                CambiarEstado(EstadoAnclaje.EsperandoColocacion);
                return;
            }

            AdquirirColeccionPersistida();
        }

        private void OnDestroy()
        {
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

        /// <summary>
        /// Busca el anchor guardado en sesiones anteriores. Si existe lo recupera y ancla el
        /// modelo sin intervención del usuario; si no, pasa a modo colocación manual.
        /// </summary>
        private void IntentarRestaurar()
        {
            XrResult res = AnchorManager.EnumeratePersistedAnchorNames(out string[] nombres);
            if (res != XrResult.XR_SUCCESS || nombres == null || Array.IndexOf(nombres, PersistedAnchorName) < 0)
            {
                Debug.LogWarning("[DigitalTwin][MR] No hay anclaje guardado de una sesión anterior; " +
                          "hay que colocar el modelo sobre el edificio.");
                CambiarEstado(EstadoAnclaje.EsperandoColocacion);
                return;
            }

            var tarea = AnchorManager.CreateSpatialAnchorFromPersistedAnchor(
                PersistedAnchorName, PersistedAnchorName);
            tarea.AutoComplete();
            tarea.AutoCompleteTask.ContinueWith(_ => EncolarEnHiloPrincipal(() =>
            {
                var (r, anchor) = tarea.Result;
                if (r != XrResult.XR_SUCCESS || anchor == null)
                {
                    Debug.LogWarning($"[DigitalTwin][MR] El anclaje guardado no se ha podido recuperar ({r}); " +
                                     "se pedirá colocarlo de nuevo.");
                    CambiarEstado(EstadoAnclaje.EsperandoColocacion);
                    return;
                }

                _anchor = anchor;
                PublicarPose("anclaje recuperado de una sesión anterior");
            }));
        }

        /// <summary>
        /// Fija el anclaje en la pose indicada (la que haya elegido el operario) y lo persiste
        /// para las siguientes sesiones. Es el punto de entrada que llama la UI de colocación.
        /// </summary>
        public void ColocarEnPose(Pose pose)
        {
            if (Estado == EstadoAnclaje.NoSoportado) return;

            _anchor = AnchorManager.CreateAnchor(pose, PersistedAnchorName);
            if (_anchor == null)
            {
                Fallo(EstadoAnclaje.Error, "No se ha podido crear el anchor espacial en la pose indicada.");
                return;
            }

            PublicarPose("anclaje creado por colocación manual");

            if (_coleccionLista && AnchorManager.IsPersistedAnchorSupported())
                PersistirAnclaje();
        }

        private void PersistirAnclaje()
        {
            var tarea = AnchorManager.PersistAnchor(_anchor, PersistedAnchorName);
            tarea.AutoComplete();
            tarea.AutoCompleteTask.ContinueWith(_ => EncolarEnHiloPrincipal(() =>
            {
                if (tarea.Result == XrResult.XR_SUCCESS)
                    Debug.LogWarning("[DigitalTwin][MR] Anclaje guardado; en la próxima ejecución se restaurará solo.");
                else
                    Debug.LogWarning($"[DigitalTwin][MR] El anclaje no se ha podido guardar ({tarea.Result}); " +
                                     "funcionará en esta sesión pero habrá que recolocarlo en la siguiente.");
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

        /// <summary>Borra el anclaje guardado, para poder recolocar el modelo desde cero.</summary>
        public void OlvidarAnclaje()
        {
            if (_coleccionLista) AnchorManager.UnpersistAnchor(PersistedAnchorName);
            _anchor = null;
            CambiarEstado(EstadoAnclaje.EsperandoColocacion);
        }

        private void PublicarPose(string motivo)
        {
            if (!TryGetPose(out Pose pose))
            {
                Fallo(EstadoAnclaje.Error, "El anchor existe pero su pose todavía no es consultable.");
                return;
            }

            Debug.LogWarning($"[DigitalTwin][MR] {motivo}: posición {pose.position}.");
            CambiarEstado(EstadoAnclaje.Anclado);
            OnAnclado?.Invoke(pose);
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
