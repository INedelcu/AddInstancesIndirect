using UnityEngine;
using Unity.Mathematics;
using Unity.Collections;
using System;

sealed class RayTracingInstanceData : IDisposable
{
    public NativeArray<Matrix4x4> matrices;
    public GraphicsBuffer colors = null;
    public int rows;
    public int columns;
    public Color color1;
    public Color color2;

    public RayTracingInstanceData(int _columns, int _rows)
    {
        rows = _rows;
        columns = _columns;
        
        matrices = new NativeArray<Matrix4x4>(rows * columns, Allocator.Persistent);        

        int index = 0;

        NativeArray<Vector3> data = new NativeArray<Vector3>(rows * columns, Allocator.Temp);

        Matrix4x4 m = Matrix4x4.identity;

        UnityEngine.Random.InitState(12345);

        float angle = 0;

        for (int row = 0; row < rows; row++)
        {
            float z = row + 0.5f - rows * 0.5f;

            for (int column = 0; column < columns; column++)
            {
                float x = column + 0.5f - columns * 0.5f;

                angle += 10.0f;

                Quaternion rotation = Quaternion.Euler(0, angle, 0);

                m.SetTRS(new Vector3(5.5f * x, 2.001f, 5.5f * z), rotation, new Vector3(2, 2, 2));

                matrices[index] = m;
                
                Color c = UnityEngine.Random.ColorHSV(0, 1, 0.2f, 1, 0.8f, 1.2f);

                data[index] = new Vector3(c.r, c.g, c.b);

                index++;
            }
        }

        colors = new GraphicsBuffer(GraphicsBuffer.Target.Structured, rows * columns, 3 * sizeof(float));
        colors.SetData(data);
    }

    public void Dispose()
    {
        if (matrices.IsCreated)
        {
            matrices.Dispose();
        }

        if (colors != null)
        {
            colors.Release();
            colors = null;
        }
    }
}