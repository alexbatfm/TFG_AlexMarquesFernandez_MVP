using UnityEditor;
using UnityEngine;

namespace DigitalTwin.EditorTools
{
    /// <summary>
    /// Configura la pantalla de presentación de la aplicación para que muestre, tras el logotipo
    /// de Unity, el de la Universidad de Zaragoza y después el bloque de identidad de la propia
    /// aplicación («Gemelo Digital BIM»), en secuencia y sobre el mismo fondo.
    ///
    /// La configuración de la pantalla de presentación (<see cref="PlayerSettings.SplashScreen"/>)
    /// es única para todo el proyecto: no existe una por plataforma. Lo que se fija aquí lo ven
    /// igual la compilación de escritorio (Windows, <c>MainScene</c>) y el APK del visor
    /// (HTC Vive Focus Vision, <c>ARScene</c>). Por eso la decisión que toma este fichero está
    /// pensada para el caso más exigente de los dos, el visor, y el escritorio la hereda:
    ///
    ///  - Fondo OSCURO. En un visor la pantalla de presentación llena todo el campo de visión y
    ///    está a pocos centímetros de los ojos: un fondo blanco al 100 % es la primera imagen que
    ///    recibe el usuario, con la pupila todavía adaptada a la penumbra del casco, y además es
    ///    un salto brusco al vídeo de transparencia que viene detrás. Un fondo oscuro es la
    ///    convención de los propios visores (la pantalla de carga del sistema del Focus Vision es
    ///    oscura) y en un monitor no perjudica.
    ///  - Logotipo en NEGATIVO (trazo blanco). El institucional es azul y negro y sobre fondo
    ///    oscuro desaparece; el propio manual de identidad de la universidad prevé la versión en
    ///    negativo para fondos oscuros. Si el fichero del negativo NO está en el proyecto, esta
    ///    herramienta no inventa nada: vuelve al fondo claro con el logotipo original, y lo dice.
    ///  - Animación ESTÁTICA, no <c>Dolly</c>. El zum sobre un panel fijado a la cabeza es un
    ///    movimiento visual sin correlato vestibular; es justo lo que las guías de confort en XR
    ///    piden evitar, y en escritorio no aporta nada.
    ///  - DOS logotipos propios, en este orden: primero el institucional, después el bloque de la
    ///    aplicación (símbolo más nombre, <c>logo_app_lockup_*</c>). El orden sigue la convención
    ///    editor → producto: quien avala aparece antes que lo avalado, y el último fotograma antes
    ///    de la interfaz es el nombre de lo que arranca. Se descartó componer los dos en una sola
    ///    imagen porque no existe ese fichero y fabricarlo aquí sería inventar identidad; y se
    ///    descartó sustituir el institucional por el de la aplicación porque el entregable es de
    ///    la universidad. Los dos comparten el fondo <c>#16181C</c> a propósito: la identidad de
    ///    la aplicación se diseñó a partir del fondo que esta pantalla ya tenía. Si falta el
    ///    fichero del bloque, se avisa y la secuencia se queda con el institucional solo.
    ///
    /// Por qué se aplica sola y no desde un menú. La identidad institucional del entregable no es
    /// algo que nadie deba estar cambiando: no hay decisión que tomar cada vez, y una opción de
    /// menú que hay que acordarse de ejecutar es una forma segura de acabar entregando una build
    /// sin ella. Al ejecutarse en cada recarga de dominio, además, se reaplica sola tras un cambio
    /// de plataforma. Se conserva de todos modos una entrada de menú para forzar la reaplicación y
    /// ver el informe cuando se quiera comprobar.
    ///
    /// Es idempotente: compara el estado deseado completo (logotipo, fondo, animación, estilo del
    /// logotipo de Unity e imagen de RV) con el actual y, si coinciden, no toca nada. Sin esa
    /// comprobación marcaría el proyecto como modificado en cada recompilación.
    ///
    /// Sobre el logotipo de Unity: con licencia Personal no puede retirarse, pero sí acompañarse
    /// del propio, que es lo que se hace aquí. Con Plus o Pro basta con poner
    /// <c>showUnityLogo</c> a falso.
    /// </summary>
    [InitializeOnLoad]
    public static class ConfigurarSplashUnizar
    {
        // Logotipo institucional en positivo (azul y negro, transparente): para fondo claro.
        private const string RutaLogoPositivo = "Assets/Branding/logo_unizar.png";
        // Logotipo institucional en negativo (trazo blanco, transparente): para fondo oscuro.
        private const string RutaLogoNegativo = "Assets/Branding/logo_unizar_negativo.png";
        // Bloque de identidad de la aplicación (símbolo + «GEMELO DIGITAL BIM»), en sus dos
        // versiones. Vienen del paquete de identidad (TFG/utility/Identidad Corporativa).
        private const string RutaLockupPositivo = "Assets/Branding/logo_app_lockup_positivo.png";
        private const string RutaLockupNegativo = "Assets/Branding/logo_app_lockup_negativo.png";
        // Imagen opcional para el campo «Virtual Reality Splash Image» del reproductor: la que
        // Unity muestra en pantallas de RV mientras carga, antes de la secuencia de logotipos.
        // Ya lleva el fondo oscuro pintado, porque ese campo no se compone sobre el color de fondo.
        private const string RutaImagenRV = "Assets/Branding/splash_vr_unizar.png";

