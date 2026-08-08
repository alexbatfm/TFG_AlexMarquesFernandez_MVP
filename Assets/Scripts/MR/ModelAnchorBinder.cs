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
    /// IFC (por defecto el primero indexado, configurable con <see cref="IndicePuntoReferencia"/>).
    /// Se eligen estos y no el origen del modelo porque son posiciones reconocibles físicamente
    /// dentro del edificio: el operario puede situarse en ese punto real y confirmar allí el
    /// anclaje, que es un gesto mucho más preciso que intentar adivinar dónde cae un origen
    /// abstracto de coordenadas.
    /// </summary>
    public class ModelAnchorBinder : MonoBehaviour
    {
        [Tooltip("Cuál de los puntos 'Esfera...' del modelo se usa como referencia física.")]
        public int IndicePuntoReferencia = 0;

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

        private Transform ResolverPuntoReferencia(SceneModelIndex index)
        {
            if (index.NavPoints.Count == 0) return null;

            int i = Mathf.Clamp(IndicePuntoReferencia, 0, index.NavPoints.Count - 1);
            if (i != IndicePuntoReferencia)
            {
                Debug.LogWarning($"[DigitalTwin][MR] IndicePuntoReferencia={IndicePuntoReferencia} fuera de " +
                                 $"rango ({index.NavPoints.Count} puntos); se usa el {i}.");
            }
            return index.NavPoints[i].transform;
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
            Debug.Log($"[DigitalTwin][MR] Modelo anclado usando '{_index.NavPoints[Mathf.Clamp(IndicePuntoReferencia, 0, _index.NavPoints.Count - 1)].ifcName}' " +
                      $"como referencia. Desviación residual: {Vector3.Distance(_puntoReferencia.position, pose.position):F4} m.");
        }
    }
}
