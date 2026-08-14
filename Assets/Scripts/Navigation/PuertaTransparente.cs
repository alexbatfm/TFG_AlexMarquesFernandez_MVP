using System.Collections.Generic;
using IFCImporter;
using UnityEngine;

namespace DigitalTwin.Navigation
{
    /// <summary>
    /// Regla de presentación de los nodos puerta: MIENTRAS EL USUARIO OCUPA EL NODO DE UNA
    /// PUERTA, LA HOJA DE ESA PUERTA NO SE DIBUJA. Al abandonar el nodo se restituye.
    ///
    /// POR QUÉ EXISTE. El grafo incorpora las puertas como nodos intermedios (decisión de la
    /// generación, capítulo 6 de la memoria), así que llegar a una puerta y quedarse en el
    /// umbral es un destino legítimo del recorrido. Lo que no funciona es quedarse mirando la
    /// hoja a diez centímetros de la cara. La primera solución (2026-08-13) fue el tránsito
    /// automático: no detenerse en la puerta y continuar hasta el siguiente nodo, eligiendo la
    /// continuación por producto escalar. Las pruebas del 14-08 lo descartaron —encadenaba
    /// desplazamientos que el usuario no había pedido y hacía la navegación menos predecible—
    /// y ese mismo día se revirtió. Esta regla lo sustituye con una fracción de su complejidad: se
    /// llega a la puerta y punto, pero la hoja desaparece, de modo que se ve la estancia de
    /// destino en lugar de la madera. El umbral pasa a ser lo que la memoria dice que es: un
    /// sitio razonable desde el que mirar, se ve la sala que se deja y la que se entra.
    ///
    /// ES PRESENTACIÓN, NO NAVEGACIÓN. No toca la construcción del grafo, ni la alcanzabilidad,
    /// ni el desplazamiento: solo apaga <see cref="Renderer"/>. La consumen las dos versiones
    /// (escritorio y visor) en el mismo punto lógico —al cambiar el nodo actual— para que el
    /// comportamiento sea idéntico.
    ///
    /// DECISIONES, con su porqué:
    ///
    ///  · SE APAGA EL RENDERER, NUNCA EL COLLIDER. La puerta sigue siendo seleccionable y su
    ///    ficha de metadatos sigue respondiendo mientras es invisible — el mismo motivo por el
    ///    que los marcadores de navegación conservan su volumen con la malla oculta. Apagar el
    ///    objeto entero además desregistraría el nodo de la escena.
    ///
    ///  · CORTE SECO, SIN DESVANECIMIENTO. Un fundido exige instanciar materiales y cambiarlos
    ///    a la cola transparente; en URP eso depende de variantes de sombreador que el recorte
    ///    de la build puede haber eliminado —la clase de fallo que ya costó un bloque entero
    ///    con los oclusores— y añade trabajo por fotograma. El corte es gratis, ocurre en el
    ///    instante de la llegada (cuando la atención está en la estancia de destino, no en la
    ///    hoja) y es trivialmente reversible. Si en el visor el corte resulta brusco, el
    ///    fundido puede añadirse después sobre esta misma clase.
    ///
    ///  · PUERTAS DE VARIAS HOJAS: se apagan TODOS los renderers bajo el elemento IfcDoor del
    ///    nodo (hoja, tirador, vidrio del ojo de buey…), porque forman una sola entidad IFC.
    ///    Se registran solo los que estaban encendidos, para no «restituir» algo que otro
    ///    sistema hubiera apagado por su cuenta.
    ///
    ///  · DOS PUERTAS PRÓXIMAS: cada nodo referencia una entidad IfcDoor distinta (el grafo
    ///    identifica nodos por GlobalId, único), así que nunca hay ambigüedad sobre qué hoja
    ///    ocultar. Al saltar de un nodo puerta a otro contiguo, la llegada restituye primero
    ///    la anterior y oculta después la nueva: como máximo hay UNA puerta invisible en todo
    ///    momento, y ninguna queda invisible tras un recorrido largo.
    ///
    /// Todas las salidas dejan traza (LogWarning): una puerta que no se restituyera sería un
    /// fallo silencioso exactamente del tipo que la casa prohíbe.
    /// </summary>
    public static class PuertaTransparente
    {
        private static IfcMetadata _puertaOculta;
        private static readonly List<Renderer> _renderersApagados = new List<Renderer>();

        /// <summary>La puerta actualmente invisible, o null. Expuesta para diagnóstico.</summary>
        public static IfcMetadata PuertaOculta => _puertaOculta;

        /// <summary>
        /// Notifica que el usuario ha pasado a ocupar un nodo. Si el nodo anterior era una
        /// puerta, su hoja se restituye; si el nuevo es una puerta, su hoja se oculta. Admite
        /// null (nodo sin elemento en escena, o navegación degradada): equivale a «no estoy en
        /// ninguna puerta» y solo restituye.
        /// </summary>
        public static void AlLlegarANodo(IfcMetadata metaDelNodo)
        {
            if (_puertaOculta == metaDelNodo) return; // mismo nodo puerta: nada que hacer

            Restituir();

            if (metaDelNodo == null || metaDelNodo.ifcType != "IfcDoor") return;

            int apagados = 0;
            foreach (var r in metaDelNodo.GetComponentsInChildren<Renderer>(true))
            {
                if (r == null || !r.enabled) continue;
                r.enabled = false;
                _renderersApagados.Add(r);
                apagados++;
            }
            _puertaOculta = metaDelNodo;

            Debug.LogWarning($"[DigitalTwin] Puerta transparente: hoja de '{metaDelNodo.ifcName}' " +
                             $"oculta ({apagados} renderer/s) mientras el usuario ocupa su nodo. " +
                             "El collider se conserva: la puerta sigue siendo consultable.");
        }

        /// <summary>
        /// Vuelve a dibujar la puerta oculta, si la hay. Además de en cada cambio de nodo, se
        /// llama al destruirse el gestor de recorrido de cada versión: por ningún camino debe
        /// quedar una puerta invisible al salir de la navegación.
        /// </summary>
        public static void Restituir()
        {
            if (_puertaOculta == null && _renderersApagados.Count == 0) return;

            int restituidos = 0;
            foreach (var r in _renderersApagados)
            {
                if (r == null) continue; // la escena pudo descargarse; no hay nada que restituir
                r.enabled = true;
                restituidos++;
            }

            string nombre = _puertaOculta != null ? _puertaOculta.ifcName : "(elemento ya descargado)";
            Debug.LogWarning($"[DigitalTwin] Puerta transparente: hoja de '{nombre}' restituida " +
                             $"({restituidos} renderer/s) al abandonar su nodo.");

            _renderersApagados.Clear();
            _puertaOculta = null;
        }
    }
}
