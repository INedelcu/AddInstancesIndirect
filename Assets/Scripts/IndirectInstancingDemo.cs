using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Profiling;
using UnityEngine.UI;
using Unity.Collections.LowLevel.Unsafe;

[ExecuteInEditMode]
public class MeshInstancing : MonoBehaviour
{
    [SerializeField] RayTracingShader rayTracingShader = null;
    [SerializeField] ComputeShader cullingShader = null;
    [SerializeField] Mesh mesh = null;
    [SerializeField] Material material = null;
    [SerializeField] Vector2Int counts = new Vector2Int(50, 50); 
    [SerializeField] Texture envTexture = null;
    [SerializeField] float cullingRadius = 200;

    public Toggle enableInstancingToggle;
    public Toggle enableRayBounceToggle;
    public Text fpsText;
    public Text titleText;

    private float lastRealtimeSinceStartup = 0;
    private float updateFPSTimer = 1.0f;

    private uint cameraWidth = 0;
    private uint cameraHeight = 0;

    private RenderTexture rayTracingOutput = null;

    private RayTracingAccelerationStructure rtas = null;

    private RayTracingInstanceData instanceData = null;

    // Add instance matrices.
    private GraphicsBuffer gpuMatrices = null;

    // Instance matrices after culling -> input to RTAS build.
    private GraphicsBuffer instanceMatrices = null;

    // Instance indices to associate them with other instance data (e.g. Per Instance Color). Filled by Instance Culling compute shader.
    // This is needed because the instanceMatrices used when building the RTAS are added in random order by the Instance Culling compute shader.
    private GraphicsBuffer instanceIndices = null;

    private GraphicsBuffer indirectArgsBuffer = null;

    private GraphicsBuffer instanceCount = null;

    private GraphicsBuffer vertexBuffer = null;
    private GraphicsBuffer indexBuffer = null;

    private void ReleaseResources()
    {
        if (rtas != null)
        {
            rtas.Release();
            rtas = null;
        }

        if (rayTracingOutput)
        {
            rayTracingOutput.Release();
            rayTracingOutput = null;
        }

        cameraWidth = 0;
        cameraHeight = 0;

        if (instanceData != null)
        {
            instanceData.Dispose();
            instanceData = null;
        }

        if (gpuMatrices != null)
        {
            gpuMatrices.Release();
            gpuMatrices = null;
        }

        if (instanceMatrices != null)
        {
            instanceMatrices.Release();
            instanceMatrices = null;
        }

        if (instanceIndices != null)
        {
            instanceIndices.Release();
            instanceIndices = null;
        }

        if (instanceCount != null)
        {
            instanceCount.Release();
            instanceCount = null;
        }

        if (indirectArgsBuffer != null)
        {
            indirectArgsBuffer.Release();
            indirectArgsBuffer = null;
        }

        if (vertexBuffer != null)
        {
            vertexBuffer.Release();
            vertexBuffer = null;
        }

        if (indexBuffer != null)
        {
            indexBuffer.Release();
            indexBuffer = null;
        }
    }

    struct Vertex
    {
        public Vertex(Vector3 pos, Vector3 n) { position = pos; normal = n; }
        public Vector3 position;
        public Vector3 normal;
    }

    private void CreateResources()
    {
        if (cameraWidth != Camera.main.pixelWidth || cameraHeight != Camera.main.pixelHeight)
        {
            if (rayTracingOutput)
                rayTracingOutput.Release();

            rayTracingOutput = new RenderTexture(Camera.main.pixelWidth, Camera.main.pixelHeight, 0, RenderTextureFormat.ARGBHalf);
            rayTracingOutput.enableRandomWrite = true;
            rayTracingOutput.Create();

            cameraWidth = (uint)Camera.main.pixelWidth;
            cameraHeight = (uint)Camera.main.pixelHeight;
        }

        if (instanceData == null || instanceData.columns != counts.x || instanceData.rows != counts.y)
        {
            if (instanceData != null)
            {
                instanceData.Dispose();
            }

            instanceData = new RayTracingInstanceData(counts.x, counts.y);

            if (gpuMatrices != null)
            {
                gpuMatrices.Release();
                gpuMatrices = null;
            }

            if (instanceMatrices != null) 
            {
                instanceMatrices.Release();
                instanceMatrices = null;
            }

            if (instanceIndices != null)
            {
                instanceIndices.Release();
                instanceIndices = null;
            }
        }

        if (gpuMatrices == null)
        {
            gpuMatrices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, instanceData.matrices.Length, UnsafeUtility.SizeOf(typeof(Matrix4x4)));

            gpuMatrices.SetData(instanceData.matrices);
        }

