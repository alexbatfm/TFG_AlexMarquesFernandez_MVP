using DigitalTwin.Core;
using DigitalTwin.UI;
using UnityEngine;
using UnityEngine.UI;

namespace DigitalTwin.MR
{
    /// <summary>
    /// Pantalla de carga del visor: lo que el usuario ve entre que arranca la aplicación y que
    /// la escena está lista (geometría cargada, grafo resuelto y, en el visor, vídeo de
    /// transparencia activo).
    ///
    /// EL MOTIVO ES EL CONFORT, NO LA ESTÉTICA, Y ESO CONDICIONA CADA DECISIÓN DE ESTA CLASE.
    /// Con el visor puesto, una imagen que no responde al movimiento de la cabeza produce
    /// malestar: el oído interno informa de un giro que el ojo no confirma, y ese desacople
    /// visual-vestibular es la causa directa del mareo en realidad mixta. Cuando el hilo
    /// principal se bloquea, la aplicación deja de entregar fotogramas al compositor, el runtime
    /// de OpenXR reproyecta el último disponible y se produce exactamente ese desacople. De ahí
    /// la regla que gobierna todo el arranque: **una pantalla de carga que siga bloqueando el
    /// hilo principal no resuelve nada**. Esta clase es la mitad visible del trabajo; la otra
    /// mitad es el reparto entre fotogramas que hacen <see cref="MRDigitalTwinBootstrap"/>,
    /// <see cref="Core.ColliderBootstrapper"/> y <see cref="MROcclusionService"/>.
    ///
    /// Los cuatro criterios de confort que se aplican aquí, y por qué:
    ///
    /// 1. SE DEJA PASAR EL VÍDEO DE TRANSPARENCIA EN CUANTO ESTÁ. Ver la habitación real es lo
    ///    más cómodo que existe: el sistema vestibular recibe exactamente lo que espera, porque
    ///    lo que se ve ES el entorno. Este proyecto tiene la transparencia funcionando, así que
    ///    la pantalla de carga no dibuja ningún fondo que la tape: la cámara se borra con alfa
    ///    cero, que es lo que el compositor necesita para mezclar el vídeo por debajo. Mientras
    ///    la capa todavía no existe —hay una espera deliberada de 90 fotogramas, ver
    ///    <see cref="MRPassthroughController"/>— ese borrado se ve como un color plano, y por
    ///    eso el color es un gris azulado oscuro y no negro puro ni blanco: el negro absoluto
    ///    suprime toda referencia visual y el blanco a campo completo deslumbra en un panel a
    ///    pocos centímetros del ojo. Es además el mismo tono de la pantalla de presentación, con
    ///    lo que el paso de una a otra no se percibe como un corte.
    ///
    /// 2. NADA FIJADO RÍGIDAMENTE A LA CABEZA. Un elemento que no se mueve nunca respecto a los
    ///    ojos elimina la referencia visual estable y es de lo que peor se tolera. El panel se
    ///    coloca en el mundo, delante del usuario, y se queda quieto. Solo si el usuario gira lo
    ///    bastante como para perderlo de vista (más de <see cref="GradosDeHolgura"/>) lo sigue,
    ///    y lo hace con retardo y amortiguado, nunca pegado. Obsérvese la asimetría: el panel se
    ///    mueve porque el usuario se ha movido antes, jamás por iniciativa propia.
    ///
    /// 3. NINGÚN MOVIMIENTO DE CÁMARA QUE EL USUARIO NO HAYA PROVOCADO. Esta clase no escribe la
    ///    transformada de la cámara ni la del origen de realidad extendida. Ni un giro de
    ///    bienvenida, ni un acercamiento, ni un balanceo.
    ///
    /// 4. LA ESPERA TIENE FINAL VISIBLE. Barra de avance por fases con peso real y el paso en
    ///    curso escrito con todas las letras. Una espera acotada se tolera mucho mejor que una
    ///    indefinida, y en un visor eso no es una cortesía: la ansiedad de no saber si la
    ///    aplicación se ha colgado agrava la incomodidad física.
    ///
    /// POR QUÉ SE RESTRINGE LA MÁSCARA DE CULTIVO DE LA CÁMARA. Mientras la pantalla está
    /// puesta, la cámara solo dibuja la capa de la propia pantalla. No es un adorno: durante el
    /// montaje del modo anclado la geometría se reactiva ANTES de vestirse de oclusor, y sin
    /// esta restricción el usuario vería el edificio virtual opaco encima de su sala real
    /// durante las décimas de segundo que dura el cambio de materiales. Antes eso no ocurría
    /// porque todo el montaje pasaba en un único fotograma —a costa de la parada que este
    /// trabajo elimina—. La máscara se guarda y se repone al cerrar.
    /// </summary>
    public class MRPantallaDeCarga : MonoBehaviour
    {
        // --- Colocación ------------------------------------------------------------------
        /// <summary>Distancia de lectura. Más cerca de un metro la convergencia ocular resulta
        /// forzada en un visor; más allá de dos, el texto pierde píxeles. 1,5 m es también la
        /// distancia a la que ya se leen los carteles de navegación del proyecto.</summary>
        private const float DistanciaAlUsuario = 1.5f;

