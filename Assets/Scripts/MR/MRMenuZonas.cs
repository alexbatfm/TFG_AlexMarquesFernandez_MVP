using System.Collections.Generic;
using DigitalTwin.Core;
using DigitalTwin.Navigation;
using IFCImporter;
using UnityEngine;
using UnityEngine.UI;

namespace DigitalTwin.MR
{
    /// <summary>
    /// El MENÚ de la versión inmersiva en modo de navegación por nodos. Nació como «menú de
    /// zonas» y desde el 15-08 la etiqueta se quedaba corta: además de la lista de salas aloja
    /// la fila de iluminación solar, el pie de leyenda y, desde la ronda 9, la vuelta al
    /// selector de modo — así que en la interfaz y en los comentarios se llama «menú» a secas.
    /// EL NOMBRE DE LA CLASE SE CONSERVA a propósito: renombrar clase y fichero exige borrar el
    /// .meta antiguo, y el montaje de trabajo no puede borrar ficheros (quedaría un .meta
    /// huérfano y un GUID desincronizado); las zonas siguen siendo además su contenido
    /// principal.
    ///
    /// La lista de salas traslada directamente al punto representativo de la elegida, sin
    /// pasos intermedios.
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
    ///
    /// DESDE EL 15-08 ES ADEMÁS EL CONTENEDOR DE CONTROLES del modo: bajo las zonas van la
    /// fila de ILUMINACIÓN SOLAR (muestra el estado —«fija», «hora real», «hora real (es de
    /// noche)»— y lo alterna al pulsarla; era una función construida e inalcanzable desde el
    /// visor, porque su único control vivía en el menú de ajustes de escritorio, que no se
    /// construye bajo XR) y un PIE DE LEYENDA con los gestos del menú. Una sola pieza
    /// invocable con un solo botón, en lugar de dos superficies que aprender; en modo anclado
    /// nada de esto existe, porque el menú no se crea allí.
    ///
    /// CONFIRMACIÓN DE LA VUELTA AL SELECTOR (17-08, tras la ronda 9). La fila desmonta la
    /// sesión y recarga la escena, y un gatillo accidental no debe costar eso. Se aplica el
    /// mismo patrón que el menú de escritorio usa para «Salir de la aplicación»
    /// (<see cref="DigitalTwin.Core.UI.SettingsMenuController"/>; guía, cap. 5, «Confirmación
    /// de la acción irreversible»): DOS PASOS SOBRE LA PROPIA FILA. Primer gatillo: la fila
    /// pasa a «¿Seguro? Pulsa otra vez para volver» y sube de intensidad; segundo gatillo:
    /// ejecuta. Cancelan el gatillo sobre otra fila (que además la ejecuta), el gatillo fuera
    /// de las filas (el clic fuera del panel de escritorio), A/X (el Escape de escritorio:
    /// cancela y deja el menú abierto) y cualquier cierre del menú. En este modo no se pierde
    /// trabajo comparable al registro del anclado, así que el aviso del pie es más ligero:
    /// dice el coste (recarga de la escena) y cómo cancelar. Misma convención que en
    /// <see cref="MRMenuAnclado"/>: un solo patrón que aprender.
    /// </summary>
    public class MRMenuZonas : MonoBehaviour
    {
        private const float AnchoPx = 460f;
        private const float AltoCabeceraPx = 56f;
        private const float AltoFilaPx = 46f;
        private const float AltoSeparadorPx = 10f;
        // El pie tiene sitio para DOS líneas: durante la confirmación de la vuelta al selector
        // dice el coste y cómo cancelar (misma medida en MRMenuAnclado).
        private const float AltoPiePx = 48f;
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

        // Fila de iluminación solar (contenedor de controles del modo, ver cabecera).
        private BoxCollider _volumenSolar;
        private Image _fondoSolar;
        private Text _textoSolar;
        private bool _solarSenalada;

