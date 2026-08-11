using UnityEngine;

namespace DigitalTwin.Core
{
    /// <summary>
    /// Creación de materiales en tiempo de ejecución a prueba de compilaciones.
    ///
    /// POR QUE EXISTE
    ///
    /// <c>Shader.Find</c> se comporta de forma distinta en el editor y en una compilación. En el
    /// editor encuentra cualquier sombreador del proyecto; en una compilación solo encuentra los
    /// que se han incluido, y devuelve <c>null</c> para el resto. Como <c>new Material(null)</c>
    /// lanza <c>ArgumentNullException</c>, el patrón
    ///
    ///     var mat = new Material(Shader.Find("Universal Render Pipeline/Unlit"));
    ///
    /// funciona perfectamente al pulsar Play y revienta en el dispositivo.
    ///
    /// Eso ocurrió: la excepción se propagó desde el constructor de dos componentes visuales hasta
    /// el arranque de Realidad Aumentada, que quedó interrumpido a media ejecución. Todo lo que
    /// venía después --- el middleware de sensores y los mandos --- no llegó a crearse nunca. El
    /// resultado en el visor era un modelo que se veía pero con el que no se podía hacer nada, sin
    /// ninguna pista visible de por qué.
    ///
    /// La lección de fondo: un adorno visual que falla no debe poder tumbar la funcionalidad. Por
    /// eso este método <b>nunca lanza</b>. Si no encuentra sombreador devuelve <c>null</c>, avisa
    /// una sola vez, y quien lo llama decide seguir sin ese elemento decorativo.
    ///
    /// Complemento necesario: <c>Assets/Editor/IncluirShadersEnBuild.cs</c> registra estos
    /// sombreadores en la lista de incluidos siempre, para que en la compilación sí se encuentren.
    /// Este fichero es la red de seguridad; aquél, la solución.
    /// </summary>
    public static class RuntimeMaterials
    {
        /// <summary>
        /// Sombreadores sin iluminación, en orden de preferencia. El primero es el del pipeline
        /// universal, que es el que usa el proyecto; los siguientes son alternativas presentes en
        /// instalaciones estándar de Unity.
        /// </summary>
        public static readonly string[] SombreadoresSinIluminacion =
        {
            "Universal Render Pipeline/Unlit",
            "Unlit/Color",
            "Sprites/Default",
            "UI/Default",
        };

        private static bool _yaAvisado;

        /// <summary>
        /// Devuelve un material sin iluminación del color indicado, o <c>null</c> si no hay ningún
        /// sombreador disponible. No lanza nunca.
        /// </summary>
        public static Material CrearSinIluminacion(Color color)
        {
            foreach (var nombre in SombreadoresSinIluminacion)
            {
                var shader = Shader.Find(nombre);
                if (shader == null) continue;

                var mat = new Material(shader);
                // Los dos nombres de propiedad conviven: el pipeline universal usa _BaseColor y
                // los sombreadores clásicos _Color. Asignar ambos evita tener que saber cuál toca.
                if (mat.HasProperty("_BaseColor")) mat.SetColor("_BaseColor", color);
                if (mat.HasProperty("_Color")) mat.SetColor("_Color", color);
                return mat;
            }

            if (!_yaAvisado)
            {
                _yaAvisado = true;
                Debug.LogWarning("[DigitalTwin] No se ha encontrado ningun sombreador sin iluminacion " +
                                 "en esta compilacion. Los elementos decorativos (caja de seleccion, " +
                                 "linea del panel, rayo de los mandos) no se dibujaran, pero el resto " +
                                 "del sistema sigue funcionando. Revisa Project Settings > Graphics > " +
                                 "Always Included Shaders.");
            }
            return null;
        }
    }
}
