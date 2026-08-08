using System;
using System.Collections.Generic;
using DigitalTwin.UI;
using IFCImporter;
using UnityEngine;
using UnityEngine.UI;

namespace DigitalTwin.Metadata
{
    /// <summary>
    /// Panel lateral de metadatos (Fase 2). Al seleccionar cualquier elemento del modelo
    /// muestra name/type/tag/ruta jerárquica y todos sus Psets, con cada grupo desplegable
    /// con un clic. Construido enteramente por código (ver RuntimeUIFactory) con posicionado
    /// manual en vez de VerticalLayoutGroup/ContentSizeFitter: al no poder abrir el Editor
    /// para verificar visualmente el resultado, se ha preferido matemática de layout
    /// determinista y fácil de razonar sobre el comportamiento del sistema de layout de uGUI.
    ///
    /// Punto de extensión para la Fase 4: <see cref="SensorSectionBuilder"/> permite inyectar,
    /// sin tocar esta clase, una sección extra al principio del panel para los sensores IoT
    /// (EQE...) con sus valores en tiempo real.
    /// </summary>
    public class MetadataPanelController : MonoBehaviour
    {
        private const float PanelWidth = 440f;
        private const float HeaderHeight = 150f;
        private const float RowHeight = 30f;
        private const float PropRowHeight = 22f;
        private const float Indent = 18f;
        private const float Padding = 16f;

        /// <summary>
        /// Fase 4: si se asigna, se llama cada vez que se muestra un elemento, justo antes de
        /// los Psets. Recibe los metadatos del elemento y un contenedor ya posicionado y con el
        /// ancho del panel; debe devolver la altura en píxeles que ha ocupado (0 si no ha
        /// añadido nada, p.ej. porque el elemento no es un sensor).
        /// </summary>
        public Func<IfcMetadata, RectTransform, float> SensorSectionBuilder;

        private RectTransform _panelRoot;
        private RectTransform _viewport;
        private RectTransform _content;
        private ManualScrollArea _scrollArea;
        private Text _titleText;
        private Text _subtitleText;
        private Text _pathText;
        private IfcMetadata _current;
        private readonly HashSet<int> _expandedIndices = new HashSet<int>();

        public IfcMetadata Current => _current;
        public event Action<IfcMetadata> OnElementShown;
        public event Action OnPanelHidden;

        private Image _fondo;

        public void Initialize(Canvas canvas)
        {
            BuildPanel(canvas.transform);
            Hide();
        }

        /// <summary>
        /// Ajusta la opacidad del fondo del panel dejando el texto intacto.
        ///
        /// Se expone como método y no como un simple color configurable porque la transparencia
        /// aquí tiene un compromiso que conviene entender: un panel translúcido da sensación de
        /// espacio y evita la claustrofobia de tener una ficha opaca ocupando media vista, que es
        /// justo lo que se busca en Realidad Mixta. Pero en un visor con passthrough el fondo real
        /// puede ser cualquier cosa (una pared con muebles, un cuadro), y si el texto también se
        /// vuelve translúcido se pierde legibilidad justo cuando más importa.
        ///
        /// Por eso solo se toca el alfa del fondo: los textos y los valores de sensor siguen a
        /// opacidad completa. Un valor en torno a 0,7 funciona bien; por debajo de 0,5 el texto
        /// empieza a competir con lo que hay detrás.
        /// </summary>
        public void SetOpacidadFondo(float alfa)
        {
            if (_fondo == null) return;
            Color c = _fondo.color;
            c.a = Mathf.Clamp01(alfa);
            _fondo.color = c;
        }

