using DigitalTwin.Navigation;
using UnityEngine;
using UnityEngine.UI;

namespace DigitalTwin.UI
{
    /// <summary>
    /// Menú de pausa y configuración de la versión de escritorio: se abre con Escape y permite
    /// ajustar la sensibilidad del ratón y el modo de pantalla, además de cerrar la aplicación.
    ///
    /// Por qué existe. La aplicación compilada no ofrecía ninguna forma de salir salvo Alt+F4, ni
    /// de corregir una sensibilidad mal ajustada sin recompilar. En el Editor no se nota, porque
    /// el propio Editor da esos controles; en una build entregable, su ausencia es visible.
    ///
    /// Cómo se presentan los ajustes. Cada fila muestra a la izquierda qué configura, a la derecha
    /// su valor actual seguido de una flecha, y debajo una línea que explica para qué sirve. Un
    /// ajuste que cambia al pulsarlo, sin nada que lo anuncie, obliga al usuario a descubrirlo
    /// probando; y quien no lo pruebe nunca sabrá que existe.
    ///
    /// Por qué no se activa en Realidad Mixta. En un visor no hay tecla Escape ni puntero de ratón,
    /// la interfaz debe vivir en el espacio y no pegada a la pantalla, y salir de la aplicación lo
    /// gestiona el sistema del visor. El menú comprueba si hay un dispositivo XR activo y, en ese
    /// caso, no se construye: la Fase 5 tendrá el suyo propio.
    /// </summary>
    public class SettingsMenuController : MonoBehaviour
    {
        private const float AnchoPanel = 460f;
        private const float AltoFila = 38f;
        // Altura MÍNIMA de una línea de ayuda. La real se mide sobre el texto (ver Ajuste): una
        // ayuda de dos líneas ocupa lo que ocupa, no lo que se le reservó.
        private const float AltoAyuda = 15f;
        private const float Margen = 18f;
        // Separación entre las dos filas de acción del pie. Se separan algo más que los ajustes
        // porque son de naturaleza distinta (una vuelve, la otra sale) y no deben leerse como una
        // pareja de opciones intercambiables.
        private const float SeparacionAcciones = 10f;

        // Por encima del menú de zonas (30-31) y del panel de metadatos.
        private const int OrdenBase = 200;

        private const string ClaveSensibilidad = "dt.sensibilidad";

        // Un ciclo corto de valores con nombre es más comprensible que un número suelto: "0,25"
        // no dice nada, "rápida" sí.
        private static readonly float[] PasosSensibilidad = { 0.05f, 0.10f, 0.15f, 0.25f, 0.40f };
        private static readonly string[] NombresSensibilidad =
            { "muy lenta", "lenta", "normal", "rápida", "muy rápida" };

        private RectTransform _raiz;
        private Text _valorSensibilidad, _valorPantalla, _valorSolar;
        private Text _textoSalir;
        private Image _fondoSalir;
        private Text _pista;

        private TourCameraLook _camara;
        private bool _abierto;
        private bool _confirmandoSalida;
        private bool _pantallaCompleta;
        private int _framesParaResincronizarPantalla;

        public static bool MenuAbierto { get; private set; }

        public void Initialize(Canvas canvas)
        {
            if (UnityEngine.XR.XRSettings.isDeviceActive)
            {
                Debug.Log("[DigitalTwin] Menú de configuración omitido: hay un dispositivo XR activo.");
                enabled = false;
                return;
            }

            _camara = FindFirstObjectByType<TourCameraLook>();
            _pantallaCompleta = Screen.fullScreen;

            if (_camara != null)
                _camara.Sensitivity = PlayerPrefs.GetFloat(ClaveSensibilidad, _camara.Sensitivity);

            Construir(canvas.transform);
            Cerrar();
        }

