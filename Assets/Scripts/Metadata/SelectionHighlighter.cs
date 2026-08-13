using System.Collections.Generic;
using UnityEngine;

namespace DigitalTwin.Metadata
{
    /// <summary>
    /// Resalta visualmente el elemento seleccionado, para que se vea de qué objeto son los datos
    /// que muestra el panel de metadatos.
    ///
    /// Por qué hace falta: el panel no está pegado al objeto, así que sin ninguna marca visual el
    /// operario puede estar leyendo la ficha de una tubería mientras mira otra idéntica al lado.
    /// En un edificio con decenas de elementos repetidos (sensores, luminarias, puertas) esa
    /// ambigüedad es constante.
    ///
    /// Estrategia: se dibuja una caja de aristas alrededor del objeto y, además, se le aplica un
    /// tinte de color. Se usan las dos cosas a la vez a propósito, porque cada una cubre el
    /// punto débil de la otra:
    ///
    ///   - La caja de aristas (LineRenderer) funciona **siempre**, con cualquier shader y
    ///     cualquier material, porque no depende del material del objeto. Además se ve a
    ///     distancia y a través del desorden visual del passthrough en Realidad Mixta.
    ///   - El tinte de color depende de que el shader tenga la propiedad esperada, pero cuando
    ///     funciona identifica el objeto de forma mucho más inmediata que un contorno.
    ///
    /// Se descartó un contorno tipo *outline* real (el efecto de borde luminoso que rodea la
    /// silueta) porque en URP exige añadir un Renderer Feature al pipeline de render, es decir,
    /// tocar la configuración global de renderizado del proyecto. Para el beneficio que aporta
    /// frente a la caja de aristas, no compensa el riesgo.
    ///
    /// El tinte se aplica con <see cref="MaterialPropertyBlock"/> y no cambiando el material:
    /// asignar un material por código crea una copia por objeto que Unity no libera hasta salir
    /// de Play, y con cientos de elementos seleccionables eso es una fuga de memoria lenta pero
    /// segura. El bloque de propiedades no instancia nada.
    /// </summary>
    public class SelectionHighlighter : MonoBehaviour
    {
        [Header("Aspecto")]
        public Color ColorResaltado = new Color(1f, 0.78f, 0.15f, 1f);
        [Tooltip("Cuánto se mezcla el color de resaltado con el color original del material (0-1).")]
        [Range(0f, 1f)] public float IntensidadTinte = 0.55f;
        [Tooltip("Grosor de las aristas de la caja, en metros.")]
        public float GrosorAristas = 0.012f;
        [Tooltip("Margen que se deja entre la geometría y la caja, en metros.")]
        public float MargenCaja = 0.02f;

        // Nombres de propiedad de color de los shaders de URP (_BaseColor) y del pipeline
        // integrado (_Color). Se prueban ambos porque los materiales que llegan del .glb vía
        // glTFast pueden usar cualquiera de los dos según cómo se haya importado el modelo.
        private static readonly int IdBaseColor = Shader.PropertyToID("_BaseColor");
        private static readonly int IdColor = Shader.PropertyToID("_Color");

        private readonly List<Renderer> _renderersTenidos = new List<Renderer>();
        private MaterialPropertyBlock _bloque;
        private LineRenderer _caja;
        private Transform _objetivoActual;

        /// <summary>Objeto resaltado ahora mismo, o null si no hay ninguno.</summary>
        public Transform Objetivo => _objetivoActual;

        private void Awake()
        {
            _bloque = new MaterialPropertyBlock();
            ConstruirCaja();
        }

        /// <summary>
        /// La caja se construye una sola vez y se reutiliza moviendo sus vértices. Crear y
        /// destruir un LineRenderer en cada selección generaría basura constante en un flujo
        /// donde el operario va clicando elementos sin parar.
        /// </summary>
        private void ConstruirCaja()
        {
            var go = new GameObject("~CajaSeleccion");
            go.transform.SetParent(transform, false);
            _caja = go.AddComponent<LineRenderer>();

            // Shader sin iluminación: la caja debe verse igual bajo cualquier luz, y en MR el
            // passthrough no aporta iluminación a la geometría virtual.
            // Antes se hacia new Material(Shader.Find(...)) directamente. En una compilacion
            // Shader.Find devuelve null para sombreadores no incluidos, y el constructor lanzaba
            // ArgumentNullException, que abortaba el arranque entero de Realidad Aumentada. Ahora
            // la creacion no puede lanzar: si no hay sombreador, este adorno simplemente no se
            // dibuja. Ver DigitalTwin.Core.RuntimeMaterials.
            var mat = DigitalTwin.Core.RuntimeMaterials.CrearSinIluminacion(ColorResaltado);
            if (mat != null) _caja.material = mat;

            _caja.useWorldSpace = true;
            _caja.loop = false;
            _caja.widthMultiplier = GrosorAristas;
            _caja.numCornerVertices = 0;
            _caja.numCapVertices = 0;
            _caja.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _caja.receiveShadows = false;
            _caja.alignment = LineAlignment.View; // siempre de frente a la cámara, también en MR
            _caja.positionCount = 0;
            go.SetActive(false);
        }