        private void BuildPanel(Transform canvasTransform)
        {
            _panelRoot = RuntimeUIFactory.CreateRect(canvasTransform, "MetadataPanel");
            _panelRoot.anchorMin = new Vector2(1, 0);
            _panelRoot.anchorMax = new Vector2(1, 1);
            _panelRoot.pivot = new Vector2(1, 0.5f);
            _panelRoot.anchoredPosition = Vector2.zero;
            _panelRoot.sizeDelta = new Vector2(PanelWidth, 0);

            _fondo = RuntimeUIFactory.CreatePanel(_panelRoot, "Background", new Color(0.05f, 0.06f, 0.08f, 0.92f));
            var bgRect = (RectTransform)_panelRoot.Find("Background");
            RuntimeUIFactory.StretchToParent(bgRect);

            // Botón cerrar
            var closeRect = RuntimeUIFactory.CreateRect(_panelRoot, "CloseButton");
            closeRect.anchorMin = closeRect.anchorMax = new Vector2(1, 1);
            closeRect.pivot = new Vector2(1, 1);
            closeRect.anchoredPosition = new Vector2(-Padding, -Padding);
            closeRect.sizeDelta = new Vector2(28, 28);
            var closeImg = RuntimeUIFactory.CreatePanel(closeRect, "Bg", new Color(1, 1, 1, 0.08f));
            RuntimeUIFactory.StretchToParent((RectTransform)closeImg.transform);
            var closeLabel = RuntimeUIFactory.CreateText(closeRect, "X", "✕", 18, TextAnchor.MiddleCenter, Color.white);
            RuntimeUIFactory.StretchToParent((RectTransform)closeLabel.transform);
            ClickRouter.Instance.Register(closeRect, Hide, sortOrder: 20, isActive: () => _panelRoot.gameObject.activeSelf);

            // Cabecera
            var titleRect = RuntimeUIFactory.CreateRect(_panelRoot, "Title");
            titleRect.anchorMin = new Vector2(0, 1);
            titleRect.anchorMax = new Vector2(1, 1);
            titleRect.pivot = new Vector2(0, 1);
            titleRect.anchoredPosition = new Vector2(Padding, -Padding);
            titleRect.sizeDelta = new Vector2(-Padding * 2 - 34, 30);
            _titleText = RuntimeUIFactory.CreateText(titleRect, "Text", "", 22, TextAnchor.UpperLeft, Color.white, FontStyle.Bold);
            RuntimeUIFactory.StretchToParent((RectTransform)_titleText.transform);

            var subtitleRect = RuntimeUIFactory.CreateRect(_panelRoot, "Subtitle");
            subtitleRect.anchorMin = new Vector2(0, 1);
            subtitleRect.anchorMax = new Vector2(1, 1);
            subtitleRect.pivot = new Vector2(0, 1);
            subtitleRect.anchoredPosition = new Vector2(Padding, -56);
            subtitleRect.sizeDelta = new Vector2(-Padding * 2, 22);
            _subtitleText = RuntimeUIFactory.CreateText(subtitleRect, "Text", "", 15, TextAnchor.UpperLeft, new Color(0.75f, 0.85f, 1f, 1f));
            RuntimeUIFactory.StretchToParent((RectTransform)_subtitleText.transform);

            var pathRect = RuntimeUIFactory.CreateRect(_panelRoot, "Path");
            pathRect.anchorMin = new Vector2(0, 1);
            pathRect.anchorMax = new Vector2(1, 1);
            pathRect.pivot = new Vector2(0, 1);
            pathRect.anchoredPosition = new Vector2(Padding, -82);
            pathRect.sizeDelta = new Vector2(-Padding * 2, 56);
            _pathText = RuntimeUIFactory.CreateText(pathRect, "Text", "", 12, TextAnchor.UpperLeft, new Color(0.65f, 0.65f, 0.68f, 1f));
            RuntimeUIFactory.StretchToParent((RectTransform)_pathText.transform);

            // Viewport con recorte (RectMask2D) + contenido con scroll manual
            _viewport = RuntimeUIFactory.CreateRect(_panelRoot, "Viewport");
            _viewport.anchorMin = new Vector2(0, 0);
            _viewport.anchorMax = new Vector2(1, 1);
            _viewport.pivot = new Vector2(0.5f, 1f);
            _viewport.offsetMax = new Vector2(-Padding, -HeaderHeight);
            _viewport.offsetMin = new Vector2(Padding, Padding);
            _viewport.gameObject.AddComponent<RectMask2D>();

            _content = RuntimeUIFactory.CreateRect(_viewport, "Content");
            _content.anchorMin = new Vector2(0, 1);
            _content.anchorMax = new Vector2(1, 1);
            _content.pivot = new Vector2(0, 1);
            _content.anchoredPosition = Vector2.zero;
            _content.sizeDelta = new Vector2(0, 0);

            _scrollArea = _viewport.gameObject.AddComponent<ManualScrollArea>();
            _scrollArea.Viewport = _viewport;
            _scrollArea.Content = _content;
        }

