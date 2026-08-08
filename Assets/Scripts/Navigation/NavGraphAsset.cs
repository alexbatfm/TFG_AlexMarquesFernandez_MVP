using System;
using System.Collections.Generic;
using UnityEngine;

namespace DigitalTwin.Navigation
{
    /// <summary>
    /// Grafo de navegación entre los puntos "Esfera..." del modelo, precalculado en el Editor
    /// (ver <c>NavGraphBuilder</c>) y consultado en tiempo de ejecución por
    /// <see cref="TourNavigationManager"/>.
    ///
    /// Por qué precalcularlo y guardarlo en vez de resolverlo en cada arranque:
    ///
    ///  - Es inspeccionable. Al ser un asset se puede abrir en el Inspector y ver exactamente qué
    ///    puntos conectan con cuáles, que es lo que hace falta cuando un salto "raro" resulta
    ///    molesto en la demo.
    ///  - Es editable a mano. Si el algoritmo une dos puntos separados por un muro de carga, o
    ///    deja de unir dos que sí se comunican por una puerta, se corrige esa arista concreta sin
    ///    tener que retocar heurísticas que afectarían a todo el edificio.
    ///  - Es estable. Un cálculo en tiempo de ejecución podría dar resultados ligeramente
    ///    distintos entre ejecuciones si cambia el orden de los objetos; un asset versionado en
    ///    git da siempre la misma navegación, que para una demo evaluable importa.
    ///
    /// Los nodos se identifican por GlobalId de IFC y no por índice ni por nombre de GameObject:
    /// es el único identificador que sobrevive a reimportar el modelo.
    /// </summary>
    public class NavGraphAsset : ScriptableObject
    {
        [Serializable]
        public class Nodo
        {
            public string GlobalId;
            public string Nombre;
            public string Sala;
            public Vector3 Posicion;
            /// <summary>Índices dentro de <see cref="Nodos"/> de los puntos conectados a este.</summary>
            public List<int> Vecinos = new List<int>();
        }

        public List<Nodo> Nodos = new List<Nodo>();

        [Header("Trazabilidad de la generación")]
        public string GeneradoEl;
        public string EscenaOrigen;
        [Tooltip("Parámetros con los que se generó, para poder reproducir el resultado.")]
        public string Parametros;

        private Dictionary<string, int> _indicePorGlobalId;

        /// <summary>Índice del nodo con ese GlobalId, o -1 si el punto no está en el grafo.</summary>
        public int IndiceDe(string globalId)
        {
            if (string.IsNullOrEmpty(globalId)) return -1;

            if (_indicePorGlobalId == null)
            {
                _indicePorGlobalId = new Dictionary<string, int>(Nodos.Count);
                for (int i = 0; i < Nodos.Count; i++)
                    if (!string.IsNullOrEmpty(Nodos[i].GlobalId))
                        _indicePorGlobalId[Nodos[i].GlobalId] = i;
            }

            return _indicePorGlobalId.TryGetValue(globalId, out int idx) ? idx : -1;
        }

        /// <summary>Número total de aristas (cada conexión se cuenta una sola vez).</summary>
        public int ContarAristas()
        {
            int total = 0;
            foreach (var n in Nodos) total += n.Vecinos.Count;
            return total / 2;
        }
    }
}