        /// <summary>Resalta el objeto indicado, quitando el resaltado anterior si lo hubiera.</summary>
        public void Resaltar(Transform objetivo)
        {
            Limpiar();
            if (objetivo == null) return;

            _objetivoActual = objetivo;
            AplicarTinte(objetivo);
            DibujarCaja(objetivo);
        }

        /// <summary>Quita el resaltado actual y devuelve los materiales a su aspecto original.</summary>
        public void Limpiar()
        {
            foreach (var r in _renderersTenidos)
            {
                if (r == null) continue;
                // Un bloque vacío devuelve el material a sus valores propios: no hace falta
                // guardar y restaurar los colores originales uno por uno.
                r.SetPropertyBlock(null);
            }
            _renderersTenidos.Clear();

            if (_caja != null) _caja.gameObject.SetActive(false);
            _objetivoActual = null;
        }

        private void AplicarTinte(Transform objetivo)
        {
            var renderers = objetivo.GetComponentsInChildren<Renderer>();
            foreach (var r in renderers)
            {
                if (r == null || r is LineRenderer) continue;

                r.GetPropertyBlock(_bloque);

                // Se mezcla con el color actual en vez de sustituirlo para que el objeto siga
                // siendo reconocible (una puerta resaltada debe seguir pareciendo una puerta).
                Color baseActual = r.sharedMaterial != null && r.sharedMaterial.HasProperty(IdBaseColor)
                    ? r.sharedMaterial.GetColor(IdBaseColor)
                    : Color.white;
                Color mezcla = Color.Lerp(baseActual, ColorResaltado, IntensidadTinte);
                mezcla.a = baseActual.a;

                _bloque.SetColor(IdBaseColor, mezcla);
                _bloque.SetColor(IdColor, mezcla);
                r.SetPropertyBlock(_bloque);

                _renderersTenidos.Add(r);
            }
        }

        /// <summary>
        /// Dibuja las 12 aristas del volumen que envuelve al objeto recorriéndolas en una sola
        /// polilínea continua (con algún tramo repetido), en vez de crear doce LineRenderer.
        /// </summary>
        private void DibujarCaja(Transform objetivo)
        {
            if (!CalcularVolumen(objetivo, out Bounds b)) return;

            b.Expand(MargenCaja * 2f);
            Vector3 c = b.center, e = b.extents;

            // Los ocho vértices del volumen.
            Vector3 v000 = c + new Vector3(-e.x, -e.y, -e.z);
            Vector3 v100 = c + new Vector3(+e.x, -e.y, -e.z);
            Vector3 v110 = c + new Vector3(+e.x, +e.y, -e.z);
            Vector3 v010 = c + new Vector3(-e.x, +e.y, -e.z);
            Vector3 v001 = c + new Vector3(-e.x, -e.y, +e.z);
            Vector3 v101 = c + new Vector3(+e.x, -e.y, +e.z);
            Vector3 v111 = c + new Vector3(+e.x, +e.y, +e.z);
            Vector3 v011 = c + new Vector3(-e.x, +e.y, +e.z);

            // Recorrido que cubre las 12 aristas sin levantar el trazo.
            Vector3[] ruta =
            {
                v000, v100, v110, v010, v000,   // cara trasera
                v001, v101, v111, v011, v001,   // salto a la cara delantera y recorrido
                v011, v010, v110, v111,         // aristas laterales superiores
                v101, v100                      // aristas laterales inferiores
            };

            _caja.positionCount = ruta.Length;
            _caja.SetPositions(ruta);
            _caja.widthMultiplier = GrosorAristas;
            _caja.gameObject.SetActive(true);
        }

        /// <summary>
        /// Volumen envolvente en coordenadas de mundo. Se agregan los volúmenes de todos los
        /// Renderer del objeto y sus hijos porque un elemento IFC puede estar troceado en varias
        /// mallas (por material, por ejemplo), y encuadrar solo la primera dejaría fuera parte
        /// de la geometría.
        /// </summary>
        private static bool CalcularVolumen(Transform objetivo, out Bounds bounds)
        {
            bounds = default;
            bool encontrado = false;

            foreach (var r in objetivo.GetComponentsInChildren<Renderer>())
            {
                if (r == null || r is LineRenderer || !r.enabled) continue;
                if (!encontrado) { bounds = r.bounds; encontrado = true; }
                else bounds.Encapsulate(r.bounds);
            }
            return encontrado;
        }
    }
}
