﻿using System.Runtime.InteropServices;

namespace T3.Core.DataTypes;

[StructLayout(LayoutKind.Explicit, Size = Stride)]
public struct Point
{
    [FieldOffset(0)]
    public Vector3 Position;

    [FieldOffset(3 * 4)]
    public float F1;

    [FieldOffset(4 * 4)]
    public Quaternion Orientation;

    [FieldOffset(8 * 4)]
    public Vector4 Color;
        
    [FieldOffset(12 * 4)]
    public Vector3 Scale;
        
    [FieldOffset(15 * 4)]
    public float F2;


    public Point()
    {
        Position = Vector3.Zero;
        F1 = 1;
        Orientation = Quaternion.Identity;
        Color = Vector4.One;
        Scale = Vector3.One;
        F2 = 1;
    }

    public static Point Separator()
    {
        return new Point
                   {
                       Position = Vector3.Zero,
                       F1 = 1,
                       Orientation = Quaternion.Identity,
                       Color = Vector4.One,
                       Scale = new Vector3(float.NaN, float.NaN, float.NaN),
                       F2 = 1,
                   };
    }

    [Newtonsoft.Json.JsonIgnore]
    public const int Stride = 16 * 4;
}