        private void Construir(Transform padre)
        {
            // --- Pista permanente, fuera del menú, visible con el menú cerrado -----------------
            // Sin ella el menú es invisible: nada en pantalla sugiere que Escape haga algo.
            _pista = RuntimeUIFactory.CreateText(padre, "PistaEscape", "ESC  ·  menú y configuración",
                                                 13, TextAnchor.LowerLeft, new Color(1f, 1f, 1f, 0.55f));
            var rtP = (RectTransform)_pista.transform;
            rtP.anchorMin = rtP.anchorMax = Vector2.zero;
            rtP.pivot = Vector2.zero;
            rtP.anchoredPosition = new Vector2(Margen, Margen);
            rtP.sizeDelta = new Vector2(280f, 20f);

            // --- Menú --------------------------------------------------------------------------
            _raiz = RuntimeUIFactory.CreateRect(padre, "MenuConfiguracion");
            RuntimeUIFactory.StretchToParent(_raiz);

            // El velo atenúa la escena y absorbe las pulsaciones que caen fuera del panel, para
            // que no lleguen al modelo. Pulsar fuera cancela además una confirmación en curso.
            var velo = RuntimeUIFactory.CreatePanel(_raiz, "Velo", new Color(0f, 0f, 0f, 0.6f));
            var rtVelo = (RectTransform)velo.transform;
            RuntimeUIFactory.StretchToParent(rtVelo);
            ClickRouter.Instance.Register(rtVelo, CancelarConfirmacion, OrdenBase,
                                          isActive: () => _abierto);

            var panel = RuntimeUIFactory.CreateRect(_raiz, "Panel");
            panel.anchorMin = panel.anchorMax = new Vector2(0.5f, 0.5f);
            panel.pivot = new Vector2(0.5f, 0.5f);
            panel.anchoredPosition = Vector2.zero;
            // El ancho es fijo; la altura NO: se calcula al final, a partir de lo que ocupan de
            // verdad las filas (ver el cierre de este método). Hasta el 17-08 la altura salía de
            // una fórmula que reservaba «una línea extra» para la ayuda de la iluminación solar,
            // pero el recorrido vertical de las filas no la reservaba: la segunda línea de esa
            // ayuda se dibujaba por debajo de su rectángulo (los textos se crean con Overflow) y
            // el botón «Volver a la escena», dibujado después, la tapaba —la etiqueta «cortada»
            // de la sesión del 17-08—. Medir y derivar evita que el siguiente texto largo lo
            // repita.
            panel.sizeDelta = new Vector2(AnchoPanel, 0f);

            var fondo = RuntimeUIFactory.CreatePanel(panel, "Fondo", new Color(0.07f, 0.09f, 0.13f, 0.97f));
            RuntimeUIFactory.StretchToParent((RectTransform)fondo.transform);

            Cabecera(panel, "Configuración", 20, FontStyle.Bold, Color.white, -Margen, 28f);
            Cabecera(panel, "Pulsa una opción para cambiar su valor", 12, FontStyle.Normal,
                     new Color(1f, 1f, 1f, 0.55f), -(Margen + 30f), 18f);

            float y = -(Margen + 30f + 22f);

            _valorSensibilidad = Ajuste(panel, ref y, "Sensibilidad del ratón",
                "Cuánto gira la vista al arrastrar. Súbela si te cansa girar; bájala si se te pasa el punto.",
                CambiarSensibilidad, OrdenBase + 1);

            _valorPantalla = Ajuste(panel, ref y, "Modo de pantalla",
                "Alterna entre pantalla completa y ventana.",
                AlternarPantalla, OrdenBase + 2);

            _valorSolar = Ajuste(panel, ref y, "Iluminación solar",
                "Orienta el sol según la hora real y las coordenadas del modelo IFC. El edificio no " +
                "tiene luz artificial: de noche queda a oscuras.",
                AlternarSolar, OrdenBase + 3);

            Accion(panel, ref y, "Volver a la escena", new Color(0.14f, 0.26f, 0.19f, 1f),
                   Cerrar, OrdenBase + 4);

            _fondoSalir = Accion(panel, ref y, "Salir de la aplicación",
                                 new Color(0.30f, 0.13f, 0.13f, 1f), PulsarSalir, OrdenBase + 5,
                                 out _textoSalir);

            // Altura del panel = lo que ocupa el contenido + margen inferior. `y` apunta al
            // borde inferior de la última fila menos su separación; se deshace esa separación
            // para que el margen bajo el último botón sea exactamente `Margen`, igual que arriba.
            // Las filas están ancladas al borde superior del panel, así que cambiar la altura
            // aquí no las mueve.
            float alturaContenido = -(y + SeparacionAcciones);
            panel.sizeDelta = new Vector2(AnchoPanel, alturaContenido + Margen);

            Refrescar();
        }