        /// <summary>El panel se sitúa ligeramente por debajo de la horizontal de la mirada, que
        /// es la posición de reposo natural del ojo y la que ya usa el selector de modo.</summary>
        private const float DesplazamientoVertical = -0.05f;

        /// <summary>Holgura angular antes de recolocar. Por debajo de unos 25° el panel seguiría
        /// al usuario en casi cualquier movimiento y volvería a ser, de hecho, una superposición
        /// fijada a la cabeza; muy por encima de 35° el usuario puede perderlo del todo y quedar
        /// sin saber qué está pasando. 30° deja quieto el panel en el rango de exploración
        /// normal y solo lo trae de vuelta cuando el usuario ha girado de verdad.</summary>
        private const float GradosDeHolgura = 30f;

        /// <summary>Igual que la holgura angular, pero para el desplazamiento: si el usuario
        /// camina y el panel se le queda a más de esta distancia de la prevista, se recoloca.</summary>
        private const float HolguraDeDistancia = 0.7f;

        /// <summary>Constante de tiempo del amortiguado de la recolocación. Con 0,45 s el panel
        /// tarda algo más de un segundo en asentarse: se percibe como que «viene detrás», que es
        /// justo lo que se busca, y no como que persigue la mirada.</summary>
        private const float SuavizadoDeSeguimiento = 0.45f;

        // --- Transiciones ----------------------------------------------------------------
        /// <summary>NO hay transición de entrada, a propósito: la pantalla tiene que estar en el
        /// primer fotograma que se dibuje. Cualquier aparición progresiva reabriría precisamente
        /// el intervalo indefinido que esta pantalla existe para cubrir.
        ///
        /// La de salida sí: un corte seco a la escena montada se percibe como un parpadeo. Se
        /// eligen 0,25 s —unos 22 fotogramas a 90 Hz— porque por debajo de una décima el cambio
        /// se lee como un corte y no aporta nada, y a partir de medio segundo el usuario ya está
        /// esperando otra vez, que es lo contrario del efecto buscado. La máscara de cultivo se
        /// repone al EMPEZAR el desvanecimiento, de modo que la escena aparece por detrás del
        /// panel que se va: el resultado es un fundido cruzado y no una sustitución.</summary>
        private const float SegundosDeSalida = 0.25f;

        /// <summary>Red de seguridad. Si algo se atasca y nadie cierra la pantalla, el usuario se
        /// quedaría encerrado en un panel sobre un fondo plano sin poder hacer nada, que es peor
        /// que cualquier escena a medio montar. Pasado este plazo la pantalla se retira sola y lo
        /// denuncia en el registro. El plazo es holgado a propósito: el arranque medido está en
        /// el entorno de dos segundos, y el modo anclado puede esperar legítimamente varios
        /// segundos a que el sistema devuelva la sesión de transparencia. Es la ÚLTIMA red: la
        /// primera es la guarda del secuenciador de arranque (<c>MRBootSequencer</c>, 15 s), que
        /// además de retirar esta pantalla fuerza el selector de modo, porque retirar la
        /// pantalla sin selector detrás sigue dejando al usuario sin entrada.</summary>
        private const float SegundosDeSeguridad = 30f;

        private static readonly Color ColorFondoPanel = new Color(0.07f, 0.10f, 0.15f, 0.90f);

