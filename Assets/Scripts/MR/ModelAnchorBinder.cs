using DigitalTwin.Core;
using IFCImporter;
using UnityEngine;

namespace DigitalTwin.MR
{
    /// <summary>
    /// Fase 5 - Coloca el modelo BIM en el mundo real a partir de la pose del anclaje
    /// espacial que proporciona <see cref="MRAnchorService"/>, y ejecuta sobre la raíz del
    /// modelo los movimientos rígidos que le pide el registro por puntos.
    ///
    /// El problema que resuelve: el anchor devuelve una pose del mundo físico, pero el modelo
    /// tiene su propio origen, heredado del IFC, que no coincide con ningún punto notable del
    /// edificio. Colocar el modelo directamente en la pose del anchor lo dejaría desplazado
    /// tantos metros como diste el origen del IFC del punto físico donde el operario colocó el
    /// anclaje. Lo que hay que hacer es calcular la transformación que lleva un ELEMENTO DE
    /// REFERENCIA conocido del modelo hasta la pose del anchor, y aplicarla al modelo entero.
    ///
    /// QUÉ CAMBIÓ EL 15-08 POR LA TARDE. Hasta entonces la referencia era un único punto (el punto de vista
    /// del Recibidor) y la orientación se tomaba de la pose del mando al crear el anclaje: una
    /// correspondencia determina la traslación pero NO la orientación, que quedaba supuesta, y
    /// el «residuo» que se registraba era cero por construcción. Ahora la pose la decide un
    /// registro por pares de puntos (<see cref="MRRegistroPorPuntos"/>) con residuo real, y el
    /// anclaje se crea en la pose que ese registro deja al elemento de referencia. Este binder
    /// conserva su papel en la RESTAURACIÓN: al arrancar con anclaje guardado, vuelve a llevar
    /// la referencia a la pose recuperada. La referencia ya no es fija: es la que el registro
    /// eligió (por defecto, la primera puerta registrada) y su GlobalId viaja en el nombre del
    /// anclaje persistido; <see cref="GlobalIdPuntoReferencia"/> es solo el respaldo cuando el
    /// modelo no tiene puertas o el nombre recuperado no se reconoce.
    ///
    /// SOLO GIRO HORIZONTAL ES UNA RESTRICCIÓN, NO UNA SUPOSICIÓN. El registro resuelve
    /// guiñada + traslación con la inclinación fijada a cero (los puntos se toman a nivel de
    /// suelo con ruido vertical, y una rotación libre absorbería ese ruido inclinando el
    /// edificio). Coherentemente, aquí la orientación de la referencia y la del anclaje se
    /// reducen AMBAS a su guiñada antes de compararlas: el edificio no se inclina nunca,
    /// venga la pose de donde venga. La guiñada del modelo se define por la proyección
    /// horizontal del eje X de su raíz (ver <see cref="GuinadaActualDelModelo"/>): es la
    /// misma definición al crear el anclaje y al restaurarlo, que es lo único que importa.
    ///
    /// ESPACIOS. El servicio de anclaje habla en espacio de seguimiento (el del origen de
    /// realidad extendida); el modelo vive en mundo. La conversión se hace aquí con el
    /// transform del origen, que es la única pieza que sabe dónde está el suelo físico (con
    /// origen a nivel de suelo, y=0 del seguimiento es el suelo real).
    ///
    /// REFINADO DEL ANCLAJE. El runtime puede reajustar la pose de un anchor al mejorar su mapa
    /// del entorno. Se consulta periódicamente y, si se ha movido más de un umbral, se vuelve a
    /// aplicar y se deja constancia en el registro con la magnitud del salto: es la deriva
    /// hecha visible, y alimenta el capítulo de pruebas.
    /// </summary>
    public class ModelAnchorBinder : MonoBehaviour
    {
        /// <summary>
        /// GlobalId IFC del punto "Esfera..." que se usa como referencia DE RESPALDO.
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
        [Tooltip("GlobalId IFC del punto 'Esfera...' usado como referencia de respaldo. Por defecto, el del Recibidor.")]
        public string GlobalIdPuntoReferencia = "0rnbMfC4L9qhDOi7l3AfPB";

