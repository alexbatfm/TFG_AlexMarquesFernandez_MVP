using System.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.SceneManagement;

namespace DigitalTwin.MR
{
    /// <summary>
    /// Instrumentación del CAMINO DEL ALFA hasta el objetivo de render final — la pregunta que
    /// las rondas 7-9 no llegaron a hacer y que el logcat del 17-08 (sesión 13:08) dejó en
    /// evidencia: la capa de transparencia se crea (XrResult=XR_SUCCESS), el servicio del sistema
    /// la muestra y la cámara física se enciende, pero NO SE VE VÍDEO. Con la capa SUBYACENTE
    /// (<c>Underlay</c>) el vídeo solo asoma donde el fotograma de la aplicación llega al
    /// compositor con alfa &lt; 1; si el objetivo de render final no conserva el alfa, la capa de
    /// proyección tapa la subyacente por completo y el resultado es negro, sin un solo error.
    ///
    /// ESTA CLASE NO CAMBIA NADA. Solo lee y registra, una vez por carga de ARScene, los ajustes
    /// que deciden si el alfa sobrevive hasta la capa de proyección:
    ///
    ///  - <see cref="Graphics.preserveFramebufferAlpha"/> (opción de reproductor «Preserve
    ///    Framebuffer Alpha»): es el interruptor RAÍZ. URP lo lee en
    ///    <c>UniversalRenderPipeline.CreateCameraData</c> como <c>needsAlphaChannel</c>; si es
    ///    falso Y hay HDR a 32 bits, <c>MakeRenderTextureGraphicsFormat</c> elige
    ///    <c>B10G11R11_UFloatPack32</c>, que NO TIENE CANAL ALFA, y a partir de ahí el alfa cero
    ///    de la cámara es física­mente irrecuperable (verificado en el fuente de URP 17 instalado).
    ///  - Ajustes del render pipeline activo: HDR, precisión del búfer HDR, MSAA y salida de alfa
    ///    del postprocesado. Con HDR + 32 bits + sin «preserve alpha» → formato sin alfa.
    ///  - Estado de la cámara principal: modo de borrado y alfa del color de borrado.
    ///
    /// Además deja escrito el VEREDICTO derivado (¿el formato final tendrá alfa?) para que la
    /// próxima sesión de visor lo confirme en una sola pasada, y un <c>LogError</c> si en el
    /// dispositivo el camino del alfa NO puede transportar transparencia: en ese caso el vídeo de
    /// transparencia es imposible por construcción, no por azar.
    ///
    /// El modo de mezcla con el entorno (<c>XrEnvironmentBlendMode</c>) NO se re-instrumenta aquí:
    /// el plugin de OpenXR ya lo publica en el logcat con la línea «Available Environment Blend
    /// Modes», y en la sesión del 17-08 quedó MEDIDO que el visor ofrece una única opción,
    /// <c>XR_ENVIRONMENT_BLEND_MODE_OPAQUE</c>. Con passthrough por capa subyacente eso es lo
    /// esperado y correcto: la visibilidad del vídeo NO depende del modo de mezcla, sino del alfa
    /// de la capa de proyección, que es lo que esta clase mide.
    /// </summary>
    internal static class MRPassthroughDiagnostics
    {
        private const string NombreEscenaMR = "ARScene";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Instalar()
        {
            // Un solo disparo por proceso para la suscripción; el barrido de la vuelta al selector
            // destruye el objeto runner (prefijo ~), pero la suscripción sobrevive y lo recrea en
            // cada recarga de ARScene, igual que la corrección de MRInputDiagnostics de la ronda 9.
            SceneManager.sceneLoaded -= AlCargarEscena;
            SceneManager.sceneLoaded += AlCargarEscena;
            LanzarSiProcede(SceneManager.GetActiveScene());
        }

        private static void AlCargarEscena(Scene escena, LoadSceneMode modo) => LanzarSiProcede(escena);

        private static void LanzarSiProcede(Scene escena)
        {
            if (escena.name != NombreEscenaMR) return;
            var go = new GameObject("~DiagnosticoAlfaAR");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<Runner>();
        }

