using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.XR;

namespace DigitalTwin.MR
{
    /// <summary>
    /// Diagnóstico de la entrada de realidad extendida: vuelca al registro qué dispositivos ve
    /// cada una de las dos capas de entrada que conviven en Unity.
    ///
    /// Por qué hace falta. Existen dos formas de leer los mandos, y no siempre están alimentadas a
    /// la vez: la interfaz clásica <c>UnityEngine.XR.InputDevices</c>, que consulta al subsistema
    /// de realidad extendida, y el sistema de entrada nuevo, donde los proveedores registran los
    /// mandos como dispositivos con su propia disposición de botones. El registro del visor mostró
    /// que el paquete del fabricante inscribe los mandos con la interfaz <c>XRInputV1</c>, que es
    /// la del sistema nuevo, mientras que <see cref="MRControllerRig"/> los busca por la clásica.
    ///
    /// Si la primera capa no devuelve nada, el rig descarta ambas manos, oculta los dos rayos y no
    /// ocurre absolutamente nada, sin un solo error que lo explique. Este volcado distingue ese
    /// caso de los demás en una sola ejecución, en lugar de deducirlo a base de compilaciones.
    ///
    /// Se repite varias veces durante los primeros segundos a propósito: los mandos no siempre
    /// están registrados en el instante en que arranca la escena --- pueden estar dormidos, o
    /// tardar en emparejarse --- y un único volcado en el primer fotograma daría un negativo
    /// engañoso.
    ///
    /// Todo va como aviso y no como mensaje informativo porque en las compilaciones que no son de
    /// desarrollo Unity no envía los mensajes informativos al registro del dispositivo. Es la
    /// razón por la que las primeras trazas de este proyecto no aparecían por ninguna parte.
    /// </summary>
    public class MRInputDiagnostics : MonoBehaviour
    {
        /// <summary>Momentos, en segundos desde el arranque, en que se vuelca el estado.</summary>
        private static readonly float[] Instantes = { 0f, 2f, 5f, 10f };

        private void Start()
        {
            StartCoroutine(VolcarPeriodicamente());
        }

        private IEnumerator VolcarPeriodicamente()
        {
            float anterior = 0f;
            foreach (float t in Instantes)
            {
                float espera = t - anterior;
                if (espera > 0f) yield return new WaitForSeconds(espera);
                anterior = t;

                Volcar(t);
            }
        }

        private void Volcar(float segundos)
        {
            var sb = new StringBuilder();
            sb.Append("[DigitalTwin][AR][Diag] t=").Append(segundos.ToString("0")).Append("s  ");

            // --- Capa clásica: la que usa MRControllerRig -----------------------------------
            var dispositivos = new List<InputDevice>();
            InputDevices.GetDevices(dispositivos);

            sb.Append("InputDevices=").Append(dispositivos.Count);
            if (dispositivos.Count > 0)
            {
                sb.Append(" [");
                for (int i = 0; i < dispositivos.Count; i++)
                {
                    var d = dispositivos[i];
                    if (i > 0) sb.Append(" | ");
                    sb.Append(d.name).Append(" caracteristicas=").Append(d.characteristics);
                }
                sb.Append("]");
            }

            // Consulta por nodo, que es exactamente como pregunta el rig. Puede haber dispositivos
            // en la lista general y aun así no resolverse por nodo, y esa distinción importa.
            var derecho = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            var izquierdo = InputDevices.GetDeviceAtXRNode(XRNode.LeftHand);
            var cabeza = InputDevices.GetDeviceAtXRNode(XRNode.Head);
            sb.Append("  porNodo: dcha=").Append(derecho.isValid ? derecho.name : "NO VALIDO")
              .Append(", izda=").Append(izquierdo.isValid ? izquierdo.name : "NO VALIDO")
              .Append(", cabeza=").Append(cabeza.isValid ? cabeza.name : "NO VALIDO");

            // --- Capa nueva: donde el paquete del fabricante inscribe los mandos --------------
#if ENABLE_INPUT_SYSTEM
            int total = 0;
            var nombres = new StringBuilder();
            foreach (var d in UnityEngine.InputSystem.InputSystem.devices)
            {
                total++;
                if (nombres.Length > 0) nombres.Append(" | ");
                nombres.Append(d.name).Append(" (").Append(d.layout).Append(")");
            }
            sb.Append("  InputSystem=").Append(total);
            if (total > 0) sb.Append(" [").Append(nombres).Append("]");
#else
            sb.Append("  InputSystem=no compilado");
#endif

            Debug.LogWarning(sb.ToString());
        }

        /// <summary>Instancia viva, para no duplicar el diagnóstico si una escena se carga sin
        /// que el barrido de la vuelta al selector lo haya destruido. La comparación con null
        /// usa el operador de Unity, así que un objeto destruido cuenta como ausente.</summary>
        private static MRInputDiagnostics _instancia;

        /// <summary>
        /// Punto de entrada propio, independiente del arranque de la escena.
        ///
        /// Se registra por separado a conciencia: si el diagnóstico dependiera de
        /// <see cref="MRDigitalTwinBootstrap"/>, un fallo en ese arranque se llevaría por delante
        /// justamente la herramienta que sirve para diagnosticarlo.
        ///
        /// La suscripción a sceneLoaded existe por lo que midió el registro del visor del
        /// 17-08: la vuelta al selector de modo (ronda 9) barre los objetos persistentes con
        /// prefijo «~» —este incluido— y recarga la escena, pero [RuntimeInitializeOnLoadMethod]
        /// corre una sola vez por proceso. Resultado medido: volcados en t=0/2/5/10 s del primer
        /// arranque y NI UNO tras las tres recargas de aquella sesión. Cada rearranque crea su
        /// propio rig de mandos, así que cada uno merece su ventana de diagnóstico.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Instalar()
        {
            // AfterSceneLoad corre después de cargarse la primera escena, de modo que este
            // manejador solo dispara en las cargas posteriores (las vueltas al selector).
            UnityEngine.SceneManagement.SceneManager.sceneLoaded +=
                (escena, modo) => Crear($"recarga de la escena '{escena.name}'");
            Crear(null);
        }

        private static void Crear(string motivoDeReinstalacion)
        {
            if (_instancia != null)
            {
                Debug.LogWarning("[DigitalTwin][AR][Diag] El diagnostico de entrada anterior " +
                                 $"sigue vivo al cargar escena ({motivoDeReinstalacion}); no se duplica.");
                return;
            }

            var go = new GameObject("~DiagnosticoEntradaAR");
            Object.DontDestroyOnLoad(go);
            _instancia = go.AddComponent<MRInputDiagnostics>();

            if (motivoDeReinstalacion != null)
            {
                Debug.LogWarning("[DigitalTwin][AR][Diag] Diagnostico de entrada reinstalado tras " +
                                 $"{motivoDeReinstalacion} (el anterior cayo con el barrido de la " +
                                 "sesion); volcados en t=0/2/5/10 s desde ahora.");
            }
        }
    }
}
