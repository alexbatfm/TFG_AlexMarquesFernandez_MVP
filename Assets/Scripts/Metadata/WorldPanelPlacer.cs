using UnityEngine;

namespace DigitalTwin.Metadata
{
    /// <summary>
    /// Mantiene el panel de metadatos ante el usuario, a distancia de lectura, y traza una línea
    /// que lo une con el elemento del que informa.
    ///
    /// Este componente solo se usa en la versión inmersiva. En escritorio el panel lateral fijo
    /// funciona porque hay ratón y pantalla plana.
    ///
    /// **Vinculación al usuario, no al objeto.** La primera versión colocaba el panel junto al
    /// elemento seleccionado. El resultado es que un sensor situado al fondo de una nave produce
    /// una ficha ilegible salvo que el operario se acerque, lo que contradice el propósito del
    /// sistema: consultar el estado de un activo sin desplazarse hasta él. El panel se sitúa por
    /// tanto ante el usuario, a distancia fija.
    ///
    /// **Por qué no va soldado a la cabeza.** Una interfaz rígidamente anclada a la cámara se
    /// desaconseja de forma unánime en entornos inmersivos: al no responder al movimiento de la
    /// cabeza, obliga a la mirada a perseguir un objeto que nunca se queda quieto, y en sesiones
    /// prolongadas resulta fatigante. La alternativa adoptada es un seguimiento perezoso: el
    /// panel permanece inmóvil mientras la mirada se mantenga dentro de una zona muerta angular,
    /// y solo cuando el usuario gira lo suficiente se desplaza suavemente hasta volver a quedar
    /// frente a él. Se conserva así la ventaja de que siempre esté accesible sin el coste de que
    /// esté siempre encima.
    ///
    /// Se sigue únicamente el giro horizontal, nunca la inclinación: si el panel acompañase al
    /// cabeceo, mirar al suelo lo arrastraría hacia abajo y taparía precisamente lo que se está
    /// mirando.
    ///
    /// **Por qué se mantiene la línea de unión, y por qué ahora importa más.** Con el panel junto
    /// al objeto, la línea resolvía la ambigüedad entre elementos iguales y cercanos. Alejado el
    /// panel del objeto, pasa a ser el único vínculo entre la ficha y el activo que describe: sin
    /// ella, la información quedaría huérfana. Se mantiene además el resaltado de
    /// <see cref="SelectionHighlighter"/>, porque cada mecanismo cubre un caso en el que los
    /// otros fallan.
    /// </summary>
    public class WorldPanelPlacer : MonoBehaviour
    {
        [Header("Colocación respecto al usuario")]
        [Tooltip("Distancia del panel al usuario, en metros. Por debajo de 0,8 m la disparidad " +
                 "entre convergencia y enfoque resulta molesta; por encima de 2 m el texto pierde " +
                 "legibilidad. Bajada de 1,3 a 1,1 tras la prueba del 2026-08-13 (texto borroso).")]
        public float DistanciaAlUsuario = 1.1f;

        [Tooltip("Margen angular del BORDE SUPERIOR del panel bajo la linea de vision, en grados. " +
                 "La altura del panel ya no es una constante: se DERIVA del tamano real del " +
                 "lienzo en Initialize (ver AlturaRelativaCalculada), de modo que cambiar el " +
                 "ancho del lienzo no puede volver a invadir la linea de vision. 2,4 era el " +
                 "margen que producia el valor aprobado (-0,40 m) con el lienzo de 0,58 m.")]
        [Range(0.5f, 10f)] public float MargenBajoVisionGrados = 2.4f;

        /// <summary>
        /// Desplazamiento vertical del CENTRO del panel respecto a los ojos, en metros. El panel
        /// entero debe quedar POR DEBAJO de la línea de visión: esa condición es la que permite
        /// ver el elemento señalado por encima del panel y la que hace funcionar el cierre por
        /// segundo disparo (si el panel invadiera la mirada, interceptaría el rayo del segundo
        /// disparo en lugar del elemento).
        ///
        /// EL CÁLCULO, escrito y no solo el número. Con el margen angular θ y la distancia d,
        /// el borde superior debe quedar tan(θ)·d metros bajo los ojos; como el centro está
        /// media altura de lienzo por debajo del borde:
        ///
        ///     AlturaRelativa = −( tan(θ)·d + alto/2 ),   alto = altoPx·(anchoMetros/anchoPx)
        ///
        /// Con los valores históricos (0,58 m de ancho, lienzo 900×1100, d = 1,1, θ = 2,4°):
        /// alto = 0,709, y −(0,046 + 0,354) = −0,40 — exactamente el valor que se aprobó en el
        /// visor, lo que valida la fórmula. Con el ancho definitivo de 1 m: alto = 1,222 y
        /// AlturaRelativa = −(0,046 + 0,611) = −0,66 m.
        ///
        /// POR QUÉ SE DERIVA EN VEZ DE CONSTANTE: el 14-08 el ancho se subió a 0,70 m con la
        /// constante antigua (−0,40) y el borde superior pasó a quedar 2,8 cm POR ENCIMA de los
        /// ojos (el umbral de invasión estaba en 0,654 m de ancho). Con la derivación, esa
        /// clase de error deja de poder existir.
        /// </summary>
        public float AlturaRelativaCalculada { get; private set; } = -0.40f;

