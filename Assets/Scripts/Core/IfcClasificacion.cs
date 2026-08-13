namespace DigitalTwin.Core
{
    /// <summary>
    /// Taxonomía única de tipos IFC según lo que dejan pasar: la vista, al operario, o nada.
    ///
    /// POR QUÉ EXISTE COMO CLASE DE EJECUCIÓN Y NO DENTRO DE NavGraphBuilder
    ///
    /// Estas familias nacieron en <c>Assets/Editor/NavGraphBuilder.cs</c> para penalizar lo que
    /// atraviesa cada arista del grafo de navegación. La Fase 5 necesita exactamente la misma
    /// distinción para decidir qué geometría ocluye en modo anclado: «qué deja pasar la vista» y
    /// «qué deja pasar al operario» son la misma pregunta. Pero NavGraphBuilder vive en el
    /// ensamblado del Editor, que no se compila en el reproductor, así que el código de ejecución
    /// no puede referenciarlo. La taxonomía se muda aquí y el Editor pasa a consumirla de este
    /// fichero: una sola tabla sirviendo a los dos subsistemas, sin duplicarla a mano —que es
    /// justo la clase de duplicación que ya costó una omisión en el arranque de Realidad
    /// Aumentada.
    ///
    /// Las cadenas y la semántica de <see cref="EsDeTipo"/> (igualdad o prefijo) son las que
    /// tenía NavGraphBuilder: mover no es cambiar, y el grafo regenerado debe salir idéntico.
    /// </summary>
    public static class IfcClasificacion
    {
        /// <summary>
        /// Pasos practicables: atravesarlos es lo que hace una persona al ir de una estancia a
        /// otra, no un atajo imposible.
        /// </summary>
        public static readonly string[] TiposDePaso =
        {
            "IfcDoor", "IfcOpeningElement", "IfcStair", "IfcStairFlight", "IfcRamp"
        };

        /// <summary>
        /// Cerramientos transparentes: ventanas, mamparas y muro cortina con sus montantes y
        /// paneles. A través de ellos se ve, así que ni penalizan como un muro en el grafo ni
        /// deben ocluir en modo anclado: un vidrio que borra lo que hay detrás es un muro.
        /// Se incluyen IfcMember e IfcPlate porque en este modelo son los montantes y los
        /// paneles de la fachada acristalada.
        /// </summary>
        public static readonly string[] TiposTransparentes =
        {
            "IfcWindow", "IfcCurtainWall", "IfcPlate", "IfcMember"
        };

        /// <summary>Cerramientos opacos: delimitan el espacio y no dejan ver al otro lado.</summary>
        public static readonly string[] TiposDeCerramiento =
        {
            "IfcWall", "IfcWallStandardCase", "IfcSlab", "IfcRoof", "IfcColumn", "IfcBeam"
        };

        /// <summary>
        /// Lo que ocluye en modo anclado: muros, forjados, pilares y revestimientos. Es un
        /// subconjunto deliberado de <see cref="TiposDeCerramiento"/> más IfcCovering; quedan
        /// fuera las puertas (el IFC no sabe si están abiertas) y el mobiliario (se mueve),
        /// siguiendo el principio de que un oclusor equivocado resta y restar es peor que sumar.
        /// Sobre el modelo de referencia: 94 muros + 1 forjado + 6 pilares + 12 revestimientos
        /// = 113 elementos (medido sobre metadata.json el 2026-08-13).
        /// </summary>
        public static readonly string[] TiposOclusores =
        {
            "IfcWall", "IfcSlab", "IfcColumn", "IfcCovering"
        };

        /// <summary>
        /// Igualdad exacta o prefijo, la misma regla que usaba NavGraphBuilder: así
        /// "IfcWallStandardCase" cae en la familia de "IfcWall" sin enumerarlo aparte.
        /// </summary>
        public static bool EsDeTipo(string ifcType, string[] familia)
        {
            if (string.IsNullOrEmpty(ifcType)) return false;
            foreach (var t in familia)
                if (ifcType == t || ifcType.StartsWith(t)) return true;
            return false;
        }

        /// <summary>
        /// ¿Debe este tipo escribir profundidad en modo anclado?
        ///
        /// El orden de las comprobaciones importa: la regla de prefijo hace que "IfcWallType"
        /// empiece por "IfcWall", así que primero se descartan las definiciones de catálogo
        /// (que además ya están ocultas y sin colisionador) y los transparentes, y solo entonces
        /// se consulta la familia de oclusores.
        /// </summary>
        public static bool EsOclusor(string ifcType)
        {
            if (string.IsNullOrEmpty(ifcType)) return false;
            if (SceneModelIndex.EsDefinicionDeTipo(ifcType)) return false;
            if (EsDeTipo(ifcType, TiposTransparentes)) return false;
            return EsDeTipo(ifcType, TiposOclusores);
        }
    }
}