        /// <summary>
        /// Altura que necesita un texto para mostrarse entero con el ancho que tiene asignado,
        /// nunca por debajo de <paramref name="minimo"/>. Mismo criterio que
        /// <c>MetadataPanelController.ColocarBloque</c>: los textos se crean con Overflow
        /// vertical (no se recorta información en silencio), así que quien maqueta tiene que
        /// darles el sitio que piden. Se añade un pequeño resguardo porque el cuerpo de letra
        /// efectivo se redondea con la escala del lienzo y una línea puede quedar al límite.
        /// </summary>
        private static float AltoNecesario(Text texto, float minimo)
        {
            const float Resguardo = 2f;
            float preferida = texto.preferredHeight;
            if (preferida <= 0f)
            {
                // Sin fuente cargada o sin ancho aún: no se puede medir. Se avisa y se cae al
                // mínimo, que es lo que había antes de medir.
                Debug.LogWarning("[DigitalTwin] No se ha podido medir la altura del texto de ayuda '" +
                                 texto.name + "' del menú de configuración; se usa la mínima (" +
                                 minimo.ToString("0") + " px).");
                return minimo;
            }
            return Mathf.Max(minimo, preferida + Resguardo);
        }

        private void Cabecera(RectTransform panel, string texto, int tam, FontStyle estilo,
                              Color color, float y, float alto)
        {
            var t = RuntimeUIFactory.CreateText(panel, "Cab" + texto.GetHashCode(), texto, tam,
                                                TextAnchor.MiddleCenter, color, estilo);
            var rt = (RectTransform)t.transform;
            rt.anchorMin = new Vector2(0, 1); rt.anchorMax = new Vector2(1, 1);
            rt.pivot = new Vector2(0.5f, 1);
            rt.anchoredPosition = new Vector2(0, y);
            rt.sizeDelta = new Vector2(0, alto);
        }