        // Fila de vuelta al selector de modo (ronda 9): cambiar de modo sin reiniciar.
        private BoxCollider _volumenSelector;
        private Image _fondoSelector;
        private Text _textoSelector;
        private bool _selectorSenalado;
        private static readonly Color ColorFilaSelector = new Color(0.55f, 0.80f, 1f, 0.10f);
        // Confirmación pendiente: la misma familia cálida del señalado, más intensa —cambio de
        // intensidad y no de tono, como en escritorio—, con dos grados según el rayo la señale
        // o no, para que el estado se vea sin leer.
        private static readonly Color ColorFilaConfirmando = new Color(1f, 0.55f, 0.15f, 0.40f);
        private static readonly Color ColorFilaConfirmandoSenalada = new Color(1f, 0.55f, 0.15f, 0.70f);
        private static readonly Color ColorPie = new Color(0.65f, 0.68f, 0.73f, 1f);
        private static readonly Color ColorPieAviso = new Color(1f, 0.85f, 0.55f, 1f);
        private const string TextoSelector = "Volver al selector de modo";
        private const string TextoSelectorConfirmando = "¿Seguro? Pulsa otra vez para volver";
        private const string PieNormal = "Gatillo: elegir (viaja hasta la zona)  ·  A o X: cerrar";
        private const string PieConfirmando =
            "Se recarga la escena (unos segundos); no se pierde nada\n" +
            "Gatillo fuera de las filas o A o X: cancelar";
        private Text _pie;
        private bool _confirmandoSelector;

        public void Initialize(MRControllerRig rig, Camera camara, MRNodeNavigator navegador,
                               SceneModelIndex index)
        {
            _rig = rig;
            _camara = camara;
            _navegador = navegador;

            _zonas = TourNavigationManager.CalcularZonas(index.NavPoints);
            if (_zonas.Count == 0)
            {
                // Antes de la ronda 9 el menu se abandonaba aqui; ahora aloja tambien la vuelta
                // al selector de modo, asi que se construye igualmente (sin filas de zona).
                Debug.LogWarning("[DigitalTwin][AR] Menu: ningun punto de navegacion declara " +
                                 "sala (LOC_Localizacion4); se construye sin filas de zona.");
            }

            Construir();
            Debug.LogWarning($"[DigitalTwin][AR] Menu del visor listo: {_zonas.Count} zonas del " +
                             "modelo, fila de iluminacion solar y vuelta al selector de modo. " +
                             "Se abre y cierra con el boton primario del mando (tecla M en el " +
                             "respaldo del Editor).");
        }

