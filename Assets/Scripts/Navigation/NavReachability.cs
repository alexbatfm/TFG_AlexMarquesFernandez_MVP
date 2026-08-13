using System;
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
    /// igual: TourNavigationManager pasa a delegar aquí, con el mismo resultado que su bucle
    /// anterior, y el controlador inmersivo consume esta misma función.
    ///
    /// La clase es pura a propósito: opera sobre índices del grafo y no toca la escena. Los
    /// nodos de puerta se identifican mediante un predicado que aporta quien llama, porque el
    /// asset del grafo no guarda el tipo IFC y la escena sí lo sabe (por GlobalId).
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

        /// <summary>
        /// Resolución del destino de una pulsación: si el nodo señalado es una puerta, el
        /// desplazamiento continúa hasta el siguiente nodo en lugar de dejar al usuario dentro
        /// del vano.
        ///
        /// Devuelve la ruta como índices de nodo SIN incluir el origen: el primer elemento es el
        /// nodo pulsado y el último el destino final. Para un nodo normal la ruta tiene un solo
        /// elemento; para una puerta, la puerta y su continuación (y así sucesivamente si la
        /// continuación vuelve a ser puerta, hasta <paramref name="maxPuertasEncadenadas"/>).
        ///
        /// Elección de la continuación cuando la puerta tiene más de un vecino aparte del origen:
        /// el que mejor prolonga la trayectoria, por producto escalar entre la dirección de
        /// llegada a la puerta y la dirección hacia cada candidato (ambas en planta). NO se usa
        /// la arista más barata: el coste mide obstáculos, no intención, y puede devolver al
        /// usuario a la misma sala de la que viene.
        ///
        /// Esto vive aquí, en la capa de navegación, y no en la construcción del grafo: el grafo
        /// ya trata las puertas como nodos intermedios y no hay que tocarlo.
        /// </summary>
        /// <param name="grafo">Grafo precalculado.</param>
        /// <param name="indiceOrigen">Nodo en el que está el usuario.</param>
        /// <param name="indicePulsado">Nodo que ha señalado.</param>
        /// <param name="esPuerta">Predicado que dice si un índice de nodo es una puerta. Lo
        /// aporta el llamante porque el asset no guarda el tipo IFC.</param>
        /// <param name="maxPuertasEncadenadas">Tope de puertas seguidas, contra un grafo
        /// degenerado en el que las puertas formen un ciclo.</param>
        public static List<int> ResolverDestino(NavGraphAsset grafo, int indiceOrigen,
                                                int indicePulsado, Func<int, bool> esPuerta,
                                                int maxPuertasEncadenadas = 3)
        {
            var ruta = new List<int>();
            if (grafo == null || esPuerta == null ||
                indiceOrigen < 0 || indiceOrigen >= grafo.Nodos.Count ||
                indicePulsado < 0 || indicePulsado >= grafo.Nodos.Count)
            {
                Debug.LogWarning("[DigitalTwin] NavReachability.ResolverDestino: argumentos inválidos " +
                                 $"(origen {indiceOrigen}, pulsado {indicePulsado}); no se resuelve ruta.");
                return ruta;
            }

            ruta.Add(indicePulsado);

            int previo = indiceOrigen;
            int actual = indicePulsado;
            int puertasEncadenadas = 0;

            while (esPuerta(actual))
            {
                if (puertasEncadenadas >= maxPuertasEncadenadas)
                {
                    Debug.LogWarning($"[DigitalTwin] NavReachability: {puertasEncadenadas} puertas " +
                                     "encadenadas; se detiene la resolución y el destino queda en la " +
                                     $"última ('{grafo.Nodos[actual].Nombre}'). Revisa el grafo si esto " +
                                     "no es un vestíbulo real.");
                    break;
                }

                int continuacion = ElegirContinuacion(grafo, previo, actual);
                if (continuacion < 0)
                {
                    Debug.LogWarning($"[DigitalTwin] NavReachability: la puerta '{grafo.Nodos[actual].Nombre}' " +
                                     "no tiene ningún vecino distinto del origen; el desplazamiento " +
                                     "termina en el vano. El grafo tiene una puerta sin salida.");
                    break;
                }

                ruta.Add(continuacion);
                previo = actual;
                actual = continuacion;
                puertasEncadenadas++;
            }

            return ruta;
        }

        /// <summary>
        /// Vecino de la puerta que mejor prolonga la trayectoria de llegada, por producto
        /// escalar en planta. Devuelve -1 si no hay candidatos.
        ///
        /// Se excluye únicamente el nodo del que se viene. Si todos los candidatos quedan hacia
        /// atrás (producto escalar negativo) se elige igualmente el menos malo, porque dejar al
        /// usuario dentro del vano es peor que girar; el caso queda registrado por el llamante,
        /// que conoce los nombres.
        /// </summary>
        private static int ElegirContinuacion(NavGraphAsset grafo, int previo, int puerta)
        {
            Vector3 llegada = grafo.Nodos[puerta].Posicion - grafo.Nodos[previo].Posicion;
            llegada.y = 0f;
            bool llegadaValida = llegada.sqrMagnitude > 0.0001f;
            if (llegadaValida) llegada.Normalize();

            int mejor = -1;
            float mejorProducto = float.MinValue;

            foreach (int candidato in VecinosAlcanzables(grafo, puerta))
            {
                if (candidato == previo) continue;

                Vector3 salida = grafo.Nodos[candidato].Posicion - grafo.Nodos[puerta].Posicion;
                salida.y = 0f;
                if (salida.sqrMagnitude < 0.0001f) continue;
                salida.Normalize();

                // Sin dirección de llegada utilizable (puerta encima del origen, caso degenerado)
                // se elige el primer candidato válido de forma determinista.
                float producto = llegadaValida ? Vector3.Dot(llegada, salida) : 0f;
                if (producto > mejorProducto)
                {
                    mejorProducto = producto;
                    mejor = candidato;
                }
            }

            return mejor;
        }

        /// <summary>Producto escalar de la mejor continuación, para poder registrarlo.</summary>
        public static float ProductoEscalarDe(NavGraphAsset grafo, int previo, int puerta, int continuacion)
        {
            if (grafo == null || previo < 0 || puerta < 0 || continuacion < 0) return 0f;
            Vector3 llegada = grafo.Nodos[puerta].Posicion - grafo.Nodos[previo].Posicion;
            Vector3 salida = grafo.Nodos[continuacion].Posicion - grafo.Nodos[puerta].Posicion;
            llegada.y = 0f; salida.y = 0f;
            if (llegada.sqrMagnitude < 0.0001f || salida.sqrMagnitude < 0.0001f) return 0f;
            return Vector3.Dot(llegada.normalized, salida.normalized);
        }
    }
}
