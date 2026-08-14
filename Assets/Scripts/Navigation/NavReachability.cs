using System.Collections.Generic;
using UnityEngine;

namespace DigitalTwin.Navigation
{
    /// <summary>
    /// La definición de alcanzabilidad del recorrido, en un único sitio.
    ///
    /// POR QUÉ EXISTE
    ///
    /// «Dado un nodo actual, a qué nodos se puede ir» estaba implementado solo dentro de
    /// <see cref="TourNavigationManager"/> (escritorio). El arranque de Realidad Aumentada no
    /// instancia esa clase, así que su controlador de interacción no imponía el grafo: se podía
    /// viajar a cualquier punto atravesando cerramientos, invalidando en la versión inmersiva las
    /// penalizaciones y las puertas como nodos intermedios que el proyecto defiende.
    ///
    /// De las dos vías posibles —extraer un ayudante puro que ambos arranques consuman, o
    /// instanciar TourNavigationManager también en Realidad Aumentada— se elige la primera.
    /// TourNavigationManager mezcla la consulta del grafo con la presentación (proyecta
    /// indicadores a coordenadas de pantalla, gestiona un pool de UI de escritorio y mueve la
    /// cámara directamente, no el origen de realidad extendida); reutilizarlo en el visor habría
    /// exigido neutralizar esas tres cosas a la vez, con más superficie de fallo que compartir la
    /// consulta. Lo que importa —que solo exista UNA definición de alcanzabilidad— se cumple
    /// igual: TourNavigationManager delega aquí, con el mismo resultado que su bucle anterior
    /// (equivalencia comprobada nodo a nodo sobre los 54 nodos del grafo de referencia), y el
    /// controlador inmersivo consume esta misma función.
    ///
    /// QUÉ NO ES. Entre el 2026-08-13 y el 14 esta clase tuvo dos mecanismos más: el
    /// tránsito automático a través de nodos puerta (continuación elegida por producto escalar)
    /// y la expansión de la oferta «a través de» las puertas (DestinosOfrecidos). Las pruebas
    /// en el visor del 14-08 los descartaron y la decisión tras la segunda prueba de ese día
    /// fue REVERTIRLOS: el grafo
    /// se consulta tal cual y pulsar un destino lleva a ese destino, exactamente el
    /// comportamiento que documentan la memoria (capítulo 6) y la guía (capítulo 5). El efecto
    /// de «ver la estancia al otro lado» lo aporta ahora una regla de presentación
    /// (<see cref="PuertaTransparente"/>), que no toca ni el grafo ni la navegación. El grafo y
    /// su generación (NavGraphBuilder: RNG sobre coste con cuatro penalizaciones y pasada de
    /// conectividad) nunca cambiaron durante ese periodo: la reversión es solo de esta capa de
    /// consulta.
    ///
    /// La clase es pura a propósito: opera sobre índices del grafo y no toca la escena.
    /// </summary>
    public static class NavReachability
    {
        /// <summary>
        /// Vecinos alcanzables desde un nodo, como índices dentro de <c>grafo.Nodos</c>.
        ///
        /// Es la traducción literal del bucle que usaba TourNavigationManager: se respeta el
        /// orden de la lista de vecinos del asset y se descartan índices fuera de rango (un
        /// asset editado a mano puede tenerlos). Devuelve lista vacía —nunca null— si el nodo
        /// no existe o no tiene vecinos; distinguir «sin grafo» de «sin vecinos» es cosa del
        /// llamante, que es quien sabe si tiene un plan B.
        /// </summary>
        public static List<int> VecinosAlcanzables(NavGraphAsset grafo, int indiceNodo)
        {
            var resultado = new List<int>();
            if (grafo == null || indiceNodo < 0 || indiceNodo >= grafo.Nodos.Count)
            {
                Debug.LogWarning($"[DigitalTwin] NavReachability: consulta de vecinos sobre un nodo " +
                                 $"inválido (índice {indiceNodo}, grafo " +
                                 $"{(grafo == null ? "nulo" : grafo.Nodos.Count + " nodos")}). " +
                                 "Se devuelve una lista vacía.");
                return resultado;
            }

            foreach (int v in grafo.Nodos[indiceNodo].Vecinos)
            {
                if (v < 0 || v >= grafo.Nodos.Count) continue;
                if (v == indiceNodo) continue;
                resultado.Add(v);
            }
            return resultado;
        }
    }
}
