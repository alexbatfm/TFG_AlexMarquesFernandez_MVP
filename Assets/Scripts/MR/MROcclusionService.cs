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
    /// - OCLUYEN (escritura solo de profundidad, material <c>DigitalTwin/OclusorProfundidad</c>):
    ///   muros, forjados, pilares y revestimientos —lo que con seguridad está donde el modelo
    ///   dice—. No se ven, pero un sensor de la sala contigua deja de flotar a través de la
    ///   pared. Conservan su colisionador: apuntarlos es apuntar al elemento real que tienen
    ///   delante, y su ficha sigue disponible por señalamiento.
    ///
    /// - NO OCLUYEN Y SE OCULTAN DEL TODO (render y colisionador desactivados): los cerramientos
    ///   transparentes (a través del vidrio se ve), las puertas (el modelo no sabe si están
    ///   abiertas: tratar como opaca una puerta abierta borra la sala contigua), el mobiliario
    ///   (se mueve) y el resto de elementos sin garantía de ubicación. El principio: un oclusor
    ///   equivocado RESTA, y restar es peor que sumar, porque el usuario no tiene ninguna señal
    ///   de que algo falte. Su ficha sigue siendo consultable por los mecanismos de lista, que
    ///   no afirman dónde está el activo.
    ///
    /// - LOS SENSORES QUEDAN VISIBLES: son la telemetría que este modo superpone a la realidad.
    ///   Sujetos a la oclusión como todo lo demás: los de la sala en que se está se ven, los de
    ///   las contiguas no.
    ///
    /// La clasificación reutiliza <see cref="IfcClasificacion"/>, la misma taxonomía con la que
    /// el grafo de navegación penaliza atravesar cada familia: «qué deja pasar la vista» y «qué
    /// deja pasar al operario» son la misma pregunta.
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

            var materialOclusor = new Material(shader) { name = "~OclusorProfundidad" };

            var sensores = new HashSet<IfcMetadata>(index.Sensors);
            var elementosOclusores = new HashSet<IfcMetadata>();
            var elementosOcultados = new HashSet<IfcMetadata>();
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
                    // Escritura solo de profundidad. Se sustituyen TODAS las ranuras de material
                    // del renderer, conservando su número: una malla con dos submateriales que
                    // recibiera uno solo dejaría una submalla dibujándose en rosa.
                    int ranuras = renderer.sharedMaterials.Length;
                    var materiales = new Material[ranuras];
                    for (int i = 0; i < ranuras; i++) materiales[i] = materialOclusor;
                    renderer.sharedMaterials = materiales;
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

            Debug.LogWarning($"[DigitalTwin][AR] Modo anclado: oclusores aplicados. " +
                             $"{elementosOclusores.Count} elementos escriben solo profundidad, " +
                             $"{elementosOcultados.Count} elementos ocultados (sin render ni " +
                             $"seleccion por rayo), {sensoresVisibles} mallas de sensor visibles.");
            return true;
        }
    }
}