        public void Show(IfcMetadata meta)
        {
            if (meta == null) return;
            if (_current != meta) _expandedIndices.Clear();
            _current = meta;
            _panelRoot.gameObject.SetActive(true);

            _titleText.text = string.IsNullOrEmpty(meta.ifcName) ? "(sin nombre)" : meta.ifcName;
            _subtitleText.text = $"{meta.ifcType}  ·  Tag {meta.ifcTag}  ·  GlobalId {meta.globalId}";
            _pathText.text = meta.hierarchyPath;

            RebuildContent(meta);
            _scrollArea.ResetScroll();
            OnElementShown?.Invoke(meta);
        }

        public void Hide()
        {
            _current = null;
            if (_panelRoot != null) _panelRoot.gameObject.SetActive(false);
            OnPanelHidden?.Invoke();
        }

        /// <summary>Reconstruye el contenido si el panel está mostrando este elemento (usado por
        /// la Fase 4 cuando llega un valor nuevo de sensor y hay que refrescar la sección IoT sin
        /// esperar a que el usuario vuelva a clicar).</summary>
        public void RefreshIfShowing(IfcMetadata meta)
        {
            if (_current == meta && _panelRoot.gameObject.activeSelf) RebuildContent(meta);
        }

        private void ClearContent()
        {
            for (int i = _content.childCount - 1; i >= 0; i--)
                Destroy(_content.GetChild(i).gameObject);
        }

        private void RebuildContent(IfcMetadata meta)
        {
            ClearContent();
            float y = 0f;

            if (SensorSectionBuilder != null)
            {
                var sensorContainer = RuntimeUIFactory.CreateRect(_content, "SensorSection");
                sensorContainer.anchorMin = new Vector2(0, 1);
                sensorContainer.anchorMax = new Vector2(1, 1);
                sensorContainer.pivot = new Vector2(0, 1);
                sensorContainer.anchoredPosition = new Vector2(0, -y);
                sensorContainer.sizeDelta = new Vector2(0, 0);

                float used = Mathf.Max(0f, SensorSectionBuilder(meta, sensorContainer));
                sensorContainer.sizeDelta = new Vector2(0, used);
                if (used > 0f) y += used + 10f;
            }

            if (meta.propertySets == null || meta.propertySets.Count == 0)
            {
                var emptyText = RuntimeUIFactory.CreateText(_content, "Empty", "Este elemento no tiene Psets asociados.", 14, TextAnchor.UpperLeft, new Color(0.7f, 0.7f, 0.7f, 1f));
                var emptyRect = (RectTransform)emptyText.transform;
                emptyRect.anchorMin = new Vector2(0, 1);
                emptyRect.anchorMax = new Vector2(1, 1);
                emptyRect.pivot = new Vector2(0, 1);
                emptyRect.anchoredPosition = new Vector2(0, -y);
                emptyRect.sizeDelta = new Vector2(0, 24);
                y += 24f;
            }
            else
            {
                for (int i = 0; i < meta.propertySets.Count; i++)
                    y = BuildPsetRow(i, meta.propertySets[i], y);
            }

            _content.sizeDelta = new Vector2(0, y + Padding);
            _scrollArea.ClampNow();
        }

