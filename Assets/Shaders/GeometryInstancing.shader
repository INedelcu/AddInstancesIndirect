Shader "RayTracing/MeshInstancing"
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

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 vertex : SV_POSITION;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                return o;
            }

            fixed4 frag() : SV_Target
            {
                return fixed4(1, 1, 1, 1);
            }

            ENDCG
        }
    }

    SubShader
    {
        Tags { "RenderType" = "Opaque" "DisableBatching" = "True" }

        Pass
        {
            // RayTracingShader.SetShaderPass must use this name in order to execute the ray tracing shaders from this Pass.
            Name "Test1"

            HLSLPROGRAM

            #pragma multi_compile _ INSTANCING_ON

            // Specify this shader is a raytracing shader. The name is not important.
            #pragma raytracing test

            struct AttributeData
            {
                float2 barycentrics;
            };

            struct RayPayload
            {
                float3 color;
            };

            // Set by Unity.
            uint unity_BaseInstanceID;

            StructuredBuffer<float3> InstanceColors;

            float3 _Color;

            [shader("closesthit")]
            void ClosestHitMain(inout RayPayload payload, AttributeData attribs)
            {
    #if INSTANCING_ON
                uint instanceIndex = InstanceIndex() - unity_BaseInstanceID;
                payload.color = InstanceColors[instanceIndex];
    #else
                payload.color = _Color;
    #endif
            }

            ENDHLSL
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

            // Use INSTANCING_ON shader keyword for supporting instanced and non-instanced geometries.
            // Unity will setup SH coeffiecients - unity_SHAArray, unity_SHBArray, etc when RayTracingAccelerationStructure.AddInstances is used.
            #pragma multi_compile _ INSTANCING_ON

#if INSTANCING_ON
            // Unity built-in shader property and represents the index of the fist ray tracing Mesh instance in the TLAS.
            uint unity_BaseInstanceID;

            // How many ray tracing instances were added using RayTracingAccelerationStructure.AddInstances is used. Not used here.
            uint unity_InstanceCount;
#endif

            int g_EnableRayBounce;

            float3 g_CameraPos;

            StructuredBuffer<float3> g_Colors;
            StructuredBuffer<uint> g_InstanceIndices;

            Texture2D<float4> _MainTex;
            SamplerState sampler__MainTex;
            float4 _MainTex_ST;

            struct AttributeData
            {
                float2 barycentrics;
            };

            struct Vertex
            {
                float3 position;
                float3 normal;
            };

            Vertex FetchVertex(uint vertexIndex)
            {
                Vertex v;
                v.position = UnityRayTracingFetchVertexAttribute3(vertexIndex, kVertexAttributePosition);
                v.normal = UnityRayTracingFetchVertexAttribute3(vertexIndex, kVertexAttributeNormal);
                return v;
            }

            Vertex InterpolateVertices(Vertex v0, Vertex v1, Vertex v2, float3 barycentrics)
            {
                Vertex v;
#define INTERPOLATE_ATTRIBUTE(attr) v.attr = v0.attr * barycentrics.x + v1.attr * barycentrics.y + v2.attr * barycentrics.z
                INTERPOLATE_ATTRIBUTE(position);
                INTERPOLATE_ATTRIBUTE(normal);
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

                bool isFrontFace = HitKind() == HIT_KIND_TRIANGLE_FRONT_FACE;

                float3 localNormal = isFrontFace ? v.normal : -v.normal;                

                float3 worldNormal = normalize(mul(localNormal, (float3x3)WorldToObject()));

                float3 reflectionVec = reflect(WorldRayDirection(), worldNormal);

                float3 reflectionCol = float3(0, 0, 0);

                float t = saturate(0.25 + pow(saturate(dot(reflectionVec, WorldRayDirection())), 5));

                float3 lightDir = float3(0.0, -1.0, 0.0);
                float3 light = saturate(dot(worldNormal, normalize(-lightDir)));
                
                if (g_EnableRayBounce == 1 && payload.bounceIndex < 2)
                {
                    float3 worldPosition = mul(ObjectToWorld(), float4(v.position, 1));

                    RayDesc ray;
                    ray.Origin = worldPosition + worldNormal * 0.005f;
                    ray.Direction = reflectionVec;
                    ray.TMin = 0;
                    ray.TMax = 1e20f;

                    RayPayload payloadRefl;
                    payloadRefl.bounceIndex = payload.bounceIndex + 1;

                    uint missShaderIndex = 0;
                    TraceRay(g_AccelStruct, 0, 0xFF, 0, 1, missShaderIndex, ray, payloadRefl);

                    reflectionCol = payloadRefl.color;
                }                
                else
                {
                    reflectionCol = g_EnvTexture.SampleLevel(sampler_g_EnvTexture, reflectionVec, 0).xyz;
                }

#if INSTANCING_ON
                uint instanceID = InstanceIndex() - unity_BaseInstanceID;

                float3 instanceColor = g_Colors[g_InstanceIndices[instanceID]];
#else
                float3 instanceColor = g_Colors[InstanceID()];
#endif
                payload.color =  lerp(light * instanceColor.xyz, reflectionCol, t);
            }

            ENDHLSL
        }
    }
}
