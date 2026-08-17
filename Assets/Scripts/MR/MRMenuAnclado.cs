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
    /// </summary>
    public class MRMenuAnclado : MonoBehaviour
    {
        // Mismas medidas que MRMenuZonas, a propósito: la forma es parte del contrato de
        // aprendizaje («un solo menú»). Si se cambian allí, cambiarlas aquí.
        private const float AnchoPx = 460f;
        private const float AltoCabeceraPx = 56f;
        private const float AltoFilaPx = 46f;
        private const float AltoSeparadorPx = 10f;
        private const float AltoPiePx = 40f;
        private const float MargenPx = 10f;
        private const float AnchoMetros = 0.42f;
        private const float DistanciaAlAbrir = 1.0f;

        private static readonly Color ColorFilaNormal = new Color(1f, 1f, 1f, 0.04f);
        private static readonly Color ColorFilaSenalada = new Color(1f, 0.82f, 0.2f, 0.30f);
        private static readonly Color ColorTextoNormal = new Color(0.85f, 0.88f, 0.93f, 1f);
        private static readonly Color ColorFilaSelector = new Color(0.55f, 0.80f, 1f, 0.10f);

        public bool Abierto { get; private set; }

        private MRControllerRig _rig;
        private Camera _camara;
        private MRColocacionAnclaje _colocacion;

        private class Fila
        {
            public string Id;
            public BoxCollider Volumen;
            public Image Fondo;
            public Color ColorBase;
        }

        private RectTransform _raiz;
        private readonly List<Fila> _filas = new List<Fila>();
        private int _filaSenalada = -1;

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

            var fondo = DigitalTwin.UI.RuntimeUIFactory.CreatePanel(_raiz, "Fondo",
                new Color(0.05f, 0.06f, 0.08f, 0.94f));
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
            y = CrearFila("selector", "Volver al selector de modo", ColorFilaSelector, y, material);

            var pie = DigitalTwin.UI.RuntimeUIFactory.CreateText(_raiz, "Pie",
                "Gatillo: elegir  ·  A o X: cerrar",
                16, TextAnchor.MiddleCenter, new Color(0.65f, 0.68f, 0.73f, 1f));
            var rtPie = (RectTransform)pie.transform;
            rtPie.anchorMin = rtPie.anchorMax = new Vector2(0.5f, 1f);
            rtPie.pivot = new Vector2(0.5f, 1f);
            rtPie.anchoredPosition = new Vector2(0f, -y);
            rtPie.sizeDelta = new Vector2(AnchoPx - MargenPx * 2f, AltoPiePx);
            if (material != null) pie.material = material;

            _raiz.gameObject.SetActive(false);
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

            _filas.Add(new Fila { Id = id, Volumen = volumen, Fondo = fondoFila, ColorBase = colorBase });
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
                _filas[i].Fondo.color = i == _filaSenalada ? ColorFilaSenalada
                                                           : _filas[i].ColorBase;

            bool haySenal = _filaSenalada >= 0;
            _rig.MostrarImpacto(haySenal ? mejorDist : 0f, haySenal);

            if (!_rig.GatilloPulsadoEsteFrame() || _filaSenalada < 0) return;

            string id = _filas[_filaSenalada].Id;
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
                case "selector":
                    MRDigitalTwinBootstrap.VolverAlSelector(
                        "fila 'Volver al selector de modo' del menu del modo anclado");
                    break;
                default:
                    Debug.LogWarning($"[DigitalTwin][AR] Menu anclado: fila desconocida '{id}'.");
                    break;
            }
        }

        private void Alternar()
        {
            if (Abierto) { Cerrar(); return; }
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

            _raiz.gameObject.SetActive(true);
            Abierto = true;
            Debug.LogWarning("[DigitalTwin][AR] Menu del modo anclado abierto.");
        }

        private void Cerrar()
        {
            _raiz.gameObject.SetActive(false);
            Abierto = false;
            _filaSenalada = -1;
            Debug.LogWarning("[DigitalTwin][AR] Menu del modo anclado cerrado.");
        }
    }
}