        /// <summary>Borrado de la cámara mientras no hay vídeo de transparencia: gris azulado
        /// oscuro, con ALFA CERO. El alfa es lo que decide: en cuanto la capa de transparencia
        /// entra en la pila, el compositor mezcla el vídeo justo donde el alfa es cero, y este
        /// color deja de verse sin que haya que tocar nada. Ver el criterio 1 de la clase.</summary>
        private static readonly Color ColorBorradoNeutro = new Color(0.086f, 0.094f, 0.110f, 0f);

        public static MRPantallaDeCarga Instancia { get; private set; }

        /// <summary>Nombre del objeto raíz. Lo consulta el barrido de objetos persistentes de la
        /// vuelta al selector, que debe dejar viva esta pantalla precisamente porque su trabajo
        /// es cubrir esa recarga.</summary>
        public const string NombreObjeto = "~PantallaDeCargaAR";

        /// <summary>Borrado que tenía la cámara antes de que esta pantalla lo cambiara. Se lo
        /// entrega el arranque a <see cref="MRPassthroughController"/> para que, si algún día
        /// restaura la cámara, restaure lo que la escena traía y no lo que puso la carga.</summary>
        public CameraClearFlags BorradoOriginal { get; private set; }
        public Color FondoOriginal { get; private set; }

        private ContenidoPantallaDeCarga _contenido;
        private Camera _camara;
        private int _mascaraOriginal;
        private bool _camaraTomada;
        private int _capa = -1;

        private bool _cerrando;
        private float _alfa = 1f;
        private float _abiertaEn;

        private bool _recolocando;
        private Vector3 _velocidad;

        /// <summary>
        /// Fotogramas durante los que la colocación se rehace en seco.
        ///
        /// En el fotograma en que se crea la pantalla, la cámara puede no llevar todavía
        /// aplicada la pose de la cabeza que entrega el runtime: colocar el panel «delante del
        /// usuario» con esa pose lo dejaría delante del origen de la escena, que puede no ser
        /// donde el usuario mira. Rehacer la colocación durante los primeros fotogramas la
        /// corrige en cuanto llegan datos de seguimiento. No contradice la regla de no mover
        /// nada por iniciativa propia: esto no es un movimiento percibido, es dónde aparece el
        /// panel, y ocurre antes de que haya nada que percibir.
        /// </summary>
        private const int FotogramasDeAsentamiento = 5;
        private int _fotogramasDeAsentamiento;

        /// <summary>Abre la pantalla, o devuelve la que ya está puesta cancelando su cierre si
        /// estaba desvaneciéndose. Idempotente: el arranque la pide en varios puntos sin
        /// llevar la cuenta de si ya existe.</summary>
        public static MRPantallaDeCarga Abrir()
        {
            if (Instancia != null)
            {
                Instancia.CancelarCierre();
                return Instancia;
            }

            var go = new GameObject(NombreObjeto);
            DontDestroyOnLoad(go);
            Instancia = go.AddComponent<MRPantallaDeCarga>();
            Instancia.Construir();
            return Instancia;
        }

        private void Construir()
        {
            _abiertaEn = Time.unscaledTime;

            // Capa propia para la máscara de cultivo. Se usa la capa «UI» del motor, que existe
            // siempre; si no se resolviera, se renuncia a restringir el dibujado en vez de
            // arriesgarse a dejar la cámara sin ver nada.
            _capa = LayerMask.NameToLayer("UI");
            if (_capa < 0)
                Debug.LogWarning("[DigitalTwin][AR][Carga] No se resuelve la capa 'UI': la " +
                                 "pantalla de carga no restringira el dibujado y la escena a " +
                                 "medio montar sera visible detras del panel.");

            var canvas = RuntimeUIFactory.CreateWorldCanvas("~LienzoCargaAR",
                anchoPx: 700f, altoPx: 380f, anchoMetros: 0.72f);
            canvas.transform.SetParent(transform, true);

            var rt = (RectTransform)canvas.transform;
            var fondo = RuntimeUIFactory.CreatePanel(rt, "Fondo", ColorFondoPanel);
            RuntimeUIFactory.StretchToParent((RectTransform)fondo.transform);

            // Cuerpos de letra del visor, coherentes con el selector de modo (título 38, cuerpo
            // 24 sobre un lienzo de proporción equivalente): a 1,5 m el título ocupa algo más de
            // grado y medio de campo visual, que es lo que en este proyecto ya se ha comprobado
            // legible con el casco puesto.
            _contenido = new ContenidoPantallaDeCarga();
            _contenido.Construir(rt, "Gemelo Digital BIM", cuerpoTitulo: 40, cuerpoPaso: 28,
                                 cuerpoPorcentaje: 24, altoBarra: 18f);
            _contenido.IncluirEnDesvanecido(fondo);

            AplicarCapa(gameObject);

            TomarCamara(Camera.main);
            Recolocar(inmediato: true);
            _fotogramasDeAsentamiento = FotogramasDeAsentamiento;

            ProgresoDeArranque.AlCambiar += Refrescar;
            Refrescar();

            Debug.LogWarning("[DigitalTwin][AR][Carga] Pantalla de carga puesta. La camara " +
                             "dibuja solo esta capa y se borra con alfa cero: en cuanto la capa " +
                             "de transparencia entre en la pila, se vera la sala real de fondo.");
        }

