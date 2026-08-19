using System.Collections.Generic;
using DigitalTwin.Core;
using UnityEngine;
using UnityEngine.UI;

namespace DigitalTwin.UI
{
    /// <summary>
    /// Contenido común de la pantalla de carga: título, paso en curso, barra de avance y
    /// porcentaje. Lo comparten la versión de escritorio y la del visor, que solo se diferencian
    /// en dónde vive el lienzo y en las restricciones de confort que impone cada plataforma.
    ///
    /// Se construye dentro de un rectángulo que le da quien lo usa, con anclajes relativos a ese
    /// rectángulo: así el mismo bloque encaja en un lienzo de 1920x1080 sobre la pantalla y en
    /// uno de 700x380 flotando a metro y medio del usuario, sin ninguna medida repetida.
    /// </summary>
    internal sealed class ContenidoPantallaDeCarga
    {
        private static readonly Color ColorTitulo = new Color(1f, 1f, 1f, 0.96f);
        private static readonly Color ColorPaso = new Color(1f, 1f, 1f, 0.80f);
        private static readonly Color ColorCanalBarra = new Color(1f, 1f, 1f, 0.14f);
        private static readonly Color ColorRellenoBarra = new Color(0.42f, 0.62f, 0.85f, 1f);

        private Text _paso;
        private Text _titulo;
        private Text _porcentaje;
        private Image _canal;
        private RectTransform _relleno;
        private Image _rellenoImagen;

        /// <summary>Grafos a los que se aplica el desvanecimiento de salida.</summary>
        private readonly List<Graphic> _graficos = new List<Graphic>();

        public void Construir(RectTransform contenedor, string titulo, int cuerpoTitulo,
                              int cuerpoPaso, int cuerpoPorcentaje, float altoBarra)
        {
            var tituloRect = RuntimeUIFactory.CreateRect(contenedor, "Titulo");
            tituloRect.anchorMin = new Vector2(0f, 1f);
            tituloRect.anchorMax = new Vector2(1f, 1f);
            tituloRect.pivot = new Vector2(0.5f, 1f);
            tituloRect.anchoredPosition = new Vector2(0f, -28f);
            tituloRect.sizeDelta = new Vector2(-56f, cuerpoTitulo * 1.6f);
            _titulo = RuntimeUIFactory.CreateText(tituloRect, "Texto", titulo, cuerpoTitulo,
                TextAnchor.MiddleCenter, ColorTitulo, FontStyle.Bold);
            RuntimeUIFactory.StretchToParent((RectTransform)_titulo.transform);

            // El paso en curso ocupa el centro del bloque y admite dos líneas: los textos de fase
            // son frases, no etiquetas, porque una espera con un final que se entiende se tolera
            // mucho mejor que una espera muda.
            var pasoRect = RuntimeUIFactory.CreateRect(contenedor, "Paso");
            pasoRect.anchorMin = new Vector2(0f, 0f);
            pasoRect.anchorMax = new Vector2(1f, 1f);
            pasoRect.offsetMin = new Vector2(34f, altoBarra + 78f);
            pasoRect.offsetMax = new Vector2(-34f, -(cuerpoTitulo * 1.6f + 40f));
            _paso = RuntimeUIFactory.CreateText(pasoRect, "Texto", string.Empty, cuerpoPaso,
                TextAnchor.MiddleCenter, ColorPaso, FontStyle.Normal);
            RuntimeUIFactory.StretchToParent((RectTransform)_paso.transform);

            var canalRect = RuntimeUIFactory.CreateRect(contenedor, "Barra");
            canalRect.anchorMin = new Vector2(0f, 0f);
            canalRect.anchorMax = new Vector2(1f, 0f);
            canalRect.pivot = new Vector2(0.5f, 0f);
            canalRect.anchoredPosition = new Vector2(0f, 52f);
            canalRect.sizeDelta = new Vector2(-68f, altoBarra);
            _canal = RuntimeUIFactory.CreatePanel(canalRect, "Canal", ColorCanalBarra);
            RuntimeUIFactory.StretchToParent((RectTransform)_canal.transform);

            _rellenoImagen = RuntimeUIFactory.CreatePanel(canalRect, "Relleno", ColorRellenoBarra);
            _relleno = (RectTransform)_rellenoImagen.transform;
            _relleno.anchorMin = Vector2.zero;
            _relleno.anchorMax = new Vector2(0f, 1f);
            _relleno.offsetMin = Vector2.zero;
            _relleno.offsetMax = Vector2.zero;

            var porcentajeRect = RuntimeUIFactory.CreateRect(contenedor, "Porcentaje");
            porcentajeRect.anchorMin = new Vector2(0f, 0f);
            porcentajeRect.anchorMax = new Vector2(1f, 0f);
            porcentajeRect.pivot = new Vector2(0.5f, 0f);
            porcentajeRect.anchoredPosition = new Vector2(0f, 12f);
            porcentajeRect.sizeDelta = new Vector2(-68f, cuerpoPorcentaje * 1.5f);
            _porcentaje = RuntimeUIFactory.CreateText(porcentajeRect, "Texto", "0 %",
                cuerpoPorcentaje, TextAnchor.MiddleCenter, ColorPaso, FontStyle.Normal);
            RuntimeUIFactory.StretchToParent((RectTransform)_porcentaje.transform);

            _graficos.Add(_titulo);
            _graficos.Add(_paso);
            _graficos.Add(_porcentaje);
            _graficos.Add(_canal);
            _graficos.Add(_rellenoImagen);
        }