        private float BuildPsetRow(int index, PsetGroup group, float y)
        {
            bool expanded = _expandedIndices.Contains(index);

            var header = RuntimeUIFactory.CreateRect(_content, $"PsetHeader_{index}");
            header.anchorMin = new Vector2(0, 1);
            header.anchorMax = new Vector2(1, 1);
            header.pivot = new Vector2(0, 1);
            header.anchoredPosition = new Vector2(0, -y);
            header.sizeDelta = new Vector2(0, RowHeight);

            var headerBg = RuntimeUIFactory.CreatePanel(header, "Bg", new Color(1, 1, 1, 0.06f));
            RuntimeUIFactory.StretchToParent((RectTransform)headerBg.transform);

            var chevronRect = RuntimeUIFactory.CreateRect(header, "Chevron");
            chevronRect.anchorMin = new Vector2(0, 0);
            chevronRect.anchorMax = new Vector2(0, 1);
            chevronRect.pivot = new Vector2(0, 0.5f);
            chevronRect.anchoredPosition = new Vector2(6, 0);
            chevronRect.sizeDelta = new Vector2(20, 0);
            RuntimeUIFactory.CreateText(chevronRect, "Text", expanded ? "▾" : "▸", 14, TextAnchor.MiddleCenter, Color.white);
            RuntimeUIFactory.StretchToParent((RectTransform)chevronRect.Find("Text"));

            var titleRect = RuntimeUIFactory.CreateRect(header, "Title");
            titleRect.anchorMin = new Vector2(0, 0);
            titleRect.anchorMax = new Vector2(1, 1);
            titleRect.pivot = new Vector2(0, 0.5f);
            titleRect.anchoredPosition = new Vector2(28, 0);
            titleRect.sizeDelta = new Vector2(-100, 0);
            var titleText = RuntimeUIFactory.CreateText(titleRect, "Text", group.psetName, 15, TextAnchor.MiddleLeft, Color.white, FontStyle.Bold);
            RuntimeUIFactory.StretchToParent((RectTransform)titleText.transform);

            var countRect = RuntimeUIFactory.CreateRect(header, "Count");
            countRect.anchorMin = new Vector2(1, 0);
            countRect.anchorMax = new Vector2(1, 1);
            countRect.pivot = new Vector2(1, 0.5f);
            countRect.anchoredPosition = new Vector2(-10, 0);
            countRect.sizeDelta = new Vector2(60, 0);
            var countText = RuntimeUIFactory.CreateText(countRect, "Text", $"{group.properties.Count}", 13, TextAnchor.MiddleRight, new Color(0.7f, 0.7f, 0.7f, 1f));
            RuntimeUIFactory.StretchToParent((RectTransform)countText.transform);

            ClickRouter.Instance.Register(header, () => ToggleFoldout(index), sortOrder: 15,
                isActive: () => header != null && header.gameObject.activeInHierarchy);

            y += RowHeight;

            if (expanded)
            {
                foreach (var prop in group.properties)
                {
                    var row = RuntimeUIFactory.CreateRect(_content, "Prop");
                    row.anchorMin = new Vector2(0, 1);
                    row.anchorMax = new Vector2(1, 1);
                    row.pivot = new Vector2(0, 1);
                    row.anchoredPosition = new Vector2(0, -y);
                    row.sizeDelta = new Vector2(0, PropRowHeight);

                    var keyRect = RuntimeUIFactory.CreateRect(row, "Key");
                    keyRect.anchorMin = new Vector2(0, 0);
                    keyRect.anchorMax = new Vector2(0.5f, 1);
                    keyRect.pivot = new Vector2(0, 0.5f);
                    keyRect.anchoredPosition = new Vector2(Indent, 0);
                    keyRect.sizeDelta = new Vector2(-Indent, 0);
                    var keyText = RuntimeUIFactory.CreateText(keyRect, "Text", prop.key, 12, TextAnchor.MiddleLeft, new Color(0.68f, 0.78f, 0.95f, 1f));
                    RuntimeUIFactory.StretchToParent((RectTransform)keyText.transform);

                    var valRect = RuntimeUIFactory.CreateRect(row, "Value");
                    valRect.anchorMin = new Vector2(0.5f, 0);
                    valRect.anchorMax = new Vector2(1f, 1);
                    valRect.pivot = new Vector2(0, 0.5f);
                    valRect.anchoredPosition = new Vector2(4, 0);
                    valRect.sizeDelta = new Vector2(-4, 0);
                    var valText = RuntimeUIFactory.CreateText(valRect, "Text", prop.value ?? "—", 12, TextAnchor.MiddleLeft, Color.white);
                    RuntimeUIFactory.StretchToParent((RectTransform)valText.transform);

                    y += PropRowHeight;
                }
            }

            return y;
        }

        private void ToggleFoldout(int index)
        {
            if (!_expandedIndices.Add(index)) _expandedIndices.Remove(index);
            if (_current != null) RebuildContent(_current);
        }
    }
}
