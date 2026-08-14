using System.Collections.Generic;
using DigitalTwin.Core;
using DigitalTwin.Navigation;
using IFCImporter;
using UnityEngine;
using UnityEngine.UI;

namespace DigitalTwin.MR
{
    /// <summary>
    /// Menú de zonas de la versión inmersiva: la última pieza de paridad con el escritorio.
    /// Una lista con las salas del edificio que traslada directamente al punto representativo
    /// de la elegida, sin pasos intermedios.
    ///
    /// LA DEFINICIÓN DE LAS ZONAS NO VIVE AQUÍ. Igual que la alcanzabilidad
    /// (<see cref="NavReachability"/>), las zonas tienen una sola definición para las dos
    /// versiones: <see cref="TourNavigationManager.CalcularZonas"/> — las salas que declaran
    /// los puntos del propio modelo en su propiedad de localización, cada una con su punto
    /// representativo. Esta clase solo las presenta en el espacio y lanza el viaje.
    ///
    /// APERTURA Y CIERRE con el botón primario del mando (A/X; en el respaldo del Editor, la
    /// tecla M). Cerrado no existe: la raíz entera está desactivada, no consume ni un rayo.
    /// Abierto, se coloca de frente al usuario a un metro y el rayo del mando lo maneja igual
    /// que el selector de modo; mientras tanto, el controlador de interacción cede el rayo
    /// (ver MRInteractionController), de modo que una pulsación sobre el menú nunca selecciona
    /// lo que hubiera detrás.
    ///
    /// SOLO EN NAVEGACIÓN POR NODOS. En modo anclado el desplazamiento es físico —el usuario
    /// anda—, y un teletransporte desincronizaría su cuerpo de la vista: es exactamente la
    /// misma razón por la que ese modo no ofrece puntos de navegación. El arranque no crea
    /// este menú en anclado y deja constancia.
    ///
    /// El viaje reutiliza el desplazamiento del navegador, que aplica el criterio de
    /// escritorio para trayectos largos: por encima de 12 m, salto instantáneo. Con trece
    /// zonas repartidas por la planta, ese es el caso normal del menú.
    /// </summary>
    public class MRMenuZonas : MonoBehaviour
    {
        private const float AnchoPx = 460f;
        private const float AltoCabeceraPx = 56f;
        private const float AltoFilaPx = 46f;
        private const float MargenPx = 10f;
        private const float AnchoMetros = 0.42f;

        /// <summary>Distancia y altura de aparición frente al usuario. Más cerca que el panel
        /// de metadatos (1,1 m): el menú es modal y breve, no un documento que leer.</summary>
        private const float DistanciaAlAbrir = 1.0f;

        private static readonly Color ColorFilaNormal = new Color(1f, 1f, 1f, 0.04f);
        private static readonly Color ColorFilaSenalada = new Color(1f, 0.82f, 0.2f, 0.30f);
        private static readonly Color ColorTextoNormal = new Color(0.85f, 0.88f, 0.93f, 1f);
        private static readonly Color ColorTextoSalaActual = new Color(1f, 0.82f, 0.2f, 1f);

        public bool Abierto { get; private set; }

        private MRControllerRig _rig;
        private Camera _camara;
        private MRNodeNavigator _navegador;

        private List<(string Sala, IfcMetadata Punto)> _zonas;

        private class Fila
        {
            public string Sala;
            public IfcMetadata Punto;
            public BoxCollider Volumen;
            public Image Fondo;
            public Text Texto;
        }

        private RectTransform _raiz;
        private readonly List<Fila> _filas = new List<Fila>();
        private int _filaSenalada = -1;

        public void Initialize(MRControllerRig rig, Camera camara, MRNodeNavigator navegador,
                               SceneModelIndex index)
        {
            _rig = rig;
            _camara = camara;
            _navegador = navegador;

            _zonas = TourNavigationManager.CalcularZonas(index.NavPoints);
            if (_zonas.Count == 0)
            {
                Debug.LogWarning("[DigitalTwin][AR] Menu de zonas: ningun punto de navegacion " +
                                 "declara sala (LOC_Localizacion4); el menu queda inoperante.");
                return;
            }

            Construir();
            Debug.LogWarning($"[DigitalTwin][AR] Menu de zonas del visor listo: {_zonas.Count} " +
                             "zonas del modelo. Se abre y cierra con el boton primario del " +
                             "mando (tecla M en el respaldo del Editor).");
        }