        /// <summary>
        /// Fila de ajuste: rótulo a la izquierda, valor actual y flecha a la derecha, y una línea
        /// de ayuda debajo. Devuelve el texto del valor para poder refrescarlo.
        /// </summary>
        private Text Ajuste(RectTransform panel, ref float y, string rotulo, string ayuda,
                            System.Action alPulsar, int orden)
        {
            var fila = RuntimeUIFactory.CreateRect(panel, "Ajuste" + orden);
            fila.anchorMin = new Vector2(0, 1); fila.anchorMax = new Vector2(1, 1);
            fila.pivot = new Vector2(0.5f, 1);
            fila.anchoredPosition = new Vector2(0, y);
            fila.sizeDelta = new Vector2(-Margen * 2, AltoFila);

            var fondo = RuntimeUIFactory.CreatePanel(fila, "Fondo", new Color(0.13f, 0.16f, 0.22f, 1f));
            RuntimeUIFactory.StretchToParent((RectTransform)fondo.transform);

            var izq = RuntimeUIFactory.CreateText(fila, "Rotulo", rotulo, 15,
                                                  TextAnchor.MiddleLeft, Color.white);
            var rtI = (RectTransform)izq.transform;
            RuntimeUIFactory.StretchToParent(rtI);
            rtI.offsetMin = new Vector2(14, 0);
            rtI.offsetMax = new Vector2(-14, 0);

            // El valor va a la derecha, resaltado, con una flecha que anuncia que hay un siguiente
            // valor. Es lo que convierte la fila en algo que parece pulsable.
            var der = RuntimeUIFactory.CreateText(fila, "Valor", "", 15,
                                                  TextAnchor.MiddleRight,
                                                  new Color(0.55f, 0.80f, 1f), FontStyle.Bold);
            var rtD = (RectTransform)der.transform;
            RuntimeUIFactory.StretchToParent(rtD);
            rtD.offsetMin = new Vector2(14, 0);
            rtD.offsetMax = new Vector2(-14, 0);

            ClickRouter.Instance.Register(fila, () => { CancelarConfirmacion(); alPulsar(); },
                                          orden, isActive: () => _abierto);
            y -= AltoFila + 3f;

            var txtAyuda = RuntimeUIFactory.CreateText(panel, "Ayuda" + orden, ayuda, 11,
                               TextAnchor.UpperLeft, new Color(1f, 1f, 1f, 0.45f));
            var rtA = (RectTransform)txtAyuda.transform;
            rtA.anchorMin = new Vector2(0, 1); rtA.anchorMax = new Vector2(1, 1);
            rtA.pivot = new Vector2(0.5f, 1);
            rtA.anchoredPosition = new Vector2(0, y);
            // Primero el ancho (del que depende el ajuste de línea) y después la altura medida
            // sobre ese ancho: una ayuda de dos líneas recibe dos líneas de sitio.
            rtA.sizeDelta = new Vector2(-Margen * 2 - 8f, AltoAyuda);
            float altoAyuda = AltoNecesario(txtAyuda, AltoAyuda);
            rtA.sizeDelta = new Vector2(-Margen * 2 - 8f, altoAyuda);
            y -= altoAyuda + 5f;

            return der;
        }

        private Image Accion(RectTransform panel, ref float y, string texto, Color color,
                             System.Action alPulsar, int orden)
        {
            Text ignorado;
            return Accion(panel, ref y, texto, color, alPulsar, orden, out ignorado);
        }

        private Image Accion(RectTransform panel, ref float y, string texto, Color color,
                             System.Action alPulsar, int orden, out Text etiqueta)
        {
            var fila = RuntimeUIFactory.CreateRect(panel, "Accion" + orden);
            fila.anchorMin = new Vector2(0, 1); fila.anchorMax = new Vector2(1, 1);
            fila.pivot = new Vector2(0.5f, 1);
            fila.anchoredPosition = new Vector2(0, y);
            fila.sizeDelta = new Vector2(-Margen * 2, AltoFila);

            var fondo = RuntimeUIFactory.CreatePanel(fila, "Fondo", color);
            RuntimeUIFactory.StretchToParent((RectTransform)fondo.transform);

            etiqueta = RuntimeUIFactory.CreateText(fila, "Texto", texto, 15,
                                                   TextAnchor.MiddleCenter, Color.white);
            RuntimeUIFactory.StretchToParent((RectTransform)etiqueta.transform);

            ClickRouter.Instance.Register(fila, alPulsar, orden, isActive: () => _abierto);
            y -= AltoFila + SeparacionAcciones;
            return fondo;
        }

        private void Update()
        {
            if (EscapePulsado())
            {
                // Con una confirmación de salida en curso, Escape la cancela en vez de cerrar el
                // menú: es la reacción esperable de quien se ha arrepentido.
                if (_confirmandoSalida) { CancelarConfirmacion(); return; }
                if (_abierto) Cerrar(); else Abrir();
            }

            // Screen.fullScreen no cambia en el mismo fotograma: el sistema aplica el cambio al
            // final, y a veces tarda alguno más. Leerlo justo después de asignarlo devolvía el
            // valor anterior, y por eso la etiqueta mostraba lo contrario de lo ocurrido. Se
            // resincroniza durante unos fotogramas tras cada cambio.
            if (_framesParaResincronizarPantalla > 0)
            {
                _framesParaResincronizarPantalla--;
                if (_pantallaCompleta != Screen.fullScreen)
                {
                    _pantallaCompleta = Screen.fullScreen;
                    Refrescar();
                }
            }
        }

