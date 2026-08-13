// Oclusor de profundidad para el modo anclado de Realidad Aumentada.
//
// Escribe SOLO en el bufer de profundidad (ColorMask 0 + ZWrite On): la geometria no se ve,
// pero impide ver a traves de ella. Con la camara borrando a alfa cero, el color queda
// intacto alli donde solo dibuja este sombreador, asi que el video de transparencia sigue
// mostrandose; lo que cambia es que cualquier objeto virtual situado DETRAS de un muro real
// falla la prueba de profundidad y desaparece, que es exactamente la correccion que aporta
// disponer de la malla BIM del edificio.
//
// POR QUE VIVE EN Resources/ Y NO SE BUSCA CON Shader.Find A SECAS: Shader.Find devuelve null
// en compilacion para sombreadores no incluidos (ya costo un ciclo de depuracion entero, ver
// RuntimeMaterials). Los assets bajo Resources/ se incluyen SIEMPRE en la build, asi que
// Resources.Load<Shader> lo encuentra tambien en el dispositivo. MROcclusionService registra
// un error si aun asi faltara.
//
// POR QUE LLEVA LAS MACROS DE INSTANCIACION ESTEREO: el visor renderiza en una pasada
// instanciada (una instancia por ojo). Un sombreador sin estas macros dibuja solo en el ojo
// izquierdo o con matrices del ojo equivocado — otro fallo de la familia "codigo correcto que
// no se ejecuta donde debe y no da error". La lista de comprobacion del visor incluye cerrar
// un ojo y luego el otro para verificar esto.
Shader "DigitalTwin/OclusorProfundidad"
{
    SubShader
    {
        // Geometry-10: se dibuja al principio del tramo opaco. Para la correccion da igual el
        // orden dentro de los opacos (la prueba de profundidad es conmutativa), pero dibujar
        // los oclusores pronto descarta antes los fragmentos de lo que queda detras.
        Tags { "RenderType"="Opaque" "Queue"="Geometry-10" "RenderPipeline"="UniversalPipeline" }

        Pass
        {
            Name "OclusorProfundidad"
            ZWrite On
            ZTest LEqual
            // Tres mecanismos independientes para NO escribir color, tras la prueba del
            // 2026-08-13 en la que el modo anclado se vio negro con los sensores delante
            // (cuadro compatible con oclusores pintando opaco): ColorMask 0 descarta la
            // escritura, Blend Zero One deja el destino intacto aunque la escritura ocurriera
            // (resultado = 0*fuente + 1*destino, tambien en alfa), y el fragmento devuelve
            // (0,0,0,0) por si ambos fallaran en algun controlador. Si aun asi se pinta, el
            // culpable no es este fichero y las trazas de MROcclusionService lo acotan.
            ColorMask 0
            Blend Zero One, Zero One
            Cull Back

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.positionCS = TransformObjectToHClip(IN.positionOS.xyz);
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                // ColorMask 0: este valor nunca llega al bufer de color.
                return half4(0, 0, 0, 0);
            }
            ENDHLSL
        }
    }
    Fallback Off
}
