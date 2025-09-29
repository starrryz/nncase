// Copyright (c) Canaan Inc. All rights reserved.
// Licensed under the Apache license. See LICENSE file in the project root for full license information.

using System;
using System.Linq;
using NetFabric.Hyperlinq;
using Nncase.CostModel;
using Nncase.IR;
using Nncase.IR.NTT;
using OrtKISharp;

namespace Nncase.Evaluator.IR.NTT;

/// <summary>
/// Evaluator for <see cref="VectorizedRoPE"/>.
/// </summary>
public class VectorizedRoPEEvaluator : IEvaluator<VectorizedRoPE>, ITypeInferencer<VectorizedRoPE>, ICostEvaluator<VectorizedRoPE>,
    IMetricEvaluator<VectorizedRoPE>
{
    public static bool AxisEqual(IRArray<SBP> a, IRArray<SBP> b, int startA, int startB)
    {
        var lenA = a.Count - startA;
        if (lenA != b.Count - startB)
        {
            return false;
        }

        for (int i = 0; i < lenA; i++)
        {
            if (!Equals(a[startA + i], b[startB + i]))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc/>
    public IValue Visit(IEvaluateContext context, VectorizedRoPE target)
    {
        var inputTensor = context.GetArgumentValueAsTensor(target, VectorizedRoPE.Input);
        var cosTensor = context.GetArgumentValueAsTensor(target, VectorizedRoPE.Cos);
        var sinTensor = context.GetArgumentValueAsTensor(target, VectorizedRoPE.Sin);

        var originDtype = inputTensor.ElementType;
        if (originDtype.IsFloat() && originDtype is PrimType && originDtype != DataTypes.Float32)
        {
            inputTensor = inputTensor.Cast<float>();
            cosTensor = cosTensor.Cast<float>();
            sinTensor = sinTensor.Cast<float>();
        }

        var input = inputTensor.ToOrtTensor();
        var cos = cosTensor.ToOrtTensor();
        var sin = sinTensor.ToOrtTensor();

        var sliceAxis = 1;
        var sliceDim = inputTensor.Dimensions[sliceAxis] / 2;
        var parts = OrtKI.Split(input, new[] { sliceDim, sliceDim }, sliceAxis);

        // rotate half
        var rotated = OrtKI.Concat([OrtKI.Neg(parts[1]), parts[0]], sliceAxis);
        var output = OrtKI.Add(OrtKI.Mul(input, cos), OrtKI.Mul(rotated, sin));
        return output.ToValue(originDtype);
    }

    /// <inheritdoc/>
    public IRType Visit(ITypeInferenceContext context, VectorizedRoPE target)
    {
        var input = context.CheckArgumentType<IRType>(target, VectorizedRoPE.Input);
        var cos = context.CheckArgumentType<IRType>(target, VectorizedRoPE.Cos);
        var sin = context.CheckArgumentType<IRType>(target, VectorizedRoPE.Sin);

        return (input, cos, sin) switch
        {
            (DistributedType a, DistributedType b, DistributedType c) => Visit(a, b, c),
            (TensorType a, TensorType, TensorType) => Visit(a),
            _ => new InvalidType(input.GetType().ToString()),
        };
    }

    /// <inheritdoc/>
    public Cost Visit(ICostEvaluateContext context, VectorizedRoPE target)
    {
        var inputType = context.GetArgumentType<IRType>(target, VectorizedRoPE.Input);
        var cosType = context.GetArgumentType<IRType>(target, VectorizedRoPE.Cos);
        var sinType = context.GetArgumentType<IRType>(target, VectorizedRoPE.Sin);
        var macPerElement = 4; // 2 for mul, 1 for add, 1 for neg and concat
        var returnType = context.GetReturnType<IRType>();
        return new()
        {
            [CostFactorNames.MemoryLoad] = CostUtility.GetMemoryAccess(inputType) + CostUtility.GetMemoryAccess(cosType) + CostUtility.GetMemoryAccess(sinType),
            [CostFactorNames.CPUCycles] = CostUtility.GetCPUCycles(inputType, macPerElement),
            [CostFactorNames.MemoryStore] = CostUtility.GetMemoryAccess(returnType),
        };
    }

    public Metric Visit(IMetricEvaluateContext context, VectorizedRoPE target)
    {
        var inputType = context.GetArgumentType<TensorType>(target, VectorizedRoPE.Input);
        var cosType = context.GetArgumentType<TensorType>(target, VectorizedRoPE.Cos);
        var sinType = context.GetArgumentType<TensorType>(target, VectorizedRoPE.Sin);
        var returnType = context.GetReturnType<TensorType>();
        var macPerElement = 2; // 1 for mul, 1 for add

        return new()
        {
            [MetricFactorNames.OffChipMemoryTraffic] = CostUtility.GetMemoryAccess(inputType) + CostUtility.GetMemoryAccess(cosType) + CostUtility.GetMemoryAccess(sinType) + CostUtility.GetMemoryAccess(returnType),
            [MetricFactorNames.FLOPs] = MetricUtility.GetFLOPs(returnType, macPerElement),
        };
    }

    private IRType Visit(TensorType input)
    {
        return input;
    }

    private IRType Visit(DistributedType input, DistributedType cos, DistributedType sin)
    {
        // // 这里是核心问题，对不对，在构建的时候，先默认不支持？
        // var invalid = new InvalidType($"{input}, {cos}, {sin} not support");
        // if (input.Placement != cos.Placement || cos.Placement != sin.Placement
        //     || !input.AxisPolicies[1..].SequenceEqual(cos.AxisPolicies)
        //     || !cos.AxisPolicies.SequenceEqual(sin.AxisPolicies)
        //     || input.AxisPolicies[^1] is not SBPBroadCast)
        // {
        //     return invalid;
        // }

        // return input;
        // 仅在“不支持”时再构造错误消息；且别插 IR 对象，避免 ToString
        if (input.Placement != cos.Placement || cos.Placement != sin.Placement
            || !AxisEqual(input.AxisPolicies, cos.AxisPolicies, startA: 1, startB: 0)
            || !AxisEqual(cos.AxisPolicies, sin.AxisPolicies, startA: 0, startB: 0)
            || input.AxisPolicies[^1] is not SBPBroadCast)
        {
            return new InvalidType("RoPE: distributed types mismatch (placement/axis/SBP)");

            // 如需一点信息（仍避免 ToString）：
            // return new InvalidType($"RoPE mismatch: in={input.GetType().Name}, cos={cos.GetType().Name}, sin={sin.GetType().Name}");
        }

        return input;
    }
}
