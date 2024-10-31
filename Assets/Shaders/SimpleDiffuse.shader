Shader "RayTracing/SimpleDiffuse"
{
    Properties
    { 
        _Color("Main Color", Color) = (1, 1, 1, 1)
        _MainTex("Albedo (RGB)", 2D) = "white" {}
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "DisableBatching" = "True" }
        LOD 100

        Pass
        {
            CGPROGRAM

            #pragma vertex vert
            #pragma fragment frag

            #include "UnityCG.cginc"

            float3 _Color;

            Texture2D<float4> _MainTex;
            SamplerState sampler__MainTex;
            float4 _MainTex_ST;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv0 : TEXCOORD0;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
                float2 uv0 : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv0 = v.uv0;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 texColor = _MainTex.Sample(sampler__MainTex, i.uv0 * _MainTex_ST.xy + _MainTex_ST.yw).rgb;
                return fixed4(_Color.xyz * texColor, 1);
            }

            ENDCG
        }
    }

    SubShader
    {
        Pass
        {
            Name "Test"

            HLSLPROGRAM

            #include "UnityRayTracingMeshUtils.cginc"
            #include "RayPayload.hlsl"
            #include "GlobalResources.hlsl"

            #pragma raytracing some_name

            float3 _Color;
            
            Texture2D<float4> _MainTex;
            SamplerState sampler__MainTex;
            float4 _MainTex_ST;

            struct AttributeData
            {
                float2 barycentrics;
            };

            struct Vertex
            {
                float2 uv;
            };

            Vertex FetchVertex(uint vertexIndex)
            {
                Vertex v;
                v.uv = UnityRayTracingFetchVertexAttribute2(vertexIndex, kVertexAttributeTexCoord0);
                return v;
            }

            Vertex InterpolateVertices(Vertex v0, Vertex v1, Vertex v2, float3 barycentrics)
            {
                Vertex v;
#define INTERPOLATE_ATTRIBUTE(attr) v.attr = v0.attr * barycentrics.x + v1.attr * barycentrics.y + v2.attr * barycentrics.z
                INTERPOLATE_ATTRIBUTE(uv);
                return v;
            }

            [shader("closesthit")]
            void ClosestHitMain(inout RayPayload payload : SV_RayPayload, AttributeData attribs : SV_IntersectionAttributes)
            {
                uint3 triangleIndices = UnityRayTracingFetchTriangleIndices(PrimitiveIndex());

                Vertex v0, v1, v2;
                v0 = FetchVertex(triangleIndices.x);
                v1 = FetchVertex(triangleIndices.y);
                v2 = FetchVertex(triangleIndices.z);

                float3 barycentricCoords = float3(1.0 - attribs.barycentrics.x - attribs.barycentrics.y, attribs.barycentrics.x, attribs.barycentrics.y);
                Vertex v = InterpolateVertices(v0, v1, v2, barycentricCoords);

                // Simple mip level based on intersection T to avoid noise. It should depend on texture size and scale and other factors (uv ray differentials).
                float level = 1.2f * pow(RayTCurrent() * 0.04f, 0.5);
                
                // Simulate trilinear filtering by sampling 2 neighbour mip levels
                float3 texColor1 = _MainTex.SampleLevel(sampler__MainTex, v.uv * _MainTex_ST.xy + _MainTex_ST.yw, (uint)level).rgb;
                float3 texColor2 = _MainTex.SampleLevel(sampler__MainTex, v.uv * _MainTex_ST.xy + _MainTex_ST.yw, (uint)(level + 1)).rgb;

                payload.color = _Color.xyz * lerp(texColor1, texColor2, frac(level));
            }

            ENDHLSL
        }
    }
}
