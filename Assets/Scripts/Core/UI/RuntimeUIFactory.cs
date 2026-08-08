using UnityEngine;
using UnityEngine.UI;

namespace DigitalTwin.UI
{
    /// <summary>
    /// Construye la UI del gemelo digital enteramente por código (Canvas, paneles, texto,
    /// scroll manual). No se usan prefabs ni TextMeshPro a propósito: TMP necesita importar
    /// "TMP Essential Resources" desde el Editor la primera vez, un paso manual que no se
    /// puede automatizar sin abrir Unity. Usando UnityEngine.UI.Text con la fuente interna
    /// del motor (LegacyRuntime.ttf) el proyecto funciona nada más pulsar Play, sin pasos
    /// de configuración previos.
    /// </summary>
    public static class RuntimeUIFactory
    {
        private static Font _font;
        public static Font DefaultFont => _font ??= Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf");

        private static Sprite _circleSprite;
        private static Sprite _ringSprite;

        public static Canvas CreateRootCanvas(string name, int sortOrder = 0)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(CanvasScaler), typeof(GraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.ScreenSpaceOverlay;
            canvas.sortingOrder = sortOrder;

            var scaler = go.GetComponent<CanvasScaler>();
            scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
            scaler.referenceResolution = new Vector2(1920, 1080);
            scaler.matchWidthOrHeight = 0.5f;

            Object.DontDestroyOnLoad(go);
            return canvas;
        }

        /// <summary>
        /// Canvas flotante en el espacio 3D, para Realidad Mixta.
        ///
        /// En un visor no existe "la pantalla": un canvas en modo ScreenSpaceOverlay se pega a
        /// la cara del usuario y resulta incómodo e irreal. Lo natural es un panel que ocupa un
        /// sitio en el espacio, junto al elemento del que informa.
        ///
        /// Se construye con una resolución alta en píxeles (<paramref name="anchoPx"/>) que
        /// luego se escala a metros: así el texto se define con el mismo tamaño de fuente que en
        /// escritorio y sigue viéndose nítido, en vez de tener que reescribir todo el layout con
        /// medidas físicas.
        /// </summary>
        public static Canvas CreateWorldCanvas(string name, float anchoPx = 900f, float altoPx = 1100f,
                                               float anchoMetros = 0.55f)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(Canvas), typeof(GraphicRaycaster));
            var canvas = go.GetComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;

            var rt = (RectTransform)go.transform;
            rt.sizeDelta = new Vector2(anchoPx, altoPx);

            // Escala uniforme para pasar de píxeles de layout a metros del mundo real.
            float escala = anchoMetros / anchoPx;
            rt.localScale = new Vector3(escala, escala, escala);

            Object.DontDestroyOnLoad(go);
            return canvas;
        }

        public static RectTransform CreateRect(Transform parent, string name)
        {
            var go = new GameObject(name, typeof(RectTransform));
            var rt = go.GetComponent<RectTransform>();
            rt.SetParent(parent, false);
            return rt;
        }

        public static Image CreatePanel(Transform parent, string name, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.color = color;
            img.raycastTarget = false; // no usamos GraphicRaycaster; evita coste innecesario
            return img;
        }

        public static Image CreateIcon(Transform parent, string name, Sprite sprite, Color color)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Image));
            go.transform.SetParent(parent, false);
            var img = go.GetComponent<Image>();
            img.sprite = sprite;
            img.color = color;
            img.raycastTarget = false;
            if (sprite != null) img.type = Image.Type.Simple;
            return img;
        }

        public static Text CreateText(Transform parent, string name, string content, int fontSize, TextAnchor anchor, Color color, FontStyle style = FontStyle.Normal)
        {
            var go = new GameObject(name, typeof(RectTransform), typeof(CanvasRenderer), typeof(Text));
            go.transform.SetParent(parent, false);
            var txt = go.GetComponent<Text>();
            txt.font = DefaultFont;
            txt.text = content;
            txt.fontSize = fontSize;
            txt.alignment = anchor;
            txt.color = color;
            txt.fontStyle = style;
            txt.horizontalOverflow = HorizontalWrapMode.Wrap;
            txt.verticalOverflow = VerticalWrapMode.Overflow;
            txt.raycastTarget = false;
            return txt;
        }

        /// <summary>Círculo relleno, generado en memoria (sin depender de assets importados).</summary>
        public static Sprite CircleSprite()
        {
            if (_circleSprite != null) return _circleSprite;
            _circleSprite = BuildCircle(64, filled: true);
            return _circleSprite;
        }

        /// <summary>Anillo (círculo hueco), usado como marco del hotspot.</summary>
        public static Sprite RingSprite()
        {
            if (_ringSprite != null) return _ringSprite;
            _ringSprite = BuildCircle(64, filled: false);
            return _ringSprite;
        }

        private static Sprite BuildCircle(int size, bool filled)
        {
            var tex = new Texture2D(size, size, TextureFormat.RGBA32, false)
            {
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
                name = filled ? "DigitalTwin_Circle" : "DigitalTwin_Ring"
            };

            float r = size * 0.5f;
            float ringThickness = size * 0.14f;
            var pixels = new Color32[size * size];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    float dx = x + 0.5f - r;
                    float dy = y + 0.5f - r;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    float alpha;
                    if (filled)
                    {
                        alpha = Mathf.Clamp01((r - d) / 1.5f);
                    }
                    else
                    {
                        float distToRing = Mathf.Abs(d - (r - ringThickness * 0.5f));
                        alpha = Mathf.Clamp01(1f - (distToRing - ringThickness * 0.5f) / 1.5f);
                        if (d > r) alpha = Mathf.Min(alpha, Mathf.Clamp01((r - d) / 1.5f + 1f));
                    }
                    pixels[y * size + x] = new Color(1, 1, 1, alpha);
                }
            }
            tex.SetPixels32(pixels);
            tex.Apply(false, false);

            return Sprite.Create(tex, new Rect(0, 0, size, size), new Vector2(0.5f, 0.5f), size);
        }

        /// <summary>Ancla y estira un RectTransform para llenar completamente a su padre.</summary>
        public static void StretchToParent(RectTransform rt)
        {
            rt.anchorMin = Vector2.zero;
            rt.anchorMax = Vector2.one;
            rt.offsetMin = Vector2.zero;
            rt.offsetMax = Vector2.zero;
        }
    }
}
