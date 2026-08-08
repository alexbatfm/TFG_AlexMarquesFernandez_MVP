using System;
using UnityEngine;

namespace DigitalTwin.Visual
{
    /// <summary>
    /// Orienta la luz direccional según la posición real del Sol para las coordenadas del
    /// edificio, la fecha y la hora.
    ///
    /// POR QUÉ ESTO EN UN GEMELO DIGITAL
    /// No es un adorno. El modelo IFC viene georreferenciado, y aprovechar ese dato hace que la
    /// iluminación de la escena se corresponda con la que un operario encontraría al entrar al
    /// edificio a esa misma hora: las salas orientadas a poniente se ven contra la luz por la
    /// tarde, y las interiores quedan en penumbra. Es una forma barata de reforzar la
    /// correspondencia entre gemelo y realidad, que es justamente lo que el sistema persigue.
    ///
    /// DATOS QUE VIENEN DEL PROPIO MODELO
    /// El fichero IFC declara en su IfcSite la latitud y longitud del emplazamiento:
    ///
    ///     IFCSITE(...,(40,25,13,9643),(-3,-42,-20,-772056),0.,...)
    ///
    /// que en el formato compuesto de IFC (grados, minutos, segundos, millonésimas de segundo)
    /// equivale a 40,42028 N / -3,70577 O. El contexto geométrico declara además el norte
    /// verdadero como IFCDIRECTION((6.12e-17, 1.0)), es decir sin rotación respecto al norte
    /// geográfico, por lo que el eje norte del modelo coincide con el real y no hace falta
    /// corregir el acimut. Si se cambiara de modelo habría que revisar ambos datos.
    ///
    /// AVISO SOBRE ESTAS COORDENADAS
    /// Corresponden al centro de Madrid, pese a que el fichero se llame "ibimVLC" y el modelo se
    /// haya venido llamando OrigenVLC. Puede ser que el emplazamiento nunca se fijara en Revit y
    /// quedara el valor por defecto. Antes de dar el dato por bueno en la memoria conviene
    /// confirmarlo; mientras tanto los valores son editables desde el Inspector.
    ///
    /// ALGORITMO
    /// Se emplea la aproximación de la NOAA para la posición solar, con precisión del orden del
    /// minuto de arco. Es holgadamente suficiente: aquí se busca que la luz entre por la ventana
    /// correcta, no hacer cálculos astronómicos.
    /// </summary>
    [ExecuteAlways]
    public class SolarLighting : MonoBehaviour
    {
        [Header("Emplazamiento (leído del IfcSite del modelo)")]
        [Tooltip("Latitud en grados decimales. Positiva al norte.")]
        public double Latitud = 40.42028;
        [Tooltip("Longitud en grados decimales. Positiva al este.")]
        public double Longitud = -3.70577;
        [Tooltip("Desfase horario respecto a UTC. España peninsular: 1 en invierno, 2 en verano.")]
        public int DesfaseUTC = 2;
        [Tooltip("Rotación del norte del modelo respecto al norte geográfico, en grados. El IFC " +
                 "de este proyecto declara norte verdadero sin rotación, así que 0.")]
        public float RotacionNorte = 0f;

        [Header("Momento")]
        [Tooltip("Si está activo usa la fecha y hora del sistema. Si no, los valores de abajo.")]
        public bool UsarHoraReal = false;

        [Tooltip("Desactivado por defecto a propósito: con la hora real, una demo por la noche " +
                 "deja el edificio a oscuras y parece que algo falla. Con estos valores fijos la " +
                 "escena queda siempre bien iluminada y el efecto sigue siendo demostrable " +
                 "moviendo la hora.")]
        [Range(0f, 23.99f)] public float HoraSimulada = 12f;
        [Range(1, 12)] public int MesSimulado = 6;
        [Range(1, 31)] public int DiaSimulado = 21;

        [Header("Aspecto")]
        public Gradient ColorSegunAltura;
        [Tooltip("Intensidad de la luz con el Sol en lo alto.")]
        public float IntensidadMaxima = 1.2f;
        [Tooltip("Intensidad mínima, de noche. No se baja a cero para que el interior siga " +
                 "siendo legible: un gemelo digital debe poder consultarse a cualquier hora.")]
        public float IntensidadNocturna = 0.15f;