        // GPU matrices after culling.
        if (instanceMatrices == null)
        {
            instanceMatrices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, instanceData.matrices.Length, UnsafeUtility.SizeOf(typeof(Matrix4x4)));
        }

        // Instance indices that were added to the RTAS.
        if (instanceIndices == null)
        {
            instanceIndices = new GraphicsBuffer(GraphicsBuffer.Target.Structured, instanceData.matrices.Length, UnsafeUtility.SizeOf(typeof(uint)));
        }

        if (instanceCount == null)
        {
            instanceCount = new GraphicsBuffer(GraphicsBuffer.Target.Structured | GraphicsBuffer.Target.Counter, 1, 4);
        }

        if (indirectArgsBuffer == null)
        {
            // Use 10 sets of (instanceStart, instanceCount) pairs just for testing.
            indirectArgsBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw, 10, 8);
            uint[] ints = new uint[2 * 10];
            for (int i = 0; i < 10; i++)
            {
                ints[2 * i + 0] = 0;
                ints[2 * i + 1] = 0;
            }
            indirectArgsBuffer.SetData(ints);
        }

        // Consider counterclockwise vertices generating front face triangles.
        // Culling back faces is enabled in TraceRay.
        if (vertexBuffer == null)
        {
            List<Vertex> vertices = new List<Vertex>
            {
                //////////////////////////////////////////////////////////////
                // Add a plane.
                new Vertex(new Vector3(1, 0, -1), new Vector3(0, 1, 0)),
                new Vertex(new Vector3(1, 0, 1), new Vector3(0, 1, 0)),
                new Vertex(new Vector3(-1, 0, 1), new Vector3(0, 1, 0)),
                new Vertex(new Vector3(-1, 0, -1), new Vector3(0, 1, 0)),


                //////////////////////////////////////////////////////////////
                // Add a cube.
                // Top face.
                new Vertex(new Vector3(1, 1, -1), new Vector3(0, 1, 0)),
                new Vertex(new Vector3(1, 1, 1), new Vector3(0, 1, 0)),
                new Vertex(new Vector3(-1, 1, 1), new Vector3(0, 1, 0)),
                new Vertex(new Vector3(-1, 1, -1), new Vector3(0, 1, 0)),

                 // Front face.
                 new Vertex(new Vector3(1, -1, -1), new Vector3(0, 0, -1)),
                new Vertex(new Vector3(1, 1, -1), new Vector3(0, 0, -1)),
                new Vertex(new Vector3(-1, 1, -1), new Vector3(0, 0, -1)),
                new Vertex(new Vector3(-1, -1, -1), new Vector3(0, 0, -1)),                

                // Back face.
                new Vertex(new Vector3(1, 1, 1), new Vector3(0, 0, 1)),
                new Vertex(new Vector3(1, -1, 1), new Vector3(0, 0, 1)),
                new Vertex(new Vector3(-1, -1, 1), new Vector3(0, 0, 1)),
                new Vertex(new Vector3(-1, 1, 1), new Vector3(0, 0, 1)),

                // Left face.
                new Vertex(new Vector3(-1, 1, -1), new Vector3(-1, 0, 0)),
                new Vertex(new Vector3(-1, 1, 1), new Vector3(-1, 0, 0)),
                new Vertex(new Vector3(-1, -1, 1), new Vector3(-1, 0, 0)),
                new Vertex(new Vector3(-1, -1, -1), new Vector3(-1, 0, 0)),

                // Right face.
                new Vertex(new Vector3(1, 1, 1), new Vector3(1, 0, 0)),
                new Vertex(new Vector3(1, 1, -1), new Vector3(1, 0, 0)),
                new Vertex(new Vector3(1, -1, -1), new Vector3(1, 0, 0)),
                new Vertex(new Vector3(1, -1, 1), new Vector3(1, 0, 0)),

                // Bottom face.
                new Vertex(new Vector3(1, -1, 1), new Vector3(0, -1, 0)),
                new Vertex(new Vector3(1, -1, -1), new Vector3(0, -1, 0)),
                new Vertex(new Vector3(-1, -1, -1), new Vector3(0, -1, 0)),
                new Vertex(new Vector3(-1, -1, 1), new Vector3(0, -1, 0)),
            };

            vertexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.Vertex, vertices.Count, System.Runtime.InteropServices.Marshal.SizeOf(typeof(Vertex)));
            vertexBuffer.SetData(vertices);
        }

        if (indexBuffer == null)
        {
            List<uint> indices = new List<uint>()
            {
                 // Plane triangles.
                0, 1, 2, 0, 2, 3,

                // Cube triangles
                // Top face.
                4, 5, 6, 4, 6, 7,

                // Front face.
                8, 9, 10, 8, 10, 11,

                // Back face.
                12, 13, 14, 12, 14, 15,

                // Left face.
                16, 17, 18, 16, 18, 19,

                // Right face.
                20, 21, 22, 20, 22, 23,

                // Bottom face.
                24, 25, 26, 24, 26, 27,
            };

            indexBuffer = new GraphicsBuffer(GraphicsBuffer.Target.Raw | GraphicsBuffer.Target.Index, indices.Count, sizeof(uint));
            indexBuffer.SetData(indices);
        }
    }

    void OnDestroy()
    {
        ReleaseResources();
    }

    void OnDisable()
    {
        ReleaseResources();
    }

    private void OnEnable()
    {
        if (rtas != null)
            return;

        rtas = new RayTracingAccelerationStructure();
    }
    private void Update()
    {
        if (fpsText)
        {
            float deltaTime = Time.realtimeSinceStartup - lastRealtimeSinceStartup;
            updateFPSTimer += deltaTime;
            
            if (updateFPSTimer >= 0.2f)
            {
                float fps = 1.0f / Mathf.Max(deltaTime, 0.0001f);
                fpsText.text = "FPS: " + Mathf.Ceil(fps).ToString();
                updateFPSTimer = 0.0f;
            }

            lastRealtimeSinceStartup = Time.realtimeSinceStartup;
        }
    }

    [ImageEffectOpaque]
    void OnRenderImage(RenderTexture src, RenderTexture dest)
    {
        if (!SystemInfo.supportsRayTracing || !rayTracingShader)
        {
            Debug.Log("The Ray Tracing API is not supported by this GPU or by the current graphics API.");
            Graphics.Blit(src, dest);
            return;
        }

        if (mesh == null)
        {
            Debug.Log("Please set a Mesh!");
            Graphics.Blit(src, dest);
            return;
        }

        if (material == null)
        {
            Debug.Log("Please set a Material!");
            Graphics.Blit(src, dest);
            return;
        }

        VertexAttributeDescriptor[] vertexDescs = new VertexAttributeDescriptor[2];
        vertexDescs[0] = new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3, 0);
        vertexDescs[1] = new VertexAttributeDescriptor(VertexAttribute.Normal, VertexAttributeFormat.Float32, 3, 0);

        CreateResources();

        CommandBuffer cmdBuffer = new CommandBuffer();
        cmdBuffer.name = "Indirect Geometry Instancing Test";

        RayTracingInstanceCullingConfig cullingConfig = new RayTracingInstanceCullingConfig();

        cullingConfig.flags = RayTracingInstanceCullingFlags.None;

        RayTracingSubMeshFlagsConfig rayTracingSubMeshFlagsConfig = new RayTracingSubMeshFlagsConfig();
        rayTracingSubMeshFlagsConfig.opaqueMaterials = RayTracingSubMeshFlags.Enabled | RayTracingSubMeshFlags.ClosestHitOnly;
        rayTracingSubMeshFlagsConfig.transparentMaterials = RayTracingSubMeshFlags.Disabled;
        rayTracingSubMeshFlagsConfig.alphaTestedMaterials = RayTracingSubMeshFlags.Disabled;

        cullingConfig.subMeshFlagsConfig = rayTracingSubMeshFlagsConfig;
             
        List<RayTracingInstanceCullingTest> instanceTests = new List<RayTracingInstanceCullingTest>();

        RayTracingInstanceCullingTest instanceTest = new RayTracingInstanceCullingTest();
        instanceTest.allowTransparentMaterials = false;
        instanceTest.allowOpaqueMaterials = true;
        instanceTest.allowAlphaTestedMaterials = true;
        instanceTest.layerMask = 1;
        instanceTest.shadowCastingModeMask = (1 << (int)ShadowCastingMode.Off) | (1 << (int)ShadowCastingMode.On) | (1 << (int)ShadowCastingMode.TwoSided);
        instanceTest.instanceMask = 1 << 0;

        instanceTests.Add(instanceTest);

        cullingConfig.instanceTests = instanceTests.ToArray();

        rtas.ClearInstances();

        bool instancingEnabled = (enableInstancingToggle == null) || enableInstancingToggle.isOn;
        
        // Just test the indirectArgsOffset to make sure it works.
        uint indirectArgsOffset = 9 * (2 * sizeof(uint));

        if (instanceData != null)
        {
            Profiler.BeginSample("Add Ray Tracing Instances to RTAS");

            /*
            RayTracingGeometryInstanceConfig cube = new RayTracingGeometryInstanceConfig();

            cube.vertexBuffer = vertexBuffer;
            cube.vertexAttributes = vertexDescs;
            cube.indexBuffer = indexBuffer;
            cube.indexStart = 6;
            cube.indexCount = 2 * 3 * 6;
            cube.material = material;
            cube.dynamicGeometry = false;
            cube.enableTriangleCulling = false;
            cube.frontTriangleCounterClockwise = true;
            cube.lightProbeUsage = LightProbeUsage.Off;
            cube.materialProperties = new MaterialPropertyBlock();
            cube.materialProperties.SetBuffer("g_Colors", instanceData.colors);
            cube.materialProperties.SetBuffer("g_InstanceIndices", instanceIndices);

            */

            RayTracingMeshInstanceConfig teapot = new RayTracingMeshInstanceConfig();

            teapot.mesh = mesh;
            teapot.material = material;
            teapot.enableTriangleCulling = false;
            teapot.lightProbeUsage = LightProbeUsage.Off;
            teapot.materialProperties = new MaterialPropertyBlock();
            teapot.materialProperties.SetBuffer("g_Colors", instanceData.colors);
            teapot.materialProperties.SetBuffer("g_InstanceIndices", instanceIndices);
            teapot.layer = 2;

            if (instancingEnabled)
            {
                rtas.AddInstancesIndirect(teapot, instanceMatrices, instanceMatrices.count, indirectArgsBuffer, indirectArgsOffset);
            }
            else
            {
                // Add the instances one by one.
                for (int i = 0; i < instanceData.matrices.Length; i++)
                {
                    // Use custom InstanceID() in HLSL to read the instance matrix from g_Colors;
                    // The last argument is the custom InstanceID().
                    
                    rtas.AddInstance(teapot, instanceData.matrices[i], null, (uint)i);
                }
            }



            Profiler.EndSample();
        }

        rtas.CullInstances(ref cullingConfig);

        if (instancingEnabled)
        {
            // Execute culling.
            Vector3 cameraPos = Camera.main.transform.position;
            cmdBuffer.SetBufferCounterValue(instanceCount, 0);
            cmdBuffer.SetComputeVectorParam(cullingShader, "CameraPosAndRadius2", new Vector4(cameraPos.x, cameraPos.y, cameraPos.z, cullingRadius * cullingRadius));
            cmdBuffer.SetComputeBufferParam(cullingShader, 0, "InstanceMatricesBeforeCulling", gpuMatrices);
            cmdBuffer.SetComputeBufferParam(cullingShader, 0, "InstanceMatricesAfterCulling", instanceMatrices);
            cmdBuffer.SetComputeBufferParam(cullingShader, 0, "InstanceIndices", instanceIndices);
            cmdBuffer.SetComputeBufferParam(cullingShader, 0, "InstanceCount", instanceCount);
            cmdBuffer.SetComputeIntParam(cullingShader, "TotalInstanceCount", gpuMatrices.count);

            int threadGroups = (gpuMatrices.count + 64 - 1) / 64;
            cmdBuffer.DispatchCompute(cullingShader, 0, threadGroups, 1, 1);

            cmdBuffer.CopyCounterValue(instanceCount, indirectArgsBuffer, indirectArgsOffset + 4);

            Action<AsyncGPUReadbackRequest> checkOutput = (AsyncGPUReadbackRequest rq) =>
            {
                var count = rq.GetData<uint>();

                if (titleText)
                {
                    titleText.text = "Adding " + count[0] + " instances to RTAS";
                }

            };

            cmdBuffer.RequestAsyncReadback(indirectArgsBuffer, 4, (int)(indirectArgsOffset + 4), checkOutput);
        }
        else
        {
            titleText.text = "Adding " + gpuMatrices.count + " instances to RTAS";
        }

        RayTracingAccelerationStructure.BuildSettings buildSettings = new RayTracingAccelerationStructure.BuildSettings(RayTracingAccelerationStructureBuildFlags.MinimizeMemory, Camera.main.transform.position);
        cmdBuffer.BuildRayTracingAccelerationStructure(rtas, buildSettings);

        cmdBuffer.SetRayTracingShaderPass(rayTracingShader, "Test");

        // Input
        cmdBuffer.SetRayTracingAccelerationStructure(rayTracingShader, Shader.PropertyToID("g_AccelStruct"), rtas);
        cmdBuffer.SetRayTracingMatrixParam(rayTracingShader, Shader.PropertyToID("g_InvViewMatrix"), Camera.main.cameraToWorldMatrix);
        cmdBuffer.SetRayTracingFloatParam(rayTracingShader, Shader.PropertyToID("g_Zoom"), Mathf.Tan(Mathf.Deg2Rad * Camera.main.fieldOfView * 0.5f));
        cmdBuffer.SetGlobalTexture(Shader.PropertyToID("g_EnvTexture"), envTexture);
        cmdBuffer.SetGlobalVector(Shader.PropertyToID("g_CameraPos"), Camera.main.transform.position);
        
        bool enableRayBounce = (!enableRayBounceToggle || enableRayBounceToggle.isOn) && (SystemInfo.graphicsDeviceVendor == "NVIDIA");
        
        cmdBuffer.SetGlobalInteger(Shader.PropertyToID("g_EnableRayBounce"), enableRayBounce ? 1 : 0);

        // Output
        cmdBuffer.SetRayTracingTextureParam(rayTracingShader, Shader.PropertyToID("g_Output"), rayTracingOutput);

        cmdBuffer.DispatchRays(rayTracingShader, "MainRayGenShader", cameraWidth, cameraHeight, 1);

        Graphics.ExecuteCommandBuffer(cmdBuffer);

        cmdBuffer.Release();

        Graphics.Blit(rayTracingOutput, dest);
    }
}
