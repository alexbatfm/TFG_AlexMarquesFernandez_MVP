using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

namespace DigitalTwin.MR
{
    /// <summary>
    /// Indicadores de destino de la versión de Realidad Aumentada: un cartel flotante, siempre
    /// orientado al usuario, sobre cada nodo al que se puede ir desde el nodo actual.
    ///
    /// Es el equivalente inmersivo de los hotspots de escritorio. Allí
    /// <c>TourNavigationManager</c> los dibuja proyectados a coordenadas de pantalla; en un
    /// visor no existe «la pantalla», así que cada indicador es un pequeño lienzo en espacio de
    /// mundo colocado sobre el punto físico del destino, con el mismo lenguaje visual (anillo y
    /// punto dorados, etiqueta con la sala) para que ambas versiones se reconozcan como el mismo
    /// sistema.
    ///
    /// Solo se muestran los destinos que el grafo declare alcanzables desde el nodo actual,
    /// nunca los 36 puntos: quién decide qué es alcanzable no es esta clase, sino quien la llama
    /// (ver <see cref="MRNodeNavigator"/> y <see cref="Navigation.NavReachability"/>). Esta
    /// clase solo presenta.
    ///
    /// Cada cartel lleva un volumen de colisión propio (el mismo patrón que el panel de
    /// metadatos, ver <c>WorldPanelPlacer.ConstruirVolumenDeBloqueo</c>): el rayo del mando lo
    /// consulta directamente con <c>Collider.Raycast</c>, sin pasar por la física global, de
    /// modo que apuntar al cartel es apuntar al destino aunque la esfera del punto esté oculta.
    /// </summary>
    public class MRIndicadoresDestino : MonoBehaviour
    {
        /// <summary>Altura del cartel sobre la posición del nodo, en metros. Los puntos ya están
        /// a la altura de la vista, así que el cartel queda justo por encima de la línea de
        /// visión y no tapa el destino.</summary>
        public float AlturaSobreElNodo = 0.35f;

        /// <summary>Ancho del cartel en metros. Legible a 10-15 m sin invadir la escena.</summary>
        public float AnchoMetros = 0.45f;

        // Mismos colores que los hotspots de escritorio (TourNavigationManager.CreateHotspotSlot).
        private static readonly Color ColorAnillo = new Color(1f, 0.82f, 0.2f, 0.95f);
        private static readonly Color ColorPunto = new Color(1f, 0.82f, 0.2f, 0.9f);

        private class Cartel
        {
            public GameObject Raiz;
            public BoxCollider Volumen;
            public Text Etiqueta;
            public int IndiceNodo = -1;
        }

        private readonly List<Cartel> _pool = new List<Cartel>();
        private Camera _camara;
        private int _visibles;

        public void Initialize(Camera camara)
        {
            _camara = camara;
        }

        /// <summary>Un destino a señalar: índice de nodo del grafo, posición en mundo y texto.</summary>
        public struct Destino
        {
            public int IndiceNodo;
            public Vector3 Posicion;
            public string Etiqueta;
        }

        /// <summary>
        /// Muestra un cartel por destino y oculta los sobrantes. La lista viene ya filtrada por
        /// alcanzabilidad; aquí no se decide nada, solo se coloca.
        /// </summary>
        public void Mostrar(IReadOnlyList<Destino> destinos)
        {
            if (destinos == null) { OcultarTodos(); return; }

            while (_pool.Count < destinos.Count) _pool.Add(CrearCartel(_pool.Count));

            for (int i = 0; i < _pool.Count; i++)
            {
                var cartel = _pool[i];
                if (i < destinos.Count)
                {
                    var d = destinos[i];
                    cartel.IndiceNodo = d.IndiceNodo;
                    cartel.Etiqueta.text = d.Etiqueta;
                    cartel.Raiz.transform.position = d.Posicion + Vector3.up * AlturaSobreElNodo;
                    cartel.Raiz.SetActive(true);
                }
                else
                {
                    cartel.IndiceNodo = -1;
                    cartel.Raiz.SetActive(false);
                }
            }

            _visibles = destinos.Count;
        }

        public void OcultarTodos()
        {
            foreach (var cartel in _pool)
            {
                cartel.IndiceNodo = -1;
                cartel.Raiz.SetActive(false);
            }
            _visibles = 0;
        }

