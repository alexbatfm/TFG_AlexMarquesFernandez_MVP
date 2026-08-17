using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace DigitalTwin.EditorTools
{
    /// <summary>
    /// Asigna el icono de la aplicación en los ajustes del reproductor: los cuatro juegos que pide
    /// Android para el APK del visor (adaptativo en dos capas, redondo y heredado) y el icono por
    /// defecto del proyecto, que es el que recibe el ejecutable de escritorio.
    ///
    /// Qué problema resuelve. Hasta el 17-08-2026 todas las entradas de icono de Android estaban
    /// vacías (<c>m_Textures: []</c> en <c>ProjectSettings.asset</c>) y la aplicación aparecía en la
    /// biblioteca del visor con el icono genérico de Unity. Los ficheros están en
    /// <c>Assets/Branding/AppIcon/</c>, generados uno a uno a su tamaño final (no reescalados de un
    /// único máster; ver el <c>LEEME-instalacion.md</c> de esa carpeta, que trae la tabla de qué
    /// fichero va en qué casilla). Son veinticuatro casillas repartidas en tres pestañas y dos capas: es
    /// una asignación que se hace una sola vez, pero en la que una casilla equivocada —el fondo en
    /// la capa del frente, un 162 en el hueco del 216— no se detecta hasta tener el APK instalado.
    /// Por eso se hace desde código y no arrastrando.
    ///
    /// Por qué es una opción de menú y no se aplica sola. Como <see cref="ConfigurarPublicacion"/>:
    /// no hay decisión que tomar cada vez, pero sí un efecto sobre el fichero de ajustes en cada
    /// ejecución, y una herramienta que reescribe los iconos en cada recarga de dominio marca el
    /// proyecto como modificado sin motivo. Se acompaña de una comprobación de solo lectura que dice
    /// qué casillas están vacías o apuntan a otro fichero.
    ///
    /// Por qué no referencia <c>UnityEditor.Android.AndroidPlatformIconKind</c> en el código. Esa
    /// clase vive en el módulo de compilación para Android; si el proyecto se abre en un editor sin
    /// ese módulo, un script que la nombre no compila, y un script de editor que no compila bloquea
    /// el proyecto entero, no solo a sí mismo. Se localiza por reflexión y, si no está, se avisa y no
    /// se toca nada. Es el mismo argumento por el que <see cref="ConfigurarPublicacion"/> dejó los
    /// iconos fuera en su día.
    ///
    /// Tamaños que Unity pide y el paquete no trae: 81 (adaptativo) y 36 (redondo y heredado). Se
    /// rellenan con el fichero inmediatamente mayor (108 y 48) y Unity los reduce al empaquetar; son
    /// las densidades más bajas y la pérdida por reescalado es menor que dejar la casilla vacía, que
    /// obliga al motor a reescalar desde el icono por defecto.
    /// </summary>
    public static class ConfigurarIconosApp
    {
        private const string Carpeta = "Assets/Branding/AppIcon/";
        private const string Master = Carpeta + "icono_master_1024.png";

        // Tamaños disponibles en disco por juego (los que genera el paquete de identidad).
        private static readonly int[] TamanosAdaptativo = { 432, 324, 216, 162, 108 };
        private static readonly int[] TamanosSencillo = { 192, 144, 96, 72, 48 };

        private enum Juego { Adaptativo, Redondo, Heredado }

        [MenuItem("Tools/TFG/Asignar iconos de la aplicación")]
        public static void Aplicar()
        {
            var master = AssetDatabase.LoadAssetAtPath<Texture2D>(Master);
            if (master == null)
            {
                Debug.LogError($"[Iconos] Falta {Master}. Copia la carpeta Branding/ del paquete de " +
                               "identidad bajo Assets/ y vuelve a ejecutar; no se ha tocado nada.");
                return;
            }

            // Icono por defecto del proyecto: lo usa el ejecutable de Windows y cualquier
            // plataforma sin juego propio. NamedBuildTarget.Unknown es, en esta API, «por defecto».
            PlayerSettings.SetIcons(NamedBuildTarget.Unknown, new[] { master }, IconKind.Any);

            var android = NamedBuildTarget.Android;
            var kinds = PlayerSettings.GetPlatformIconKinds(android);
            if (kinds == null || kinds.Length == 0)
            {
                Debug.LogError("[Iconos] Unity no devuelve tipos de icono para Android: falta el módulo " +
                               "de compilación para Android en este editor. Icono por defecto asignado; " +
                               "los juegos del APK quedan sin tocar.");
                AssetDatabase.SaveAssets();
                return;
            }

            var resumen = new List<string>();
            var faltan = new List<string>();
            int casillas = 0;

            foreach (var kind in kinds)
            {
                Juego? juego = Identificar(kind);
                if (juego == null)
                {
                    Debug.LogWarning($"[Iconos] Tipo de icono de Android no reconocido ({kind}); se deja " +
                                     "como está.");
                    continue;
                }

                var iconos = PlayerSettings.GetPlatformIcons(android, kind);
                foreach (var icono in iconos)
                {
                    Texture2D[] texturas = TexturasPara(juego.Value, icono.width, icono.maxLayerCount, faltan);
                    if (texturas == null) continue;
                    icono.SetTextures(texturas);
                    casillas += texturas.Length;
                }
                PlayerSettings.SetPlatformIcons(android, kind, iconos);
                resumen.Add($"{juego.Value} ({iconos.Length} tamaños)");
            }

            AssetDatabase.SaveAssets();

            string texto = $"[Iconos] Asignadas {casillas} casillas de Android: {string.Join(", ", resumen)}. " +
                           $"Icono por defecto del proyecto: {Master}. Los tamaños 81 y 36 se sirven " +
                           "reescalando 108 y 48. Compila el APK y mira la biblioteca del visor: el " +
                           "icono es un cubo azul macizo y otro en alambre sobre fondo oscuro.";
            if (faltan.Count > 0)
                Debug.LogError(texto + $" FALTAN ficheros: {string.Join(", ", faltan)}; esas casillas se han dejado como estaban.");
            else
                Debug.LogWarning(texto);
        }

        /// <summary>
        /// Vuelca, sin cambiar nada, qué tiene cada casilla. Sirve para saber antes de compilar si
        /// se va a empaquetar el icono previsto, y para verificar el resultado de <see cref="Aplicar"/>
        /// sin abrir el panel de ajustes.
        /// </summary>
        [MenuItem("Tools/TFG/Comprobar iconos de la aplicación")]
        public static void Comprobar()
        {
            var porDefecto = PlayerSettings.GetIcons(NamedBuildTarget.Unknown, IconKind.Any);
            string def = (porDefecto != null && porDefecto.Length > 0 && porDefecto[0] != null)
                ? AssetDatabase.GetAssetPath(porDefecto[0]) : "VACÍO";

            var android = NamedBuildTarget.Android;
            var kinds = PlayerSettings.GetPlatformIconKinds(android) ?? Array.Empty<PlatformIconKind>();
            var lineas = new List<string> { $"[Iconos] Icono por defecto: {def}." };
            int vacias = 0, ajenas = 0;

            foreach (var kind in kinds)
            {
                foreach (var icono in PlayerSettings.GetPlatformIcons(android, kind))
                {
                    var texturas = icono.GetTextures() ?? Array.Empty<Texture2D>();
                    var nombres = new List<string>();
                    for (int capa = 0; capa < icono.maxLayerCount; capa++)
                    {
                        var t = capa < texturas.Length ? texturas[capa] : null;
                        if (t == null) { nombres.Add("VACÍO"); vacias++; continue; }
                        string ruta = AssetDatabase.GetAssetPath(t);
                        if (!ruta.StartsWith(Carpeta)) ajenas++;
                        nombres.Add(System.IO.Path.GetFileName(ruta));
                    }
                    lineas.Add($"  {kind} {icono.width}: {string.Join(" / ", nombres)}");
                }
            }

            string informe = string.Join("\n", lineas);
            if (kinds.Length == 0)
                Debug.LogError(informe + "\nUnity no devuelve tipos de icono para Android (¿falta el módulo?).");
            else if (vacias > 0 || ajenas > 0)
                Debug.LogError(informe + $"\n{vacias} casillas vacías y {ajenas} con un fichero ajeno a " +
                               $"{Carpeta}: ejecuta Tools > TFG > Asignar iconos de la aplicación.");
            else
                Debug.LogWarning(informe + "\nTodas las casillas de Android apuntan al juego de identidad.");
        }

        // ------------------------------------------------------------------------------------

        private static Texture2D[] TexturasPara(Juego juego, int ancho, int capas, List<string> faltan)
        {
            if (juego == Juego.Adaptativo)
            {
                int n = Ajustar(ancho, TamanosAdaptativo);
                var fondo = Cargar($"adaptive_background_{n}.png", faltan);
                var frente = Cargar($"adaptive_foreground_{n}.png", faltan);
                if (fondo == null || frente == null) return null;
                // Orden de capas del icono adaptativo en Unity: primero el fondo, después el frente.
                // El fichero de fondo es opaco (#16181C) y el del frente lleva el símbolo con alfa;
                // invertirlos daría un icono negro sin símbolo, sin error alguno.
                if (capas >= 2) return new[] { fondo, frente };
                return new[] { frente };
            }

            string prefijo = juego == Juego.Redondo ? "round" : "legacy";
            var tex = Cargar($"{prefijo}_{Ajustar(ancho, TamanosSencillo)}.png", faltan);
            return tex == null ? null : new[] { tex };
        }

        /// <summary>El tamaño exacto si existe; si no, el inmediatamente mayor disponible.</summary>
        private static int Ajustar(int ancho, int[] disponibles)
        {
            var mayores = disponibles.Where(d => d >= ancho).ToArray();
            return mayores.Length > 0 ? mayores.Min() : disponibles.Max();
        }

        private static Texture2D Cargar(string fichero, List<string> faltan)
        {
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(Carpeta + fichero);
            if (tex == null && !faltan.Contains(fichero)) faltan.Add(fichero);
            return tex;
        }

        /// <summary>
        /// Reconoce el juego al que pertenece un tipo de icono sin nombrar la clase del módulo de
        /// Android en tiempo de compilación. Primero por identidad con los campos estáticos de
        /// <c>UnityEditor.Android.AndroidPlatformIconKind</c> (localizada por reflexión); si eso
        /// falla, por el nombre que el propio tipo declara.
        /// </summary>
        private static Juego? Identificar(PlatformIconKind kind)
        {
            var tipo = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("UnityEditor.Android.AndroidPlatformIconKind", false))
                .FirstOrDefault(t => t != null);
            if (tipo != null)
            {
                if (kind.Equals(Estatico(tipo, "Adaptive"))) return Juego.Adaptativo;
                if (kind.Equals(Estatico(tipo, "Round"))) return Juego.Redondo;
                if (kind.Equals(Estatico(tipo, "Legacy"))) return Juego.Heredado;
            }
            string nombre = (kind.ToString() ?? string.Empty).ToLowerInvariant();
            if (nombre.Contains("adaptive")) return Juego.Adaptativo;
            if (nombre.Contains("round")) return Juego.Redondo;
            if (nombre.Contains("legacy")) return Juego.Heredado;
            return null;
        }

        private static object Estatico(Type tipo, string campo) =>
            tipo.GetField(campo, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
    }
}
