using UnityEngine;

namespace DigitalTwin.Metadata
{
    /// <summary>
    /// Coloca el panel de metadatos en el espacio 3D junto al elemento seleccionado, orientado
    /// hacia el usuario, y traza una línea que une el panel con el objeto.
    ///
    /// Este componente solo se usa en Realidad Mixta. En escritorio el panel lateral fijo
    /// funciona bien porque hay un ratón y una pantalla plana; en un visor, en cambio, un panel
    /// pegado a la cara es incómodo y rompe la sensación de estar dentro del edificio.
    ///
    /// Por qué panel flotante MÁS línea de unión, y no solo una de las dos cosas:
    /// un panel colocado junto al objeto parece suficiente para saber de qué informa, pero deja
    /// de serlo en cuanto el objeto queda tapado por una pared, o cuando hay varios elementos
    /// iguales cerca (dos sensores en la misma sala). La línea resuelve esa ambigüedad sin
    /// obligar al usuario a razonar. Es el mismo motivo por el que se mantiene además el
    /// resaltado de <see cref="SelectionHighlighter"/>: cada mecanismo cubre un caso en el que
    /// los otros fallan.
    ///
    /// El panel se coloca a un lado del objeto y no delante a propósito: taparlo con su propia
    /// ficha sería contraproducente en un sistema cuyo objetivo es que el operario vea el activo.
    /// </summary>
    public class WorldPanelPlacer : MonoBehaviour
    {
        [Header("Colocación")]
        [Tooltip("Distancia a la que se separa el panel del borde del objeto, en metros.")]
        public float SeparacionLateral = 0.45f;
        [Tooltip("Distancia máxima a la que el panel se aleja del usuario. Si el objeto está más " +
                 "lejos, el panel se acerca para seguir siendo legible.")]
        public float DistanciaMaximaAlUsuario = 2.2f;
        [Tooltip("Suavizado del movimiento del panel. 0 = salto instantáneo.")]
        [Range(0f, 20f)] public float Suavizado = 8f;

        [Header("Línea de unión")]
        public Color ColorLinea = new Color(1f, 0.78f, 0.15f, 0.85f);
        public float GrosorLinea = 0.006f;

        private Transform _panel;
        private Transform _camara;
        private Transform _objetivo;
        private LineRenderer _linea;
        private Vector3 _posicionDeseada;
        private bool _colocadoAlMenosUnaVez;

        public void Initialize(Canvas canvasMundo)
        {
            _panel = canvasMundo.transform;
            _camara = Camera.main != null ? Camera.main.transform : null;
            canvasMundo.worldCamera = Camera.main;
            ConstruirLinea();
            _panel.gameObject.SetActive(false);
        }

        private void ConstruirLinea()
        {
            var go = new GameObject("~LineaUnionPanel");
            go.transform.SetParent(transform, false);
            _linea = go.AddComponent<LineRenderer>();

            var shader = Shader.Find("Universal Render Pipeline/Unlit") ?? Shader.Find("Unlit/Color");
            var mat = new Material(shader);
            mat.SetColor(Shader.PropertyToID("_BaseColor"), ColorLinea);
            mat.SetColor(Shader.PropertyToID("_Color"), ColorLinea);
            _linea.material = mat;

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

            // El primer encuadre se coloca de golpe; a partir de ahí se suaviza. Si no, el panel
            // "viaja" desde donde estuviera el anterior, lo que despista.
            if (!_colocadoAlMenosUnaVez || Suavizado <= 0f)
            {
                _panel.position = _posicionDeseada;
                _colocadoAlMenosUnaVez = true;
            }
            else
            {
                _panel.position = Vector3.Lerp(_panel.position, _posicionDeseada,
                                               1f - Mathf.Exp(-Suavizado * Time.deltaTime));
            }

            // Orientar el panel hacia el usuario (billboard). Se fuerza el eje vertical del mundo
            // para que el texto nunca aparezca inclinado aunque el usuario ladee la cabeza.
            Vector3 haciaUsuario = _panel.position - _camara.position;
            haciaUsuario.y = 0f;
            if (haciaUsuario.sqrMagnitude > 0.0001f)
                _panel.rotation = Quaternion.LookRotation(haciaUsuario.normalized, Vector3.up);

            ActualizarLinea();
        }

        private void CalcularPosicionDeseada()
        {
            Vector3 centroObjeto = CentroDe(_objetivo);

            // Lado hacia el que se desplaza el panel: el derecho respecto a la vista del usuario.
            Vector3 haciaObjeto = centroObjeto - _camara.position;
            haciaObjeto.y = 0f;
            if (haciaObjeto.sqrMagnitude < 0.0001f) haciaObjeto = Vector3.forward;
            haciaObjeto.Normalize();

            Vector3 derecha = Vector3.Cross(Vector3.up, haciaObjeto).normalized;
            float radio = RadioAproximado(_objetivo);

            Vector3 destino = centroObjeto + derecha * (radio + SeparacionLateral);

            // Si el objeto está lejos, el panel se trae hacia el usuario por la misma línea de
            // visión: un panel a diez metros sería ilegible por mucho que esté bien colocado.
            float distancia = Vector3.Distance(_camara.position, destino);
            if (distancia > DistanciaMaximaAlUsuario)
            {
                Vector3 direccion = (destino - _camara.position).normalized;
                destino = _camara.position + direccion * DistanciaMaximaAlUsuario;
            }

            _posicionDeseada = destino;
        }

        private void ActualizarLinea()
        {
            if (_linea == null) return;

            // La línea sale del borde izquierdo del panel (el lado que mira al objeto) y no de su
            // centro, para que no se vea atravesando la ficha.
            Vector3 origen = _panel.position - _panel.right * (AnchoPanelEnMetros() * 0.5f);
            _linea.SetPosition(0, origen);
            _linea.SetPosition(1, CentroDe(_objetivo));
            _linea.widthMultiplier = GrosorLinea;
        }

        private float AnchoPanelEnMetros()
        {
            var rt = _panel as RectTransform;
            return rt != null ? rt.rect.width * _panel.lossyScale.x : 0.5f;
        }

        private static Vector3 CentroDe(Transform t)
        {
            if (t == null) return Vector3.zero;
            var r = t.GetComponentInChildren<Renderer>();
            return r != null ? r.bounds.center : t.position;
        }

        private static float RadioAproximado(Transform t)
        {
            if (t == null) return 0.2f;
            var r = t.GetComponentInChildren<Renderer>();
            return r != null ? r.bounds.extents.magnitude : 0.2f;
        }
    }
}