        [Tooltip("Recalcular el cielo procedural y la luz ambiental al moverse el Sol. Sin esto, " +
                 "la luz directa cambia pero el cielo y el rebote de luz se quedan congelados.")]
        public bool ActualizarAmbiente = true;

        [Tooltip("Grados que debe girar el Sol para recalcular el ambiente. Recalcularlo cada " +
                 "fotograma es caro y visualmente indistinguible.")]
        public float UmbralActualizacionAmbiente = 1.5f;

        private Quaternion _rotacionUltimoAmbiente = Quaternion.identity;
        private bool _ambienteCalculadoAlgunaVez;

        private Light _luz;

        /// <summary>Altura del Sol sobre el horizonte, en grados. Negativa de noche.</summary>
        public double AlturaSolar { get; private set; }
        /// <summary>Acimut del Sol en grados desde el norte, en sentido horario.</summary>
        public double AcimutSolar { get; private set; }
        public bool EsDeNoche => AlturaSolar <= 0.0;

        private void OnEnable()
        {
            _luz = GetComponent<Light>();
            if (_luz == null) _luz = gameObject.AddComponent<Light>();
            _luz.type = LightType.Directional;
            Aplicar();
        }

        private void Update()
        {
            // En modo edición se refresca continuamente para poder mover la hora y ver el efecto;
            // en ejecución solo si se pide hora real, que cambia sola.
            if (!Application.isPlaying || UsarHoraReal) Aplicar();
        }

        /// <summary>Recalcula la posición del Sol y la aplica a la luz.</summary>
        public void Aplicar()
        {
            if (_luz == null) return;

            DateTime momento = UsarHoraReal
                ? DateTime.Now
                : new DateTime(DateTime.Now.Year, MesSimulado,
                               Mathf.Clamp(DiaSimulado, 1, DateTime.DaysInMonth(DateTime.Now.Year, MesSimulado)))
                  .AddHours(HoraSimulada);

            CalcularPosicionSolar(momento, Latitud, Longitud, DesfaseUTC,
                                  out double altura, out double acimut);
            AlturaSolar = altura;
            AcimutSolar = acimut;

            // De acimut/altura a un vector en el sistema de Unity: +Z norte, +X este, +Y arriba.
            float a = (float)(acimut + RotacionNorte) * Mathf.Deg2Rad;
            float h = (float)altura * Mathf.Deg2Rad;
            Vector3 haciaElSol = new Vector3(
                Mathf.Sin(a) * Mathf.Cos(h),
                Mathf.Sin(h),
                Mathf.Cos(a) * Mathf.Cos(h));

            // La luz direccional viaja hacia donde mira, o sea en sentido contrario al Sol.
            if (haciaElSol.sqrMagnitude > 0.0001f)
                transform.rotation = Quaternion.LookRotation(-haciaElSol.normalized, Vector3.up);

            // Interpolación suave alrededor del horizonte para que amanecer y ocaso no sean un
            // salto brusco de luz.
            float factor = Mathf.Clamp01((float)(altura + 6.0) / 12.0f);
            _luz.intensity = Mathf.Lerp(IntensidadNocturna, IntensidadMaxima, factor);

            if (ColorSegunAltura != null && ColorSegunAltura.colorKeys.Length > 0)
                _luz.color = ColorSegunAltura.Evaluate(factor);

            ActualizarCieloYAmbiente();
        }