        // Decisión (ver resumen de la clase). Ponerlo a false devuelve el fondo blanco original.
        private const bool FondoOscuro = true;
        // Gris casi negro, no negro puro: el negro absoluto en un visor OLED «desaparece» y hace
        // que el logotipo flote sin referencia; un gris muy oscuro sigue siendo cómodo y da marco.
        private static readonly Color ColorFondoOscuro = new Color(0.086f, 0.094f, 0.110f, 1f); // #16181C
        private static readonly Color ColorFondoClaro = Color.white;
        private const float SegundosLogo = 2.5f;
        // El bloque de la aplicación dura algo menos: es la segunda pantalla propia y la suma
        // (Unity 2 s + 2,5 s + 2 s) es lo que el usuario espera con el casco puesto antes de ver
        // nada. Por debajo de 2 s Unity no garantiza que se lea.
        private const float SegundosLockup = 2f;

        static ConfigurarSplashUnizar()
        {
            // Se aplaza al primer tick del editor: en el constructor estático, la base de datos de
            // assets puede no estar lista todavía y la carga del sprite devolvería null.
            EditorApplication.delayCall += () => Aplicar(forzar: false);
        }

        [MenuItem("Tools/TFG/Reaplicar pantalla de presentación (informe)")]
        private static void ReaplicarDesdeMenu() => Aplicar(forzar: true);

