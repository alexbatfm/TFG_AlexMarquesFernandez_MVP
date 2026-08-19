// Rayos X del activo seleccionado (visibilidad selectiva, T10 en version reducida, 19-08-2026).
//
// QUE HACE. Dibuja SOLO la parte de una malla que queda DETRAS de algo ya escrito en el bufer de
// profundidad: ZTest Greater. La parte visible del activo no pasa por aqui (la dibuja su material
// de siempre, con la prueba de profundidad normal); esta pasada solo aporta la parte oculta, y la
// aporta con un aspecto DISTINTO a proposito: silueta translucida en el color de seleccion, con
// un borde mas claro (Fresnel) y una trama de rayas horizontales en espacio de mundo.
//
// POR QUE SE DISTINGUE LA PARTE OCULTA EN VEZ DE DIBUJAR EL ACTIVO ENTERO "siempre encima". Si el
// sensor se viera igual delante que detras de un muro, la imagen mentiria sobre la profundidad:
// el operario no sabria si el activo esta en su sala o en la contigua, que es justo la pregunta
// que va a hacerse con el casco puesto. La convencion del dibujo tecnico para lo oculto es la
// linea discontinua; aqui, la trama y la translucidez. La frontera entre el aspecto normal y el
// tramado dibuja ademas el canto del oclusor, que en modo anclado es invisible.
//
// POR QUE LA TRAMA VA EN ESPACIO DE MUNDO Y NO DE PANTALLA. Una trama de pantalla se rasteriza en
// posiciones distintas en cada ojo (rivalidad binocular: centelleo), y se desliza al mover la
// cabeza. Una trama fija al mundo (rayas por altura, periodo en metros) es la misma en los dos
// ojos y se queda quieta: se lee como una propiedad del objeto y no de la pantalla.
//
// COLA Y PROFUNDIDAD. Queue Transparent-10 (2990): despues de los oclusores (Geometry-10, 1990)
// y de los propios sensores (Geometry), que ya han escrito su profundidad, y ANTES de los lienzos
// de interfaz (3000), para que la ficha siga pintandose encima. ZWrite Off: no debe tapar nada.
//
// MODOS (propiedad _Trama): 0 = malla (Fresnel + rayas por altura; Cull Back); 1 = linea
// (guiones a lo largo de la coordenada U del LineRenderer; sin Fresnel; Cull Off, porque la tira
// de la linea mira a la camara y su orientacion no esta garantizada).
//
// Vive en Resources/ por el mismo motivo que OclusorProfundidad: Shader.Find devuelve null en la
// compilacion para sombreadores no referenciados por ningun material del proyecto. Macros de
// instanciacion estereo obligatorias (una pasada instanciada en el visor).
Shader "DigitalTwin/RayosX"
{
    Properties
    {
        _ColorOculto ("Color de la parte oculta", Color) = (1.0, 0.78, 0.15, 0.55)
        _ColorBorde  ("Color del borde (Fresnel)", Color) = (1.0, 0.92, 0.55, 0.95)
        _Trama       ("Trama: 0 rayas por altura (malla), 1 guiones a lo largo (linea)", Float) = 0
        _Periodo     ("Periodo de la trama (m para la malla; repeticiones para la linea)", Float) = 0.04
        _Cull        ("Cull", Float) = 2
    }

    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent-10" "RenderPipeline"="UniversalPipeline"
               "IgnoreProjector"="True" }

        Pass
        {
            Name "RayosXOculto"
            ZWrite Off
            ZTest Greater
            Cull [_Cull]
            Blend SrcAlpha OneMinusSrcAlpha

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                half4 _ColorOculto;
                half4 _ColorBorde;
                float _Trama;
                float _Periodo;
                float _Cull;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS   : NORMAL;
                float2 uv         : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float3 normalWS   : TEXCOORD1;
                float2 uv         : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes IN)
            {
                Varyings OUT;
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_TRANSFER_INSTANCE_ID(IN, OUT);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(OUT);
                OUT.positionWS = TransformObjectToWorld(IN.positionOS.xyz);
                OUT.positionCS = TransformWorldToHClip(OUT.positionWS);
                OUT.normalWS   = TransformObjectToWorldNormal(IN.normalOS);
                OUT.uv         = IN.uv;
                return OUT;
            }

            half4 frag(Varyings IN) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(IN);
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(IN);

                half4 color = _ColorOculto;

                if (_Trama < 0.5)
                {
                    // Malla: borde claro donde la superficie se ve de canto (Fresnel), y rayas
                    // horizontales fijas al mundo. La raya clara lleva el alfa completo y la
                    // oscura algo menos de la mitad: se sigue viendo la forma entera, pero con la
                    // textura inconfundible de "oculto".
                    half3 n = normalize(IN.normalWS);
                    half3 v = GetWorldSpaceNormalizeViewDir(IN.positionWS);
                    half fres = 1.0h - saturate(abs(dot(n, v)));
                    fres = fres * fres;
                    color = lerp(_ColorOculto, _ColorBorde, fres);

                    float periodo = max(_Periodo, 0.002);
                    float raya = frac(IN.positionWS.y / periodo);
                    half trama = raya < 0.5 ? 1.0h : 0.40h;
                    color.a *= trama;
                }
                else
                {
                    // Linea: guiones a lo largo de U (en modo Stretch, U recorre 0..1 toda la
                    // linea, asi que _Periodo es el numero de guiones).
                    float guion = frac(IN.uv.x * max(_Periodo, 1.0));
                    if (guion > 0.5) discard;
                }

                return color;
            }
            ENDHLSL
        }
    }
    Fallback Off
}