        private void Construir()
        {
            float altoPx = AltoCabeceraPx + _zonas.Count * AltoFilaPx
                         + AltoSeparadorPx + AltoFilaPx      // fila de iluminación solar
                         + AltoSeparadorPx + AltoFilaPx      // fila de vuelta al selector
                         + AltoPiePx + MargenPx * 2f;        // pie de leyenda
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
                "Menú", 24, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
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

            // --- Fila de iluminación solar: muestra el estado, no alterna a ciegas ---------
            y += AltoSeparadorPx;
            var solarRect = DigitalTwin.UI.RuntimeUIFactory.CreateRect(_raiz, "IluminacionSolar");
            solarRect.anchorMin = solarRect.anchorMax = new Vector2(0.5f, 1f);
            solarRect.pivot = new Vector2(0.5f, 1f);
            solarRect.anchoredPosition = new Vector2(0f, -y);
            solarRect.sizeDelta = new Vector2(AnchoPx - MargenPx * 2f, AltoFilaPx);

            _fondoSolar = DigitalTwin.UI.RuntimeUIFactory.CreatePanel(solarRect, "Fondo",
                new Color(1f, 0.82f, 0.2f, 0.10f));
            DigitalTwin.UI.RuntimeUIFactory.StretchToParent((RectTransform)_fondoSolar.transform);
            if (material != null) _fondoSolar.material = material;

            _textoSolar = DigitalTwin.UI.RuntimeUIFactory.CreateText(solarRect, "Texto", "",
                20, TextAnchor.MiddleLeft, ColorTextoNormal);
            var rtSolar = (RectTransform)_textoSolar.transform;
            DigitalTwin.UI.RuntimeUIFactory.StretchToParent(rtSolar);
            rtSolar.offsetMin = new Vector2(18f, 0f);
            rtSolar.offsetMax = new Vector2(-10f, 0f);
            if (material != null) _textoSolar.material = material;

            _volumenSolar = solarRect.gameObject.AddComponent<BoxCollider>();
            _volumenSolar.isTrigger = true;
            _volumenSolar.size = new Vector3(AnchoPx - MargenPx * 2f, AltoFilaPx, 1f);
            _volumenSolar.center = Vector3.zero;
            y += AltoFilaPx;

            // --- Fila de vuelta al selector de modo (ronda 9) ------------------------------
            // Hasta ahora cambiar de modo exigia reiniciar la aplicacion; en una demostracion
            // eso es inaceptable. La fila desmonta la sesion entera y recarga la escena (ver
            // MRDigitalTwinBootstrap.VolverAlSelector y su justificacion del desmontaje).
            y += AltoSeparadorPx;
            var selectorRect = DigitalTwin.UI.RuntimeUIFactory.CreateRect(_raiz, "VolverAlSelector");
            selectorRect.anchorMin = selectorRect.anchorMax = new Vector2(0.5f, 1f);
            selectorRect.pivot = new Vector2(0.5f, 1f);
            selectorRect.anchoredPosition = new Vector2(0f, -y);
            selectorRect.sizeDelta = new Vector2(AnchoPx - MargenPx * 2f, AltoFilaPx);

            _fondoSelector = DigitalTwin.UI.RuntimeUIFactory.CreatePanel(selectorRect, "Fondo",
                ColorFilaSelector);
            DigitalTwin.UI.RuntimeUIFactory.StretchToParent((RectTransform)_fondoSelector.transform);
            if (material != null) _fondoSelector.material = material;

            _textoSelector = DigitalTwin.UI.RuntimeUIFactory.CreateText(selectorRect, "Texto",
                TextoSelector, 20, TextAnchor.MiddleLeft, ColorTextoNormal);
            var rtSelector = (RectTransform)_textoSelector.transform;
            DigitalTwin.UI.RuntimeUIFactory.StretchToParent(rtSelector);
            rtSelector.offsetMin = new Vector2(18f, 0f);
            rtSelector.offsetMax = new Vector2(-10f, 0f);
            if (material != null) _textoSelector.material = material;

            _volumenSelector = selectorRect.gameObject.AddComponent<BoxCollider>();
            _volumenSelector.isTrigger = true;
            _volumenSelector.size = new Vector3(AnchoPx - MargenPx * 2f, AltoFilaPx, 1f);
            _volumenSelector.center = Vector3.zero;
            y += AltoFilaPx;

            // --- Pie de leyenda: los gestos del menú, dichos en el propio menú -------------
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
                Debug.LogWarning("[DigitalTwin][AR] Menu: vuelta al selector PENDIENTE DE " +
                                 "CONFIRMACION (segundo gatillo sobre la fila para ejecutar; " +
                                 "gatillo fuera, A/X u otra fila cancelan).");
                return;
            }
            _confirmandoSelector = false;   // confirmado: no es una cancelacion, no se traza como tal
            RefrescarConfirmacion();
            Cerrar();
            MRDigitalTwinBootstrap.VolverAlSelector(
                "fila 'Volver al selector de modo' del menu de navegacion, confirmada");
        }

        private void CancelarConfirmacion(string motivo)
        {
            if (!_confirmandoSelector) return;
            _confirmandoSelector = false;
            RefrescarConfirmacion();
            Debug.LogWarning($"[DigitalTwin][AR] Menu: vuelta al selector CANCELADA ({motivo}).");
        }

        /// <summary>Texto de la fila y del pie según haya o no confirmación pendiente. Los
        /// colores los pinta Update cada fotograma.</summary>
        private void RefrescarConfirmacion()
        {
            if (_textoSelector != null)
                _textoSelector.text = _confirmandoSelector ? TextoSelectorConfirmando : TextoSelector;
            if (_pie == null) return;
            _pie.text = _confirmandoSelector ? PieConfirmando : PieNormal;
            _pie.fontSize = _confirmandoSelector ? 14 : 16;
            _pie.color = _confirmandoSelector ? ColorPieAviso : ColorPie;
        }

        /// <summary>Texto de la fila solar, siempre con el estado delante: «hora real (es de
        /// noche)» explica por qué la escena está oscura sin que nadie pregunte.</summary>
        private void RefrescarTextoSolar()
        {
            if (_textoSolar == null) return;
            var solar = Visual.SolarLightingController.Instancia;
            _textoSolar.text = solar != null
                ? $"Iluminacion solar: {solar.Descripcion}  (pulsar para cambiar)"
                : "Iluminacion solar: no disponible";
        }