        /// <summary>
        /// RESTRICCIÓN IMPUESTA AL AJUSTE, no una opción: solo se alinea el giro en horizontal
        /// (guiñada). Hasta el 15-08 por la tarde era un booleano configurable que proyectaba la pose del
        /// mando «por si el operario lo creaba torcido» —una suposición—; desde el registro por
        /// puntos es una restricción física del problema (el edificio no se inclina aunque los
        /// puntos midan con ruido vertical) que comparten el ajuste (<see cref="MRRegistroPorPuntos"/>)
        /// y este binder, y por eso ya no se puede desactivar: no hay ningún camino de código que
        /// aplique inclinación al modelo.
        /// </summary>
        public bool SoloGiroHorizontal => true;

        /// <summary>Umbral a partir del cual un reajuste de la pose del anchor por parte del
        /// runtime se vuelve a aplicar al modelo (metros y grados).</summary>
        private const float UmbralRefinadoMetros = 0.01f;
        private const float UmbralRefinadoGrados = 0.1f;
        private const float SegundosEntreConsultas = 0.5f;

        private SceneModelIndex _index;
        private MRAnchorService _anclaje;
        private Transform _origenXR;
        private Transform _raizModelo;
        private IfcMetadata _metaReferencia;

        private Pose _ultimaPoseAplicada;
        private bool _hayPoseAplicada;
        private float _proximaConsulta;
        private int _refinadosAplicados;

        /// <summary>Cadencia de la traza periódica de pose (origen, modelo, anclaje, con cabeceo
        /// y alabeo), pedida por la revisión del 19-08 para distinguir un defecto de la
        /// aplicación de una deriva del seguimiento del visor.</summary>
        private const float SegundosEntreTrazasDePose = 5f;
        private float _proximaTrazaDePose;

        public bool EstaVinculado { get; private set; }
        public Transform RaizModelo => _raizModelo;
        public IfcMetadata Referencia => _metaReferencia;