        /// <summary>
        /// ¿Corta el rayo alguno de los carteles visibles? Devuelve el índice de nodo del más
        /// cercano y la distancia del impacto. Es la vía prioritaria de señalar un destino: el
        /// cartel se ve, la esfera no.
        /// </summary>
        public bool TryImpacto(Ray rayo, out int indiceNodo, out float distancia)
        {
            indiceNodo = -1;
            distancia = float.MaxValue;

            foreach (var cartel in _pool)
            {
                if (cartel.IndiceNodo < 0 || !cartel.Raiz.activeSelf || cartel.Volumen == null) continue;
                if (cartel.Volumen.Raycast(rayo, out RaycastHit hit, 60f) && hit.distance < distancia)
                {
                    distancia = hit.distance;
                    indiceNodo = cartel.IndiceNodo;
                }
            }

            return indiceNodo >= 0;
        }

        private Cartel CrearCartel(int indice)
        {
            // Mismo mecanismo de lienzo en espacio de mundo que el panel de metadatos, a escala
            // de cartel. La resolución interna alta y la escala a metros mantienen el texto
            // nítido (ver RuntimeUIFactory.CreateWorldCanvas).
            var canvas = DigitalTwin.UI.RuntimeUIFactory.CreateWorldCanvas(
                $"~IndicadorDestino_{indice}", anchoPx: 240f, altoPx: 170f, anchoMetros: AnchoMetros);
            var raiz = canvas.gameObject;
            raiz.transform.SetParent(transform, true);

            var rt = (RectTransform)raiz.transform;

            // Anillo con punto central, el icono de destino que el usuario ya conoce de la
            // versión de escritorio.
            var anillo = DigitalTwin.UI.RuntimeUIFactory.CreateIcon(
                rt, "Anillo", DigitalTwin.UI.RuntimeUIFactory.RingSprite(), ColorAnillo);
            var anilloRect = (RectTransform)anillo.transform;
            anilloRect.anchorMin = anilloRect.anchorMax = new Vector2(0.5f, 1f);
            anilloRect.pivot = new Vector2(0.5f, 1f);
            anilloRect.anchoredPosition = new Vector2(0f, -8f);
            anilloRect.sizeDelta = new Vector2(84f, 84f);

            var punto = DigitalTwin.UI.RuntimeUIFactory.CreateIcon(
                anilloRect, "Punto", DigitalTwin.UI.RuntimeUIFactory.CircleSprite(), ColorPunto);
            var puntoRect = (RectTransform)punto.transform;
            puntoRect.anchorMin = puntoRect.anchorMax = new Vector2(0.5f, 0.5f);
            puntoRect.sizeDelta = new Vector2(20f, 20f);

            var etiquetaRect = DigitalTwin.UI.RuntimeUIFactory.CreateRect(rt, "Etiqueta");
            etiquetaRect.anchorMin = new Vector2(0.5f, 1f);
            etiquetaRect.anchorMax = new Vector2(0.5f, 1f);
            etiquetaRect.pivot = new Vector2(0.5f, 1f);
            etiquetaRect.anchoredPosition = new Vector2(0f, -96f);
            etiquetaRect.sizeDelta = new Vector2(230f, 64f);
            var etiqueta = DigitalTwin.UI.RuntimeUIFactory.CreateText(
                etiquetaRect, "Texto", "", 26, TextAnchor.UpperCenter, Color.white, FontStyle.Bold);
            DigitalTwin.UI.RuntimeUIFactory.StretchToParent((RectTransform)etiqueta.transform);

            // Volumen para el rayo del mando. Disparador: no interviene en ninguna simulación
            // física, solo se consulta a mano con Collider.Raycast (patrón del panel). Centrado
            // en el lienzo, cuyo rectángulo pivota sobre su centro.
            var volumen = raiz.AddComponent<BoxCollider>();
            volumen.isTrigger = true;
            volumen.size = new Vector3(rt.rect.width, rt.rect.height, 1f);
            volumen.center = Vector3.zero;

            raiz.SetActive(false);
            return new Cartel { Raiz = raiz, Volumen = volumen, Etiqueta = etiqueta };
        }

        private void LateUpdate()
        {
            if (_visibles == 0) return;

            if (_camara == null)
            {
                _camara = Camera.main;
                if (_camara == null) return;
            }

            // Billboard solo en horizontal, la misma convención (y el mismo sentido del vector)
            // que el panel de metadatos: el texto no se inclina aunque el usuario ladee la
            // cabeza, y el frente del lienzo queda hacia el usuario.
            Vector3 posCamara = _camara.transform.position;
            foreach (var cartel in _pool)
            {
                if (cartel.IndiceNodo < 0 || !cartel.Raiz.activeSelf) continue;

                Vector3 haciaFuera = cartel.Raiz.transform.position - posCamara;
                haciaFuera.y = 0f;
                if (haciaFuera.sqrMagnitude < 0.0001f) continue;
                cartel.Raiz.transform.rotation =
                    Quaternion.LookRotation(haciaFuera.normalized, Vector3.up);
            }
        }
    }
}
