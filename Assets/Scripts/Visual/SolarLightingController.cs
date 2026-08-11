using UnityEngine;

namespace DigitalTwin.Visual
{
    /// <summary>
    /// Interruptor de la iluminación solar por georreferenciación.
    ///
    /// POR QUE EXISTE ESTE COMPONENTE APARTE
    ///
    /// <see cref="SolarLighting"/> sabe calcular la posición del Sol, pero nadie lo instanciaba: el
    /// componente estaba escrito y no llegaba a colocarse en ninguna escena, de modo que la luz
    /// direccional se quedaba tal como venía del modelo --- a pleno mediodía, fuese la hora que
    /// fuese. Esa es la razón de que la función pareciera rota: no fallaba el cálculo, es que no se
    /// ejecutaba.
    ///
    /// Este componente lo monta sobre la luz direccional de la escena, y lo hace conmutable.
    ///
    /// POR QUE ARRANCA DESACTIVADO
    ///
    /// Porque el edificio del modelo no tiene iluminación artificial. Con la hora real, quien abra
    /// el gemelo digital de noche se encuentra un interior a oscuras y no ve nada, lo cual es un
    /// mal comportamiento por defecto y un riesgo en una demostración en directo. La sincronización
    /// solar es una prestación que se activa a voluntad, no el modo normal de trabajo.
    ///
    /// Al desactivarse se restaura exactamente la iluminación original de la escena, guardada al
    /// arrancar. Sin eso, apagar el interruptor dejaría la luz en la última posición calculada, que
    /// es peor que no haberlo tocado.
    /// </summary>
    public class SolarLightingController : MonoBehaviour
    {
        private const string ClavePreferencia = "dt.solar.activado";

        private Light _luz;
        private SolarLighting _solar;

        // Estado original de la luz de la escena, para poder devolverlo tal cual.
        private Quaternion _rotacionOriginal;
        private float _intensidadOriginal;
        private Color _colorOriginal;
        private Color _ambienteOriginal;
        private bool _guardado;

        public bool Activado { get; private set; }

        /// <summary>Descripción legible del estado, para la interfaz de ajustes.</summary>
        public string Descripcion
        {
            get
            {
                if (!Activado) return "fija";
                if (_solar == null) return "activada";
                return _solar.EsDeNoche ? "hora real (es de noche)" : "hora real";
            }
        }

        public static SolarLightingController Instancia { get; private set; }

        /// <summary>
        /// Monta el control sobre la luz direccional de la escena. Devuelve null si no hay ninguna,
        /// en cuyo caso simplemente no existe la opción.
        /// </summary>
        public static SolarLightingController Crear()
        {
            Light direccional = null;
            foreach (var l in FindObjectsByType<Light>(FindObjectsSortMode.None))
            {
                if (l.type != LightType.Directional) continue;
                direccional = l;
                break;
            }

            if (direccional == null)
            {
                Debug.LogWarning("[DigitalTwin] No hay luz direccional en la escena; la sincronizacion " +
                                 "solar no se activa.");
                return null;
            }

            var ctrl = direccional.gameObject.AddComponent<SolarLightingController>();
            Instancia = ctrl;
            return ctrl;
        }

        private void Awake()
        {
            _luz = GetComponent<Light>();
            GuardarOriginal();

            // El componente de calculo se anade desactivado: al habilitarse ejecuta su OnEnable y
            // toma el control de la luz, y al deshabilitarse deja de tocarla.
            _solar = GetComponent<SolarLighting>();
            if (_solar == null) _solar = gameObject.AddComponent<SolarLighting>();
            _solar.UsarHoraReal = true;   // si se activa, que sea con la hora de verdad
            _solar.enabled = false;

            // Se respeta lo que el usuario dejara elegido, pero el valor de fabrica es apagado.
            Aplicar(PlayerPrefs.GetInt(ClavePreferencia, 0) == 1);
        }

        private void GuardarOriginal()
        {
            if (_guardado || _luz == null) return;
            _rotacionOriginal = transform.rotation;
            _intensidadOriginal = _luz.intensity;
            _colorOriginal = _luz.color;
            _ambienteOriginal = RenderSettings.ambientLight;
            _guardado = true;
        }

        public void Alternar()
        {
            Aplicar(!Activado);
            PlayerPrefs.SetInt(ClavePreferencia, Activado ? 1 : 0);
        }

        private void Aplicar(bool activar)
        {
            Activado = activar;
            if (_solar != null) _solar.enabled = activar;

            if (!activar) RestaurarOriginal();

            Debug.Log($"[DigitalTwin] Sincronizacion solar {(activar ? "activada" : "desactivada")}.");
        }

        private void RestaurarOriginal()
        {
            if (!_guardado || _luz == null) return;
            transform.rotation = _rotacionOriginal;
            _luz.intensity = _intensidadOriginal;
            _luz.color = _colorOriginal;
            RenderSettings.ambientLight = _ambienteOriginal;
        }
    }
}
