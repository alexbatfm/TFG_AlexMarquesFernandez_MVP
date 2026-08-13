using System;
using UnityEngine;
using UnityEngine.UI;

namespace DigitalTwin.MR
{
    /// <summary>
    /// Los dos modos de la versión de Realidad Aumentada. No son dos ajustes del mismo programa:
    /// cambian el modelo de entrada (andar frente a desplazarse por nodos), el fondo, el papel de
    /// la geometría y el conjunto de colisionadores. Por eso la elección es al arrancar y no un
    /// conmutador en caliente (ver docs/roadmap/DISENO-modo-anclado.md).
    /// </summary>
    public enum ModoAR
    {
        /// <summary>Revisión remota: el modelo ante el usuario, desplazamiento por el grafo de
        /// puntos de navegación. La transparencia se apaga al entrar.</summary>
        NavegacionPorNodos,

        /// <summary>En obra: el modelo superpuesto al edificio real como oclusor invisible,
        /// desplazamiento andando. La transparencia permanece encendida.</summary>
        Anclado
    }

    /// <summary>
    /// Selector de modo que se muestra al arrancar, antes de montar el gemelo digital, con la
    /// transparencia ya activa para no dejar al usuario eligiendo dentro de un vacío negro.
    ///
    /// Son dos tarjetas flotantes en el espacio, señalables con el rayo del mando y activables
    /// con el gatillo. Se usan volúmenes de colisión propios consultados con
    /// <c>Collider.Raycast</c> —el mismo patrón que el panel de metadatos y los indicadores de
    /// destino— y NO botones de interfaz clásicos: la interacción de puntero sobre lienzos
    /// todavía no existe en el visor, mientras que el gesto de apuntar-y-disparar contra un
    /// volumen es exactamente el que ya está verificado en hardware.
    /// </summary>
    public class MRModeSelector : MonoBehaviour
    {
        private const float DistanciaAlUsuario = 1.4f;
        private const float SeparacionLateral = 0.34f;
        private const float SegundosAntesDeAvisarSinMando = 20f;

        private static readonly Color FondoNormal = new Color(0.07f, 0.10f, 0.15f, 0.85f);
        private static readonly Color FondoSenalado = new Color(0.15f, 0.24f, 0.34f, 0.92f);
        private static readonly Color ColorTitulo = Color.white;
        private static readonly Color ColorTexto = new Color(1f, 1f, 1f, 0.75f);

        private class Tarjeta
        {
            public ModoAR Modo;
            public GameObject Raiz;
            public BoxCollider Volumen;
            public Image Fondo;
            public Vector3 EscalaBase;
            public bool Senalada;
        }

        private readonly Tarjeta[] _tarjetas = new Tarjeta[2];
        private MRControllerRig _rig;
        private Camera _camara;
        private Action<ModoAR> _alElegir;
        private bool _elegido;
        private float _segundosSinMando;
        private bool _avisadoSinMando;

        public static MRModeSelector Mostrar(MRControllerRig rig, Camera camara, Action<ModoAR> alElegir)
        {
            var go = new GameObject("~SelectorDeModoAR");
            UnityEngine.Object.DontDestroyOnLoad(go);
            var selector = go.AddComponent<MRModeSelector>();
            selector._rig = rig;
            selector._camara = camara;
            selector._alElegir = alElegir;
            selector.ConstruirTarjetas();
            return selector;
        }

        private void ConstruirTarjetas()
        {
            // Delante del usuario, a la altura de la mirada, mirando hacia él. Se colocan una
            // vez y se quedan quietas: un objetivo que persigue al usuario es difícil de señalar.
            Vector3 adelante = _camara.transform.forward;
            adelante.y = 0f;
            if (adelante.sqrMagnitude < 0.0001f) adelante = Vector3.forward;
            adelante.Normalize();
            Vector3 lateral = Vector3.Cross(Vector3.up, adelante).normalized;
            Vector3 centro = _camara.transform.position + adelante * DistanciaAlUsuario
                             + Vector3.up * -0.05f;

            _tarjetas[0] = CrearTarjeta(ModoAR.NavegacionPorNodos,
                "Navegación por nodos",
                "Revisión remota: recorre el gemelo digital por sus puntos de vista.",
                centro - lateral * SeparacionLateral, adelante);

            _tarjetas[1] = CrearTarjeta(ModoAR.Anclado,
                "Modo anclado",
                "En obra: el modelo se superpone al edificio real y te desplazas andando.",
                centro + lateral * SeparacionLateral, adelante);
        }