        /// <summary>
        /// Guarda los ajustes de cámara que se van a alterar y los sustituye por los de carga.
        /// Se separa de <see cref="Construir"/> porque hay que repetirlo tras una recarga de
        /// escena: la cámara de la escena vieja se destruye con ella.
        /// </summary>
        private void TomarCamara(Camera camara)
        {
            // IDEMPOTENTE, Y NO ES UNA CORTESÍA. El arranque llama a Abrir() y acto seguido a
            // ReanclarTrasRecarga() sin saber si la pantalla acaba de crearse; en ese caso la
            // cámara YA está tomada, y volver a leer sus ajustes «originales» guardaba como
            // originales los que había puesto esta misma pantalla: la máscara de cultivo
            // reducida a la capa UI y el borrado con alfa cero. Al cerrar se «reponía» esa
            // máscara, la cámara se quedaba dibujando solo la capa UI y el selector de modo
            // —que vive en la capa por defecto, como los mandos— no se veía nunca (regresión
            // del 19-08, sesiones de las 13:48 a las 13:55). Si la cámara es la misma y sigue
            // tomada, no hay nada que hacer.
            if (_camaraTomada && camara != null && _camara == camara) return;

            // Cámara distinta (tras una recarga de escena la anterior ya no existe): se devuelve
            // lo tomado a la anterior antes de tomar la nueva.
            if (_camaraTomada) ReponerCamara();

            _camara = camara;
            if (_camara == null)
            {
                _camaraTomada = false;
                Debug.LogWarning("[DigitalTwin][AR][Carga] Sin camara principal: la pantalla de " +
                                 "carga no puede fijar el borrado ni la mascara de cultivo.");
                return;
            }

            BorradoOriginal = _camara.clearFlags;
            FondoOriginal = _camara.backgroundColor;
            _mascaraOriginal = _camara.cullingMask;

            // Invariante: lo que se guarda como original nunca puede ser la propia máscara de
            // carga. Ninguna cámara de escena dibuja solo la capa UI; si se la encuentra así es
            // que algo la dejó a medias, y guardarla perpetuaría el encierro.
            if (_capa >= 0 && _mascaraOriginal == (1 << _capa))
            {
                Debug.LogError("[DigitalTwin][AR][Carga] La camara ya tenia la mascara de carga " +
                               "(solo capa UI) al tomarla: se guarda 'todas las capas' como " +
                               "original para no perpetuar una mascara que dejaria la escena " +
                               "invisible.");
                _mascaraOriginal = ~0;
            }
            _camaraTomada = true;

            _camara.clearFlags = CameraClearFlags.SolidColor;
            _camara.backgroundColor = ColorBorradoNeutro;
            if (_capa >= 0) _camara.cullingMask = 1 << _capa;
        }

        /// <summary>
        /// Vuelve a tomar la cámara y a colocarse delante del usuario tras una recarga de
        /// escena. Lo llama el arranque, que es quien sabe cuándo ha terminado la recarga.
        /// </summary>
        public void ReanclarTrasRecarga()
        {
            TomarCamara(Camera.main);
            Recolocar(inmediato: true);
            _fotogramasDeAsentamiento = FotogramasDeAsentamiento;
        }

        private void AplicarCapa(GameObject raiz)
        {
            if (_capa < 0) return;
            foreach (var t in raiz.GetComponentsInChildren<Transform>(true))
                t.gameObject.layer = _capa;
        }