        public void Initialize(SceneModelIndex index, MRAnchorService anclaje, Transform origenXR)
        {
            _index = index;
            _anclaje = anclaje;
            _origenXR = origenXR;
            _raizModelo = ResolverRaizModelo(index);
            _metaReferencia = BuscarElemento(GlobalIdPuntoReferencia) ?? PrimerPuntoDeVista();

            if (_raizModelo == null || _metaReferencia == null)
            {
                Debug.LogError("[DigitalTwin][MR] No se puede vincular el modelo al anclaje: " +
                               "falta la raíz del modelo o un elemento de referencia.");
                enabled = false;
                return;
            }

            if (_origenXR == null)
                Debug.LogWarning("[DigitalTwin][MR] Binder sin origen de realidad extendida: se asume " +
                                 "que el espacio de seguimiento coincide con el de mundo.");

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

        private IfcMetadata BuscarElemento(string globalId)
        {
            if (string.IsNullOrEmpty(globalId) || _index == null) return null;
            foreach (var m in _index.AllElements)
                if (m != null && m.globalId == globalId) return m;
            foreach (var m in _index.NavPoints)
                if (m != null && m.globalId == globalId) return m;
            return null;
        }

        private IfcMetadata PrimerPuntoDeVista()
        {
            if (_index == null || _index.NavPoints.Count == 0) return null;
            var m = _index.NavPoints[0];
            Debug.LogWarning($"[DigitalTwin][MR] No se ha encontrado el punto de referencia con GlobalId " +
                             $"'{GlobalIdPuntoReferencia}' entre los {_index.NavPoints.Count} puntos del " +
                             $"modelo. Se usa '{m.ifcName}' como sustituto de respaldo.");
            return m;
        }

        /// <summary>Cambia el elemento de referencia (lo llama el registro al elegir la primera
        /// estación). Falso si el GlobalId no existe en el modelo; entonces no cambia nada.</summary>
        public bool UsarReferencia(string globalId)
        {
            var m = BuscarElemento(globalId);
            if (m == null) return false;
            _metaReferencia = m;
            return true;
        }

        /// <summary>
        /// Guiñada actual del modelo en mundo (grados), definida por la proyección horizontal
        /// del eje X de la raíz; si ese eje fuese vertical (importadores que giran la raíz),
        /// se usa el eje Z. Es una definición estable de sesión a sesión porque el modelo se
        /// importa siempre con la misma raíz.
        /// </summary>
        public float GuinadaActualDelModelo()
        {
            Vector3 eje = Vector3.ProjectOnPlane(_raizModelo.right, Vector3.up);
            if (eje.sqrMagnitude < 1e-6f) eje = Vector3.ProjectOnPlane(_raizModelo.forward, Vector3.up);
            if (eje.sqrMagnitude < 1e-6f) return 0f;
            return Mathf.Atan2(eje.x, eje.z) * Mathf.Rad2Deg;
        }

        /// <summary>
        /// Aplica a la raíz un movimiento rígido de mundo: giro alrededor de la vertical
        /// (por el origen de mundo) y traslación, en ese orden — exactamente lo que devuelve
        /// <see cref="MRRegistroPorPuntos.Resolver"/>. Se opera sobre la raíz para no tocar las
        /// posiciones relativas internas del modelo, que vienen del IFC y deben conservarse.
        /// </summary>
        public void AplicarMovimientoRigido(Quaternion giroHorizontal, Vector3 traslacion)
        {
            if (_raizModelo == null) return;
            _raizModelo.rotation = giroHorizontal * _raizModelo.rotation;
            _raizModelo.position = giroHorizontal * _raizModelo.position + traslacion;
        }

        /// <summary>
        /// Pose del elemento de referencia EN ESPACIO DE SEGUIMIENTO, tal como debe anclarse:
        /// su posición y la guiñada actual del modelo. Es lo que se entrega a
        /// <see cref="MRAnchorService.ColocarEnPose"/> tras un registro.
        /// </summary>
        public bool TryPoseDeReferenciaEnSeguimiento(out Pose poseSeguimiento)
        {
            poseSeguimiento = default;
            if (_metaReferencia == null || _raizModelo == null) return false;
            Vector3 posMundo = _metaReferencia.transform.position;
            Quaternion rotMundo = Quaternion.AngleAxis(GuinadaActualDelModelo(), Vector3.up);
            poseSeguimiento = new Pose(AMundoInverso(posMundo), Quaternion.Inverse(RotacionOrigen()) * rotMundo);
            return true;
        }

        private Quaternion RotacionOrigen() => _origenXR != null ? _origenXR.rotation : Quaternion.identity;
        private Vector3 AMundo(Vector3 seguimiento) =>
            _origenXR != null ? _origenXR.TransformPoint(seguimiento) : seguimiento;
        private Vector3 AMundoInverso(Vector3 mundo) =>
            _origenXR != null ? _origenXR.InverseTransformPoint(mundo) : mundo;

        /// <summary>
        /// Mueve y gira el modelo para que su elemento de referencia coincida con la pose del
        /// anclaje (guiñada y posición). Lo dispara el servicio tanto al crear el anclaje —donde
        /// es un no-op, porque el anclaje se creó justo en la pose de la referencia— como al
        /// restaurarlo en una sesión posterior, donde es LA operación que devuelve el modelo a
        /// su sitio.
        /// </summary>
        private void AplicarAnclaje(Pose poseSeguimiento, string referenciaGlobalId)
        {
            if (!string.IsNullOrEmpty(referenciaGlobalId) &&
                (_metaReferencia == null || _metaReferencia.globalId != referenciaGlobalId))
            {
                if (!UsarReferencia(referenciaGlobalId))
                    Debug.LogWarning($"[DigitalTwin][MR] El anclaje refiere al elemento '{referenciaGlobalId}', " +
                                     $"que no está en el modelo; se aplica sobre '{_metaReferencia.ifcName}' " +
                                     "y el resultado puede no ser el esperado. Recoloca el modelo.");
            }

            AplicarPose(poseSeguimiento);
            _ultimaPoseAplicada = poseSeguimiento;
            _hayPoseAplicada = true;
            EstaVinculado = true;

            Vector3 posMundo = AMundo(poseSeguimiento.position);
            Debug.LogWarning($"[DigitalTwin][MR] Modelo llevado al anclaje usando '{_metaReferencia.ifcName}' " +
                             $"(GlobalId {_metaReferencia.globalId}) como referencia: posicion de mundo " +
                             $"{posMundo}, guiñada del modelo {GuinadaActualDelModelo():0.0}°. Desviacion " +
                             $"referencia-anclaje tras aplicar: " +
                             $"{Vector3.Distance(_metaReferencia.transform.position, posMundo) * 100f:0.0} cm " +
                             "(es la exactitud de la operacion de mapeo, NO la calidad del registro: esa la " +
                             "da el residuo del registro por puntos).");
        }

        private void AplicarPose(Pose poseSeguimiento)
        {
            Vector3 posObjetivo = AMundo(poseSeguimiento.position);
            Quaternion rotObjetivo = RotacionOrigen() * poseSeguimiento.rotation;

            // Solo guiñada, en ambos lados de la comparación (ver SoloGiroHorizontal).
            float guinadaObjetivo = GuinadaDe(rotObjetivo);
            float delta = Mathf.DeltaAngle(GuinadaActualDelModelo(), guinadaObjetivo);
            Quaternion giro = Quaternion.AngleAxis(delta, Vector3.up);

            // Girar alrededor de la referencia y llevarla a la posición del anclaje: un solo
            // movimiento rígido, con la misma primitiva que usa el registro.
            Vector3 refAntes = _metaReferencia.transform.position;
            Vector3 traslacion = posObjetivo - giro * refAntes;
            AplicarMovimientoRigido(giro, traslacion);
        }

        /// <summary>Guiñada de una rotación: proyección horizontal de su eje X (misma
        /// definición que <see cref="GuinadaActualDelModelo"/>, para que anclaje y modelo
        /// se comparen con la misma vara).</summary>
        private static float GuinadaDe(Quaternion rot)
        {
            Vector3 eje = Vector3.ProjectOnPlane(rot * Vector3.right, Vector3.up);
            if (eje.sqrMagnitude < 1e-6f) eje = Vector3.ProjectOnPlane(rot * Vector3.forward, Vector3.up);
            if (eje.sqrMagnitude < 1e-6f) return 0f;
            return Mathf.Atan2(eje.x, eje.z) * Mathf.Rad2Deg;
        }

        /// <summary>Cabeceo y alabeo de una rotación respecto a la vertical de mundo, medidos
        /// sobre sus ejes (no los Euler de Unity, que se enredan cerca de ±180° de guiñada): la
        /// inclinación total de su «arriba», y cuánto de ella cae en el plano de su eje Z
        /// (cabeceo) y en el de su eje X (alabeo).</summary>
        private static string CabeceoYAlabeo(Transform t)
        {
            float inclinacion = Vector3.Angle(t.up, Vector3.up);
            float cabeceo = Mathf.Asin(Mathf.Clamp(t.forward.y, -1f, 1f)) * Mathf.Rad2Deg;
            float alabeo = Mathf.Asin(Mathf.Clamp(t.right.y, -1f, 1f)) * Mathf.Rad2Deg;
            return $"inclinacion {inclinacion:0.00}° (cabeceo {-cabeceo:0.00}°, alabeo {alabeo:0.00}°)";
        }

        /// <summary>
        /// Traza periódica de pose, desde que el binder se inicializa (no solo tras anclar):
        /// origen XR (posición, guiñada, cabeceo, alabeo), raíz del modelo (ídem), pose del
        /// anclaje en seguimiento si existe, y la cota de la referencia del modelo EN
        /// SEGUIMIENTO (debe ser ≈0 para un punto de suelo: si no lo es, el suelo del modelo no
        /// está en el suelo físico, sea por el registro o por el origen). Es la evidencia que
        /// separa «el modelo se inclina por la aplicación» de «deriva el seguimiento».
        /// </summary>
        private void TrazarPoseSiToca()
        {
            if (_raizModelo == null || Time.time < _proximaTrazaDePose) return;
            _proximaTrazaDePose = Time.time + SegundosEntreTrazasDePose;

            string origen = _origenXR != null
                ? $"origen XR pos {_origenXR.position}, guiñada {_origenXR.eulerAngles.y:0.0}°, {CabeceoYAlabeo(_origenXR)}"
                : "origen XR ausente";
            string modelo = $"modelo pos {_raizModelo.position}, guiñada {GuinadaActualDelModelo():0.0}°, " +
                            $"{CabeceoYAlabeo(_raizModelo)}";
            string referencia = "referencia: ninguna";
            if (_metaReferencia != null)
            {
                Vector3 refMundo = _metaReferencia.transform.position;
                Vector3 refSeg = AMundoInverso(refMundo);
                referencia = $"referencia '{_metaReferencia.ifcName}' en mundo y={refMundo.y:0.000}, en seguimiento " +
                             $"({refSeg.x:0.00}, {refSeg.y:0.000}, {refSeg.z:0.00}) [origen del objeto, a nivel de suelo " +
                             "en las puertas: su y de seguimiento deberia ser ~0 tras registrar]";
            }
            string anclaje = "anclaje: sin pose";
            if (_anclaje != null && _anclaje.TryGetPose(out Pose poseAnclaje))
            {
                Vector3 ea = poseAnclaje.rotation.eulerAngles;
                anclaje = $"anclaje (seguimiento) pos {poseAnclaje.position}, Euler ({ea.x:0.00}, {ea.y:0.0}, {ea.z:0.00})" +
                          (_hayPoseAplicada
                              ? $", salto respecto a la ultima aplicada {Vector3.Distance(poseAnclaje.position, _ultimaPoseAplicada.position) * 100f:0.0} cm / " +
                                $"{Quaternion.Angle(poseAnclaje.rotation, _ultimaPoseAplicada.rotation):0.00}°"
                              : string.Empty);
            }
            Debug.LogWarning($"[DigitalTwin][MR][Pose] {origen}; {modelo}; {referencia}; {anclaje}; " +
                             $"reajustes del runtime aplicados: {_refinadosAplicados}.");
        }

        private void Update()
        {
            TrazarPoseSiToca();

            if (!_hayPoseAplicada || _anclaje == null || Time.time < _proximaConsulta) return;
            _proximaConsulta = Time.time + SegundosEntreConsultas;

            if (!_anclaje.TryGetPose(out Pose actual)) return;

            float salto = Vector3.Distance(actual.position, _ultimaPoseAplicada.position);
            float giro = Quaternion.Angle(actual.rotation, _ultimaPoseAplicada.rotation);
            if (salto < UmbralRefinadoMetros && giro < UmbralRefinadoGrados) return;

            AplicarPose(actual);
            _ultimaPoseAplicada = actual;
            _refinadosAplicados++;
            Debug.LogWarning($"[DigitalTwin][MR] El runtime ha reajustado la pose del anclaje: salto de " +
                             $"{salto * 100f:0.0} cm y {giro:0.00}°; modelo reubicado (reajuste " +
                             $"n.º {_refinadosAplicados} de la sesion). Es la deriva del seguimiento hecha " +
                             "visible.");
        }
    }
}
