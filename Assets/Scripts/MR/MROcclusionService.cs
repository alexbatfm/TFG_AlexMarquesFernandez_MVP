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
    /// DIAGNÓSTICO SIN CANARIO (desde el 15-08 por la noche). Tras la prueba del 2026-08-13
    /// (modo anclado en negro sin un solo error) esta clase incorporó un «canario de revelado»:
    /// seis segundos con los oclusores pintados de verde antes de pasar a solo-profundidad, para
    /// discriminar en una sola sesión entre «los materiales no se aplican», «el sombreador de
    /// profundidad pinta» y «todo correcto». La prueba del 14-08 zanjó la cuestión (verde y
    /// después vídeo: la cadena de oclusión funciona), así que el canario cumplió su función y
    /// se retiró el 15-08: seis segundos de sala verde en cada arranque eran ya solo ruido, y en
    /// una defensa serían un ruido muy visible. Se retira entero, no tras una constante a cero:
    /// código muerto que nadie volvería a activar. Su diseño queda registrado en
    /// <c>TFG/docs/roadmap/PRUEBA-AR-2-2026-08-13.md</c> por si otra plataforma obligara a
    /// reconstruirlo. Lo que se CONSERVA es la parte del diagnóstico que no cuesta nada
    /// visualmente:
    ///
    ///  1. Identidad del sombreador en el registro (nombre, soporte, pases, API gráfica): si el
    ///     dispositivo lo sustituyó o no lo soporta, se ve; y si no se soporta NO se aplica.
    ///  2. Recuento de aplicación: cuántos renderers quedan en solo-profundidad, cuántos
    ///     elementos se ocultan y cuántas mallas de sensor permanecen visibles.
    ///  3. VIGILANCIA DE LA CÁMARA (<see cref="MRVigilanciaCamaraAnclado"/>): comprueba y
    ///     registra clearFlags y color de borrado (con su alfa) al aplicar y a los 5 y 35
    ///     segundos, junto con el estado REAL de la capa de transparencia (estado interno y
    ///     capa viva en el runtime, que desde el 15-08 son cosas distintas); si alguien
    ///     reescribe el borrado mientras la transparencia está activa, lo denuncia y lo repara.
    /// </summary>
    public static class MROcclusionService
    {
        public const string RutaShaderOclusor = "MR/OclusorProfundidad";

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

            // El material de solo-profundidad se aplica DESDE EL PRIMER FOTOGRAMA del modo
            // anclado (antes había una fase de revelado en verde; ver la nota de la clase). Se
            // sustituyen TODAS las ranuras conservando su número: una malla con dos
            // submateriales que recibiera uno solo dejaría una submalla sin dibujar.
            int renderersCambiados = 0;
            foreach (var r in renderersOclusores)
            {
                if (r == null) continue;
                int ranuras = r.sharedMaterials.Length;
                var materiales = new Material[ranuras];
                for (int i = 0; i < ranuras; i++) materiales[i] = materialOclusor;
                r.sharedMaterials = materiales;
                renderersCambiados++;
            }

            Debug.LogWarning($"[DigitalTwin][AR] Modo anclado: clasificacion aplicada. " +
                             $"{elementosOclusores.Count} elementos oclusores " +
                             $"({renderersCambiados} renderers en solo-profundidad, cola " +
                             $"{materialOclusor.renderQueue}), " +
                             $"{elementosOcultados.Count} elementos ocultados (sin render ni " +
                             $"seleccion por rayo), {sensoresVisibles} mallas de sensor visibles.");

            // La vigilancia de cámara sigue en pie: es la traza que discrimina un borrado
            // reescrito de una capa de transparencia muerta.
            var vigilanciaGo = new GameObject("~VigilanciaCamaraAnclado");
            Object.DontDestroyOnLoad(vigilanciaGo);
            vigilanciaGo.AddComponent<MRVigilanciaCamaraAnclado>().Iniciar();

            return true;
        }
    }

    /// <summary>
    /// Vigilancia del estado de cámara/transparencia en modo anclado. Componente aparte porque
    /// el servicio de oclusión es estático y esto necesita corrutinas. Es lo que queda del
    /// antiguo canario de revelado tras retirar la fase verde (ver la nota de
    /// <see cref="MROcclusionService"/>): las trazas, que valen, sin los seis segundos de sala
    /// verde, que ya no valían nada.
    /// </summary>
    internal class MRVigilanciaCamaraAnclado : MonoBehaviour
    {
        public void Iniciar()
        {
            StartCoroutine(Secuencia());
        }

        private IEnumerator Secuencia()
        {
            // Tres instantes: inmediato, a los 5 s y a los 35 s. Si alguien reescribe el
            // borrado con alfa distinto de cero, se denuncia y se repara.
            RegistrarEstadoCamara("al aplicar la oclusion");
            yield return new WaitForSeconds(5f);
            RegistrarEstadoCamara("5 s despues");
            yield return new WaitForSeconds(30f);
            RegistrarEstadoCamara("35 s despues");
        }

        /// <summary>
        /// Registra clearFlags y color de borrado (con alfa) y los repara si la transparencia
        /// está activa y alguien los ha reescrito. La composición del vídeo depende de que el
        /// fotograma llegue con alfa cero allí donde no hay geometría. Desde el 15-08 la traza
        /// separa «capa activa» (estado interno) de «capa viva» (existe en el runtime): la
        /// prueba de esa tarde demostró que pueden divergir, y esta línea es la que lo delata.
        /// </summary>
        private static void RegistrarEstadoCamara(string momento)
        {
            var camara = Camera.main;
            var passthrough = MRPassthroughController.Instancia;
            bool capaActiva = passthrough != null && passthrough.Activado;
            bool capaViva = passthrough != null && passthrough.CapaViva();

            if (camara == null)
            {
                Debug.LogError($"[DigitalTwin][AR] Estado de camara ({momento}): NO hay camara " +
                               "MainCamera. Nada de lo demas puede componerse.");
                return;
            }

            Color fondo = camara.backgroundColor;
            Debug.LogWarning($"[DigitalTwin][AR] Estado camara/transparencia ({momento}): capa " +
                             $"activa={capaActiva}, capa viva en el runtime={capaViva}, " +
                             $"clearFlags={camara.clearFlags}, " +
                             $"fondo=({fondo.r:0.00},{fondo.g:0.00},{fondo.b:0.00},{fondo.a:0.00}).");

            if (capaActiva && !capaViva)
            {
                Debug.LogError("[DigitalTwin][AR] El estado dice capa activa pero el runtime no " +
                               "la tiene: la sesion OpenXR la destruyo. El controlador de " +
                               "transparencia deberia recrearla solo; si esta linea reaparece " +
                               "en la siguiente vigilancia, no lo esta haciendo.");
            }

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
