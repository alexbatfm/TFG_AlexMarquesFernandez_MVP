using UnityEditor;
using UnityEngine;

namespace DigitalTwin.EditorTools
{
    /// <summary>
    /// Configura la pantalla de presentación de la aplicación para que muestre el logotipo de la
    /// Universidad de Zaragoza junto al de Unity, del mismo modo que este último aparece de serie.
    ///
    /// Por qué se aplica solo y no desde un menú. La identidad institucional del entregable no es
    /// algo que nadie deba estar cambiando: no hay decisión que tomar cada vez, y una opción de
    /// menú que hay que acordarse de ejecutar es una forma segura de acabar entregando una build
    /// sin ella. Al ejecutarse en cada recarga de dominio, además, se reaplica sola tras un cambio
    /// de plataforma, que es justo cuando resulta fácil perderla de vista.
    ///
    /// Por qué es una herramienta de editor y no un ajuste hecho a mano. La configuración vive en
    /// los ajustes del reproductor, y tocarla pinchando por los menús no deja rastro de qué se
    /// cambió ni permite reproducirlo en otra máquina. Como script queda versionada y se documenta
    /// a sí misma.
    ///
    /// Es idempotente: comprueba antes si ya está aplicada y, si lo está, no hace nada. Sin esa
    /// comprobación marcaría el proyecto como modificado en cada recompilación.
    ///
    /// Sobre el logotipo de Unity: con licencia Personal no puede retirarse, pero sí acompañarse
    /// del propio, que es lo que se hace aquí. Con Plus o Pro basta con poner
    /// <c>showUnityLogo</c> a falso.
    /// </summary>
    [InitializeOnLoad]
    public static class ConfigurarSplashUnizar
    {
        private const string RutaLogo = "Assets/Branding/logo_unizar.png";
        private const float SegundosLogo = 2.5f;

        static ConfigurarSplashUnizar()
        {
            // Se aplaza al primer tick del editor: en el constructor estático, la base de datos de
            // assets puede no estar lista todavía y la carga del sprite devolvería null.
            EditorApplication.delayCall += Aplicar;
        }

        private static void Aplicar()
        {
            if (YaConfigurada()) return;

            var importador = AssetImporter.GetAtPath(RutaLogo) as TextureImporter;
            if (importador == null)
            {
                // Sin logotipo no se puede hacer nada, pero tampoco es un error del proyecto:
                // basta con avisar una vez y seguir.
                Debug.LogWarning($"[Splash] No se encuentra {RutaLogo}; la pantalla de presentacion " +
                                  "se queda con el logotipo de Unity solamente.");
                return;
            }

            // La pantalla de presentación solo admite Sprite. Importado como textura normal, el
            // campo del logotipo lo rechaza sin explicar por qué.
            if (importador.textureType != TextureImporterType.Sprite)
            {
                importador.textureType = TextureImporterType.Sprite;
                importador.spriteImportMode = SpriteImportMode.Single;
                importador.alphaIsTransparency = true;
                importador.mipmapEnabled = false;
                importador.SaveAndReimport();
            }

            var sprite = AssetDatabase.LoadAssetAtPath<Sprite>(RutaLogo);
            if (sprite == null)
            {
                Debug.LogWarning("[Splash] El logotipo no se ha podido cargar como Sprite.");
                return;
            }

            PlayerSettings.SplashScreen.show = true;
            PlayerSettings.SplashScreen.showUnityLogo = true;   // obligatorio con licencia Personal
            PlayerSettings.SplashScreen.drawMode = PlayerSettings.SplashScreen.DrawMode.AllSequential;
            PlayerSettings.SplashScreen.animationMode = PlayerSettings.SplashScreen.AnimationMode.Dolly;
            PlayerSettings.SplashScreen.unityLogoStyle = PlayerSettings.SplashScreen.UnityLogoStyle.LightOnDark;

            // Fondo negro. La razón es de confort visual: la pantalla de presentación es lo
            // primero que se ve al ponerse el visor, a menudo en una sala en penumbra y con los
            // ojos sin adaptar. Un fondo blanco a pantalla completa y a pocos centímetros de la
            // cara resulta agresivo.
            PlayerSettings.SplashScreen.backgroundColor = Color.black;

            PlayerSettings.SplashScreen.logos = new[]
            {
                PlayerSettings.SplashScreenLogo.Create(SegundosLogo, sprite)
            };

            AssetDatabase.SaveAssets();
            Debug.Log($"[Splash] Pantalla de presentacion configurada con el logotipo institucional " +
                      $"({SegundosLogo:0.0} s), en secuencia con el de Unity, sobre fondo negro.");

            AvisarSiElLogoEsOscuro(sprite);
        }

        /// <summary>
        /// Sobre fondo negro, un logotipo de trazo oscuro resulta invisible. Como el fichero puede
        /// cambiarse en cualquier momento por otra version, la comprobacion se hace aqui y no se
        /// dan por supuestos sus colores.
        ///
        /// La solucion correcta no es aclarar el fondo --- el motivo de que sea negro es el confort
        /// visual dentro del visor --- sino usar la version en negativo del logotipo, que las
        /// instituciones publican junto a la principal precisamente para fondos oscuros.
        /// </summary>
        private static void AvisarSiElLogoEsOscuro(Sprite sprite)
        {
            var tex = sprite.texture;
            if (!tex.isReadable) return;   // sin acceso a los pixeles no se puede comprobar

            double suma = 0; int n = 0;
            var pixeles = tex.GetPixels32();
            for (int i = 0; i < pixeles.Length; i += 37)   // muestreo disperso: basta para la media
            {
                var p = pixeles[i];
                if (p.a < 40) continue;                    // el fondo transparente no cuenta
                suma += (p.r + p.g + p.b) / 3.0;
                n++;
            }
            if (n == 0) return;

            double luminancia = suma / n;
            if (luminancia < 110)
                Debug.LogWarning($"[Splash] El logotipo tiene un trazo oscuro (luminancia media " +
                                  $"{luminancia:0}/255) y el fondo es negro: apenas se vera. Sustituye " +
                                  $"{RutaLogo} por la version en negativo del logotipo institucional.");
        }

        private static bool YaConfigurada()
        {
            var logos = PlayerSettings.SplashScreen.logos;
            if (logos == null || logos.Length == 0) return false;

            var esperado = AssetDatabase.LoadAssetAtPath<Sprite>(RutaLogo);
            if (esperado == null) return false;

            foreach (var l in logos)
                if (l.logo == esperado) return true;

            return false;
        }
    }
}