        /// <summary>
        /// Recalcula el cielo procedural y la iluminación ambiental derivada de él.
        ///
        /// Por qué hace falta: el cielo procedural de Unity dibuja el disco solar en la posición
        /// de la luz direccional designada como fuente, y la iluminación ambiental del modo
        /// "Skybox" se obtiene integrando ese cielo. Pero ninguna de las dos cosas se recalcula
        /// sola al girar la luz. Sin esta llamada, la luz directa se movería mientras el cielo y
        /// el rebote de luz se quedan congelados en la orientación que tuvieran al cargar la
        /// escena, que es peor que no mover nada: la sombra apunta en una dirección y el cielo
        /// dice otra.
        ///
        /// Se limita por umbral de giro porque el recálculo integra el cielo completo y es caro.
        /// Hacerlo cada fotograma tendría un coste perfectamente perceptible en un visor
        /// autónomo, y el resultado sería visualmente indistinguible: el Sol se desplaza unos
        /// 15 grados por hora simulada.
        /// </summary>
        private void ActualizarCieloYAmbiente()
        {
            if (!ActualizarAmbiente) return;

            if (_ambienteCalculadoAlgunaVez &&
                Quaternion.Angle(_rotacionUltimoAmbiente, transform.rotation) < UmbralActualizacionAmbiente)
                return;

            _rotacionUltimoAmbiente = transform.rotation;
            _ambienteCalculadoAlgunaVez = true;

            // Designar esta luz como fuente del cielo procedural. Si no se hace, Unity elige la
            // direccional más brillante, que normalmente será esta, pero conviene no depender de
            // ello en una escena donde puede haber más luces.
            RenderSettings.sun = _luz;
            DynamicGI.UpdateEnvironment();
        }

        /// <summary>
        /// Posición solar por la aproximación de la NOAA. Devuelve altura sobre el horizonte y
        /// acimut desde el norte en sentido horario, ambos en grados.
        /// </summary>
        public static void CalcularPosicionSolar(DateTime horaLocal, double latitud, double longitud,
                                                 int desfaseUTC, out double altura, out double acimut)
        {
            double horaDecimal = horaLocal.Hour + horaLocal.Minute / 60.0 + horaLocal.Second / 3600.0;
            int diaDelAnio = horaLocal.DayOfYear;
            bool bisiesto = DateTime.IsLeapYear(horaLocal.Year);

            // Ángulo fraccional del año, en radianes.
            double gamma = 2.0 * Math.PI / (bisiesto ? 366.0 : 365.0) *
                           (diaDelAnio - 1 + (horaDecimal - 12.0) / 24.0);

            // Ecuación del tiempo, en minutos: corrige la diferencia entre el mediodía solar
            // real y el del reloj, que a lo largo del año llega a ser de un cuarto de hora.
            double ecuacionTiempo = 229.18 * (0.000075
                + 0.001868 * Math.Cos(gamma)
                - 0.032077 * Math.Sin(gamma)
                - 0.014615 * Math.Cos(2 * gamma)
                - 0.040849 * Math.Sin(2 * gamma));

            // Declinación solar, en radianes.
            double declinacion = 0.006918
                - 0.399912 * Math.Cos(gamma)
                + 0.070257 * Math.Sin(gamma)
                - 0.006758 * Math.Cos(2 * gamma)
                + 0.000907 * Math.Sin(2 * gamma)
                - 0.002697 * Math.Cos(3 * gamma)
                + 0.00148 * Math.Sin(3 * gamma);

            double desviacion = ecuacionTiempo + 4.0 * longitud - 60.0 * desfaseUTC;
            double horaSolarVerdadera = horaDecimal * 60.0 + desviacion;

            // Ángulo horario: 0 en el mediodía solar, negativo por la mañana.
            double anguloHorario = (horaSolarVerdadera / 4.0) - 180.0;
            double ah = anguloHorario * Math.PI / 180.0;
            double lat = latitud * Math.PI / 180.0;

            double cosCenit = Math.Sin(lat) * Math.Sin(declinacion) +
                              Math.Cos(lat) * Math.Cos(declinacion) * Math.Cos(ah);
            cosCenit = Math.Max(-1.0, Math.Min(1.0, cosCenit));
            double cenit = Math.Acos(cosCenit);
            altura = 90.0 - cenit * 180.0 / Math.PI;

            double senoCenit = Math.Sin(cenit);
            if (Math.Abs(senoCenit) < 1e-9)
            {
                acimut = 180.0;
                return;
            }

            double cosAcimut = (Math.Sin(lat) * Math.Cos(cenit) - Math.Sin(declinacion)) /
                               (Math.Cos(lat) * senoCenit);
            cosAcimut = Math.Max(-1.0, Math.Min(1.0, cosAcimut));
            acimut = 180.0 - Math.Acos(cosAcimut) * 180.0 / Math.PI;

            // Por la tarde el Sol está al oeste: el acimut se refleja respecto al meridiano.
            if (anguloHorario > 0.0) acimut = 360.0 - acimut;
            acimut = (acimut + 360.0) % 360.0;
        }
    }
}
