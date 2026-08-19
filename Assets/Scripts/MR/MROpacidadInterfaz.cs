using UnityEngine;

namespace DigitalTwin.MR
{
    /// <summary>
    /// Opacidad de los fondos de interfaz del visor, y el porqué de su valor. Punto único: la
    /// ficha de activos, el menú de zonas, el menú del modo anclado y el panel de colocación
    /// del anclaje y el aviso de cámara caída leen de aquí. Antes de reunirlos, el valor
    /// estaba escrito cinco veces y con tres cifras distintas: 0,91 en el arranque, 0,93 en el
    /// aviso y 0,94 en los tres restantes. Es exactamente el desajuste que esta clase existe
    /// para impedir.
    ///
    /// POR QUÉ IMPORTA. En el visor estos lienzos se dibujan sobre una capa de PROYECCIÓN con
    /// alfa: la cámara borra a color sólido con alfa cero y, en modo anclado, la geometría del
    /// modelo escribe solo profundidad. Bajo el texto no queda ninguna superficie opaca, de
    /// modo que el fondo del panel es la ÚNICA que escribe alfa, y ese alfa es el que el
    /// compositor del dispositivo usa para mezclar el fotograma con el vídeo de las cámaras.
    /// El resultado es que la opacidad de estos fondos no es una preferencia estética: decide
    /// cuánta luz de la sala se suma al fondo del texto, y con ella la legibilidad.
    ///
    /// LA CIFRA. Hasta el 17 de agosto de 2026 el fondo iba a 0,55 «para dar sensación de
    /// espacio», y en la primera sesión con vídeo de transparencia real el panel no se leyó
    /// delante de una sala iluminada. El valor vigente es 0,92, decidido por Alex, y sale de
    /// la cuenta que sigue.
    ///
    /// EL ALFA QUE LLEGA AL COMPOSITOR NO ES EL DEL COLOR. El material de estos fondos es
    /// «UI/Default», cuya mezcla es <c>Blend SrcAlpha OneMinusSrcAlpha</c> aplicada a los
    /// cuatro canales. Sobre un destino borrado con alfa cero, el canal alfa resultante es
    ///
    ///     a = alfa*alfa + 0*(1 - alfa) = alfa²
    ///
    /// es decir 0,8464 para un color de alfa 0,92, y el vídeo pasa en la fracción 1 - alfa² =
    /// 0,1536. No es un supuesto pesimista inventado: es el comportamiento de la mezcla por
    /// defecto de la interfaz de Unity cuando el fotograma se compone contra un fondo
    /// transparente, y el registro del dispositivo del 18 de agosto de 2026 confirma el otro
    /// eslabón — la capa de proyección se envía con las banderas
    /// BLEND_TEXTURE_SOURCE_ALPHA|UNPREMULTIPLIED_ALPHA, o sea que el compositor usa ese alfa
    /// escrito y vuelve a multiplicar por él el color de la capa.
    ///
    /// CONTRASTE RESULTANTE, peor caso. Fondo (0,05 0,06 0,08) en sRGB, texto blanco, vídeo
    /// blanco saturado (una ventana al mediodía), mezcla en luz lineal, que es la que hace el
    /// compositor y también la más desfavorable aquí. Luminancia relativa del fondo compuesto
    /// según la definición de WCAG 2.1:
    ///
    ///     L(fondo) = 0,1574   ->   contraste con blanco = (1 + 0,05) / (0,1574 + 0,05) = 5,06:1
    ///
    /// El umbral de referencia de RNF-07 es el de WCAG 2.1 AA para texto normal, 4,5:1, así
    /// que el texto blanco lo cumple con margen. El alfa por debajo del cual deja de cumplirlo
    /// es 0,906, lo que explica que 0,91 estuviera al filo (4,66:1) y que la centésima que
    /// separaba el código de su propio comentario no fuera cosmética.
    ///
    /// LO QUE ESTA CIFRA NO CUBRE. 4,5:1 lo alcanza el texto blanco. Los grises secundarios
    /// del panel y de los menús —la ruta jerárquica, la línea de ayuda, el pie del menú, el
    /// texto tenue del panel de anclaje— quedan entre 1,17:1 y 4,22:1 sobre ese mismo vídeo
    /// blanco. Sobre el panel opaco, sin vídeo, van de 4,43:1 a 19,14:1. La corrección de esos
    /// grises es de color de texto y no de opacidad de fondo: subir el alfa hasta rescatarlos
    /// exigiría 0,98, que es opacidad completa a efectos prácticos y renuncia a la
    /// translucidez que se buscaba. Queda anotado en REQUISITOS-memoria.md como decisión.
    ///
    /// EL ALFA ES POR PÍXEL. Sube solo donde el fondo pinta, es decir en el rectángulo de cada
    /// panel dentro de su lienzo de mundo. El resto de la escena sigue con la cámara a alfa
    /// cero y los oclusores sin color, así que la transparencia fuera de los paneles no cambia.
    ///
    /// QUÉ QUEDA FUERA, Y POR QUÉ. Cuatro superficies del visor no siguen esta constante, a
    /// propósito, y se enumeran aquí para que la excepción no viva escondida en su fichero.
    ///
    ///   - Selector de modo (<see cref="MRModeSelector"/>, alfa 0,85) y pantalla de carga
    ///     (<see cref="MRPantallaDeCarga"/>, alfa 0,90). Transitorias, con color base propio
    ///     (0,07 0,10 0,15) y anteriores al montaje del gemelo digital, de modo que no hay
    ///     ninguna ficha ni menú al lado con el que compararlas. Son además de lo poco
    ///     verificado con el casco puesto. Texto blanco sobre vídeo blanco saturado: 3,15:1 y
    ///     4,25:1.
    ///   - Leyenda pegada al mando (<see cref="MRControllerRig"/>, alfa 0,78) y rótulo del
    ///     elemento señalado (<see cref="MRInteractionController"/>, alfa 0,82). Son rótulos
    ///     pequeños y permanentes, no paneles: subirlos a 0,92 los convierte en dos parches
    ///     opacos delante de la mano y del propio elemento que nombran, que es lo contrario de
    ///     lo que hacen. Con su texto (0,92 0,94 0,97) dan 2,06:1 y 2,41:1 sobre vídeo blanco
    ///     saturado, así que la carencia está medida y es una decisión pendiente, no un olvido.
    ///
    /// Si alguna vez se unifican, el sitio es este y no sus cuatro ficheros.
    ///
    /// PENDIENTE DE MEDIR CON EL CASCO. Que el alfa escrito sea alfa² está deducido del
    /// sombreador, no leído del dispositivo: <see cref="MRDiagnosticoComposicion"/> muestrea el
    /// centro y dos cuadrantes del objetivo del ojo, y en el registro del 18 de agosto ninguna
    /// lectura cayó dentro de un panel. Una lectura tomada con la ficha abierta delante del
    /// punto de muestreo cerraría la cuestión: debería dar 0,85 y no 0,92.
    /// </summary>
    public static class MROpacidadInterfaz
    {
        /// <summary>Opacidad del fondo de todo panel o menú del visor. Único sitio donde se
        /// escribe la cifra.</summary>
        public const float FondoDePanel = 0.92f;

        /// <summary>Color de fondo de los paneles y menús del visor, con la opacidad ya
        /// aplicada. El gris azulado muy oscuro es el mismo del escritorio, para que las dos
        /// plataformas se reconozcan como la misma aplicación.</summary>
        public static readonly Color ColorDeFondo = new Color(0.05f, 0.06f, 0.08f, FondoDePanel);

        /// <summary>Devuelve <paramref name="rgb"/> con la opacidad canónica de fondo. Para
        /// superficies que necesiten otro color base sin salirse del criterio.</summary>
        public static Color ConOpacidadDeFondo(Color rgb)
        {
            return new Color(rgb.r, rgb.g, rgb.b, FondoDePanel);
        }
    }
}