        [Tooltip("Zona muerta angular, en grados. Mientras la mirada no se aleje más de este " +
                 "ángulo del panel, el panel no se mueve en absoluto.")]
        [Range(5f, 60f)] public float ZonaMuertaGrados = 22f;

        [Tooltip("Ángulo, en grados, por debajo del cual se considera que el panel ya ha vuelto a " +
                 "quedar centrado y cesa el seguimiento. Da histéresis: sin ella el panel " +
                 "entraría y saldría del estado de seguimiento continuamente en el borde.")]
        [Range(0f, 10f)] public float AnguloDeReposoGrados = 2.5f;

        [Tooltip("Suavizado del movimiento del panel. 0 = salto instantáneo.")]
        [Range(0f, 20f)] public float Suavizado = 6f;

        [Header("Línea de unión")]
        public Color ColorLinea = new Color(1f, 0.78f, 0.15f, 0.85f);
        public float GrosorLinea = 0.006f;

        private Transform _panel;
        private Transform _camara;
        private Transform _objetivo;
        private LineRenderer _linea;
        private Vector3 _posicionDeseada;
        private bool _colocadoAlMenosUnaVez;

        private BoxCollider _volumenPanel;

        public void Initialize(Canvas canvasMundo)
        {
            _panel = canvasMundo.transform;
            _camara = Camera.main != null ? Camera.main.transform : null;
            canvasMundo.worldCamera = Camera.main;
            ConstruirVolumenDeBloqueo(canvasMundo);
            ConstruirLinea();
            CalcularAlturaRelativa(canvasMundo);
            _panel.gameObject.SetActive(false);
        }

        /// <summary>
        /// Deriva la altura del panel del tamaño REAL del lienzo, aplicando la fórmula
        /// documentada en <see cref="AlturaRelativaCalculada"/>. Se registra el cálculo entero:
        /// si alguna vez el panel invade la mirada, el registro dirá con qué números se colocó.
        /// </summary>
        private void CalcularAlturaRelativa(Canvas canvasMundo)
        {
            var rt = canvasMundo.transform as RectTransform;
            if (rt == null)
            {
                Debug.LogWarning("[DigitalTwin][AR] El lienzo del panel no tiene RectTransform; " +
                                 $"la altura relativa se queda en el valor por defecto " +
                                 $"({AlturaRelativaCalculada:0.00} m).");
                return;
            }

            float altoMetros = rt.rect.height * rt.localScale.y;
            float margen = Mathf.Tan(MargenBajoVisionGrados * Mathf.Deg2Rad) * DistanciaAlUsuario;
            AlturaRelativaCalculada = -(margen + altoMetros * 0.5f);

            Debug.LogWarning($"[DigitalTwin][AR] Panel: lienzo de " +
                             $"{rt.rect.width * rt.localScale.x:0.00}x{altoMetros:0.00} m a " +
                             $"{DistanciaAlUsuario:0.00} m; altura relativa derivada = " +
                             $"-(tan({MargenBajoVisionGrados:0.0} grados)*{DistanciaAlUsuario:0.00} + " +
                             $"{altoMetros * 0.5f:0.00}) = {AlturaRelativaCalculada:0.00} m. " +
                             "El borde superior queda bajo la linea de vision por construccion.");
        }

