using System.Collections;
using System.Collections.Generic;
using DigitalTwin.Core;
using IFCImporter;
using UnityEngine;

namespace DigitalTwin.MR
{
    /// <summary>
    /// Viste la escena para el modo anclado: la geometría del modelo deja de dibujarse y pasa a
    /// ser oclusor o a desaparecer, según la garantía que ofrezca su ubicación.
    ///
    /// - OCLUYEN (escritura solo de profundidad, sombreador <c>DigitalTwin/OclusorProfundidad</c>):
    ///   muros, forjados, pilares y revestimientos —lo que con seguridad está donde el modelo
    ///   dice—. No se ven, pero un sensor de la sala contigua deja de flotar a través de la
    ///   pared. Conservan su colisionador: apuntarlos es apuntar al elemento real que tienen
    ///   delante, y su ficha sigue disponible por señalamiento.
    ///
    /// - NO OCLUYEN Y SE OCULTAN DEL TODO (render y colisionador desactivados): los cerramientos
    ///   transparentes (a través del vidrio se ve), las puertas (el modelo no sabe si están
    ///   abiertas), el mobiliario (se mueve) y el resto de elementos sin garantía de ubicación.
    ///   El principio: un oclusor equivocado RESTA, y restar es peor que sumar. Su ficha sigue
    ///   siendo consultable por los mecanismos de lista, que no afirman dónde está el activo.
    ///
    /// - LOS SENSORES QUEDAN VISIBLES: son la telemetría que este modo superpone a la realidad.
    ///
    /// DIAGNÓSTICO TRAS LA PRUEBA DEL 2026-08-13 (modo anclado en negro, sin un solo error).
    /// Aquel registro no permitía distinguir «el sombreador pinta negro» de «los oclusores no se
    /// aplicaron» de «la capa de transparencia se apagó». Esta versión hace tres cosas:
    ///
    ///  1. Identifica el sombreador en el registro (nombre, soporte, pases, API gráfica): si el
    ///     dispositivo lo sustituyó o no lo soporta, se ve; y si no se soporta NO se aplica.
    ///  2. CANARIO DE REVELADO: los primeros segundos del modo anclado los oclusores se pintan
    ///     de verde con un material conocido-bueno (el mismo que usa el rayo de los mandos) y
    ///     después cambian al de solo-profundidad, con una traza en cada instante. Lo observado
    ///     entre ambas trazas discrimina en una sola sesión: verde visible y luego vídeo de la
    ///     sala = todo correcto; verde visible y luego NEGRO = el sombreador de profundidad
    ///     pinta pese a sus tres bloqueos de color; nunca verde = los materiales no se están
    ///     aplicando a lo que se cree.
    ///  3. VIGILANCIA DE LA CÁMARA: comprueba y registra clearFlags y color de borrado (con su
    ///     alfa) al aplicar, tras el canario y periódicamente; si alguien los reescribe mientras
    ///     la transparencia está activa, lo denuncia y los repara. La composición del vídeo
    ///     depende de que el fotograma llegue con alfa cero donde no hay geometría.
    /// </summary>
    public static class MROcclusionService
    {
        public const string RutaShaderOclusor = "MR/OclusorProfundidad";

        /// <summary>Duración de la fase de revelado en verde, en segundos.</summary>
        public const float SegundosDeCanario = 6f;

