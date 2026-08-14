using IFCImporter;
using UnityEngine;

namespace DigitalTwin.Navigation
{
    /// <summary>
    /// LA REGLA DE POSICIÓN DE LOS NODOS DE NAVEGACIÓN, en un único sitio para el generador
    /// del grafo y para los dos consumidores en ejecución (escritorio y visor) — el mismo
    /// patrón de definición única que <see cref="NavReachability"/> y las zonas.
    ///
    /// TODOS LOS NODOS A LA MISMA ALTURA SOBRE SU SUELO: 1,40 m (decisión del 15-08, con la
    /// investigación detrás en la nota de diseño de PRUEBA-AR-6 y en el estado del arte de la
    /// memoria). El razonamiento, comprimido:
    ///
    ///  · En el visor, la altura de los OJOS la impone el usuario a través del seguimiento a
    ///    nivel de suelo: no es un parámetro del programa. El parámetro de diseño es la altura
    ///    del MARCADOR, y el criterio es que quede en o bajo la línea de visión de
    ///    prácticamente cualquier adulto de pie: el sitio al que vas a ir se mira ligeramente
    ///    hacia abajo, no hacia arriba.
    ///  · La cota la da la antropometría: la altura de ojos de pie del percentil 5 femenino es
    ///    1,421 m (ANSUR II, Gordon et al. 2014; tablas resumen del Ergonomics Center, NCSU).
    ///    1,40 m queda por debajo de ella con margen, así que ≥95 % de los adultos ven los
    ///    nodos en o bajo su horizonte; para la estatura mediana (ojos ~1,58 m) el descenso de
    ///    mirada a un salto típico de 3 m es ~3,4°, dentro de la región de confort de 0–35°
    ///    bajo el horizonte y en la dirección de la mirada de reposo (10–20° bajo el
    ///    horizonte) que documenta Microsoft para realidad mixta.
    ///  · No más bajo: el marcador debe librar el mobiliario (~0,9–1,1 m) y esta misma altura
    ///    es la de la cámara de la versión de escritorio, que debe seguir siendo una altura de
    ///    ojos adulta plausible para que las capturas de ambas versiones sean comparables.
    ///
    /// LA ALTURA ES SOBRE EL SUELO DEL NODO, no absoluta: en un modelo de varias plantas cada
    /// nodo conserva su planta (el filtro de desnivel del generador sigue funcionando). El
    /// suelo se estima por tipo: para una puerta, la base de su volumen (la hoja arranca del
    /// suelo); para un punto de vista "Esfera...", su altura de autor menos los 1,55 m con
    /// que se colocaron en el modelo (cifra medida sobre el activo y documentada en la guía).
    ///
    /// Antes de esta regla, las puertas usaban el CENTRO de su hoja (~1,05 m) y las esferas su
    /// altura de autor (1,55 m): dos alturas distintas que obligaron a una cadena de reglas
    /// especiales aguas abajo (cartel sobre el dintel, viaje que conservaba la altura,
    /// alturas de vista por defecto). Unificar la altura elimina la causa; las reglas
    /// especiales se retiraron con ella.
    /// </summary>
    public static class PosicionDeNodos
    {
        /// <summary>Altura de los nodos sobre su suelo, en metros. Ver la cabecera.</summary>
        public const float AlturaDeNodo = 1.40f;

        /// <summary>Altura de autor de los puntos "Esfera..." del modelo de referencia, medida
        /// sobre el activo (36 de 36 a y=1,550 exactos). Se usa para estimar su suelo.</summary>
        public const float AlturaAutorPuntosDeVista = 1.55f;

        /// <summary>
        /// Posición viva de un nodo de navegación: su planta (x, z) y la altura unificada
        /// sobre su suelo. Es la que consumen el generador del grafo, el recorrido de
        /// escritorio y el navegador del visor — una sola definición.
        /// </summary>
        public static Vector3 De(IfcMetadata meta)
        {
            if (meta == null) return Vector3.zero;

            if (meta.ifcType == "IfcDoor")
            {
                var r = meta.GetComponentInChildren<Renderer>();
                if (r != null)
                    return new Vector3(r.bounds.center.x, r.bounds.min.y + AlturaDeNodo,
                                       r.bounds.center.z);
                // Puerta sin renderer (no debería ocurrir): el origen del objeto cae en una
                // esquina del marco, a nivel de suelo, así que sirve como suelo.
                Vector3 p = meta.transform.position;
                return new Vector3(p.x, p.y + AlturaDeNodo, p.z);
            }

            Vector3 pos = meta.transform.position;
            return new Vector3(pos.x, pos.y - AlturaAutorPuntosDeVista + AlturaDeNodo, pos.z);
        }
    }
}
