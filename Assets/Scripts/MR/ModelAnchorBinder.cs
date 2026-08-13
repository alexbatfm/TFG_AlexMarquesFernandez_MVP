using DigitalTwin.Core;
using IFCImporter;
using UnityEngine;

namespace DigitalTwin.MR
{
    /// <summary>
    /// Fase 5 - Coloca el modelo BIM en el mundo real a partir de la pose del anclaje
    /// espacial que proporciona <see cref="MRAnchorService"/>.
    ///
    /// El problema que resuelve: el anchor devuelve una pose del mundo físico, pero el modelo
    /// tiene su propio origen, heredado del IFC, que no coincide con ningún punto notable del
    /// edificio. Colocar el modelo directamente en la pose del anchor lo dejaría desplazado
    /// tantos metros como diste el origen del IFC del punto físico donde el operario colocó el
    /// anclaje. Lo que hay que hacer es calcular la transformación que lleva un punto de
    /// referencia conocido del modelo hasta la pose del anchor, y aplicarla al modelo entero.
    ///
    /// Como punto de referencia se usa uno de los puntos de navegación "Esfera..." del propio
    /// IFC, elegido por su GlobalId (ver <see cref="GlobalIdPuntoReferencia"/>).
    /// Se eligen estos y no el origen del modelo porque son posiciones reconocibles físicamente
    /// dentro del edificio: el operario puede situarse en ese punto real y confirmar allí el
    /// anclaje, que es un gesto mucho más preciso que intentar adivinar dónde cae un origen
    /// abstracto de coordenadas.
    /// </summary>
    public class ModelAnchorBinder : MonoBehaviour
    {
        /// <summary>
        /// GlobalId IFC del punto "Esfera..." que se usa como referencia física.
        ///
        /// Se identifica por GlobalId y no por índice a propósito: <c>FindObjectsByType</c>, que
        /// es lo que alimenta <see cref="SceneModelIndex"/>, no garantiza un orden estable entre
        /// ejecuciones ni entre versiones de Unity. Un índice podría apuntar hoy al recibidor y
        /// mañana a un baño, desplazando el edificio entero sin ningún error visible. El
        /// GlobalId viene del IFC y no cambia nunca.
        ///
        /// Por defecto, el punto del Recibidor (la entrada del edificio): es el sitio más fácil
        /// de localizar físicamente por un operario que llega de fuera.
        /// </summary>
        [Tooltip("GlobalId IFC del punto 'Esfera...' usado como referencia. Por defecto, el del Recibidor.")]
        public string GlobalIdPuntoReferencia = "0rnbMfC4L9qhDOi7l3AfPB";

        [Tooltip("Si está activo, solo se alinea el giro en horizontal (yaw). Recomendado: el " +
                 "edificio no debe inclinarse aunque el anclaje se cree con el mando torcido.")]
        public bool SoloGiroHorizontal = true;

        private SceneModelIndex _index;
        private Transform _raizModelo;
        private Transform _puntoReferencia;

        public bool EstaVinculado { get; private set; }

        public void Initialize(SceneModelIndex index, MRAnchorService anclaje)
        {
            _index = index;
            _raizModelo = ResolverRaizModelo(index);
            _puntoReferencia = ResolverPuntoReferencia(index);

            if (_raizModelo == null || _puntoReferencia == null)
            {
                Debug.LogError("[DigitalTwin][MR] No se puede vincular el modelo al anclaje: " +
                               "falta la raíz del modelo o un punto de referencia 'Esfera...'.");
                enabled = false;
                return;
            }

            anclaje.OnAnclado += AplicarAnclaje;
        }

        /// <summary>
        /// Raíz del modelo importado: se sube por la jerarquía desde cualquier elemento con
        /// metadatos hasta el objeto más alto que siga formando parte del modelo. Así no hay
        /// que codificar a mano el nombre del GameObject del .glb, que podría cambiar al
        /// reimportarlo.
        /// </summary>
        private static Transform ResolverRaizModelo(SceneModelIndex index)
        {
            if (index.AllElements.Count == 0) return null;

            Transform t = index.AllElements[0].transform;
            while (t.parent != null) t = t.parent;
            return t;
        }

        private IfcMetadata _metaReferencia;

        private Transform ResolverPuntoReferencia(SceneModelIndex index)
        {
            if (index.NavPoints.Count == 0) return null;

            foreach (var punto in index.NavPoints)
            {
                if (punto != null && punto.globalId == GlobalIdPuntoReferencia)
                {
                    _metaReferencia = punto;
                    return punto.transform;
                }
            }

            // Si el GlobalId configurado no aparece (modelo distinto, o reexportado con otros
            // identificadores) se cae al primer punto para no dejar el sistema inservible, pero
            // se avisa bien claro: el modelo quedará anclado en un sitio que no es el previsto.
            _metaReferencia = index.NavPoints[0];
            Debug.LogWarning($"[DigitalTwin][MR] No se ha encontrado el punto de referencia con GlobalId " +
                             $"'{GlobalIdPuntoReferencia}' entre los {index.NavPoints.Count} puntos del modelo. " +
                             $"Se usa '{_metaReferencia.ifcName}' como sustituto: comprueba que el anclaje " +
                             $"queda donde esperas, o corrige GlobalIdPuntoReferencia.");
            return _metaReferencia.transform;
        }

        /// <summary>
        /// Mueve y gira el modelo para que su punto de referencia coincida exactamente con la
        /// pose del anclaje. Se opera sobre la raíz para no tocar las posiciones relativas
        /// internas del modelo, que vienen del IFC y deben conservarse intactas.
        /// </summary>
        private void AplicarAnclaje(Pose pose)
        {
            Quaternion giroDestino = pose.rotation;
            if (SoloGiroHorizontal)
            {
                // Proyectar el "adelante" del anclaje al plano horizontal: si el operario crea
                // el anclaje con el mando inclinado, el edificio no debe quedar torcido.
                Vector3 adelante = Vector3.ProjectOnPlane(pose.rotation * Vector3.forward, Vector3.up);
                giroDestino = adelante.sqrMagnitude > 0.0001f
                    ? Quaternion.LookRotation(adelante.normalized, Vector3.up)
                    : Quaternion.identity;
            }

            // 1) Giro: llevar la orientación actual del punto de referencia a la del anclaje.
            Quaternion delta = giroDestino * Quaternion.Inverse(_puntoReferencia.rotation);
            _raizModelo.rotation = delta * _raizModelo.rotation;

            // 2) Traslación: tras girar, desplazar la raíz lo que haga falta para que el punto
            //    de referencia caiga justo sobre la posición del anclaje.
            _raizModelo.position += pose.position - _puntoReferencia.position;

            EstaVinculado = true;
            Debug.LogWarning($"[DigitalTwin][MR] Modelo anclado usando '{_metaReferencia.ifcName}' como referencia " +
                      $"(GlobalId {_metaReferencia.globalId}). Desviación residual: " +
                      $"{Vector3.Distance(_puntoReferencia.position, pose.position):F4} m.");
        }
    }
}