        /// <summary>
        /// Aplica la vestimenta de modo anclado. Devuelve false —con el motivo registrado— si el
        /// sombreador de oclusión no está disponible; en ese caso NO se oculta nada: un modelo
        /// visible encima de la realidad es feo pero diagnosticable, mientras que ocultar sin
        /// ocluir haría desaparecer sensores tras muros inexistentes sin explicación.
        /// </summary>
        public static bool Aplicar(SceneModelIndex index)
        {
            var shader = Resources.Load<Shader>(RutaShaderOclusor);
            if (shader == null)
            {
                Debug.LogError($"[DigitalTwin][AR] No se encuentra el sombreador de oclusion en " +
                               $"Resources/{RutaShaderOclusor}. El modo anclado se queda SIN " +
                               "oclusores y con la geometria visible: revisa que el fichero " +
                               "Assets/Resources/MR/OclusorProfundidad.shader exista y compile.");
                return false;
            }

            // Identidad del sombreador en el registro: si el dispositivo lo ha sustituido por el
            // de error, o no soporta sus variantes, este es el sitio donde se ve.
            Debug.LogWarning($"[DigitalTwin][AR] Oclusion: sombreador '{shader.name}', " +
                             $"soportado={shader.isSupported}, pases={shader.passCount}, " +
                             $"API grafica={SystemInfo.graphicsDeviceType}.");
            if (!shader.isSupported)
            {
                Debug.LogError("[DigitalTwin][AR] El sombreador de oclusion NO esta soportado en " +
                               "este dispositivo (variantes sin compilar o etapa incompatible). " +
                               "No se aplica la oclusion: la geometria se queda visible.");
                return false;
            }

            var materialOclusor = new Material(shader) { name = "~OclusorProfundidad" };

            var sensores = new HashSet<IfcMetadata>(index.Sensors);
            var elementosOclusores = new HashSet<IfcMetadata>();
            var elementosOcultados = new HashSet<IfcMetadata>();
            var renderersOclusores = new List<Renderer>();
            int sensoresVisibles = 0;

            foreach (var renderer in Object.FindObjectsByType<Renderer>(FindObjectsSortMode.None))
            {
                // Solo geometría identificada del modelo. Los renderers sin IfcMetadata en su
                // ascendencia son decorado del sistema (rayo de los mandos, línea del panel,
                // lienzos) o raíces sin entidad, y no se tocan.
                var meta = renderer.GetComponentInParent<IfcMetadata>();
                if (meta == null) continue;

                // Ya ocultos por ColliderBootstrapper con su propia lógica: marcadores de
                // navegación, volúmenes de espacio y definiciones de catálogo.
                if (meta.ifcType == SceneModelIndex.NavPointIfcType) continue;
                if (meta.ifcType == SceneModelIndex.SpaceIfcType) continue;
                if (SceneModelIndex.EsDefinicionDeTipo(meta.ifcType)) continue;

                if (sensores.Contains(meta))
                {
                    sensoresVisibles++;
                    continue;
                }

                if (IfcClasificacion.EsOclusor(meta.ifcType))
                {
                    renderersOclusores.Add(renderer);
                    elementosOclusores.Add(meta);
                }
                else
                {
                    renderer.enabled = false;
                    // Lo que no se representa no es seleccionable por señalamiento: su
                    // colisionador (vive en el mismo GameObject que la malla, ver
                    // ColliderBootstrapper) se apaga con él.
                    foreach (var col in renderer.GetComponents<Collider>())
                        col.enabled = false;
                    elementosOcultados.Add(meta);
                }
            }

            Debug.LogWarning($"[DigitalTwin][AR] Modo anclado: clasificacion aplicada. " +
                             $"{elementosOclusores.Count} elementos oclusores " +
                             $"({renderersOclusores.Count} renderers), " +
                             $"{elementosOcultados.Count} elementos ocultados (sin render ni " +
                             $"seleccion por rayo), {sensoresVisibles} mallas de sensor visibles.");

            // El canario aplica primero el verde de revelado y, pasados unos segundos, el
            // material de solo-profundidad; también vigila la cámara. Ver la nota de la clase.
            var canarioGo = new GameObject("~CanarioOclusionAR");
            Object.DontDestroyOnLoad(canarioGo);
            canarioGo.AddComponent<MRCanarioOclusion>()
                     .Iniciar(renderersOclusores, materialOclusor);

            return true;
        }
    }

    /// <summary>
    /// Fase de revelado del modo anclado y vigilancia del estado de cámara/transparencia.
    /// Componente aparte porque el servicio de oclusión es estático y esto necesita corrutinas.
    /// </summary>
    internal class MRCanarioOclusion : MonoBehaviour
    {
        private static readonly Color VerdeRevelado = new Color(0.15f, 0.85f, 0.35f, 1f);

        private List<Renderer> _oclusores;
        private Material _materialProfundidad;

        public void Iniciar(List<Renderer> oclusores, Material materialProfundidad)
        {
            _oclusores = oclusores;
            _materialProfundidad = materialProfundidad;
            StartCoroutine(Secuencia());
        }