        /// <summary>
        /// Volumen físico que ocupa el panel, para que el rayo del mando lo detecte.
        ///
        /// Un lienzo de interfaz no participa en el sistema de física: la selección del visor se
        /// resuelve con <c>Physics.Raycast</c> contra colisionadores, así que el rayo atravesaba
        /// el panel y seleccionaba el objeto que hubiera detrás. El síntoma para el usuario es que
        /// la ficha no responde y, peor, que pulsar sobre ella cambia la selección.
        ///
        /// Es un colisionador de bloqueo, no de interacción: su función es interceptar el rayo
        /// para que <see cref="MR.MRInteractionController"/> sepa que la intención se dirige a la
        /// interfaz y no al edificio. Va como disparador para que no altere ninguna simulación
        /// física ni afecte al desplazamiento.
        /// </summary>
        private void ConstruirVolumenDeBloqueo(Canvas canvasMundo)
        {
            var rt = canvasMundo.transform as RectTransform;
            if (rt == null) return;

            _volumenPanel = canvasMundo.gameObject.AddComponent<BoxCollider>();
            _volumenPanel.isTrigger = true;
            // El tamaño va en unidades locales del lienzo; la escala del RectTransform ya lo lleva
            // a metros. El grosor es simbólico: basta con que el rayo tenga algo que cortar.
            _volumenPanel.size = new Vector3(rt.rect.width, rt.rect.height, 1f);
            _volumenPanel.center = Vector3.zero;
        }

        /// <summary>
        /// Cierto si el rayo corta el panel mientras está visible, con la distancia del impacto.
        /// Se consulta antes que el mundo: lo que el usuario tiene delante manda sobre lo que hay
        /// detrás.
        /// </summary>
        public bool RayoImpactaPanel(Ray rayo, out float distancia)
        {
            distancia = 0f;
            if (_volumenPanel == null || _panel == null || !_panel.gameObject.activeInHierarchy)
                return false;

            if (_volumenPanel.Raycast(rayo, out RaycastHit hit, 50f))
            {
                distancia = hit.distance;
                return true;
            }
            return false;
        }

        private void ConstruirLinea()
        {
            var go = new GameObject("~LineaUnionPanel");
            go.transform.SetParent(transform, false);
            _linea = go.AddComponent<LineRenderer>();

            // Ver la nota equivalente en SelectionHighlighter: la creacion directa del material
            // lanzaba en compilacion y se llevaba por delante el arranque completo.
            var mat = DigitalTwin.Core.RuntimeMaterials.CrearSinIluminacion(ColorLinea);
            if (mat != null) _linea.material = mat;

            _linea.useWorldSpace = true;
            _linea.positionCount = 2;
            _linea.widthMultiplier = GrosorLinea;
            _linea.numCapVertices = 2;
            _linea.alignment = LineAlignment.View;
            _linea.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _linea.receiveShadows = false;
            go.SetActive(false);
        }

        /// <summary>Empieza a seguir al objeto indicado. Con null, oculta panel y línea.</summary>
        public void Seguir(Transform objetivo)
        {
            _objetivo = objetivo;
            _colocadoAlMenosUnaVez = false;

            // Cada selección nueva reencuadra el panel ante el usuario. Si conservara el rumbo
            // anterior, consultar un elemento podría abrir su ficha fuera del campo de visión,
            // que es el defecto que este componente viene a corregir.
            _rumboPanel = Vector3.zero;
            _siguiendo = false;

            bool visible = objetivo != null;
            if (_panel != null) _panel.gameObject.SetActive(visible);
            if (_linea != null) _linea.gameObject.SetActive(visible);
        }

        private void LateUpdate()
        {
            // En LateUpdate y no en Update: la cámara del visor la mueve el sistema de XR durante
            // Update, así que colocar antes daría un panel siempre un fotograma por detrás, que
            // en un HMD se percibe como un temblor muy molesto.
            if (_objetivo == null || _panel == null) return;
            if (_camara == null)
            {
                if (Camera.main == null) return;
                _camara = Camera.main.transform;
            }

            CalcularPosicionDeseada();

            // La posición se aplica directamente, sin suavizar aquí. El suavizado ya está en el
            // rumbo, que es lo único que conviene retrasar. Amortiguar además la posición
            // introduciría un segundo retardo encadenado sobre el mismo movimiento y, sobre todo,
            // haría que el panel se quedase atrás al caminar: la traslación del usuario debe
            // seguirse al instante, solo el giro merece inercia.
            _panel.position = _posicionDeseada;
            _colocadoAlMenosUnaVez = true;

            // Orientar el panel hacia el usuario (billboard). Se fuerza el eje vertical del mundo
            // para que el texto nunca aparezca inclinado aunque el usuario ladee la cabeza.
            Vector3 haciaUsuario = _panel.position - _camara.position;
            haciaUsuario.y = 0f;
            if (haciaUsuario.sqrMagnitude > 0.0001f)
                _panel.rotation = Quaternion.LookRotation(haciaUsuario.normalized, Vector3.up);

            ActualizarLinea();
        }

        /// <summary>
        /// Rumbo horizontal, en el plano del suelo, hacia el que mira el panel. Se guarda como
        /// dirección y no como posición porque la altura y la distancia son constantes: lo único
        /// que el seguimiento perezoso decide es hacia dónde.
        /// </summary>
        private Vector3 _rumboPanel;
        private bool _siguiendo;