        private void Update()
        {
            if (_rig == null || _raiz == null) return;

            if (_rig.BotonMenuPulsadoEsteFrame()) Alternar();
            if (!Abierto) return;

            // Interacción propia mientras está abierto (el controlador de interacción cede el
            // rayo): fila señalada por los volúmenes, gatillo para elegir.
            _filaSenalada = -1;
            _solarSenalada = false;
            _selectorSenalado = false;
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
                if (_volumenSolar != null &&
                    _volumenSolar.Raycast(rayo, out RaycastHit hitSolar, 20f) &&
                    hitSolar.distance < mejorDist)
                {
                    mejorDist = hitSolar.distance;
                    _filaSenalada = -1;
                    _solarSenalada = true;
                }
                if (_volumenSelector != null &&
                    _volumenSelector.Raycast(rayo, out RaycastHit hitSelector, 20f) &&
                    hitSelector.distance < mejorDist)
                {
                    mejorDist = hitSelector.distance;
                    _filaSenalada = -1;
                    _solarSenalada = false;
                    _selectorSenalado = true;
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
            if (_fondoSolar != null)
                _fondoSolar.color = _solarSenalada ? ColorFilaSenalada
                                                   : new Color(1f, 0.82f, 0.2f, 0.10f);
            if (_fondoSelector != null)
            {
                if (_confirmandoSelector)
                    _fondoSelector.color = _selectorSenalado ? ColorFilaConfirmandoSenalada
                                                             : ColorFilaConfirmando;
                else
                    _fondoSelector.color = _selectorSenalado ? ColorFilaSenalada : ColorFilaSelector;
            }

            bool haySenal = _filaSenalada >= 0 || _solarSenalada || _selectorSenalado;
            _rig.MostrarImpacto(haySenal ? mejorDist : 0f, haySenal);

            if (!_rig.GatilloPulsadoEsteFrame()) return;

            if (_selectorSenalado)
            {
                PulsarSelector();   // dos pasos: primero avisa, el segundo ejecuta
                return;
            }

            if (!haySenal)
            {
                // Gatillo fuera de las filas: el equivalente al clic fuera del panel en
                // escritorio. Cancela la confirmacion pendiente (si la hay) y nada mas.
                CancelarConfirmacion("gatillo fuera de las filas");
                return;
            }

            // Cualquier otra fila cancela la confirmacion Y se ejecuta, como en escritorio.
            CancelarConfirmacion("se ha elegido otra fila");

            if (_solarSenalada)
            {
                // El menú se queda abierto: el sentido de la fila es VER el estado nuevo.
                var solar = Visual.SolarLightingController.Instancia;
                if (solar != null)
                {
                    solar.Alternar();
                    Debug.LogWarning($"[DigitalTwin][AR] Iluminacion solar alternada desde el " +
                                     $"menu: ahora '{solar.Descripcion}'.");
                }
                else
                {
                    Debug.LogWarning("[DigitalTwin][AR] Iluminacion solar no disponible: no hay " +
                                     "controlador creado.");
                }
                RefrescarTextoSolar();
                return;
            }

            if (_filaSenalada >= 0)
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
            if (Abierto)
            {
                // Con una confirmacion pendiente, el boton primario la cancela en vez de cerrar
                // el menu: es la reaccion esperable de quien se ha arrepentido (mismo papel que
                // Escape en el menu de escritorio).
                if (_confirmandoSelector) { CancelarConfirmacion("boton primario A/X"); return; }
                Cerrar();
                return;
            }

            if (_navegador != null && _navegador.EnTransito)
            {
                Debug.LogWarning("[DigitalTwin][AR] Menu: no se abre durante un " +
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

            RefrescarTextoSolar();
            _confirmandoSelector = false;
            RefrescarConfirmacion();
            _raiz.gameObject.SetActive(true);
            Abierto = true;
            var solarAbierto = Visual.SolarLightingController.Instancia;
            Debug.LogWarning($"[DigitalTwin][AR] Menu abierto ({_filas.Count} zonas; " +
                             $"sala actual: '{(_navegador != null ? _navegador.SalaActual : "?")}'; " +
                             $"iluminacion solar: '{(solarAbierto != null ? solarAbierto.Descripcion : "no disponible")}').");
        }

        private void Cerrar()
        {
            // Cerrar por cualquier via descarta la confirmacion pendiente: no puede quedar viva
            // con el menu cerrado.
            if (_confirmandoSelector) CancelarConfirmacion("el menu se cierra");
            _raiz.gameObject.SetActive(false);
            Abierto = false;
            _filaSenalada = -1;
            Debug.LogWarning("[DigitalTwin][AR] Menu cerrado.");
        }
    }
}