        private void Refrescar()
        {
            _contenido?.Refrescar(ProgresoDeArranque.TextoDeFase, ProgresoDeArranque.Fraccion);
        }

        private void Update()
        {
            // Latido para la medida de regularidad de entrega. Se hace aquí porque esta pantalla
            // es el único componente garantizado vivo durante todo el arranque en las dos
            // plataformas.
            ProgresoDeArranque.RegistrarIntervaloDeFotograma();

            if (_camara == null) TomarCamara(Camera.main);

            SeguirConHolgura();

            if (!_cerrando)
            {
                if (Time.unscaledTime - _abiertaEn > SegundosDeSeguridad)
                {
                    Debug.LogError("[DigitalTwin][AR][Carga] La pantalla de carga lleva puesta " +
                                   SegundosDeSeguridad + " s y nadie la ha cerrado: se retira " +
                                   "sola para no dejar al usuario encerrado. Perfil '" +
                                   ProgresoDeArranque.Perfil + "', ultima fase: '" +
                                   ProgresoDeArranque.TextoDeFase + "' (" +
                                   ProgresoDeArranque.Fraccion.ToString("0.00") + "). El " +
                                   "arranque ha quedado a medias.");
                    Cerrar();
                }
                return;
            }

            _alfa -= Time.unscaledDeltaTime / SegundosDeSalida;
            if (_alfa <= 0f) { Destruir(); return; }
            _contenido?.AplicarAlfa(_alfa);
        }

        /// <summary>
        /// Seguimiento perezoso: el panel está anclado al mundo y solo se mueve cuando el
        /// usuario lo ha dejado fuera de su holgura. Ver el criterio 2 de la clase.
        /// </summary>
        private void SeguirConHolgura()
        {
            if (_camara == null) return;

            if (_fotogramasDeAsentamiento > 0)
            {
                _fotogramasDeAsentamiento--;
                Recolocar(inmediato: true);
                return;
            }

            Vector3 posCamara = _camara.transform.position;
            Vector3 haciaPanel = transform.position - posCamara;
            float distancia = haciaPanel.magnitude;
            haciaPanel.y = 0f;

            Vector3 mirada = _camara.transform.forward;
            mirada.y = 0f;

            if (!_recolocando && haciaPanel.sqrMagnitude > 0.0001f && mirada.sqrMagnitude > 0.0001f)
            {
                bool fueraDeVista = Vector3.Angle(mirada, haciaPanel) > GradosDeHolgura;
                bool demasiadoLejos = Mathf.Abs(distancia - DistanciaAlUsuario) > HolguraDeDistancia;
                if (fueraDeVista || demasiadoLejos) _recolocando = true;
            }

            if (!_recolocando) return;

            Vector3 destino = PosicionDeseada(out Quaternion orientacion);
            transform.position = Vector3.SmoothDamp(transform.position, destino, ref _velocidad,
                                                    SuavizadoDeSeguimiento, Mathf.Infinity,
                                                    Time.unscaledDeltaTime);
            // El giro acompaña al desplazamiento con la misma constante de tiempo. Solo guiñada:
            // el panel nunca se inclina ni rueda, porque una referencia visual con horizonte
            // torcido es justo lo que peor sienta con el visor puesto.
            transform.rotation = Quaternion.Slerp(transform.rotation, orientacion,
                                                  1f - Mathf.Exp(-Time.unscaledDeltaTime /
                                                                 SuavizadoDeSeguimiento));

            if ((transform.position - destino).sqrMagnitude < 0.0004f)
            {
                _recolocando = false;
                _velocidad = Vector3.zero;
            }
        }

        private void Recolocar(bool inmediato)
        {
            if (_camara == null) return;
            Vector3 destino = PosicionDeseada(out Quaternion orientacion);
            if (inmediato)
            {
                transform.position = destino;
                transform.rotation = orientacion;
                _recolocando = false;
                _velocidad = Vector3.zero;
            }
            else _recolocando = true;
        }

        private Vector3 PosicionDeseada(out Quaternion orientacion)
        {
            Vector3 adelante = _camara.transform.forward;
            adelante.y = 0f;
            if (adelante.sqrMagnitude < 0.0001f) adelante = Vector3.forward;
            adelante.Normalize();

            orientacion = Quaternion.LookRotation(adelante, Vector3.up);
            return _camara.transform.position + adelante * DistanciaAlUsuario
                   + Vector3.up * DesplazamientoVertical;
        }