        public void Refrescar(string paso, float fraccion)
        {
            if (_paso != null) _paso.text = paso;
            if (_porcentaje != null) _porcentaje.text = Mathf.RoundToInt(fraccion * 100f) + " %";
            if (_relleno != null) _relleno.anchorMax = new Vector2(Mathf.Clamp01(fraccion), 1f);
        }

        /// <summary>Registra un gráfico adicional (el fondo del panel) en el desvanecimiento.</summary>
        public void IncluirEnDesvanecido(Graphic g)
        {
            if (g != null) _graficos.Add(g);
        }

        /// <summary>Multiplica el alfa de todo el contenido. Se guarda el alfa de diseño de cada
        /// gráfico la primera vez, para que el desvanecimiento no lo aplaste.</summary>
        public void AplicarAlfa(float alfa)
        {
            for (int i = 0; i < _graficos.Count; i++)
            {
                var g = _graficos[i];
                if (g == null) continue;
                if (!_alfasBase.ContainsKey(g)) _alfasBase[g] = g.color.a;
                var c = g.color;
                c.a = _alfasBase[g] * Mathf.Clamp01(alfa);
                g.color = c;
            }
        }

        private readonly Dictionary<Graphic, float> _alfasBase = new Dictionary<Graphic, float>();
    }

    /// <summary>
    /// Pantalla de carga de la versión de escritorio.
    ///
    /// Aquí no hay problema vestibular —el monitor no se mueve con la cabeza— así que la
    /// presentación es la convencional: un lienzo sobre la pantalla que tapa la escena mientras
    /// se monta. Lo que se conserva del visor es el motivo de fondo: que el usuario sepa que la
    /// aplicación está trabajando y en qué paso va, en lugar de mirar una escena a medio montar
    /// durante medio segundo.
    ///
    /// El modelo de progreso es el mismo (<see cref="ProgresoDeArranque"/>) y el bloque de
    /// contenido también (<see cref="ContenidoPantallaDeCarga"/>): lo único propio de esta clase
    /// es el tipo de lienzo y el fondo a pantalla completa.
    /// </summary>
    public class PantallaDeCargaEscritorio : MonoBehaviour
    {
        /// <summary>Duración del desvanecimiento de salida. Ver la justificación del valor en
        /// la pantalla del visor (<c>MRPantallaDeCarga</c>): se comparte para que las dos
        /// plataformas se comporten igual donde no hay razón para que difieran.</summary>
        private const float SegundosDeSalida = 0.25f;

        private static readonly Color ColorFondo = new Color(0.055f, 0.065f, 0.082f, 1f);

        public static PantallaDeCargaEscritorio Instancia { get; private set; }

        private ContenidoPantallaDeCarga _contenido;
        private Canvas _canvas;
        private bool _cerrando;
        private float _alfa = 1f;

        public static PantallaDeCargaEscritorio Abrir()
        {
            if (Instancia != null)
            {
                Instancia.CancelarCierre();
                return Instancia;
            }

            var go = new GameObject("~PantallaDeCarga");
            DontDestroyOnLoad(go);
            Instancia = go.AddComponent<PantallaDeCargaEscritorio>();
            Instancia.Construir();
            return Instancia;
        }

        private void Construir()
        {
            // Orden de dibujo muy alto: la pantalla de carga se dibuja por encima de cualquier
            // interfaz que el arranque vaya creando mientras ella está puesta.
            _canvas = RuntimeUIFactory.CreateRootCanvas("~LienzoPantallaDeCarga", sortOrder: 5000);
            var raiz = (RectTransform)_canvas.transform;

            var fondo = RuntimeUIFactory.CreatePanel(raiz, "Fondo", ColorFondo);
            RuntimeUIFactory.StretchToParent((RectTransform)fondo.transform);

            var bloque = RuntimeUIFactory.CreateRect(raiz, "Bloque");
            bloque.anchorMin = new Vector2(0.5f, 0.5f);
            bloque.anchorMax = new Vector2(0.5f, 0.5f);
            bloque.pivot = new Vector2(0.5f, 0.5f);
            bloque.sizeDelta = new Vector2(900f, 300f);
            bloque.anchoredPosition = Vector2.zero;

            _contenido = new ContenidoPantallaDeCarga();
            _contenido.Construir(bloque, "Gemelo Digital BIM", cuerpoTitulo: 44, cuerpoPaso: 26,
                                 cuerpoPorcentaje: 22, altoBarra: 16f);
            _contenido.IncluirEnDesvanecido(fondo);

            ProgresoDeArranque.AlCambiar += Refrescar;
            Refrescar();
        }

        private void Refrescar()
        {
            _contenido?.Refrescar(ProgresoDeArranque.TextoDeFase, ProgresoDeArranque.Fraccion);
        }

        private void Update()
        {
            ProgresoDeArranque.RegistrarIntervaloDeFotograma();

            if (!_cerrando) return;
            _alfa -= Time.unscaledDeltaTime / SegundosDeSalida;
            if (_alfa <= 0f) { Destruir(); return; }
            _contenido?.AplicarAlfa(_alfa);
        }

        public void Cerrar()
        {
            if (_cerrando) return;
            _cerrando = true;
        }

        private void CancelarCierre()
        {
            if (!_cerrando) return;
            _cerrando = false;
            _alfa = 1f;
            _contenido?.AplicarAlfa(1f);
        }

        private void Destruir()
        {
            ProgresoDeArranque.AlCambiar -= Refrescar;
            if (Instancia == this) Instancia = null;
            if (_canvas != null) Destroy(_canvas.gameObject);
            Destroy(gameObject);
        }

        private void OnDestroy()
        {
            ProgresoDeArranque.AlCambiar -= Refrescar;
            if (Instancia == this) Instancia = null;
        }
    }
}
