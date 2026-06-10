Shader "ProceduralHands/Highlight"
{
    // Brillo de borde (rim) Fresnel aditivo, pensado para la copia de malla ligeramente agrandada que
    // el resaltador crea alrededor de un grabbable. URP unlit, transparente y sin escritura de profundidad.
    Properties
    {
        _Color ("Color", Color) = (0.35, 0.75, 1.0, 1.0)            // color del brillo
        _Intensity ("Intensidad", Range(0, 6)) = 1.6                // multiplicador general del brillo
        _FresnelPower ("Potencia Fresnel", Range(0.25, 8)) = 3.0    // cuán fino/marcado es el borde
        _Base ("Brillo base", Range(0, 1)) = 0.12                   // brillo mínimo en toda la superficie
    }

    SubShader
    {
        Tags { "RenderType" = "Transparent" "Queue" = "Transparent" "RenderPipeline" = "UniversalPipeline" }

        Pass
        {
            Name "Highlight"
            Blend One One        // mezcla aditiva: el brillo se suma a lo que haya detrás
            ZWrite Off           // no escribimos profundidad (es un efecto transparente encima de la malla)
            Cull Back            // descartamos las caras traseras

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct Attributes
            {
                float4 positionOS : POSITION;
                float3 normalOS : NORMAL;
            };

            struct Varyings
            {
                float4 positionHCS : SV_POSITION;
                float3 normalWS : TEXCOORD0;
                float3 viewDirWS : TEXCOORD1;
            };

            CBUFFER_START(UnityPerMaterial)
                float4 _Color;
                float _Intensity;
                float _FresnelPower;
                float _Base;
            CBUFFER_END

            Varyings vert (Attributes IN)
            {
                Varyings OUT;
                // Posiciones del vértice en los distintos espacios (objeto → mundo → clip).
                VertexPositionInputs positions = GetVertexPositionInputs(IN.positionOS.xyz);
                // Posición en espacio de recorte (clip), la que necesita el rasterizador.
                OUT.positionHCS = positions.positionCS;
                // Normal en espacio de mundo (para el cálculo Fresnel en el fragment).
                OUT.normalWS = TransformObjectToWorldNormal(IN.normalOS);
                // Dirección hacia la cámara en espacio de mundo (también para el Fresnel).
                OUT.viewDirWS = GetWorldSpaceViewDir(positions.positionWS);
                return OUT;
            }

            half4 frag (Varyings IN) : SV_Target
            {
                // Renormalizamos tras la interpolación (la interpolación entre vértices acorta los vectores).
                float3 normalWS = normalize(IN.normalWS);
                float3 viewDirWS = normalize(IN.viewDirWS);
                // Fresnel: 1 en los bordes (normal perpendicular a la vista) y 0 de frente; _FresnelPower controla el grosor del borde.
                float fresnel = pow(1.0 - saturate(dot(normalWS, viewDirWS)), _FresnelPower);
                // Color final = color base * intensidad * (borde + brillo base constante).
                half3 color = _Color.rgb * (_Intensity * (fresnel + _Base));
                // Alfa 1 pero con blend aditivo (One One): el negro no aporta, solo suma el brillo.
                return half4(color, 1.0);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
