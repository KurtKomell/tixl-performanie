﻿#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using T3.Core.Animation;
using T3.Core.Logging;

namespace T3.Core.DataTypes;

public sealed class Curve : IEditableInputType
{
    internal const int TimePrecision = 4;

    public IList<VDefinition> GetVDefinitions()
    {
        return _state.Table.Values;
    }

    public IList<VDefinition> Keys => _state.Table.Values;
    public SortedList<double, VDefinition> Table => _state.Table;

    public object Clone()
    {
        return TypedClone();
    }

    public Curve TypedClone()
    {
        return new Curve { _state = _state.Clone() };
    }

    public Animation.CurveUtils.OutsideCurveBehavior PreCurveMapping { get => _state.PreCurveMapping; set => _state.PreCurveMapping = value; }

    public Animation.CurveUtils.OutsideCurveBehavior PostCurveMapping { get => _state.PostCurveMapping; set => _state.PostCurveMapping = value; }

    public bool HasVAt(double u)
    {
        u = Math.Round(u, TimePrecision);
        return _state.Table.ContainsKey(u);
    }



    public bool HasKeyBefore(double u)
    {
        if (_state.Table.Count == 0)
            return false;

        var smalledTime = _state.Table.Keys[0];
        return smalledTime < Math.Round(u, TimePrecision);
    }

    public bool HasKeyAfter(double u)
    {
        if (_state.Table.Count == 0)
            return false;

        var largestTime = _state.Table.Keys[_state.Table.Count - 1];
        return largestTime > Math.Round(u, TimePrecision);
    }

    public bool TryGetPreviousKey(double u, [NotNullWhen(true)] out VDefinition? key)
    {
        var index = FindIndexBefore(u);
        if (index >= 0)
        {
            key = _state.Table.Values[index];
            return true;
        }

        key = null;
        return false;
    }

    public bool TryGetNextKey(double u, [NotNullWhen(true)] out VDefinition? key)
    {
        var index = FindIndexBefore(u) + 1;

        if (index >= 0 && index < _state.Table.Count)
        {
            key = _state.Table.Values[index];
            return true;
        }

        key = null;
        return false;
    }

    public int FindIndexBefore(double u)
    {
        u = Math.Round(u, TimePrecision);
        var keys = _state.Table.Keys;

        var low = 0;
        var high = keys.Count - 1;
        var candidate = -1;

        while (low <= high)
        {
            var mid = (low + high) / 2;
            var midKey = keys[mid];

            if (midKey < u)
            {
                candidate = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return candidate;
    }

    public void AddOrUpdateV(double u, VDefinition key)
    {
        u = Math.Round(u, TimePrecision);
        key.U = u;
        _state.Table[u] = key;
        SplineInterpolator.UpdateTangents(_state.Table.ToList());
    }

    public void RemoveKeyframeAt(double u)
    {
        u = Math.Round(u, TimePrecision);
        var state = _state;
        state.Table.Remove(u);
        SplineInterpolator.UpdateTangents(state.Table.ToList());
    }

    public void UpdateTangents()
    {
        SplineInterpolator.UpdateTangents(_state.Table.ToList());
    }

    /// <summary>
    /// Tries to move a keyframe to a new position
    /// </summary>
    /// <returns>Returns false if the position is already taken by a keyframe</returns>
    public void MoveKey(double u, double newU)
    {
        u = Math.Round(u, TimePrecision);
        newU = Math.Round(newU, TimePrecision);
        var state = _state;
        if (!state.Table.ContainsKey(u))
        {
            Log.Warning("Tried to move a non-existing keyframe from {u} to {newU}");
            return;
        }

        if (state.Table.ContainsKey(newU))
        {
            return;
        }

        var key = state.Table[u];
        state.Table.Remove(u);
        state.Table[newU] = key;
        key.U = newU;
        SplineInterpolator.UpdateTangents(state.Table.ToList());
    }
    
    // Returns null if there is no vDefinition at that position
    public VDefinition? GetV(double u)
    {
        u = Math.Round(u, TimePrecision);
        return _state.Table.TryGetValue(u, out var foundValue)
                   ? foundValue.Clone()
                   : null;
    }

    public double GetSampledValue(double u)
    {
        if (_state.Table.Count < 1 || double.IsNaN(u) || double.IsInfinity(u))
            return 0.0;

        u = Math.Round(u, TimePrecision);
        double offset = 0.0;
        double mappedU = u;
        var first = _state.Table.First();
        var last = _state.Table.Last();

        if (u <= first.Key)
        {
            _state.PreCurveMapper?.Calc(u, _state.Table, out mappedU, out offset);
        }
        else if (u >= last.Key)
        {
            _state.PostCurveMapper?.Calc(u, _state.Table, out mappedU, out offset);
        }

        double resultValue;
        if (mappedU <= first.Key)
        {
            resultValue = offset + first.Value.Value;
        }
        else if (mappedU >= last.Key)
        {
            resultValue = offset + last.Value.Value;
        }
        else
        {
            //interpolate
            var a = _state.Table.Last(e => e.Key <= mappedU);
            var b = _state.Table.First(e => e.Key > mappedU);

            if (a.Value.OutType == VDefinition.Interpolation.Constant)
            {
                resultValue = offset + ConstInterpolator.Interpolate(a, b, mappedU);
            }
            else if (a.Value.OutType == VDefinition.Interpolation.Linear && b.Value.OutType == VDefinition.Interpolation.Linear)
            {
                resultValue = offset + LinearInterpolator.Interpolate(a, b, mappedU);
            }
            else
            {
                resultValue = offset + SplineInterpolator.Interpolate(a, b, mappedU);
            }
        }

        return resultValue;
    }

    internal void Write(JsonTextWriter writer)
    {
        _state.Write(writer);
    }

    internal void Read(JToken inputToken)
    {
        _state.Read(inputToken);
    }

    private CurveState _state = new();

    public static void UpdateCurveBoolValue(Curve curves, double time, bool value)
    {
        var key = curves.GetV(time) ?? new VDefinition
                                           {
                                               U = time,
                                               InType = VDefinition.Interpolation.Constant,
                                               OutType = VDefinition.Interpolation.Constant,
                                               InEditMode = VDefinition.EditMode.Constant,
                                               OutEditMode = VDefinition.EditMode.Constant,
                                           };
        key.Value = value ? 1 : 0;
        curves.AddOrUpdateV(time, key);
    }

    public static void UpdateCurveValues(Curve[] curves, double time, float[] values)
    {
        for (var index = 0; index < curves.Length; index++)
        {
            var key = curves[index].GetV(time) ?? new VDefinition { U = time };
            key.Value = values[index];
            curves[index].AddOrUpdateV(time, key);
        }
    }

    public static void UpdateCurveValues(Curve[] curves, double time, int[] values)
    {
        for (var index = 0; index < curves.Length; index++)
        {
            var key = curves[index].GetV(time) ?? new VDefinition
                                                      {
                                                          U = time,
                                                          InType = VDefinition.Interpolation.Constant,
                                                          OutType = VDefinition.Interpolation.Constant,
                                                          InEditMode = VDefinition.EditMode.Constant,
                                                          OutEditMode = VDefinition.EditMode.Constant,
                                                      };
            key.Value = values[index];
            curves[index].AddOrUpdateV(time, key);
        }
    }
}