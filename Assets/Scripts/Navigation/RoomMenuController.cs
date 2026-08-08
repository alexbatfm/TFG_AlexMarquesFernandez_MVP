using System.Collections.Generic;
using DigitalTwin.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DigitalTwin.Navigation
{
    /// <summary>
    /// Menú desplegable de acceso directo a las zonas del edificio, al estilo de los recorridos
    /// virtuales comerciales: una lista de estancias que traslada al punto representativo de la
    /// elegida.
    ///
    /// Por qué hace falta habiendo ya un grafo de navegación. El recorrido por puntos es adecuado
    /// para moverse por las inmediaciones, pero llegar al otro extremo del edificio exige
    /// encadenar una decena de saltos y conocer de antemano la disposición de las plantas. Un
    /// operario que acude a revisar un sensor concreto sabe a qué sala va, no por qué pasillos se
    /// llega; el menú responde a esa forma de plantearse el desplazamiento.
    ///
    /// Las zonas no se declaran en ninguna parte: salen del propio modelo, del conjunto de salas
    /// distintas que declaran los puntos de navegación en su conjunto de propiedades de
    /// localización. Añadir una estancia al modelo la incorpora al menú sin tocar código.
    ///
    /// Construido con el mini-framework de interfaz del proyecto, sin depender del sistema de
    /// eventos estándar del motor, por el mismo motivo que el resto de la interfaz.
    /// </summary>
    public class RoomMenuController : MonoBehaviour
    {
        private const float AnchoMenu = 240f;
        private const float AltoFila = 30f;
        private const float Margen = 16f;
        private const float AltoCabecera = 32f;

        private TourNavigationManager _tour;
        private RectTransform _raiz;
        private RectTransform _lista;
        private Text _textoCabecera;
        private bool _desplegado;

        private readonly List<Text> _etiquetas = new List<Text>();
        private readonly List<string> _salas = new List<string>();

        public void Initialize(TourNavigationManager tour, Canvas canvas)
        {
            _tour = tour;
            Construir(canvas.transform);
            Replegar();
        }

        private void Construir(Transform padre)
        {
            _raiz = RuntimeUIFactory.CreateRect(padre, "MenuZonas");
            _raiz.anchorMin = _raiz.anchorMax = new Vector2(0, 1);
            _raiz.pivot = new Vector2(0, 1);
            // Bajo la etiqueta del punto actual, que ocupa la primera franja.
            _raiz.anchoredPosition = new Vector2(Margen, -(Margen + 34f));
            _raiz.sizeDelta = new Vector2(AnchoMenu, AltoCabecera);

            // --- Cabecera pulsable ---
            var cabecera = RuntimeUIFactory.CreateRect(_raiz, "Cabecera");
            cabecera.anchorMin = new Vector2(0, 1);
            cabecera.anchorMax = new Vector2(1, 1);
            cabecera.pivot = new Vector2(0, 1);
            cabecera.anchoredPosition = Vector2.zero;
            cabecera.sizeDelta = new Vector2(0, AltoCabecera);

            var fondoCab = RuntimeUIFactory.CreatePanel(cabecera, "Fondo", new Color(0.08f, 0.10f, 0.14f, 0.92f));
            RuntimeUIFactory.StretchToParent((RectTransform)fondoCab.transform);

            _textoCabecera = RuntimeUIFactory.CreateText(cabecera, "Texto", "Ir a una zona  ▾", 16,
                                                        TextAnchor.MiddleLeft, Color.white, FontStyle.Bold);
            var rtCab = (RectTransform)_textoCabecera.transform;
            RuntimeUIFactory.StretchToParent(rtCab);
            rtCab.offsetMin = new Vector2(10, 0);
            rtCab.offsetMax = new Vector2(-10, 0);

            ClickRouter.Instance.Register(cabecera, Alternar, sortOrder: 30,
                                          isActive: () => _raiz.gameObject.activeSelf);

            // --- Lista de zonas ---
            _lista = RuntimeUIFactory.CreateRect(_raiz, "Lista");
            _lista.anchorMin = new Vector2(0, 1);
            _lista.anchorMax = new Vector2(1, 1);
            _lista.pivot = new Vector2(0, 1);
            _lista.anchoredPosition = new Vector2(0, -AltoCabecera);
            _lista.sizeDelta = new Vector2(0, 0);

            var fondoLista = RuntimeUIFactory.CreatePanel(_lista, "Fondo", new Color(0.05f, 0.06f, 0.08f, 0.94f));
            RuntimeUIFactory.StretchToParent((RectTransform)fondoLista.transform);

            ConstruirFilas();
        }

        private void ConstruirFilas()
        {
            _salas.Clear();
            _salas.AddRange(_tour.Salas);

            float y = 0f;
            foreach (var sala in _salas)
            {
                string nombre = sala; // copia local: el delegado del clic la captura

                var fila = RuntimeUIFactory.CreateRect(_lista, "Zona_" + nombre);
                fila.anchorMin = new Vector2(0, 1);
                fila.anchorMax = new Vector2(1, 1);
                fila.pivot = new Vector2(0, 1);
                fila.anchoredPosition = new Vector2(0, -y);
                fila.sizeDelta = new Vector2(0, AltoFila);

                var texto = RuntimeUIFactory.CreateText(fila, "Texto", nombre, 15,
                                                        TextAnchor.MiddleLeft, new Color(0.85f, 0.88f, 0.93f, 1f));
                var rt = (RectTransform)texto.transform;
                RuntimeUIFactory.StretchToParent(rt);
                rt.offsetMin = new Vector2(18, 0);
                rt.offsetMax = new Vector2(-10, 0);
                _etiquetas.Add(texto);

                ClickRouter.Instance.Register(fila, () => Seleccionar(nombre), sortOrder: 31,
                                              isActive: () => _desplegado && _lista.gameObject.activeSelf);

                y += AltoFila;
            }

            _lista.sizeDelta = new Vector2(0, y);
        }

        private void Alternar()
        {
            if (_desplegado) Replegar(); else Desplegar();
        }

        private void Desplegar()
        {
            _desplegado = true;
            _lista.gameObject.SetActive(true);
            _textoCabecera.text = "Ir a una zona  ▴";
            ResaltarSalaActual();
        }

        private void Replegar()
        {
            _desplegado = false;
            if (_lista != null) _lista.gameObject.SetActive(false);
            if (_textoCabecera != null) _textoCabecera.text = "Ir a una zona  ▾";
        }

        private void Seleccionar(string sala)
        {
            // Se repliega en cualquier caso: si el usuario elige la sala en la que ya está, el
            // viaje no se produce, pero dejar el menú abierto haría pensar que el clic no ha
            // tenido efecto.
            _tour.ViajarASala(sala);
            Replegar();
        }

        /// <summary>
        /// Marca la sala en la que se encuentra el usuario. Es una ayuda de orientación barata:
        /// al abrir el menú se ve de un vistazo dónde se está, no solo adónde se puede ir.
        /// </summary>
        private void ResaltarSalaActual()
        {
            string actual = _tour.SalaActual;
            for (int i = 0; i < _etiquetas.Count && i < _salas.Count; i++)
            {
                bool esActual = _salas[i] == actual;
                _etiquetas[i].color = esActual
                    ? new Color(1f, 0.82f, 0.2f, 1f)
                    : new Color(0.85f, 0.88f, 0.93f, 1f);
                _etiquetas[i].fontStyle = esActual ? FontStyle.Bold : FontStyle.Normal;
            }
        }
    }
}