        private sealed class Runner : MonoBehaviour
        {
            private IEnumerator Start()
            {
                // Esperar a que la cámara y el pipeline estén asentados (la capa de transparencia
                // se crea a los 90 fotogramas; con 100 el estado ya es el definitivo).
                for (int i = 0; i < 100; i++) yield return null;
                Registrar();
                Destroy(gameObject);
            }

            private static void Registrar()
            {
                bool preserveAlpha = Graphics.preserveFramebufferAlpha;

                var rp = GraphicsSettings.currentRenderPipeline as UniversalRenderPipelineAsset;
                if (rp == null)
                {
                    Debug.LogError("[DigitalTwin][AR][Alfa] No hay UniversalRenderPipelineAsset " +
                                   "activo (currentRenderPipeline nulo o no-URP). No se puede " +
                                   "auditar el camino del alfa; el resto del diagnostico no aplica.");
                    return;
                }

                bool hdr = rp.supportsHDR;
                var precision = rp.hdrColorBufferPrecision;   // _32Bits / _64Bits
                int msaa = rp.msaaSampleCount;
                bool postAlpha = rp.allowPostProcessAlphaOutput;
                bool precision64 = precision == HDRColorBufferPrecision._64Bits;

                // Réplica exacta de UniversalRenderPipeline.MakeRenderTextureGraphicsFormat
                // (URP 17): con HDR, si NO se pide alfa y la precision no es de 64 bits, el formato
                // es B10G11R11 (sin alfa); en cualquier otro caso el objetivo lleva alfa.
                bool formatoConAlfa = !hdr || preserveAlpha || precision64;
                string formatoEsperado = !hdr ? "LDR R8G8B8A8 (con alfa)"
                    : (formatoConAlfa ? "R16G16B16A16_SFloat (con alfa)"
                                      : "B10G11R11_UFloatPack32 (SIN alfa)");

                var cam = Camera.main;
                string estadoCam = cam != null
                    ? $"clearFlags={cam.clearFlags}, fondoAlfa={cam.backgroundColor.a:0.00}, allowHDR={cam.allowHDR}, allowMSAA={cam.allowMSAA}"
                    : "SIN Camera.main";

                Debug.LogWarning(
                    "[DigitalTwin][AR][Alfa] Camino del alfa hasta el objetivo final: " +
                    $"preserveFramebufferAlpha={preserveAlpha}; pipeline='{rp.name}', HDR={hdr}, " +
                    $"precisionHDR={precision}, MSAA={msaa}x, allowPostProcessAlphaOutput={postAlpha}; " +
                    $"camara[{estadoCam}]. Formato de objetivo esperado: {formatoEsperado}.");

                if (!formatoConAlfa)
                {
                    Debug.LogError(
                        "[DigitalTwin][AR][Alfa] EL OBJETIVO DE RENDER FINAL NO LLEVA CANAL ALFA " +
                        "(HDR a 32 bits sin 'Preserve Framebuffer Alpha'): el alfa cero de la " +
                        "camara se descarta y la capa de proyeccion se compone OPACA sobre la capa " +
                        "de transparencia SUBYACENTE. El video de transparencia es IMPOSIBLE en " +
                        "este estado, con o sin capa creada. Solucion: activar 'Preserve " +
                        "Framebuffer Alpha' en Player Settings (o desactivar HDR / usar precision " +
                        "de 64 bits). Ver PRUEBA-AR-10-2026-08-17.md.");
                }
                else if (hdr && !precision64 && !preserveAlpha)
                {
                    // Inalcanzable dado formatoConAlfa, pero deja constancia de la ruta.
                    Debug.LogWarning("[DigitalTwin][AR][Alfa] (nota) HDR 32 bits sin preserve alpha.");
                }
                else
                {
                    Debug.LogWarning(
                        "[DigitalTwin][AR][Alfa] El objetivo de render final SI lleva alfa: el " +
                        "camino esta preparado para transportar la transparencia. Si aun asi no se " +
                        "ve video, la causa esta por debajo (compositor / servicio de camaras), no " +
                        "en el camino del alfa de URP.");
                }
            }
        }
    }
}