        private void CalcularPosicionDeseada()
        {
            Vector3 miradaPlana = _camara.forward;
            miradaPlana.y = 0f;
            if (miradaPlana.sqrMagnitude < 0.0001f) miradaPlana = Vector3.forward;
            miradaPlana.Normalize();

            // Primer encuadre: el panel aparece directamente ante el usuario.
            if (_rumboPanel.sqrMagnitude < 0.0001f) _rumboPanel = miradaPlana;

            float desvio = Vector3.Angle(_rumboPanel, miradaPlana);

            // Histéresis. Se entra en seguimiento al salir de la zona muerta y no se sale hasta
            // que el panel vuelve a estar prácticamente centrado. Con un solo umbral, el panel
            // quedaría oscilando en el borde: cada pequeño giro lo activaría, avanzaría unos
            // grados hasta volver a entrar en la zona muerta, y se detendría de golpe.
            if (!_siguiendo && desvio > ZonaMuertaGrados) _siguiendo = true;
            else if (_siguiendo && desvio <= AnguloDeReposoGrados) _siguiendo = false;

            if (_siguiendo)
            {
                float t = Suavizado <= 0f ? 1f : 1f - Mathf.Exp(-Suavizado * Time.deltaTime);
                _rumboPanel = Vector3.Slerp(_rumboPanel, miradaPlana, t).normalized;
            }

            // La altura acompaña a la del usuario pero ignora el cabeceo, de modo que agacharse
            // baja el panel y mirar al suelo no.
            Vector3 origen = _camara.position;
            _posicionDeseada = origen
                             + _rumboPanel * DistanciaAlUsuario
                             + Vector3.up * AlturaRelativaCalculada;
        }

        private void ActualizarLinea()
        {
            if (_linea == null) return;

            _linea.SetPosition(0, PuntoDeSalidaHaciaObjeto());
            _linea.SetPosition(1, CentroDe(_objetivo));
            _linea.widthMultiplier = GrosorLinea;
        }

        /// <summary>
        /// Punto del borde del panel por el que debe salir la línea hacia el objeto: aquel en el
        /// que la recta panel-objeto, proyectada sobre el plano del panel, corta su rectángulo.
        ///
        /// La versión anterior salía siempre del borde izquierdo, lo cual era correcto mientras el
        /// panel se colocaba a la derecha del elemento. Vinculado ahora el panel al usuario, el
        /// objeto puede quedar en cualquier dirección —incluso a la espalda—, y un origen fijo
        /// produce una línea que ni sale por el lado que mira al objeto ni parece tocar la ficha.
        ///
        /// Se resuelve en coordenadas locales del panel: se proyecta la dirección al objeto sobre
        /// el plano del rectángulo y se escala hasta el primero de los dos bordes que encuentra,
        /// que es el menor de los dos factores de corte. Así la línea nace siempre pegada al canto
        /// correcto, sea cual sea la posición relativa.
        /// </summary>
        private Vector3 PuntoDeSalidaHaciaObjeto()
        {
            var rt = _panel as RectTransform;
            if (rt == null || _objetivo == null) return _panel.position;

            Vector3 local = _panel.InverseTransformPoint(CentroDe(_objetivo));
            Vector2 dir = new Vector2(local.x, local.y);

            // Objeto justo detrás del panel: no hay dirección de salida definida y cualquier borde
            // sería arbitrario. Se ancla al centro, que además es lo que se ve.
            if (dir.sqrMagnitude < 0.0001f) return _panel.position;
            dir.Normalize();

            float mitadAncho = rt.rect.width * 0.5f;
            float mitadAlto = rt.rect.height * 0.5f;

            float tX = Mathf.Abs(dir.x) > 0.0001f ? mitadAncho / Mathf.Abs(dir.x) : float.MaxValue;
            float tY = Mathf.Abs(dir.y) > 0.0001f ? mitadAlto / Mathf.Abs(dir.y) : float.MaxValue;
            float t = Mathf.Min(tX, tY);

            return _panel.TransformPoint(new Vector3(dir.x * t, dir.y * t, 0f));
        }

        private static Vector3 CentroDe(Transform t)
        {
            if (t == null) return Vector3.zero;
            var r = t.GetComponentInChildren<Renderer>();
            return r != null ? r.bounds.center : t.position;
        }

        // RadioAproximado() se ha retirado: servía para separar el panel del borde del objeto, y
        // el panel ya no se coloca junto al objeto.
    }
}