        private IEnumerator Secuencia()
        {
            // 1) Verde de revelado con un material conocido-bueno (el mismo ayudante que crea el
            // material del rayo de los mandos, verificado en el visor). Si estas paredes verdes
            // NO se ven, los materiales no se están aplicando donde se cree.
            var verde = DigitalTwin.Core.RuntimeMaterials.CrearSinIluminacion(VerdeRevelado);
            int pintados = 0;
            if (verde != null)
            {
                pintados = AplicarATodos(verde);
                Debug.LogWarning($"[DigitalTwin][AR] Canario de oclusion: {pintados} renderers en " +
                                 $"VERDE de revelado durante {MROcclusionService.SegundosDeCanario:0} s. " +
                                 "Lo que se observe ahora y justo despues discrimina el fallo: " +
                                 "verde->video correcto; verde->negro = el sombreador de " +
                                 "profundidad pinta; nunca verde = los materiales no llegan.");
            }
            else
            {
                Debug.LogWarning("[DigitalTwin][AR] Canario de oclusion: sin sombreador basico para " +
                                 "el verde; se pasa directamente a solo-profundidad.");
            }

            RegistrarEstadoCamara("al aplicar el canario");
            yield return new WaitForSeconds(MROcclusionService.SegundosDeCanario);

            // 2) Cambio a solo-profundidad. A partir de esta traza, lo correcto es ver el vídeo
            // de la sala a través de donde estaba el verde, con los sensores ocluidos por sala.
            int cambiados = AplicarATodos(_materialProfundidad);
            Debug.LogWarning($"[DigitalTwin][AR] Canario terminado: {cambiados} renderers cambiados " +
                             "a solo-profundidad (cola " + _materialProfundidad.renderQueue + "). " +
                             "Si AHORA se ve negro donde habia verde, el sombreador de profundidad " +
                             "esta pintando pese a ColorMask 0 + Blend Zero One.");

            // 3) Vigilancia de la cámara: inmediatamente, a los 5 s y a los 35 s. Si alguien
            // reescribe el borrado con alfa distinto de cero, se denuncia y se repara.
            RegistrarEstadoCamara("tras el cambio a solo-profundidad");
            yield return new WaitForSeconds(5f);
            RegistrarEstadoCamara("5 s despues");
            yield return new WaitForSeconds(30f);
            RegistrarEstadoCamara("35 s despues");
        }

        private int AplicarATodos(Material material)
        {
            int aplicados = 0;
            foreach (var r in _oclusores)
            {
                if (r == null) continue;
                // Se sustituyen TODAS las ranuras conservando su número: una malla con dos
                // submateriales que recibiera uno solo dejaría una submalla sin dibujar.
                int ranuras = r.sharedMaterials.Length;
                var materiales = new Material[ranuras];
                for (int i = 0; i < ranuras; i++) materiales[i] = material;
                r.sharedMaterials = materiales;
                aplicados++;
            }
            return aplicados;
        }

        /// <summary>
        /// Registra clearFlags y color de borrado (con alfa) y los repara si la transparencia
        /// está activa y alguien los ha reescrito. La composición del vídeo depende de que el
        /// fotograma llegue con alfa cero allí donde no hay geometría.
        /// </summary>
        private static void RegistrarEstadoCamara(string momento)
        {
            var camara = Camera.main;
            var passthrough = MRPassthroughController.Instancia;
            bool capaActiva = passthrough != null && passthrough.Activado;

            if (camara == null)
            {
                Debug.LogError($"[DigitalTwin][AR] Estado de camara ({momento}): NO hay camara " +
                               "MainCamera. Nada de lo demas puede componerse.");
                return;
            }

            Color fondo = camara.backgroundColor;
            Debug.LogWarning($"[DigitalTwin][AR] Estado camara/transparencia ({momento}): capa " +
                             $"activa={capaActiva}, clearFlags={camara.clearFlags}, " +
                             $"fondo=({fondo.r:0.00},{fondo.g:0.00},{fondo.b:0.00},{fondo.a:0.00}).");

            if (capaActiva &&
                (camara.clearFlags != CameraClearFlags.SolidColor || fondo.a > 0.001f))
            {
                Debug.LogError("[DigitalTwin][AR] La camara ha sido reescrita con la transparencia " +
                               "activa (el borrado ya no es color solido con alfa cero): se " +
                               "restaura. Buscar en el registro quien la toco entre esta traza y " +
                               "la anterior.");
                camara.clearFlags = CameraClearFlags.SolidColor;
                camara.backgroundColor = new Color(0f, 0f, 0f, 0f);
            }
        }
    }
}
