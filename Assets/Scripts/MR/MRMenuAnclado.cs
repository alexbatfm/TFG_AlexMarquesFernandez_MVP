using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace DigitalTwin.MR
{
    /// <summary>
    /// El MENÚ del modo anclado (ronda 9). Hasta ahora este modo no tenía menú: recolocar el
    /// anclaje exigía tener el panel de colocación abierto, y cambiar de modo exigía reiniciar
    /// la aplicación — en el visor un reinicio es caro y en una demostración, inaceptable.
    ///
    /// MISMA FORMA Y MISMO GESTO que el menú del modo de navegación (<see cref="MRMenuZonas"/>):
    /// se abre y cierra con el botón primario (A/X; tecla M en el respaldo del Editor), se
    /// coloca de frente al usuario a un metro, filas señalables con el rayo y gatillo para
    /// elegir, con el material de interfaz siempre visible. La intención es que el usuario
    /// aprenda UN solo menú, no dos. No comparte clase con el de navegación porque no comparte
    /// contenido (aquí no hay zonas ni navegador: el desplazamiento es físico) y porque el modo
    /// de navegación está verificado y no debe refactorizarse.
    ///
    /// Contenido: reabrir el panel de anclaje (desde la ronda 9 A/X ya no lo abre directamente:
    /// el botón pertenece al menú y el panel se oculta con A/X cuando está abierto), rehacer el
    /// anclaje (olvidar el guardado y volver al registro por puntos) y volver al selector de
    /// modo (ver <see cref="MRDigitalTwinBootstrap.VolverAlSelector"/>).
    ///
    /// COORDINACIÓN CON EL PANEL DE COLOCACIÓN: mientras el panel captura el rayo, este menú
    /// no responde al botón (el panel lo usa para ocultarse), y además comprueba
    /// <see cref="MRColocacionAnclaje.FotogramaBotonConsumido"/> para no abrirse con la misma
    /// pulsación que cerró el panel — el orden de ejecución entre componentes no está
    /// garantizado y sin esa marca la pulsación haría las dos cosas a la vez.
    ///
    /// CONFIRMACIÓN DE LA ACCIÓN IRREVERSIBLE (17-08, tras la ronda 9). «Volver al selector de
    /// modo» desmonta la sesión y recarga la escena; en este modo, además, abandona el
    /// registro que el usuario acabe de hacer. Una pulsación accidental del gatillo no puede
    /// costar eso. Se reproduce el patrón que el menú de escritorio ya aplica a su acción
    /// irreversible (<see cref="DigitalTwin.Core.UI.SettingsMenuController"/>, «Salir de la
    /// aplicación»; guía, cap. 5, «Confirmación de la acción irreversible»): confirmación en
    /// DOS PASOS SOBRE LA PROPIA FILA, sin diálogo aparte. El primer gatillo no hace nada
    /// salvo cambiar el texto de la fila («¿Seguro? Pulsa otra vez para volver») y subir la
    /// intensidad de su color; el segundo ejecuta. Cancelan: el gatillo sobre cualquier otra
    /// fila (que además la ejecuta, como en escritorio), el gatillo fuera de las filas
    /// (equivalente al clic fuera del panel), el botón primario A/X (equivalente a Escape:
    /// cancela y deja el menú abierto) y cualquier cierre del menú (p. ej. el panel de
    /// colocación reclamando el rayo). Mientras dura, el pie de leyenda dice QUÉ se pierde
    /// —calculado con el estado real del registro, ver
    /// <see cref="MRColocacionAnclaje.AvisoAlAbandonarElModo"/>— y cómo cancelar. Ver
    /// <see cref="PulsarSelector"/>.
    /// </summary>
    public class MRMenuAnclado : MonoBehaviour
    {
        // Mismas medidas que MRMenuZonas, a propósito: la forma es parte del contrato de
        // aprendizaje («un solo menú»). Si se cambian allí, cambiarlas aquí.
        private const float AnchoPx = 460f;
        private const float AltoCabeceraPx = 56f;
        private const float AltoFilaPx = 46f;
        private const float AltoSeparadorPx = 10f;
        // El pie tiene sitio para DOS líneas: durante la confirmación de la vuelta al selector
        // dice qué se pierde y cómo cancelar (misma medida en MRMenuZonas).
        private const float AltoPiePx = 48f;
        private const float MargenPx = 10f;
        private const float AnchoMetros = 0.42f;
        private const float DistanciaAlAbrir = 1.0f;

        private static readonly Color ColorFilaNormal = new Color(1f, 1f, 1f, 0.04f);
        private static readonly Color ColorFilaSenalada = new Color(1f, 0.82f, 0.2f, 0.30f);
        private static readonly Color ColorTextoNormal = new Color(0.85f, 0.88f, 0.93f, 1f);
        private static readonly Color ColorFilaSelector = new Color(0.55f, 0.80f, 1f, 0.10f);
        // Confirmación pendiente: la misma familia cálida del señalado, más intensa —cambio de
        // intensidad y no de tono, como en escritorio (rojo apagado -> rojo intenso)—, con dos
        // grados según el rayo la señale o no, para que el estado se vea sin leer.
        private static readonly Color ColorFilaConfirmando = new Color(1f, 0.55f, 0.15f, 0.40f);
        private static readonly Color ColorFilaConfirmandoSenalada = new Color(1f, 0.55f, 0.15f, 0.70f);
        private static readonly Color ColorPie = new Color(0.65f, 0.68f, 0.73f, 1f);
        private static readonly Color ColorPieAviso = new Color(1f, 0.85f, 0.55f, 1f);

        private const string TextoSelector = "Volver al selector de modo";
        private const string TextoSelectorConfirmando = "¿Seguro? Pulsa otra vez para volver";
        private const string PieNormal = "Gatillo: elegir  ·  A o X: cerrar";
        private const string PieCancelar = "Gatillo fuera de las filas o A o X: cancelar";

        public bool Abierto { get; private set; }

        private MRControllerRig _rig;
        private Camera _camara;
        private MRColocacionAnclaje _colocacion;

        private class Fila
        {
            public string Id;
            public BoxCollider Volumen;
            public Image Fondo;
            public Text Texto;
            public Color ColorBase;
        }

        private RectTransform _raiz;
        private readonly List<Fila> _filas = new List<Fila>();
        private int _filaSenalada = -1;
        private Text _pie;

        // Confirmación en dos pasos de la vuelta al selector (ver cabecera de la clase).
        private bool _confirmandoSelector;

        public void Initialize(MRControllerRig rig, Camera camara, MRColocacionAnclaje colocacion)
        {
            _rig = rig;
            _camara = camara;
            _colocacion = colocacion;

            Construir();
            Debug.LogWarning("[DigitalTwin][AR] Menu del modo anclado listo (panel de anclaje, " +
                             "rehacer el anclaje, volver al selector). Se abre y cierra con el " +
                             "boton primario del mando (tecla M en el respaldo del Editor).");
        }

        private void Construir()
        {
            float altoPx = AltoCabeceraPx
                         + 2 * AltoFilaPx                    // panel de anclaje, rehacer
                         + AltoSeparadorPx + AltoFilaPx      // volver al selector
                         + AltoPiePx + MargenPx * 2f;
            var canvas = DigitalTwin.UI.RuntimeUIFactory.CreateWorldCanvas(
                "~MenuAncladoAR", anchoPx: AnchoPx, altoPx: altoPx, anchoMetros: AnchoMetros);
            _raiz = (RectTransform)canvas.transform;
            _raiz.SetParent(transform, true);

            // Material sin prueba de profundidad: en anclado los oclusores invisibles escriben
            // profundidad, y un menu a un metro del usuario no debe quedar recortado por un
            // muro que ni siquiera se ve.
            var material = MRIndicadoresDestino.MaterialSiempreVisible();

            // Misma opacidad que la ficha de activos y que el menú de zonas: ver
            // MROpacidadInterfaz. Los dos menús se abren en la misma sesión y una diferencia
            // de dos centésimas es visible sobre un vídeo de cámaras claro.
            var fondo = DigitalTwin.UI.RuntimeUIFactory.CreatePanel(_raiz, "Fondo",
                MROpacidadInterfaz.ColorDeFondo);
            DigitalTwin.UI.RuntimeUIFactory.StretchToParent((RectTransform)fondo.transform);
            if (material != null) fondo.material = material;

            var cabecera = DigitalTwin.UI.RuntimeUIFactory.CreateText(_raiz, "Cabecera",
                "Menú", 24, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            var rtCab = (RectTransform)cabecera.transform;
            rtCab.anchorMin = rtCab.anchorMax = new Vector2(0.5f, 1f);
            rtCab.pivot = new Vector2(0.5f, 1f);
            rtCab.anchoredPosition = new Vector2(0f, -MargenPx);
            rtCab.sizeDelta = new Vector2(AnchoPx - MargenPx * 2f, AltoCabeceraPx - MargenPx);
            if (material != null) cabecera.material = material;

            float y = AltoCabeceraPx + MargenPx;
            y = CrearFila("panel", "Panel de anclaje", ColorFilaNormal, y, material);
            y = CrearFila("rehacer", "Rehacer el anclaje", ColorFilaNormal, y, material);
            y += AltoSeparadorPx;
            y = CrearFila("selector", TextoSelector, ColorFilaSelector, y, material);

            _pie = DigitalTwin.UI.RuntimeUIFactory.CreateText(_raiz, "Pie", PieNormal,
                16, TextAnchor.MiddleCenter, ColorPie);
            var rtPie = (RectTransform)_pie.transform;
            rtPie.anchorMin = rtPie.anchorMax = new Vector2(0.5f, 1f);
            rtPie.pivot = new Vector2(0.5f, 1f);
            rtPie.anchoredPosition = new Vector2(0f, -y);
            rtPie.sizeDelta = new Vector2(AnchoPx - MargenPx * 2f, AltoPiePx);
            if (material != null) _pie.material = material;

            _raiz.gameObject.SetActive(false);
        }

        // ==================================================================================
        //  Confirmación en dos pasos de la vuelta al selector (patrón del menú de escritorio)
        // ==================================================================================

        /// <summary>Primer gatillo sobre la fila: solo avisa. Segundo: ejecuta.</summary>
        private void PulsarSelector()
        {
            if (!_confirmandoSelector)
            {
                _confirmandoSelector = true;
                RefrescarConfirmacion();
                Debug.LogWarning("[DigitalTwin][AR] Menu anclado: vuelta al selector PENDIENTE " +
                                 "DE CONFIRMACION (segundo gatillo sobre la fila para ejecutar; " +
                                 "gatillo fuera, A/X u otra fila cancelan). Aviso: '" +
                                 (_pie != null ? _pie.text.Replace('\n', ' ') : "?") + "'.");
                return;
            }
            _confirmandoSelector = false;   // confirmado: no es una cancelacion, no se traza como tal
            RefrescarConfirmacion();
            Cerrar();
            MRDigitalTwinBootstrap.VolverAlSelector(
                "fila 'Volver al selector de modo' del menu del modo anclado, confirmada");
        }

        private void CancelarConfirmacion(string motivo)
        {
            if (!_confirmandoSelector) return;
            _confirmandoSelector = false;
            RefrescarConfirmacion();
            Debug.LogWarning($"[DigitalTwin][AR] Menu anclado: vuelta al selector CANCELADA ({motivo}).");
        }

        /// <summary>Texto de la fila y del pie según haya o no confirmación pendiente. Los
        /// colores los pinta Update cada fotograma. En el pie, la primera línea es el aviso de
        /// pérdida calculado con el estado real del registro (no una frase fija), y la segunda,
        /// cómo cancelar.</summary>
        private void RefrescarConfirmacion()
        {
            var filaSelector = _filas.Find(f => f.Id == "selector");
            if (filaSelector != null && filaSelector.Texto != null)
                filaSelector.Texto.text = _confirmandoSelector ? TextoSelectorConfirmando : TextoSelector;

            if (_pie == null) return;
            if (!_confirmandoSelector)
            {
                _pie.text = PieNormal;
                _pie.fontSize = 16;
                _pie.color = ColorPie;
                return;
            }
            string aviso = _colocacion != null
                ? _colocacion.AvisoAlAbandonarElModo()
                : "Se pierde el registro no guardado de esta sesion";
            _pie.text = aviso + "\n" + PieCancelar;
            _pie.fontSize = 14;
            _pie.color = ColorPieAviso;
        }

        private float CrearFila(string id, string texto, Color colorBase, float y, Material material)
        {
            var filaRect = DigitalTwin.UI.RuntimeUIFactory.CreateRect(_raiz, "Fila_" + id);
            filaRect.anchorMin = filaRect.anchorMax = new Vector2(0.5f, 1f);
            filaRect.pivot = new Vector2(0.5f, 1f);
            filaRect.anchoredPosition = new Vector2(0f, -y);
            filaRect.sizeDelta = new Vector2(AnchoPx - MargenPx * 2f, AltoFilaPx);

            var fondoFila = DigitalTwin.UI.RuntimeUIFactory.CreatePanel(filaRect, "Fondo", colorBase);
            DigitalTwin.UI.RuntimeUIFactory.StretchToParent((RectTransform)fondoFila.transform);
            if (material != null) fondoFila.material = material;

            var textoFila = DigitalTwin.UI.RuntimeUIFactory.CreateText(filaRect, "Texto", texto,
                20, TextAnchor.MiddleLeft, ColorTextoNormal);
            var rtTexto = (RectTransform)textoFila.transform;
            DigitalTwin.UI.RuntimeUIFactory.StretchToParent(rtTexto);
            rtTexto.offsetMin = new Vector2(18f, 0f);
            rtTexto.offsetMax = new Vector2(-10f, 0f);
            if (material != null) textoFila.material = material;

            var volumen = filaRect.gameObject.AddComponent<BoxCollider>();
            volumen.isTrigger = true;
            volumen.size = new Vector3(AnchoPx - MargenPx * 2f, AltoFilaPx, 1f);
            volumen.center = Vector3.zero;

            _filas.Add(new Fila
            {
                Id = id, Volumen = volumen, Fondo = fondoFila, Texto = textoFila, ColorBase = colorBase
            });
            return y + AltoFilaPx;
        }

        private void Update()
        {
            if (_rig == null || _raiz == null) return;

            bool panelCaptura = _colocacion != null && _colocacion.CapturaElRayo;
            bool botonYaConsumido = _colocacion != null &&
                                    _colocacion.FotogramaBotonConsumido == Time.frameCount;

            if (_rig.BotonMenuPulsadoEsteFrame() && !panelCaptura && !botonYaConsumido)
                Alternar();

            if (!Abierto) return;

            if (panelCaptura)
            {
                // El panel de colocacion se ha abierto (p. ej. lo abrio esta misma fila, o un
                // cambio de estado del anclaje): el menu se retira para no disputarle el rayo.
                Cerrar();
                return;
            }

            _filaSenalada = -1;
            float mejorDist = float.MaxValue;
            if (_rig.TryGetRayo(out Ray rayo))
            {
                for (int i = 0; i < _filas.Count; i++)
                {
                    if (_filas[i].Volumen.Raycast(rayo, out RaycastHit hit, 20f) &&
                        hit.distance < mejorDist)
                    {
                        mejorDist = hit.distance;
                        _filaSenalada = i;
                    }
                }
            }

            for (int i = 0; i < _filas.Count; i++)
            {
                bool senalada = i == _filaSenalada;
                Color color;
                if (_confirmandoSelector && _filas[i].Id == "selector")
                    color = senalada ? ColorFilaConfirmandoSenalada : ColorFilaConfirmando;
                else
                    color = senalada ? ColorFilaSenalada : _filas[i].ColorBase;
                _filas[i].Fondo.color = color;
            }

            bool haySenal = _filaSenalada >= 0;
            _rig.MostrarImpacto(haySenal ? mejorDist : 0f, haySenal);

            if (!_rig.GatilloPulsadoEsteFrame()) return;

            if (_filaSenalada < 0)
            {
                // Gatillo fuera de las filas: el equivalente al clic fuera del panel en
                // escritorio. Cancela la confirmacion pendiente (si la hay) y no hace nada mas.
                CancelarConfirmacion("gatillo fuera de las filas");
                return;
            }

            string id = _filas[_filaSenalada].Id;
            if (id == "selector") { PulsarSelector(); return; }

            // Cualquier otra fila cancela la confirmacion Y se ejecuta, como en escritorio.
            CancelarConfirmacion("se ha elegido otra fila: '" + id + "'");
            Cerrar();
            Accionar(id);
        }

        private void Accionar(string id)
        {
            switch (id)
            {
                case "panel":
                    if (_colocacion != null) _colocacion.AbrirPanel();
                    else Debug.LogWarning("[DigitalTwin][AR] Menu anclado: no hay interfaz de " +
                                          "colocacion (arranque sin rig o sin raiz de modelo); " +
                                          "no se puede abrir el panel.");
                    break;
                case "rehacer":
                    if (_colocacion != null) _colocacion.RehacerAnclaje();
                    else Debug.LogWarning("[DigitalTwin][AR] Menu anclado: no hay interfaz de " +
                                          "colocacion; no se puede rehacer el anclaje.");
                    break;
                // "selector" no pasa por aqui: lo gestiona PulsarSelector (dos pasos).
                default:
                    Debug.LogWarning($"[DigitalTwin][AR] Menu anclado: fila desconocida '{id}'.");
                    break;
            }
        }

        private void Alternar()
        {
            if (Abierto)
            {
                // Con una confirmacion pendiente, el boton primario la cancela en vez de cerrar
                // el menu: es la reaccion esperable de quien se ha arrepentido (mismo papel que
                // Escape en el menu de escritorio).
                if (_confirmandoSelector) { CancelarConfirmacion("boton primario A/X"); return; }
                Cerrar();
                return;
            }
            Abrir();
        }

        private void Abrir()
        {
            Vector3 mirada = _camara != null ? _camara.transform.forward : Vector3.forward;
            mirada.y = 0f;
            if (mirada.sqrMagnitude < 0.0001f) mirada = Vector3.forward;
            mirada.Normalize();

            float altoMetros = _raiz.rect.height * _raiz.localScale.y;
            Vector3 origen = _camara != null ? _camara.transform.position : Vector3.zero;
            _raiz.position = origen + mirada * DistanciaAlAbrir
                           + Vector3.up * (-altoMetros * 0.5f + 0.10f);
            _raiz.rotation = Quaternion.LookRotation(mirada, Vector3.up);

            _confirmandoSelector = false;
            RefrescarConfirmacion();
            _raiz.gameObject.SetActive(true);
            Abierto = true;
            Debug.LogWarning("[DigitalTwin][AR] Menu del modo anclado abierto.");
        }

        private void Cerrar()
        {
            // Cerrar por cualquier via (A/X, otra fila, el panel de colocacion reclamando el
            // rayo) descarta la confirmacion pendiente: no puede quedar viva con el menu cerrado.
            if (_confirmandoSelector) CancelarConfirmacion("el menu se cierra");
            _raiz.gameObject.SetActive(false);
            Abierto = false;
            _filaSenalada = -1;
            Debug.LogWarning("[DigitalTwin][AR] Menu del modo anclado cerrado.");
        }
    }
}