        private void Construir()
        {
            float altoPx = AltoCabeceraPx + _zonas.Count * AltoFilaPx + MargenPx * 2f;
            var canvas = DigitalTwin.UI.RuntimeUIFactory.CreateWorldCanvas(
                "~MenuZonasAR", anchoPx: AnchoPx, altoPx: altoPx, anchoMetros: AnchoMetros);
            _raiz = (RectTransform)canvas.transform;
            _raiz.SetParent(transform, true);

            // El mismo material sin prueba de profundidad que los carteles de destino, por el
            // mismo motivo: el menú se abre a un metro del usuario y un tabique cercano no
            // debe partirlo por la mitad.
            var material = MRIndicadoresDestino.MaterialSiempreVisible();

            var fondo = DigitalTwin.UI.RuntimeUIFactory.CreatePanel(_raiz, "Fondo",
                new Color(0.05f, 0.06f, 0.08f, 0.94f));
            DigitalTwin.UI.RuntimeUIFactory.StretchToParent((RectTransform)fondo.transform);
            if (material != null) fondo.material = material;

            var cabecera = DigitalTwin.UI.RuntimeUIFactory.CreateText(_raiz, "Cabecera",
                "Ir a una zona", 24, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
            var rtCab = (RectTransform)cabecera.transform;
            rtCab.anchorMin = rtCab.anchorMax = new Vector2(0.5f, 1f);
            rtCab.pivot = new Vector2(0.5f, 1f);
            rtCab.anchoredPosition = new Vector2(0f, -MargenPx);
            rtCab.sizeDelta = new Vector2(AnchoPx - MargenPx * 2f, AltoCabeceraPx - MargenPx);
            if (material != null) cabecera.material = material;

            float y = AltoCabeceraPx + MargenPx;
            foreach (var (sala, punto) in _zonas)
            {
                var filaRect = DigitalTwin.UI.RuntimeUIFactory.CreateRect(_raiz, "Zona_" + sala);
                filaRect.anchorMin = filaRect.anchorMax = new Vector2(0.5f, 1f);
                filaRect.pivot = new Vector2(0.5f, 1f);
                filaRect.anchoredPosition = new Vector2(0f, -y);
                // Tamaño FIJO y no anclado a los bordes: el volumen de colisión se dimensiona
                // en la creación y un rectángulo estirado aún no tiene medidas resueltas.
                filaRect.sizeDelta = new Vector2(AnchoPx - MargenPx * 2f, AltoFilaPx);

                var fondoFila = DigitalTwin.UI.RuntimeUIFactory.CreatePanel(filaRect, "Fondo",
                    ColorFilaNormal);
                DigitalTwin.UI.RuntimeUIFactory.StretchToParent((RectTransform)fondoFila.transform);
                if (material != null) fondoFila.material = material;

                var texto = DigitalTwin.UI.RuntimeUIFactory.CreateText(filaRect, "Texto", sala,
                    20, TextAnchor.MiddleLeft, ColorTextoNormal);
                var rtTexto = (RectTransform)texto.transform;
                DigitalTwin.UI.RuntimeUIFactory.StretchToParent(rtTexto);
                rtTexto.offsetMin = new Vector2(18f, 0f);
                rtTexto.offsetMax = new Vector2(-10f, 0f);
                if (material != null) texto.material = material;

                // Volumen para el rayo del mando: el mismo patrón que los carteles de destino.
                var volumen = filaRect.gameObject.AddComponent<BoxCollider>();
                volumen.isTrigger = true;
                volumen.size = new Vector3(AnchoPx - MargenPx * 2f, AltoFilaPx, 1f);
                volumen.center = Vector3.zero;

                _filas.Add(new Fila
                {
                    Sala = sala, Punto = punto, Volumen = volumen,
                    Fondo = fondoFila, Texto = texto
                });
                y += AltoFilaPx;
            }

            _raiz.gameObject.SetActive(false);
        }

        private void Update()
        {
            if (_rig == null || _raiz == null) return;

            if (_rig.BotonMenuPulsadoEsteFrame()) Alternar();
            if (!Abierto) return;

            // Interacción propia mientras está abierto (el controlador de interacción cede el
            // rayo): fila señalada por los volúmenes, gatillo para elegir.
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

            string salaActual = _navegador != null ? _navegador.SalaActual : string.Empty;
            for (int i = 0; i < _filas.Count; i++)
            {
                bool senalada = i == _filaSenalada;
                bool esActual = _filas[i].Sala == salaActual;
                _filas[i].Fondo.color = senalada ? ColorFilaSenalada : ColorFilaNormal;
                _filas[i].Texto.color = esActual ? ColorTextoSalaActual : ColorTextoNormal;
                _filas[i].Texto.fontStyle = esActual ? FontStyle.Bold : FontStyle.Normal;
            }

            _rig.MostrarImpacto(_filaSenalada >= 0 ? mejorDist : 0f, _filaSenalada >= 0);

            if (_filaSenalada >= 0 && _rig.GatilloPulsadoEsteFrame())
            {
                var fila = _filas[_filaSenalada];
                // Se cierra en cualquier caso, como en escritorio: si se elige la sala en la
                // que ya se está, el viaje no se produce, pero dejar el menú abierto haría
                // pensar que la pulsación no tuvo efecto.
                Cerrar();
                _navegador.IrAZona(fila.Punto, fila.Sala);
            }
        }

        private void Alternar()
        {
            if (Abierto) { Cerrar(); return; }

            if (_navegador != null && _navegador.EnTransito)
            {
                Debug.LogWarning("[DigitalTwin][AR] Menu de zonas: no se abre durante un " +
                                 "desplazamiento.");
                return;
            }
            Abrir();
        }

        private void Abrir()
        {
            // De frente al usuario, a un metro, con el centro un poco por debajo de la línea
            // de visión (el menú es alto; así la cabecera queda a la altura de los ojos).
            Vector3 mirada = _camara.transform.forward;
            mirada.y = 0f;
            if (mirada.sqrMagnitude < 0.0001f) mirada = Vector3.forward;
            mirada.Normalize();

            float altoMetros = _raiz.rect.height * _raiz.localScale.y;
            _raiz.position = _camara.transform.position + mirada * DistanciaAlAbrir
                           + Vector3.up * (-altoMetros * 0.5f + 0.10f);
            _raiz.rotation = Quaternion.LookRotation(mirada, Vector3.up);

            _raiz.gameObject.SetActive(true);
            Abierto = true;
            Debug.LogWarning($"[DigitalTwin][AR] Menu de zonas abierto ({_filas.Count} zonas; " +
                             $"sala actual: '{(_navegador != null ? _navegador.SalaActual : "?")}').");
        }

        private void Cerrar()
        {
            _raiz.gameObject.SetActive(false);
            Abierto = false;
            _filaSenalada = -1;
            Debug.LogWarning("[DigitalTwin][AR] Menu de zonas cerrado.");
        }
    }
}