        private Tarjeta CrearTarjeta(ModoAR modo, string titulo, string descripcion,
                                     Vector3 posicion, Vector3 adelante)
        {
            var canvas = DigitalTwin.UI.RuntimeUIFactory.CreateWorldCanvas(
                $"~TarjetaModo_{modo}", anchoPx: 460f, altoPx: 280f, anchoMetros: 0.52f);
            var raiz = canvas.gameObject;
            raiz.transform.SetParent(transform, true);
            raiz.transform.position = posicion;
            // Mismo convenio de orientación que el panel: el frente del lienzo queda hacia el
            // usuario cuando su +Z apunta en el sentido que se aleja de él.
            raiz.transform.rotation = Quaternion.LookRotation(adelante, Vector3.up);

            var rt = (RectTransform)raiz.transform;

            var fondo = DigitalTwin.UI.RuntimeUIFactory.CreatePanel(rt, "Fondo", FondoNormal);
            DigitalTwin.UI.RuntimeUIFactory.StretchToParent((RectTransform)fondo.transform);

            var tituloRect = DigitalTwin.UI.RuntimeUIFactory.CreateRect(rt, "Titulo");
            tituloRect.anchorMin = new Vector2(0f, 1f);
            tituloRect.anchorMax = new Vector2(1f, 1f);
            tituloRect.pivot = new Vector2(0.5f, 1f);
            tituloRect.anchoredPosition = new Vector2(0f, -28f);
            tituloRect.sizeDelta = new Vector2(-40f, 60f);
            DigitalTwin.UI.RuntimeUIFactory.CreateText(tituloRect, "Texto", titulo, 38,
                TextAnchor.MiddleCenter, ColorTitulo, FontStyle.Bold);
            DigitalTwin.UI.RuntimeUIFactory.StretchToParent(
                (RectTransform)tituloRect.GetChild(0).transform);

            var descRect = DigitalTwin.UI.RuntimeUIFactory.CreateRect(rt, "Descripcion");
            descRect.anchorMin = new Vector2(0f, 0f);
            descRect.anchorMax = new Vector2(1f, 1f);
            descRect.offsetMin = new Vector2(26f, 24f);
            descRect.offsetMax = new Vector2(-26f, -100f);
            DigitalTwin.UI.RuntimeUIFactory.CreateText(descRect, "Texto", descripcion, 24,
                TextAnchor.UpperCenter, ColorTexto, FontStyle.Normal);
            DigitalTwin.UI.RuntimeUIFactory.StretchToParent(
                (RectTransform)descRect.GetChild(0).transform);

            var volumen = raiz.AddComponent<BoxCollider>();
            volumen.isTrigger = true;
            volumen.size = new Vector3(rt.rect.width, rt.rect.height, 1f);
            volumen.center = Vector3.zero;

            return new Tarjeta
            {
                Modo = modo,
                Raiz = raiz,
                Volumen = volumen,
                Fondo = fondo,
                EscalaBase = raiz.transform.localScale
            };
        }

        private void Update()
        {
            if (_elegido || _rig == null) return;

            if (!_rig.TryGetRayo(out Ray rayo))
            {
                // Sin mando válido no hay forma de elegir. Se avisa una vez, pasado un margen
                // para que los mandos dormidos terminen de emparejarse, porque la alternativa
                // es un usuario parado ante dos tarjetas que no responden y un registro mudo.
                _segundosSinMando += Time.deltaTime;
                if (!_avisadoSinMando && _segundosSinMando > SegundosAntesDeAvisarSinMando)
                {
                    _avisadoSinMando = true;
                    Debug.LogWarning("[DigitalTwin][AR] Selector de modo a la espera: ningun mando " +
                                     "valido desde hace " + SegundosAntesDeAvisarSinMando +
                                     " s. Enciende un mando para poder elegir modo.");
                }
                return;
            }
            _segundosSinMando = 0f;

            Tarjeta senalada = null;
            float distancia = float.MaxValue;
            foreach (var tarjeta in _tarjetas)
            {
                if (tarjeta?.Volumen == null) continue;
                if (tarjeta.Volumen.Raycast(rayo, out RaycastHit hit, 20f) && hit.distance < distancia)
                {
                    distancia = hit.distance;
                    senalada = tarjeta;
                }
            }

            foreach (var tarjeta in _tarjetas)
            {
                if (tarjeta == null) continue;
                bool activa = tarjeta == senalada;
                if (activa != tarjeta.Senalada)
                {
                    tarjeta.Senalada = activa;
                    tarjeta.Fondo.color = activa ? FondoSenalado : FondoNormal;
                    // Escala absoluta desde la base guardada: la escala del lienzo ya lleva la
                    // conversión de píxeles a metros y no debe acumularse.
                    tarjeta.Raiz.transform.localScale = tarjeta.EscalaBase * (activa ? 1.05f : 1f);
                }
            }

            _rig.MostrarImpacto(senalada != null ? distancia : 0f, senalada != null);

            if (senalada != null && _rig.GatilloPulsadoEsteFrame())
            {
                _elegido = true;
                Debug.LogWarning($"[DigitalTwin][AR] Modo elegido: {senalada.Modo}.");
                var callback = _alElegir;
                Destroy(gameObject);
                callback?.Invoke(senalada.Modo);
            }
        }
    }
}