        private static void Aplicar(bool forzar)
        {
            // Qué se quiere, decidido con lo que hay en disco.
            bool oscuro = FondoOscuro;
            if (oscuro && AssetImporter.GetAtPath(RutaLogoNegativo) == null)
            {
                // Se avisa SIEMPRE que falte, no solo al aplicar: es una acción pendiente de una
                // persona (conseguir el fichero), y si solo se dijera una vez pasaría inadvertida.
                Debug.LogWarning($"[Splash] Falta {RutaLogoNegativo} (logotipo en negativo). Se " +
                                  "configura la pantalla de presentacion con FONDO CLARO y el " +
                                  "logotipo original; en el visor ese fondo blanco a pantalla " +
                                  "completa es agresivo. Anade el fichero y la herramienta " +
                                  "cambiara sola a fondo oscuro.");
                oscuro = false;
            }

            string rutaLogo = oscuro ? RutaLogoNegativo : RutaLogoPositivo;
            Color fondo = oscuro ? ColorFondoOscuro : ColorFondoClaro;
            var estiloUnity = oscuro
                ? PlayerSettings.SplashScreen.UnityLogoStyle.LightOnDark
                : PlayerSettings.SplashScreen.UnityLogoStyle.DarkOnLight;

            var sprite = CargarSprite(rutaLogo);
            if (sprite == null)
            {
                // Sin logotipo institucional no se puede hacer nada, pero tampoco es un error del
                // proyecto: basta con avisar y seguir.
                Debug.LogWarning($"[Splash] No se encuentra {rutaLogo} o no carga como Sprite; la " +
                                  "pantalla de presentacion se queda con el logotipo de Unity solamente.");
                return;
            }

            // Bloque de identidad de la aplicación: opcional. Sin él, secuencia de un solo logotipo.
            string rutaLockup = oscuro ? RutaLockupNegativo : RutaLockupPositivo;
            var lockup = CargarSprite(rutaLockup);
            if (lockup == null)
                Debug.LogWarning($"[Splash] Falta {rutaLockup} (bloque de identidad de la aplicacion): " +
                                  "la secuencia lleva solo el logotipo institucional. Copia Branding/ " +
                                  "del paquete de identidad bajo Assets/ y la herramienta lo anadira sola.");

            // Imagen de RV: opcional. Si no está, se avisa y se deja el campo vacío.
            Texture2D imagenRV = CargarImagenRV();

            if (!forzar && YaConfigurada(sprite, lockup, fondo, estiloUnity, imagenRV)) return;

            PlayerSettings.SplashScreen.show = true;
            PlayerSettings.SplashScreen.showUnityLogo = true;   // obligatorio con licencia Personal
            PlayerSettings.SplashScreen.drawMode = PlayerSettings.SplashScreen.DrawMode.AllSequential;
            PlayerSettings.SplashScreen.animationMode = PlayerSettings.SplashScreen.AnimationMode.Static;
            PlayerSettings.SplashScreen.unityLogoStyle = estiloUnity;
            PlayerSettings.SplashScreen.backgroundColor = fondo;
            PlayerSettings.SplashScreen.logos = lockup != null
                ? new[]
                {
                    PlayerSettings.SplashScreenLogo.Create(SegundosLogo, sprite),
                    PlayerSettings.SplashScreenLogo.Create(SegundosLockup, lockup)
                }
                : new[] { PlayerSettings.SplashScreenLogo.Create(SegundosLogo, sprite) };
            PlayerSettings.virtualRealitySplashScreen = imagenRV;

            AssetDatabase.SaveAssets();
            Debug.LogWarning($"[Splash] Pantalla de presentacion configurada: fondo " +
                             $"{(oscuro ? "OSCURO" : "CLARO")} ({ColorHex(fondo)}), logotipo " +
                             $"{System.IO.Path.GetFileName(rutaLogo)} durante {SegundosLogo:0.0} s" +
                             (lockup != null
                                 ? $" y despues {System.IO.Path.GetFileName(rutaLockup)} durante {SegundosLockup:0.0} s,"
                                 : ", SIN bloque de la aplicacion,") +
                             $" en secuencia tras el de Unity ({estiloUnity}), animacion estatica, imagen " +
                             $"de RV {(imagenRV != null ? RutaImagenRV : "SIN ASIGNAR")}. Esta " +
                             $"configuracion es unica para escritorio y visor.");

            AvisarSiElLogoNoContrasta(sprite, fondo);
            if (lockup != null) AvisarSiElLogoNoContrasta(lockup, fondo);
        }

        /// <summary>
        /// Carga un PNG como Sprite, corrigiendo antes su importación si hace falta. La pantalla de
        /// presentación solo admite Sprite: importado como textura normal, el campo del logotipo lo
        /// rechaza sin explicar por qué. Devuelve null, sin lanzar, si el fichero no existe.
        /// </summary>
        private static Sprite CargarSprite(string ruta)
        {
            var importador = AssetImporter.GetAtPath(ruta) as TextureImporter;
            if (importador == null) return null;
            if (importador.textureType != TextureImporterType.Sprite)
            {
                importador.textureType = TextureImporterType.Sprite;
                importador.spriteImportMode = SpriteImportMode.Single;
                importador.alphaIsTransparency = true;
                importador.mipmapEnabled = false;
                importador.SaveAndReimport();
            }
            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(ruta);
            if (sprite == null)
                Debug.LogWarning($"[Splash] {ruta} existe pero no se ha podido cargar como Sprite.");
            return sprite;
        }