        private static bool EscapePulsado()
        {
#if ENABLE_INPUT_SYSTEM
            var teclado = UnityEngine.InputSystem.Keyboard.current;
            return teclado != null && teclado.escapeKey.wasPressedThisFrame;
#else
            return Input.GetKeyDown(KeyCode.Escape);
#endif
        }

        public void Abrir()
        {
            _abierto = true;
            MenuAbierto = true;
            _raiz.gameObject.SetActive(true);
            if (_pista != null) _pista.gameObject.SetActive(false);
            if (_camara != null) _camara.enabled = false;
            _pantallaCompleta = Screen.fullScreen;
            _confirmandoSalida = false;
            Refrescar();
        }

        public void Cerrar()
        {
            _abierto = false;
            MenuAbierto = false;
            _confirmandoSalida = false;
            if (_raiz != null) _raiz.gameObject.SetActive(false);
            if (_pista != null) _pista.gameObject.SetActive(true);
            if (_camara != null)
            {
                _camara.enabled = true;
                // Reengancha el giro al estado real de la cámara para que no dé un salto al
                // recuperar el control.
                _camara.SyncFromTransform();
            }
        }

        private void CambiarSensibilidad()
        {
            if (_camara == null) return;
            int i = IndiceSensibilidad();
            i = (i + 1) % PasosSensibilidad.Length;
            _camara.Sensitivity = PasosSensibilidad[i];
            PlayerPrefs.SetFloat(ClaveSensibilidad, _camara.Sensitivity);
            Refrescar();
        }

        private int IndiceSensibilidad()
        {
            if (_camara == null) return 2;
            for (int k = 0; k < PasosSensibilidad.Length; k++)
                if (Mathf.Abs(PasosSensibilidad[k] - _camara.Sensitivity) < 0.001f) return k;
            return 2;
        }

        /// <summary>
        /// Conmuta la sincronización solar. Puede no existir el control --- por ejemplo si la
        /// escena no tiene luz direccional ---, y en ese caso la fila lo indica en lugar de fallar.
        /// </summary>
        private void AlternarSolar()
        {
            var ctrl = DigitalTwin.Visual.SolarLightingController.Instancia;
            if (ctrl == null) return;
            ctrl.Alternar();
            Refrescar();
        }

        private void AlternarPantalla()
        {
            _pantallaCompleta = !_pantallaCompleta;
            Screen.fullScreen = _pantallaCompleta;
            _framesParaResincronizarPantalla = 20;
            Refrescar();
        }

        private void PulsarSalir()
        {
            if (!_confirmandoSalida)
            {
                _confirmandoSalida = true;
                Refrescar();
                return;
            }
            Salir();
        }

        private void CancelarConfirmacion()
        {
            if (!_confirmandoSalida) return;
            _confirmandoSalida = false;
            Refrescar();
        }

        private void Refrescar()
        {
            if (_valorSensibilidad != null)
                _valorSensibilidad.text = NombresSensibilidad[IndiceSensibilidad()] + "   ▸";

            if (_valorPantalla != null)
                _valorPantalla.text = (_pantallaCompleta ? "pantalla completa" : "ventana") + "   ▸";

            if (_valorSolar != null)
            {
                var ctrl = DigitalTwin.Visual.SolarLightingController.Instancia;
                _valorSolar.text = ctrl == null ? "no disponible" : ctrl.Descripcion + "   ▸";
            }

            if (_textoSalir != null)
                _textoSalir.text = _confirmandoSalida
                    ? "¿Seguro?  Pulsa otra vez para salir"
                    : "Salir de la aplicación";

            if (_fondoSalir != null)
                _fondoSalir.color = _confirmandoSalida
                    ? new Color(0.62f, 0.16f, 0.16f, 1f)
                    : new Color(0.30f, 0.13f, 0.13f, 1f);
        }

        private void Salir()
        {
            PlayerPrefs.Save();
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#else
            Application.Quit();
#endif
        }
    }
}