        /// <summary>
        /// Empieza la retirada. La máscara de cultivo se repone YA —no al final del
        /// desvanecimiento— para que la escena aparezca por detrás del panel que se va.
        /// </summary>
        public void Cerrar()
        {
            if (_cerrando) return;
            _cerrando = true;
            ReponerCamara();
            // Traza del traspaso. Sin ella, el registro del 19-08 decía «pantalla puesta» y
            // «selector visible» y no había forma de saber si la pantalla había llegado a
            // retirarse ni con qué máscara dejaba la cámara.
            Debug.LogWarning("[DigitalTwin][AR][Carga] Pantalla de carga retirada tras " +
                             $"{Time.unscaledTime - _abiertaEn:0.0} s; mascara de la camara " +
                             $"repuesta a {(_camara != null ? _camara.cullingMask : 0):X8}.");
        }

        private void CancelarCierre()
        {
            if (!_cerrando) return;
            _cerrando = false;
            _alfa = 1f;
            _abiertaEn = Time.unscaledTime;
            _contenido?.AplicarAlfa(1f);
            TomarCamara(_camara != null ? _camara : Camera.main);
        }

        /// <summary>
        /// Devuelve la cámara a como estaba. La máscara de cultivo siempre; el borrado, solo si
        /// la transparencia no lo ha tomado ya: cuando el vídeo está activo, el borrado correcto
        /// es el que ha puesto <see cref="MRPassthroughController"/> (color sólido con alfa
        /// cero) y reponer aquí el cielo de la escena rompería la composición.
        /// </summary>
        private void ReponerCamara()
        {
            if (!_camaraTomada) return;
            _camaraTomada = false;
            if (_camara == null) return;   // destruida con la escena: no hay nada que reponer

            // Segunda línea del mismo invariante que TomarCamara: la máscara que se repone no
            // puede ser la de carga. Si lo fuera, la escena entera quedaría invisible al cerrar.
            if (_capa >= 0 && _mascaraOriginal == (1 << _capa))
            {
                Debug.LogError("[DigitalTwin][AR][Carga] La mascara 'original' guardada era la " +
                               "de carga (solo capa UI): se repone 'todas las capas'.");
                _mascaraOriginal = ~0;
            }
            _camara.cullingMask = _mascaraOriginal;

            // El borrado solo se repone si LO QUE HAY PUESTO SIGUE SIENDO LO QUE PUSO ESTA
            // PANTALLA. Comprobar en su lugar si la transparencia está activa no basta y da un
            // error concreto: al entrar en navegación por nodos el montaje APAGA la
            // transparencia, y al apagarse el controlador ya repone el cielo de la escena; si
            // acto seguido esta pantalla repusiera «su» original —que en esa segunda apertura es
            // el borrado con alfa cero que había dejado la transparencia— el modo de navegación
            // se quedaría sobre fondo negro en lugar de sobre el cielo. Con esta comprobación,
            // quien haya escrito el último gana, que es lo correcto: esta pantalla solo deshace
            // lo suyo.
            bool sigueElBorradoDeCarga =
                _camara.clearFlags == CameraClearFlags.SolidColor &&
                MismoColor(_camara.backgroundColor, ColorBorradoNeutro);
            if (!sigueElBorradoDeCarga) return;

            _camara.clearFlags = BorradoOriginal;
            _camara.backgroundColor = FondoOriginal;
        }

        private static bool MismoColor(Color a, Color b)
        {
            const float tolerancia = 0.001f;
            return Mathf.Abs(a.r - b.r) < tolerancia && Mathf.Abs(a.g - b.g) < tolerancia &&
                   Mathf.Abs(a.b - b.b) < tolerancia && Mathf.Abs(a.a - b.a) < tolerancia;
        }

        private void Destruir()
        {
            ReponerCamara();
            ProgresoDeArranque.AlCambiar -= Refrescar;
            if (Instancia == this) Instancia = null;
            Destroy(gameObject);
        }

        private void OnDestroy()
        {
            ReponerCamara();
            ProgresoDeArranque.AlCambiar -= Refrescar;
            if (Instancia == this) Instancia = null;
        }
    }
}