        private static Texture2D CargarImagenRV()
        {
            var importador = AssetImporter.GetAtPath(RutaImagenRV) as TextureImporter;
            if (importador == null)
            {
                Debug.LogWarning($"[Splash] No hay {RutaImagenRV}: el campo «Virtual Reality Splash " +
                                  "Image» del reproductor queda vacio (el visor mostrara solo la " +
                                  "secuencia de logotipos, si el runtime la compone).");
                return null;
            }
            // Ese campo pide una Texture2D corriente, no un Sprite.
            if (importador.textureType != TextureImporterType.Default)
            {
                importador.textureType = TextureImporterType.Default;
                importador.mipmapEnabled = false;
                importador.SaveAndReimport();
            }
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(RutaImagenRV);
            if (tex == null)
                Debug.LogWarning($"[Splash] {RutaImagenRV} existe pero no se ha podido cargar como Texture2D.");
            return tex;
        }

        /// <summary>
        /// Comprueba que el logotipo contraste con el fondo. Un trazo oscuro sobre fondo oscuro,
        /// o claro sobre claro, deja la pantalla de presentacion practicamente vacia sin que nada
        /// falle: no hay error, no hay aviso del motor, y solo se descubre mirandola.
        /// </summary>
        private static void AvisarSiElLogoNoContrasta(Sprite sprite, Color fondoColor)
        {
            var tex = sprite.texture;
            if (!tex.isReadable)
            {
                // Sin acceso a los pixeles no se puede comprobar; se dice, para que nadie crea que
                // la comprobacion se hizo. Activar Read/Write en el importador la habilita.
                Debug.LogWarning($"[Splash] {tex.name} no es legible (Read/Write desactivado): no se " +
                                  "ha podido comprobar el contraste logotipo/fondo. Miralo a ojo.");
                return;
            }

            double suma = 0; int n = 0;
            var pixeles = tex.GetPixels32();
            for (int i = 0; i < pixeles.Length; i += 37)   // muestreo disperso: basta para la media
            {
                var p = pixeles[i];
                if (p.a < 40) continue;                    // el fondo transparente no cuenta
                suma += (p.r + p.g + p.b) / 3.0;
                n++;
            }
            if (n == 0)
            {
                Debug.LogWarning($"[Splash] {tex.name} no tiene pixeles opacos: la pantalla de presentacion saldra vacia.");
                return;
            }

            double logo = suma / n;
            double fondo = (fondoColor.r + fondoColor.g + fondoColor.b) / 3.0 * 255.0;

            // Menos de 60 puntos de diferencia sobre 255 es un contraste insuficiente para leerse.
            if (System.Math.Abs(logo - fondo) < 60)
                Debug.LogWarning($"[Splash] El logotipo (luminancia media {logo:0}/255) apenas " +
                                  $"contrasta con el fondo ({fondo:0}/255): la pantalla de " +
                                  $"presentacion se vera casi vacia. Cambia el fondo o usa la " +
                                  $"otra version del logotipo.");
        }

        private static bool YaConfigurada(Sprite esperado, Sprite lockup, Color fondo,
                                          PlayerSettings.SplashScreen.UnityLogoStyle estilo,
                                          Texture2D imagenRV)
        {
            var logos = PlayerSettings.SplashScreen.logos;
            int esperados = lockup != null ? 2 : 1;
            if (logos == null || logos.Length != esperados || logos[0].logo != esperado) return false;
            if (lockup != null && logos[1].logo != lockup) return false;
            if (!PlayerSettings.SplashScreen.show || !PlayerSettings.SplashScreen.showUnityLogo) return false;
            if (PlayerSettings.SplashScreen.drawMode != PlayerSettings.SplashScreen.DrawMode.AllSequential) return false;
            if (PlayerSettings.SplashScreen.animationMode != PlayerSettings.SplashScreen.AnimationMode.Static) return false;
            if (PlayerSettings.SplashScreen.unityLogoStyle != estilo) return false;
            if (!Aproximado(PlayerSettings.SplashScreen.backgroundColor, fondo)) return false;
            if (PlayerSettings.virtualRealitySplashScreen != imagenRV) return false;
            return true;
        }

        private static bool Aproximado(Color a, Color b) =>
            Mathf.Abs(a.r - b.r) < 0.01f && Mathf.Abs(a.g - b.g) < 0.01f &&
            Mathf.Abs(a.b - b.b) < 0.01f && Mathf.Abs(a.a - b.a) < 0.01f;

        private static string ColorHex(Color c) => "#" + ColorUtility.ToHtmlStringRGB(c);
    }
}
